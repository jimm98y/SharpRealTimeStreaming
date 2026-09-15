using System;
using System.Net;
using Rtsp;

namespace SharpRTSPClient.Tests
{
    /// <summary>
    /// The client has to work against cameras that offer either scheme, so it accepts whichever one
    /// the server challenges with. (The server in this repo only ever offers Digest.)
    /// </summary>
    public class AuthenticationSchemeTests
    {
        private static readonly NetworkCredential Credentials = new NetworkCredential("admin", "password");

        [Fact]
        public void ABasicChallengeIsAnswered()
        {
            var authentication = Authentication.Create(Credentials, "Basic realm=\"AProxy\"");

            Assert.IsType<AuthenticationBasic>(authentication);
        }

        [Theory]
        [InlineData("Digest realm=\"AXIS_WS_ACCC8E3A0A8F\", nonce=\"000057c3Y810622bff50b36005eb5efeae118626a161bf\", stale=FALSE")]
        [InlineData("Digest realm=\"IP Camera(21388)\", nonce=\"534407f373af1bdff561b7b4da295354\", stale=\"FALSE\"")]
        public void ADigestChallengeIsAnswered(string challenge)
        {
            var authentication = Authentication.Create(Credentials, challenge);

            Assert.IsType<AuthenticationDigest>(authentication);
        }

        [Fact]
        public void BasicPutsTheCredentialsOnTheWireReversibly()
        {
            var authentication = Authentication.Create(Credentials, "Basic realm=\"cam\"");

            string header = authentication.GetResponse(0, "rtsp://cam/stream1", "DESCRIBE", new byte[0]);

            // this is why the server does not offer Basic - the password is recoverable from the header
            Assert.StartsWith("Basic ", header, StringComparison.Ordinal);
            string decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(header.Substring("Basic ".Length)));
            Assert.Equal("admin:password", decoded);
        }

        [Fact]
        public void DigestDoesNotRevealThePassword()
        {
            var authentication = Authentication.Create(Credentials, "Digest realm=\"cam\", nonce=\"deadbeef\"");

            string header = authentication.GetResponse(0, "rtsp://cam/stream1", "DESCRIBE", new byte[0]);

            Assert.StartsWith("Digest ", header, StringComparison.Ordinal);
            Assert.DoesNotContain("password", header, StringComparison.OrdinalIgnoreCase);
        }
    }
}
