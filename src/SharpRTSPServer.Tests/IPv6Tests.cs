using System;
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
            Assert.IsTrue(RTSPServer.CanSendUdpTo(mapped), "an IPv4 client can still be sent UDP");
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
        public void UdpIsRefusedForAClientThatCannotBeReachedOverIt()
        {
            // The sockets the transport provides are IPv4, so there is nothing to reach a real IPv6
            // client from. Saying so lets the client interleave over the connection it already has;
            // the alternative was accepting the SETUP and sending the media nowhere.
            Assert.IsFalse(RTSPServer.CanSendUdpTo(IPAddress.Parse("2001:db8::1")));
            Assert.IsTrue(RTSPServer.CanSendUdpTo(IPAddress.Parse("10.0.0.5")));
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
        public void AnIPv6ClientAskingForUdpIsToldItCannotHaveIt()
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

            using var client = new RtspTestClient(IPAddress.IPv6Loopback.ToString(), port, "admin", "password");

            string baseUri = $"rtsp://[::1]:{port}/stream1";
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP;unicast;client_port=41500-41501");

            Assert.AreEqual(461, setup.StatusCode,
                "refusing it sends the client to a transport that works, rather than to media that never arrives");
        }
    }
}
