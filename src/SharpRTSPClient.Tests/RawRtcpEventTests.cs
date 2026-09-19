// SharpRTSPClient
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
using System.Buffers;
using System.Linq;
using System.Reflection;
using Rtsp;
using Rtsp.Messages;
using SharpSRTP.SRTP;

namespace SharpRTSPClient.Tests
{
    /// <summary>
    /// The raw RTCP event hands out what the client made of the payload, not what arrived on the wire.
    /// </summary>
    /// <remarks>
    /// Three of the four raw data events used to pass the unprotected copy and one passed the buffer
    /// as it arrived, so under SAVP a subscriber was given ciphertext for audio RTCP and plaintext
    /// for everything else. Without SRTP the two are the same object, which is why it went unnoticed
    /// - so these tests set a real context up and protect the packet, or they would not tell the
    /// difference either. The four events are now one, but the packet still has to arrive decrypted.
    /// </remarks>
    [TestClass]
    public sealed class RawRtcpEventTests
    {
        private const string CryptoSuite = SrtpCryptoSuites.AES_CM_128_HMAC_SHA1_80;

        private sealed class Owner : IMemoryOwner<byte>
        {
            private readonly byte[] _buffer;
            public Owner(byte[] buffer) { _buffer = buffer; }
            public Memory<byte> Memory => _buffer.AsMemory();
            public void Dispose() { }
        }

        /// <summary>A sender report, which is the packet type the RTCP handler acts on.</summary>
        private static byte[] SenderReport()
        {
            var packet = new byte[28];
            packet[0] = 0x80;
            packet[1] = 200;
            packet[3] = 0x06; // (28 / 4) - 1
            return packet;
        }

        /// <summary>
        /// Hands a protected report to the receive path of a track and reports what the event carried.
        /// </summary>
        /// <remarks>
        /// This used to name one of a pair of by-kind handlers and set VideoContext or AudioContext
        /// by reflection. Both are gone - a track carries its own keys and its own index - so it
        /// drives the one handler with the track it means.
        /// </remarks>
        private static (byte[] Plaintext, byte[] OnTheWire, byte[] Seen, int TrackIndex) Exchange(TrackKind kind)
        {
            // both ends of the same session share the keys, so what one protects the other unprotects
            SrtpKeys keys = SrtpProtocol.CreateMasterKeys(CryptoSuite, SrtpProtocol.GenerateMki(0));
            var sender = SrtpProtocol.CreateSrtpSessionContext(keys);
            var receiver = SrtpProtocol.CreateSrtpSessionContext(keys);

            byte[] plaintext = SenderReport();
            byte[] onTheWire = RTSPClient.ProtectRtcp(sender, plaintext);

            using var client = new RTSPClient { ProcessRTCP = false };

            ClientTrack track = client.AddTrack(kind);
            track.Context = receiver;

            byte[] seen = null;
            int trackIndex = -1;
            client.ReceivedRawRTCP += (s, e) => { seen = e.Data.Data.ToArray(); trackIndex = e.TrackIndex; };

            MethodInfo handler = typeof(RTSPClient).GetMethod("RtcpControlDataReceived",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(handler, "RtcpControlDataReceived is not where the test expects it");

            var data = new RtspData(new Owner(onTheWire.ToArray()), onTheWire.Length);
            handler.Invoke(client, new object[] { track, null, new RtspDataEventArgs(data) });

            return (plaintext, onTheWire, seen, trackIndex);
        }

        [TestMethod]
        public void TheRtcpEventCarriesTheUnprotectedReportForAVideoTrack()
        {
            var exchange = Exchange(TrackKind.Video);

            Assert.IsNotNull(exchange.Seen, "the event should have been raised");
            CollectionAssert.AreEqual(exchange.Plaintext, exchange.Seen);
        }

        [TestMethod]
        public void TheRtcpEventCarriesTheUnprotectedReportForAnAudioTrack()
        {
            var exchange = Exchange(TrackKind.Audio);

            Assert.IsNotNull(exchange.Seen, "the event should have been raised");
            CollectionAssert.AreNotEqual(exchange.OnTheWire, exchange.Seen,
                "a subscriber was being handed the packet still encrypted");
            CollectionAssert.AreEqual(exchange.Plaintext, exchange.Seen);
        }

        [TestMethod]
        public void TheEventSaysWhichTrackTheReportArrivedOn()
        {
            var exchange = Exchange(TrackKind.Audio);

            Assert.AreEqual(0, exchange.TrackIndex, "the only track set up is the first one");
        }

        [TestMethod]
        public void TheProtectedAndUnprotectedFormsReallyDiffer()
        {
            // otherwise the tests above would pass whatever the handler did
            var exchange = Exchange(TrackKind.Audio);

            CollectionAssert.AreNotEqual(exchange.Plaintext, exchange.OnTheWire,
                "the test is not exercising SRTP at all if these match");
        }
    }
}
