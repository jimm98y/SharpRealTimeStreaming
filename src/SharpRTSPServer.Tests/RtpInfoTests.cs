using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using SharpRTSPServer;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// The RTP-Info of a PLAY, which is what a receiver lines the tracks up by before any RTCP has
    /// arrived.
    /// </summary>
    /// <remarks>
    /// It used to carry the sequence number alone. Without the rtptime beside it there is nothing
    /// pairing a track's RTP clock with the start of the range, so a receiver has to wait for the
    /// first sender report on every track before it can put audio and video on the same timeline.
    /// </remarks>
    [TestClass]
    public sealed class RtpInfoTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        private static List<ReadOnlyMemory<byte>> OneNal() => new List<ReadOnlyMemory<byte>>
        {
            new ReadOnlyMemory<byte>(new byte[] { 0x65, 0x11, 0x22, 0x33 }),
        };

        [TestMethod]
        public void RtpInfoCarriesTheTimestampTheTrackHasReached()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");
            var videoTrack = new H264Track(Sps, Pps);
            server.AddStreamSource(new RTSPStreamSource("stream1", videoTrack, null));
            server.StartListen();

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            // The source is already running when this client arrives, which is the ordinary case for
            // live media and the only one where there is a timestamp to report.
            const uint Produced = 123456u;
            videoTrack.FeedInRawSamples(Produced, OneNal());
            Thread.Sleep(100);

            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");
            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");

            var play = client.Send("PLAY", baseUri, $"Session: {setup.Session}");
            Assert.AreEqual(200, play.StatusCode);

            string rtpInfo = play.Match(@"RTP-Info:\s*([^\r\n]+)");
            Assert.IsNotNull(rtpInfo, "PLAY should carry RTP-Info");

            var rtptime = Regex.Match(rtpInfo, @"rtptime=(\d+)");
            Assert.IsTrue(rtptime.Success, "RTP-Info should name the RTP timestamp: " + rtpInfo);
            Assert.AreEqual(Produced, uint.Parse(rtptime.Groups[1].Value),
                "the timestamp should be the one the track has reached");

            Assert.Contains("seq=", rtpInfo, "the sequence number should still be there: " + rtpInfo);
        }

        [TestMethod]
        public void ATrackThatHasProducedNothingNamesNoTimestamp()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");
            server.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), null));
            server.StartListen();

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");
            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");

            var play = client.Send("PLAY", baseUri, $"Session: {setup.Session}");
            string rtpInfo = play.Match(@"RTP-Info:\s*([^\r\n]+)");

            Assert.IsNotNull(rtpInfo);

            // Saying nothing beats naming a time that means nothing - a receiver told rtptime=0 would
            // line the track up against a point the media never passed through.
            Assert.DoesNotContain("rtptime=", rtpInfo,
                "a track that has sent nothing has no timestamp to report: " + rtpInfo);
        }

        [TestMethod]
        public void EachTrackReportsItsOwnClock()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");

            var videoTrack = new H264Track(Sps, Pps);
            var audioTrack = new PCMUTrack();
            server.AddStreamSource(new RTSPStreamSource("stream1", videoTrack, audioTrack));
            server.StartListen();

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            // Different clocks, which is the whole point of reporting one per track - 90kHz for the
            // video and 8kHz for this audio, so the two numbers are nothing like each other.
            const uint VideoAt = 900000u;
            const uint AudioAt = 80000u;

            videoTrack.FeedInRawSamples(VideoAt, OneNal());
            audioTrack.FeedInRawSamples(AudioAt, new List<ReadOnlyMemory<byte>>
            {
                new ReadOnlyMemory<byte>(new byte[160]),
            });

            Thread.Sleep(150);

            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");
            var videoSetup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            client.Send("SETUP", baseUri + "/trackID=1",
                $"Transport: RTP/AVP/TCP;unicast;interleaved=2-3\r\nSession: {videoSetup.Session}");

            var play = client.Send("PLAY", baseUri, $"Session: {videoSetup.Session}");
            string rtpInfo = play.Match(@"RTP-Info:\s*([^\r\n]+)");

            Assert.IsNotNull(rtpInfo);

            var times = Regex.Matches(rtpInfo, @"rtptime=(\d+)");
            Assert.HasCount(2, times, "both tracks should report a timestamp: " + rtpInfo);

            Assert.AreEqual(VideoAt, uint.Parse(times[0].Groups[1].Value), "video: " + rtpInfo);
            Assert.AreEqual(AudioAt, uint.Parse(times[1].Groups[1].Value), "audio: " + rtpInfo);
        }
    }
}
