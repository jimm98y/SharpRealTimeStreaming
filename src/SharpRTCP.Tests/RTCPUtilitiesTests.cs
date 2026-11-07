// SharpRTCP
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
using System.Buffers.Binary;
using System.Linq;

namespace SharpRTCP.Tests
{
    /// <summary>
    /// Reports read as they are written: report blocks, sender reports of compound packets, source descriptions and Picture
    /// Loss Indications; and RTCP told from the RTP it shares a transport with.
    /// </summary>
    [TestClass]
    public sealed class RTCPUtilitiesTests
    {
        private const uint Us = 0x11223344;
        private const uint Them = 0x55667788;

        private static byte[] SenderReport(uint ssrc, ulong ntp, uint rtpTimestamp, uint packets, uint octets)
        {
            byte[] packet = new byte[RTCPUtilities.GetSenderReportLength(0)];
            RTCPUtilities.WriteHeader(packet, 0, PacketType.SenderReport, packet.Length / 4 - 1, ssrc);
            BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(8), ntp);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), rtpTimestamp);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(20), packets);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(24), octets);
            return packet;
        }

        [TestMethod]
        public void HeaderEncodesVersionTypeLengthAndSsrc()
        {
            Span<byte> rtcp = new byte[28];

            RTCPUtilities.WriteHeader(rtcp, count: 0, packetType: PacketType.SenderReport, length: (28 / 4) - 1, ssrc: 0xDEADBEEF);

            Assert.AreEqual(0x80, rtcp[0]);
            Assert.AreEqual(PacketType.SenderReport, rtcp[1]);
            Assert.AreEqual(6, (rtcp[2] << 8) | rtcp[3]);
            Assert.AreEqual(0xDE, rtcp[4]);
            Assert.AreEqual(0xEF, rtcp[7]);
        }

        [TestMethod]
        public void SenderReportCarriesNtpTimeRtpTimeAndCounters()
        {
            var now = new DateTime(2024, 6, 7, 12, 0, 0, 250, DateTimeKind.Utc);
            var written = new SenderReport
            {
                SSRC = Them,
                NTPTimestamp = SharpRTCP.SenderReport.ToNTPTimestamp(now),
                RTPTimestamp = 0x11223344,
                PacketCount = 1000,
                OctetCount = 250000,
            };

            byte[] rtcp = RTCPUtilities.CreateSenderReport(written);

            Assert.HasCount(RTCPUtilities.GetSenderReportLength(0), rtcp);
            uint ntpSeconds = BinaryPrimitives.ReadUInt32BigEndian(rtcp.AsSpan(8));
            Assert.AreEqual((uint)(now - new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds, ntpSeconds);
            Assert.AreEqual(0x40000000u, BinaryPrimitives.ReadUInt32BigEndian(rtcp.AsSpan(12)), "a quarter of a second, of 2^32ths");

            Assert.IsTrue(RTCPUtilities.TryReadSenderReport(rtcp, out SenderReport read));
            Assert.AreEqual((Them, written.NTPTimestamp, 0x11223344u, 1000u, 250000u), (read.SSRC, read.NTPTimestamp, read.RTPTimestamp, read.PacketCount, read.OctetCount));
            Assert.AreEqual(now, read.NTPTime);
        }

        [TestMethod]
        public void ASenderReportCarriesItsBlocks()
        {
            var written = new SenderReport { SSRC = Them, PacketCount = 1 };
            written.Reports.Add(new ReportBlock { HasData = true, SSRC = Us, ExtendedHighestSequence = 7 });

            byte[] rtcp = RTCPUtilities.CreateSenderReport(written);

            Assert.HasCount(RTCPUtilities.GetSenderReportLength(1), rtcp);
            Assert.AreEqual(written.Reports.Single(), RTCPUtilities.ReadSenderReports(rtcp).Single().Reports.Single());
        }

        [TestMethod]
        public void AGoodbyeNamesWhoLeaves()
        {
            byte[] rtcp = RTCPUtilities.CreateGoodbye(Them);

            Assert.HasCount(RTCPUtilities.GoodbyeLength, rtcp);
            Assert.AreEqual(0x81, rtcp[0], "one source");
            Assert.AreEqual(PacketType.Goodbye, rtcp[1]);
            Assert.AreEqual(Them, BinaryPrimitives.ReadUInt32BigEndian(rtcp.AsSpan(4)));
        }

        [TestMethod]
        public void AReportBlockReadsAsItWasWritten()
        {
            var written = new ReportBlock
            {
                HasData = true,
                SSRC = Them,
                FractionLost = 85,
                CumulativeLost = -3,
                ExtendedHighestSequence = 65536 + 7,
                Jitter = 123,
                LastSenderReport = 0xAABBCCDD,
                DelaySinceLastSenderReport = 32768,
            };

            var receiverReport = new ReceiverReport { SSRC = Us };
            receiverReport.Reports.Add(written);
            byte[] report = RTCPUtilities.CreateReceiverReport(receiverReport);
            var (reporter, read) = RTCPUtilities.ReadReportBlocks(report).Single();

            Assert.AreEqual(Us, reporter);
            Assert.AreEqual(written, read, "a negative count of lost packets too: duplicates arrived");
        }

        [TestMethod]
        public void AReceiverReportReadsAsItWasWritten()
        {
            var written = new ReceiverReport { SSRC = Us };
            written.Reports.Add(new ReportBlock { HasData = true, SSRC = Them, FractionLost = 1, ExtendedHighestSequence = 100 });
            written.Reports.Add(new ReportBlock { HasData = true, SSRC = Them + 1, CumulativeLost = 5, Jitter = 9 });

            byte[] compound = RTCPUtilities.CreateCompoundPacket(RTCPUtilities.CreateReceiverReport(written), RTCPUtilities.CreateSourceDescription(Us, "receiver"));
            var read = RTCPUtilities.ReadReceiverReports(compound).Single();

            Assert.AreEqual(Us, read.SSRC);
            CollectionAssert.AreEqual(written.Reports, read.Reports);
            Assert.IsEmpty(RTCPUtilities.ReadSenderReports(compound), "a receiver report is no sender report");
        }

        [TestMethod]
        public void AReceiverReportOfNothingStillSaysWhoIsThere()
        {
            byte[] report = RTCPUtilities.CreateReceiverReport(new ReceiverReport { SSRC = Us });

            Assert.HasCount(8, report);
            Assert.AreEqual(0x80, report[0], "version 2, no blocks");
            Assert.AreEqual(PacketType.ReceiverReport, report[1]);
            Assert.AreEqual(Us, BinaryPrimitives.ReadUInt32BigEndian(report.AsSpan(4)));
        }

        [TestMethod]
        public void TheSenderReportsOfACompoundPacketAreRead()
        {
            ulong ntp = ((ulong)3976214400 << 32) | 0x80000000; // 2026-01-01 00:00:00.5 UTC
            byte[] compound = RTCPUtilities.CreateCompoundPacket(
                SenderReport(Them, ntp, 90000, 61, 79100),
                RTCPUtilities.CreateSourceDescription(Them, "camera"));

            var report = RTCPUtilities.ReadSenderReports(compound).Single();

            Assert.AreEqual((Them, ntp, 90000u, 61u, 79100u), (report.SSRC, report.NTPTimestamp, report.RTPTimestamp, report.PacketCount, report.OctetCount));
            Assert.AreEqual(new DateTime(2026, 1, 1, 0, 0, 0, 500, DateTimeKind.Utc), report.NTPTime);
            Assert.AreEqual(((3976214400u & 0xFFFF) << 16) | 0x8000, report.CompactNTP, "the middle 32 bits, which a report block echoes");
        }

        [TestMethod]
        public void TheBlocksOfASenderReportAreReadToo()
        {
            // a sender report of a block: its 20 bytes of sender information before it
            byte[] sender = SenderReport(Them, 0, 0, 1, 1);
            byte[] packet = new byte[sender.Length + RTCPUtilities.ReportBlockLength];
            sender.CopyTo(packet, 0);
            packet[0] |= 1;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)(packet.Length / 4 - 1));
            RTCPUtilities.WriteReportBlock(packet.AsSpan(sender.Length), new ReportBlock { SSRC = Us, ExtendedHighestSequence = 42 });

            var (reporter, block) = RTCPUtilities.ReadReportBlocks(packet).Single();
            var senderReport = RTCPUtilities.ReadSenderReports(packet).Single();

            Assert.AreEqual((Them, Us, 42u), (reporter, block.SSRC, block.ExtendedHighestSequence));
            Assert.AreEqual(block, senderReport.Reports.Single(), "of the sender report's own blocks too");
        }

        [TestMethod]
        public void ACompoundPacketIsReadUpToWhereItIsNotWellFormed()
        {
            byte[] report = RTCPUtilities.CreateReceiverReport(new ReceiverReport { SSRC = Us });
            // a second packet that says it runs past the end of the datagram
            byte[] broken = { 0x80, 200, 0x00, 0x10, 0, 0, 0, 0 };

            var packets = RTCPUtilities.SplitCompoundPacket(RTCPUtilities.CreateCompoundPacket(report, broken));

            Assert.AreEqual((0, 8), packets.Single());
        }

        [TestMethod]
        public void ASourceDescriptionCarriesTheCName()
        {
            byte[] sdes = RTCPUtilities.CreateSourceDescription(Us, "cname");

            // header 4, SSRC 4, the item's type and length 2, the name 5, the null that ends the items 1: 32 bits each
            Assert.HasCount(16, sdes);
            Assert.AreEqual(0x81, sdes[0], "one chunk");
            Assert.AreEqual(PacketType.SourceDescription, sdes[1]);
            Assert.AreEqual(3, BinaryPrimitives.ReadUInt16BigEndian(sdes.AsSpan(2)));
            Assert.AreEqual(Us, BinaryPrimitives.ReadUInt32BigEndian(sdes.AsSpan(4)));
            Assert.AreEqual(1, sdes[8], "CNAME");
            Assert.AreEqual(5, sdes[9]);
            Assert.AreEqual("cname", System.Text.Encoding.ASCII.GetString(sdes, 10, 5));
            Assert.AreEqual(0, sdes[15]);
        }

        [TestMethod]
        public void APictureLossIndicationNamesTheSource()
        {
            byte[] pli = RTCPUtilities.CreatePictureLossIndication(Us, Them);

            Assert.HasCount(12, pli);
            Assert.AreEqual(0x81, pli[0], "version 2, FMT 1");
            Assert.AreEqual(PacketType.PayloadFeedback, pli[1]);
            Assert.AreEqual(2, BinaryPrimitives.ReadUInt16BigEndian(pli.AsSpan(2)));
            Assert.AreEqual(Us, BinaryPrimitives.ReadUInt32BigEndian(pli.AsSpan(4)));
            Assert.AreEqual(Them, BinaryPrimitives.ReadUInt32BigEndian(pli.AsSpan(8)));
        }

        [TestMethod]
        public void RtcpIsToldFromTheRtpItIsMultiplexedWith()
        {
            Assert.IsFalse(RTCPUtilities.IsRTCP(new byte[] { 0x80, 96, 0, 1, 0, 0, 0, 0 }));
            Assert.IsFalse(RTCPUtilities.IsRTCP(new byte[] { 0x80, 0x80 | 96, 0, 1, 0, 0, 0, 0 }), "a marker set");
            Assert.IsTrue(RTCPUtilities.IsRTCP(RTCPUtilities.CreateReceiverReport(new ReceiverReport { SSRC = Us })));
        }
    }
}
