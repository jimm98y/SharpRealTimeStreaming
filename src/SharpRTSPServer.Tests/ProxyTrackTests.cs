using System;
using System.Collections.Generic;
using System.Linq;
using SharpRTSPServer;

namespace SharpRTSPServer.Tests
{
    public class ProxyTrackTests
    {
        private static List<byte[]> Packetize(ProxyTrack track, params byte[][] samples)
        {
            var input = samples.Select(s => new ReadOnlyMemory<byte>(s)).ToList();
            var (packets, owners) = track.CreateRtpPackets(input, 0);

            var copies = packets.Select(p => p.ToArray()).ToList();
            foreach (var owner in owners)
            {
                owner.Dispose();
            }
            return copies;
        }

        [Fact]
        public void EverySampleIsForwarded()
        {
            var track = new ProxyTrack(TrackType.Video);
            byte[] first = { 1, 2, 3 };
            byte[] second = { 4, 5, 6, 7 };
            byte[] third = { 8 };

            var packets = Packetize(track, first, second, third);

            // all three used to be dropped except the first, silently losing media
            Assert.Equal(3, packets.Count);
            Assert.Equal(first, packets[0]);
            Assert.Equal(second, packets[1]);
            Assert.Equal(third, packets[2]);
        }

        [Fact]
        public void PacketsArePassedThroughByteForByte()
        {
            var track = new ProxyTrack(TrackType.Video);
            byte[] rtp = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();

            Assert.Equal(rtp, Assert.Single(Packetize(track, rtp)));
        }

        [Fact]
        public void NoSamplesProducesNoPackets()
        {
            var track = new ProxyTrack(TrackType.Video);

            // used to throw IndexOutOfRangeException reaching for samples[0]
            Assert.Empty(Packetize(track));
        }

        [Fact]
        public void EmptySamplesAreSkipped()
        {
            var track = new ProxyTrack(TrackType.Video);

            var packets = Packetize(track, new byte[0], new byte[] { 9, 9 }, new byte[0]);

            Assert.Equal(new byte[] { 9, 9 }, Assert.Single(packets));
        }

        [Fact]
        public void NullSamplesAreRejected()
        {
            var track = new ProxyTrack(TrackType.Video);

            Assert.Throws<ArgumentNullException>(() => track.CreateRtpPackets(null, 0));
        }

        [Fact]
        public void TrackIsNotReadyUntilItIsStarted()
        {
            var track = new ProxyTrack(TrackType.Audio);
            Assert.False(track.IsReady);

            track.Start();
            Assert.True(track.IsReady);

            track.Dispose();
            Assert.False(track.IsReady);
        }

        [Fact]
        public void TrackTypeDeterminesTheId()
        {
            Assert.Equal((int)TrackType.Video, new ProxyTrack(TrackType.Video).ID);
            Assert.Equal((int)TrackType.Audio, new ProxyTrack(TrackType.Audio).ID);
        }
    }
}
