// SharpRTSPClient
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

namespace SharpRTSPClient.Tests
{
    /// <summary>
    /// What a receiver report actually says.
    /// </summary>
    /// <remarks>
    /// It used to say nothing: the right shape, the right length, and a report count of zero, so it
    /// named no source and carried no numbers. A sender receiving one learned that somebody was
    /// listening and not one thing more - not what share of its packets went missing, not how
    /// unevenly they were turning up, not how long the round trip was, which is everything a sender
    /// would act on.
    /// </remarks>
    [TestClass]
    public class ReceiverReportContentTests
    {
        private const uint Us = 0x11223344;
        private const uint Them = 0x55667788;

        private const int ClockRate = 90000;

        private static RtcpChannelState ChannelThatReceived(params ushort[] sequenceNumbers)
        {
            var channel = new RtcpChannelState { ClockRate = ClockRate };

            uint timestamp = 0;

            foreach (ushort sequenceNumber in sequenceNumbers)
            {
                channel.Reception.RecordPacket(sequenceNumber, timestamp += 3000, ClockRate);
            }

            return channel;
        }

        private static byte[] ReportFor(RtcpChannelState channel)
        {
            using var client = new RTSPClient();
            return client.BuildRtcpReceiverReport(Us, Them, channel);
        }

        private static uint ReadUInt32(byte[] report, int at) =>
            (uint)((report[at] << 24) | (report[at + 1] << 16) | (report[at + 2] << 8) | report[at + 3]);

        private static int ReadLost(byte[] report)
        {
            int raw = (report[13] << 16) | (report[14] << 8) | report[15];

            // twenty four bits, signed
            return raw >= 0x800000 ? raw - 0x1000000 : raw;
        }

        [TestMethod]
        public void AReportNamesTheSourceItIsAbout()
        {
            byte[] report = ReportFor(ChannelThatReceived(1, 2, 3, 4, 5));

            Assert.AreEqual(1, report[0] & 0x1F, "it should carry one report block");
            Assert.AreEqual(201, report[1], "and be a receiver report");
            Assert.HasCount(32, report, "a header, a sender, and one block of twenty four bytes");

            Assert.AreEqual(Us, ReadUInt32(report, 4), "the report is from us");
            Assert.AreEqual(Them, ReadUInt32(report, 8), "and about them");
        }

        [TestMethod]
        public void NothingLostIsReportedAsNothingLost()
        {
            byte[] report = ReportFor(ChannelThatReceived(100, 101, 102, 103, 104));

            Assert.AreEqual(0, report[12], "no packets went missing, so no fraction of them did");
            Assert.AreEqual(0, ReadLost(report));
            Assert.AreEqual(104u, ReadUInt32(report, 16), "the highest sequence number reached");
        }

        [TestMethod]
        public void PacketsThatDidNotArriveAreReported()
        {
            // ten sent, five arrived
            byte[] report = ReportFor(ChannelThatReceived(1, 3, 5, 7, 9));

            Assert.AreEqual(4, ReadLost(report), "four of the nine expected did not arrive");

            // four of nine, as a fraction out of 256
            Assert.AreEqual((4 << 8) / 9, report[12], "and the fraction should say the same");
        }

        [TestMethod]
        public void TheFractionIsSinceTheLastReportRatherThanOverall()
        {
            var channel = ChannelThatReceived(1, 3, 5, 7, 9);

            ReportFor(channel); // the losses so far are now behind us

            channel.Reception.RecordPacket(10, 30000, ClockRate);
            channel.Reception.RecordPacket(11, 33000, ClockRate);

            byte[] second = ReportFor(channel);

            Assert.AreEqual(0, second[12],
                "nothing was lost since the last report, whatever was lost before it");

            // but what was lost altogether has not been forgotten
            Assert.AreEqual(4, ReadLost(second));
        }

        [TestMethod]
        public void WrappingTheSequenceNumberDoesNotLookLikeMassiveLoss()
        {
            // The sequence number is sixteen bits and a stream of any length goes past it. Reading
            // the wrap as a jump backwards would report sixty five thousand packets lost at once.
            var channel = ChannelThatReceived(65533, 65534, 65535, 0, 1, 2);

            byte[] report = ReportFor(channel);

            Assert.AreEqual(0, ReadLost(report), "nothing was lost, the numbering merely wrapped");
            Assert.AreEqual(65536u + 2u, ReadUInt32(report, 16),
                "the sequence carries on past the wrap rather than starting again");
        }

        [TestMethod]
        public void DuplicatesAreReportedRatherThanHidden()
        {
            var channel = ChannelThatReceived(1, 2, 2, 3);

            byte[] report = ReportFor(channel);

            // Three expected, four received. The specification says to report the negative rather
            // than clamp it, because it is how a sender learns its packets are being duplicated.
            Assert.AreEqual(-1, ReadLost(report));
        }

        [TestMethod]
        public void UnevenArrivalIsReportedAsJitter()
        {
            var channel = new RtcpChannelState { ClockRate = ClockRate };

            // timestamps spaced evenly, arriving whenever this loop gets to them, which is not evenly
            for (ushort i = 1; i <= 20; i++)
            {
                channel.Reception.RecordPacket(i, (uint)(i * 3000), ClockRate);
                System.Threading.Thread.Sleep(i % 3 == 0 ? 12 : 1);
            }

            byte[] report = ReportFor(channel);

            Assert.IsGreaterThan(0u, ReadUInt32(report, 20),
                "packets arriving at uneven spacing should be reported as jitter");
        }

        [TestMethod]
        public void TheSenderIsToldWhichReportIsBeingAnsweredAndHowLongItWaited()
        {
            var channel = ChannelThatReceived(1, 2, 3);

            const uint middle32 = 0xAABBCCDD;
            channel.Reception.RecordSenderReportArrival(middle32);

            System.Threading.Thread.Sleep(120);

            byte[] report = ReportFor(channel);

            Assert.AreEqual(middle32, ReadUInt32(report, 24), "which report is being answered");

            // in sixty five thousandths of a second, so a tenth of a second is about 6553
            uint delay = ReadUInt32(report, 28);

            Assert.IsGreaterThan(0u, delay, "and how long it sat here, which is half of the round trip");
            Assert.IsLessThan(65536u * 5, delay, "a plausible delay, not a wild one: " + delay);
        }

        [TestMethod]
        public void AChannelThatHasReceivedNothingStillReportsThatItIsThere()
        {
            var channel = new RtcpChannelState { ClockRate = ClockRate };

            byte[] report = ReportFor(channel);

            Assert.HasCount(8, report, "nothing to say about anyone, so no report block");
            Assert.AreEqual(0, report[0] & 0x1F);
            Assert.AreEqual(Us, ReadUInt32(report, 4), "but it still says who is still listening");
        }

        [TestMethod]
        public void ReconnectingStartsTheCountingAgain()
        {
            var channel = ChannelThatReceived(1, 3, 5);

            channel.Reset();

            // a new stream numbers its packets from wherever it likes, so what was lost on the old
            // one says nothing about the new one
            channel.Reception.RecordPacket(9000, 3000, ClockRate);
            channel.Reception.RecordPacket(9001, 6000, ClockRate);

            byte[] report = ReportFor(channel);

            Assert.AreEqual(0, ReadLost(report));
            Assert.AreEqual(9001u, ReadUInt32(report, 16));
        }
    }
}
