using System;
using System.Linq;
using SharpSRTP.SRTP;
using SharpRTSPClient;

namespace SharpRTSPClient.Tests
{
    public class SrtcpTests
    {
        private const string CryptoSuite = SrtpCryptoSuites.AES_CM_128_HMAC_SHA1_80;

        private static SrtpSessionContext NewContext()
        {
            SrtpKeys keys = SrtpProtocol.CreateMasterKeys(CryptoSuite, SrtpProtocol.GenerateMki(0));
            return SrtpProtocol.CreateSrtpSessionContext(keys);
        }

        /// <summary>
        /// A plain Receiver Report, the kind the client actually sends.
        /// </summary>
        private static byte[] ReceiverReport(uint ssrc) => new byte[]
        {
            0x80, 201, 0x00, 0x01,
            (byte)(ssrc >> 24), (byte)(ssrc >> 16), (byte)(ssrc >> 8), (byte)ssrc,
        };

        [Fact]
        public void ProtectedRtcpCarriesThePlaintextHeaderRatherThanZeros()
        {
            byte[] report = ReceiverReport(0xDEADBEEF);

            byte[] protectedRtcp = RTSPClient.ProtectRtcp(NewContext(), report);

            // The first 8 bytes of an SRTCP packet stay in the clear, so they must still be our report.
            // They used to come out as zeros because the payload was never copied into the output buffer.
            Assert.Equal(report, protectedRtcp.Take(8).ToArray());
        }

        [Fact]
        public void ProtectedRtcpIsNotAllZeros()
        {
            byte[] protectedRtcp = RTSPClient.ProtectRtcp(NewContext(), ReceiverReport(0x11223344));

            Assert.Contains(protectedRtcp, b => b != 0);
        }

        [Fact]
        public void ProtectedRtcpIsLongerThanTheInputBecauseOfTheAuthTag()
        {
            byte[] report = ReceiverReport(0x11223344);

            byte[] protectedRtcp = RTSPClient.ProtectRtcp(NewContext(), report);

            Assert.True(protectedRtcp.Length > report.Length,
                "SRTCP appends an index and an authentication tag, so the result must grow");
        }

        [Fact]
        public void ProtectedRtcpRoundTripsBackToThePlaintext()
        {
            // both ends of the same session share the keys, so what one protects the other can unprotect
            SrtpKeys keys = SrtpProtocol.CreateMasterKeys(CryptoSuite, SrtpProtocol.GenerateMki(0));
            var sender = SrtpProtocol.CreateSrtpSessionContext(keys);
            var receiver = SrtpProtocol.CreateSrtpSessionContext(keys);

            byte[] report = ReceiverReport(0x0BADF00D);
            byte[] protectedRtcp = RTSPClient.ProtectRtcp(sender, report);

            byte[] buffer = protectedRtcp.ToArray();
            int result = receiver.DecodeRtcpContext.UnprotectRtcp(buffer, buffer.Length, out int length);

            Assert.Equal(0, result);
            Assert.Equal(report, buffer.Take(length).ToArray());
        }

        [Fact]
        public void DifferentReportsProduceDifferentProtectedPackets()
        {
            var context = NewContext();

            byte[] first = RTSPClient.ProtectRtcp(context, ReceiverReport(0x11111111));
            byte[] second = RTSPClient.ProtectRtcp(context, ReceiverReport(0x22222222));

            Assert.NotEqual(first, second);
        }
    }
}
