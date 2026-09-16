using System;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using SharpRTSPServer;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// The Digest nonce is rotated, so a captured Authorization header stops working.
    /// </summary>
    [TestClass]
    public sealed class NonceRotationTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        private const string UserName = "admin";
        private const string Password = "password";

        private static RTSPServer NewServer(int port, TimeSpan nonceLifetime)
        {
            var server = new RTSPServer(port, UserName, Password) { NonceLifetime = nonceLifetime };
            server.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), null));
            server.StartListen();
            return server;
        }

        /// <summary>
        /// Sends one request with a caller supplied Authorization header, exactly as a replay would.
        /// </summary>
        private static (int StatusCode, string Challenge) SendRaw(int port, string uri, string authorization)
        {
            using var tcp = new TcpClient("127.0.0.1", port) { ReceiveTimeout = 10_000, SendTimeout = 10_000 };
            using var stream = tcp.GetStream();

            var request = new StringBuilder();
            request.Append("OPTIONS ").Append(uri).Append(" RTSP/1.0\r\nCSeq: 1\r\n");
            if (authorization != null)
            {
                request.Append("Authorization: ").Append(authorization).Append("\r\n");
            }
            request.Append("\r\n");

            byte[] payload = Encoding.ASCII.GetBytes(request.ToString());
            stream.Write(payload, 0, payload.Length);
            stream.Flush();

            var raw = new StringBuilder();
            byte[] one = new byte[1];
            while (!raw.ToString().Contains("\r\n\r\n"))
            {
                if (stream.Read(one, 0, 1) == 0) break;
                raw.Append((char)one[0]);
            }

            string text = raw.ToString();
            int status = int.Parse(text.Substring(9, 3), CultureInfo.InvariantCulture);
            return (status, text);
        }

        private static string Md5(string value)
        {
            using var md5 = MD5.Create();
            byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(value));
            var builder = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
            {
                builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }

        private static string BuildDigest(string realm, string nonce, string uri)
        {
            string ha1 = Md5($"{UserName}:{realm}:{Password}");
            string ha2 = Md5($"OPTIONS:{uri}");
            string response = Md5($"{ha1}:{nonce}:{ha2}");
            return $"Digest username=\"{UserName}\", realm=\"{realm}\", nonce=\"{nonce}\", uri=\"{uri}\", response=\"{response}\"";
        }

        private static string Field(string text, string name)
        {
            var match = System.Text.RegularExpressions.Regex.Match(text, name + "=\"([^\"]+)\"");
            return match.Success ? match.Groups[1].Value : null;
        }

        [TestMethod]
        public void ACapturedAuthorizationHeaderStopsWorkingOnceTheNonceHasRotated()
        {
            // A second, not a fraction of one. The first assertion below has to land inside the
            // nonce's own window, and under a loaded test run the round trip to get the challenge is
            // not as quick as it looks.
            var lifetime = TimeSpan.FromSeconds(1);

            int port = TestPorts.FindFree();
            using var server = NewServer(port, lifetime);

            string uri = "rtsp://127.0.0.1:" + port + "/stream1";

            // learn the challenge, then build the header a well behaved client would send
            var challenged = SendRaw(port, uri, null);
            Assert.AreEqual(401, challenged.StatusCode);

            string authorization = BuildDigest(Field(challenged.Challenge, "realm"), Field(challenged.Challenge, "nonce"), uri);
            Assert.AreEqual(200, SendRaw(port, uri, authorization).StatusCode, "the header should work while its nonce is current");

            // Several rotations, so the captured nonce is well out of the grace window. It used to be
            // valid for as long as the server ran.
            Thread.Sleep(lifetime + lifetime + lifetime + TimeSpan.FromMilliseconds(500));

            Assert.AreEqual(401, SendRaw(port, uri, authorization).StatusCode,
                "a replayed header should be refused once its nonce has rotated out");
        }

        [TestMethod]
        public void AHeaderBuiltAgainstTheNonceJustRotatedOutIsStillAccepted()
        {
            // Deliberately unhurried. The point is to land after exactly one rotation, and a short
            // lifetime would make that a race against however long the handshake below takes.
            var lifetime = TimeSpan.FromSeconds(2);

            int port = TestPorts.FindFree();
            using var server = NewServer(port, lifetime);

            string uri = "rtsp://127.0.0.1:" + port + "/stream1";

            var challenged = SendRaw(port, uri, null);
            string authorization = BuildDigest(Field(challenged.Challenge, "realm"), Field(challenged.Challenge, "nonce"), uri);

            // One rotation. A request already on its way when the nonce turns over must not be failed.
            Thread.Sleep(lifetime + TimeSpan.FromMilliseconds(600));

            Assert.AreEqual(200, SendRaw(port, uri, authorization).StatusCode,
                "the nonce just rotated out is still within its grace period");
        }

        [TestMethod]
        public void AClientThatAnswersTheNewChallengeGetsBackIn()
        {
            var lifetime = TimeSpan.FromSeconds(1);

            int port = TestPorts.FindFree();
            using var server = NewServer(port, lifetime);

            string uri = "rtsp://127.0.0.1:" + port + "/stream1";
            Thread.Sleep(lifetime + lifetime + lifetime);

            // a client meeting the server for the first time after several rotations
            using var client = new RtspTestClient(port, UserName, Password);
            Assert.AreEqual(200, client.Send("OPTIONS", uri).StatusCode);
        }

        [TestMethod]
        public void RotationIsOffWhenNoCredentialsAreConfigured()
        {
            int port = TestPorts.FindFree();
            var server = new RTSPServer(port, null, null) { NonceLifetime = TimeSpan.FromMilliseconds(200) };
            server.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), null));
            server.StartListen();

            using (server)
            {
                Thread.Sleep(600);

                string uri = "rtsp://127.0.0.1:" + port + "/stream1";
                Assert.AreEqual(200, SendRaw(port, uri, null).StatusCode, "an open server should stay open");
            }
        }
    }
}
