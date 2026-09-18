using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using SharpSRTP.SRTP;
using SharpRTSPServer;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// Protected media sent to a group, under a key every member of the group holds.
    /// </summary>
    /// <remarks>
    /// SRTP is built for this - RFC 3711 is a group protocol, and RFC 4568 puts the key in the SDP
    /// precisely so that everyone who is given the SDP can read the stream. What stood in the way
    /// here was that this server derives a key for each client, which cannot work when they are all
    /// listening to the same stream. A stream can now hold the key instead.
    /// </remarks>
    [TestClass]
    [DoNotParallelize] // binds real group ports and reads what arrives on them
    public sealed class MulticastSrtpTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        private static readonly byte[] Nal = { 0x65, 0x11, 0x22, 0x33, 0x44, 0x55 };

        private const string Group = "239.255.42.98";

        private static RTSPServer NewServer(int port, out H264Track videoTrack, int multicastPortStart,
            bool sharedKey = true)
        {
            var server = new RTSPServer(port, "admin", "password", false, null,
                SrtpCryptoSuites.AES_CM_128_HMAC_SHA1_80, null)
            {
                MulticastAddress = Group,
                MulticastTimeToLive = 0,
            };

            server.SetMulticastPortRange(multicastPortStart, multicastPortStart + 40);

            videoTrack = new H264Track(Sps, Pps) { RtpProfile = RtpProfiles.SAVP };

            server.AddStreamSource(new RTSPStreamSource("stream1", videoTrack, null)
            {
                SharedSrtpKey = sharedKey,
            });

            server.StartListen();
            return server;
        }

        private sealed class Joined
        {
            public RtspTestClient Client;
            public string Session;
            public string Destination;
            public int Port;
            public string CryptoSuite;
            public byte[] MasterKeySalt;
        }

        private static Joined Join(int port)
        {
            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);

            var describe = client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            var crypto = Regex.Match(describe.Body ?? string.Empty,
                @"a=crypto:\d+\s+(\S+)\s+inline:([A-Za-z0-9+/=]+)");
            Assert.IsTrue(crypto.Success, "the SDP should carry a key: " + describe.Body);

            var setup = client.Send("SETUP", baseUri + "/trackID=0", "Transport: RTP/AVP;multicast");
            Assert.AreEqual(200, setup.StatusCode, "a protected track with a shared key may go to a group");

            string transport = setup.Match(@"Transport:\s*([^\r\n]+)");
            var destination = Regex.Match(transport, @"destination=([^;\s]+)");
            var groupPort = Regex.Match(transport, @"port=(\d+)-(\d+)");

            Assert.IsTrue(destination.Success, transport);
            Assert.IsTrue(groupPort.Success, transport);

            return new Joined
            {
                Client = client,
                Session = setup.Session,
                Destination = destination.Groups[1].Value,
                Port = int.Parse(groupPort.Groups[1].Value),
                CryptoSuite = crypto.Groups[1].Value,
                MasterKeySalt = Convert.FromBase64String(crypto.Groups[2].Value),
            };
        }

        /// <summary>
        /// How long to keep feeding and waiting before deciding nothing is coming.
        /// </summary>
        /// <remarks>
        /// Long, on purpose. These read real packets off a real socket, so how quickly one arrives is
        /// up to the machine and how busy it is; only a run that is about to fail waits it out.
        /// </remarks>
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

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

        private static UdpClient Listen(string group, int port)
        {
            var socket = new UdpClient();
            socket.ExclusiveAddressUse = false;
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            socket.JoinMulticastGroup(IPAddress.Parse(group));
            socket.Client.ReceiveTimeout = 3000;
            return socket;
        }

        [TestMethod]
        public void EveryMemberOfTheGroupIsGivenTheSameKey()
        {
            int port = TestPorts.FindFree();
            using var server = NewServer(port, out _, 56600);

            var first = Join(port);
            var second = Join(port);

            using (first.Client)
            using (second.Client)
            {
                // A key each is exactly what cannot work here: one stream goes to the group, so a
                // client holding a different key would receive it and be unable to read a byte.
                CollectionAssert.AreEqual(first.MasterKeySalt, second.MasterKeySalt,
                    "both clients should be told the same key");
                Assert.AreEqual(first.Port, second.Port, "and sent to the same place");
            }
        }

        [TestMethod]
        public void WhatTheGroupSendsDecryptsUnderTheKeyFromTheSdp()
        {
            int port = TestPorts.FindFree();
            using var server = NewServer(port, out var videoTrack, 56700);

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

                // The second client reads the stream with the key it was given, which is the whole
                // claim: one protected stream, read by everyone who was handed the SDP.
                SrtpKeys keys = SrtpProtocol.CreateMasterKeys(second.CryptoSuite, Array.Empty<byte>(),
                    second.MasterKeySalt);
                SrtpSessionContext context = SrtpProtocol.CreateSrtpSessionContext(keys);

                int read = 0;

                for (int i = 0; i < 12 && read < 4; i++)
                {
                    int at = i;
                    byte[] packet = WaitForPacket(wire, () => videoTrack.FeedInRawSamples(
                        (uint)((at + 1) * 3000),
                        new List<ReadOnlyMemory<byte>> { new ReadOnlyMemory<byte>(Nal) }));

                    if (packet == null)
                    {
                        break;
                    }

                    // protected, not plain: the tag makes it longer than what went in
                    Assert.IsGreaterThan(12 + Nal.Length, packet.Length,
                        "the media should be protected, not sent in the clear");

                    int result = context.DecodeRtpContext.UnprotectRtp(packet, packet.Length, out int length);
                    Assert.AreEqual(0, result, $"packet {read} did not authenticate under the group key");

                    var payload = new ArraySegment<byte>(packet, 12, length - 12);
                    CollectionAssert.AreEqual(Nal, payload.ToArray(),
                        $"packet {read} decrypted to something other than what was fed in");

                    read++;
                }

                Assert.IsGreaterThanOrEqualTo(4, read, "not enough protected media arrived to say anything");
            }
        }

        /// <summary>
        /// Sets a client up one to one and says which SSRC it was told to expect.
        /// </summary>
        private static (RtspTestClient Client, string Session, uint Ssrc, byte[] Key) JoinUnicast(int port, int clientPort)
        {
            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);

            var describe = client.Send("DESCRIBE", baseUri, "Accept: application/sdp");
            var crypto = Regex.Match(describe.Body ?? string.Empty, @"inline:([A-Za-z0-9+/=]+)");
            Assert.IsTrue(crypto.Success, describe.Body);

            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                $"Transport: RTP/AVP/TCP;unicast;interleaved={clientPort}-{clientPort + 1}");

            Assert.AreEqual(200, setup.StatusCode, "one to one delivery of a shared key stream is allowed");

            string transport = setup.Match(@"Transport:\s*([^\r\n]+)");
            var ssrc = Regex.Match(transport, @"ssrc=([0-9A-Fa-f]+)");
            Assert.IsTrue(ssrc.Success, "the reply should name the SSRC the client will hear: " + transport);

            return (client, setup.Session,
                uint.Parse(ssrc.Groups[1].Value, System.Globalization.NumberStyles.HexNumber),
                Convert.FromBase64String(crypto.Groups[1].Value));
        }

        [TestMethod]
        public void ClientsSentTheStreamOneToOneEachGetTheirOwnSsrc()
        {
            int port = TestPorts.FindFree();
            using var server = NewServer(port, out _, 56800);

            var first = JoinUnicast(port, 0);
            var second = JoinUnicast(port, 0);

            using (first.Client)
            using (second.Client)
            {
                // The same key, which is the point of a shared one - and different SSRCs, which is
                // what keeps it safe. SRTP works its keystream out from the key, the SSRC and the
                // packet number, so two senders that differ in their SSRC never share one.
                CollectionAssert.AreEqual(first.Key, second.Key, "they hold the same key");

                Assert.AreNotEqual(first.Ssrc, second.Ssrc,
                    "two senders under one key must not share an SSRC");
            }
        }

        [TestMethod]
        public void AClientSentTheStreamOneToOneCanReadItWithTheSharedKey()
        {
            int port = TestPorts.FindFree();
            using var server = NewServer(port, out var videoTrack, 56850);

            var joined = JoinUnicast(port, 0);

            using (joined.Client)
            {
                string baseUri = $"rtsp://127.0.0.1:{port}/stream1";
                Assert.AreEqual(200, joined.Client.Send("PLAY", baseUri, "Session: " + joined.Session).StatusCode);

                SrtpKeys keys = SrtpProtocol.CreateMasterKeys(SrtpCryptoSuites.AES_CM_128_HMAC_SHA1_80,
                    Array.Empty<byte>(), joined.Key);
                SrtpSessionContext context = SrtpProtocol.CreateSrtpSessionContext(keys);

                int read = 0;

                for (int i = 0; i < 12 && read < 3; i++)
                {
                    videoTrack.FeedInRawSamples((uint)((i + 1) * 3000),
                        new List<ReadOnlyMemory<byte>> { new ReadOnlyMemory<byte>(Nal) });

                    var frame = joined.Client.ReadInterleaved();

                    if (frame.Channel != 0)
                    {
                        continue;
                    }

                    byte[] packet = frame.Payload;

                    int result = context.DecodeRtpContext.UnprotectRtp(packet, packet.Length, out int length);
                    Assert.AreEqual(0, result, $"packet {read} did not authenticate under the shared key");

                    var payload = new ArraySegment<byte>(packet, 12, length - 12);
                    CollectionAssert.AreEqual(Nal, payload.ToArray(), $"packet {read} came back wrong");

                    read++;
                }

                Assert.IsGreaterThanOrEqualTo(3, read, "not enough arrived to say anything");
            }
        }

        [TestMethod]
        public void OneStreamCanBeSentToAGroupAndOneToOneAtTheSameTime()
        {
            int port = TestPorts.FindFree();
            using var server = NewServer(port, out var videoTrack, 56900);

            var group = Join(port);
            var alone = JoinUnicast(port, 0);

            using (group.Client)
            using (alone.Client)
            using (var wire = Listen(group.Destination, group.Port))
            {
                string baseUri = $"rtsp://127.0.0.1:{port}/stream1";
                group.Client.Send("PLAY", baseUri, "Session: " + group.Session);
                alone.Client.Send("PLAY", baseUri, "Session: " + alone.Session);

                Thread.Sleep(200);

                // The group sends under the track's SSRC and this client under one of its own, so the
                // two never protect a packet with the same keystream even though the key is one.
                Assert.AreNotEqual(0u, alone.Ssrc);

                SrtpKeys keys = SrtpProtocol.CreateMasterKeys(group.CryptoSuite, Array.Empty<byte>(),
                    group.MasterKeySalt);
                SrtpSessionContext groupContext = SrtpProtocol.CreateSrtpSessionContext(keys);

                uint timestamp = 0;

                void Feed() => videoTrack.FeedInRawSamples(
                    timestamp += 3000,
                    new List<ReadOnlyMemory<byte>> { new ReadOnlyMemory<byte>(Nal) });

                // The group first, feeding as it waits. Reading the one to one client is what used to
                // be interleaved with this, and it blocks for as long as its own socket allows - so
                // a group packet that was merely slow could run the whole loop out.
                byte[] toGroup = WaitForPacket(wire, Feed);

                bool fromGroup = toGroup != null;

                if (fromGroup)
                {
                    Assert.AreEqual(0,
                        groupContext.DecodeRtpContext.UnprotectRtp(toGroup, toGroup.Length, out _),
                        "the group's media should read under the key from the SDP");
                }

                bool fromUnicast = false;

                for (int i = 0; i < 20 && !fromUnicast; i++)
                {
                    Feed();

                    if (alone.Client.ReadInterleaved().Channel == 0)
                    {
                        fromUnicast = true;
                    }
                }

                Assert.IsTrue(fromGroup, "the group should still be sent the media");
                Assert.IsTrue(fromUnicast, "and so should the client that asked for it one to one");
            }
        }

        [TestMethod]
        public void WithoutASharedKeyAProtectedTrackStillWillNotGoToAGroup()
        {
            int port = TestPorts.FindFree();
            using var server = NewServer(port, out _, 56900, sharedKey: false);

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            var setup = client.Send("SETUP", baseUri + "/trackID=0", "Transport: RTP/AVP;multicast");

            // Each client was handed a key of its own, so there is nothing the group could send that
            // more than one of them could read.
            Assert.AreEqual(461, setup.StatusCode,
                "a key per client cannot protect a stream they all listen to");
        }

        [TestMethod]
        public void KeysStayPerClientForAnOrdinaryProtectedStream()
        {
            int port = TestPorts.FindFree();
            using var server = NewServer(port, out _, 57000, sharedKey: false);

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            byte[] KeyFor()
            {
                using var client = new RtspTestClient(port, "admin", "password");
                client.Send("OPTIONS", baseUri);
                var describe = client.Send("DESCRIBE", baseUri, "Accept: application/sdp");
                var crypto = Regex.Match(describe.Body ?? string.Empty, @"inline:([A-Za-z0-9+/=]+)");
                Assert.IsTrue(crypto.Success, describe.Body);
                return Convert.FromBase64String(crypto.Groups[1].Value);
            }

            // The default is unchanged, and it is the better one wherever it can be used: a key
            // belonging to one client cannot be used to read another one's media.
            CollectionAssert.AreNotEqual(KeyFor(), KeyFor(),
                "two clients of an ordinary protected stream should not share a key");
        }
    }
}
