using System;
using System.Linq;
using System.Reflection;
using SharpRTSPServer;

namespace SharpRTSPServer.Tests
{
    [TestClass]
    public class TrackApiTests
    {
        private static Type[] PublicTracks() =>
            typeof(TrackBase).Assembly
                .GetExportedTypes()
                .Where(t => !t.IsAbstract && typeof(ITrack).IsAssignableFrom(t))
                .OrderBy(t => t.Name)
                .ToArray();

        [TestMethod]
        public void EveryTrackLivesInTheSameNamespace()
        {
            // PCMATrack was the odd one out in SharpRTSPServer.Tracks, so it needed a different
            // using than every other track, PCMUTrack included
            var strays = PublicTracks()
                .Where(t => t.Namespace != "SharpRTSPServer")
                .Select(t => $"{t.Namespace}.{t.Name}")
                .ToArray();

            Assert.IsEmpty(strays);
        }

        [TestMethod]
        public void TheDocumentedTracksAreAllPresent()
        {
            var expected = new[]
            {
                "AACTrack", "AMRTrack", "AV1Track", "G726Track", "H264Track", "H265Track",
                "H266Track", "MetadataTrack", "MJpegTrack", "MP4VTrack", "OpusTrack", "PCMATrack",
                "PCMUTrack", "ProxyTrack",
            };

            var actual = PublicTracks().Select(t => t.Name).ToArray();

            CollectionAssert.AreEqual(expected, actual);
        }

        public static IEnumerable<object[]> DefaultTracks() => new[]
        {
            new object[] { new H264Track(), "H264" },
            new object[] { new H265Track(), "H265" },
            new object[] { new H266Track(), "H266" },
            new object[] { new AV1Track(), "AV1" },
            new object[] { new MJpegTrack(), "JPEG" },
            new object[] { new OpusTrack(), "opus" },
            new object[] { new PCMATrack(), "PCMA" },
            new object[] { new PCMUTrack(), "PCMU" },
            new object[] { new AACTrack(44100, 2), "mpeg4-generic" },
            new object[] { new AMRTrack(), "AMR" },
            new object[] { new AMRTrack(wideband: true), "AMR-WB" },
            new object[] { new G726Track(), "G726-32" },
            new object[] { new G726Track(G726BitRate.Rate16), "G726-16" },
            new object[] { new MP4VTrack(), "MP4V-ES" },
            new object[] { new MetadataTrack(), "vnd.onvif.metadata" },
        };

        [TestMethod]
        [DynamicData(nameof(DefaultTracks))]
        public void TrackReportsItsCodecName(ITrack track, string expectedCodec)
        {
            Assert.AreEqual(expectedCodec, track.Codec);
        }

        [TestMethod]
        public void AudioAndVideoTracksDefaultToTheirConventionalIds()
        {
            Assert.AreEqual((int)TrackType.Video, new H264Track().ID);
            Assert.AreEqual((int)TrackType.Audio, new AACTrack(44100, 2).ID);
            Assert.AreEqual((int)TrackType.Audio, new PCMATrack().ID);
            Assert.AreEqual((int)TrackType.Audio, new PCMUTrack().ID);
            Assert.AreEqual((int)TrackType.Audio, new AMRTrack().ID);
            Assert.AreEqual((int)TrackType.Audio, new G726Track().ID);
            Assert.AreEqual((int)TrackType.Video, new MP4VTrack().ID);
        }

        [TestMethod]
        public void TracksDefaultToTheUnencryptedProfile()
        {
            Assert.AreEqual(RtpProfiles.AVP, new H264Track().RtpProfile);
            Assert.AreEqual(RtpProfiles.SAVP, new H264Track { RtpProfile = RtpProfiles.SAVP }.RtpProfile);
        }

        [TestMethod]
        public void FeedingSamplesWithoutASinkIsDropped()
        {
            // A track with no sink is one that has not been added to a server yet, or whose stream
            // source has just been removed. A producer thread feeding across either of those points
            // should not have an exception thrown back at it.
            var track = new H264Track(new byte[] { 0x67 }, new byte[] { 0x68 });

            track.FeedInRawSamples(0, new System.Collections.Generic.List<ReadOnlyMemory<byte>>());
        }

        [TestMethod]
        public void FeedingSamplesAfterTheStreamSourceIsRemovedIsDropped()
        {
            using var server = new RTSPServer(TestPorts.FindFree(), "admin", "password");
            var track = new H264Track(new byte[] { 0x67, 0x42, 0x00, 0x1E }, new byte[] { 0x68, 0xCE, 0x3C, 0x80 });
            var streamSource = new RTSPStreamSource("stream1", track, null);

            server.AddStreamSource(streamSource);
            Assert.IsNotNull(track.Sink);

            server.RemoveStreamSource(streamSource);
            Assert.IsNull(track.Sink);

            // whatever the producer had in flight when the source went away
            track.FeedInRawSamples(0, new System.Collections.Generic.List<ReadOnlyMemory<byte>>
            {
                new ReadOnlyMemory<byte>(new byte[] { 0x65, 0x01, 0x02 })
            });
        }
    }
}
