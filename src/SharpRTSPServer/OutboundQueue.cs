using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace SharpRTSPServer
{
    /// <summary>
    /// One frame's worth of RTP waiting to go out on a connection.
    /// </summary>
    /// <remarks>
    /// It owns copies of the packets. The buffers the producer hands to
    /// <see cref="IRtpSender.FeedInRawRTP"/> are released as soon as that call returns, so anything
    /// written later has to have its own.
    /// </remarks>
    internal sealed class QueuedFrame
    {
        public int StreamType { get; set; }

        public uint RtpTimestamp { get; set; }

        public bool PreserveSourceHeaders { get; set; }

        /// <summary>
        /// The SSRC the forwarding track had when this frame was produced, applied at write time so
        /// the producer does not reach into the stream while the writer is using it.
        /// </summary>
        public uint SourceSsrc { get; set; }

        /// <summary>Rented buffers, each holding one RTP packet in its first <see cref="Lengths"/> bytes.</summary>
        public List<byte[]> Packets { get; } = new List<byte[]>();

        public List<int> Lengths { get; } = new List<int>();

        /// <summary>How much of the queue's budget this frame takes up.</summary>
        public int Bytes { get; private set; }

        public void Take(IReadOnlyList<Memory<byte>> packets)
        {
            foreach (Memory<byte> packet in packets)
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(packet.Length);
                packet.Span.CopyTo(buffer);
                Packets.Add(buffer);
                Lengths.Add(packet.Length);
                Bytes += packet.Length;
            }
        }

        public void Release()
        {
            foreach (byte[] buffer in Packets)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            Packets.Clear();
            Lengths.Clear();
            Bytes = 0;
        }
    }

    /// <summary>
    /// The media waiting to go out on one connection, and the thread that writes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this, the media for several clients went out one after another on whichever thread
    /// produced it, so a client that stopped reading held up the media behind it for everyone on
    /// that stream. The producer now hands a frame over and carries on; each connection drains its
    /// own queue at whatever rate its client manages.
    /// </para>
    /// <para>
    /// The queue is bounded, because a client that never reads would otherwise grow it until the
    /// server ran out of memory. When it is full the oldest frames are dropped: this is live media,
    /// and a client that has fallen behind is better served by what is happening now than by
    /// working through what it missed. Frames are dropped whole, so a client never receives half of
    /// one.
    /// </para>
    /// </remarks>
    internal sealed class OutboundQueue : IDisposable
    {
        private readonly Queue<QueuedFrame> _frames = new Queue<QueuedFrame>();
        private readonly object _gate = new object();
        private readonly ILogger _logger;
        private readonly int _maxFrames;
        private readonly long _maxBytes;
        private readonly Action<QueuedFrame> _write;
        private readonly string _describedAs;

        private Thread _writer;
        private bool _stopping;
        private long _dropped;
        private long _reportedDrops;
        private long _queuedBytes;

        public OutboundQueue(int maxFrames, long maxBytes, Action<QueuedFrame> write, string describedAs, ILogger logger)
        {
            _maxFrames = maxFrames < 1 ? 1 : maxFrames;
            _maxBytes = maxBytes < 1 ? 1 : maxBytes;
            _write = write;
            _describedAs = describedAs;
            _logger = logger;
        }

        /// <summary>
        /// Puts a frame in the queue, dropping the oldest ones if there is no room.
        /// </summary>
        public void Enqueue(QueuedFrame frame)
        {
            lock (_gate)
            {
                if (_stopping)
                {
                    frame.Release();
                    return;
                }

                // Bounded by both, because the two say different things: a frame count is a bound on
                // how far behind a client may fall, and a byte count is a bound on what that costs.
                // Sixty four frames of audio and sixty four of high bitrate video are not remotely
                // the same amount of memory.
                while (_frames.Count > 0 && (_frames.Count >= _maxFrames || _queuedBytes + frame.Bytes > _maxBytes))
                {
                    QueuedFrame oldest = _frames.Dequeue();
                    _queuedBytes -= oldest.Bytes;
                    oldest.Release();
                    _dropped++;
                }

                _frames.Enqueue(frame);
                _queuedBytes += frame.Bytes;
                Monitor.Pulse(_gate);

                EnsureWriterStarted();
            }

            ReportDrops();
        }

        private void EnsureWriterStarted()
        {
            if (_writer != null)
            {
                return;
            }

            // A thread of its own rather than a pooled one: it spends its life blocked in a write,
            // which is exactly what the pool should not be used for.
            _writer = new Thread(Drain)
            {
                IsBackground = true,
                Name = "RTSP send " + _describedAs
            };
            _writer.Start();
        }

        private void ReportDrops()
        {
            long dropped;
            long reportedBefore;

            lock (_gate)
            {
                if (_dropped == _reportedDrops)
                {
                    return;
                }

                dropped = _dropped;
                reportedBefore = _reportedDrops;
                _reportedDrops = _dropped;
            }

            // Said at intervals rather than per frame, since a client that has stopped reading drops
            // one for every frame produced from then on. Crossing a hundred counts, rather than
            // landing exactly on one - a batch that steps from 99 to 101 is still worth a line.
            if (dropped == 1 || dropped / 100 > reportedBefore / 100)
            {
                _logger.LogWarning("Dropped {dropped} frames for {connection}, it is not keeping up", dropped, _describedAs);
            }
        }

        private void Drain()
        {
            while (true)
            {
                QueuedFrame frame;

                lock (_gate)
                {
                    while (_frames.Count == 0 && !_stopping)
                    {
                        Monitor.Wait(_gate);
                    }

                    if (_stopping && _frames.Count == 0)
                    {
                        return;
                    }

                    frame = _frames.Dequeue();
                    _queuedBytes -= frame.Bytes;
                }

                try
                {
                    _write(frame);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error writing queued media to {connection}", _describedAs);
                }
                finally
                {
                    frame.Release();
                }
            }
        }

        public void Dispose()
        {
            Thread writer;

            lock (_gate)
            {
                if (_stopping)
                {
                    return;
                }

                _stopping = true;
                writer = _writer;

                // whatever is still waiting will never be sent, so its buffers go back now
                while (_frames.Count > 0)
                {
                    _frames.Dequeue().Release();
                }

                _queuedBytes = 0;

                Monitor.PulseAll(_gate);
            }

            // The writer may be inside a write to a client that has stopped reading, and that only
            // ends when the socket is closed - which the caller is about to do. So it is not waited
            // on here; it is a background thread and it will notice.
            _ = writer;
        }
    }
}
