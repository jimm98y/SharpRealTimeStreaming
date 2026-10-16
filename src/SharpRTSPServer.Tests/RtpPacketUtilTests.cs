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
    [TestClass]
    public class RtpPacketUtilTests
    {
        [TestMethod]
        public void HeaderEncodesVersionFlagsAndPayloadType()
        {
            Span<byte> packet = new byte[12];

            RtpPacketUtil.WriteHeader(packet, RtpPacketUtil.RTP_VERSION,
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

            RtpPacketUtil.WriteHeader(packet, RtpPacketUtil.RTP_VERSION,
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

            RtpPacketUtil.WriteSequenceNumber(packet, sequenceNumber);

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

            RtpPacketUtil.WriteTimestamp(packet, timestamp);

            Assert.AreEqual(timestamp, RtpPacketUtil.ReadTimestamp(packet));
        }

        [TestMethod]
        [DataRow(0u)]
        [DataRow(0xDEADBEEFu)]
        [DataRow(uint.MaxValue)]
        public void SsrcIsWrittenBigEndian(uint ssrc)
        {
            Span<byte> packet = new byte[12];

            RtpPacketUtil.WriteSsrc(packet, ssrc);

            Assert.AreEqual((byte)(ssrc >> 24), packet[8]);
            Assert.AreEqual((byte)(ssrc >> 16), packet[9]);
            Assert.AreEqual((byte)(ssrc >> 8), packet[10]);
            Assert.AreEqual((byte)ssrc, packet[11]);
        }

        [TestMethod]
        public void WritingTheSsrcDoesNotDisturbTheTimestamp()
        {
            byte[] packet = new byte[12];

            RtpPacketUtil.WriteTimestamp(packet, 0x11223344);
            RtpPacketUtil.WriteSsrc(packet, 0xAABBCCDD);
            RtpPacketUtil.WriteSequenceNumber(packet, 4321);

            Assert.AreEqual(0x11223344u, RtpPacketUtil.ReadTimestamp(packet));
        }
    }
}
