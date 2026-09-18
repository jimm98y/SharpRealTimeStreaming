using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace SharpRTSPServer
{
    /// <summary>
    /// One frame's worth of RTP, waiting to go out on however many connections want it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It owns copies of the packets. The buffers the producer hands to
    /// <see cref="IRtpSender.FeedInRawRTP"/> are released as soon as that call returns, so anything
    /// written later has to have its own.
    /// </para>
    /// <para>
    /// One copy, shared by every connection the frame is queued on, counted so that the buffers go
    /// back to the pool when the last of them has finished with it. A copy each meant a frame was
    /// copied as many times as there were clients watching, which is the one cost that grows with
    /// the audience.
    /// </para>
    /// </remarks>
    internal sealed class QueuedFrame
    {
        /// <summary>The producer's own, given up once the frame has been offered to every connection.</summary>
        private int _references = 1;

        public int StreamType { get; set; }

        public uint RtpTimestamp { get; set; }

        public bool PreserveSourceHeaders { get; set; }

        /// <summary>
        /// The SSRC the forwarding track had when this frame was produced, applied at write time so
        /// the producer does not reach into the stream while the writer is using it.
        /// </summary>
        public uint SourceSsrc { get; set; }

        /// <summary>The packets of this frame, and the memory holding them.</summary>
        public RtpPackets Packets { get; private set; }

        /// <summary>How much of the queue's budget this frame takes up.</summary>
        public int Bytes { get; private set; }

        /// <summary>
        /// Frames that have been finished with, waiting to be used again.
        /// </summary>
        /// <remarks>
        /// One frame object and the two lists inside it, per frame of every stream, is a steady drip
        /// of garbage for something that lives a few milliseconds. The buffers were already pooled;
        /// this is what was holding them.
        /// <para>
        /// Bounded, because a burst must not leave a pool the size of the burst behind it for ever.
        /// Past the bound a frame is simply made and dropped, as they all were before.
        /// </para>
        /// </remarks>
        private static readonly ConcurrentBag<QueuedFrame> Spare = new ConcurrentBag<QueuedFrame>();

        private static int _spareCount;

        private const int MOST_SPARE = 64;

        /// <summary>
        /// A frame to fill in, reused if there is one going spare.
        /// </summary>
        public static QueuedFrame Take()
        {
            if (Spare.TryTake(out QueuedFrame frame))
            {
                Interlocked.Decrement(ref _spareCount);
                frame._references = 1;
                return frame;
            }

            return new QueuedFrame();
        }

        /// <summary>
        /// Takes a frame over: the packets as they were built, and whatever holds them.
        /// </summary>
        /// <remarks>
        /// Nothing is copied. Every packet used to be copied into a buffer of this frame's own,
        /// because the track released the originals the moment it had handed them over - a copy and
        /// a pooled buffer per packet, for a frame about to go out unchanged. The track hands over
        /// what holds them instead, and this releases it when the last client is done.
        /// </remarks>
        public void Fill(RtpPackets packets)
        {
            Packets = packets;
            Bytes = 0;

            for (int i = 0; i < packets.Count; i++)
            {
                Bytes += packets[i].Length;
            }
        }

        /// <summary>
        /// Claims a share of this frame. Balanced by a <see cref="Release"/> once it has been written
        /// or dropped.
        /// </summary>
        public void AddRef()
        {
            Interlocked.Increment(ref _references);
        }

        /// <summary>
        /// Gives up a share of this frame, handing the buffers back once nobody holds one.
        /// </summary>
        public void Release()
        {
            if (Interlocked.Decrement(ref _references) > 0)
            {
                return;
            }

            Packets?.Release();
            Packets = null;
            Bytes = 0;

            // Back for the next frame, unless there are already enough waiting. The lists keep
            // whatever capacity they grew to, which is the point: the next frame of the same stream
            // is the same shape as this one.
            if (Interlocked.Increment(ref _spareCount) <= MOST_SPARE)
            {
                Spare.Add(this);
            }
            else
            {
                Interlocked.Decrement(ref _spareCount);
            }
        }
    }

    /// <summary>
    /// The media waiting to go out on one connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this, the media for several clients went out one after another on whichever thread
    /// produced it, so a client that stopped reading held up the media behind it for everyone on
    /// that stream. The producer now hands a frame over and carries on; each connection drains its
    /// own queue at whatever rate its client manages.
    /// </para>
    /// <para>
    /// The writing is done by threads from a <see cref="RtpWriterPool"/> shared by every connection,
    /// not by one of its own. A queue asks to be run when it has something, is picked up by whatever
    /// thread is free, and gives that thread back when it runs out or has had a fair turn. Only one
    /// thread holds it at a time, so its frames still go out in order.
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
        private readonly RtpWriterPool _pool;

        /// <summary>
        /// Whether a pool thread is writing this queue or is about to. While it is set no other
        /// thread takes the queue on, which is what keeps the frames of one connection in order.
        /// </summary>
        private bool _scheduled;

        private bool _stopping;
        private long _dropped;
        private long _reportedDrops;
        private long _queuedBytes;

        /// <summary>
        /// How many frames a connection may send before it goes to the back of the queue. Without a
        /// limit, one connection with a lot waiting would hold a writer for as long as its producer
        /// kept up, and the connections behind it would not be written to at all.
        /// </summary>
        private const int FRAMES_PER_TURN = 8;

        public OutboundQueue(RtpWriterPool pool, int maxFrames, long maxBytes, Action<QueuedFrame> write, string describedAs, ILogger logger)
        {
            _pool = pool;
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
            bool ask;

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

                    // the size first: the frame reports nothing once the last share of it is gone
                    _queuedBytes -= oldest.Bytes;
                    oldest.Release();
                    _dropped++;
                }

                _frames.Enqueue(frame);
                _queuedBytes += frame.Bytes;

                // Only when nobody has it. A thread that is already writing this connection will see
                // the frame when it comes round again, and asking twice would put the connection in
                // the queue of the pool twice and let two threads write it at once.
                ask = !_scheduled;
                _scheduled = true;
            }

            // Outside the lock, so that the lock of the pool is never taken while holding this one.
            if (ask)
            {
                _pool.Schedule(this);
            }

            ReportDrops();
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

        /// <summary>
        /// Writes up to one turn of what is waiting, then either gives the queue up or asks for
        /// another turn behind everyone else.
        /// </summary>
        /// <remarks>
        /// Called by one pool thread at a time. Giving the queue up and finding it empty happen
        /// under the same lock, so a frame arriving at that moment either finds the queue still
        /// taken - and is written by this turn or the next - or finds it free and asks for a thread
        /// itself. Neither leaves a frame in a queue that nobody will come back to.
        /// </remarks>
        internal void WriteSome()
        {
            for (int written = 0; written < FRAMES_PER_TURN; written++)
            {
                QueuedFrame frame;

                lock (_gate)
                {
                    if (_stopping || _frames.Count == 0)
                    {
                        _scheduled = false;
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

            // Still holding its turn, so nothing else has taken the queue on - back to the end of
            // the line rather than carrying on and starving whoever is behind it.
            _pool.Schedule(this);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_stopping)
                {
                    return;
                }

                _stopping = true;

                // whatever is still waiting will never be sent, so its buffers go back now
                while (_frames.Count > 0)
                {
                    _frames.Dequeue().Release();
                }

                _queuedBytes = 0;
            }

            // A pool thread may be inside a write to a client that has stopped reading, and that
            // only ends when the socket is closed - which the caller is on its way to doing. It is
            // not waited on here; it will find the queue stopped and give it up. The flag is left
            // for that thread to clear, so nothing else takes the queue on in the meantime.
        }
    }
}
