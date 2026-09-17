using System;
using SharpRTSPClient;

namespace SharpRTSPClient.Tests
{
    /// <summary>
    /// Placing a stream's RTP timestamps on the sender's clock, which is what makes two streams of
    /// one session comparable.
    /// </summary>
    /// <remarks>
    /// Each stream runs its own clock, at its own rate, from a starting point the sender picked at
    /// random - so their RTP timestamps say nothing about one another. A sender report pairs one
    /// wall clock time with the RTP timestamp of that instant, and that pairing is the whole of it.
    /// </remarks>
    [TestClass]
    public sealed class SenderClockSyncTests
    {
        private const int VideoClockRate = 90000;
        private const int AudioClockRate = 44100;

        private static readonly DateTime Anchor = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);

        private static RtcpChannelState Channel(int clockRate)
        {
            return new RtcpChannelState { ClockRate = clockRate };
        }

        [TestMethod]
        public void WithoutASenderReportNothingCanBePlaced()
        {
            var channel = Channel(VideoClockRate);

            Assert.IsFalse(channel.HasSenderSync);
            Assert.IsFalse(channel.TryMapToSenderClock(123456, out _),
                "there is nothing to place it against until a report arrives");
        }

        [TestMethod]
        public void ThePacketTheReportNamesLandsOnTheReportedTime()
        {
            var channel = Channel(VideoClockRate);
            channel.RecordSenderReport(Anchor, 1_000_000);

            Assert.IsTrue(channel.TryMapToSenderClock(1_000_000, out DateTime when));
            Assert.AreEqual(Anchor, when);
        }

        [TestMethod]
        public void LaterPacketsAdvanceAtTheClockRate()
        {
            var channel = Channel(VideoClockRate);
            channel.RecordSenderReport(Anchor, 1_000_000);

            // a second of video is 90000 ticks
            Assert.IsTrue(channel.TryMapToSenderClock(1_090_000, out DateTime oneSecond));
            Assert.AreEqual(Anchor.AddSeconds(1), oneSecond);

            // and a fortieth of a second is 2250
            Assert.IsTrue(channel.TryMapToSenderClock(1_002_250, out DateTime oneFrame));
            Assert.AreEqual(Anchor.AddSeconds(0.025), oneFrame);
        }

        [TestMethod]
        public void PacketsBeforeTheReportGoBackwards()
        {
            var channel = Channel(VideoClockRate);
            channel.RecordSenderReport(Anchor, 1_000_000);

            // a report describes an instant, and packets either side of it are placed either side
            Assert.IsTrue(channel.TryMapToSenderClock(910_000, out DateTime before));
            Assert.AreEqual(Anchor.AddSeconds(-1), before);
        }

        [TestMethod]
        public void TheClockWrappingIsNotReadAsAJumpBackwards()
        {
            var channel = Channel(VideoClockRate);

            // a report just short of where a 32 bit clock runs out
            channel.RecordSenderReport(Anchor, uint.MaxValue - 45000);

            // and a packet half a second later, which has wrapped round to a small number
            Assert.IsTrue(channel.TryMapToSenderClock(45000 - 1, out DateTime when));

            Assert.AreEqual(Anchor.AddSeconds(1), when,
                "a wrap is thirteen hours of video, not a jump back in time");
        }

        [TestMethod]
        public void TwoStreamsOfOneSessionLandOnOneTimeline()
        {
            // the point of the whole exercise: different rates, unrelated starting points
            var video = Channel(VideoClockRate);
            var audio = Channel(AudioClockRate);

            video.RecordSenderReport(Anchor, 1_286_285_715);
            audio.RecordSenderReport(Anchor, 1_215_322_853);

            // a video frame and an audio frame, each a quarter of a second after its own report
            Assert.IsTrue(video.TryMapToSenderClock(1_286_285_715 + VideoClockRate / 4, out DateTime videoTime));
            Assert.IsTrue(audio.TryMapToSenderClock(1_215_322_853 + AudioClockRate / 4, out DateTime audioTime));

            Assert.AreEqual(videoTime, audioTime,
                "timestamps that are worlds apart are the same instant once each is placed on the sender's clock");
        }

        [TestMethod]
        public void AClockRateWeNeverLearnedMeansNoSync()
        {
            var channel = Channel(0);
            channel.RecordSenderReport(Anchor, 1000);

            Assert.IsFalse(channel.TryMapToSenderClock(2000, out _),
                "a report says when a timestamp happened, not how fast the clock runs");
        }

        [TestMethod]
        public void AReconnectStartsFromNothingAgain()
        {
            var channel = Channel(VideoClockRate);
            channel.RecordSenderReport(Anchor, 1000);
            Assert.IsTrue(channel.HasSenderSync);

            // the new stream's clock starts wherever the sender decides, so the old pairing is void
            channel.Reset();

            Assert.IsFalse(channel.HasSenderSync);
            Assert.IsFalse(channel.TryMapToSenderClock(1000, out _));
        }

        [TestMethod]
        public void ALaterReportReplacesTheOne()
        {
            var channel = Channel(VideoClockRate);
            channel.RecordSenderReport(Anchor, 1_000_000);

            // senders re-report as their clocks drift, and the newest pairing is the one to use
            channel.RecordSenderReport(Anchor.AddSeconds(5), 1_450_000);

            Assert.IsTrue(channel.TryMapToSenderClock(1_450_000, out DateTime when));
            Assert.AreEqual(Anchor.AddSeconds(5), when);
        }
    }
}
