using System;
using System.Collections.Generic;
using System.Linq;
using SharpRTSPServer;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// Drives a real server over a socket to check the session handling rules that keep one client
    /// from reaching into another client's session.
    /// </summary>
    public sealed class RtspSessionSecurityTests : IDisposable
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
            _server = new RTSPServer(_port, UserName, Password);
            _server.AddStreamSource(new RTSPStreamSource(StreamId, new H264Track(Sps, Pps), null));
            _server.StartListen();
        }

        private RtspTestClient Connect() => new RtspTestClient(_port, UserName, Password);

        /// <summary>
        /// Brings a client all the way to an established session and returns its session ID.
        /// </summary>
        private string EstablishSession(RtspTestClient client)
        {
            Assert.Equal(200, client.Send("OPTIONS", BaseUri).StatusCode);
            Assert.Equal(200, client.Send("DESCRIBE", BaseUri, "Accept: application/sdp").StatusCode);

            var setup = client.Send("SETUP", TrackUri, TcpTransport);
            Assert.Equal(200, setup.StatusCode);
            Assert.False(string.IsNullOrEmpty(setup.Session));
            return setup.Session;
        }

        [Fact]
        public void AnotherConnectionCannotTearDownOurSession()
        {
            using var victim = Connect();
            string session = EstablishSession(victim);

            using var attacker = Connect();
            attacker.Send("OPTIONS", BaseUri);

            // the attacker knows the session ID but it does not belong to their connection
            Assert.Equal(454, attacker.Send("TEARDOWN", BaseUri, $"Session: {session}").StatusCode);

            // and the victim's session is untouched
            Assert.Equal(200, victim.Send("PLAY", BaseUri, $"Session: {session}").StatusCode);
        }

        [Fact]
        public void AnotherConnectionCannotControlOurSession()
        {
            using var victim = Connect();
            string session = EstablishSession(victim);

            using var attacker = Connect();
            attacker.Send("OPTIONS", BaseUri);

            Assert.Equal(454, attacker.Send("PLAY", BaseUri, $"Session: {session}").StatusCode);
            Assert.Equal(454, attacker.Send("PAUSE", BaseUri, $"Session: {session}").StatusCode);
            Assert.Equal(454, attacker.Send("GET_PARAMETER", BaseUri, $"Session: {session}").StatusCode);
        }

        [Fact]
        public void OwnSessionIsAcceptedOnTheConnectionThatCreatedIt()
        {
            using var client = Connect();
            string session = EstablishSession(client);

            Assert.Equal(200, client.Send("PLAY", BaseUri, $"Session: {session}").StatusCode);
            Assert.Equal(200, client.Send("PAUSE", BaseUri, $"Session: {session}").StatusCode);
            Assert.Equal(200, client.Send("TEARDOWN", BaseUri, $"Session: {session}").StatusCode);
        }

        [Fact]
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
            Assert.Equal(sessions.Count, sessions.Distinct().Count());

            foreach (string session in sessions)
            {
                // wide enough to not be enumerable
                Assert.True(session.Length >= 16, $"session ID '{session}' is too short to be unguessable");

                // and not a counter - the IDs used to be 1, 2, 3, ...
                Assert.False(int.TryParse(session, out _), $"session ID '{session}' is a plain number");
            }
        }

        [Fact]
        public void SessionIdIsUnknownToTheServerAfterTeardown()
        {
            using var client = Connect();
            string session = EstablishSession(client);

            Assert.Equal(200, client.Send("TEARDOWN", BaseUri, $"Session: {session}").StatusCode);

            using var later = Connect();
            later.Send("OPTIONS", BaseUri);
            Assert.Equal(454, later.Send("PLAY", BaseUri, $"Session: {session}").StatusCode);
        }

        [Fact]
        public void RequestWithoutCredentialsIsChallenged()
        {
            using var anonymous = new RtspTestClient(_port, null, null);

            var response = anonymous.Send("DESCRIBE", BaseUri, "Accept: application/sdp");

            Assert.Equal(401, response.StatusCode);
            Assert.Contains("WWW-Authenticate", response.Headers, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void DigestIsTheDefaultScheme()
        {
            Assert.Equal(RtspAuthenticationScheme.Digest, _server.AuthenticationScheme);
        }

        [Fact]
        public void TheDefaultChallengeIsDigestAndNotBasic()
        {
            using var anonymous = new RtspTestClient(_port, null, null);

            var response = anonymous.Send("DESCRIBE", BaseUri, "Accept: application/sdp");
            string challenge = response.Match("WWW-Authenticate:\\s*(.+)");

            // Basic would put the password on the wire in a trivially reversible form
            Assert.StartsWith("Digest", challenge, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Basic", challenge, StringComparison.OrdinalIgnoreCase);

            // and the challenge has to carry a realm and a nonce for the client to respond to
            Assert.Contains("realm=", challenge, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("nonce=", challenge, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void BasicCredentialsAreNotAccepted()
        {
            using var client = new RtspTestClient(_port, null, null);

            string basic = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{UserName}:{Password}"));
            var response = client.Send("DESCRIBE", BaseUri,
                "Accept: application/sdp", $"Authorization: Basic {basic}");

            // even with the right user name and password, Basic is refused
            Assert.Equal(401, response.StatusCode);
        }

        [Fact]
        public void WrongDigestCredentialsAreRejected()
        {
            using var client = new RtspTestClient(_port, UserName, "the-wrong-password");

            Assert.Equal(401, client.Send("DESCRIBE", BaseUri, "Accept: application/sdp").StatusCode);
        }

        [Fact]
        public void EachServerUsesADifferentNonce()
        {
            using var first = new RtspTestClient(_port, null, null);
            string firstChallenge = first.Send("OPTIONS", BaseUri).Match("nonce=\"([^\"]+)\"");

            int otherPort = TestPorts.FindFree();
            using (var otherServer = new RTSPServer(otherPort, UserName, Password))
            {
                otherServer.AddStreamSource(new RTSPStreamSource(StreamId, new H264Track(Sps, Pps), null));
                otherServer.StartListen();

                using var second = new RtspTestClient(otherPort, null, null);
                string secondChallenge = second.Send("OPTIONS", $"rtsp://127.0.0.1:{otherPort}/{StreamId}").Match("nonce=\"([^\"]+)\"");

                Assert.False(string.IsNullOrEmpty(firstChallenge));
                Assert.NotEqual(firstChallenge, secondChallenge);
                Assert.True(firstChallenge.Length >= 16, "the nonce must be wide enough to not be guessable");
            }
        }

        [Fact]
        public void RequestForAnUnknownStreamIsNotFound()
        {
            using var client = Connect();
            client.Send("OPTIONS", BaseUri);

            Assert.Equal(404, client.Send("DESCRIBE", $"rtsp://127.0.0.1:{_port}/no-such-stream",
                "Accept: application/sdp").StatusCode);
        }

        [Fact]
        public void SetupForAnUnknownTrackIsNotFound()
        {
            using var client = Connect();
            client.Send("OPTIONS", BaseUri);
            client.Send("DESCRIBE", BaseUri, "Accept: application/sdp");

            Assert.Equal(404, client.Send("SETUP", $"{BaseUri}/trackID=9", TcpTransport).StatusCode);
        }

        [Fact]
        public void DescribeReturnsAnSdpDescribingTheTrack()
        {
            using var client = Connect();
            client.Send("OPTIONS", BaseUri);

            var describe = client.Send("DESCRIBE", BaseUri, "Accept: application/sdp");

            Assert.Equal(200, describe.StatusCode);
            Assert.Contains("application/sdp", describe.Headers, StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
            _server.Dispose();
        }
    }
}
