using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SharpRTSPClient;

namespace SharpRTSPClient.Tests
{
    /// <summary>
    /// The client answers sender reports on an interval, rather than one for one.
    /// </summary>
    /// <remarks>
    /// A report used to go back for every one that arrived, so the client reported at whatever rate
    /// the far end chose - and a server that sends one before every frame was answered before every
    /// frame. RFC 3550 has RTCP at a few per cent of what a session carries, from both ends.
    /// </remarks>
    [TestClass]
    public sealed class ReceiverReportRateTests
    {
        private const uint OurSsrc = 0x11223344;
        private const uint ServerSsrc = 0x55667788;
        private const byte SenderReport = 200;

        /// <summary>A sender report long enough to be acted on.</summary>
        private static byte[] SenderReportPacket(uint ssrc)
        {
            var packet = new byte[28];
            packet[0] = 0x80;
            packet[1] = SenderReport;
            packet[2] = 0x00;
            packet[3] = 0x06; // (28 / 4) - 1
            packet[4] = (byte)(ssrc >> 24);
            packet[5] = (byte)(ssrc >> 16);
            packet[6] = (byte)(ssrc >> 8);
            packet[7] = (byte)ssrc;
            return packet;
        }

        private static int ReportsFor(RTSPClient client, RtcpChannelState channel, int senderReports)
        {
            int total = 0;
            for (int i = 0; i < senderReports; i++)
            {
                total += client.ParseRTCPAndGenerateResponse(
                    new Memory<byte>(SenderReportPacket(ServerSsrc)), OurSsrc, channel).Count;
            }
            return total;
        }

        [TestMethod]
        public void ABurstOfSenderReportsIsAnsweredOnce()
        {
            using var client = new RTSPClient { ReceiverReportInterval = TimeSpan.FromMinutes(5) };
            var channel = new RtcpChannelState();

            // thirty reports in a row, as a server reporting per frame would send
            Assert.AreEqual(1, ReportsFor(client, channel, 30),
                "one answer, not one per report");
        }

        [TestMethod]
        public void TheFirstSenderReportIsAnsweredStraightAway()
        {
            using var client = new RTSPClient { ReceiverReportInterval = TimeSpan.FromMinutes(5) };
            var channel = new RtcpChannelState();

            // the far end is waiting to hear that we are here, so the first one is not held back
            Assert.AreEqual(1, ReportsFor(client, channel, 1));
        }

        [TestMethod]
        public void TheIntervalComesRoundAgain()
        {
            using var client = new RTSPClient { ReceiverReportInterval = TimeSpan.FromMilliseconds(100) };
            var channel = new RtcpChannelState();

            Assert.AreEqual(1, ReportsFor(client, channel, 5));
            Thread.Sleep(200);
            Assert.AreEqual(1, ReportsFor(client, channel, 5), "the next interval should be answered too");
        }

        [TestMethod]
        public void AnIntervalOfZeroAnswersEveryReport()
        {
            using var client = new RTSPClient { ReceiverReportInterval = TimeSpan.Zero };
            var channel = new RtcpChannelState();

            Assert.AreEqual(5, ReportsFor(client, channel, 5));
        }

        [TestMethod]
        public void AReconnectStartsTheIntervalAgain()
        {
            using var client = new RTSPClient { ReceiverReportInterval = TimeSpan.FromMinutes(5) };
            var channel = new RtcpChannelState();

            Assert.AreEqual(1, ReportsFor(client, channel, 5));

            // a reconnect is a new stream, and the far end has not heard from us on it yet
            channel.Reset();

            Assert.AreEqual(1, ReportsFor(client, channel, 5));
        }
    }
}
