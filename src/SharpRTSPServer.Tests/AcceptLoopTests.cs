using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using SharpRTSPServer;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// The server has to keep accepting connections after one of them fails to be accepted.
    /// </summary>
    [TestClass]
    public sealed class AcceptLoopTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        private static X509Certificate2 ServerCertificate()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
            var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

            // SChannel cannot build server credentials from an ephemeral key, see RtspsTests
            return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx, "x"), "x",
                X509KeyStorageFlags.Exportable);
        }

        private static void SaySomethingThatIsNotTls(int port)
        {
            using var rude = new TcpClient("127.0.0.1", port);
            byte[] junk = Encoding.ASCII.GetBytes("OPTIONS * RTSP/1.0\r\n\r\n");
            rude.GetStream().Write(junk, 0, junk.Length);
            rude.GetStream().Flush();
        }

        [TestMethod]
        public void AFailedTlsHandshakeDoesNotStopTheServerAccepting()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password", false, ServerCertificate(), null);
            server.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), null));
            server.StartListen();

            // Anyone at all could send this. The handshake happens inside the accept, and its failure
            // used to end the accept loop for good: the server stopped taking new connections while
            // everything already connected carried on, so nothing looked wrong from the outside.
            SaySomethingThatIsNotTls(port);
            Thread.Sleep(250);

            using var client = new RtspTestClient(port, "admin", "password", useTls: true);
            var response = client.Send("OPTIONS", "rtsps://127.0.0.1:" + port + "/stream1");

            Assert.AreEqual(200, response.StatusCode, "the server stopped accepting after one bad handshake");
        }

        [TestMethod]
        public void RepeatedBadHandshakesDoNotStopTheServerAccepting()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password", false, ServerCertificate(), null);
            server.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), null));
            server.StartListen();

            for (int i = 0; i < 20; i++)
            {
                SaySomethingThatIsNotTls(port);
            }

            Thread.Sleep(250);

            using var client = new RtspTestClient(port, "admin", "password", useTls: true);
            Assert.AreEqual(200, client.Send("OPTIONS", "rtsps://127.0.0.1:" + port + "/stream1").StatusCode);
        }

        [TestMethod]
        public void AClientThatConnectsAndLeavesDoesNotStopTheServerAccepting()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");
            server.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), null));
            server.StartListen();

            for (int i = 0; i < 20; i++)
            {
                using (new TcpClient("127.0.0.1", port))
                {
                }
            }

            Thread.Sleep(250);

            using var client = new RtspTestClient(port, "admin", "password");
            Assert.AreEqual(200, client.Send("OPTIONS", "rtsp://127.0.0.1:" + port + "/stream1").StatusCode);
        }
    }
}
