using System;
using System.Collections.Generic;
using System.Linq;
using SharpRTSPServer;

namespace SharpRTSPServer.Tests
{
    [TestClass]
    public class AACTrackTests
    {
        private const uint Timestamp = 0x0A0B0C0D;

        private static readonly byte[] ConfigDescriptor = { 0x12, 0x10 };

        private static List<byte[]> Packetize(AACTrack track, params byte[][] frames)
        {
            var samples = frames.Select(f => new ReadOnlyMemory<byte>(f)).ToList();
            var (packets, owners) = track.CreateRtpPackets(samples, Timestamp);

            var copies = packets.Select(p => p.ToArray()).ToList();
            foreach (var owner in owners)
            {
                owner.Dispose();
            }
            return copies;
        }

        private static AACTrack NewTrack() => new AACTrack(ConfigDescriptor, 44100, 2);

        [TestMethod]
        public void FrameIsPrefixedWithAnAuHeaderCarryingItsLength()
        {
            var track = NewTrack();
            byte[] frame = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();

            byte[] packet = Assert.ContainsSingle(Packetize(track, frame));

            Assert.HasCount(12 + 4 + frame.Length, packet);

            // AU headers-length is a fixed 16 bits
            Assert.AreEqual(0x00, packet[12]);
            Assert.AreEqual(0x10, packet[13]);

            // the AU-size occupies the top 13 bits of the 16 bit AU header
            int auSize = ((packet[14] << 8) | packet[15]) >> 3;
            Assert.AreEqual(frame.Length, auSize);

            CollectionAssert.AreEqual(frame, packet.Skip(16).ToArray());
        }

        [TestMethod]
        public void AudioPacketsAlwaysSetTheMarkerBit()
        {
            var track = NewTrack();

            var packets = Packetize(track, new byte[10], new byte[20]);

            Assert.HasCount(2, packets);
            foreach (var p in packets)
            {
                Assert.AreNotEqual(0, p[1] & 0x80);
            }
        }

        [TestMethod]
        public void FrameAtTheAuSizeLimit_IsAccepted()
        {
            var track = NewTrack();
            byte[] frame = new byte[AACTrack.MAX_FRAME_LENGTH];

            byte[] packet = Assert.ContainsSingle(Packetize(track, frame));

            int auSize = ((packet[14] << 8) | packet[15]) >> 3;
            Assert.AreEqual(AACTrack.MAX_FRAME_LENGTH, auSize);
        }

        [TestMethod]
        public void FrameTooLargeForTheAuSizeField_IsRejectedRatherThanCorrupted()
        {
            var track = NewTrack();
            byte[] frame = new byte[AACTrack.MAX_FRAME_LENGTH + 1];

            // the AU-size is 13 bits wide, so a longer frame cannot be described - it used to
            // overflow the field silently and produce an undecodable stream
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Packetize(track, frame));
        }

        [TestMethod]
        public void TrackIsOnlyReadyOnceItHasAConfigDescriptor()
        {
            var track = new AACTrack(44100, 2);
            Assert.IsFalse(track.IsReady);

            track.SetConfigDescriptor(ConfigDescriptor);
            Assert.IsTrue(track.IsReady);
        }

        [TestMethod]
        public void SdpCarriesTheConfigDescriptorAndChannelLayout()
        {
            var track = NewTrack();
            track.ID = 1;

            string sdp = track.BuildSDP(new System.Text.StringBuilder()).ToString();

            StringAssert.Contains(sdp, "m=audio 0 RTP/AVP 97");
            StringAssert.Contains(sdp, "a=control:trackID=1");
            StringAssert.Contains(sdp, "a=rtpmap:97 mpeg4-generic/44100/2");
            StringAssert.Contains(sdp, "config=1210");
            StringAssert.Contains(sdp, "mode=AAC-hbr");
            StringAssert.Contains(sdp, "sizeLength=13");
        }
    }
}
