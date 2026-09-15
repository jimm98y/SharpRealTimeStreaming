using System;
using System.Linq;
using Rtsp.Sdp;
using SharpRTSPClient;

namespace SharpRTSPClient.Tests
{
    /// <summary>
    /// The SDP is supplied by the server, so the crypto attribute is untrusted input.
    /// </summary>
    [TestClass]
    public class SdpCryptoTests
    {
        [TestMethod]
        public void ParseMKI_ReadsTheValueIntoABufferOfTheDeclaredLength()
        {
            using var client = new RTSPClient();

            byte[] mki = client.ParseMKI("1:4");

            Assert.IsNotNull(mki);
            Assert.HasCount(4, mki);
            Assert.AreEqual(1, mki[0]);
        }

        [TestMethod]
        [DataRow("")]                        // empty
        [DataRow("1")]                       // no length
        [DataRow("1:2:3")]                   // too many parts
        [DataRow("abc:4")]                   // value is not a number
        [DataRow("1:abc")]                   // length is not a number
        [DataRow("1:0")]                     // zero length
        [DataRow("1:-4")]                    // negative length
        [DataRow("-1:4")]                    // negative value
        [DataRow("1:2147483647")]            // length that would exhaust memory
        [DataRow("1:99999999999999999999")]  // length that does not fit in an int
        [DataRow("16909060:2")]              // value too wide for the declared length
        public void ParseMKI_RejectsMalformedInputInsteadOfThrowing(string sdpMki)
        {
            using var client = new RTSPClient();

            Assert.IsNull(client.ParseMKI(sdpMki));
        }

        [TestMethod]
        public void ParseMKI_RejectsALengthAboveTheRfc4568Maximum()
        {
            using var client = new RTSPClient();

            Assert.IsNotNull(client.ParseMKI("1:128"));
            Assert.IsNull(client.ParseMKI("1:129"));
        }

        [TestMethod]
        public void PrepareSrtpContext_ToleratesMediaWithoutAnRtpType()
        {
            using var client = new RTSPClient();
            var media = new Media(string.Empty) { RtpType = null };

            // must not throw - the null check has to guard both profile comparisons
            Assert.IsNull(client.PrepareSrtpContext(media));
        }

        [TestMethod]
        [DataRow("RTP/AVP")]
        [DataRow("RTP/AVPF")]
        public void PrepareSrtpContext_ReturnsNothingForUnencryptedProfiles(string rtpType)
        {
            using var client = new RTSPClient();
            var media = new Media(string.Empty) { RtpType = rtpType };

            Assert.IsNull(client.PrepareSrtpContext(media));
        }

        [TestMethod]
        public void PrepareSrtpContext_RejectsAnInvalidBase64MasterKey()
        {
            using var client = new RTSPClient();
            var media = new Media(string.Empty) { RtpType = "RTP/SAVP" };
            media.Attributs.Add(Attribut.ParseInvariant("crypto:1 AES_CM_128_HMAC_SHA1_80 inline:!!!not-base64!!!"));

            Assert.IsNull(client.PrepareSrtpContext(media));
        }
    }
}
