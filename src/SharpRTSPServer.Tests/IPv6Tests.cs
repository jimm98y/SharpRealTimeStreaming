using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using SharpRTSPServer;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// Clients that reach the server over IPv6.
    /// </summary>
    /// <remarks>
    /// The server listened on IPv4 only, so an IPv6 client could not connect at all. It also worked
    /// out where to send UDP media by cutting a client address off at its first colon, which does
    /// nothing to an IPv4 address - there is no port on it to remove - and leaves nothing at all of
    /// an IPv6 one.
    /// </remarks>
    [TestClass]
    public sealed class IPv6Tests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        [TestMethod]
        public void AnAddressIsNotCutOffAtItsFirstColon()
        {
            // The one that used to become the empty string
            Assert.AreEqual("2001:db8::1",
                RTSPServer.MediaDestination(IPAddress.Parse("2001:db8::1")));

            // and the one the cutting was presumably meant for, which never had a port on it anyway
            Assert.AreEqual("192.168.1.10",
                RTSPServer.MediaDestination(IPAddress.Parse("192.168.1.10")));
        }

        [TestMethod]
        public void AnIPv4ClientOnADualStackListenerIsAddressedAsIPv4()
        {
            // How every IPv4 client is reported once the server listens on both, and sending to it in
            // this shape asks for an IPv6 socket where an IPv4 one is wanted.
            var mapped = IPAddress.Parse("127.0.0.1").MapToIPv6();

            Assert.IsTrue(mapped.IsIPv4MappedToIPv6, "the test is about the mapped form");
            Assert.AreEqual("127.0.0.1", RTSPServer.MediaDestination(mapped));

            // the mapping is how the listener describes it, not how the client is reached
            Assert.AreEqual(AddressFamily.InterNetwork, RTSPServer.MediaFamily(mapped));
        }

        [TestMethod]
        public void AScopeIsNotPassedOnToASocket()
        {
            // A link local address carries a scope that means something only on the machine that
            // wrote it down.
            var scoped = IPAddress.Parse("fe80::1%12");

            Assert.AreEqual("fe80::1", RTSPServer.MediaDestination(scoped));
        }

        [TestMethod]
        public void MediaGoesOutInTheFamilyTheClientArrivedOn()
        {
            Assert.AreEqual(AddressFamily.InterNetworkV6, RTSPServer.MediaFamily(IPAddress.Parse("2001:db8::1")));
            Assert.AreEqual(AddressFamily.InterNetwork, RTSPServer.MediaFamily(IPAddress.Parse("10.0.0.5")));
        }

        [TestMethod]
        public void AnIPv6ClientCanConnectAndBeServedOverTheConnectionItArrivedOn()
        {
            if (!Socket.OSSupportsIPv6)
            {
                Assert.Inconclusive("this machine has no IPv6");
                return;
            }

            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");
            server.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), null));
            server.StartListen();

            // over the loopback of the other family
            using var client = new RtspTestClient(IPAddress.IPv6Loopback.ToString(), port, "admin", "password");

            string baseUri = $"rtsp://[::1]:{port}/stream1";

            Assert.AreEqual(200, client.Send("OPTIONS", baseUri).StatusCode,
                "an IPv6 client should be able to speak RTSP at all");

            Assert.AreEqual(200, client.Send("DESCRIBE", baseUri, "Accept: application/sdp").StatusCode);

            // Interleaved, which is the transport that works for either family because the media
            // rides the connection that is already open.
            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");

            Assert.AreEqual(200, setup.StatusCode, "interleaved media should be set up over IPv6");
            Assert.AreEqual(200, client.Send("PLAY", baseUri, $"Session: {setup.Session}").StatusCode);
        }

        [TestMethod]
        public void MediaOverUdpReachesAnIPv6Client()
        {
            if (!Socket.OSSupportsIPv6)
            {
                Assert.Inconclusive("this machine has no IPv6");
                return;
            }

            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");
            server.SetRtpPortRange(55200, 55400);

            var videoTrack = new H264Track(Sps, Pps);
            server.AddStreamSource(new RTSPStreamSource("stream1", videoTrack, null));
            server.StartListen();

            // where this client wants its RTP, on the loopback of the family it connected over
            using var rtp = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 0));
            int rtpPort = ((IPEndPoint)rtp.Client.LocalEndPoint).Port;
            using var rtcp = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, rtpPort + 1));

            using var client = new RtspTestClient(IPAddress.IPv6Loopback.ToString(), port, "admin", "password");

            string baseUri = $"rtsp://[::1]:{port}/stream1";
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                $"Transport: RTP/AVP;unicast;client_port={rtpPort}-{rtpPort + 1}");

            Assert.AreEqual(200, setup.StatusCode, "a UDP SETUP from an IPv6 client should be accepted");
            Assert.IsNotNull(setup.Match(@"server_port=(\d+)"), "the reply should name the server ports");

            Assert.AreEqual(200, client.Send("PLAY", baseUri, $"Session: {setup.Session}").StatusCode);

            rtp.Client.ReceiveTimeout = 5000;

            // Fed until something arrives or the patience runs out, because the first frame can be
            // produced before PLAY has finished being answered.
            byte[] received = null;

            for (int i = 0; i < 20 && received == null; i++)
            {
                videoTrack.FeedInRawSamples((uint)((i + 1) * 3000), new List<ReadOnlyMemory<byte>>
                {
                    new ReadOnlyMemory<byte>(new byte[] { 0x65, 0x11, 0x22, 0x33 }),
                });

                try
                {
                    var from = new IPEndPoint(IPAddress.IPv6Any, 0);
                    received = rtp.Receive(ref from);
                }
                catch (SocketException)
                {
                    // nothing yet
                }
            }

            Assert.IsNotNull(received, "no RTP arrived over IPv6");
            Assert.IsGreaterThan(12, received.Length, "an RTP packet is a header and then a payload");
            Assert.AreEqual(2, received[0] >> 6, "it should be RTP version 2");
        }
    }
}
