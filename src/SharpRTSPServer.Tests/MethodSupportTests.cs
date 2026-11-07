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

using System.Collections.Generic;
using System.Linq;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// What the server says it answers, and what it does with everything else.
    /// </summary>
    /// <remarks>
    /// The list in the OPTIONS reply used to be the transport's default, naming ANNOUNCE,
    /// SET_PARAMETER and REDIRECT. None of the three was handled, and the request fell out of the
    /// dispatch without a reply, so a client that believed the list waited on an answer that was
    /// never coming.
    /// </remarks>
    [TestClass]
    public sealed class MethodSupportTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        private static RTSPServer NewServer(int port)
        {
            var server = new RTSPServer(port, new InMemoryUserRepository("admin", "password"));
            server.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), null));
            server.StartListen();
            return server;
        }

        [TestMethod]
        public void OptionsNamesOnlyTheMethodsThatAreAnswered()
        {
            int port = TestPorts.FindFree();
            using var server = NewServer(port);
            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            using var client = new RtspTestClient(port, "admin", "password");
            var options = client.Send("OPTIONS", baseUri);

            Assert.AreEqual(200, options.StatusCode);

            string advertised = options.Match(@"Public:\s*([^\r\n]+)");
            Assert.IsNotNull(advertised, "OPTIONS should say what the server supports");

            var named = advertised.Split(',').Select(m => m.Trim()).Where(m => m.Length > 0).ToArray();

            CollectionAssert.AreEquivalent(
                new[] { "OPTIONS", "DESCRIBE", "SETUP", "PLAY", "PAUSE", "GET_PARAMETER", "SET_PARAMETER", "TEARDOWN" },
                named,
                "the advertised list should be exactly what is implemented, and it was: " + advertised);
        }

        [TestMethod]
        public void SetParameterIsAnsweredAsAKeepAlive()
        {
            int port = TestPorts.FindFree();
            using var server = NewServer(port);
            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");

            // ONVIF clients keep a session alive with this one rather than GET_PARAMETER
            var kept = client.Send("SET_PARAMETER", baseUri, $"Session: {setup.Session}");

            Assert.AreEqual(200, kept.StatusCode, "an empty SET_PARAMETER is a keepalive and should be answered");
        }

        [TestMethod]
        public void AMethodThatIsNotImplementedIsRefusedRatherThanIgnored()
        {
            int port = TestPorts.FindFree();
            using var server = NewServer(port);
            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);

            // This one is in the transport's default list and has never been implemented here. The
            // point is the reply, whatever it says - silence leaves the client waiting on its own
            // timeout with nothing to act on.
            var announced = client.Send("ANNOUNCE", baseUri, "Content-Type: application/sdp");

            Assert.AreEqual(501, announced.StatusCode, "an unimplemented method should be refused, not dropped");
        }

        [TestMethod]
        public void TheMethodsThatAreAdvertisedAreAllAnswered()
        {
            int port = TestPorts.FindFree();
            using var server = NewServer(port);
            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");
            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");

            string session = setup.Session;

            // everything the list promises, in an order a client could actually use
            var answered = new List<string>();

            foreach (var (method, headers) in new[]
            {
                ("PLAY", "Session: " + session),
                ("GET_PARAMETER", "Session: " + session),
                ("SET_PARAMETER", "Session: " + session),
                ("PAUSE", "Session: " + session),
                ("TEARDOWN", "Session: " + session),
            })
            {
                var response = client.Send(method, baseUri, headers);
                answered.Add($"{method}={response.StatusCode}");

                Assert.AreEqual(200, response.StatusCode, $"{method} is advertised, so it should be answered");
            }

            Assert.HasCount(5, answered);
        }
    }
}
