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
                var from = new IPEndPoint(IPAddress.Any, 0);

                for (int i = 0; i < 12 && read < 4; i++)
                {
                    videoTrack.FeedInRawSamples((uint)((i + 1) * 3000),
                        new List<ReadOnlyMemory<byte>> { new ReadOnlyMemory<byte>(Nal) });

                    byte[] packet;

                    try
                    {
                        packet = wire.Receive(ref from);
                    }
                    catch (SocketException)
                    {
                        continue;
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

        [TestMethod]
        public void AStreamWhoseKeyIsSharedDoesNotGoOutOneToOne()
        {
            int port = TestPorts.FindFree();
            using var server = NewServer(port, out _, 56800);

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            // Every client holds the same key here. Sending to several of them separately would have
            // them all protecting packets with that key under the same SSRC while numbering
            // independently, so one keystream would cover two different packets - worse than sending
            // nothing at all.
            var unicast = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP;unicast;client_port=41800-41801");

            Assert.AreEqual(461, unicast.StatusCode,
                "a shared key and one to one delivery do not go together");
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
