using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace SharpRTSPServer
{
    /// <summary>
    /// The pictures a client needs to start watching, kept from the last one it could start on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A client arriving at a stream has nothing it can show until a keyframe comes round, because
    /// every picture in between is described in terms of ones it never saw. Waiting for the next
    /// keyframe means a second or two of nothing, with the sound playing on without it - so the two
    /// start that far out of step, and the decoder complains about the pictures it cannot make sense
    /// of in the meantime.
    /// </para>
    /// <para>
    /// Keeping the last keyframe alone would not help: after sending it the live stream resumes at
    /// the current picture, which refers to everything between the two - which the client still
    /// never saw. So this keeps the keyframe and everything since, and a client that arrives is
    /// given the lot before the live stream reaches it. It then has a complete group and can decode
    /// from the first picture it is handed.
    /// </para>
    /// <para>
    /// The frames are the same ones being sent to everybody else, held by reference rather than
    /// copied, so a stream costs one group of pictures however many clients are watching it.
    /// </para>
    /// </remarks>
    internal sealed class GroupOfPicturesCache
    {
        private readonly object _gate = new object();
        private readonly List<QueuedFrame> _frames = new List<QueuedFrame>();
        private readonly ILogger _logger;

        /// <summary>
        /// Whether the group in hand is worth keeping, or has outgrown what is worth holding.
        /// </summary>
        private bool _keeping;

        private long _bytes;

        public GroupOfPicturesCache(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// How many pictures of a group are kept before the whole group is given up on.
        /// </summary>
        /// <remarks>
        /// A stream whose keyframes are minutes apart, or one whose keyframes this server cannot
        /// recognise, would otherwise be held in memory without limit. Past the bound the group is
        /// dropped and nothing is replayed until the next keyframe, which is what would have
        /// happened anyway without any of this.
        /// </remarks>
        public int MaxFrames { get; set; } = 300;

        public long MaxBytes { get; set; } = 16L * 1024 * 1024;

        /// <summary>
        /// Takes note of a frame going out to the stream.
        /// </summary>
        /// <remarks>
        /// A keyframe starts the group again; anything else joins the one in progress. Audio and
        /// metadata are not kept: every packet of them stands on its own, so a client that missed
        /// the last few has lost a moment of sound rather than the ability to decode at all, and
        /// replaying them would only play that moment twice.
        /// </remarks>
        public void Note(QueuedFrame frame)
        {
            if (frame.Kind != TrackType.Video)
            {
                return;
            }

            lock (_gate)
            {
                if (frame.IsKeyFrame)
                {
                    ReleaseAll();
                    _keeping = true;
                }
                else if (!_keeping)
                {
                    // Mid-group, and the start of it was never seen - so there is no group here to
                    // give anybody.
                    return;
                }

                if (_frames.Count >= MaxFrames || _bytes + frame.Bytes > MaxBytes)
                {
                    _logger.LogDebug(
                        "Giving up keeping the group of pictures after {frames} frames, it is larger than is worth holding",
                        _frames.Count);

                    ReleaseAll();
                    _keeping = false;
                    return;
                }

                frame.AddRef();
                _frames.Add(frame);
                _bytes += frame.Bytes;
            }
        }

        /// <summary>
        /// Hands the group in hand to a client that has just started watching.
        /// </summary>
        /// <remarks>
        /// Called with the connection list held, so that the live stream cannot reach this client
        /// between the group going in and the client being on the list - which would put the live
        /// picture in front of the group it depends on.
        /// </remarks>
        public void ReplayInto(OutboundQueue queue)
        {
            lock (_gate)
            {
                if (!_keeping)
                {
                    return;
                }

                for (int i = 0; i < _frames.Count; i++)
                {
                    _frames[i].AddRef();
                }

                queue.EnqueueAll(_frames);
            }
        }

        /// <summary>
        /// Lets go of everything held.
        /// </summary>
        public void Clear()
        {
            lock (_gate)
            {
                ReleaseAll();
                _keeping = false;
            }
        }

        private void ReleaseAll()
        {
            for (int i = 0; i < _frames.Count; i++)
            {
                _frames[i].Release();
            }

            _frames.Clear();
            _bytes = 0;
        }
    }
}
