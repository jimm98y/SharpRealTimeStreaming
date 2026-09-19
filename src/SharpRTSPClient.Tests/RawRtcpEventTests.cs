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
    /// The raw RTCP events hand out what the client made of the payload, not what arrived on the wire.
    /// </summary>
    /// <remarks>
    /// Three of the four raw data events passed the unprotected copy and one passed the buffer as it
    /// arrived, so under SAVP a subscriber was given ciphertext for audio RTCP and plaintext for
    /// everything else. Without SRTP the two are the same object, which is why it went unnoticed -
    /// so these tests set a real context up and protect the packet, or they would not tell the
    /// difference either.
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

        /// <summary>A sender report, which is the packet type the RTCP handlers act on.</summary>
        private static byte[] SenderReport()
        {
            var packet = new byte[28];
            packet[0] = 0x80;
            packet[1] = 200;
            packet[3] = 0x06; // (28 / 4) - 1
            return packet;
        }

        private static void SetContext(RTSPClient client, string propertyName, SrtpSessionContext context)
        {
            PropertyInfo property = typeof(RTSPClient).GetProperty(propertyName);
            Assert.IsNotNull(property, propertyName + " is not where the test expects it");
            property.SetValue(client, context);
        }

        private static void Raise(RTSPClient client, string handlerName, byte[] payload)
        {
            MethodInfo handler = typeof(RTSPClient).GetMethod(handlerName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(handler, handlerName + " is not where the test expects it");

            var data = new RtspData(new Owner(payload), payload.Length);
            handler.Invoke(client, new object[] { null, new RtspDataEventArgs(data) });
        }

        /// <summary>
        /// Drives one handler with a protected report and gives back what its event carried.
        /// </summary>
        private static (byte[] Plaintext, byte[] OnTheWire, byte[] Seen) Exchange(string contextProperty, string handlerName)
        {
            // both ends of the same session share the keys, so what one protects the other unprotects
            SrtpKeys keys = SrtpProtocol.CreateMasterKeys(CryptoSuite, SrtpProtocol.GenerateMki(0));
            var sender = SrtpProtocol.CreateSrtpSessionContext(keys);
            var receiver = SrtpProtocol.CreateSrtpSessionContext(keys);

            byte[] plaintext = SenderReport();
            byte[] onTheWire = RTSPClient.ProtectRtcp(sender, plaintext);

            using var client = new RTSPClient { ProcessRTCP = false };
            SetContext(client, contextProperty, receiver);

            byte[] seen = null;
            if (handlerName.StartsWith("Video"))
            {
                client.ReceivedRawVideoRTCP += (s, e) => seen = e.Data.ToArray();
            }
            else
            {
                client.ReceivedRawAudioRTCP += (s, e) => seen = e.Data.ToArray();
            }

            Raise(client, handlerName, onTheWire.ToArray());
            return (plaintext, onTheWire, seen);
        }

        [TestMethod]
        public void TheVideoRtcpEventCarriesTheUnprotectedReport()
        {
            var exchange = Exchange("VideoContext", "VideoRtcpControlDataReceived");

            Assert.IsNotNull(exchange.Seen, "the event should have been raised");
            CollectionAssert.AreEqual(exchange.Plaintext, exchange.Seen);
        }

        [TestMethod]
        public void TheAudioRtcpEventCarriesTheUnprotectedReport()
        {
            var exchange = Exchange("AudioContext", "AudioRtcpControlDataReceived");

            Assert.IsNotNull(exchange.Seen, "the event should have been raised");
            CollectionAssert.AreNotEqual(exchange.OnTheWire, exchange.Seen,
                "a subscriber was being handed the packet still encrypted");
            CollectionAssert.AreEqual(exchange.Plaintext, exchange.Seen);
        }

        [TestMethod]
        public void TheProtectedAndUnprotectedFormsReallyDiffer()
        {
            // otherwise the two tests above would pass whatever the handlers did
            var exchange = Exchange("AudioContext", "AudioRtcpControlDataReceived");

            CollectionAssert.AreNotEqual(exchange.Plaintext, exchange.OnTheWire,
                "the test is not exercising SRTP at all if these match");
        }
    }
}
