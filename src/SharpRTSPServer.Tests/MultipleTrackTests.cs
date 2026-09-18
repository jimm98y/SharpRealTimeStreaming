using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SharpRTSPServer;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// Streams that carry more than one video track and one audio track.
    /// </summary>
    /// <remarks>
    /// A stream is however many tracks it has - two languages, two qualities, a picture and the data
    /// describing what is in it. The server used to be built around exactly one of each: the track a
    /// request was about was worked out from its kind, so a second audio track had nowhere to be.
    /// What identifies a track now is its ID, and its kind only says what sort of thing is in it.
    /// </remarks>
    [TestClass]
    public sealed class MultipleTrackTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        private static List<ReadOnlyMemory<byte>> One(byte[] sample) =>
            new List<ReadOnlyMemory<byte>> { new ReadOnlyMemory<byte>(sample) };

        [TestMethod]
        public void AStreamCanCarryTwoAudioTracks()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");

            var english = new PCMUTrack { ID = 1 };
            var french = new PCMATrack { ID = 2 };

            server.AddStreamSource(new RTSPStreamSource("stream1",
                new H264Track(Sps, Pps), english, french));
            server.StartListen();

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);

            var describe = client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            // both of them described, each with a control URL of its own
            Assert.Contains("PCMU", describe.Body, describe.Body);
            Assert.Contains("PCMA", describe.Body, describe.Body);
            Assert.Contains("a=control:trackID=1", describe.Body, describe.Body);
            Assert.Contains("a=control:trackID=2", describe.Body, describe.Body);

            // and both can be set up, which is what a stream with one audio track could not offer
            var first = client.Send("SETUP", baseUri + "/trackID=1",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            Assert.AreEqual(200, first.StatusCode, "the first audio track should be set up");

            var second = client.Send("SETUP", baseUri + "/trackID=2",
                $"Transport: RTP/AVP/TCP;unicast;interleaved=2-3\r\nSession: {first.Session}");
            Assert.AreEqual(200, second.StatusCode, "and so should the second");
        }

        [TestMethod]
        public void EachOfTwoAudioTracksCarriesItsOwnMedia()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");

            var english = new PCMUTrack { ID = 1 };
            var french = new PCMATrack { ID = 2 };

            server.AddStreamSource(new RTSPStreamSource("stream1",
                new H264Track(Sps, Pps), english, french));
            server.StartListen();

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            var first = client.Send("SETUP", baseUri + "/trackID=1",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            client.Send("SETUP", baseUri + "/trackID=2",
                $"Transport: RTP/AVP/TCP;unicast;interleaved=2-3\r\nSession: {first.Session}");

            Assert.AreEqual(200, client.Send("PLAY", baseUri, "Session: " + first.Session).StatusCode);

            byte[] toEnglish = { 0x11, 0x11, 0x11, 0x11 };
            byte[] toFrench = { 0x22, 0x22, 0x22, 0x22 };

            var onChannel = new Dictionary<int, byte[]>();

            for (int i = 0; i < 20 && onChannel.Count < 2; i++)
            {
                english.FeedInRawSamples((uint)(i * 160), One(toEnglish));
                french.FeedInRawSamples((uint)(i * 160), One(toFrench));

                var frame = client.ReadInterleaved();

                if ((frame.Channel == 0 || frame.Channel == 2) && !onChannel.ContainsKey(frame.Channel))
                {
                    onChannel[frame.Channel] = frame.Payload.Skip(12).ToArray();
                }
            }

            Assert.HasCount(2, onChannel, "both tracks should carry media");

            // what was fed to one does not come out of the other
            CollectionAssert.AreEqual(toEnglish, onChannel[0], "the first track should carry its own audio");
            CollectionAssert.AreEqual(toFrench, onChannel[2], "and the second its own");
        }

        [TestMethod]
        public void AStreamRefusesTwoTracksWithTheSameId()
        {
            // The ID is what a control URL names, so two tracks sharing one would give a client no
            // way of saying which it wanted.
            var source = new RTSPStreamSource("stream1", new H264Track(Sps, Pps));

            var clash = Assert.ThrowsExactly<ArgumentException>(
                () => source.AddTrack(new MP4VTrack(new byte[] { 0x00, 0x00, 0x01, 0xB0 })));

            Assert.Contains("ID 0", clash.Message, clash.Message);
        }

        // ------------------------------------------------------------------ metadata

        [TestMethod]
        public void MetadataIsDescribedAsAnApplicationTrack()
        {
            var track = new MetadataTrack();
            string sdp = track.BuildSDP(new StringBuilder()).ToString();

            // application, which is what SDP has for a stream that is neither sound nor pictures
            Assert.Contains("m=application", sdp, sdp);
            Assert.Contains("vnd.onvif.metadata/90000", sdp, sdp);
            Assert.AreEqual(TrackType.Metadata, track.Kind);
        }

        [TestMethod]
        public void MetadataIsCarriedAlongsideThePicture()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");

            var metadata = new MetadataTrack();
            server.AddStreamSource(new RTSPStreamSource("stream1",
                new H264Track(Sps, Pps), null, metadata));
            server.StartListen();

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);

            var describe = client.Send("DESCRIBE", baseUri, "Accept: application/sdp");
            Assert.Contains("m=application", describe.Body, describe.Body);

            var setup = client.Send("SETUP", baseUri + "/trackID=2",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            Assert.AreEqual(200, setup.StatusCode, "a metadata track should be set up like any other");

            Assert.AreEqual(200, client.Send("PLAY", baseUri, "Session: " + setup.Session).StatusCode);

            const string document = "<tt:MetadataStream><tt:Event>something happened</tt:Event></tt:MetadataStream>";

            byte[] arrived = null;

            for (int i = 0; i < 12 && arrived == null; i++)
            {
                metadata.FeedInRawSamples((uint)((i + 1) * 3000), document);

                var frame = client.ReadInterleaved();

                if (frame.Channel == 0)
                {
                    arrived = frame.Payload;
                }
            }

            Assert.IsNotNull(arrived, "no metadata arrived");

            Assert.AreEqual(document, Encoding.UTF8.GetString(arrived, 12, arrived.Length - 12),
                "the document should arrive as it was written");

            Assert.AreEqual(0x80, arrived[1] & 0x80, "a document that fits in one packet ends in it");
        }

        [TestMethod]
        public void ALongDocumentIsCutUpAndOnlyTheLastPacketIsMarked()
        {
            var track = new MetadataTrack { PacketMTU = 200 };

            string document = new string('x', 2000);
            var (packets, owners) = track.CreateRtpPackets(
                One(Encoding.UTF8.GetBytes(document)), 3000);

            try
            {
                Assert.IsGreaterThan(1, packets.Count, "a document larger than the MTU should be cut up");

                for (int i = 0; i < packets.Count - 1; i++)
                {
                    Assert.AreEqual(0, packets[i].Span[1] & 0x80, $"packet {i} is not the end of the document");
                }

                Assert.AreEqual(0x80, packets[packets.Count - 1].Span[1] & 0x80,
                    "the last packet is what says the document has ended");

                var rebuilt = string.Concat(packets.Select(p => Encoding.UTF8.GetString(p.Span.Slice(12).ToArray())));
                Assert.AreEqual(document, rebuilt, "put back together it should be what went in");
            }
            finally
            {
                foreach (var owner in owners)
                {
                    owner.Dispose();
                }
            }
        }

        [TestMethod]
        public void TracksKnowTheirKindRatherThanInferringItFromTheirId()
        {
            // The ID used to be the kind, so a track with ID 2 was neither video nor audio and a
            // second audio track was impossible to describe.
            Assert.AreEqual(TrackType.Audio, new PCMATrack { ID = 7 }.Kind);
            Assert.AreEqual(TrackType.Video, new H264Track(Sps, Pps) { ID = 4 }.Kind);
            Assert.AreEqual(TrackType.Metadata, new MetadataTrack { ID = 9 }.Kind);
        }
    }
}
