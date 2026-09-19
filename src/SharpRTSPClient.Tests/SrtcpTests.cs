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

using System.Linq;
using SharpSRTP.SRTP;

namespace SharpRTSPClient.Tests
{
    [TestClass]
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

        [TestMethod]
        public void ProtectedRtcpCarriesThePlaintextHeaderRatherThanZeros()
        {
            byte[] report = ReceiverReport(0xDEADBEEF);

            byte[] protectedRtcp = RTSPClient.ProtectRtcp(NewContext(), report);

            // The first 8 bytes of an SRTCP packet stay in the clear, so they must still be our report.
            // They used to come out as zeros because the payload was never copied into the output buffer.
            CollectionAssert.AreEqual(report, protectedRtcp.Take(8).ToArray());
        }

        [TestMethod]
        public void ProtectedRtcpIsNotAllZeros()
        {
            byte[] protectedRtcp = RTSPClient.ProtectRtcp(NewContext(), ReceiverReport(0x11223344));

            Assert.Contains(b => b != 0, protectedRtcp);
        }

        [TestMethod]
        public void ProtectedRtcpIsLongerThanTheInputBecauseOfTheAuthTag()
        {
            byte[] report = ReceiverReport(0x11223344);

            byte[] protectedRtcp = RTSPClient.ProtectRtcp(NewContext(), report);

            Assert.IsGreaterThan(report.Length, protectedRtcp.Length,
                "SRTCP appends an index and an authentication tag, so the result must grow");
        }

        [TestMethod]
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

            Assert.AreEqual(0, result);
            CollectionAssert.AreEqual(report, buffer.Take(length).ToArray());
        }

        [TestMethod]
        public void DifferentReportsProduceDifferentProtectedPackets()
        {
            var context = NewContext();

            byte[] first = RTSPClient.ProtectRtcp(context, ReceiverReport(0x11111111));
            byte[] second = RTSPClient.ProtectRtcp(context, ReceiverReport(0x22222222));

            Assert.AreNotSequenceEqual(first, second);
        }
    }
}
