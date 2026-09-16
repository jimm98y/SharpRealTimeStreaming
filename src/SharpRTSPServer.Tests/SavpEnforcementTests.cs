using System;
using System.Collections.Generic;
using SharpSRTP.SRTP;
using SharpRTSPServer;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// A server told to use SRTP must never send the media unprotected instead.
    /// </summary>
    /// <remarks>
    /// The keys are handed to the client in the SDP, so everything here turns on whether the SDP the
    /// client actually received carried them. An unprotected packet is spotted by its length: the
    /// AES_CM_128_HMAC_SHA1_80 suite appends a ten byte authentication tag, so protected RTP is ten
    /// bytes longer than what went in.
    /// </remarks>
    [TestClass]
    public sealed class SavpEnforcementTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        private const int UnprotectedLength = 16; // 12 byte RTP header plus a 4 byte NAL
        private const int ProtectedLength = UnprotectedLength + 10;

        private const string OverriddenSdp =
            "v=0\r\no=- 0 0 IN IP4 0.0.0.0\r\ns=Test\r\nc=IN IP4 0.0.0.0\r\n" +
            "m=video 0 RTP/SAVP 96\r\na=control:trackID=0\r\na=rtpmap:96 H264/90000\r\n";

        private static RTSPServer NewSavpServer(int port, out H264Track videoTrack, bool overrideSdp)
        {
            var server = new RTSPServer(port, "admin", "password", false, null,
                SrtpCryptoSuites.AES_CM_128_HMAC_SHA1_80, null);

            videoTrack = new H264Track(Sps, Pps) { RtpProfile = RtpProfiles.SAVP };
            var streamSource = new RTSPStreamSource("stream1", videoTrack, null);
            if (overrideSdp)
            {
                streamSource.OverrideSDP(OverriddenSdp, true);
            }

            server.AddStreamSource(streamSource);
            server.StartListen();
            return server;
        }

        private static void FeedOneNal(H264Track track)
        {
            track.FeedInRawSamples(9000, new List<ReadOnlyMemory<byte>>
            {
                new ReadOnlyMemory<byte>(new byte[] { 0x65, 0x11, 0x22, 0x33 })
            });
        }

        private static int FirstRtpLength(RtspTestClient client)
        {
            for (int i = 0; i < 20; i++)
            {
                var frame = client.ReadInterleaved();
                if (frame.Channel == 0)
                    return frame.Payload.Length;
            }

            Assert.Fail("no RTP arrived");
            return -1;
        }

        [TestMethod]
        public void MediaIsProtectedWhenTheSdpIsGenerated()
        {
            int port = TestPorts.FindFree();
            using var server = NewSavpServer(port, out var videoTrack, overrideSdp: false);

            string baseUri = "rtsp://127.0.0.1:" + port + "/stream1";
            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            Assert.AreEqual(200, setup.StatusCode);
            Assert.AreEqual(200, client.Send("PLAY", baseUri, "Session: " + setup.Session).StatusCode);

            FeedOneNal(videoTrack);

            Assert.AreEqual(ProtectedLength, FirstRtpLength(client), "the RTP should carry an SRTP auth tag");
        }

        [TestMethod]
        public void MediaIsProtectedWhenTheSdpIsOverridden()
        {
            int port = TestPorts.FindFree();
            using var server = NewSavpServer(port, out var videoTrack, overrideSdp: true);

            string baseUri = "rtsp://127.0.0.1:" + port + "/stream1";
            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);

            // An overridden SDP used to be returned untouched, so no keys were ever derived and the
            // media went out in the clear - which is every ProxyTrack deployment.
            var describe = client.Send("DESCRIBE", baseUri, "Accept: application/sdp");
            Assert.AreEqual(200, describe.StatusCode);

            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            Assert.AreEqual(200, setup.StatusCode);
            Assert.AreEqual(200, client.Send("PLAY", baseUri, "Session: " + setup.Session).StatusCode);

            FeedOneNal(videoTrack);

            Assert.AreEqual(ProtectedLength, FirstRtpLength(client), "the RTP should carry an SRTP auth tag");
        }

        [TestMethod]
        public void TheOverriddenSdpGainsACryptoAttribute()
        {
            int port = TestPorts.FindFree();
            using var server = NewSavpServer(port, out _, overrideSdp: true);

            string baseUri = "rtsp://127.0.0.1:" + port + "/stream1";
            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);

            var describe = client.Send("DESCRIBE", baseUri, "Accept: application/sdp");
            Assert.AreEqual(200, describe.StatusCode);

            Assert.IsNotNull(describe.Body, "the SDP should come back with the response");
            Assert.Contains("a=crypto:1 ", describe.Body,
                "the overridden SDP has to gain the keys, otherwise the client has no way to decrypt");
            Assert.Contains("inline:", describe.Body);
        }

        [TestMethod]
        public void SetupOfAnSavpTrackIsRefusedWhenTheClientNeverAskedForTheSdp()
        {
            int port = TestPorts.FindFree();
            using var server = NewSavpServer(port, out _, overrideSdp: false);

            string baseUri = "rtsp://127.0.0.1:" + port + "/stream1";
            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);

            // No DESCRIBE, so this client holds no key. It used to be set up anyway and then served
            // unencrypted RTP.
            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");

            Assert.AreEqual(400, setup.StatusCode, "a SAVP track cannot be set up without keys");
        }

        [TestMethod]
        public void AnAvpTrackIsStillSetUpWithoutADescribe()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");
            var videoTrack = new H264Track(Sps, Pps);
            server.AddStreamSource(new RTSPStreamSource("stream1", videoTrack, null));
            server.StartListen();

            string baseUri = "rtsp://127.0.0.1:" + port + "/stream1";
            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);

            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            Assert.AreEqual(200, setup.StatusCode, "an unencrypted track needs no keys and so no DESCRIBE");

            Assert.AreEqual(200, client.Send("PLAY", baseUri, "Session: " + setup.Session).StatusCode);
            FeedOneNal(videoTrack);

            Assert.AreEqual(UnprotectedLength, FirstRtpLength(client));
        }
    }
}
