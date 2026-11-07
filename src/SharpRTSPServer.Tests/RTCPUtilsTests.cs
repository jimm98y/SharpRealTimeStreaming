// SharpRTSPServer
// Copyright (C) 2026 Lukas Volf
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// What 0.8.0 offered as SharpRTSPServer.RTCPUtils still writes what it wrote then, though the
    /// work is now SharpRTCP's.
    /// </summary>
    [TestClass]
#pragma warning disable CS0618 // what is obsolete is what is under test
    public class RTCPUtilsTests
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
        public void HeaderKeepsTheVersionPaddingAndCountItWasGiven()
        {
            Span<byte> rtcp = new byte[8];

            RTCPUtils.WriteRTCPHeader(rtcp, version: 1, hasPadding: true, count: 3,
                packetType: RTCPUtils.RTCP_PACKET_TYPE_BYE, length: 1, ssrc: 1);

            // as 0.8.0 wrote it: (version << 6) + (padding << 5) + count
            Assert.AreEqual((1 << 6) + (1 << 5) + 3, rtcp[0]);
            Assert.AreEqual(RTCPUtils.RTCP_PACKET_TYPE_BYE, rtcp[1]);
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

        [TestMethod]
        public void SenderReportLeavesTheHeaderAlone()
        {
            // the header was the caller's to write, before or after the sender information
            Span<byte> rtcp = new byte[28];
            RTCPUtils.WriteRTCPHeader(rtcp, RTCPUtils.RTCP_VERSION, hasPadding: false, count: 0,
                packetType: RTCPUtils.RTCP_PACKET_TYPE_SENDER_REPORT, length: 6, ssrc: 0xDEADBEEF);
            byte[] header = rtcp.Slice(0, 8).ToArray();

            RTCPUtils.WriteSenderReport(rtcp, DateTime.UtcNow, 1, 2, 3);

            CollectionAssert.AreEqual(header, rtcp.Slice(0, 8).ToArray());
        }
    }
#pragma warning restore CS0618
}
