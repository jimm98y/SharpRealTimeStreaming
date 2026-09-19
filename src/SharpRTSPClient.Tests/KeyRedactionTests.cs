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
    /// An SDP describing a protected stream carries the SRTP master key in its crypto attribute, so
    /// logging one verbatim wrote the key that protects the media into the log - where it outlives
    /// the session and is readable by anyone who can read the log.
    /// </summary>
    [TestClass]
    public sealed class KeyRedactionTests
    {
        private const string Key = "d0RmdmNtVnpiV0Z6ZFhKbGMybHVaMlZ5YVhSNQ==";

        [TestMethod]
        public void TheKeyIsTakenOutOfACryptoAttribute()
        {
            string sdp = "m=video 0 RTP/SAVP 96\r\n"
                       + "a=crypto:1 AES_CM_128_HMAC_SHA1_80 inline:" + Key + "\r\n";

            string redacted = RTSPClient.RedactKeys(sdp);

            Assert.DoesNotContain(Key, redacted, "the master key must not reach the log");
            Assert.Contains("inline:<redacted>", redacted);
        }

        [TestMethod]
        public void WhatTheLineSaysIsBeingDoneIsKept()
        {
            string sdp = "a=crypto:1 AES_CM_128_HMAC_SHA1_80 inline:" + Key + "\r\n";

            string redacted = RTSPClient.RedactKeys(sdp);

            Assert.Contains("a=crypto:1", redacted, "the tag is worth having");
            Assert.Contains("AES_CM_128_HMAC_SHA1_80", redacted, "so is the suite");
        }

        [TestMethod]
        public void TheParametersAfterTheKeyAreKept()
        {
            string sdp = "a=crypto:1 AES_CM_128_HMAC_SHA1_80 inline:" + Key + "|2^20|1:4\r\n";

            string redacted = RTSPClient.RedactKeys(sdp);

            Assert.DoesNotContain(Key, redacted);
            Assert.Contains("|2^20|1:4", redacted, "the lifetime and MKI say what is being done, not with what");
        }

        [TestMethod]
        public void EveryKeyInADescriptionIsTakenOut()
        {
            string second = "YWJjZGVmZ2hpamtsbW5vcHFyc3R1dnd4eXowMTIz";

            string sdp = "m=video 0 RTP/SAVP 96\r\n"
                       + "a=crypto:1 AES_CM_128_HMAC_SHA1_80 inline:" + Key + "\r\n"
                       + "m=audio 0 RTP/SAVP 97\r\n"
                       + "a=crypto:1 AES_CM_128_HMAC_SHA1_80 inline:" + second + "\r\n";

            string redacted = RTSPClient.RedactKeys(sdp);

            Assert.DoesNotContain(Key, redacted);
            Assert.DoesNotContain(second, redacted);
        }

        [TestMethod]
        public void AnSdpWithNoKeysIsHandedBackAsItIs()
        {
            string sdp = "v=0\r\nm=video 0 RTP/AVP 96\r\na=control:trackID=0\r\n";

            Assert.AreEqual(sdp, RTSPClient.RedactKeys(sdp), "nothing to hide, so nothing to rewrite");
        }

        [TestMethod]
        public void NothingAtAllIsHandledRatherThanThrown()
        {
            Assert.IsNull(RTSPClient.RedactKeys(null));
            Assert.AreEqual(string.Empty, RTSPClient.RedactKeys(string.Empty));
        }
    }
}
