using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Linq;
using System.Text;
using SharpRTSPServer;

namespace SharpRTSPServer.Tests
{
    [TestClass]
    public class ServerLifecycleTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        private static RTSPStreamSource NewStreamSource(string streamId = "stream1") =>
            new RTSPStreamSource(streamId, new H264Track(Sps, Pps), null);

        /// <summary>
        /// Reports whether the server is still holding the connection open.
        /// </summary>
        /// <remarks>
        /// Poll reports a socket as readable both when data has arrived and when the peer has closed,
        /// so the two are told apart by whether there is anything to actually read.
        /// </remarks>
        private static bool ConnectionIsAccepted(TcpClient client)
        {
            Socket socket = client.Client;

            bool readable = socket.Poll(200_000 /* microseconds */, SelectMode.SelectRead);
            bool closedByPeer = readable && socket.Available == 0;

            return !closedByPeer;
        }

        [TestMethod]
        public void ConnectionsBeyondTheLimitAreRefused()
        {
            int port = TestPorts.FindFree();
            var sockets = new List<TcpClient>();

            using (var server = new RTSPServer(port, "admin", "password"))
            {
                server.MaxConnections = 2;
                server.AddStreamSource(NewStreamSource());
                server.StartListen();

                try
                {
                    for (int i = 0; i < 2; i++)
                    {
                        var accepted = new TcpClient("127.0.0.1", port);
                        sockets.Add(accepted);

                        // wait for it to be counted, not for a fixed time - the limit can only be
                        // enforced against connections the server has got to yet
                        WaitForConnectionCount(server, i + 1);
                        Assert.IsTrue(ConnectionIsAccepted(accepted), $"connection {i} should have been accepted");
                    }

                    // the third one is over the limit and must be dropped rather than queued forever
                    var refused = new TcpClient("127.0.0.1", port);
                    sockets.Add(refused);

                    // Waited for rather than slept on. A fixed pause has to be long enough for the
                    // slowest run on the busiest machine, and a run where the server had not got to
                    // the connection yet failed as though it had been allowed in.
                    Assert.IsTrue(WaitForConnectionToBeDropped(refused),
                        "the connection over the limit should have been refused");
                }
                finally
                {
                    foreach (var socket in sockets) socket.Dispose();
                }
            }
        }

        [TestMethod]
        public void ConnectionsAreAcceptedWhenTheLimitIsDisabled()
        {
            int port = TestPorts.FindFree();
            var sockets = new List<TcpClient>();

            using (var server = new RTSPServer(port, "admin", "password"))
            {
                server.MaxConnections = 0; // no limit
                server.AddStreamSource(NewStreamSource());
                server.StartListen();

                try
                {
                    for (int i = 0; i < 5; i++)
                    {
                        var socket = new TcpClient("127.0.0.1", port);
                        sockets.Add(socket);
                        WaitForServerToAccept();
                        Assert.IsTrue(ConnectionIsAccepted(socket), $"connection {i} should have been accepted");
                    }
                }
                finally
                {
                    foreach (var socket in sockets) socket.Dispose();
                }
            }
        }

        /// <summary>
        /// The accept loop runs on its own thread, so give it a moment to take the connection.
        /// </summary>
        private static void WaitForServerToAccept() => System.Threading.Thread.Sleep(150);

        /// <summary>
        /// Waits for the far end to close a connection, and says whether it did.
        /// </summary>
        /// <remarks>
        /// Only takes the whole time when the answer is no, which is the run that is about to fail
        /// anyway - a connection that is going to be dropped is dropped long before this is up.
        /// </remarks>
        private static bool WaitForConnectionToBeDropped(TcpClient client)
        {
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);

            while (DateTime.UtcNow < until)
            {
                if (!ConnectionIsAccepted(client))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Waits until the server has actually taken the connections on, rather than for a fixed
        /// time that a loaded machine can outrun.
        /// </summary>
        private static void WaitForConnectionCount(RTSPServer server, int expected)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);

