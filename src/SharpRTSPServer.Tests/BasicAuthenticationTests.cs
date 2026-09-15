using System;
using System.Text;
using SharpRTSPServer;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// Basic is opt-in. It exists for clients and hardware decoders that cannot do Digest, and it
    /// sends the password in a reversible form, so it must never be what a default server offers.
    /// </summary>
    public sealed class BasicAuthenticationTests : IDisposable
    {
        private const string UserName = "admin";
        private const string Password = "password";
        private const string StreamId = "stream1";

        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        private readonly RTSPServer _server;
        private readonly int _port;

        private string BaseUri => $"rtsp://127.0.0.1:{_port}/{StreamId}";

        public BasicAuthenticationTests()
        {
            _port = TestPorts.FindFree();
            _server = new RTSPServer(_port, UserName, Password);
            _server.AuthenticationScheme = RtspAuthenticationScheme.Basic;
            _server.AddStreamSource(new RTSPStreamSource(StreamId, new H264Track(Sps, Pps), null));
            _server.StartListen();
        }

        private static string BasicHeader(string user, string password) =>
            "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));

        [Fact]
        public void TheSchemeIsReportedBack()
        {
            Assert.Equal(RtspAuthenticationScheme.Basic, _server.AuthenticationScheme);
        }

        [Fact]
        public void TheChallengeIsBasic()
        {
            using var client = new RtspTestClient(_port, null, null);

            var response = client.Send("OPTIONS", BaseUri);
            string challenge = response.Match("WWW-Authenticate:\\s*(.+)");

            Assert.Equal(401, response.StatusCode);
            Assert.StartsWith("Basic", challenge, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("realm=", challenge, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void CorrectBasicCredentialsAreAccepted()
        {
            using var client = new RtspTestClient(_port, null, null);

            var response = client.Send("DESCRIBE", BaseUri,
                "Accept: application/sdp", BasicHeader(UserName, Password));

            Assert.Equal(200, response.StatusCode);
        }

        [Theory]
        [InlineData(UserName, "the-wrong-password")]
        [InlineData("the-wrong-user", Password)]
        public void WrongBasicCredentialsAreRejected(string user, string password)
        {
            using var client = new RtspTestClient(_port, null, null);

            var response = client.Send("DESCRIBE", BaseUri,
                "Accept: application/sdp", BasicHeader(user, password));

            Assert.Equal(401, response.StatusCode);
        }

        [Fact]
        public void ABasicSessionWorksEndToEnd()
        {
            using var client = new RtspTestClient(_port, null, null);
            string auth = BasicHeader(UserName, Password);

            Assert.Equal(200, client.Send("DESCRIBE", BaseUri, "Accept: application/sdp", auth).StatusCode);

            var setup = client.Send("SETUP", $"{BaseUri}/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1", auth);
            Assert.Equal(200, setup.StatusCode);
            Assert.False(string.IsNullOrEmpty(setup.Session));

            Assert.Equal(200, client.Send("PLAY", BaseUri, $"Session: {setup.Session}", auth).StatusCode);
        }

        [Fact]
        public void SwitchingSchemeChangesTheChallenge()
        {
            using (var basicClient = new RtspTestClient(_port, null, null))
            {
                Assert.StartsWith("Basic", basicClient.Send("OPTIONS", BaseUri).Match("WWW-Authenticate:\\s*(.+)"),
                    StringComparison.OrdinalIgnoreCase);
            }

            _server.AuthenticationScheme = RtspAuthenticationScheme.Digest;

            using (var digestClient = new RtspTestClient(_port, null, null))
            {
                Assert.StartsWith("Digest", digestClient.Send("OPTIONS", BaseUri).Match("WWW-Authenticate:\\s*(.+)"),
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void WithoutCredentialsConfiguredTheServerStaysOpenWhicheverSchemeIsAsked()
        {
            int port = TestPorts.FindFree();

            using var open = new RTSPServer(port, null, null);
            open.AuthenticationScheme = RtspAuthenticationScheme.Basic;
            open.AddStreamSource(new RTSPStreamSource(StreamId, new H264Track(Sps, Pps), null));
            open.StartListen();

            using var client = new RtspTestClient(port, null, null);

            // no credentials means no challenge, regardless of the scheme selected
            Assert.Equal(200, client.Send("OPTIONS", $"rtsp://127.0.0.1:{port}/{StreamId}").StatusCode);
        }

        public void Dispose()
        {
            _server.Dispose();
        }
    }
}
