using System;
using System.Collections.Generic;
using System.Linq;
using SharpRTSPServer;

namespace SharpRTSPServer.Tests
{
    [TestClass]
    public class MJpegTrackTests
    {
        private const byte Sof0 = 0xC0;
        private const byte Dqt = 0xDB;
        private const byte Dri = 0xDD;
        private const byte Sos = 0xDA;

        /// <summary>4:2:2 chroma subsampling, the sampling factor of the first component.</summary>
        private const byte Yuv422 = 0x21;

        /// <summary>4:2:0 chroma subsampling.</summary>
        private const byte Yuv420 = 0x22;

        /// <summary>
        /// Builds the smallest JPEG the packetizer will accept: SOI, optional segments, SOF0 and EOI.
        /// </summary>
        private static byte[] Jpeg(
            int width = 640,
            int height = 480,
            byte subsampling = Yuv422,
            int quantizationTables = 2,
            bool withRestartInterval = false)
        {
            var bytes = new List<byte> { 0xFF, 0xD8 }; // SOI

            for (int i = 0; i < quantizationTables; i++)
            {
                bytes.AddRange(new byte[] { 0xFF, Dqt });
                byte[] table = Enumerable.Range(0, 64).Select(v => (byte)(v + i)).ToArray();
                int length = table.Length + 2;
                bytes.Add((byte)(length >> 8));
                bytes.Add((byte)length);
                bytes.AddRange(table);
            }

            if (withRestartInterval)
            {
                bytes.AddRange(new byte[] { 0xFF, Dri, 0x00, 0x04, 0x00, 0x08 });
            }

            // SOF0: length, bpp, height, width, component count, then 3 bytes per component
            bytes.AddRange(new byte[] { 0xFF, Sof0 });
            bytes.AddRange(new byte[] { 0x00, 0x11 }); // 17 bytes for 3 components
            bytes.Add(8);                              // bits per pixel
            bytes.Add((byte)(height >> 8));
            bytes.Add((byte)height);
            bytes.Add((byte)(width >> 8));
            bytes.Add((byte)width);
            bytes.Add(3);                              // component count
            bytes.AddRange(new byte[] { 1, subsampling, 0 });
            bytes.AddRange(new byte[] { 2, 0x11, 1 });
            bytes.AddRange(new byte[] { 3, 0x11, 1 });

            // scan data, then EOI
            bytes.AddRange(new byte[] { 0xFF, Sos, 0x00, 0x08, 1, 1, 0, 0, 63, 0 });
            bytes.AddRange(Enumerable.Repeat((byte)0x5A, 200));
            bytes.AddRange(new byte[] { 0xFF, 0xD9 }); // EOI

            return bytes.ToArray();
        }

        private static List<byte[]> Packetize(MJpegTrack track, byte[] jpeg)
        {
            var samples = new List<ReadOnlyMemory<byte>> { jpeg };
            var (packets, owners) = track.CreateRtpPackets(samples, 90000);

            var copies = packets.Select(p => p.ToArray()).ToList();
            foreach (var owner in owners)
            {
                owner.Dispose();
            }
            return copies;
        }

        [TestMethod]
        public void DimensionsAndBitDepthAreReadFromTheFrameHeader()
        {
            var info = MJpegTrack.ParseJpeg(Jpeg(width: 640, height: 480), out _, out _, out _);

            Assert.AreEqual(640, info.width);
            Assert.AreEqual(480, info.height);
            Assert.AreEqual(8, info.bpp);
        }

        [TestMethod]
        [DataRow(Yuv422, (byte)0)] // RFC 2435 type 0
        [DataRow(Yuv420, (byte)1)] // RFC 2435 type 1
        public void ChromaSubsamplingSelectsTheRtpType(byte subsampling, byte expectedType)
        {
            var info = MJpegTrack.ParseJpeg(Jpeg(subsampling: subsampling), out _, out _, out _);

            Assert.AreEqual(expectedType, info.type);
        }

        [TestMethod]
        public void ARestartIntervalShiftsTheTypeBy64()
        {
            var info = MJpegTrack.ParseJpeg(Jpeg(subsampling: Yuv422, withRestartInterval: true), out _, out _, out _);

            Assert.AreEqual(64, info.type);
        }

        [TestMethod]
        public void BothQuantizationTablesAreReturned()
        {
            MJpegTrack.ParseJpeg(Jpeg(quantizationTables: 2), out var first, out var second, out _);

            Assert.AreEqual(64, first.Length);
            Assert.AreEqual(64, second.Length);
            CollectionAssert.AreNotEqual(first.ToArray(), second.ToArray());
        }

