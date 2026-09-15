using System;
using System.Net;
using Rtsp;

namespace SharpRTSPClient.Tests
{
    /// <summary>
    /// The client has to work against cameras that offer either scheme, so it accepts whichever one
    /// the server challenges with. (The server in this repo only ever offers Digest.)
    /// </summary>
    [TestClass]
    public class AuthenticationSchemeTests
    {
        private static readonly NetworkCredential Credentials = new NetworkCredential("admin", "password");

        [TestMethod]
        public void ABasicChallengeIsAnswered()
        {
            var authentication = Authentication.Create(Credentials, "Basic realm=\"AProxy\"");

            Assert.IsInstanceOfType<AuthenticationBasic>(authentication);
        }

        [TestMethod]
        [DataRow("Digest realm=\"AXIS_WS_ACCC8E3A0A8F\", nonce=\"000057c3Y810622bff50b36005eb5efeae118626a161bf\", stale=FALSE")]
        [DataRow("Digest realm=\"IP Camera(21388)\", nonce=\"534407f373af1bdff561b7b4da295354\", stale=\"FALSE\"")]
        public void ADigestChallengeIsAnswered(string challenge)
        {
            var authentication = Authentication.Create(Credentials, challenge);

            Assert.IsInstanceOfType<AuthenticationDigest>(authentication);
        }

        [TestMethod]
        public void BasicPutsTheCredentialsOnTheWireReversibly()
        {
            var authentication = Authentication.Create(Credentials, "Basic realm=\"cam\"");

            string header = authentication.GetResponse(0, "rtsp://cam/stream1", "DESCRIBE", new byte[0]);

            // this is why the server does not offer Basic - the password is recoverable from the header
            StringAssert.StartsWith(header, "Basic ", StringComparison.Ordinal);
            string decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(header.Substring("Basic ".Length)));
            Assert.AreEqual("admin:password", decoded);
        }

        [TestMethod]
        public void DigestDoesNotRevealThePassword()
        {
            var authentication = Authentication.Create(Credentials, "Digest realm=\"cam\", nonce=\"deadbeef\"");

            string header = authentication.GetResponse(0, "rtsp://cam/stream1", "DESCRIBE", new byte[0]);

            StringAssert.StartsWith(header, "Digest ", StringComparison.Ordinal);
            Assert.IsFalse(header.Contains("password", StringComparison.OrdinalIgnoreCase), "the password must not appear in the header");
        }
    }
}
