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
                "AACTrack", "AV1Track", "H264Track", "H265Track", "H266Track",
                "MJpegTrack", "OpusTrack", "PCMATrack", "PCMUTrack", "ProxyTrack",
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
        }

        [TestMethod]
        public void TracksDefaultToTheUnencryptedProfile()
        {
            Assert.AreEqual(RtpProfiles.AVP, new H264Track().RtpProfile);
            Assert.AreEqual(RtpProfiles.SAVP, new H264Track { RtpProfile = RtpProfiles.SAVP }.RtpProfile);
        }

        [TestMethod]
        public void FeedingSamplesWithoutASinkIsRejected()
        {
            var track = new H264Track(new byte[] { 0x67 }, new byte[] { 0x68 });

            Assert.ThrowsExactly<InvalidOperationException>(
                () => track.FeedInRawSamples(0, new System.Collections.Generic.List<ReadOnlyMemory<byte>>()));
        }
    }
}
