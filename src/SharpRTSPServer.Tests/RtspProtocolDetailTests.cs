using System;
using System.Collections.Generic;
using SharpRTSPServer;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// Details of what the server puts on the wire that a client is entitled to rely on.
    /// </summary>
    [TestClass]
    public sealed class RtspProtocolDetailTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        private static RTSPServer NewServer(int port, bool withAudio)
        {
            var server = new RTSPServer(port, "admin", "password");
            var audio = withAudio ? new AACTrack(new byte[] { 0x12, 0x10 }, 44100, 2) : null;
            server.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), audio));
            server.StartListen();
            return server;
        }

        [TestMethod]
        public void OptionsOnTheBaseUrlIsAnswered()
        {
            int port = TestPorts.FindFree();
            using var server = NewServer(port, withAudio: false);

            // OPTIONS is not about any one stream. Clients and health checks send it to the base URL,
            // which used to be answered 404 because the stream was looked up before the method was.
            using var client = new RtspTestClient(port, "admin", "password");
            var response = client.Send("OPTIONS", "rtsp://127.0.0.1:" + port + "/");

            Assert.AreEqual(200, response.StatusCode);
            Assert.Contains("Public:", response.Headers, "the reply should say what the server supports");
        }

        [TestMethod]
        public void OptionsOnAnUnknownStreamIsStillAnswered()
        {
            int port = TestPorts.FindFree();
            using var server = NewServer(port, withAudio: false);

            using var client = new RtspTestClient(port, "admin", "password");
            Assert.AreEqual(200, client.Send("OPTIONS", "rtsp://127.0.0.1:" + port + "/nosuchstream").StatusCode);
        }

        [TestMethod]
        public void DescribeOnAnUnknownStreamIsStillNotFound()
        {
            int port = TestPorts.FindFree();
            using var server = NewServer(port, withAudio: false);

            using var client = new RtspTestClient(port, "admin", "password");
            Assert.AreEqual(404, client.Send("DESCRIBE", "rtsp://127.0.0.1:" + port + "/nosuchstream",
                "Accept: application/sdp").StatusCode);
        }

        [TestMethod]
        public void RtpInfoNamesOnlyTheTracksThatWereSetUp()
        {
            int port = TestPorts.FindFree();
            using var server = NewServer(port, withAudio: true);

            string baseUri = "rtsp://127.0.0.1:" + port + "/stream1";
            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            // only the video track, although the source has audio too
            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            Assert.AreEqual(200, setup.StatusCode);

            var play = client.Send("PLAY", baseUri, "Session: " + setup.Session);
            Assert.AreEqual(200, play.StatusCode);

            string rtpInfo = play.Match(@"RTP-Info:\s*(.+)");
            Assert.IsNotNull(rtpInfo);

            // It used to name both tracks under the session URL whatever had been set up, so a client
            // was told about an audio stream that would never send.
            Assert.Contains("trackID=0", rtpInfo, "the video track it set up should be named");
            Assert.DoesNotContain("trackID=1", rtpInfo, "the audio track was never set up");
        }

        [TestMethod]
        public void RtpInfoNamesBothTracksWhenBothWereSetUp()
        {
            int port = TestPorts.FindFree();
            using var server = NewServer(port, withAudio: true);

            string baseUri = "rtsp://127.0.0.1:" + port + "/stream1";
            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            var video = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            client.Send("SETUP", baseUri + "/trackID=1",
                "Transport: RTP/AVP/TCP;unicast;interleaved=2-3", "Session: " + video.Session);

            var play = client.Send("PLAY", baseUri, "Session: " + video.Session);
            string rtpInfo = play.Match(@"RTP-Info:\s*(.+)");

            Assert.Contains("trackID=0", rtpInfo);
            Assert.Contains("trackID=1", rtpInfo);
        }

        [TestMethod]
        public void TheGeneratedSdpUsesCrlfThroughout()
        {
            int port = TestPorts.FindFree();
            using var server = NewServer(port, withAudio: true);

            string baseUri = "rtsp://127.0.0.1:" + port + "/stream1";
            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);

            var describe = client.Send("DESCRIBE", baseUri, "Accept: application/sdp");
            Assert.AreEqual(200, describe.StatusCode);
            Assert.IsNotNull(describe.Body);

            // RFC 4566 says CRLF. The session lines and the track lines used to be written with a
            // bare LF while the crypto attribute went through AppendLine, so one document carried
            // both - and on Linux the MJpeg track produced different bytes from the same build.
            string body = describe.Body;
            for (int i = 0; i < body.Length; i++)
            {
                if (body[i] == '\n')
                {
                    Assert.IsTrue(i > 0 && body[i - 1] == '\r', "found a bare LF at offset " + i);
                }
            }

            Assert.Contains("\r\n", body, "the SDP should have line endings at all");
        }

        [TestMethod]
        public void MediaFedTheMomentPlayIsAnsweredStillReachesTheClient()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");
            var videoTrack = new H264Track(Sps, Pps);
            server.AddStreamSource(new RTSPStreamSource("stream1", videoTrack, null));
            server.StartListen();

            string baseUri = "rtsp://127.0.0.1:" + port + "/stream1";
            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            Assert.AreEqual(200, client.Send("PLAY", baseUri, "Session: " + setup.Session).StatusCode);

            // The session used to be marked as playing after the response had gone out, so a sample
            // produced right here fell into the gap and was dropped.
            videoTrack.FeedInRawSamples(9000, new List<ReadOnlyMemory<byte>>
            {
                new ReadOnlyMemory<byte>(new byte[] { 0x65, 0x11, 0x22, 0x33 })
            });

            for (int i = 0; i < 20; i++)
            {
                if (client.ReadInterleaved().Channel == 0)
                    return;
            }

            Assert.Fail("the sample fed immediately after PLAY never arrived");
        }
    }
}
