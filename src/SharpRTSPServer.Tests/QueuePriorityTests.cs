using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using SharpRTSPServer;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// What gets thrown away when a client cannot keep up.
    /// </summary>
    /// <remarks>
    /// One queue carries every track of a connection, so when it overflows the sound and the picture
    /// are competing for the same room. What goes decides what the viewer notices: a dropped video
    /// frame is a moment of stale picture, a dropped audio packet is an audible gap, and a dropped
    /// reference frame is every frame after it decoding into rubbish until the next keyframe.
    /// </remarks>
    [TestClass]
    public sealed class QueuePriorityTests
    {
        private const int VIDEO_TRACK = 0;
        private const int AUDIO_TRACK = 1;

        /// <summary>A frame of a given track, taking up a given amount of the queue's budget.</summary>
        private static QueuedFrame Frame(int trackId, TrackType kind, int bytes)
        {
            RtpPackets packets = RtpPackets.Take();
            packets.Rent(bytes);

            QueuedFrame frame = QueuedFrame.Take();
            frame.StreamType = trackId;
            frame.Kind = kind;
            frame.Fill(packets);
            return frame;
        }

        [TestMethod]
        public void AudioIsNotThrownAwayToMakeRoomForPictures()
        {
            using var pool = new RtpWriterPool(NullLogger.Instance, RtpWriterPool.DEFAULT_MAX_THREADS);
            using var stuck = new ManualResetEventSlim(false);
            using var firstTaken = new ManualResetEventSlim(false);

            var written = new List<int>();

            // A client that has stopped reading: the first frame is picked up and the write never
            // returns, so everything after it piles up behind exactly as it would in the real thing.
            // Room for every frame offered, but not for every byte: the video is what does not fit,
            // and the question is whether the audio is thrown out alongside it.
            var queue = new OutboundQueue(pool, 64, 200 * 1000,
                frame =>
                {
                    lock (written)
                    {
                        written.Add(frame.StreamType);
                    }

                    firstTaken.Set();
                    stuck.Wait(TimeSpan.FromSeconds(10));
                },
                "a client that stopped reading", NullLogger.Instance);

            try
            {
                queue.Enqueue(Frame(VIDEO_TRACK, TrackType.Video, 20000));
                Assert.IsTrue(firstTaken.Wait(TimeSpan.FromSeconds(5)), "the writer never started");

                // Twenty of each: 3KB of audio and 400KB of video, into a 200KB budget. Audio is
                // small and frequent, video large and occasional, which is what makes the two so
                // unequal a fit for one budget.
                for (int i = 0; i < 20; i++)
                {
                    queue.Enqueue(Frame(AUDIO_TRACK, TrackType.Audio, 160));
                    queue.Enqueue(Frame(VIDEO_TRACK, TrackType.Video, 20000));
                }
            }
            finally
            {
                stuck.Set();
            }

            // let what survived be written
            Thread.Sleep(1000);

            int audio, video;
            lock (written)
            {
                audio = written.Count(t => t == AUDIO_TRACK);
                video = written.Count(t => t == VIDEO_TRACK);
            }

            Assert.IsGreaterThan(0, audio + video, "nothing was written at all");

            // The audio fits many times over in what the video could not use. Losing any of it to
            // make room for pictures is what a listener hears as stuttering.
            Assert.IsGreaterThanOrEqualTo(20, audio,
                $"only {audio} of 20 audio frames survived, against {video} video frames");
        }
    }
}
