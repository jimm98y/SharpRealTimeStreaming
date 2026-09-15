using System;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SharpRTSPServer;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// The server serving RTSPS. Completing a TLS handshake is a stronger requirement than the
    /// certificate merely loading, and it is the part that differs between platforms.
    /// </summary>
    [TestClass]
    public sealed class RtspsTests
    {
        private const string UserName = "admin";
        private const string Password = "password";
        private const string StreamId = "stream1";

        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        /// <summary>
        /// Builds a self-signed certificate usable as a TLS server identity.
        /// </summary>
        /// <remarks>
        /// The PKCS#12 round trip is not decoration. CreateSelfSigned hands back a certificate whose
        /// key is ephemeral, and SslStream on Windows goes through SChannel, which cannot build
        /// server credentials from an ephemeral key - it fails the handshake with "No credentials are
        /// available in the security package". Re-importing gives the key somewhere SChannel can
        /// reference it. The same trap applies to anything else that loads a server certificate.
        /// </remarks>
        private static X509Certificate2 CreateServerCertificate(string host = "localhost")
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyAgreement, true));
            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

            var subjectAlternativeName = new SubjectAlternativeNameBuilder();
            if (IPAddress.TryParse(host, out IPAddress address))
            {
                subjectAlternativeName.AddIpAddress(address);
            }
            else
            {
                subjectAlternativeName.AddDnsName(host);
            }
            request.CertificateExtensions.Add(subjectAlternativeName.Build());

            using var ephemeral = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

            return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
        }

        private static RTSPServer StartTlsServer(X509Certificate2 certificate, int port)
        {
            var server = new RTSPServer(port, UserName, Password,
                useHttpTunnel: false,
                tlsCertificate: certificate,
                loggerFactory: null);

            server.AddStreamSource(new RTSPStreamSource(StreamId, new H264Track(Sps, Pps), null));
            server.StartListen();
            return server;
        }

        [TestMethod]
        public void ServerCompletesAnRtspsHandshakeAndAnswers()
        {
            int port = TestPorts.FindFree();
            using X509Certificate2 certificate = CreateServerCertificate();
            using RTSPServer server = StartTlsServer(certificate, port);

            using var client = new RtspTestClient(port, UserName, Password, useTls: true);

            // reaching a reply at all means the TLS handshake completed and the server had usable
            // credentials, which is exactly what an ephemeral key would have denied it
            Assert.AreEqual(200, client.Send("OPTIONS", $"rtsps://127.0.0.1:{port}/{StreamId}").StatusCode);
        }

        [TestMethod]
        public void RtspsStillRequiresAuthentication()
        {
            int port = TestPorts.FindFree();
            using X509Certificate2 certificate = CreateServerCertificate();
            using RTSPServer server = StartTlsServer(certificate, port);

            using var anonymous = new RtspTestClient(port, null, null, useTls: true);

            var response = anonymous.Send("DESCRIBE", $"rtsps://127.0.0.1:{port}/{StreamId}", "Accept: application/sdp");

            Assert.AreEqual(401, response.StatusCode, "TLS does not replace authentication");
            StringAssert.Contains(response.Headers, "WWW-Authenticate", StringComparison.OrdinalIgnoreCase);
        }

        [TestMethod]
        public void RtspsServesTheSdpOverTheEncryptedConnection()
        {
            int port = TestPorts.FindFree();
            using X509Certificate2 certificate = CreateServerCertificate();
            using RTSPServer server = StartTlsServer(certificate, port);

            string baseUri = $"rtsps://127.0.0.1:{port}/{StreamId}";
            using var client = new RtspTestClient(port, UserName, Password, useTls: true);

            client.Send("OPTIONS", baseUri);
            var describe = client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            Assert.AreEqual(200, describe.StatusCode);
            StringAssert.Contains(describe.Headers, "application/sdp", StringComparison.OrdinalIgnoreCase);
        }

        [TestMethod]
        public void TheServerReportsTheCertificateItWasGiven()
        {
            int port = TestPorts.FindFree();
            using X509Certificate2 certificate = CreateServerCertificate();
            using RTSPServer server = StartTlsServer(certificate, port);

            Assert.IsNotNull(server.TlsCertificate);
            Assert.AreEqual(certificate.Thumbprint, server.TlsCertificate.Thumbprint);
            Assert.IsTrue(server.TlsCertificate.HasPrivateKey, "a server certificate is useless without its private key");
        }
    }
}
