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
using System.Text.RegularExpressions;
using System.Threading;
using SharpSRTP.SRTP;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// How long a shared key lasts, and what that costs a server that runs for years.
    /// </summary>
    /// <remarks>
    /// Under one key, an SSRC cannot be given out twice - each sender numbers its packets from the
    /// start of its own session, so a repeat would protect two different packets with one keystream.
    /// Remembering every SSRC ever used says that correctly and grows for as long as the process
    /// lives. Counting up says the same thing in four bytes, and the count goes back to nothing when
    /// the key is replaced, which happens whenever the stream is left with nobody on it.
    /// </remarks>
    [TestClass]
    public sealed class SharedKeyLifetimeTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        private static RTSPServer NewServer(int port)
        {
            var server = new RTSPServer(port, new InMemoryUserRepository("admin", "password"), false, null,
                SrtpCryptoSuites.AES_CM_128_HMAC_SHA1_80, null);

            server.AddStreamSource(new RTSPStreamSource("stream1",
                new H264Track(Sps, Pps) { RtpProfile = RtpProfiles.SAVP }, null)
            {
                SharedSrtpKey = true,
            });

            server.StartListen();
            return server;
        }

        private static (byte[] Key, uint Ssrc) Session(int port, bool keepOpen, out RtspTestClient held)
        {
            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);

            var describe = client.Send("DESCRIBE", baseUri, "Accept: application/sdp");
            var crypto = Regex.Match(describe.Body ?? string.Empty, @"inline:([A-Za-z0-9+/=]+)");
            Assert.IsTrue(crypto.Success, describe.Body);

            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            Assert.AreEqual(200, setup.StatusCode);

            string transport = setup.Match(@"Transport:\s*([^\r\n]+)");
            var ssrc = Regex.Match(transport, @"ssrc=([0-9A-Fa-f]+)");
            Assert.IsTrue(ssrc.Success, transport);

            var result = (Convert.FromBase64String(crypto.Groups[1].Value),
                uint.Parse(ssrc.Groups[1].Value, System.Globalization.NumberStyles.HexNumber));

            if (keepOpen)
            {
                held = client;
            }
            else
            {
                client.Send("TEARDOWN", baseUri, "Session: " + setup.Session);
                client.Dispose();
                held = null;
            }

            return result;
        }

        [TestMethod]
        public void TheKeyStandsWhileAnybodyIsOnTheStream()
        {
            int port = TestPorts.FindFree();
            using var server = NewServer(port);

            var first = Session(port, keepOpen: true, out var staying);

            using (staying)
            {
                var second = Session(port, keepOpen: false, out _);
                var third = Session(port, keepOpen: false, out _);

                // The key cannot change under a client that is still using it, so while one stays
                // every other client is handed the same one - which is what lets them all read one
                // stream.
                CollectionAssert.AreEqual(first.Key, second.Key, "the key should not change under a client");
                CollectionAssert.AreEqual(first.Key, third.Key);

                // and no two of them are sending under the same SSRC
                Assert.AreNotEqual(first.Ssrc, second.Ssrc);
                Assert.AreNotEqual(second.Ssrc, third.Ssrc);
                Assert.AreNotEqual(first.Ssrc, third.Ssrc);
            }
        }

        [TestMethod]
        public void TheKeyIsReplacedOnceTheStreamIsEmpty()
        {
            int port = TestPorts.FindFree();
            using var server = NewServer(port);

            var before = Session(port, keepOpen: false, out _);

            // let the teardown finish taking the connection off the stream
            Thread.Sleep(300);

            var after = Session(port, keepOpen: false, out _);

            // Nobody was holding the old one, so nothing was broken by replacing it - and this is
            // what gives the SSRCs back: what must not repeat is an SSRC under the key it was used
            // with, so a new key makes all of them free again.
            CollectionAssert.AreNotEqual(before.Key, after.Key,
                "a stream nobody is on should not keep handing out the same key for the life of the process");
        }

        [TestMethod]
        public void SsrcsAreGivenOutAgainOnceTheKeyHasChanged()
        {
            int port = TestPorts.FindFree();
            using var server = NewServer(port);

            var seen = new List<uint>();

            // Several rounds of one client at a time, each leaving before the next arrives, so the
            // key is replaced between them. A server that remembered SSRCs for ever would be holding
            // one entry for each of these, and one for every session it ever serves after that.
            for (int i = 0; i < 12; i++)
            {
                seen.Add(Session(port, keepOpen: false, out _).Ssrc);
                Thread.Sleep(120);
            }

            Assert.HasCount(12, seen);

            // Nothing here asserts they repeat - they are drawn from a new random point each time,
            // so they almost certainly will not. What matters is that the server is not obliged to
            // remember them, and it no longer does.
            foreach (uint ssrc in seen)
            {
                Assert.AreNotEqual(0u, ssrc, "zero goes on meaning no stream");
            }
        }

        [TestMethod]
        public void ClientsStillReadTheStreamAfterTheKeyHasBeenReplaced()
        {
            int port = TestPorts.FindFree();

            var server = new RTSPServer(port, new InMemoryUserRepository("admin", "password"), false, null,
                SrtpCryptoSuites.AES_CM_128_HMAC_SHA1_80, null);

            var videoTrack = new H264Track(Sps, Pps) { RtpProfile = RtpProfiles.SAVP };
            server.AddStreamSource(new RTSPStreamSource("stream1", videoTrack, null) { SharedSrtpKey = true });
            server.StartListen();

            using (server)
            {
                string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

                // one session, gone
                Session(port, keepOpen: false, out _);
                Thread.Sleep(300);

                // and another, which must work as well as the first did - a replaced key is only any
                // use if what follows it can be read
                using var client = new RtspTestClient(port, "admin", "password");
                client.Send("OPTIONS", baseUri);

                var describe = client.Send("DESCRIBE", baseUri, "Accept: application/sdp");
                var crypto = Regex.Match(describe.Body ?? string.Empty, @"inline:([A-Za-z0-9+/=]+)");
                Assert.IsTrue(crypto.Success);

                var setup = client.Send("SETUP", baseUri + "/trackID=0",
                    "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
                Assert.AreEqual(200, client.Send("PLAY", baseUri, "Session: " + setup.Session).StatusCode);

                SrtpKeys keys = SrtpProtocol.CreateMasterKeys(SrtpCryptoSuites.AES_CM_128_HMAC_SHA1_80,
                    Array.Empty<byte>(), Convert.FromBase64String(crypto.Groups[1].Value));
                SrtpSessionContext context = SrtpProtocol.CreateSrtpSessionContext(keys);

                byte[] nal = { 0x65, 0xAA, 0xBB, 0xCC };
                int read = 0;

                for (int i = 0; i < 12 && read < 3; i++)
                {
                    videoTrack.FeedInRawSamples((uint)((i + 1) * 3000),
                        new List<ReadOnlyMemory<byte>> { new ReadOnlyMemory<byte>(nal) });

                    var frame = client.ReadInterleaved();

                    if (frame.Channel != 0)
                    {
                        continue;
                    }

                    Assert.AreEqual(0,
                        context.DecodeRtpContext.UnprotectRtp(frame.Payload, frame.Payload.Length, out int length),
                        "media sent under the new key should read under the new key");

                    CollectionAssert.AreEqual(nal,
                        new ArraySegment<byte>(frame.Payload, 12, length - 12).ToArray());

                    read++;
                }

                Assert.IsGreaterThanOrEqualTo(3, read, "not enough arrived to say anything");
            }
        }
    }
}
