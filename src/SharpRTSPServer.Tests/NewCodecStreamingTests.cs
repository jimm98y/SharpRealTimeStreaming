using System;
using System.Collections.Generic;
using System.Linq;
using SharpRTSPServer;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// The new tracks carried through a real server, rather than packetised on their own.
    /// </summary>
    /// <remarks>
    /// A packetiser can be right about the bytes and still not reach a client: the SDP has to
    /// describe the track in a way SETUP can find, the control URL has to match, and the media has
    /// to survive the send path. That is what these check.
    /// </remarks>
    [TestClass]
    public sealed class NewCodecStreamingTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        /// <summary>
        /// Plays a stream and hands back the SDP and the first RTP packet that arrived on a track.
        /// </summary>
        private static (string Sdp, byte[] FirstPacket) Play(
            RTSPServer server, int port, int trackId, Action feed)
        {
            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);

            var describe = client.Send("DESCRIBE", baseUri, "Accept: application/sdp");
            Assert.AreEqual(200, describe.StatusCode);

            var setup = client.Send("SETUP", $"{baseUri}/trackID={trackId}",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            Assert.AreEqual(200, setup.StatusCode, "the SDP should describe a track SETUP can find");

            Assert.AreEqual(200, client.Send("PLAY", baseUri, "Session: " + setup.Session).StatusCode);

            byte[] first = null;

            for (int i = 0; i < 12 && first == null; i++)
            {
                feed();

                var frame = client.ReadInterleaved();

                if (frame.Channel == 0)
                {
                    first = frame.Payload;
                }
            }

            Assert.IsNotNull(first, "no media arrived");

            return (describe.Body, first);
        }

        [TestMethod]
        public void G726IsDescribedAndCarried()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");

            var audio = new G726Track(G726BitRate.Rate24);
            server.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), audio));
            server.StartListen();

            byte[] adpcm = { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };

            var played = Play(server, port, trackId: 1,
                feed: () => audio.FeedInRawSamples(160, new List<ReadOnlyMemory<byte>>
                {
                    new ReadOnlyMemory<byte>(adpcm),
                }));

            Assert.Contains("G726-24/8000", played.Sdp, played.Sdp);

            CollectionAssert.AreEqual(adpcm, played.FirstPacket.Skip(12).ToArray(),
                "the ADPCM should reach the client exactly as it was fed in");
        }

        [TestMethod]
        public void AmrIsDescribedAndCarried()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");

            var audio = new AMRTrack();
            server.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), audio));
            server.StartListen();

            // frame type 7, which carries 31 bytes of speech
            byte[] storage = new byte[] { 7 << 3 }.Concat(Enumerable.Repeat((byte)0x5A, 31)).ToArray();

            var played = Play(server, port, trackId: 1,
                feed: () => audio.FeedInRawSamples(160, new List<ReadOnlyMemory<byte>>
                {
                    new ReadOnlyMemory<byte>(storage),
                }));

            Assert.Contains("octet-align=1", played.Sdp, played.Sdp);

            var payload = played.FirstPacket.Skip(12).ToArray();

            Assert.AreEqual(0xF0, payload[0], "the mode request byte should lead the payload");
            Assert.AreEqual(7, (payload[1] >> 3) & 0x0F, "and the table of contents name the frame type");

            CollectionAssert.AreEqual(Enumerable.Repeat((byte)0x5A, 31).ToArray(), payload.Skip(2).ToArray(),
                "the speech should arrive unchanged");
        }

        [TestMethod]
        public void Mp4vIsDescribedAndCarried()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");

            byte[] config = { 0x00, 0x00, 0x01, 0xB0, 0xF3, 0x00, 0x00, 0x01, 0xB5 };
            var video = new MP4VTrack(config);

            server.AddStreamSource(new RTSPStreamSource("stream1", video, null));
            server.StartListen();

            byte[] frame = Enumerable.Range(0, 400).Select(i => (byte)i).ToArray();

            var played = Play(server, port, trackId: 0,
                feed: () => video.FeedInRawSamples(3000, new List<ReadOnlyMemory<byte>>
                {
                    new ReadOnlyMemory<byte>(frame),
                }));

            // the headers a receiver joining later needs in order to decode anything at all
            Assert.Contains("config=000001B0F3000001B5", played.Sdp, played.Sdp);
            Assert.Contains("MP4V-ES/90000", played.Sdp, played.Sdp);

            CollectionAssert.AreEqual(frame, played.FirstPacket.Skip(12).ToArray(),
                "a frame within the MTU should arrive in one piece and unchanged");
        }

        [TestMethod]
        public void AProxyTrackSaysWhyItCannotDescribeItself()
        {
            var proxy = new ProxyTrack(TrackType.Video);

            // It forwards RTP that something else described, so there is nothing here to build an
            // SDP from - and saying so beats a NotImplementedException, which reads like an oversight.
            var complaint = Assert.ThrowsExactly<InvalidOperationException>(
                () => proxy.BuildSDP(new System.Text.StringBuilder()));

            Assert.Contains("OverrideSDP", complaint.Message,
                "the message should say where the description is meant to come from");
        }
    }
}