        [TestMethod]
        public void ASingleQuantizationTableLeavesTheSecondEmpty()
        {
            MJpegTrack.ParseJpeg(Jpeg(quantizationTables: 1), out var first, out var second, out _);

            Assert.AreEqual(64, first.Length);
            Assert.IsTrue(second.IsEmpty);
        }

        [TestMethod]
        public void MoreThanTwoQuantizationTablesIsRejected()
        {
            Assert.ThrowsExactly<NotSupportedException>(
                () => MJpegTrack.ParseJpeg(Jpeg(quantizationTables: 3), out _, out _, out _));
        }

        [TestMethod]
        [DataRow(2048, 480)]
        [DataRow(640, 2048)]
        public void ImagesLargerThanRtpAllowsAreRejected(int width, int height)
        {
            // RFC 2435 caps the dimensions at 2040 x 2040
            Assert.ThrowsExactly<NotSupportedException>(
                () => MJpegTrack.ParseJpeg(Jpeg(width: width, height: height), out _, out _, out _));
        }

        [TestMethod]
        [DataRow((byte)0x11)] // 4:4:4
        [DataRow((byte)0x12)]
        [DataRow((byte)0x44)]
        public void UnsupportedChromaSubsamplingIsRejected(byte subsampling)
        {
            Assert.ThrowsExactly<NotSupportedException>(
                () => MJpegTrack.ParseJpeg(Jpeg(subsampling: subsampling), out _, out _, out _));
        }

        [TestMethod]
        public void DataThatIsNotAJpegIsRejected()
        {
            byte[] notJpeg = { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 };

            Assert.ThrowsExactly<ArgumentException>(
                () => MJpegTrack.ParseJpeg(notJpeg, out _, out _, out _));
        }

        [TestMethod]
        public void FrameWithoutTheStartOfImageMarkerIsRejected()
        {
            var track = new MJpegTrack();
            byte[] jpeg = Jpeg();
            jpeg[0] = 0x00; // break the SOI

            Assert.ThrowsExactly<InvalidOperationException>(() => Packetize(track, jpeg));
        }

        [TestMethod]
        public void FrameWithoutTheEndOfImageMarkerIsRejected()
        {
            var track = new MJpegTrack();
            byte[] jpeg = Jpeg();
            jpeg[jpeg.Length - 1] = 0x00; // break the EOI

            Assert.ThrowsExactly<InvalidOperationException>(() => Packetize(track, jpeg));
        }

        [TestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(3)]
        public void FrameTooShortToHoldTheMarkersIsRejected(int length)
        {
            var track = new MJpegTrack();

            // used to read past the end of the buffer reaching for the SOI and EOI
            Assert.ThrowsExactly<InvalidOperationException>(() => Packetize(track, new byte[length]));
        }

        [TestMethod]
        public void PacketsCarryTheJpegHeaderAndTheFrameTimestamp()
        {
            var track = new MJpegTrack();

            byte[] packet = Packetize(track, Jpeg(width: 640, height: 480)).First();

            // payload type 26 is JPEG
            Assert.AreEqual(26, packet[1] & 0x7F);
            Assert.AreEqual(90000u, (uint)((packet[4] << 24) | (packet[5] << 16) | (packet[6] << 8) | packet[7]));

            // RFC 2435 main header: offset, type, Q, then width and height in 8 pixel units
            int fragmentOffset = (packet[13] << 16) | (packet[14] << 8) | packet[15];
            Assert.AreEqual(0, fragmentOffset);
            Assert.AreEqual(640 / 8, packet[18]);
            Assert.AreEqual(480 / 8, packet[19]);
        }

        [TestMethod]
        public void OnlyTheLastPacketOfAFrameSetsTheMarkerBit()
        {
            var track = new MJpegTrack();

            var packets = Packetize(track, Jpeg());

            foreach (var p in packets.Take(packets.Count - 1))
            {
                Assert.AreEqual(0, p[1] & 0x80);
            }
            Assert.AreNotEqual(0, packets.Last()[1] & 0x80);
        }

        [TestMethod]
        public void OnlyOneSampleAtATimeIsSupported()
        {
            var track = new MJpegTrack();
            var samples = new List<ReadOnlyMemory<byte>> { Jpeg(), Jpeg() };

            Assert.ThrowsExactly<InvalidOperationException>(() => track.CreateRtpPackets(samples, 0));
        }

        [TestMethod]
        public void SdpAdvertisesTheStaticJpegPayloadType()
        {
            var track = new MJpegTrack { ID = 0 };

            string sdp = track.BuildSDP(new System.Text.StringBuilder()).ToString();

            StringAssert.Contains(sdp, "m=video 0 RTP/AVP 26");
            StringAssert.Contains(sdp, "a=control:trackID=0");
        }
    }
}
