// SharpRTSPClient
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
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace SharpRTSPClient.Tests
{
    /// <summary>
    /// A server that stops answering, stops sending or goes away ends the session with a reason,
    /// rather than leaving the client waiting for ever.
    /// </summary>
    [TestClass]
    public sealed class TimeoutTests
    {
        private const string Sdp =
            "v=0\r\no=- 0 0 IN IP4 0.0.0.0\r\ns=Test\r\nc=IN IP4 0.0.0.0\r\n" +
            "m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 H264/90000\r\n" +
            "a=fmtp:96 packetization-mode=1; sprop-parameter-sets=Z0IAHpZUBaHogA==,aM48gA==\r\n";

        private static RTSPClient NewClient(BlockingCollection<StoppedReason> stops)
        {
            var client = new RTSPClient();
            client.Stopped += (s, e) => stops.Add(e.Reason);
            client.AcceptTrack = t => t.Kind == TrackKind.Video;
            return client;
        }

        private static StoppedReason? WaitForStop(BlockingCollection<StoppedReason> stops, int timeoutMs)
        {
            return stops.TryTake(out StoppedReason reason, timeoutMs) ? reason : (StoppedReason?)null;
        }

        [TestMethod]
        public void AnUnansweredRequestTimesOut()
        {
            using var server = new FakeRtspServer(Sdp) { IgnoreMethod = "DESCRIBE" };
            var stops = new BlockingCollection<StoppedReason>();
            using var client = NewClient(stops);
            client.ResponseTimeout = TimeSpan.FromMilliseconds(500);

            client.Connect(server.BaseUri, RTPTransport.TCP);

            Assert.AreEqual(StoppedReason.ResponseTimeout, WaitForStop(stops, 5000));
            Assert.AreEqual(RTSPClient.RtspStatus.WaitingToConnect, client.GetRtspStatus());
        }

        [TestMethod]
        public void WithoutAResponseTimeoutTheClientKeepsWaiting()
        {
            // the default is the old behaviour: nothing is timed
            using var server = new FakeRtspServer(Sdp) { IgnoreMethod = "DESCRIBE" };
            var stops = new BlockingCollection<StoppedReason>();
            using var client = NewClient(stops);

            client.Connect(server.BaseUri, RTPTransport.TCP);

            Assert.IsTrue(server.WaitForRequest("DESCRIBE"));
            Assert.IsNull(WaitForStop(stops, 1500));
        }

        [TestMethod]
        public void AnsweredRequestsDoNotTimeOut()
        {
            using var server = new FakeRtspServer(Sdp);
            var stops = new BlockingCollection<StoppedReason>();
            using var client = NewClient(stops);
            client.ResponseTimeout = TimeSpan.FromMilliseconds(500);

            client.Connect(server.BaseUri, RTPTransport.TCP);

            Assert.IsTrue(server.WaitForRequest("PLAY"));
            Assert.IsNull(WaitForStop(stops, 1500));
        }

        [TestMethod]
        public void NoMediaAfterPlayTimesOut()
        {
            // the fake server answers PLAY but never sends a packet
            using var server = new FakeRtspServer(Sdp);
            var stops = new BlockingCollection<StoppedReason>();
            using var client = NewClient(stops);
            client.ReceiveTimeout = TimeSpan.FromMilliseconds(750);

            client.Connect(server.BaseUri, RTPTransport.TCP);

            Assert.IsTrue(server.WaitForRequest("PLAY"));
            Assert.AreEqual(StoppedReason.ReceiveTimeout, WaitForStop(stops, 5000));
        }

        [TestMethod]
        public void NoMediaIsExpectedBeforePlay()
        {
            using var server = new FakeRtspServer(Sdp);
            var stops = new BlockingCollection<StoppedReason>();
            using var client = NewClient(stops);
            client.AutoPlay = false;
            client.ReceiveTimeout = TimeSpan.FromMilliseconds(500);

            client.Connect(server.BaseUri, RTPTransport.TCP);

            Assert.IsTrue(server.WaitForRequest("SETUP"));
            Assert.IsNull(WaitForStop(stops, 1500));
        }

        [TestMethod]
        public void APausedSessionDoesNotTimeOut()
        {
            using var server = new FakeRtspServer(Sdp);
            var stops = new BlockingCollection<StoppedReason>();
            using var client = NewClient(stops);
            client.ReceiveTimeout = TimeSpan.FromMilliseconds(1000);

            client.Connect(server.BaseUri, RTPTransport.TCP);
            Assert.IsTrue(server.WaitForRequest("PLAY"));
            client.Pause();

            Assert.IsTrue(server.WaitForRequest("PAUSE"));
            Assert.IsNull(WaitForStop(stops, 2000));
        }

        [TestMethod]
        public void TheServerClosingTheConnectionIsReported()
        {
            using var server = new FakeRtspServer(Sdp) { CloseAfter = "PLAY" };
            var stops = new BlockingCollection<StoppedReason>();
            using var client = NewClient(stops);

            client.Connect(server.BaseUri, RTPTransport.TCP);

            Assert.AreEqual(StoppedReason.ConnectionLost, WaitForStop(stops, 5000));
        }

        [TestMethod]
        public void StoppingOnPurposeIsNotALostConnection()
        {
            using var server = new FakeRtspServer(Sdp);
            var stops = new BlockingCollection<StoppedReason>();
            using var client = NewClient(stops);
            client.ResponseTimeout = TimeSpan.FromMilliseconds(500);
            client.ReceiveTimeout = TimeSpan.FromMilliseconds(500);

            client.Connect(server.BaseUri, RTPTransport.TCP);
            Assert.IsTrue(server.WaitForRequest("PLAY"));
            client.Stop();

            Assert.IsNull(WaitForStop(stops, 1500));
        }

        [TestMethod]
        public void ATlsHandshakeThatNeverFinishesTimesOut()
        {
            // a plain RTSP server never answers a TLS hello, so the rtsps handshake hangs
            using var server = new FakeRtspServer(Sdp);
            var stops = new BlockingCollection<StoppedReason>();
            using var client = NewClient(stops);
            client.ConnectTimeout = TimeSpan.FromMilliseconds(750);

            var elapsed = Stopwatch.StartNew();
            client.Connect($"rtsps://127.0.0.1:{server.Port}/stream1", RTPTransport.TCP);
            elapsed.Stop();

            Assert.AreEqual(StoppedReason.ConnectTimeout, WaitForStop(stops, 0));
            Assert.IsLessThan(TimeSpan.FromSeconds(5), elapsed.Elapsed, "Connect did not honour the timeout");
            Assert.AreEqual(RTSPClient.RtspStatus.ConnectFailed, client.GetRtspStatus());
        }

        [TestMethod]
        public void AConnectThatNeverCompletesTimesOut()
        {
            // an address from TEST-NET-1 (RFC 5737): packets to it should go nowhere
            var stops = new BlockingCollection<StoppedReason>();
            using var client = NewClient(stops);
            client.ConnectTimeout = TimeSpan.FromMilliseconds(750);

            var elapsed = Stopwatch.StartNew();
            client.Connect("rtsp://192.0.2.1:554/stream1", RTPTransport.TCP);
            elapsed.Stop();

            StoppedReason? reason = WaitForStop(stops, 0);
            if (reason == StoppedReason.ConnectionFailed)
            {
                Assert.Inconclusive("This network refused the address outright, so there was nothing to time out.");
            }

            Assert.AreEqual(StoppedReason.ConnectTimeout, reason);
            Assert.IsLessThan(TimeSpan.FromSeconds(5), elapsed.Elapsed, "Connect did not honour the timeout");
        }

        [TestMethod]
        public void AConnectWithATimeoutTriesEachAddressOfAName()
        {
            // localhost is ::1 as well as 127.0.0.1, and the server listens on the second only: the
            // first refuses, the second answers
            using var server = new FakeRtspServer(Sdp);
            var stops = new BlockingCollection<StoppedReason>();
            using var client = NewClient(stops);
            client.ConnectTimeout = TimeSpan.FromSeconds(5);

            client.Connect($"rtsp://localhost:{server.Port}/stream1", RTPTransport.TCP);

            Assert.IsTrue(server.WaitForRequest("PLAY"));
            Assert.IsNull(WaitForStop(stops, 0));
        }

        [TestMethod]
        public void AConnectWithATimeoutReachesAnIPv6Server()
        {
            // a TcpClient made without an address family is IPv4 only on .NET Framework
            if (!System.Net.Sockets.Socket.OSSupportsIPv6)
            {
                Assert.Inconclusive("This machine has no IPv6.");
            }

            using var server = new FakeRtspServer(Sdp, System.Net.IPAddress.IPv6Loopback);
            var stops = new BlockingCollection<StoppedReason>();
            using var client = NewClient(stops);
            client.ConnectTimeout = TimeSpan.FromSeconds(5);

            client.Connect(server.BaseUri, RTPTransport.TCP);

            Assert.IsTrue(server.WaitForRequest("PLAY"));
            Assert.IsNull(WaitForStop(stops, 0));
        }

        [TestMethod]
        public void ARefusedConnectFailsRatherThanTimesOut()
        {
            // a port nothing listens on, of a server that was there and is gone
            int port;
            using (var server = new FakeRtspServer(Sdp))
            {
                port = server.Port;
            }

            var stops = new BlockingCollection<StoppedReason>();
            using var client = NewClient(stops);
            client.ConnectTimeout = TimeSpan.FromSeconds(5);

            var elapsed = Stopwatch.StartNew();
            client.Connect($"rtsp://127.0.0.1:{port}/stream1", RTPTransport.TCP);
            elapsed.Stop();

            Assert.AreEqual(StoppedReason.ConnectionFailed, WaitForStop(stops, 0));
            Assert.IsLessThan(TimeSpan.FromSeconds(4), elapsed.Elapsed, "a refusal was waited out as a timeout");
        }

        [TestMethod]
        public void StoppingWhileATimeoutFiresTearsDownOnceAndReportsAtMostOnce()
        {
            // the watchdog tears down on its timer thread just as Stop() does on another: run it
            // again and again, Stop() falling just before, at or just after the timeout
            for (int i = 0; i < 20; i++)
            {
                using var server = new FakeRtspServer(Sdp) { IgnoreMethod = "DESCRIBE" };
                var stops = new BlockingCollection<StoppedReason>();
                using var client = NewClient(stops);
                client.ResponseTimeout = TimeSpan.FromMilliseconds(300);

                client.Connect(server.BaseUri, RTPTransport.TCP);
                Assert.IsTrue(server.WaitForRequest("DESCRIBE"));
                Thread.Sleep(250 + 10 * (i % 10));
                client.Stop();

                Thread.Sleep(600);
                Assert.IsLessThanOrEqualTo(1, stops.Count, $"run {i}: {string.Join(", ", stops)}");
                Assert.AreEqual(RTSPClient.RtspStatus.WaitingToConnect, client.GetRtspStatus());
            }
        }

        [TestMethod]
        public void AStoppedHandlerMayStopAndConnectAgain()
        {
            // Stopped is raised on the watchdog's thread once the teardown is done, so a handler that
            // calls back into the client from another thread and waits for it, as one handing the
            // work to a UI thread does, finds no lock held
            using var hung = new FakeRtspServer(Sdp) { IgnoreMethod = "DESCRIBE" };
            using var answering = new FakeRtspServer(Sdp);
            using var client = new RTSPClient { ResponseTimeout = TimeSpan.FromMilliseconds(300) };
            client.AcceptTrack = t => t.Kind == TrackKind.Video;

            int handled = 0;
            using var returned = new ManualResetEventSlim();
            client.Stopped += (s, e) =>
            {
                if (Interlocked.Exchange(ref handled, 1) != 0)
                    return;
                // a thread of its own: a task waited for may be run on the waiting thread itself
                var other = new Thread(() =>
                {
                    client.Stop();
                    client.Connect(answering.BaseUri, RTPTransport.TCP);
                });
                other.Start();
                if (other.Join(3000))
                    returned.Set();
            };

            client.Connect(hung.BaseUri, RTPTransport.TCP);
            Assert.IsTrue(returned.Wait(5000), "the Stopped handler never returned");
            Assert.IsTrue(answering.WaitForRequest("PLAY"));
        }

        private static int Count(FakeRtspServer server, string method)
        {
            int count = 0;
            foreach (string sent in server.Requests)
            {
                if (sent == method)
                    count++;
            }
            return count;
        }

        private static bool WaitForCount(FakeRtspServer server, string method, int count, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (Count(server, method) < count)
            {
                if (DateTime.UtcNow > deadline)
                    return false;
                Thread.Sleep(20);
            }
            return true;
        }

        private static bool WaitForPlays(FakeRtspServer server, int plays, int timeoutMs) =>
            WaitForCount(server, "PLAY", plays, timeoutMs);

        /// <summary>Plays from a server, then has the server drop the connection.</summary>
        private static void PlayThenDrop(FakeRtspServer server, RTSPClient client, RTPTransport transport = RTPTransport.TCP)
        {
            client.Connect(server.BaseUri, transport, autoReconnect: true);
            Assert.IsTrue(server.WaitForRequest("PLAY"));
            Thread.Sleep(300); // the reply to PLAY handled: the session is playing
            server.DropConnection();
        }

        [TestMethod]
        public void AutoReconnectSetsTheSessionUpAgain()
        {
            using var server = new FakeRtspServer(Sdp);
            var stops = new BlockingCollection<StoppedReason>();
            using var client = NewClient(stops);

            PlayThenDrop(server, client);

            Assert.IsTrue(WaitForPlays(server, 2, 5000), string.Join(", ", server.Requests));
            Assert.AreEqual(2, server.Connections);
            Assert.IsNull(WaitForStop(stops, 500));
        }

        [TestMethod]
        public void AutoReconnectReachesAnIPv6Server()
        {
            // the reconnect is made as the first connection is, IPv6 alike - SharpRTSP's own was
            // IPv4 only on .NET Framework
            using var server = new FakeRtspServer(Sdp, System.Net.IPAddress.IPv6Loopback);
            var stops = new BlockingCollection<StoppedReason>();
            using var client = NewClient(stops);

            PlayThenDrop(server, client);

            Assert.IsTrue(WaitForPlays(server, 2, 5000), string.Join(", ", server.Requests));
            Assert.IsNull(WaitForStop(stops, 500));
        }

        [TestMethod]
        public void AFailedReconnectIsReported()
        {
            var server = new FakeRtspServer(Sdp);
            var stops = new BlockingCollection<StoppedReason>();
            using var client = NewClient(stops);

            client.Connect(server.BaseUri, RTPTransport.TCP, autoReconnect: true);
            Assert.IsTrue(server.WaitForRequest("PLAY"));
            Thread.Sleep(300);
            server.Dispose(); // gone, and nothing listens any more

            Assert.AreEqual(StoppedReason.ConnectionFailed, WaitForStop(stops, 5000));
        }

        [TestMethod]
        public void AutoReconnectDoesNotReconnectASessionThatNeverPlayed()
        {
            // a server that drops every connection during the handshake would be reconnected to
            // for ever
            using var server = new FakeRtspServer(Sdp) { CloseAfter = "DESCRIBE" };
            var stops = new BlockingCollection<StoppedReason>();
            using var client = NewClient(stops);

            client.Connect(server.BaseUri, RTPTransport.TCP, autoReconnect: true);

            Assert.AreEqual(StoppedReason.ConnectionLost, WaitForStop(stops, 5000));
            Thread.Sleep(500);
            Assert.AreEqual(1, server.Connections);
        }

        [TestMethod]
        public void StoppingAsTheConnectionIsLostStaysStopped()
        {
            foreach (RTPTransport transport in new[] { RTPTransport.TCP, RTPTransport.UDP })
            {
                using var server = new FakeRtspServer(Sdp);
                var stops = new BlockingCollection<StoppedReason>();
                using var client = NewClient(stops);

                PlayThenDrop(server, client, transport);
                client.Stop();

                Thread.Sleep(1000);
                Assert.AreEqual(1, server.Connections, transport.ToString());
                Assert.AreEqual(RTSPClient.RtspStatus.WaitingToConnect, client.GetRtspStatus(), transport.ToString());
                Assert.IsNull(WaitForStop(stops, 0), transport.ToString());
            }
        }

        [TestMethod]
        public void AUdpSessionCarriesOnOverANewConnection()
        {
            // the media did not come over the connection, and the server still has the session:
            // asked after on a new connection, it carries on - no handshake, no tracks again
            using var server = new FakeRtspServer(Sdp);
            var stops = new BlockingCollection<StoppedReason>();
            using var client = NewClient(stops);
            int tracks = 0;
            client.NewTrack += (s, e) => Interlocked.Increment(ref tracks);

            PlayThenDrop(server, client, RTPTransport.UDP);

            Assert.IsTrue(WaitForCount(server, "GET_PARAMETER", 1, 5000), string.Join(", ", server.Requests));
            Thread.Sleep(500);
            Assert.AreEqual(2, server.Connections);
            Assert.AreEqual(1, Count(server, "DESCRIBE"), string.Join(", ", server.Requests));
            Assert.AreEqual(1, Count(server, "PLAY"));
            Assert.AreEqual(1, tracks);
            Assert.AreEqual(RTSPClient.RtspStatus.Connected, client.GetRtspStatus());
            Assert.IsNull(WaitForStop(stops, 0));
        }

        [TestMethod]
        public void AUdpSessionTheServerForgotIsSetUpAnew()
        {
            using var server = new FakeRtspServer(Sdp) { ForgetSessions = true };
            var stops = new BlockingCollection<StoppedReason>();
            using var client = NewClient(stops);
            int tracks = 0;
            client.NewTrack += (s, e) => Interlocked.Increment(ref tracks);

            PlayThenDrop(server, client, RTPTransport.UDP);

            Assert.IsTrue(WaitForPlays(server, 2, 5000), string.Join(", ", server.Requests));
            Assert.AreEqual(1, Count(server, "GET_PARAMETER"), string.Join(", ", server.Requests));
            Assert.AreEqual(2, Count(server, "DESCRIBE"));
            Assert.AreEqual(2, tracks);
            Assert.IsNull(WaitForStop(stops, 500));
        }

        [TestMethod]
        public void ACarriedOnSessionLostBeforeTheServerAnswersIsReported()
        {
            // not reconnected to again until the server has said it still has the session: one
            // that drops every connection would otherwise be reconnected to for ever
            using var server = new FakeRtspServer(Sdp) { IgnoreMethod = "GET_PARAMETER" };
            var stops = new BlockingCollection<StoppedReason>();
            using var client = NewClient(stops);

            PlayThenDrop(server, client, RTPTransport.UDP);
            Assert.IsTrue(server.WaitForRequest("GET_PARAMETER"));
            server.DropConnection();

            Assert.AreEqual(StoppedReason.ConnectionLost, WaitForStop(stops, 5000));
            Thread.Sleep(500);
            Assert.AreEqual(2, server.Connections);
        }
    }
}
