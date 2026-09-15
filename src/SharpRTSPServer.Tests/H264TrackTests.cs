using System;
using System.Collections.Generic;
using System.Linq;
using SharpRTSPServer;

namespace SharpRTSPServer.Tests
{
    [TestClass]
    public class H264TrackTests
    {
        private const uint Timestamp = 0x11223344;

        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        /// <summary>
        /// Packetizes one NAL and returns the resulting RTP packets as plain arrays.
        /// </summary>
        private static List<byte[]> Packetize(H264Track track, params byte[][] nals)
        {
            var samples = nals.Select(n => new ReadOnlyMemory<byte>(n)).ToList();
            var (packets, owners) = track.CreateRtpPackets(samples, Timestamp);

            // the packets are pooled buffers that the caller owns, so copy before releasing them
            var copies = packets.Select(p => p.ToArray()).ToList();
            foreach (var owner in owners)
            {
                owner.Dispose();
            }
            return copies;
        }

        private static byte[] Nal(byte firstByte, int length)
        {
            byte[] nal = new byte[length];
            nal[0] = firstByte;
            for (int i = 1; i < length; i++)
            {
                nal[i] = (byte)(i & 0xFF);
            }
            return nal;
        }

        private static uint ReadTimestamp(byte[] rtpPacket) =>
            (uint)((rtpPacket[4] << 24) | (rtpPacket[5] << 16) | (rtpPacket[6] << 8) | rtpPacket[7]);

        private static bool IsMarkerSet(byte[] rtpPacket) => (rtpPacket[1] & 0x80) != 0;

        [TestMethod]
        public void SmallNal_IsSentAsASingleRtpPacket()
        {
            var track = new H264Track(Sps, Pps);
            byte[] nal = Nal(0x65, 100);

            byte[] packet = Assert.ContainsSingle(Packetize(track, nal));

            Assert.HasCount(12 + nal.Length, packet);
            Assert.AreEqual(2, packet[0] >> 6);                    // RTP version
            Assert.AreEqual(track.PayloadType, packet[1] & 0x7F);  // payload type
            Assert.AreEqual(Timestamp, ReadTimestamp(packet));
            Assert.IsTrue(IsMarkerSet(packet));                   // last NAL of the frame
            CollectionAssert.AreEqual(nal, packet.Skip(12).ToArray());       // payload is the NAL verbatim
        }

        [TestMethod]
        public void LargeNal_IsFragmentedIntoFuAPackets()
        {
            var track = new H264Track(Sps, Pps) { PacketMTU = 1400 };
            byte[] nal = Nal(0x65, 5000); // 0x65 = NRI 3, type 5 (IDR)

            var packets = Packetize(track, nal);

            Assert.IsGreaterThan(1, packets.Count, "a NAL larger than the MTU must be fragmented");

            foreach (var packet in packets)
            {
                Assert.AreEqual(Timestamp, ReadTimestamp(packet));
                Assert.AreEqual(28, packet[12] & 0x1F);            // FU-A fragmentation type
                Assert.AreEqual(3, (packet[12] >> 5) & 0x03);      // NRI carried over from the NAL
                Assert.AreEqual(5, packet[13] & 0x1F);             // original NAL type carried in the FU header
            }

            // exactly one start bit, on the first fragment
            Assert.AreNotEqual(0, packets.First()[13] & 0x80);
            foreach (var p in packets.Skip(1))
            {
                Assert.AreEqual(0, p[13] & 0x80);
            }

            // exactly one end bit, on the last fragment
            Assert.AreNotEqual(0, packets.Last()[13] & 0x40);
            foreach (var p in packets.Take(packets.Count - 1))
            {
                Assert.AreEqual(0, p[13] & 0x40);
            }

            // only the final fragment of the final NAL marks the end of the frame
            Assert.IsTrue(IsMarkerSet(packets.Last()));
            foreach (var p in packets.Take(packets.Count - 1))
            {
                Assert.IsFalse(IsMarkerSet(p));
            }
        }

        [TestMethod]
        public void FragmentsReassembleIntoTheOriginalNal()
        {
            var track = new H264Track(Sps, Pps) { PacketMTU = 1400 };
            byte[] nal = Nal(0x65, 5000);

            var packets = Packetize(track, nal);

            // FU-A drops the original NAL header byte and rebuilds it from the FU indicator and header
            byte rebuiltHeader = (byte)((packets[0][12] & 0xE0) | (packets[0][13] & 0x1F));
            var reassembled = new List<byte> { rebuiltHeader };
            foreach (var packet in packets)
            {
                reassembled.AddRange(packet.Skip(14));
            }

            CollectionAssert.AreEqual(nal, reassembled.ToArray());
        }

        [TestMethod]
        public void OnlyTheLastNalOfAFrameSetsTheMarkerBit()
        {
            var track = new H264Track(Sps, Pps);

            var packets = Packetize(track, Nal(0x67, 10), Nal(0x68, 10), Nal(0x65, 10));

            Assert.HasCount(3, packets);
            Assert.IsFalse(IsMarkerSet(packets[0]));
            Assert.IsFalse(IsMarkerSet(packets[1]));
            Assert.IsTrue(IsMarkerSet(packets[2]));
        }

        [TestMethod]
        [DataRow(1)]
        [DataRow(44)]   // exactly the header overhead, leaving no room for payload
        public void PacketMtuTooSmallToCarryPayloadIsRejected(int packetMtu)
        {
            var track = new H264Track(Sps, Pps) { PacketMTU = packetMtu };

            // the fragmentation loop would otherwise never make progress
            Assert.ThrowsExactly<InvalidOperationException>(() => Packetize(track, Nal(0x65, 5000)));
        }

        [TestMethod]
        public void SmallButWorkablePacketMtuStillFragments()
        {
            var track = new H264Track(Sps, Pps) { PacketMTU = 100 };

            var packets = Packetize(track, Nal(0x65, 1000));

            Assert.IsGreaterThan(1, packets.Count);
            foreach (var p in packets)
            {
                Assert.IsLessThanOrEqualTo(14 + 100, p.Length);
            }
        }

        [TestMethod]
        public void TrackIsOnlyReadyOnceItHasBothParameterSets()
        {
            var track = new H264Track();
            Assert.IsFalse(track.IsReady);

            track.SetParameterSets(Sps, Pps);
            Assert.IsTrue(track.IsReady);
        }

        [TestMethod]
        public void SdpCarriesTheParameterSetsAndProfileLevel()
        {
            var track = new H264Track(Sps, Pps, profileIdc: 77, profileIop: 0, level: 42) { ID = 0 };

            string sdp = track.BuildSDP(new System.Text.StringBuilder()).ToString();

            StringAssert.Contains(sdp, "m=video 0 RTP/AVP 96");
            StringAssert.Contains(sdp, "a=control:trackID=0");
            StringAssert.Contains(sdp, "a=rtpmap:96 H264/90000");
            StringAssert.Contains(sdp, "profile-level-id=4D002A"); // 77, 0, 42 in hex
            StringAssert.Contains(sdp, $"sprop-parameter-sets={Convert.ToBase64String(Sps)},{Convert.ToBase64String(Pps)}");
        }
    }
}
