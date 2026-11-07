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
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// The bounds that keep a client - or something that is not one - from costing the server more
    /// than it should.
    /// </summary>
    [TestClass]
    public sealed class HardeningTests
    {
        private const string UserName = "admin";
        private const string Password = "password";

        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        [TestMethod]
        public void RepeatedWrongPasswordsFromOneAddressAreAnsweredSlowly()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, new InMemoryUserRepository(UserName, Password))
            {
                FailedAuthenticationsBeforeDelay = 2,
                FailedAuthenticationDelay = TimeSpan.FromSeconds(2),
            };
            server.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), null));
            server.StartListen();

            string uri = $"rtsp://127.0.0.1:{port}/stream1";

            // The first couple are free: an honest client that mistyped its password should not be
            // punished for it.
            for (int i = 0; i < 2; i++)
            {
                using var early = new RtspTestClient(port, UserName, "wrong");
                Assert.AreEqual(401, early.Send("OPTIONS", uri).StatusCode);
            }

            var stopwatch = Stopwatch.StartNew();
            using (var late = new RtspTestClient(port, UserName, "wrong"))
            {
                Assert.AreEqual(401, late.Send("OPTIONS", uri).StatusCode);
            }
            stopwatch.Stop();

            Assert.IsGreaterThanOrEqualTo(1500, stopwatch.ElapsedMilliseconds,
                "past the threshold a wrong password should cost the guesser the delay");
        }

        [TestMethod]
        public void GettingThePasswordRightClearsTheCountAgain()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, new InMemoryUserRepository(UserName, Password))
            {
                FailedAuthenticationsBeforeDelay = 1,
                FailedAuthenticationDelay = TimeSpan.FromSeconds(3),
            };
            server.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), null));
            server.StartListen();

            string uri = $"rtsp://127.0.0.1:{port}/stream1";

            using (var wrong = new RtspTestClient(port, UserName, "wrong"))
            {
                Assert.AreEqual(401, wrong.Send("OPTIONS", uri).StatusCode);
            }

            // One success, which should forget the failure above.
            using (var right = new RtspTestClient(port, UserName, Password))
            {
                Assert.AreEqual(200, right.Send("OPTIONS", uri).StatusCode);
            }

            var stopwatch = Stopwatch.StartNew();
            using (var alsoRight = new RtspTestClient(port, UserName, Password))
            {
                Assert.AreEqual(200, alsoRight.Send("OPTIONS", uri).StatusCode);
            }
            stopwatch.Stop();

            // A wide margin deliberately: the delay being checked for is three seconds, and this
            // shares a machine with the rest of the suite, so a tight bound here would flake for
            // that reason rather than for a real one.
            Assert.IsLessThan(1500, stopwatch.ElapsedMilliseconds,
                "a client that got it right should not be answered slowly afterwards");
        }

        [TestMethod]
        public void MulticastIsOffUnlessItIsAskedFor()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, new InMemoryUserRepository(UserName, Password));

            Assert.IsFalse(server.MulticastEnabled,
                "a group publishes the stream to the whole segment, so it is not something to do by default");
        }

        [TestMethod]
        public void ADatagramFromSomewhereElseIsNotTakenAsTheClients()
        {
            using var transport = new RtpUdpTransport(TestPorts.FindFree(), IPEndPoint.MaxPort, AddressFamily.InterNetwork);

            // This session's media goes to an address nothing here will send from.
            transport.SetControlDestination("203.0.113.5", 5001);

            int seen = 0;
            transport.ControlReceived += (sender, e) => { using (e.Data) { Interlocked.Increment(ref seen); } };
            transport.Start();

            using (var impostor = new UdpClient(AddressFamily.InterNetwork))
            {
                byte[] rtcp = { 0x80, 0xC9, 0x00, 0x01, 0x11, 0x22, 0x33, 0x44 };
                impostor.Send(rtcp, rtcp.Length, new IPEndPoint(IPAddress.Loopback, transport.ControlPort));
            }

            Thread.Sleep(300);
            Assert.AreEqual(0, Volatile.Read(ref seen),
                "a datagram from an address this session does not send to should be discarded");
        }

        [TestMethod]
        public void ADatagramFromTheClientItselfIsStillDelivered()
        {
            using var transport = new RtpUdpTransport(TestPorts.FindFree(), IPEndPoint.MaxPort, AddressFamily.InterNetwork);

            using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            int clientPort = ((IPEndPoint)client.Client.LocalEndPoint).Port;

            // Where this session's RTCP goes is where its reports are expected to come from.
            transport.SetControlDestination("127.0.0.1", clientPort);

            var arrived = new ManualResetEventSlim(false);
            transport.ControlReceived += (sender, e) => { using (e.Data) { arrived.Set(); } };
            transport.Start();

            byte[] rtcp = { 0x80, 0xC9, 0x00, 0x01, 0x11, 0x22, 0x33, 0x44 };
            client.Send(rtcp, rtcp.Length, new IPEndPoint(IPAddress.Loopback, transport.ControlPort));

            Assert.IsTrue(arrived.Wait(TimeSpan.FromSeconds(5)),
                "the client's own reports still have to get through");
        }

        [TestMethod]
        public void AReportFromAnotherPortOnTheClientIsStillDelivered()
        {
            using var transport = new RtpUdpTransport(TestPorts.FindFree(), IPEndPoint.MaxPort, AddressFamily.InterNetwork);

            // Told to send to one port on the client, and answered from a different one - which
            // RFC 3550 allows and many receivers do.
            transport.SetControlDestination("127.0.0.1", 5001);

            var arrived = new ManualResetEventSlim(false);
            transport.ControlReceived += (sender, e) => { using (e.Data) { arrived.Set(); } };
            transport.Start();

            using (var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                byte[] rtcp = { 0x80, 0xC9, 0x00, 0x01, 0x11, 0x22, 0x33, 0x44 };
                client.Send(rtcp, rtcp.Length, new IPEndPoint(IPAddress.Loopback, transport.ControlPort));
            }

            Assert.IsTrue(arrived.Wait(TimeSpan.FromSeconds(5)),
                "it is the address that is matched, not the port");
        }

        [TestMethod]
        [DataRow("npt=1e300-")]
        [DataRow("npt=2147483647:0:0-")]
        [DataRow("npt=99999999999999999999-")]
        public void ARangeTooLargeToHoldIsRefusedRatherThanThrown(string header)
        {
            // TryParse says yes or no; it used to overflow building the TimeSpan and throw out of a
            // method whose whole contract is that it does not.
            Assert.IsFalse(NptRange.TryParse(header, out _), header);
        }

        [TestMethod]
        public void AnOutlandishRangeIsAnsweredRatherThanFailing()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, new InMemoryUserRepository(UserName, Password));
            server.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), null));
            server.StartListen();

            string uri = $"rtsp://127.0.0.1:{port}/stream1";

            using var client = new RtspTestClient(port, UserName, Password);
            Assert.AreEqual(200, client.Send("DESCRIBE", uri, "Accept: application/sdp").StatusCode);

            var setup = client.Send("SETUP", uri + "/trackID=0", "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            Assert.AreEqual(200, setup.StatusCode);

            var play = client.Send("PLAY", uri, "Session: " + setup.Session, "Range: npt=1e300-");

            // 456 is what this server says to a range it cannot play from. The point is that it is
            // an answer at all, rather than the 500 an exception used to produce.
            Assert.AreEqual(456, play.StatusCode);
        }
    }
}
