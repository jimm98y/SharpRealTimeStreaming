using System;
using System.Linq;
using System.Reflection;
using SharpRTSPServer;

namespace SharpRTSPServer.Tests
{
    public class TrackApiTests
    {
        private static Type[] PublicTracks() =>
            typeof(TrackBase).Assembly
                .GetExportedTypes()
                .Where(t => !t.IsAbstract && typeof(ITrack).IsAssignableFrom(t))
                .OrderBy(t => t.Name)
                .ToArray();

        [Fact]
        public void EveryTrackLivesInTheSameNamespace()
        {
            // PCMATrack was the odd one out in SharpRTSPServer.Tracks, so it needed a different
            // using than every other track, PCMUTrack included
            var strays = PublicTracks()
                .Where(t => t.Namespace != "SharpRTSPServer")
                .Select(t => $"{t.Namespace}.{t.Name}")
                .ToArray();

            Assert.Empty(strays);
        }

        [Fact]
        public void TheDocumentedTracksAreAllPresent()
        {
            var expected = new[]
            {
                "AACTrack", "AV1Track", "H264Track", "H265Track", "H266Track",
                "MJpegTrack", "OpusTrack", "PCMATrack", "PCMUTrack", "ProxyTrack",
            };

            var actual = PublicTracks().Select(t => t.Name).ToArray();

            Assert.Equal(expected, actual);
        }

        public static TheoryData<ITrack, string> DefaultTracks() => new TheoryData<ITrack, string>
        {
            { new H264Track(), "H264" },
            { new H265Track(), "H265" },
            { new H266Track(), "H266" },
            { new AV1Track(), "AV1" },
            { new MJpegTrack(), "JPEG" },
            { new OpusTrack(), "opus" },
            { new PCMATrack(), "PCMA" },
            { new PCMUTrack(), "PCMU" },
            { new AACTrack(44100, 2), "mpeg4-generic" },
        };

        [Theory]
        [MemberData(nameof(DefaultTracks))]
        public void TrackReportsItsCodecName(ITrack track, string expectedCodec)
        {
            Assert.Equal(expectedCodec, track.Codec);
        }

        [Fact]
        public void AudioAndVideoTracksDefaultToTheirConventionalIds()
        {
            Assert.Equal((int)TrackType.Video, new H264Track().ID);
            Assert.Equal((int)TrackType.Audio, new AACTrack(44100, 2).ID);
            Assert.Equal((int)TrackType.Audio, new PCMATrack().ID);
            Assert.Equal((int)TrackType.Audio, new PCMUTrack().ID);
        }

        [Fact]
        public void TracksDefaultToTheUnencryptedProfile()
        {
            Assert.Equal(RtpProfiles.AVP, new H264Track().RtpProfile);
            Assert.Equal(RtpProfiles.SAVP, new H264Track { RtpProfile = RtpProfiles.SAVP }.RtpProfile);
        }

        [Fact]
        public void FeedingSamplesWithoutASinkIsRejected()
        {
            var track = new H264Track(new byte[] { 0x67 }, new byte[] { 0x68 });

            Assert.Throws<InvalidOperationException>(
                () => track.FeedInRawSamples(0, new System.Collections.Generic.List<ReadOnlyMemory<byte>>()));
        }
    }
}
