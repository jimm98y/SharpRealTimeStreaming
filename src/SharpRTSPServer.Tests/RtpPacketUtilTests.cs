using System;
using SharpRTSPServer;

namespace SharpRTSPServer.Tests
{
    [TestClass]
    public class RtpPacketUtilTests
    {
        [TestMethod]
        public void HeaderEncodesVersionFlagsAndPayloadType()
        {
            Span<byte> packet = new byte[12];

            RTPPacketUtil.WriteHeader(packet, RTPPacketUtil.RTP_VERSION,
                rtpPadding: true, rtpExtension: true, rtpCsrcCount: 3, rtpMarker: true, rtpPayloadType: 96);

            Assert.AreEqual(2, packet[0] >> 6);            // version
            Assert.AreEqual(1, (packet[0] >> 5) & 0x01);   // padding
            Assert.AreEqual(1, (packet[0] >> 4) & 0x01);   // extension
            Assert.AreEqual(3, packet[0] & 0x0F);          // CSRC count
            Assert.AreEqual(1, packet[1] >> 7);            // marker
            Assert.AreEqual(96, packet[1] & 0x7F);         // payload type
        }

        [TestMethod]
        public void HeaderWithoutFlagsLeavesThemClear()
        {
            Span<byte> packet = new byte[12];

            RTPPacketUtil.WriteHeader(packet, RTPPacketUtil.RTP_VERSION,
                rtpPadding: false, rtpExtension: false, rtpCsrcCount: 0, rtpMarker: false, rtpPayloadType: 26);

            Assert.AreEqual(0x80, packet[0]);
            Assert.AreEqual(26, packet[1]);
        }

        [TestMethod]
        [DataRow((ushort)0)]
        [DataRow((ushort)1)]
        [DataRow((ushort)12345)]
        [DataRow(ushort.MaxValue)]
        public void SequenceNumberIsWrittenBigEndian(ushort sequenceNumber)
        {
            Span<byte> packet = new byte[12];

            RTPPacketUtil.WriteSequenceNumber(packet, sequenceNumber);

            Assert.AreEqual((byte)(sequenceNumber >> 8), packet[2]);
            Assert.AreEqual((byte)(sequenceNumber & 0xFF), packet[3]);
        }

        [TestMethod]
        [DataRow(0u)]
        [DataRow(1u)]
        [DataRow(0x11223344u)]
        [DataRow(uint.MaxValue)]
        public void TimestampRoundTrips(uint timestamp)
        {
            byte[] packet = new byte[12];

            RTPPacketUtil.WriteTS(packet, timestamp);

            Assert.AreEqual(timestamp, RTPPacketUtil.ReadTS(packet));
        }

        [TestMethod]
        [DataRow(0u)]
        [DataRow(0xDEADBEEFu)]
        [DataRow(uint.MaxValue)]
        public void SsrcIsWrittenBigEndian(uint ssrc)
        {
            Span<byte> packet = new byte[12];

            RTPPacketUtil.WriteSSRC(packet, ssrc);

            Assert.AreEqual((byte)(ssrc >> 24), packet[8]);
            Assert.AreEqual((byte)(ssrc >> 16), packet[9]);
            Assert.AreEqual((byte)(ssrc >> 8), packet[10]);
            Assert.AreEqual((byte)ssrc, packet[11]);
        }

        [TestMethod]
        public void WritingTheSsrcDoesNotDisturbTheTimestamp()
        {
            byte[] packet = new byte[12];

            RTPPacketUtil.WriteTS(packet, 0x11223344);
            RTPPacketUtil.WriteSSRC(packet, 0xAABBCCDD);
            RTPPacketUtil.WriteSequenceNumber(packet, 4321);

            Assert.AreEqual(0x11223344u, RTPPacketUtil.ReadTS(packet));
        }
    }

    [TestClass]

    public class RtcpUtilsTests
    {
        [TestMethod]
        public void HeaderEncodesVersionTypeLengthAndSsrc()
        {
            Span<byte> rtcp = new byte[28];

            RTCPUtils.WriteRTCPHeader(rtcp, RTCPUtils.RTCP_VERSION, hasPadding: false, count: 0,
                packetType: RTCPUtils.RTCP_PACKET_TYPE_SENDER_REPORT, length: (28 / 4) - 1, ssrc: 0xDEADBEEF);

            Assert.AreEqual(0x80, rtcp[0]);
            Assert.AreEqual(RTCPUtils.RTCP_PACKET_TYPE_SENDER_REPORT, rtcp[1]);
            Assert.AreEqual(6, (rtcp[2] << 8) | rtcp[3]);
            Assert.AreEqual(0xDE, rtcp[4]);
            Assert.AreEqual(0xEF, rtcp[7]);
        }

        [TestMethod]
        public void SenderReportCarriesNtpTimeRtpTimeAndCounters()
        {
            Span<byte> rtcp = new byte[28];
            var now = new DateTime(2024, 6, 7, 12, 0, 0, DateTimeKind.Utc);

            RTCPUtils.WriteSenderReport(rtcp, now, rtp_timestamp: 0x11223344, rtpPacketCount: 1000, octetCount: 250000);

            uint ntpSeconds = (uint)((rtcp[8] << 24) | (rtcp[9] << 16) | (rtcp[10] << 8) | rtcp[11]);
            uint expected = (uint)(now - new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            Assert.AreEqual(expected, ntpSeconds);

            Assert.AreEqual(0x11223344u, (uint)((rtcp[16] << 24) | (rtcp[17] << 16) | (rtcp[18] << 8) | rtcp[19]));
            Assert.AreEqual(1000u, (uint)((rtcp[20] << 24) | (rtcp[21] << 16) | (rtcp[22] << 8) | rtcp[23]));
            Assert.AreEqual(250000u, (uint)((rtcp[24] << 24) | (rtcp[25] << 16) | (rtcp[26] << 8) | rtcp[27]));
        }
    }
}
