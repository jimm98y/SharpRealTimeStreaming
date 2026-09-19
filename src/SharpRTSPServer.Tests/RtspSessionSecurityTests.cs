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
using System.Linq;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// Drives a real server over a socket to check the session handling rules that keep one client
    /// from reaching into another client's session.
    /// </summary>
    [TestClass]
    public sealed class RtspSessionSecurityTests
    {
        private const string UserName = "admin";
        private const string Password = "password";
        private const string StreamId = "stream1";

        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        private const string TcpTransport = "Transport: RTP/AVP/TCP;unicast;interleaved=0-1";

        private readonly RTSPServer _server;
        private readonly int _port;

        private string BaseUri => $"rtsp://127.0.0.1:{_port}/{StreamId}";
        private string TrackUri => $"{BaseUri}/trackID=0";

        public RtspSessionSecurityTests()
        {
            _port = TestPorts.FindFree();
            _server = new RTSPServer(_port, new InMemoryUserRepository(UserName, Password));
            _server.AddStreamSource(new RTSPStreamSource(StreamId, new H264Track(Sps, Pps), null));
            _server.StartListen();
        }

        private RtspTestClient Connect() => new RtspTestClient(_port, UserName, Password);

        /// <summary>
        /// Brings a client all the way to an established session and returns its session ID.
        /// </summary>
        private string EstablishSession(RtspTestClient client)
        {
            Assert.AreEqual(200, client.Send("OPTIONS", BaseUri).StatusCode);
            Assert.AreEqual(200, client.Send("DESCRIBE", BaseUri, "Accept: application/sdp").StatusCode);

            var setup = client.Send("SETUP", TrackUri, TcpTransport);
            Assert.AreEqual(200, setup.StatusCode);
            Assert.IsFalse(string.IsNullOrEmpty(setup.Session));
            return setup.Session;
        }

        [TestMethod]
        public void AnotherConnectionCannotTearDownOurSession()
        {
            using var victim = Connect();
            string session = EstablishSession(victim);

            using var attacker = Connect();
            attacker.Send("OPTIONS", BaseUri);

            // the attacker knows the session ID but it does not belong to their connection
            Assert.AreEqual(454, attacker.Send("TEARDOWN", BaseUri, $"Session: {session}").StatusCode);

            // and the victim's session is untouched
            Assert.AreEqual(200, victim.Send("PLAY", BaseUri, $"Session: {session}").StatusCode);
        }

        [TestMethod]
        public void AnotherConnectionCannotControlOurSession()
        {
            using var victim = Connect();
            string session = EstablishSession(victim);

            using var attacker = Connect();
            attacker.Send("OPTIONS", BaseUri);

            Assert.AreEqual(454, attacker.Send("PLAY", BaseUri, $"Session: {session}").StatusCode);
            Assert.AreEqual(454, attacker.Send("PAUSE", BaseUri, $"Session: {session}").StatusCode);
            Assert.AreEqual(454, attacker.Send("GET_PARAMETER", BaseUri, $"Session: {session}").StatusCode);
        }

        [TestMethod]
        public void OwnSessionIsAcceptedOnTheConnectionThatCreatedIt()
        {
            using var client = Connect();
            string session = EstablishSession(client);

            Assert.AreEqual(200, client.Send("PLAY", BaseUri, $"Session: {session}").StatusCode);
            Assert.AreEqual(200, client.Send("PAUSE", BaseUri, $"Session: {session}").StatusCode);
            Assert.AreEqual(200, client.Send("TEARDOWN", BaseUri, $"Session: {session}").StatusCode);
        }

        [TestMethod]
        public void SessionIdsAreNotGuessable()
        {
            var sessions = new List<string>();
            var clients = new List<RtspTestClient>();

            try
            {
                for (int i = 0; i < 5; i++)
                {
                    var client = Connect();
                    clients.Add(client);
                    sessions.Add(EstablishSession(client));
                }
            }
            finally
            {
                foreach (var client in clients) client.Dispose();
            }

            // all distinct
            Assert.AreEqual(sessions.Count, sessions.Distinct().Count());

            foreach (string session in sessions)
            {
                // wide enough to not be enumerable
                Assert.IsGreaterThanOrEqualTo(16, session.Length, $"session ID '{session}' is too short to be unguessable");

                // and not a counter - the IDs used to be 1, 2, 3, ...
                Assert.IsFalse(int.TryParse(session, out _), $"session ID '{session}' is a plain number");
            }
        }

        [TestMethod]
        public void SessionIdIsUnknownToTheServerAfterTeardown()
        {
            using var client = Connect();
            string session = EstablishSession(client);

            Assert.AreEqual(200, client.Send("TEARDOWN", BaseUri, $"Session: {session}").StatusCode);

            using var later = Connect();
            later.Send("OPTIONS", BaseUri);
            Assert.AreEqual(454, later.Send("PLAY", BaseUri, $"Session: {session}").StatusCode);
        }

        [TestMethod]
        public void RequestWithoutCredentialsIsChallenged()
        {
            using var anonymous = new RtspTestClient(_port, null, null);

            var response = anonymous.Send("DESCRIBE", BaseUri, "Accept: application/sdp");

            Assert.AreEqual(401, response.StatusCode);
            StringAssert.Contains(response.Headers, "WWW-Authenticate", StringComparison.OrdinalIgnoreCase);
        }

        [TestMethod]
        public void DigestIsTheDefaultScheme()
        {
            Assert.AreEqual(RtspAuthenticationScheme.Digest, _server.AuthenticationScheme);
        }

        [TestMethod]
        public void TheDefaultChallengeIsDigestAndNotBasic()
        {
            using var anonymous = new RtspTestClient(_port, null, null);

            var response = anonymous.Send("DESCRIBE", BaseUri, "Accept: application/sdp");
            string challenge = response.Match("WWW-Authenticate:\\s*(.+)");

            // Basic would put the password on the wire in a trivially reversible form
            StringAssert.StartsWith(challenge, "Digest", StringComparison.OrdinalIgnoreCase);
            Assert.IsFalse(challenge.Contains("Basic", StringComparison.OrdinalIgnoreCase), "Basic must not be offered");

            // and the challenge has to carry a realm and a nonce for the client to respond to
            StringAssert.Contains(challenge, "realm=", StringComparison.OrdinalIgnoreCase);
            StringAssert.Contains(challenge, "nonce=", StringComparison.OrdinalIgnoreCase);
        }

        [TestMethod]
        public void BasicCredentialsAreNotAccepted()
        {
            using var client = new RtspTestClient(_port, null, null);

            string basic = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{UserName}:{Password}"));
            var response = client.Send("DESCRIBE", BaseUri,
                "Accept: application/sdp", $"Authorization: Basic {basic}");

            // even with the right user name and password, Basic is refused
            Assert.AreEqual(401, response.StatusCode);
        }

        [TestMethod]
        public void WrongDigestCredentialsAreRejected()
        {
            using var client = new RtspTestClient(_port, UserName, "the-wrong-password");

            Assert.AreEqual(401, client.Send("DESCRIBE", BaseUri, "Accept: application/sdp").StatusCode);
        }

        [TestMethod]
        public void EachServerUsesADifferentNonce()
        {
            using var first = new RtspTestClient(_port, null, null);
            string firstChallenge = first.Send("OPTIONS", BaseUri).Match("nonce=\"([^\"]+)\"");

            int otherPort = TestPorts.FindFree();
            using (var otherServer = new RTSPServer(otherPort, new InMemoryUserRepository(UserName, Password)))
            {
                otherServer.AddStreamSource(new RTSPStreamSource(StreamId, new H264Track(Sps, Pps), null));
                otherServer.StartListen();

                using var second = new RtspTestClient(otherPort, null, null);
                string secondChallenge = second.Send("OPTIONS", $"rtsp://127.0.0.1:{otherPort}/{StreamId}").Match("nonce=\"([^\"]+)\"");

                Assert.IsFalse(string.IsNullOrEmpty(firstChallenge));
                Assert.AreNotEqual(firstChallenge, secondChallenge);
                Assert.IsGreaterThanOrEqualTo(16, firstChallenge.Length, "the nonce must be wide enough to not be guessable");
            }
        }

        [TestMethod]
        public void RequestForAnUnknownStreamIsNotFound()
        {
            using var client = Connect();
            client.Send("OPTIONS", BaseUri);

            Assert.AreEqual(404, client.Send("DESCRIBE", $"rtsp://127.0.0.1:{_port}/no-such-stream",
                "Accept: application/sdp").StatusCode);
        }

        [TestMethod]
        [DataRow("Transport: RTP/AVP/TCP;unicast", "TCP without interleaved channels")]
        [DataRow("Transport: RTP/AVP;unicast", "UDP without a client port")]
        [DataRow("Transport: RTP/AVP/TCP", "TCP with nothing else")]
        [DataRow("Transport: nonsense", "an unparseable transport")]
        public void MalformedTransportIsAnsweredRatherThanDropped(string transport, string description)
        {
            using var client = Connect();
            client.Send("OPTIONS", BaseUri);
            client.Send("DESCRIBE", BaseUri, "Accept: application/sdp");

            // These used to dereference null and let the exception escape, so the listener closed the
            // socket and the client was left waiting for a reply that never came.
            var response = client.Send("SETUP", TrackUri, transport);

            Assert.AreEqual(461, response.StatusCode, description);
        }

        [TestMethod]
        public void SetupWithNoTransportHeaderIsAnswered()
        {
            using var client = Connect();
            client.Send("OPTIONS", BaseUri);
            client.Send("DESCRIBE", BaseUri, "Accept: application/sdp");

            Assert.AreEqual(461, client.Send("SETUP", TrackUri).StatusCode);
        }

        [TestMethod]
        public void AConnectionRejectedAfterPlayingIsFullyTornDown()
        {
            using var victim = Connect();
            string session = EstablishSession(victim);
            Assert.AreEqual(200, victim.Send("PLAY", BaseUri, $"Session: {session}").StatusCode);

            // The stream source keeps its own list of playing connections. Dropping one from the
            // connection list alone left it there, streaming to a disposed listener and counting
            // against nothing, so neither the idle sweep nor the connection limit could see it.
            Assert.AreEqual(1, PlayingConnectionCount());

            using (var rejected = new RtspTestClient(_port, UserName, "the-wrong-password"))
            {
                rejected.Send("OPTIONS", BaseUri);
            }

            // the victim is untouched by another connection failing to authenticate
            Assert.AreEqual(1, PlayingConnectionCount());
            Assert.AreEqual(200, victim.Send("GET_PARAMETER", BaseUri, $"Session: {session}").StatusCode);
        }

        private int PlayingConnectionCount()
        {
            _server.CheckTimeouts(StreamId, out _, out int playing);
            return playing;
        }

        [TestMethod]
        public void SetupForAnUnknownTrackIsNotFound()
        {
            using var client = Connect();
            client.Send("OPTIONS", BaseUri);
            client.Send("DESCRIBE", BaseUri, "Accept: application/sdp");

            Assert.AreEqual(404, client.Send("SETUP", $"{BaseUri}/trackID=9", TcpTransport).StatusCode);
        }

        [TestMethod]
        public void DescribeReturnsAnSdpDescribingTheTrack()
        {
            using var client = Connect();
            client.Send("OPTIONS", BaseUri);

            var describe = client.Send("DESCRIBE", BaseUri, "Accept: application/sdp");

            Assert.AreEqual(200, describe.StatusCode);
            StringAssert.Contains(describe.Headers, "application/sdp", StringComparison.OrdinalIgnoreCase);
        }

        [TestCleanup]
        public void Cleanup()
        {
            _server.Dispose();
        }
    }
}
