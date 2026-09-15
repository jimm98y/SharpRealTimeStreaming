using System;
using System.Collections.Generic;
using System.Net.Sockets;
using SharpRTSPServer;

namespace SharpRTSPServer.Tests
{
    [TestClass]
    public sealed class PortExhaustionTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        [TestMethod]
        public void ClientsThatVanishWithoutTearingDownDoNotExhaustTheUdpPorts()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");
            server.MaxConnections = 0;
            server.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), null));
            server.StartListen();

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            // There are only 500 UDP port pairs. Each SETUP takes one, so a client that sets up and
            // then vanishes without a TEARDOWN must not hold its pair indefinitely.
            for (int i = 0; i < 700; i++)
            {
                var client = new RtspTestClient(port, "admin", "password");
                client.Send("OPTIONS", baseUri);
                client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

                var setup = client.Send("SETUP", baseUri + "/trackID=0",
                    "Transport: RTP/AVP;unicast;client_port=40000-40001");

                Assert.AreEqual(200, setup.StatusCode, $"SETUP number {i + 1} failed - the UDP ports ran out");

                client.Dispose(); // drop the socket, as a crashed or unplugged client would
            }
        }

        [TestMethod]
        public void SetupIsRefusedWithUnsupportedTransportWhenTheUdpRangeIsFullyTaken()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");
            server.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), null));
            server.StartListen();

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            // Take every port in the range the server allocates from, so the SETUP below has nothing
            // left to bind. UDPSocket scans the range and reports it being used up as an
            // InvalidOperationException, which the server has to answer rather than let escape.
            var hogs = new List<UdpClient>();
            try
            {
                for (int p = server.RtpPortRangeStart; p < server.RtpPortRangeEnd; p++)
                {
                    try
                    {
                        hogs.Add(new UdpClient(p));
                    }
                    catch (SocketException)
                    {
                        // already taken by something else on this machine, which suits us here
                    }
                }

                using var client = new RtspTestClient(port, "admin", "password");
                client.Send("OPTIONS", baseUri);
                client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

                var setup = client.Send("SETUP", baseUri + "/trackID=0",
                    "Transport: RTP/AVP;unicast;client_port=40000-40001");

                Assert.AreEqual(461, setup.StatusCode, "SETUP should answer 461 so the client can fall back to TCP");

                // the connection has to survive, so the fallback can go out on it
                var tcpSetup = client.Send("SETUP", baseUri + "/trackID=0",
                    "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");

                Assert.AreEqual(200, tcpSetup.StatusCode, "the TCP fallback SETUP should be accepted");
            }
            finally
            {
                foreach (var hog in hogs)
                {
                    hog.Dispose();
                }
            }
        }

        [TestMethod]
        public void SetupAllocatesFromTheConfiguredPortRange()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");

            // one pair, somewhere clear of the default range
            int firstPort = 52000;
            server.SetRtpPortRange(firstPort, firstPort + 2);

            server.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), null));
            server.StartListen();

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP;unicast;client_port=40000-40001");

            Assert.AreEqual(200, setup.StatusCode);

            string serverPort = setup.Match(@"server_port=(\d+)");
            Assert.IsNotNull(serverPort, "the SETUP reply should name the server ports");
            Assert.AreEqual(firstPort, int.Parse(serverPort), "the pair should come from the configured range");
        }

        [TestMethod]
        public void SetRtpPortRangeRejectsARangeWithNoRoomForAPair()
        {
            using var server = new RTSPServer(TestPorts.FindFree(), "admin", "password");

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => server.SetRtpPortRange(52000, 52001));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => server.SetRtpPortRange(-1, 52000));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => server.SetRtpPortRange(52000, 70000));
        }

        [TestMethod]
        public void ATornDownSessionReleasesItsUdpPortsImmediately()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");
            server.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), null));
            server.StartListen();

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            for (int i = 0; i < 700; i++)
            {
                using var client = new RtspTestClient(port, "admin", "password");
                client.Send("OPTIONS", baseUri);
                client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

                var setup = client.Send("SETUP", baseUri + "/trackID=0",
                    "Transport: RTP/AVP;unicast;client_port=40000-40001");
                Assert.AreEqual(200, setup.StatusCode, $"SETUP number {i + 1} failed - the UDP ports ran out");

                client.Send("TEARDOWN", baseUri, $"Session: {setup.Session}");
            }
        }
    }
}
