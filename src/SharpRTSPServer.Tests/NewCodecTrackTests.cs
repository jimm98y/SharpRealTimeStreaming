// SharpRTSPServer
// Copyright (C) 2026 Lukas Volf
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// The codecs a client here could already receive but the server could not send, and one neither
    /// end had.
    /// </summary>
    /// <remarks>
    /// Each of these checks what actually goes on the wire, because a packetiser that runs without
    /// throwing and produces something a decoder cannot read is the failure worth catching.
    /// </remarks>
    [TestClass]
    public sealed class NewCodecTrackTests
    {
        private static List<ReadOnlyMemory<byte>> One(byte[] sample) =>
            new List<ReadOnlyMemory<byte>> { new ReadOnlyMemory<byte>(sample) };

        private static void Release(List<IMemoryOwner<byte>> owners)
        {
        }

        // ------------------------------------------------------------------ G.726

        [TestMethod]
        public void G726NamesItsRateInTheSdpBecauseThePayloadDoesNotSayIt()
        {
            // The payload is a run of samples with nothing marking where one ends, so how many bits
            // each takes has to come from the description or a receiver cannot read any of it.
            foreach (var rate in new[] { G726BitRate.Rate16, G726BitRate.Rate24, G726BitRate.Rate32, G726BitRate.Rate40 })
            {
                var track = new G726Track(rate);
                string sdp = track.BuildSDP(new StringBuilder()).ToString();

                Assert.Contains($"G726-{(int)rate}/8000", sdp, "the rate belongs in the codec name: " + sdp);
                Assert.Contains("m=audio", sdp);
            }
        }

        [TestMethod]
        public void G726SendsTheAdpcmWithNothingAddedToIt()
        {
            var track = new G726Track(G726BitRate.Rate32) { PayloadType = 97 };

            byte[] adpcm = { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 };
            var packets = RtpPackets.Take();
            track.CreateRtpPackets(One(adpcm), 12345, packets);

            try
            {
                Assert.HasCount(1, packets);

                var packet = packets[0].Span;
                Assert.AreEqual(12 + adpcm.Length, packet.Length, "G.726 has no payload header of its own");
                Assert.AreEqual(97, packet[1] & 0x7F, "the payload type should be the one configured");

                CollectionAssert.AreEqual(adpcm, packet.Slice(12).ToArray(),
                    "the payload should be the ADPCM exactly as it was given");
            }
            finally
            {
                packets.Release();
            }
        }

        [TestMethod]
        public void G726RefusesARateItDoesNotHave()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new G726Track((G726BitRate)28));
        }

        // ------------------------------------------------------------------ AMR

        [TestMethod]
        public void AmrSaysItIsOctetAlignedBecauseAReceiverCannotTell()
        {
            var track = new AMRTrack();
            string sdp = track.BuildSDP(new StringBuilder()).ToString();

            // Without this a receiver reads the payload bit packed and makes nothing of it.
            Assert.Contains("octet-align=1", sdp, sdp);
            Assert.Contains("AMR/8000", sdp, sdp);
        }

        [TestMethod]
        public void AmrWidebandIsADifferentCodecAtADifferentRate()
        {
            string sdp = new AMRTrack(wideband: true).BuildSDP(new StringBuilder()).ToString();

            Assert.Contains("AMR-WB/16000", sdp, sdp);
        }

        [TestMethod]
        public void AmrDescribesEachFrameBeforeSendingIt()
        {
            var track = new AMRTrack { PayloadType = 98 };

            // Frame type 7 narrowband, which is 31 bytes of speech after its header byte.
            byte header = 7 << 3;
            byte[] speech = Enumerable.Range(0, 31).Select(i => (byte)i).ToArray();
            byte[] storage = new byte[] { header }.Concat(speech).ToArray();

            var packets = RtpPackets.Take();
            track.CreateRtpPackets(One(storage), 900, packets);

            try
            {
                Assert.HasCount(1, packets);

                var payload = packets[0].Span.Slice(12);

                Assert.AreEqual(0xF0, payload[0], "the first byte asks the other end for no particular rate");

                // one frame: not followed by another, type 7, marked as having arrived intact
                Assert.AreEqual(0x00, payload[1] & 0x80, "the last frame says nothing follows it");
                Assert.AreEqual(7, (payload[1] >> 3) & 0x0F, "the frame type belongs in the table of contents");
                Assert.AreEqual(0x04, payload[1] & 0x04, "and the frame is a good one");

                CollectionAssert.AreEqual(speech, payload.Slice(2, speech.Length).ToArray(),
                    "the speech itself should follow the table of contents unchanged");

                Assert.AreEqual(12 + 1 + 1 + speech.Length, packets[0].Length,
                    "a packet is the header, the mode request, one entry per frame and the frames");
            }
            finally
            {
                packets.Release();
            }
        }

        [TestMethod]
        public void AmrPutsSeveralFramesInOnePacketAndSaysSo()
        {
            var track = new AMRTrack();

            byte header = 7 << 3;
            byte[] speech = new byte[31];
            byte[] two = new byte[] { header }.Concat(speech).Concat(new byte[] { header }).Concat(speech).ToArray();

            var packets = RtpPackets.Take();
            track.CreateRtpPackets(One(two), 900, packets);

            try
            {
                var payload = packets[0].Span.Slice(12);

                // A receiver has to know how many frames there are before it can find where the first
                // one starts, which is why they are all described first.
                Assert.AreEqual(0x80, payload[1] & 0x80, "the first of two should say another follows");
                Assert.AreEqual(0x00, payload[2] & 0x80, "and the second that none does");

                Assert.AreEqual(12 + 1 + 2 + (2 * 31), packets[0].Length);
            }
            finally
            {
                packets.Release();
            }
        }

        [TestMethod]
        public void AmrWillNotSendAFrameTypeThatDoesNotExist()
        {
            var track = new AMRTrack();

            // type 12 is one of the gaps in the narrowband table
            byte[] nonsense = { 12 << 3, 0x00, 0x00 };

            Assert.ThrowsExactly<ArgumentException>(() => track.CreateRtpPackets(One(nonsense), 0, RtpPackets.Take()));
        }

        [TestMethod]
        public void AmrWillNotSendAFrameThatRunsPastTheSample()
        {
            var track = new AMRTrack();

            // says it holds 31 bytes of speech and holds two
            byte[] truncated = { 7 << 3, 0x01, 0x02 };

            Assert.ThrowsExactly<ArgumentException>(() => track.CreateRtpPackets(One(truncated), 0, RtpPackets.Take()));
        }

        // ------------------------------------------------------------------ MPEG-4 Part 2

        [TestMethod]
        public void Mp4vCarriesTheHeadersAReceiverNeedsToStartDecoding()
        {
            byte[] config = { 0x00, 0x00, 0x01, 0xB0, 0x01, 0x00, 0x00, 0x01, 0xB5 };
            var track = new MP4VTrack(config) { PayloadType = 96, ProfileLevelId = 1 };

            string sdp = track.BuildSDP(new StringBuilder()).ToString();

            Assert.Contains("MP4V-ES/90000", sdp, sdp);
            Assert.Contains("profile-level-id=1", sdp, sdp);

            // A receiver joining later never sees the start of the stream, so the headers that open
            // it have to be in the description.
            Assert.Contains("config=000001B0010000 01B5".Replace(" ", ""), sdp, sdp);
        }

        [TestMethod]
        public void Mp4vWillNotDescribeItselfWithoutThoseHeaders()
        {
            var track = new MP4VTrack();

            Assert.IsFalse(track.IsReady, "a track with nothing to decode by is not ready");
            Assert.ThrowsExactly<InvalidOperationException>(() => track.BuildSDP(new StringBuilder()));
        }

        [TestMethod]
        public void Mp4vMarksOnlyTheLastPacketOfAFrame()
        {
            var track = new MP4VTrack(new byte[] { 0x00, 0x00, 0x01, 0xB0 }) { PacketMTU = 200 };

            byte[] frame = new byte[700];
            var packets = RtpPackets.Take();
            track.CreateRtpPackets(One(frame), 3000, packets);

            try
            {
                Assert.IsGreaterThan(1, packets.Count, "a frame larger than the MTU should be cut up");

                for (int i = 0; i < packets.Count - 1; i++)
                {
                    Assert.AreEqual(0, packets[i].Span[1] & 0x80, $"packet {i} is not the end of the frame");
                }

                Assert.AreEqual(0x80, packets[packets.Count - 1].Span[1] & 0x80,
                    "the last packet is what tells a receiver the frame has ended");

                // and all of it arrives, in order
                var sent = packets.SelectMany(p => p.Span.Slice(12).ToArray()).ToArray();
                Assert.HasCount(frame.Length, sent, "every byte of the frame should be sent once");
            }
            finally
            {
                packets.Release();
            }
        }

        [TestMethod]
        public void Mp4vPrefersToCutWhereAHeaderStarts()
        {
            var track = new MP4VTrack(new byte[] { 0x00, 0x00, 0x01, 0xB0 }) { PacketMTU = 200 };

            // A start code placed late in what the first packet could hold. Cutting there costs a
            // few bytes and means the second packet opens at a header rather than inside one, so a
            // lost first packet does not take the sense of the second with it.
            byte[] frame = new byte[600];
            int startCode = 150;
            frame[startCode] = 0x00;
            frame[startCode + 1] = 0x00;
            frame[startCode + 2] = 0x01;
            frame[startCode + 3] = 0xB6;

            var packets = RtpPackets.Take();
            track.CreateRtpPackets(One(frame), 3000, packets);

            try
            {
                Assert.AreEqual(startCode, packets[0].Length - 12,
                    "the first packet should stop just before the start code");

                var second = packets[1].Span;
                Assert.AreEqual(0x00, second[12]);
                Assert.AreEqual(0x00, second[13]);
                Assert.AreEqual(0x01, second[14]);
            }
            finally
            {
                packets.Release();
            }
        }
    }
}
