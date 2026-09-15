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
    [TestClass]
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

        [TestMethod]
        public void H265SmallNalGoesIntoOnePacket()
        {
            var track = new H265Track();
            byte[] nal = Payload(100, 0x26); // NAL type 19 (IDR_W_RADL)

            byte[] packet = Assert.ContainsSingle(Packetize(track, nal));

            Assert.HasCount(12 + nal.Length, packet);
            Assert.AreEqual(Timestamp, ReadTimestamp(packet));
            Assert.IsTrue(IsMarkerSet(packet));
            CollectionAssert.AreEqual(nal, packet.Skip(12).ToArray());
        }

        [TestMethod]
        public void H265LargeNalIsFragmented()
        {
            var track = new H265Track { PacketMTU = 1400 };
            byte[] nal = Payload(5000, 0x26);

            var packets = Packetize(track, nal);

            Assert.IsGreaterThan(1, packets.Count);

            // H265 fragmentation unit: 2 byte payload header with type 49, then the FU header
            foreach (var packet in packets)
            {
                Assert.AreEqual(49, (packet[12] >> 1) & 0x3F);
                Assert.AreEqual(19, packet[14] & 0x3F); // original NAL type in the FU header
            }

            Assert.AreNotEqual(0, packets.First()[14] & 0x80, "start bit on the first fragment");
            Assert.AreNotEqual(0, packets.Last()[14] & 0x40, "end bit on the last fragment");
            Assert.IsTrue(IsMarkerSet(packets.Last()));
            foreach (var p in packets.Take(packets.Count - 1))
            {
                Assert.IsFalse(IsMarkerSet(p));
            }
        }

        [TestMethod]
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

            CollectionAssert.AreEqual(nal, reassembled.ToArray());
        }

        [TestMethod]
        public void H265SdpCarriesTheParameterSets()
        {
            var track = new H265Track(new byte[] { 0x40, 0x01 }, new byte[] { 0x42, 0x01 }, new byte[] { 0x44, 0x01 }) { ID = 0 };

            string sdp = track.BuildSDP(new StringBuilder()).ToString();

            StringAssert.Contains(sdp, "a=rtpmap:96 H265/90000");
            StringAssert.Contains(sdp, "sprop-vps=");
            StringAssert.Contains(sdp, "sprop-sps=");
            StringAssert.Contains(sdp, "sprop-pps=");
        }

        #endregion

        #region H266

        [TestMethod]
        public void H266SmallNalGoesIntoOnePacket()
        {
            var track = new H266Track();
            byte[] nal = Payload(100, 0x00);

            byte[] packet = Assert.ContainsSingle(Packetize(track, nal));

            Assert.HasCount(12 + nal.Length, packet);
            Assert.AreEqual(Timestamp, ReadTimestamp(packet));
            Assert.IsTrue(IsMarkerSet(packet));
            CollectionAssert.AreEqual(nal, packet.Skip(12).ToArray());
        }

        [TestMethod]
        public void H266LargeNalIsFragmented()
        {
            var track = new H266Track { PacketMTU = 1400 };

            var packets = Packetize(track, Payload(5000, 0x00));

            Assert.IsGreaterThan(1, packets.Count);
            Assert.IsTrue(IsMarkerSet(packets.Last()));
            foreach (var p in packets.Take(packets.Count - 1))
            {
                Assert.IsFalse(IsMarkerSet(p));
            }
        }

        #endregion

        #region AV1

        [TestMethod]
        public void AV1TemporalDelimiterAndTileListObusAreDropped()
        {
            var track = new AV1Track();

            // OBU type is bits 6..3 of the header: 4 is a temporal delimiter, 2 a tile list
            byte temporalDelimiter = (4 << 3);
            byte tileList = (2 << 3);

            Assert.IsEmpty(Packetize(track, new byte[] { temporalDelimiter, 0x00 }));
            Assert.IsEmpty(Packetize(track, new byte[] { tileList, 0x00 }));
        }

        [TestMethod]
        public void AV1FrameObuIsPacketized()
        {
            var track = new AV1Track();

            var packets = Packetize(track, FrameObu(100));

            Assert.IsNotEmpty(packets);
            foreach (var p in packets)
            {
                Assert.AreEqual(Timestamp, ReadTimestamp(p));
            }
        }

        /// <summary>An OBU_FRAME with no size field, followed by the given number of payload bytes.</summary>
        private static byte[] FrameObu(int payloadLength) =>
            new byte[] { (6 << 3) }.Concat(Payload(payloadLength)).ToArray();

        [TestMethod]
        public void AV1MarkerGoesOnTheLastObuThatIsActuallySent()
        {
            var track = new AV1Track();
            byte temporalDelimiter = (4 << 3);

            // the temporal delimiter is dropped, so the frame before it has to carry the marker -
            // taking the last sample outright left the frame unmarked and never completed
            var packets = Packetize(track, FrameObu(100), new byte[] { temporalDelimiter });

            Assert.IsNotEmpty(packets);
            Assert.IsTrue(IsMarkerSet(packets.Last()), "the last sent packet must close the frame");
        }

        [TestMethod]
        [DataRow(1)]
        [DataRow(40)]
        [DataRow(44)] // exactly the header overhead - the payload size lands on zero
        public void AV1PacketMtuTooSmallIsRejected(int packetMtu)
        {
            var track = new AV1Track { PacketMTU = packetMtu };

            // At 44 the payload size is zero, so the loop consumed nothing and allocated a packet
            // on every pass - it never terminated. Smaller values crashed out of the buffer pool.
            Assert.ThrowsExactly<InvalidOperationException>(() => Packetize(track, FrameObu(100)));
        }

        [TestMethod]
        public void AV1EmptySampleIsSkipped()
        {
            var track = new AV1Track();

            Assert.IsEmpty(Packetize(track, new byte[0]));
            Assert.IsNotEmpty(Packetize(track, new byte[0], FrameObu(50)));
        }

        [TestMethod]
        public void AV1ObuDeclaringASizeFieldItDoesNotHaveIsRejected()
        {
            var track = new AV1Track();

            // obu_has_size_field set, but nothing follows the header to read it from
            byte headerWithSizeField = (byte)((6 << 3) | 0x02);

            Assert.ThrowsExactly<ArgumentException>(() => Packetize(track, new byte[] { headerWithSizeField }));
        }

        [TestMethod]
        public void AV1SizeFieldIsStrippedFromThePayload()
        {
            var track = new AV1Track();
            byte headerWithSizeField = (byte)((6 << 3) | 0x02);

            // header, leb128 size of 3, then 3 payload bytes
            var packets = Packetize(track, new byte[] { headerWithSizeField, 0x03, 0xAA, 0xBB, 0xCC });

            byte[] packet = Assert.ContainsSingle(packets);

            // the size field is removed and the flag cleared, leaving header + 3 payload bytes
            Assert.HasCount(12 + 1 + 4, packet);
            Assert.AreEqual(0, packet[13] & 0x02);
            CollectionAssert.AreEqual(new byte[] { 0xAA, 0xBB, 0xCC }, packet.Skip(14).ToArray());
        }

        #endregion

        #region Audio

        public static IEnumerable<object[]> SimpleAudioTracks() => new[]
        {
            new object[] { new PCMUTrack(), 0 },   // static payload type 0
            new object[] { new PCMATrack(), 8 },   // static payload type 8
            new object[] { new OpusTrack(), 97 },  // dynamic, 96 + track ID 1
        };

        [TestMethod]
        [DynamicData(nameof(SimpleAudioTracks))]
        public void AudioFrameIsCopiedVerbatimAfterTheRtpHeader(ITrack track, int expectedPayloadType)
        {
            byte[] frame = Payload(160);

            byte[] packet = Assert.ContainsSingle(Packetize(track, frame));

            Assert.HasCount(12 + frame.Length, packet);
            Assert.AreEqual(expectedPayloadType, packet[1] & 0x7F);
            Assert.AreEqual(Timestamp, ReadTimestamp(packet));
            Assert.IsTrue(IsMarkerSet(packet), "audio packets are always the last of their timestamp");
            CollectionAssert.AreEqual(frame, packet.Skip(12).ToArray());
        }

        [TestMethod]
        public void EachAudioFrameBecomesItsOwnPacket()
        {
            var track = new PCMUTrack();

            var packets = Packetize(track, Payload(160), Payload(160), Payload(160));

            Assert.HasCount(3, packets);
            foreach (var p in packets)
            {
                Assert.AreEqual(Timestamp, ReadTimestamp(p));
            }
        }

        [TestMethod]
        [DataRow(typeof(PCMUTrack), 0, "a=rtpmap:0 PCMU/8000")]
        [DataRow(typeof(PCMATrack), 8, "a=rtpmap:8 PCMA/8000")]
        public void G711SdpAdvertisesTheStaticPayloadTypeAtEightKilohertz(Type trackType, int payloadType, string expectedRtpMap)
        {
            var track = (ITrack)Activator.CreateInstance(trackType);

            string sdp = track.BuildSDP(new StringBuilder()).ToString();

            StringAssert.Contains(sdp, $"m=audio 0 RTP/AVP {payloadType}");
            StringAssert.Contains(sdp, expectedRtpMap);
        }

        [TestMethod]
        public void OpusSdpAdvertisesFortyEightKilohertzStereo()
        {
            string sdp = new OpusTrack().BuildSDP(new StringBuilder()).ToString();

            StringAssert.Contains(sdp, "opus/48000/2");
        }

        #endregion

        #region Shared behaviour

        public static IEnumerable<object[]> FragmentingVideoTracks() => new[]
        {
            // 44 bytes is exactly the IP, UDP and RTP header overhead, leaving nothing for payload
            new object[] { new H264Track(new byte[] { 0x67 }, new byte[] { 0x68 }) { PacketMTU = 1 } },
            new object[] { new H264Track(new byte[] { 0x67 }, new byte[] { 0x68 }) { PacketMTU = 44 } },
            new object[] { new H265Track { PacketMTU = 1 } },
            new object[] { new H265Track { PacketMTU = 44 } },
            new object[] { new H266Track { PacketMTU = 1 } },
            new object[] { new H266Track { PacketMTU = 44 } },
        };

        [TestMethod]
        [DynamicData(nameof(FragmentingVideoTracks))]
        public void PacketMtuTooSmallToCarryPayloadIsRejected(ITrack track)
        {
            // With a payload size of zero the fragmentation loop consumes nothing and never ends;
            // with a negative one it crashes out of the buffer pool. Both are rejected up front.
            Assert.ThrowsExactly<InvalidOperationException>(() => Packetize(track, Payload(5000, 0x26)));
        }

        public static IEnumerable<object[]> NalTracks() => new[]
        {
            new object[] { new H264Track(new byte[] { 0x67 }, new byte[] { 0x68 }) { PacketMTU = 1400 } },
            new object[] { new H265Track { PacketMTU = 1400 } },
            new object[] { new H266Track { PacketMTU = 1400 } },
        };

        [TestMethod]
        [DynamicData(nameof(NalTracks))]
        public void ShortNalIsSentWholeRatherThanFragmented(ITrack track)
        {
            // H265 and H266 read a two byte NAL header, but only on the fragmenting path, which a
            // NAL this small never takes
            byte[] packet = Assert.ContainsSingle(Packetize(track, new byte[] { 0x26 }));

            Assert.HasCount(13, packet);
            Assert.AreEqual(0x26, packet[12]);
        }

        #endregion
    }
}
