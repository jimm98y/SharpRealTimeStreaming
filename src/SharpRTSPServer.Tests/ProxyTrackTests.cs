using System;
using System.Collections.Generic;
using System.Linq;
using SharpRTSPServer;

namespace SharpRTSPServer.Tests
{
    [TestClass]
    public class ProxyTrackTests
    {
        private static List<byte[]> Packetize(ProxyTrack track, params byte[][] samples)
        {
            var input = samples.Select(s => new ReadOnlyMemory<byte>(s)).ToList();
            var (packets, owners) = track.CreateRtpPackets(input, 0);

            var copies = packets.Select(p => p.ToArray()).ToList();
            foreach (var owner in owners)
            {
                owner.Dispose();
            }
            return copies;
        }

        [TestMethod]
        public void EverySampleIsForwarded()
        {
            var track = new ProxyTrack(TrackType.Video);
            byte[] first = { 1, 2, 3 };
            byte[] second = { 4, 5, 6, 7 };
            byte[] third = { 8 };

            var packets = Packetize(track, first, second, third);

            // all three used to be dropped except the first, silently losing media
            Assert.HasCount(3, packets);
            CollectionAssert.AreEqual(first, packets[0]);
            CollectionAssert.AreEqual(second, packets[1]);
            CollectionAssert.AreEqual(third, packets[2]);
        }

        [TestMethod]
        public void PacketsArePassedThroughByteForByte()
        {
            var track = new ProxyTrack(TrackType.Video);
            byte[] rtp = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();

            CollectionAssert.AreEqual(rtp, Assert.ContainsSingle(Packetize(track, rtp)));
        }

        [TestMethod]
        public void NoSamplesProducesNoPackets()
        {
            var track = new ProxyTrack(TrackType.Video);

            // used to throw IndexOutOfRangeException reaching for samples[0]
            Assert.IsEmpty(Packetize(track));
        }

        [TestMethod]
        public void EmptySamplesAreSkipped()
        {
            var track = new ProxyTrack(TrackType.Video);

            var packets = Packetize(track, new byte[0], new byte[] { 9, 9 }, new byte[0]);

            CollectionAssert.AreEqual(new byte[] { 9, 9 }, Assert.ContainsSingle(packets));
        }

        [TestMethod]
        public void NullSamplesAreRejected()
        {
            var track = new ProxyTrack(TrackType.Video);

            Assert.ThrowsExactly<ArgumentNullException>(() => track.CreateRtpPackets(null, 0));
        }

        [TestMethod]
        public void TrackIsNotReadyUntilItIsStarted()
        {
            var track = new ProxyTrack(TrackType.Audio);
            Assert.IsFalse(track.IsReady);

            track.Start();
            Assert.IsTrue(track.IsReady);

            track.Dispose();
            Assert.IsFalse(track.IsReady);
        }

        [TestMethod]
        public void TrackTypeDeterminesTheId()
        {
            Assert.AreEqual((int)TrackType.Video, new ProxyTrack(TrackType.Video).ID);
            Assert.AreEqual((int)TrackType.Audio, new ProxyTrack(TrackType.Audio).ID);
        }

        private static byte[] RtpPacket(ushort seq, uint ssrc, byte payloadType = 96)
        {
            var packet = new byte[16];
            packet[0] = 0x80;
            packet[1] = payloadType;
            packet[2] = (byte)(seq >> 8);
            packet[3] = (byte)seq;
            packet[8] = (byte)(ssrc >> 24);
            packet[9] = (byte)(ssrc >> 16);
            packet[10] = (byte)(ssrc >> 8);
            packet[11] = (byte)ssrc;
            return packet;
        }

        [TestMethod]
        public void PassthroughIsOffByDefault()
        {
            var track = new ProxyTrack(TrackType.Video);

            Assert.IsFalse(track.PreserveSourceHeaders);
            Assert.IsFalse(track.HasLearnedSourceSsrc);

            uint before = track.SSRC;
            Packetize(track, RtpPacket(7, 0xDEADBEEF));

            Assert.AreEqual(before, track.SSRC, "the track's own SSRC should be left alone");
            Assert.IsFalse(track.HasLearnedSourceSsrc);
        }

        [TestMethod]
        public void PassthroughTakesTheSsrcFromTheForwardedRtp()
        {
            var track = new ProxyTrack(TrackType.Video) { PreserveSourceHeaders = true };

            Packetize(track, RtpPacket(7, 0x3C790025));

            Assert.IsTrue(track.HasLearnedSourceSsrc);
            Assert.AreEqual(0x3C790025u, track.SSRC, "the SETUP reply and RTCP have to name the SSRC the RTP carries");
        }

        [TestMethod]
        public void PassthroughFollowsTheSourceWhenItChangesSsrc()
        {
            var track = new ProxyTrack(TrackType.Video) { PreserveSourceHeaders = true };

            Packetize(track, RtpPacket(1, 0x11111111));
            Assert.AreEqual(0x11111111u, track.SSRC);

            // a source that restarts, or a capture that loops round to a different session
            Packetize(track, RtpPacket(2, 0x22222222));
            Assert.AreEqual(0x22222222u, track.SSRC);
        }

        [TestMethod]
        public void AShortPacketDoesNotChangeTheLearnedSsrc()
        {
            var track = new ProxyTrack(TrackType.Video) { PreserveSourceHeaders = true };
            uint before = track.SSRC;

            // too short to hold an SSRC - forwarded anyway, but nothing is read out of it
            Packetize(track, new byte[] { 0x80, 96, 0, 1 });

            Assert.IsFalse(track.HasLearnedSourceSsrc);
            Assert.AreEqual(before, track.SSRC);
        }

    }
}
