using System;
using System.Collections.Generic;
using System.Linq;
using SharpRTSPServer;

namespace SharpRTSPServer.Tests
{
    [TestClass]
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

        [TestMethod]
        public void EverySampleIsForwarded()
        {
            var track = new ProxyTrack(TrackType.Video);
            byte[] first = { 1, 2, 3 };
            byte[] second = { 4, 5, 6, 7 };
            byte[] third = { 8 };

            var packets = Packetize(track, first, second, third);

            // all three used to be dropped except the first, silently losing media
            Assert.HasCount(3, packets);
            CollectionAssert.AreEqual(first, packets[0]);
            CollectionAssert.AreEqual(second, packets[1]);
            CollectionAssert.AreEqual(third, packets[2]);
        }

        [TestMethod]
        public void PacketsArePassedThroughByteForByte()
        {
            var track = new ProxyTrack(TrackType.Video);
            byte[] rtp = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();

            CollectionAssert.AreEqual(rtp, Assert.ContainsSingle(Packetize(track, rtp)));
        }

        [TestMethod]
        public void NoSamplesProducesNoPackets()
        {
            var track = new ProxyTrack(TrackType.Video);

            // used to throw IndexOutOfRangeException reaching for samples[0]
            Assert.IsEmpty(Packetize(track));
        }

        [TestMethod]
        public void EmptySamplesAreSkipped()
        {
            var track = new ProxyTrack(TrackType.Video);

            var packets = Packetize(track, new byte[0], new byte[] { 9, 9 }, new byte[0]);

            CollectionAssert.AreEqual(new byte[] { 9, 9 }, Assert.ContainsSingle(packets));
        }

        [TestMethod]
        public void NullSamplesAreRejected()
        {
            var track = new ProxyTrack(TrackType.Video);

            Assert.ThrowsExactly<ArgumentNullException>(() => track.CreateRtpPackets(null, 0));
        }

        [TestMethod]
        public void TrackIsNotReadyUntilItIsStarted()
        {
            var track = new ProxyTrack(TrackType.Audio);
            Assert.IsFalse(track.IsReady);

            track.Start();
            Assert.IsTrue(track.IsReady);

            track.Dispose();
            Assert.IsFalse(track.IsReady);
        }

        [TestMethod]
        public void TrackTypeDeterminesTheId()
        {
            Assert.AreEqual((int)TrackType.Video, new ProxyTrack(TrackType.Video).ID);
            Assert.AreEqual((int)TrackType.Audio, new ProxyTrack(TrackType.Audio).ID);
        }
    }
}
