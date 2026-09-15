using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SharpRTSPServer;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// Packetization for the codecs that had no coverage of their own.
    /// </summary>
    public class PacketizationTests
    {
        private const uint Timestamp = 0x11223344;

        private static List<byte[]> Packetize(ITrack track, params byte[][] samples)
        {
            var input = samples.Select(s => new ReadOnlyMemory<byte>(s)).ToList();
            var (packets, owners) = track.CreateRtpPackets(input, Timestamp);

            var copies = packets.Select(p => p.ToArray()).ToList();
            foreach (var owner in owners)
            {
                owner.Dispose();
            }
            return copies;
        }

        private static byte[] Payload(int length, byte first = 0x01)
        {
            byte[] data = new byte[length];
            data[0] = first;
            for (int i = 1; i < length; i++)
            {
                data[i] = (byte)(i & 0xFF);
            }
            return data;
        }

        private static uint ReadTimestamp(byte[] packet) =>
            (uint)((packet[4] << 24) | (packet[5] << 16) | (packet[6] << 8) | packet[7]);

        private static bool IsMarkerSet(byte[] packet) => (packet[1] & 0x80) != 0;

        #region H265

        [Fact]
        public void H265SmallNalGoesIntoOnePacket()
        {
            var track = new H265Track();
            byte[] nal = Payload(100, 0x26); // NAL type 19 (IDR_W_RADL)

            byte[] packet = Assert.Single(Packetize(track, nal));

            Assert.Equal(12 + nal.Length, packet.Length);
            Assert.Equal(Timestamp, ReadTimestamp(packet));
            Assert.True(IsMarkerSet(packet));
            Assert.Equal(nal, packet.Skip(12).ToArray());
        }

        [Fact]
        public void H265LargeNalIsFragmented()
        {
            var track = new H265Track { PacketMTU = 1400 };
            byte[] nal = Payload(5000, 0x26);

            var packets = Packetize(track, nal);

            Assert.True(packets.Count > 1);

            // H265 fragmentation unit: 2 byte payload header with type 49, then the FU header
            foreach (var packet in packets)
            {
                Assert.Equal(49, (packet[12] >> 1) & 0x3F);
                Assert.Equal(19, packet[14] & 0x3F); // original NAL type in the FU header
            }

            Assert.True((packets.First()[14] & 0x80) != 0, "start bit on the first fragment");
            Assert.True((packets.Last()[14] & 0x40) != 0, "end bit on the last fragment");
            Assert.True(IsMarkerSet(packets.Last()));
            Assert.All(packets.Take(packets.Count - 1), p => Assert.False(IsMarkerSet(p)));
        }

        [Fact]
        public void H265FragmentsReassembleIntoTheOriginalNal()
        {
            var track = new H265Track { PacketMTU = 1400 };
            byte[] nal = Payload(5000, 0x26);

            var packets = Packetize(track, nal);

            // the two byte NAL header is rebuilt from the FU payload header and FU header
            var reassembled = new List<byte>
            {
                (byte)((packets[0][12] & 0x81) | ((packets[0][14] & 0x3F) << 1)),
                packets[0][13],
            };
            foreach (var packet in packets)
            {
                reassembled.AddRange(packet.Skip(15));
            }

            Assert.Equal(nal, reassembled.ToArray());
        }

        [Fact]
        public void H265SdpCarriesTheParameterSets()
        {
            var track = new H265Track(new byte[] { 0x40, 0x01 }, new byte[] { 0x42, 0x01 }, new byte[] { 0x44, 0x01 }) { ID = 0 };

            string sdp = track.BuildSDP(new StringBuilder()).ToString();

            Assert.Contains("a=rtpmap:96 H265/90000", sdp);
            Assert.Contains("sprop-vps=", sdp);
            Assert.Contains("sprop-sps=", sdp);
            Assert.Contains("sprop-pps=", sdp);
        }

        #endregion

        #region H266

        [Fact]
        public void H266SmallNalGoesIntoOnePacket()
        {
            var track = new H266Track();
            byte[] nal = Payload(100, 0x00);

            byte[] packet = Assert.Single(Packetize(track, nal));

            Assert.Equal(12 + nal.Length, packet.Length);
            Assert.Equal(Timestamp, ReadTimestamp(packet));
            Assert.True(IsMarkerSet(packet));
            Assert.Equal(nal, packet.Skip(12).ToArray());
        }

        [Fact]
        public void H266LargeNalIsFragmented()
        {
            var track = new H266Track { PacketMTU = 1400 };

            var packets = Packetize(track, Payload(5000, 0x00));

            Assert.True(packets.Count > 1);
            Assert.True(IsMarkerSet(packets.Last()));
            Assert.All(packets.Take(packets.Count - 1), p => Assert.False(IsMarkerSet(p)));
        }

        #endregion

        #region AV1

        [Fact]
        public void AV1TemporalDelimiterAndTileListObusAreDropped()
        {
            var track = new AV1Track();

            // OBU type is bits 6..3 of the header: 4 is a temporal delimiter, 2 a tile list
            byte temporalDelimiter = (4 << 3);
            byte tileList = (2 << 3);

            Assert.Empty(Packetize(track, new byte[] { temporalDelimiter, 0x00 }));
            Assert.Empty(Packetize(track, new byte[] { tileList, 0x00 }));
        }

        [Fact]
        public void AV1FrameObuIsPacketized()
        {
            var track = new AV1Track();

            var packets = Packetize(track, FrameObu(100));

            Assert.NotEmpty(packets);
            Assert.All(packets, p => Assert.Equal(Timestamp, ReadTimestamp(p)));
        }

        /// <summary>An OBU_FRAME with no size field, followed by the given number of payload bytes.</summary>
        private static byte[] FrameObu(int payloadLength) =>
            new byte[] { (6 << 3) }.Concat(Payload(payloadLength)).ToArray();

        [Fact]
        public void AV1MarkerGoesOnTheLastObuThatIsActuallySent()
        {
            var track = new AV1Track();
            byte temporalDelimiter = (4 << 3);

            // the temporal delimiter is dropped, so the frame before it has to carry the marker -
            // taking the last sample outright left the frame unmarked and never completed
            var packets = Packetize(track, FrameObu(100), new byte[] { temporalDelimiter });

            Assert.NotEmpty(packets);
            Assert.True(IsMarkerSet(packets.Last()), "the last sent packet must close the frame");
        }

        [Theory]
        [InlineData(1)]
        [InlineData(40)]
        [InlineData(44)] // exactly the header overhead - the payload size lands on zero
        public void AV1PacketMtuTooSmallIsRejected(int packetMtu)
        {
            var track = new AV1Track { PacketMTU = packetMtu };

            // At 44 the payload size is zero, so the loop consumed nothing and allocated a packet
            // on every pass - it never terminated. Smaller values crashed out of the buffer pool.
            Assert.Throws<InvalidOperationException>(() => Packetize(track, FrameObu(100)));
        }

        [Fact]
        public void AV1EmptySampleIsSkipped()
        {
            var track = new AV1Track();

            Assert.Empty(Packetize(track, new byte[0]));
            Assert.NotEmpty(Packetize(track, new byte[0], FrameObu(50)));
        }

        [Fact]
        public void AV1ObuDeclaringASizeFieldItDoesNotHaveIsRejected()
        {
            var track = new AV1Track();

            // obu_has_size_field set, but nothing follows the header to read it from
            byte headerWithSizeField = (byte)((6 << 3) | 0x02);

            Assert.Throws<ArgumentException>(() => Packetize(track, new byte[] { headerWithSizeField }));
        }

        [Fact]
        public void AV1SizeFieldIsStrippedFromThePayload()
        {
            var track = new AV1Track();
            byte headerWithSizeField = (byte)((6 << 3) | 0x02);

            // header, leb128 size of 3, then 3 payload bytes
            var packets = Packetize(track, new byte[] { headerWithSizeField, 0x03, 0xAA, 0xBB, 0xCC });

            byte[] packet = Assert.Single(packets);

            // the size field is removed and the flag cleared, leaving header + 3 payload bytes
            Assert.Equal(12 + 1 + 4, packet.Length);
            Assert.Equal(0, packet[13] & 0x02);
            Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, packet.Skip(14).ToArray());
        }

        #endregion

        #region Audio

        public static TheoryData<ITrack, int> SimpleAudioTracks() => new TheoryData<ITrack, int>
        {
            { new PCMUTrack(), 0 },   // static payload type 0
            { new PCMATrack(), 8 },   // static payload type 8
            { new OpusTrack(), 97 },  // dynamic, 96 + track ID 1
        };

        [Theory]
        [MemberData(nameof(SimpleAudioTracks))]
        public void AudioFrameIsCopiedVerbatimAfterTheRtpHeader(ITrack track, int expectedPayloadType)
        {
            byte[] frame = Payload(160);

            byte[] packet = Assert.Single(Packetize(track, frame));

            Assert.Equal(12 + frame.Length, packet.Length);
            Assert.Equal(expectedPayloadType, packet[1] & 0x7F);
            Assert.Equal(Timestamp, ReadTimestamp(packet));
            Assert.True(IsMarkerSet(packet), "audio packets are always the last of their timestamp");
            Assert.Equal(frame, packet.Skip(12).ToArray());
        }

        [Fact]
        public void EachAudioFrameBecomesItsOwnPacket()
        {
            var track = new PCMUTrack();

            var packets = Packetize(track, Payload(160), Payload(160), Payload(160));

            Assert.Equal(3, packets.Count);
            Assert.All(packets, p => Assert.Equal(Timestamp, ReadTimestamp(p)));
        }

        [Theory]
        [InlineData(typeof(PCMUTrack), 0, "a=rtpmap:0 PCMU/8000")]
        [InlineData(typeof(PCMATrack), 8, "a=rtpmap:8 PCMA/8000")]
        public void G711SdpAdvertisesTheStaticPayloadTypeAtEightKilohertz(Type trackType, int payloadType, string expectedRtpMap)
        {
            var track = (ITrack)Activator.CreateInstance(trackType);

            string sdp = track.BuildSDP(new StringBuilder()).ToString();

            Assert.Contains($"m=audio 0 RTP/AVP {payloadType}", sdp);
            Assert.Contains(expectedRtpMap, sdp);
        }

        [Fact]
        public void OpusSdpAdvertisesFortyEightKilohertzStereo()
        {
            string sdp = new OpusTrack().BuildSDP(new StringBuilder()).ToString();

            Assert.Contains("opus/48000/2", sdp);
        }

        #endregion

        #region Shared behaviour

        public static TheoryData<ITrack> FragmentingVideoTracks() => new TheoryData<ITrack>
        {
            // 44 bytes is exactly the IP, UDP and RTP header overhead, leaving nothing for payload
            new H264Track(new byte[] { 0x67 }, new byte[] { 0x68 }) { PacketMTU = 1 },
            new H264Track(new byte[] { 0x67 }, new byte[] { 0x68 }) { PacketMTU = 44 },
            new H265Track { PacketMTU = 1 },
            new H265Track { PacketMTU = 44 },
            new H266Track { PacketMTU = 1 },
            new H266Track { PacketMTU = 44 },
        };

        [Theory]
        [MemberData(nameof(FragmentingVideoTracks))]
        public void PacketMtuTooSmallToCarryPayloadIsRejected(ITrack track)
        {
            // With a payload size of zero the fragmentation loop consumes nothing and never ends;
            // with a negative one it crashes out of the buffer pool. Both are rejected up front.
            Assert.Throws<InvalidOperationException>(() => Packetize(track, Payload(5000, 0x26)));
        }

        public static TheoryData<ITrack> NalTracks() => new TheoryData<ITrack>
        {
            new H264Track(new byte[] { 0x67 }, new byte[] { 0x68 }) { PacketMTU = 1400 },
            new H265Track { PacketMTU = 1400 },
            new H266Track { PacketMTU = 1400 },
        };

        [Theory]
        [MemberData(nameof(NalTracks))]
        public void ShortNalIsSentWholeRatherThanFragmented(ITrack track)
        {
            // H265 and H266 read a two byte NAL header, but only on the fragmenting path, which a
            // NAL this small never takes
            byte[] packet = Assert.Single(Packetize(track, new byte[] { 0x26 }));

            Assert.Equal(13, packet.Length);
            Assert.Equal(0x26, packet[12]);
        }

        #endregion
    }
}
