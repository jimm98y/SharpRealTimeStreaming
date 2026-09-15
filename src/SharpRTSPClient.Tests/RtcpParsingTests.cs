using System;
using System.Collections.Generic;
using System.Linq;
using SharpRTSPClient;

namespace SharpRTSPClient.Tests
{
    /// <summary>
    /// The RTCP parser reads bytes straight off the wire. On UDP transport those bytes can come from
    /// anyone who can reach the client's RTCP port, so malformed input must never throw.
    /// </summary>
    public class RtcpParsingTests
    {
        private const int SenderReport = 200;
        private const int ReceiverReport = 201;
        private const int Bye = 203;

        private const uint OurSsrc = 0x1234ABCD;
        private const uint ServerSsrc = 0x0BADF00D;

        /// <summary>
        /// Builds a single RTCP packet with the given type and body. Length is derived from the real size.
        /// </summary>
        private static byte[] Packet(int packetType, uint ssrc, int bodyBytes = 0)
        {
            byte[] packet = new byte[8 + bodyBytes];
            packet[0] = 0x80; // version 2, no padding, count 0
            packet[1] = (byte)packetType;
            int lengthInWords = (packet.Length / 4) - 1;
            packet[2] = (byte)(lengthInWords >> 8);
            packet[3] = (byte)lengthInWords;
            packet[4] = (byte)(ssrc >> 24);
            packet[5] = (byte)(ssrc >> 16);
            packet[6] = (byte)(ssrc >> 8);
            packet[7] = (byte)ssrc;
            return packet;
        }

        private static byte[] SenderReportPacket(uint ssrc) => Packet(SenderReport, ssrc, bodyBytes: 20);

        private static List<byte[]> Parse(RTSPClient client, byte[] data, RtcpChannelState channel)
        {
            return client.ParseRTCPAndGenerateResponse(new Memory<byte>(data), OurSsrc, channel);
        }

        /// <summary>A channel that has not seen any RTP yet.</summary>
        private static RtcpChannelState FreshChannel() => new RtcpChannelState();

        /// <summary>A channel already streaming from the given SSRC.</summary>
        private static RtcpChannelState ChannelStreamingFrom(uint ssrc)
        {
            var channel = new RtcpChannelState();
            channel.LearnRemoteSsrc(ssrc);
            return channel;
        }

        [Fact]
        public void SenderReport_IsAnsweredWithAReceiverReportCarryingOurSsrc()
        {
            using var client = new RTSPClient();
            var reports = Parse(client, SenderReportPacket(ServerSsrc), FreshChannel());

            byte[] report = Assert.Single(reports);
            Assert.Equal(8, report.Length);
            Assert.Equal(2, report[0] >> 6);            // version
            Assert.Equal(ReceiverReport, report[1]);    // packet type
            Assert.Equal(OurSsrc, (uint)((report[4] << 24) | (report[5] << 16) | (report[6] << 8) | report[7]));
        }

        [Fact]
        public void CompoundPacket_EveryElementIsWalked()
        {
            using var client = new RTSPClient();
            // Receiver Report, then two Sender Reports - we should answer both SRs and not fall off the end
            byte[] data = Packet(ReceiverReport, ServerSsrc)
                .Concat(SenderReportPacket(ServerSsrc))
                .Concat(SenderReportPacket(ServerSsrc))
                .ToArray();

            Assert.Equal(2, Parse(client, data, FreshChannel()).Count);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        public void TruncatedHeader_IsIgnoredInsteadOfThrowing(int length)
        {
            using var client = new RTSPClient();
            Assert.Empty(Parse(client, new byte[length], FreshChannel()));
        }

        [Fact]
        public void PacketDeclaringMoreBytesThanTheDatagramHolds_IsIgnored()
        {
            using var client = new RTSPClient();
            // an 8 byte datagram claiming to be 0xFFFF words long
            byte[] data = { 0x80, SenderReport, 0xFF, 0xFF, 1, 2, 3, 4 };

            Assert.Empty(Parse(client, data, FreshChannel()));
        }

        [Fact]
        public void SenderReportTruncatedBeforeTheRtpTimestamp_IsIgnored()
        {
            using var client = new RTSPClient();
            // a well formed but too short Sender Report - the timestamp fields are simply not there
            Assert.Empty(Parse(client, Packet(SenderReport, ServerSsrc, bodyBytes: 4), FreshChannel()));
        }

        [Fact]
        public void PacketDeclaringZeroLength_DoesNotLoopForever()
        {
            using var client = new RTSPClient();
            byte[] data = { 0x80, SenderReport, 0x00, 0x00, 1, 2, 3, 4 };

            // must return rather than spin on a packet that advances the cursor by nothing
            Assert.Empty(Parse(client, data, FreshChannel()));
        }

        [Fact]
        public void RandomBytes_NeverThrow()
        {
            using var client = new RTSPClient();
            var random = new Random(20240607);

            for (int i = 0; i < 20_000; i++)
            {
                byte[] buffer = new byte[random.Next(0, 96)];
                random.NextBytes(buffer);

                // bias towards real RTCP packet types so we reach the type specific parsing
                if (buffer.Length > 1 && random.Next(2) == 0)
                {
                    buffer[1] = (byte)(200 + random.Next(8));
                }

                byte[] input = buffer;
                var exception = Record.Exception(() => Parse(client, input, FreshChannel()));
                Assert.True(exception is null, $"threw {exception?.GetType().Name} on [{string.Join(",", buffer)}]");
            }
        }

        [Fact]
        public void Bye_FromTheStreamingSource_StopsTheClient()
        {
            using var client = new RTSPClient();
            StoppedReason? reason = null;
            client.Stopped += (s, e) => reason = e.Reason;

            Parse(client, Packet(Bye, ServerSsrc), ChannelStreamingFrom(ServerSsrc));

            Assert.Equal(StoppedReason.RtcpBye, reason);
        }

        [Fact]
        public void Bye_BeforeAnyMediaHasArrived_StopsTheClient()
        {
            using var client = new RTSPClient();
            StoppedReason? reason = null;
            client.Stopped += (s, e) => reason = e.Reason;

            // we have not seen any RTP yet, so there is nothing to compare the BYE against
            Parse(client, Packet(Bye, ServerSsrc), FreshChannel());

            Assert.Equal(StoppedReason.RtcpBye, reason);
        }

        [Fact]
        public void Bye_FromAnSsrcWeAreNotStreamingFrom_IsIgnored()
        {
            using var client = new RTSPClient();
            StoppedReason? reason = null;
            client.Stopped += (s, e) => reason = e.Reason;

            // a spoofed BYE - on UDP anyone who can reach the RTCP port could send this
            Parse(client, Packet(Bye, 0xDEADBEEF), ChannelStreamingFrom(ServerSsrc));

            Assert.Null(reason);
        }
    }
}
