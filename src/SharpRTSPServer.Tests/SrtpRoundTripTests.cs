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
using SharpSRTP.SRTP;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// What the server protects, a client holding the keys from the SDP can read back.
    /// </summary>
    /// <remarks>
    /// The other SRTP tests here ask whether the media was protected at all, which they answer from
    /// the length - the authentication tag makes it ten bytes longer. That catches a server sending
    /// in the clear, and would not notice a server whose ciphertext nobody can decrypt. This one
    /// decrypts it and compares what comes out with what went in.
    /// </remarks>
    [TestClass]
    public sealed class SrtpRoundTripTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        /// <summary>One NAL, small enough to go out as a single packet.</summary>
        private static readonly byte[] Nal = { 0x65, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77 };

        [TestMethod]
        public void ProtectedMediaDecryptsBackToWhatWasFedIn()
        {
            int port = TestPorts.FindFree();

            using var server = new RTSPServer(port, "admin", "password", false, null,
                SrtpCryptoSuites.AES_CM_128_HMAC_SHA1_80, null);

            var videoTrack = new H264Track(Sps, Pps) { RtpProfile = RtpProfiles.SAVP };
            server.AddStreamSource(new RTSPStreamSource("stream1", videoTrack, null));
            server.StartListen();

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);

            var describe = client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            // the keys the server told this client to use
            var crypto = Regex.Match(describe.Body ?? string.Empty,
                @"a=crypto:\d+\s+(\S+)\s+inline:([A-Za-z0-9+/=]+)");

            Assert.IsTrue(crypto.Success, "the SDP should carry the keys: " + describe.Body);

            string cryptoSuite = crypto.Groups[1].Value;
            byte[] masterKeySalt = Convert.FromBase64String(crypto.Groups[2].Value);

            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            Assert.AreEqual(200, setup.StatusCode);
            Assert.AreEqual(200, client.Send("PLAY", baseUri, $"Session: {setup.Session}").StatusCode);

            // Several, because the buffers the protecting borrows are reused between packets - one
            // packet would not notice a buffer handed back while still in use, or one whose previous
            // contents were left in the part beyond what was written.
            const int Frames = 12;

            for (int i = 0; i < Frames; i++)
            {
                videoTrack.FeedInRawSamples((uint)((i + 1) * 3000), new List<ReadOnlyMemory<byte>>
                {
                    new ReadOnlyMemory<byte>(Nal),
                });
            }

            SrtpKeys keys = SrtpProtocol.CreateMasterKeys(cryptoSuite, Array.Empty<byte>(), masterKeySalt);
            SrtpSessionContext context = SrtpProtocol.CreateSrtpSessionContext(keys);

            int decrypted = 0;

            for (int i = 0; i < Frames * 3 && decrypted < Frames; i++)
            {
                var frame = client.ReadInterleaved();

                if (frame.Channel != 0)
                {
                    continue;
                }

                byte[] buffer = new byte[frame.Payload.Length];
                frame.Payload.CopyTo(buffer, 0);

                int result = context.DecodeRtpContext.UnprotectRtp(buffer, buffer.Length, out int length);

                Assert.AreEqual(0, result, $"packet {decrypted} did not authenticate");

                // what is left after the 12 byte RTP header is the NAL that went in
                var payload = new ArraySegment<byte>(buffer, 12, length - 12);

                CollectionAssert.AreEqual(Nal, payload.ToArray(),
                    $"packet {decrypted} decrypted to something other than what was fed in");

                decrypted++;
            }

            Assert.AreEqual(Frames, decrypted, "not every packet came back");
        }
    }
}