            while (DateTime.UtcNow < deadline)
            {
                if (server.ConnectionCount >= expected)
                {
                    return;
                }

                System.Threading.Thread.Sleep(10);
            }

            Assert.Fail($"the server did not take on {expected} connections");
        }

        [TestMethod]
        public void DisposingTheServerClosesClientConnections()
        {
            int port = TestPorts.FindFree();
            TcpClient client;

            using (var server = new RTSPServer(port, "admin", "password"))
            {
                server.AddStreamSource(NewStreamSource());
                server.StartListen();

                client = new TcpClient("127.0.0.1", port);
                WaitForServerToAccept();
                Assert.IsTrue(ConnectionIsAccepted(client));
            }

            WaitForServerToAccept();

            using (client)
            {
                // the server used to leave every client socket open until finalization
                Assert.IsFalse(ConnectionIsAccepted(client), "disposing the server should close client connections");
            }
        }

        [TestMethod]
        public void AddingTwoStreamSourcesWithTheSameIdIsRejected()
        {
            using var server = new RTSPServer(TestPorts.FindFree(), "admin", "password");
            server.AddStreamSource(NewStreamSource("stream1"));

            // the second would be unreachable, since lookups match the first by ID
            Assert.ThrowsExactly<ArgumentException>(() => server.AddStreamSource(NewStreamSource("stream1")));
        }

        [TestMethod]
        public void StreamSourcesCanBeAddedAndRemoved()
        {
            using var server = new RTSPServer(TestPorts.FindFree(), "admin", "password");
            var first = NewStreamSource("stream1");
            var second = NewStreamSource("stream2");

            server.AddStreamSource(first);
            server.AddStreamSource(second);
            Assert.HasCount(2, server.GetStreamSources());

            server.RemoveStreamSource(first);
            CollectionAssert.AreEqual(new[] { "stream2" }, server.GetStreamSources().Select(s => s.StreamID).ToArray());
        }

        [TestMethod]
        public void GetStreamSourcesReturnsASnapshotRatherThanTheLiveList()
        {
            using var server = new RTSPServer(TestPorts.FindFree(), "admin", "password");
            server.AddStreamSource(NewStreamSource("stream1"));

            var snapshot = server.GetStreamSources();
            server.AddStreamSource(NewStreamSource("stream2"));

            Assert.ContainsSingle(snapshot);
            Assert.HasCount(2, server.GetStreamSources());
        }

        [TestMethod]
        public void AddStreamSourceRejectsNull()
        {
            using var server = new RTSPServer(TestPorts.FindFree(), "admin", "password");

            Assert.ThrowsExactly<ArgumentNullException>(() => server.AddStreamSource(null));
            Assert.ThrowsExactly<ArgumentNullException>(() => server.RemoveStreamSource(null));
        }

        [TestMethod]
        public void CheckTimeoutsOnAnUnknownStreamReportsNothingInsteadOfThrowing()
        {
            using var server = new RTSPServer(TestPorts.FindFree(), "admin", "password");

            server.CheckTimeouts("no-such-stream", out int count, out int playCount);

            Assert.AreEqual(0, count);
            Assert.AreEqual(0, playCount);
        }

        [TestMethod]
        public void FeedingRtpForAnUnknownStreamIsIgnoredInsteadOfThrowing()
        {
            using var server = new RTSPServer(TestPorts.FindFree(), "admin", "password");

            var packets = new List<Memory<byte>> { new byte[12] };

            try
            {
                server.FeedInRawRTP("no-such-stream", 0, 0, packets, null);
            }
            catch (Exception ex)
            {
                Assert.Fail($"feeding an unknown stream threw {ex.GetType().Name}");
            }
        }

        [TestMethod]
        public void PortNumberIsValidated()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new RTSPServer(-1, "admin", "password"));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new RTSPServer(70000, "admin", "password"));
        }
    }
}
