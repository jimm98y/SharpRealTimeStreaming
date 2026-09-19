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

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// Authentication says who a client is; these are about what it is then allowed to have.
    /// </summary>
    [TestClass]
    public sealed class StreamAuthorizationTests
    {
        private const string UserName = "admin";
        private const string Password = "password";

        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        private const string TcpTransport = "Transport: RTP/AVP/TCP;unicast;interleaved=0-1";

        private readonly RTSPServer _server;
        private readonly int _port;

        public StreamAuthorizationTests()
        {
            _port = TestPorts.FindFree();
            _server = new RTSPServer(_port, UserName, Password);
            _server.AddStreamSource(new RTSPStreamSource("mine", new H264Track(Sps, Pps), null));
            _server.AddStreamSource(new RTSPStreamSource("theirs", new H264Track(Sps, Pps), null));
            _server.StartListen();
        }

        [TestCleanup]
        public void Cleanup() => _server.Dispose();

        private RtspTestClient Connect() => new RtspTestClient(_port, UserName, Password);

        private string UriOf(string stream) => $"rtsp://127.0.0.1:{_port}/{stream}";

        [TestMethod]
        public void WithNoHandlerEveryStreamIsReachable()
        {
            using var client = Connect();

            Assert.AreEqual(200, client.Send("DESCRIBE", UriOf("mine"), "Accept: application/sdp").StatusCode);
            Assert.AreEqual(200, client.Send("DESCRIBE", UriOf("theirs"), "Accept: application/sdp").StatusCode);
        }

        [TestMethod]
        public void AHandlerCanRefuseOneStreamAndAllowAnother()
        {
            _server.AuthorizeStream += (sender, e) =>
            {
                if (e.StreamID == "theirs")
                {
                    e.Deny(403);
                }
            };

            using var client = Connect();

            Assert.AreEqual(200, client.Send("DESCRIBE", UriOf("mine"), "Accept: application/sdp").StatusCode,
                "the stream this client may have");
            Assert.AreEqual(403, client.Send("DESCRIBE", UriOf("theirs"), "Accept: application/sdp").StatusCode,
                "the stream it may not");
        }

        [TestMethod]
        public void TheHandlerIsToldWhoAuthenticated()
        {
            string seenUser = null;
            string seenStream = null;

            _server.AuthorizeStream += (sender, e) =>
            {
                seenUser = e.UserName;
                seenStream = e.StreamID;
            };

            using var client = Connect();
            Assert.AreEqual(200, client.Send("DESCRIBE", UriOf("mine"), "Accept: application/sdp").StatusCode);

            Assert.AreEqual(UserName, seenUser);
            Assert.AreEqual("mine", seenStream);
        }

        [TestMethod]
        public void RefusingCoversSetupAndNotJustDescribe()
        {
            _server.AuthorizeStream += (sender, e) => e.Deny(403);

            using var client = Connect();

            Assert.AreEqual(403, client.Send("SETUP", UriOf("theirs") + "/trackID=0", TcpTransport).StatusCode,
                "a client that skipped DESCRIBE must not get in through SETUP");
        }

        [TestMethod]
        public void ARefusedClientIsNotAttachedToTheStream()
        {
            _server.AuthorizeStream += (sender, e) => e.Deny(403);

            using var client = Connect();
            Assert.AreEqual(403, client.Send("DESCRIBE", UriOf("mine"), "Accept: application/sdp").StatusCode);

            // Being on the stream's list is what would have it sent the media, whatever it was told.
            _server.CheckTimeouts("mine", out int onTheStream, out _);
            Assert.AreEqual(0, onTheStream, "a refused client should never be attached to the stream");
        }

        [TestMethod]
        public void AHandlerThatThrowsRefusesRatherThanAllows()
        {
            _server.AuthorizeStream += (sender, e) => throw new InvalidOperationException("no idea");

            using var client = Connect();

            Assert.AreEqual(500, client.Send("DESCRIBE", UriOf("mine"), "Accept: application/sdp").StatusCode,
                "a handler that cannot decide must not be a way in");
        }

        [TestMethod]
        public void DenyingTwiceKeepsTheFirstRefusal()
        {
            _server.AuthorizeStream += (sender, e) => e.Deny(403);
            _server.AuthorizeStream += (sender, e) => e.Deny(401);

            using var client = Connect();

            Assert.AreEqual(403, client.Send("DESCRIBE", UriOf("mine"), "Accept: application/sdp").StatusCode,
                "one handler must not be able to soften another's refusal");
        }

        [TestMethod]
        public void OptionsIsNotAboutAnyOneStreamSoItIsNotRefused()
        {
            _server.AuthorizeStream += (sender, e) => e.Deny(403);

            using var client = Connect();

            Assert.AreEqual(200, client.Send("OPTIONS", $"rtsp://127.0.0.1:{_port}/").StatusCode,
                "OPTIONS asks what the server can do, not for a stream");
        }
    }
}
