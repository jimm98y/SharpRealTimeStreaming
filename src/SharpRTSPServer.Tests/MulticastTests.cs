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
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using SharpSRTP.SRTP;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// Media sent once, to a group, for however many clients are listening.
    /// </summary>
    /// <remarks>
    /// Multicast used to be refused outright. The awkward part of it is not the sending but the
    /// sharing: everything else here is per client - its own sockets, its own sequence numbering,
    /// its own SSRC - and a group has one of each, for all of them.
    /// </remarks>
    [TestClass]
    [DoNotParallelize] // binds real group ports, and counts what arrives on them
    public sealed class MulticastTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        /// <summary>The loopback link only, so nothing under test leaves this machine.</summary>
        private const string Group = "239.255.42.99";

        private static List<ReadOnlyMemory<byte>> OneNal() => new List<ReadOnlyMemory<byte>>
        {
            new ReadOnlyMemory<byte>(new byte[] { 0x65, 0x11, 0x22, 0x33 }),
        };

        private static RTSPServer NewServer(int port, out H264Track videoTrack, int multicastPortStart, int ttl = 0)
        {
            var server = new RTSPServer(port, new InMemoryUserRepository("admin", "password"))
            {
                MulticastEnabled = true, // not the default: a group is published to the whole segment
                MulticastAddress = Group,
                MulticastTimeToLive = ttl, // zero by default: this machine and no further
            };

            server.SetMulticastPortRange(multicastPortStart, multicastPortStart + 40);

            videoTrack = new H264Track(Sps, Pps);
            server.AddStreamSource(new RTSPStreamSource("stream1", videoTrack, null));
            server.StartListen();
            return server;
        }

        /// <summary>
        /// Sets a client up for multicast and returns what the server said to do.
        /// </summary>
        private static (RtspTestClient Client, string Session, string Destination, int Port, int Ttl) Join(int port)
        {
            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            var setup = client.Send("SETUP", baseUri + "/trackID=0", "Transport: RTP/AVP;multicast");

            Assert.AreEqual(200, setup.StatusCode, "a multicast SETUP should be accepted");

            string transport = setup.Match(@"Transport:\s*([^\r\n]+)");
            Assert.IsNotNull(transport, "the reply should describe the transport");

            var destination = Regex.Match(transport, @"destination=([^;\s]+)");
            var groupPort = Regex.Match(transport, @"port=(\d+)-(\d+)");
            var ttl = Regex.Match(transport, @"ttl=(\d+)");

            Assert.IsTrue(destination.Success, "the client has to be told which group: " + transport);
            Assert.IsTrue(groupPort.Success, "and on which port: " + transport);

            return (client, setup.Session, destination.Groups[1].Value,
                int.Parse(groupPort.Groups[1].Value),
                ttl.Success ? int.Parse(ttl.Groups[1].Value) : -1);
        }

        /// <summary>
        /// How long to keep feeding and waiting before deciding nothing is coming.
        /// </summary>
        /// <remarks>
        /// Long, on purpose. These tests read real packets off a real socket, so how quickly one
        /// arrives is up to the machine and how busy it is, and a limit that is merely usually long
        /// enough fails a run for being slow rather than for being wrong. Only a run that is about to
        /// fail waits the whole time.
        /// </remarks>
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

        /// <summary>
        /// How long to go on listening after something should have stopped arriving.
        /// </summary>
        /// <remarks>
        /// Short, for the opposite reason: every one of these is spent by a run that passes. It only
        /// has to outlast what is already on its way.
        /// </remarks>
        private static readonly TimeSpan SettlingTime = TimeSpan.FromMilliseconds(600);

        /// <summary>
        /// Feeds until a packet reaches the group, and hands back the first one.
        /// </summary>
        private static byte[] WaitForPacket(UdpClient wire, Action feed)
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            var until = DateTime.UtcNow + Patience;

            wire.Client.ReceiveTimeout = 250;

            while (DateTime.UtcNow < until)
            {
                feed();

                try
                {
                    return wire.Receive(ref from);
                }
                catch (SocketException)
                {
                    // nothing yet
                }
            }

            return null;
        }

        /// <summary>
        /// Everything that arrives from now until the group has had time to go quiet.
        /// </summary>
        private static int CountWhatArrives(UdpClient wire, TimeSpan forHowLong)
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            var until = DateTime.UtcNow + forHowLong;
            int count = 0;

            wire.Client.ReceiveTimeout = 100;

            while (DateTime.UtcNow < until)
            {
                try
                {
                    wire.Receive(ref from);
                    count++;
                }
                catch (SocketException)
                {
                    // nothing waiting, keep listening until the time is up
                }
            }

            return count;
        }

        /// <summary>
        /// Listens to a group the way a client told to would.
        /// </summary>
        private static UdpClient Listen(string group, int port)
        {
            var socket = new UdpClient();
            socket.ExclusiveAddressUse = false;
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            // The default interface, not the loopback one - loopback does not carry multicast, and
            // joining on it is refused outright. A sender on this machine still reaches this socket,
            // because a host delivers its own multicast back to itself.
            socket.JoinMulticastGroup(IPAddress.Parse(group));
            socket.Client.ReceiveTimeout = 3000;
            return socket;
        }

        [TestMethod]
        public void TheClientIsToldWhereToListen()
        {
            int port = TestPorts.FindFree();
            // a hop limit that is actually reported, since a header omits a zero
            using var server = NewServer(port, out _, 56000, ttl: 1);

            var joined = Join(port);

            using (joined.Client)
            {
                Assert.AreEqual(Group, joined.Destination, "it should name the configured group");
                Assert.IsGreaterThanOrEqualTo(56000, joined.Port, "and a port from the multicast range");
                Assert.IsLessThan(56040, joined.Port);
                Assert.AreEqual(1, joined.Ttl, "the hop limit is part of the answer");
            }
        }

        [TestMethod]
        public void TwoClientsAreSentToTheSameGroup()
        {
            int port = TestPorts.FindFree();
            using var server = NewServer(port, out _, 56100);

            var first = Join(port);
            var second = Join(port);

            using (first.Client)
            using (second.Client)
            {
                // The whole point. One group and one port for both of them, and so one copy of the
                // media rather than one each.
                Assert.AreEqual(first.Destination, second.Destination);
                Assert.AreEqual(first.Port, second.Port, "both should be sent to the same port");
            }
        }

        [TestMethod]
        public void AFrameGoesToTheGroupOnceHoweverManyAreListening()
        {
            int port = TestPorts.FindFree();
            using var server = NewServer(port, out var videoTrack, 56200);

            var first = Join(port);
            var second = Join(port);
            var third = Join(port);

            using (first.Client)
            using (second.Client)
            using (third.Client)
            using (var wire = Listen(first.Destination, first.Port))
            {
                string baseUri = $"rtsp://127.0.0.1:{port}/stream1";
                first.Client.Send("PLAY", baseUri, "Session: " + first.Session);
                second.Client.Send("PLAY", baseUri, "Session: " + second.Session);
                third.Client.Send("PLAY", baseUri, "Session: " + third.Session);

                Thread.Sleep(200);

                // Fed until the group is known to be carrying media, so what follows is timed from
                // the group being live rather than from an assumption about how long that takes.
                Assert.IsNotNull(WaitForPacket(wire, () => videoTrack.FeedInRawSamples(3000, OneNal())),
                    "the group should be carrying media before the frame that is counted");

                CountWhatArrives(wire, SettlingTime);

                // exactly one frame, of exactly one packet
                videoTrack.FeedInRawSamples(9000, OneNal());

                int arrived = CountWhatArrives(wire, SettlingTime);

                // Three clients, one packet. Sending it once is what makes this multicast rather
                // than three unicast streams that happen to share an address.
                Assert.AreEqual(1, arrived,
                    "one frame should reach the group once, not once per client listening");
            }
        }

        [TestMethod]
        public void TheGroupCarriesOneSequenceOfNumbers()
        {
            int port = TestPorts.FindFree();
            using var server = NewServer(port, out var videoTrack, 56300);

            var first = Join(port);
            var second = Join(port);

            using (first.Client)
            using (second.Client)
            using (var wire = Listen(first.Destination, first.Port))
            {
                string baseUri = $"rtsp://127.0.0.1:{port}/stream1";
                first.Client.Send("PLAY", baseUri, "Session: " + first.Session);
                second.Client.Send("PLAY", baseUri, "Session: " + second.Session);

                Thread.Sleep(200);

                const int Frames = 8;
                var numbers = new List<int>();
                var from = new IPEndPoint(IPAddress.Any, 0);

                for (int i = 0; i < Frames; i++)
                {
                    videoTrack.FeedInRawSamples((uint)((i + 1) * 3000), OneNal());

                    byte[] packet = WaitForPacket(wire, () => { });

                    if (packet == null)
                    {
                        break;
                    }

                    numbers.Add((packet[2] << 8) | packet[3]);
                }

                Assert.IsGreaterThanOrEqualTo(4, numbers.Count, "not enough arrived to say anything");

                // Two clients stamping their own numbering onto one group would show up here as a
                // number repeated or jumped.
                for (int i = 1; i < numbers.Count; i++)
                {
                    Assert.AreEqual((numbers[i - 1] + 1) & 0xFFFF, numbers[i],
                        $"the group should number its packets once: {string.Join(", ", numbers)}");
                }
            }
        }

        [TestMethod]
        public void TheGroupLastsWhileAnybodyIsStillListening()
        {
            int port = TestPorts.FindFree();
            using var server = NewServer(port, out var videoTrack, 56400);

            var staying = Join(port);
            var leaving = Join(port);

            using (staying.Client)
            using (var wire = Listen(staying.Destination, staying.Port))
            {
                string baseUri = $"rtsp://127.0.0.1:{port}/stream1";
                staying.Client.Send("PLAY", baseUri, "Session: " + staying.Session);
                leaving.Client.Send("PLAY", baseUri, "Session: " + leaving.Session);

                Thread.Sleep(200);

                // one of them goes
                leaving.Client.Send("TEARDOWN", baseUri, "Session: " + leaving.Session);
                leaving.Client.Dispose();
                Thread.Sleep(300);

                // and the media does not stop for the one still watching
                byte[] packet = WaitForPacket(wire, () => videoTrack.FeedInRawSamples(3000, OneNal()));

                Assert.IsNotNull(packet, "one client leaving should not stop the group for the others");
            }
        }

        [TestMethod]
        public void TheGroupStopsWhenTheLastListenerHasGone()
        {
            int port = TestPorts.FindFree();
            using var server = NewServer(port, out var videoTrack, 56500);

            var only = Join(port);
            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            using (var wire = Listen(only.Destination, only.Port))
            {
                only.Client.Send("PLAY", baseUri, "Session: " + only.Session);
                Thread.Sleep(200);

                only.Client.Send("TEARDOWN", baseUri, "Session: " + only.Session);
                only.Client.Dispose();
                Thread.Sleep(400);

                // drain anything already in flight, including the group saying goodbye
                CountWhatArrives(wire, SettlingTime);

                for (int i = 0; i < 5; i++)
                {
                    videoTrack.FeedInRawSamples((uint)((i + 1) * 3000), OneNal());
                }

                Assert.AreEqual(0, CountWhatArrives(wire, SettlingTime),
                    "nothing should be sent to a group nobody is listening to");
            }
        }

        [TestMethod]
        public void TakingTheStreamAwayTakesItsGroupWithIt()
        {
            int port = TestPorts.FindFree();
            using var server = NewServer(port, out var videoTrack, 57200);

            var streamSource = server.GetStreamSources()[0];

            var joined = Join(port);

            using (joined.Client)
            using (var wire = Listen(joined.Destination, joined.Port))
            {
                string baseUri = $"rtsp://127.0.0.1:{port}/stream1";
                joined.Client.Send("PLAY", baseUri, "Session: " + joined.Session);
                Thread.Sleep(200);

                // it is running
                videoTrack.FeedInRawSamples(3000, OneNal());

                byte[] running = WaitForPacket(wire, () => videoTrack.FeedInRawSamples(3000, OneNal()));

                Assert.IsNotNull(running, "the group should be carrying the media before the stream goes");

                // and then the stream it belongs to is taken away
                server.RemoveStreamSource(streamSource);
                Thread.Sleep(300);

                CountWhatArrives(wire, SettlingTime);

                for (int i = 0; i < 5; i++)
                {
                    videoTrack.FeedInRawSamples((uint)((i + 20) * 3000), OneNal());
                }

                Assert.AreEqual(0, CountWhatArrives(wire, SettlingTime),
                    "a stream that has been removed should not still be sending to a group");
            }
        }

        [TestMethod]
        public void AGroupGivesItsPortBackWhenItGoes()
        {
            int port = TestPorts.FindFree();
            using var server = NewServer(port, out _, 57300);

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            // the first client makes the group
            var first = Join(port);
            int firstPort = first.Port;

            first.Client.Send("TEARDOWN", baseUri, "Session: " + first.Session);
            first.Client.Dispose();
            Thread.Sleep(300);

            // and the next one, arriving after it has gone, gets the same port back rather than
            // running the range down one group at a time
            var second = Join(port);

            using (second.Client)
            {
                Assert.AreEqual(firstPort, second.Port,
                    "the port of a group that has ended should be available again");
            }
        }

        [TestMethod]
        public void MulticastCanBeTurnedOff()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, new InMemoryUserRepository("admin", "password")) { MulticastEnabled = false };
            server.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), null));
            server.StartListen();

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            var setup = client.Send("SETUP", baseUri + "/trackID=0", "Transport: RTP/AVP;multicast");

            Assert.AreEqual(461, setup.StatusCode,
                "a refused transport sends the client to one that works");
        }

        [TestMethod]
        public void AProtectedTrackIsNotSentToAGroup()
        {
            int port = TestPorts.FindFree();

            using var server = new RTSPServer(port, new InMemoryUserRepository("admin", "password"), false, null,
                SrtpCryptoSuites.AES_CM_128_HMAC_SHA1_80, null)
            {
                MulticastEnabled = true, // not the default: a group is published to the whole segment
                MulticastAddress = Group,
                MulticastTimeToLive = 0,
            };

            server.AddStreamSource(new RTSPStreamSource("stream1",
                new H264Track(Sps, Pps) { RtpProfile = RtpProfiles.SAVP }, null));
            server.StartListen();

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            var setup = client.Send("SETUP", baseUri + "/trackID=0", "Transport: RTP/AVP;multicast");

            // Every client is handed its own key in its own SDP, and a group has one stream for all
            // of them - so there is no key the group could send under that they could all read.
            Assert.AreEqual(461, setup.StatusCode,
                "a protected track cannot be given to a group under the keys this server hands out");
        }
    }
}
