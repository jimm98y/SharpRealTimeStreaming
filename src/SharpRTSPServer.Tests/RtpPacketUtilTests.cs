using System;
using SharpRTSPServer;

namespace SharpRTSPServer.Tests
{
    public class RtpPacketUtilTests
    {
        [Fact]
        public void HeaderEncodesVersionFlagsAndPayloadType()
        {
            Span<byte> packet = new byte[12];

            RTPPacketUtil.WriteHeader(packet, RTPPacketUtil.RTP_VERSION,
                rtpPadding: true, rtpExtension: true, rtpCsrcCount: 3, rtpMarker: true, rtpPayloadType: 96);

            Assert.Equal(2, packet[0] >> 6);            // version
            Assert.Equal(1, (packet[0] >> 5) & 0x01);   // padding
            Assert.Equal(1, (packet[0] >> 4) & 0x01);   // extension
            Assert.Equal(3, packet[0] & 0x0F);          // CSRC count
            Assert.Equal(1, packet[1] >> 7);            // marker
            Assert.Equal(96, packet[1] & 0x7F);         // payload type
        }

        [Fact]
        public void HeaderWithoutFlagsLeavesThemClear()
        {
            Span<byte> packet = new byte[12];

            RTPPacketUtil.WriteHeader(packet, RTPPacketUtil.RTP_VERSION,
                rtpPadding: false, rtpExtension: false, rtpCsrcCount: 0, rtpMarker: false, rtpPayloadType: 26);

            Assert.Equal(0x80, packet[0]);
            Assert.Equal(26, packet[1]);
        }

        [Theory]
        [InlineData((ushort)0)]
        [InlineData((ushort)1)]
        [InlineData((ushort)12345)]
        [InlineData(ushort.MaxValue)]
        public void SequenceNumberIsWrittenBigEndian(ushort sequenceNumber)
        {
            Span<byte> packet = new byte[12];

            RTPPacketUtil.WriteSequenceNumber(packet, sequenceNumber);

            Assert.Equal((byte)(sequenceNumber >> 8), packet[2]);
            Assert.Equal((byte)(sequenceNumber & 0xFF), packet[3]);
        }

        [Theory]
        [InlineData(0u)]
        [InlineData(1u)]
        [InlineData(0x11223344u)]
        [InlineData(uint.MaxValue)]
        public void TimestampRoundTrips(uint timestamp)
        {
            byte[] packet = new byte[12];

            RTPPacketUtil.WriteTS(packet, timestamp);

            Assert.Equal(timestamp, RTPPacketUtil.ReadTS(packet));
        }

        [Theory]
        [InlineData(0u)]
        [InlineData(0xDEADBEEFu)]
        [InlineData(uint.MaxValue)]
        public void SsrcIsWrittenBigEndian(uint ssrc)
        {
            Span<byte> packet = new byte[12];

            RTPPacketUtil.WriteSSRC(packet, ssrc);

            Assert.Equal((byte)(ssrc >> 24), packet[8]);
            Assert.Equal((byte)(ssrc >> 16), packet[9]);
            Assert.Equal((byte)(ssrc >> 8), packet[10]);
            Assert.Equal((byte)ssrc, packet[11]);
        }

        [Fact]
        public void WritingTheSsrcDoesNotDisturbTheTimestamp()
        {
            byte[] packet = new byte[12];

            RTPPacketUtil.WriteTS(packet, 0x11223344);
            RTPPacketUtil.WriteSSRC(packet, 0xAABBCCDD);
            RTPPacketUtil.WriteSequenceNumber(packet, 4321);

            Assert.Equal(0x11223344u, RTPPacketUtil.ReadTS(packet));
        }
    }

    public class RtcpUtilsTests
    {
        [Fact]
        public void HeaderEncodesVersionTypeLengthAndSsrc()
        {
            Span<byte> rtcp = new byte[28];

            RTCPUtils.WriteRTCPHeader(rtcp, RTCPUtils.RTCP_VERSION, hasPadding: false, count: 0,
                packetType: RTCPUtils.RTCP_PACKET_TYPE_SENDER_REPORT, length: (28 / 4) - 1, ssrc: 0xDEADBEEF);

            Assert.Equal(0x80, rtcp[0]);
            Assert.Equal(RTCPUtils.RTCP_PACKET_TYPE_SENDER_REPORT, rtcp[1]);
            Assert.Equal(6, (rtcp[2] << 8) | rtcp[3]);
            Assert.Equal(0xDE, rtcp[4]);
            Assert.Equal(0xEF, rtcp[7]);
        }

        [Fact]
        public void SenderReportCarriesNtpTimeRtpTimeAndCounters()
        {
            Span<byte> rtcp = new byte[28];
            var now = new DateTime(2024, 6, 7, 12, 0, 0, DateTimeKind.Utc);

            RTCPUtils.WriteSenderReport(rtcp, now, rtp_timestamp: 0x11223344, rtpPacketCount: 1000, octetCount: 250000);

            uint ntpSeconds = (uint)((rtcp[8] << 24) | (rtcp[9] << 16) | (rtcp[10] << 8) | rtcp[11]);
            uint expected = (uint)(now - new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            Assert.Equal(expected, ntpSeconds);

            Assert.Equal(0x11223344u, (uint)((rtcp[16] << 24) | (rtcp[17] << 16) | (rtcp[18] << 8) | rtcp[19]));
            Assert.Equal(1000u, (uint)((rtcp[20] << 24) | (rtcp[21] << 16) | (rtcp[22] << 8) | rtcp[23]));
            Assert.Equal(250000u, (uint)((rtcp[24] << 24) | (rtcp[25] << 16) | (rtcp[26] << 8) | rtcp[27]));
        }
    }
}
