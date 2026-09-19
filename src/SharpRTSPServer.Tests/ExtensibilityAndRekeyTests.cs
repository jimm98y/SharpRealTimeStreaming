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

using SharpSRTP.SRTP;
using System;
using System.Collections.Generic;

namespace SharpRTSPServer.Tests
{
    [TestClass]
    public sealed class ExtensibilityAndRekeyTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        [TestMethod]
        public void AsecondDescribeAdvertisesTheSameKeys()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password", false, null,
                SrtpCryptoSuites.AES_CM_128_HMAC_SHA1_80, null);

            var videoTrack = new H264Track(Sps, Pps) { RtpProfile = RtpProfiles.SAVP };
            server.AddStreamSource(new RTSPStreamSource("stream1", videoTrack, null));
            server.StartListen();

            string baseUri = "rtsp://127.0.0.1:" + port + "/stream1";
            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);

            string first = client.Send("DESCRIBE", baseUri, "Accept: application/sdp").Body;

            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            Assert.AreEqual(200, setup.StatusCode);
            Assert.AreEqual(200, client.Send("PLAY", baseUri, "Session: " + setup.Session).StatusCode);

            // Asking again used to derive fresh keys and replace the context of a stream that was
            // already playing, leaving the client holding keys that decrypted nothing.
            string second = client.Send("DESCRIBE", baseUri, "Accept: application/sdp").Body;

            string firstCrypto = Crypto(first);
            string secondCrypto = Crypto(second);

            Assert.IsNotNull(firstCrypto);
            Assert.AreEqual(firstCrypto, secondCrypto, "a second DESCRIBE must not rekey a stream in use");

            // and the stream still works
            videoTrack.FeedInRawSamples(9000, new List<ReadOnlyMemory<byte>>
            {
                new ReadOnlyMemory<byte>(new byte[] { 0x65, 0x11, 0x22, 0x33 })
            });

            for (int i = 0; i < 20; i++)
            {
                if (client.ReadInterleaved().Channel == 0)
                    return;
            }

            Assert.Fail("nothing arrived after the second DESCRIBE");
        }

        private static string Crypto(string sdp)
        {
            if (sdp == null)
                return null;

            foreach (string line in sdp.Split('\n'))
            {
                if (line.StartsWith("a=crypto:"))
                    return line.Trim();
            }
            return null;
        }

        [TestMethod]
        public void AThrowingMessageHandlerDoesNotFailTheRequest()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");
            server.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), null));

            int calls = 0;
            server.ReceivedRtspMessage += (s, e) =>
            {
                calls++;
                throw new InvalidOperationException("a handler someone else wrote");
            };

            server.StartListen();

            string baseUri = "rtsp://127.0.0.1:" + port + "/stream1";
            using var client = new RtspTestClient(port, "admin", "password");

            // The event is a point of extensibility, so the code behind it is not the server's. It
            // used to turn a request the server had handled correctly into a 500.
            Assert.AreEqual(200, client.Send("OPTIONS", baseUri).StatusCode);
            Assert.AreEqual(200, client.Send("DESCRIBE", baseUri, "Accept: application/sdp").StatusCode);

            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            Assert.AreEqual(200, setup.StatusCode);
            Assert.AreEqual(200, client.Send("PLAY", baseUri, "Session: " + setup.Session).StatusCode);

            Assert.IsGreaterThan(0, calls, "the handler should still have been called");
        }
    }
}
