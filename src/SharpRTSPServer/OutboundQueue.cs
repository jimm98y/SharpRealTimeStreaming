// SharpRTSPServer
// Copyright (C) 2026 Lukas Volf
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using Microsoft.Extensions.Logging;
using System;
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

        /// <summary>
        /// What sort of media this frame carries.
        /// </summary>
        /// <remarks>
        /// Not decoration: it decides what is thrown away when a client cannot keep up. A second of
        /// audio is a few kilobytes and a second of video is hundreds, so treating them as equal
        /// claims on one budget threw away most of the sound to make room for pictures.
        /// </remarks>
        public TrackType Kind { get; set; } = TrackType.Video;

        /// <summary>
        /// Whether a decoder could start on this frame, having seen nothing before it.
        /// </summary>
        public bool IsKeyFrame { get; set; } = true;

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
        /// <remarks>
        /// A plain stack under a lock, not a concurrent bag. A bag keeps a list per thread and is at
        /// its best when the same thread puts things in and takes them out; here one thread always
        /// puts and another always takes, so every take was a steal from another thread's list -
        /// which locks anyway, and allocates on the way.
        /// </remarks>
        private static readonly Stack<QueuedFrame> Spare = new Stack<QueuedFrame>();

        private static readonly object SpareLock = new object();

        private const int MOST_SPARE = 64;

        /// <summary>
        /// A frame to fill in, reused if there is one going spare.
        /// </summary>
        public static QueuedFrame Take()
        {
            lock (SpareLock)
            {
                if (Spare.Count > 0)
                {
                    QueuedFrame spare = Spare.Pop();
                    spare._references = 1;
                    return spare;
                }
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
            IsKeyFrame = packets.IsKeyFrame;
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
            lock (SpareLock)
            {
                if (Spare.Count < MOST_SPARE)
                {
                    Spare.Push(this);
                }
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
        /// <remarks>
        /// A linked list rather than a queue, because when the queue is full the frame to throw away
        /// is not always the one at the front - see <see cref="DropOneToMakeRoom"/>. Frames are still
        /// added at the back and written from the front, so each track's own order is untouched.
        /// </remarks>
        private readonly LinkedList<QueuedFrame> _frames = new LinkedList<QueuedFrame>();
        private readonly object _gate = new object();
        private readonly ILogger _logger;
        private readonly int _maxFrames;
        private readonly long _maxBytes;
        private readonly Action<QueuedFrame> _write;

        /// <summary>
        /// Told when this connection has nothing to show and a keyframe would end the wait.
        /// </summary>
        internal Action NeedsKeyFrame { get; set; }

        /// <summary>
        /// Whether this connection is ready to be written to, or is still being set up.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A client that has said SETUP but not yet PLAY is a few milliseconds from being ready, and
        /// media produced in that window used to be taken off the queue and thrown away - which for
        /// a producer that starts its stream when the first client asks for it meant throwing away
        /// the keyframe, the one frame that client could not do without.
        /// </para>
        /// <para>
        /// So it waits on the queue instead, where the ordinary bound decides how much of it may.
        /// Nothing is held indefinitely: a client that sets up and never plays simply fills its
        /// queue, and the queue is as bounded as anyone else's.
        /// </para>
        /// </remarks>
        internal Func<bool> IsReady { get; set; }

        /// <summary>
        /// The last picture this stream produced that a decoder could start on, if one is being kept.
        /// </summary>
        /// <remarks>
        /// Asked for only when the wait for a live one has run out - see <see cref="HoldsBackPicture"/>.
        /// </remarks>
        internal Func<QueuedFrame> LastKeyFrame { get; set; }

        /// <summary>
        /// Whether this stream has ever produced a picture a decoder could start on.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Holding pictures back is worth doing only where the thing being waited for is known to
        /// happen. Whether a frame can be started on is read out of the bytes, and a producer may
        /// hand over something no track here can read that way - in which case every picture looks
        /// undecodable, every client waits, and the wait ends only when it times out. That turns a
        /// stream that used to start immediately and imperfectly into one that starts imperfectly
        /// and late, which is worse in both directions.
        /// </para>
        /// <para>
        /// So nothing is held back until the stream has shown at least once that it produces
        /// something to hold out for. A stream whose keyframes cannot be recognised behaves exactly
        /// as it did before any of this existed, immediately rather than after a timeout.
        /// </para>
        /// </remarks>
        internal Func<bool> StreamProducesKeyFrames { get; set; }
        private readonly string _describedAs;
        private readonly RtpWriterPool _pool;

        /// <summary>
        /// Whether a pool thread is writing this queue or is about to. While it is set no other
        /// thread takes the queue on, which is what keeps the frames of one connection in order.
        /// </summary>
        private bool _scheduled;

        private bool _stopping;

        /// <summary>
        /// Whether the picture is mid-group as far as this connection is concerned, so that video is
        /// worth nothing to it until a frame a decoder can start on comes along.
        /// </summary>
        /// <remarks>
        /// <para>
        /// True from the start, because a client joining a stream that is already running arrives in
        /// the middle of a group: the pictures until the next keyframe refer to ones it never saw,
        /// and a decoder given them reports what ffmpeg calls co-located POCs unavailable and shows
        /// rubbish until the group ends. Sending them gains nothing and costs the bandwidth that the
        /// keyframe is waiting for.
        /// </para>
        /// <para>
        /// True again whenever a picture is dropped, for the same reason from the other direction:
        /// once one is missing, the rest of the group cannot be decoded either.
        /// </para>
        /// </remarks>
        private bool _midGroup = true;

        /// <summary>
        /// When this connection started waiting for a frame a decoder could start on.
        /// </summary>
        private DateTime _midGroupSince = DateTime.UtcNow;

        /// <summary>
        /// Whether the wait has already been given up on once, so it is reported once and not per frame.
        /// </summary>
        private bool _gaveUpWaiting;

        /// <summary>Whether the encoder has already been asked for a keyframe for this wait.</summary>
        private bool _askedForKeyFrame;

        /// <summary>
        /// Whether this client is showing the keyframe kept for it, waiting for a live one.
        /// </summary>
        private bool _sentKeptKeyFrame;
        private long _dropped;

        /// <summary>
        /// Pictures not sent because the client had no way to decode them.
        /// </summary>
        /// <remarks>
        /// Deliberate, and nothing to do with whether the client is keeping up - see the drop
        /// reporting below, which is about a client that is not.
        /// </remarks>
        private long _withheld;
        private long _reportedDrops;
        private long _queuedBytes;

        /// <summary>
        /// How many frames a connection may send before it goes to the back of the queue. Without a
        /// limit, one connection with a lot waiting would hold a writer for as long as its producer
        /// kept up, and the connections behind it would not be written to at all.
        /// </summary>
        private const int FRAMES_PER_TURN = 8;

        /// <summary>
        /// How long a connection goes without a picture while waiting for one a decoder can start on.
        /// </summary>
        /// <remarks>
        /// Long enough to cover the group of pictures of anything that sends them regularly - two
        /// seconds is several groups for a camera, which typically sends one a second or two - and
        /// short enough that a stream nothing here can read is not blank for long.
        /// </remarks>
        internal static readonly TimeSpan DEFAULT_KEY_FRAME_WAIT = TimeSpan.FromSeconds(2);

        private readonly TimeSpan _keyFrameWait;

        public OutboundQueue(RtpWriterPool pool, int maxFrames, long maxBytes, Action<QueuedFrame> write, string describedAs, ILogger logger)
            : this(pool, maxFrames, maxBytes, write, describedAs, logger, DEFAULT_KEY_FRAME_WAIT)
        {
        }

        public OutboundQueue(RtpWriterPool pool, int maxFrames, long maxBytes, Action<QueuedFrame> write, string describedAs, ILogger logger, TimeSpan keyFrameWait)
        {
            _keyFrameWait = keyFrameWait;
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
            bool askForKeyFrame = false;

            lock (_gate)
            {
                ask = Put(frame, ref askForKeyFrame);
            }

            AfterPutting(ask, askForKeyFrame);
        }

        /// <summary>
        /// Says that this connection is ready after all, and asks for whatever has been waiting.
        /// </summary>
        internal void Resume()
        {
            bool ask;

            lock (_gate)
            {
                if (_stopping || _frames.Count == 0 || _scheduled)
                {
                    return;
                }

                ask = true;
                _scheduled = true;
            }

            if (ask)
            {
                _pool.Schedule(this);
            }
        }

        /// <summary>
        /// Puts one frame in. Called with <see cref="_gate"/> held; says whether the pool needs asking.
        /// </summary>
        private bool Put(QueuedFrame frame, ref bool askForKeyFrame)
        {
            if (_stopping)
            {
                frame.Release();
                return false;
            }

            if (frame.Kind == TrackType.Video && !HoldsBackPicture(frame, ref askForKeyFrame))
            {
                // Not dropped: held back, because nothing downstream could decode it. Counted
                // apart from the drops, which are the ones a client lost because it could not keep
                // up - reporting these as those said a client was falling behind when it was
                // sitting on a still picture waiting for a keyframe, which is the opposite problem.
                frame.Release();
                _withheld++;

                // Not necessarily nothing to do, though - holding this one back may have put the
                // kept keyframe in the queue in its place.
                return AskForAWriter();
            }

            // Bounded by both, because the two say different things: a frame count is a bound on
            // how far behind a client may fall, and a byte count is a bound on what that costs.
            // Sixty four frames of audio and sixty four of high bitrate video are not remotely
            // the same amount of memory.
            while (_frames.Count > 0
                && (_frames.Count >= _maxFrames || _queuedBytes + frame.Bytes > _maxBytes))
            {
                DropOneToMakeRoom();
            }

            _frames.AddLast(frame);
            _queuedBytes += frame.Bytes;

            return AskForAWriter();
        }

        /// <summary>
        /// Whether the pool needs asking for a thread, given what is in the queue.
        /// </summary>
        /// <remarks>
        /// Only when nobody has it. A thread already writing this connection will see whatever is
        /// there when it comes round again, and asking twice would put the connection in the pool's
        /// queue twice and let two threads write it at once.
        /// <para>
        /// Called with <see cref="_gate"/> held.
        /// </para>
        /// </remarks>
        private bool AskForAWriter()
        {
            if (_frames.Count == 0 || _scheduled)
            {
                return false;
            }

            _scheduled = true;
            return true;
        }

        /// <summary>
        /// The part that must happen with <see cref="_gate"/> let go of.
        /// </summary>
        /// <remarks>
        /// The pool's lock is never taken while holding this one, and the keyframe request goes to a
        /// handler belonging to whoever produces the media - which may do anything at all, including
        /// calling back into the server, so it must not be holding this.
        /// </remarks>
        private void AfterPutting(bool ask, bool askForKeyFrame)
        {
            if (ask)
            {
                _pool.Schedule(this);
            }

            if (askForKeyFrame)
            {
                AskForAKeyFrame();
            }

            ReportDrops();
        }

        /// <summary>
        /// Whether this picture is worth sending, given what this connection has already had.
        /// </summary>
        /// <remarks>
        /// <para>
        /// False while the connection is mid-group: the pictures between a missing one and the next
        /// keyframe refer to frames the decoder has not got, so it makes rubbish of them - what
        /// ffmpeg reports as co-located POCs unavailable - and the bandwidth is better spent on the
        /// keyframe that ends the wait.
        /// </para>
        /// <para>
        /// But not for ever. Whether a frame can be started on is read out of the bytes, and a
        /// producer may well hand over something these tracks cannot read that way - NALs with start
        /// codes still on them, a codec with no reader here, an encoder that never sends a parameter
        /// set. Withholding the picture indefinitely because nothing looked like a keyframe would
        /// turn a stream that used to play with artefacts into one that shows nothing at all, which
        /// is much the worse failure. So the wait gives up after <see cref="_keyFrameWait"/> and the
        /// picture goes out as it used to.
        /// </para>
        /// <para>
        /// Called with <see cref="_gate"/> held.
        /// </para>
        /// </remarks>
        private bool HoldsBackPicture(QueuedFrame frame, ref bool ask)
        {
            if (frame.IsKeyFrame)
            {
                _midGroup = false;
                _gaveUpWaiting = false;
                _askedForKeyFrame = false;
                _sentKeptKeyFrame = false;
                return true;
            }

            if (!_midGroup)
            {
                return true;
            }

            // Never seen one on this stream, so there is nothing to wait for and waiting would only
            // delay a picture that was going to be imperfect either way.
            if (StreamProducesKeyFrames != null && !StreamProducesKeyFrames())
            {
                return true;
            }

            // One was kept, so the client is given a real picture now rather than nothing for as
            // long as the next live one takes. Straight away, not after the wait: the whole cost of
            // waiting is the picture the client does not have.
            if (!_sentKeptKeyFrame && SendKeptKeyFrame())
            {
                _sentKeptKeyFrame = true;
            }

            if (_sentKeptKeyFrame)
            {
                // And the pictures after it are held back until the next live keyframe, because they
                // describe changes to ones this client never saw: sending them on top of the kept
                // frame takes a correct still picture and melts it. Holding the still and then
                // cutting to the next group is the difference between a picture that waits and a
                // picture that falls apart.
                //
                // No timeout on this one. The others exist because the thing being waited for might
                // never come; here the stream has already produced a keyframe - that is where the
                // one being shown came from - so another is coming. And the client is not left with
                // nothing meanwhile: it has a picture to hold, and the sound never stops.
                if (!_askedForKeyFrame)
                {
                    _askedForKeyFrame = true;
                    ask = true;
                }

                return false;
            }

            if (DateTime.UtcNow - _midGroupSince < _keyFrameWait)
            {
                if (!_askedForKeyFrame)
                {
                    _askedForKeyFrame = true;
                    ask = true;
                }

                return false;
            }

            if (!_gaveUpWaiting)
            {
                _gaveUpWaiting = true;

                // The payload bytes go in it because this is the one message that says a track could
                // not tell a keyframe from anything else, and what it was reading is the only thing
                // worth knowing at that point. For H264 the first byte of the payload is the NAL
                // header, so 0x65 is the keyframe this was waiting for, 0x41 a predicted picture,
                // 0x7C a fragment of one - and 0x00 means the samples arrived with start codes still
                // on them, which no track here reads.
                _logger.LogWarning(
                    "No decodable starting point for {connection} in {seconds:F0}s, sending the picture anyway. "
                    + "Its payload starts {payload} - if that does not look like the start of a frame, "
                    + "the samples are not in the shape this track reads",
                    _describedAs, _keyFrameWait.TotalSeconds, FirstPayloadBytes(frame));
            }

            _midGroup = false;
            return true;
        }

        /// <summary>
        /// Puts the last keyframe this stream kept in front of the picture in hand, if there is one.
        /// </summary>
        /// <remarks>
        /// It is not the frame the live stream is at, so what follows refers to pictures between the
        /// two that this client never saw, and it decodes them imperfectly until a live keyframe
        /// comes round. That is the trade the stream opted into by keeping one: a real picture now,
        /// imperfect for a while, against nothing at all for as long as the next keyframe takes.
        /// <para>
        /// Called with <see cref="_gate"/> held.
        /// </para>
        /// </remarks>
        private bool SendKeptKeyFrame()
        {
            QueuedFrame kept = LastKeyFrame?.Invoke();

            if (kept == null)
            {
                return false;
            }

            kept.AddRef();
            _frames.AddLast(kept);
            _queuedBytes += kept.Bytes;

            _logger.LogDebug("Starting {connection} on the last keyframe kept for its stream", _describedAs);

            // Still mid-group on purpose. This gives the client a picture to show; it does not put
            // the client where the live stream is, and the pictures between the two are what would
            // break the one it has just been given.
            return true;
        }

        /// <summary>
        /// The first few bytes of a frame's payload, past the RTP header, as hex.
        /// </summary>
        /// <remarks>
        /// For the one message where what the bytes look like is the whole question. Twelve bytes in
        /// is the payload for every packet these tracks build, none of which use CSRCs or extensions.
        /// </remarks>
        private static string FirstPayloadBytes(QueuedFrame frame)
        {
            const int RtpHeader = 12;
            const int Wanted = 4;

            if (frame.Packets == null || frame.Packets.Count == 0)
            {
                return "(nothing)";
            }

            ReadOnlySpan<byte> packet = frame.Packets[0].Span;

            if (packet.Length <= RtpHeader)
            {
                return "(no payload)";
            }

            int count = Math.Min(Wanted, packet.Length - RtpHeader);
            var hex = new System.Text.StringBuilder(count * 3);

            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    hex.Append(' ');
                }

                hex.Append(packet[RtpHeader + i].ToString("X2"));
            }

            return hex.ToString();
        }

        /// <summary>
        /// Asks, once per wait, for the encoder to produce a frame a decoder can start on.
        /// </summary>
        /// <remarks>
        /// Waiting for the next keyframe in the ordinary course of things means waiting up to a
        /// whole group of pictures - two seconds at the far end of a common setting, and the client
        /// has nothing to show for all of it while the sound plays on without it. An encoder that
        /// can be asked for one on demand turns that wait into nothing, which is the difference
        /// between a stream that starts and one that starts two seconds late and out of step.
        /// </remarks>
        private void AskForAKeyFrame()
        {
            Action ask = NeedsKeyFrame;
            if (ask == null)
            {
                return;
            }

            // Called with the gate let go - see AfterPutting - because the handler belongs to
            // whoever is producing the media and may do anything at all, including calling back
            // into the server. It is not trusted to return promptly or to behave.
            try
            {
                ask();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A handler for a keyframe request threw, for {connection}", _describedAs);
            }
        }

        /// <summary>
        /// Throws away one frame, choosing the one the viewer will miss least.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The oldest picture first, and only then anything else. Every track of a connection shares
        /// this queue, so when it overflows the sound and the picture are competing for the same
        /// room - and they are wildly unequal in what they cost and in what losing them does. A
        /// second of audio is a few kilobytes; a second of video is hundreds. Taking the front of the
        /// queue regardless threw away four fifths of the sound to make room for pictures, which is
        /// what a listener hears as stuttering, while the pictures it made room for were themselves
        /// mostly dropped a moment later.
        /// </para>
        /// <para>
        /// Metadata is left alone for the same reason as the audio: it is small, and a gap in it is
        /// a gap in the record of what happened rather than a moment of stale picture.
        /// </para>
        /// <para>
        /// Called with <see cref="_gate"/> held.
        /// </para>
        /// </remarks>
        private void DropOneToMakeRoom()
        {
            LinkedListNode<QueuedFrame> victim = null;

            for (LinkedListNode<QueuedFrame> node = _frames.First; node != null; node = node.Next)
            {
                if (node.Value.Kind == TrackType.Video)
                {
                    victim = node;
                    break;
                }
            }

            // Nothing but sound and data in here, so the oldest of that goes after all - the queue
            // has to come down somehow.
            victim = victim ?? _frames.First;

            _frames.Remove(victim);

            // Once a picture is missing, the rest of its group decodes into rubbish, so there is no
            // sense sending any of it. The next keyframe starts the picture again.
            if (victim.Value.Kind == TrackType.Video && !_midGroup)
            {
                _midGroup = true;
                _midGroupSince = DateTime.UtcNow;
                _askedForKeyFrame = false;
                _sentKeptKeyFrame = false;
            }

            // the size first: the frame reports nothing once the last share of it is gone
            _queuedBytes -= victim.Value.Bytes;
            victim.Value.Release();
            _dropped++;
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
            // Asked before the call is made, not inside it. Saying this costs a boxed number and an
            // array to put it in, and it is said by the thread producing the media at exactly the
            // moment a client has stopped keeping up - which is when that thread has least to spare.
            if ((dropped == 1 || dropped / 100 > reportedBefore / 100)
                && _logger.IsEnabled(LogLevel.Warning))
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

                    // Left where it is rather than taken and dropped. Whoever makes this connection
                    // ready asks for the queue again.
                    if (IsReady != null && !IsReady())
                    {
                        _scheduled = false;
                        return;
                    }

                    frame = _frames.First.Value;
                    _frames.RemoveFirst();
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
                    QueuedFrame queued = _frames.First.Value;
                    _frames.RemoveFirst();
                    queued.Release();
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
