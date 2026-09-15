using System;
using System.Linq;
using Rtsp.Sdp;
using SharpRTSPClient;

namespace SharpRTSPClient.Tests
{
    /// <summary>
    /// The SDP is supplied by the server, so the crypto attribute is untrusted input.
    /// </summary>
    public class SdpCryptoTests
    {
        [Fact]
        public void ParseMKI_ReadsTheValueIntoABufferOfTheDeclaredLength()
        {
            using var client = new RTSPClient();

            byte[] mki = client.ParseMKI("1:4");

            Assert.NotNull(mki);
            Assert.Equal(4, mki.Length);
            Assert.Equal(1, mki[0]);
        }

        [Theory]
        [InlineData("")]                        // empty
        [InlineData("1")]                       // no length
        [InlineData("1:2:3")]                   // too many parts
        [InlineData("abc:4")]                   // value is not a number
        [InlineData("1:abc")]                   // length is not a number
        [InlineData("1:0")]                     // zero length
        [InlineData("1:-4")]                    // negative length
        [InlineData("-1:4")]                    // negative value
        [InlineData("1:2147483647")]            // length that would exhaust memory
        [InlineData("1:99999999999999999999")]  // length that does not fit in an int
        [InlineData("16909060:2")]              // value too wide for the declared length
        public void ParseMKI_RejectsMalformedInputInsteadOfThrowing(string sdpMki)
        {
            using var client = new RTSPClient();

            Assert.Null(client.ParseMKI(sdpMki));
        }

        [Fact]
        public void ParseMKI_RejectsALengthAboveTheRfc4568Maximum()
        {
            using var client = new RTSPClient();

            Assert.NotNull(client.ParseMKI("1:128"));
            Assert.Null(client.ParseMKI("1:129"));
        }

        [Fact]
        public void PrepareSrtpContext_ToleratesMediaWithoutAnRtpType()
        {
            using var client = new RTSPClient();
            var media = new Media(string.Empty) { RtpType = null };

            // must not throw - the null check has to guard both profile comparisons
            Assert.Null(client.PrepareSrtpContext(media));
        }

        [Theory]
        [InlineData("RTP/AVP")]
        [InlineData("RTP/AVPF")]
        public void PrepareSrtpContext_ReturnsNothingForUnencryptedProfiles(string rtpType)
        {
            using var client = new RTSPClient();
            var media = new Media(string.Empty) { RtpType = rtpType };

            Assert.Null(client.PrepareSrtpContext(media));
        }

        [Fact]
        public void PrepareSrtpContext_RejectsAnInvalidBase64MasterKey()
        {
            using var client = new RTSPClient();
            var media = new Media(string.Empty) { RtpType = "RTP/SAVP" };
            media.Attributs.Add(Attribut.ParseInvariant("crypto:1 AES_CM_128_HMAC_SHA1_80 inline:!!!not-base64!!!"));

            Assert.Null(client.PrepareSrtpContext(media));
        }
    }
}
