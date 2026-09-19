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

using System.Collections.Generic;
using System.Threading;

namespace SharpRTSPClient.Tests
{
    /// <summary>
    /// A stream described as encrypted is not played in the clear.
    /// </summary>
    /// <remarks>
    /// With no key the SRTP context stays null, and a null context means the receive path never
    /// attempts to decrypt - so the client used to play a stream that was meant to be encrypted as
    /// though it were plain RTP, and say nothing about it. Anyone who could alter the SDP could
    /// arrange that by deleting one line.
    /// </remarks>
    [TestClass]
    public sealed class SavpWithoutKeyTests
    {
        private const string GoodKey = "inline:d0RmdmcmVCspeEc3QGZiNWpVLFJhQX1cfHAwJSoj";

        private static string Sdp(string profile, string cryptoLine)
        {
            return "v=0\r\no=- 0 0 IN IP4 0.0.0.0\r\ns=Test\r\nc=IN IP4 0.0.0.0\r\n" +
                   $"m=video 0 RTP/{profile} 96\r\na=control:trackID=0\r\na=rtpmap:96 H264/90000\r\n" +
                   "a=fmtp:96 packetization-mode=1; sprop-parameter-sets=Z0IAHpZUBaHogA==,aM48gA==\r\n" +
                   cryptoLine;
        }

        private static (bool ReachedPlay, List<StoppedReason> Stops) Connect(string sdp)
        {
            using var server = new FakeRtspServer(sdp);

            var stops = new List<StoppedReason>();
            using var client = new RTSPClient();
            client.Stopped += (s, e) => { lock (stops) stops.Add(e.Reason); };

            client.Connect(server.BaseUri, RTPTransport.TCP, "admin", "password",
                MediaRequest.VIDEO_ONLY, false, null, false);

            bool reachedPlay = server.WaitForRequest("PLAY", 3000);
            Thread.Sleep(200);

            lock (stops)
            {
                return (reachedPlay, new List<StoppedReason>(stops));
            }
        }

        [TestMethod]
        public void SavpWithNoCryptoAttributeIsRefused()
        {
            var result = Connect(Sdp("SAVP", cryptoLine: string.Empty));

            Assert.IsFalse(result.ReachedPlay, "a stream with no key should not be played at all");
            CollectionAssert.Contains(result.Stops, StoppedReason.EncryptionUnavailable);
        }

        [TestMethod]
        public void SavpfWithNoCryptoAttributeIsRefused()
        {
            var result = Connect(Sdp("SAVPF", cryptoLine: string.Empty));

            Assert.IsFalse(result.ReachedPlay);
            CollectionAssert.Contains(result.Stops, StoppedReason.EncryptionUnavailable);
        }

        [TestMethod]
        public void SavpWithAKeyOfTheWrongLengthIsRefused()
        {
            // valid base64, but nothing like the length the suite needs
            var result = Connect(Sdp("SAVP", "a=crypto:1 AES_CM_128_HMAC_SHA1_80 inline:AAAA\r\n"));

            Assert.IsFalse(result.ReachedPlay);
            CollectionAssert.Contains(result.Stops, StoppedReason.EncryptionUnavailable,
                "an unusable key should be reported as that, not as a broken dialog");
        }

        [TestMethod]
        public void SavpWithASuiteWeDoNotImplementIsRefused()
        {
            var result = Connect(Sdp("SAVP", "a=crypto:1 NOT_A_REAL_SUITE " + GoodKey + "\r\n"));

            Assert.IsFalse(result.ReachedPlay);
            CollectionAssert.Contains(result.Stops, StoppedReason.EncryptionUnavailable);
        }

        [TestMethod]
        public void SavpWithAUsableKeyIsPlayed()
        {
            var result = Connect(Sdp("SAVP", "a=crypto:1 AES_CM_128_HMAC_SHA1_80 " + GoodKey + "\r\n"));

            Assert.IsTrue(result.ReachedPlay, "a stream that did give us a key should play");
            CollectionAssert.DoesNotContain(result.Stops, StoppedReason.EncryptionUnavailable);
        }

        [TestMethod]
        public void PlainAvpNeedsNoKey()
        {
            // nothing here claims to be encrypted, so nothing is expected of the SDP
            var result = Connect(Sdp("AVP", cryptoLine: string.Empty));

            Assert.IsTrue(result.ReachedPlay);
            CollectionAssert.DoesNotContain(result.Stops, StoppedReason.EncryptionUnavailable);
        }
    }
}
