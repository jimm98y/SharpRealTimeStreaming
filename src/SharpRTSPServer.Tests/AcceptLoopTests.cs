// SharpRTSPServer
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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

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
        public void ConnectionsThatNeverSpeakDoNotStopOtherClientsGettingIn()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password", false, ServerCertificate(), null);
            server.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), null));
            server.StartListen();

            // The TLS handshake used to happen inside the accept, so a client that connected and then
            // said nothing held the loop and nobody else got in at all - one socket, no credentials.
            var silent = new List<TcpClient>();
            try
            {
                for (int i = 0; i < 10; i++)
                {
                    silent.Add(new TcpClient("127.0.0.1", port));
                }

                Thread.Sleep(250);

                var stopwatch = Stopwatch.StartNew();
                using var client = new RtspTestClient(port, "admin", "password", useTls: true);
                int status = client.Send("OPTIONS", "rtsps://127.0.0.1:" + port + "/stream1").StatusCode;
                stopwatch.Stop();

                // The real signal is that it is answered at all: before this, one silent connection
                // was enough that nothing else was ever answered and this threw on its read timeout.
                // The bound is generous because the measurement shares a machine with the rest of
                // the suite, and a tight one here has flaked for that reason rather than for a real
                // one.
                Assert.AreEqual(200, status);
                Assert.IsLessThan(9000, stopwatch.ElapsedMilliseconds,
                    "a client should not be kept waiting by connections that never speak");
            }
            finally
            {
                foreach (var s in silent)
                {
                    s.Dispose();
                }
            }
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
