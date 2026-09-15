using System;
using System.Collections.Generic;
using System.Linq;
using SharpRTSPServer;

namespace SharpRTSPServer.Tests
{
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

        [Fact]
        public void SmallNal_IsSentAsASingleRtpPacket()
        {
            var track = new H264Track(Sps, Pps);
            byte[] nal = Nal(0x65, 100);

            byte[] packet = Assert.Single(Packetize(track, nal));

            Assert.Equal(12 + nal.Length, packet.Length);
            Assert.Equal(2, packet[0] >> 6);                    // RTP version
            Assert.Equal(track.PayloadType, packet[1] & 0x7F);  // payload type
            Assert.Equal(Timestamp, ReadTimestamp(packet));
            Assert.True(IsMarkerSet(packet));                   // last NAL of the frame
            Assert.Equal(nal, packet.Skip(12).ToArray());       // payload is the NAL verbatim
        }

        [Fact]
        public void LargeNal_IsFragmentedIntoFuAPackets()
        {
            var track = new H264Track(Sps, Pps) { PacketMTU = 1400 };
            byte[] nal = Nal(0x65, 5000); // 0x65 = NRI 3, type 5 (IDR)

            var packets = Packetize(track, nal);

            Assert.True(packets.Count > 1, "a NAL larger than the MTU must be fragmented");

            foreach (var packet in packets)
            {
                Assert.Equal(Timestamp, ReadTimestamp(packet));
                Assert.Equal(28, packet[12] & 0x1F);            // FU-A fragmentation type
                Assert.Equal(3, (packet[12] >> 5) & 0x03);      // NRI carried over from the NAL
                Assert.Equal(5, packet[13] & 0x1F);             // original NAL type carried in the FU header
            }

            // exactly one start bit, on the first fragment
            Assert.True((packets.First()[13] & 0x80) != 0);
            Assert.All(packets.Skip(1), p => Assert.True((p[13] & 0x80) == 0));

            // exactly one end bit, on the last fragment
            Assert.True((packets.Last()[13] & 0x40) != 0);
            Assert.All(packets.Take(packets.Count - 1), p => Assert.True((p[13] & 0x40) == 0));

            // only the final fragment of the final NAL marks the end of the frame
            Assert.True(IsMarkerSet(packets.Last()));
            Assert.All(packets.Take(packets.Count - 1), p => Assert.False(IsMarkerSet(p)));
        }

        [Fact]
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

            Assert.Equal(nal, reassembled.ToArray());
        }

        [Fact]
        public void OnlyTheLastNalOfAFrameSetsTheMarkerBit()
        {
            var track = new H264Track(Sps, Pps);

            var packets = Packetize(track, Nal(0x67, 10), Nal(0x68, 10), Nal(0x65, 10));

            Assert.Equal(3, packets.Count);
            Assert.False(IsMarkerSet(packets[0]));
            Assert.False(IsMarkerSet(packets[1]));
            Assert.True(IsMarkerSet(packets[2]));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(44)]   // exactly the header overhead, leaving no room for payload
        public void PacketMtuTooSmallToCarryPayloadIsRejected(int packetMtu)
        {
            var track = new H264Track(Sps, Pps) { PacketMTU = packetMtu };

            // the fragmentation loop would otherwise never make progress
            Assert.Throws<InvalidOperationException>(() => Packetize(track, Nal(0x65, 5000)));
        }

        [Fact]
        public void SmallButWorkablePacketMtuStillFragments()
        {
            var track = new H264Track(Sps, Pps) { PacketMTU = 100 };

            var packets = Packetize(track, Nal(0x65, 1000));

            Assert.True(packets.Count > 1);
            Assert.All(packets, p => Assert.True(p.Length <= 14 + 100));
        }

        [Fact]
        public void TrackIsOnlyReadyOnceItHasBothParameterSets()
        {
            var track = new H264Track();
            Assert.False(track.IsReady);

            track.SetParameterSets(Sps, Pps);
            Assert.True(track.IsReady);
        }

        [Fact]
        public void SdpCarriesTheParameterSetsAndProfileLevel()
        {
            var track = new H264Track(Sps, Pps, profileIdc: 77, profileIop: 0, level: 42) { ID = 0 };

            string sdp = track.BuildSDP(new System.Text.StringBuilder()).ToString();

            Assert.Contains("m=video 0 RTP/AVP 96", sdp);
            Assert.Contains("a=control:trackID=0", sdp);
            Assert.Contains("a=rtpmap:96 H264/90000", sdp);
            Assert.Contains("profile-level-id=4D002A", sdp); // 77, 0, 42 in hex
            Assert.Contains($"sprop-parameter-sets={Convert.ToBase64String(Sps)},{Convert.ToBase64String(Pps)}", sdp);
        }
    }
}
