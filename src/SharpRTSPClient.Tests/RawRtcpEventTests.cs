using System;
using System.Buffers;
using System.Linq;
using System.Reflection;
using Rtsp;
using Rtsp.Messages;
using SharpSRTP.SRTP;
using SharpRTSPClient;

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
