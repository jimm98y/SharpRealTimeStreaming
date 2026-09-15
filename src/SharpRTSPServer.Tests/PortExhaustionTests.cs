using System;
using System.Collections.Generic;
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
