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

        /// <summary>Send a sender report before this frame. Built at write time, from counts that are only right then.</summary>
        public bool SendSenderReportFirst { get; set; }

        /// <summary>Rented buffers, each holding one RTP packet in its first <see cref="Lengths"/> bytes.</summary>
        public List<byte[]> Packets { get; } = new List<byte[]>();

        public List<int> Lengths { get; } = new List<int>();

        public void Take(IReadOnlyList<Memory<byte>> packets)
        {
            foreach (Memory<byte> packet in packets)
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(packet.Length);
                packet.Span.CopyTo(buffer);
                Packets.Add(buffer);
                Lengths.Add(packet.Length);
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
        private readonly Action<QueuedFrame> _write;
        private readonly string _describedAs;

        private Thread _writer;
        private bool _stopping;
        private long _dropped;
        private long _reportedDrops;

        public OutboundQueue(int maxFrames, Action<QueuedFrame> write, string describedAs, ILogger logger)
        {
            _maxFrames = maxFrames < 1 ? 1 : maxFrames;
            _write = write;
            _describedAs = describedAs;
            _logger = logger;
        }

        /// <summary>How many frames have been dropped because this connection could not keep up.</summary>
        public long Dropped
        {
            get { lock (_gate) { return _dropped; } }
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

                while (_frames.Count >= _maxFrames)
                {
                    QueuedFrame oldest = _frames.Dequeue();
                    oldest.Release();
                    _dropped++;
                }

                _frames.Enqueue(frame);
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
            lock (_gate)
            {
                if (_dropped == _reportedDrops)
                {
                    return;
                }

                dropped = _dropped;
                _reportedDrops = _dropped;
            }

            // said at intervals rather than per frame, since a client that has stopped reading
            // drops one for every frame produced from then on
            if (dropped == 1 || dropped % 100 == 0)
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

                Monitor.PulseAll(_gate);
            }

            // The writer may be inside a write to a client that has stopped reading, and that only
            // ends when the socket is closed - which the caller is about to do. So it is not waited
            // on here; it is a background thread and it will notice.
            _ = writer;
        }
    }
}
