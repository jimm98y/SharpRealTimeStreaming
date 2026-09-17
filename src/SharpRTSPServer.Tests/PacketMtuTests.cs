using System;
using System.Collections.Generic;
using System.Linq;
using SharpRTSPServer;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// Every track fragments against the MTU it was given.
    /// </summary>
    /// <remarks>
    /// Four of them declared PacketMTU identically and a fifth did not have it at all, fragmenting
    /// at a fixed size - so a deployment that lowered the MTU for a tunnel had it honoured on some
    /// codecs and quietly ignored on the rest.
    /// </remarks>
    [TestClass]
    public sealed class PacketMtuTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        private static List<ReadOnlyMemory<byte>> Nal(int size)
        {
            var nal = new byte[size];
            nal[0] = 0x65;
            return new List<ReadOnlyMemory<byte>> { new ReadOnlyMemory<byte>(nal) };
        }

        /// <summary>The smallest JPEG the packetizer accepts, padded out so that it has to fragment.</summary>
        private static List<ReadOnlyMemory<byte>> Jpeg(int payloadPadding)
        {
            var bytes = new List<byte> { 0xFF, 0xD8 };                     // SOI

            bytes.AddRange(new byte[] { 0xFF, 0xDB });                     // DQT
            byte[] table = Enumerable.Range(0, 64).Select(v => (byte)v).ToArray();
            bytes.Add(0x00);
            bytes.Add((byte)(table.Length + 3));
            bytes.Add(0x00);
            bytes.AddRange(table);

            bytes.AddRange(new byte[] { 0xFF, 0xC0, 0x00, 0x11, 0x08 });   // SOF0
            bytes.AddRange(new byte[] { 0x01, 0xE0, 0x02, 0x80 });         // 480 x 640
            bytes.Add(0x03);
            bytes.AddRange(new byte[] { 0x01, 0x21, 0x00 });
            bytes.AddRange(new byte[] { 0x02, 0x11, 0x01 });
            bytes.AddRange(new byte[] { 0x03, 0x11, 0x01 });

            bytes.AddRange(new byte[] { 0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00 }); // SOS
            bytes.AddRange(Enumerable.Repeat((byte)0x5A, payloadPadding)); // entropy coded data
            bytes.AddRange(new byte[] { 0xFF, 0xD9 });                     // EOI

            return new List<ReadOnlyMemory<byte>> { new ReadOnlyMemory<byte>(bytes.ToArray()) };
        }

        private static int LargestPacket(IEnumerable<Memory<byte>> packets) => packets.Max(p => p.Length);

        [TestMethod]
        public void AVideoTrackFragmentsAgainstTheMtuItWasGiven()
        {
            var big = new H264Track(Sps, Pps) { PacketMTU = 1400 };
            var small = new H264Track(Sps, Pps) { PacketMTU = 400 };

            var (bigPackets, bigOwners) = big.CreateRtpPackets(Nal(8000), 0);
            var (smallPackets, smallOwners) = small.CreateRtpPackets(Nal(8000), 0);

            try
            {
                Assert.IsLessThan(1400, LargestPacket(bigPackets));
                Assert.IsLessThan(400, LargestPacket(smallPackets));
                Assert.IsGreaterThan(bigPackets.Count, smallPackets.Count,
                    "a smaller MTU has to mean more packets");
            }
            finally
            {
                foreach (var owner in bigOwners.Concat(smallOwners)) owner.Dispose();
            }
        }

        [TestMethod]
        public void MJpegFragmentsAgainstTheMtuItWasGiven()
        {
            var big = new MJpegTrack { PacketMTU = 1400 };
            var small = new MJpegTrack { PacketMTU = 400 };

            var (bigPackets, bigOwners) = big.CreateRtpPackets(Jpeg(6000), 0);
            var (smallPackets, smallOwners) = small.CreateRtpPackets(Jpeg(6000), 0);

            try
            {
                // it used to fragment at a fixed size whatever this was set to
                Assert.IsLessThan(1400, LargestPacket(bigPackets));
                Assert.IsLessThan(400, LargestPacket(smallPackets));
                Assert.IsGreaterThan(bigPackets.Count, smallPackets.Count,
                    "a smaller MTU has to mean more packets");
            }
            finally
            {
                foreach (var owner in bigOwners.Concat(smallOwners)) owner.Dispose();
            }
        }

        [TestMethod]
        public void EveryTrackDefaultsToTheSameMtu()
        {
            Assert.AreEqual(TrackBase.DEFAULT_PACKET_MTU, new H264Track(Sps, Pps).PacketMTU);
            Assert.AreEqual(TrackBase.DEFAULT_PACKET_MTU, new H265Track().PacketMTU);
            Assert.AreEqual(TrackBase.DEFAULT_PACKET_MTU, new H266Track().PacketMTU);
            Assert.AreEqual(TrackBase.DEFAULT_PACKET_MTU, new AV1Track().PacketMTU);
            Assert.AreEqual(TrackBase.DEFAULT_PACKET_MTU, new MJpegTrack().PacketMTU);
        }

        [TestMethod]
        public void AnMtuWithNoRoomForPayloadIsRejected()
        {
            // rather than looping for ever on a fragment size of zero
            var track = new H264Track(Sps, Pps) { PacketMTU = 40 };

            Assert.ThrowsExactly<InvalidOperationException>(() => track.CreateRtpPackets(Nal(8000), 0));
        }

        [TestMethod]
        public void MJpegRejectsAnMtuWithNoRoomForPayloadToo()
        {
            var track = new MJpegTrack { PacketMTU = 40 };

            Assert.ThrowsExactly<InvalidOperationException>(() => track.CreateRtpPackets(Jpeg(6000), 0));
        }
    }
}
