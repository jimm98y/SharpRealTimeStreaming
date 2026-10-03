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
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// What the VP9 track puts on the wire, read back as RFC 9628 describes it.
    /// </summary>
    [TestClass]
    public sealed class VP9TrackTests
    {
        /// <summary>
        /// The start of a VP9 key frame of the given profile and size, followed by filler to the
        /// requested length.
        /// </summary>
        internal static byte[] KeyFrame(int width, int height, int length, int profile = 0)
        {
            var bits = new List<int>();
            void Put(int value, int count)
            {
                for (int i = count - 1; i >= 0; i--) bits.Add((value >> i) & 1);
            }

            Put(2, 2);                  // frame_marker
            Put(profile & 1, 1);        // profile_low_bit
            Put(profile >> 1, 1);       // profile_high_bit
            if (profile == 3) Put(0, 1);
            Put(0, 1);                  // show_existing_frame
            Put(0, 1);                  // frame_type KEY_FRAME
            Put(1, 1);                  // show_frame
            Put(0, 1);                  // error_resilient_mode
            Put(0x498342, 24);          // frame_sync_code
            if (profile >= 2) Put(0, 1);// ten_or_twelve_bit
            Put(1, 3);                  // color_space CS_BT_601
            Put(0, 1);                  // color_range
            if (profile == 1 || profile == 3) Put(0, 3); // subsampling_x, subsampling_y, reserved_zero
            Put(width - 1, 16);
            Put(height - 1, 16);
            Put(0, 1);                  // render_and_frame_size_different

            var frame = new byte[Math.Max(length, (bits.Count + 7) / 8)];
            for (int i = 0; i < bits.Count; i++)
            {
                frame[i >> 3] |= (byte)(bits[i] << (7 - (i & 7)));
            }

            for (int i = (bits.Count + 7) / 8; i < frame.Length; i++)
            {
                frame[i] = (byte)i;
            }

            return frame;
        }

        /// <summary>The start of a VP9 inter frame of profile 0, followed by filler.</summary>
        internal static byte[] InterFrame(int length)
        {
            var frame = Enumerable.Range(0, length).Select(i => (byte)(i * 7)).ToArray();

            // frame_marker 2, profile 0, show_existing_frame 0, frame_type NON_KEY, show_frame 1, error_resilient 0
            frame[0] = 0x86;
            return frame;
        }

        private static List<ReadOnlyMemory<byte>> One(byte[] sample) =>
            new List<ReadOnlyMemory<byte>> { new ReadOnlyMemory<byte>(sample) };

        [TestMethod]
        public void TheSdpNamesVp9AtNinetyKilohertz()
        {
            var track = new VP9Track { ID = 1 };
            string sdp = track.BuildSDP(new StringBuilder()).ToString();

            Assert.Contains("m=video 0 RTP/AVP 97", sdp, sdp);
            Assert.Contains("a=rtpmap:97 VP9/90000", sdp, sdp);
            Assert.Contains("a=control:trackID=1", sdp, sdp);

            // nothing known of the profile yet, so nothing claimed about it
            Assert.DoesNotContain("profile-id", sdp, sdp);
            Assert.IsTrue(track.IsReady, "VP9 has nothing out of band to wait for");
        }

        [TestMethod]
        public void TheSdpAnnouncesTheProfileOfTheLastKeyFrame()
        {
            var track = new VP9Track();

            var packets = RtpPackets.Take();
            track.CreateRtpPackets(One(KeyFrame(320, 240, 100, profile: 2)), 0, packets);
            packets.Release();

            Assert.Contains("a=fmtp:96 profile-id=2", track.BuildSDP(new StringBuilder()).ToString());

            track.ProfileId = 1;
            Assert.Contains("a=fmtp:96 profile-id=1", track.BuildSDP(new StringBuilder()).ToString(),
                "a profile set on the track is what it announces");
        }

        [TestMethod]
        public void AKeyFrameSaysItIsOneAndCarriesItsSize()
        {
            var track = new VP9Track();
            byte[] frame = KeyFrame(1280, 720, 300);

            var packets = RtpPackets.Take();
            track.CreateRtpPackets(One(frame), 9000, packets);

            try
            {
                Assert.IsTrue(packets.IsKeyFrame, "a key frame is one a client can be started on");
                Assert.HasCount(1, packets);

                var packet = packets[0].Span;
                int descriptor = packet[12];

                Assert.AreEqual(0x80, descriptor & 0x80, "I: the picture ID is present");
                Assert.AreEqual(0x00, descriptor & 0x40, "P: a key frame is not predicted from another");
                Assert.AreEqual(0x00, descriptor & 0x20, "L: no layer indices with one layer");
                Assert.AreEqual(0x00, descriptor & 0x10, "F: the non-flexible mode");
                Assert.AreEqual(0x08, descriptor & 0x08, "B: the frame starts here");
                Assert.AreEqual(0x04, descriptor & 0x04, "E: and ends here");
                Assert.AreEqual(0x02, descriptor & 0x02, "V: a key frame carries the scalability structure");
                Assert.AreEqual(0x80, packet[1] & 0x80, "the marker ends the picture");

                Assert.AreEqual(0x80, packet[13] & 0x80, "M: the picture ID is 15 bits");

                // SS: one spatial layer, its size given, no picture group
                Assert.AreEqual(0x10, packet[15]);
                Assert.AreEqual(1280, (packet[16] << 8) | packet[17]);
                Assert.AreEqual(720, (packet[18] << 8) | packet[19]);

                CollectionAssert.AreEqual(frame, packet.Slice(20).ToArray(), "the frame follows the descriptor unchanged");
            }
            finally
            {
                packets.Release();
            }
        }

        [TestMethod]
        public void AnInterFrameIsPredictedAndNotAKeyFrame()
        {
            var track = new VP9Track();
            byte[] frame = InterFrame(200);

            var packets = RtpPackets.Take();
            track.CreateRtpPackets(One(frame), 9000, packets);

            try
            {
                Assert.IsFalse(packets.IsKeyFrame, "a client cannot start on an inter frame");

                var packet = packets[0].Span;
                Assert.AreEqual(0x40, packet[12] & 0x40, "P: predicted from an earlier picture");
                Assert.AreEqual(0x00, packet[12] & 0x02, "V: the scalability structure is for key frames");
                CollectionAssert.AreEqual(frame, packet.Slice(15).ToArray());
            }
            finally
            {
                packets.Release();
            }
        }

        [TestMethod]
        public void AFrameLargerThanThePacketIsCutAndMarkedAtBothEnds()
        {
            var track = new VP9Track { PacketMTU = 300 };
            byte[] frame = KeyFrame(640, 480, 2000);

            var packets = RtpPackets.Take();
            track.CreateRtpPackets(One(frame), 9000, packets);

            try
            {
                Assert.IsGreaterThan(2, packets.Count, "a frame larger than the MTU should be cut up");

                int pictureId = -1;
                var sent = new List<byte>();

                for (int i = 0; i < packets.Count; i++)
                {
                    var packet = packets[i].Span;
                    bool first = i == 0, last = i == packets.Count - 1;

                    Assert.IsLessThanOrEqualTo(300, packet.Length, $"packet {i} is over the MTU");
                    Assert.AreEqual(first, (packet[12] & 0x08) != 0, $"packet {i}: B only on the first");
                    Assert.AreEqual(last, (packet[12] & 0x04) != 0, $"packet {i}: E only on the last");
                    Assert.AreEqual(first, (packet[12] & 0x02) != 0, $"packet {i}: SS only on the first");
                    Assert.AreEqual(last, (packet[1] & 0x80) != 0, $"packet {i}: marker only on the last");
                    Assert.AreEqual(0x00, packet[12] & 0x40, $"packet {i}: P is the frame's, on every packet");

                    int id = ((packet[13] & 0x7F) << 8) | packet[14];
                    if (pictureId < 0) pictureId = id;
                    Assert.AreEqual(pictureId, id, "every packet of a picture has its picture ID");

                    sent.AddRange(packet.Slice(first ? 20 : 15).ToArray());
                }

                CollectionAssert.AreEqual(frame, sent, "every byte of the frame should be sent once, in order");
            }
            finally
            {
                packets.Release();
            }
        }

        [TestMethod]
        public void ThePictureIdMovesOnByOneAPictureAndWraps()
        {
            var track = new VP9Track();
            var ids = new List<int>();

            for (int i = 0; i < 3; i++)
            {
                var packets = RtpPackets.Take();
                track.CreateRtpPackets(One(InterFrame(50)), (uint)(i * 3000), packets);
                ids.Add(((packets[0].Span[13] & 0x7F) << 8) | packets[0].Span[14]);
                packets.Release();
            }

            Assert.AreEqual((ids[0] + 1) & 0x7FFF, ids[1]);
            Assert.AreEqual((ids[1] + 1) & 0x7FFF, ids[2]);
        }

        [TestMethod]
        public void TheFramesOfOnePictureShareItsIdAndOnlyTheLastIsMarked()
        {
            var track = new VP9Track();

            var packets = RtpPackets.Take();
            track.CreateRtpPackets(new List<ReadOnlyMemory<byte>> { InterFrame(40), InterFrame(60) }, 3000, packets);

            try
            {
                Assert.HasCount(2, packets);

                Assert.AreEqual(0x0C, packets[0].Span[12] & 0x0C, "each frame has its own B and E");
                Assert.AreEqual(0x0C, packets[1].Span[12] & 0x0C);

                Assert.AreEqual(0, packets[0].Span[1] & 0x80, "the first frame does not end the picture");
                Assert.AreEqual(0x80, packets[1].Span[1] & 0x80, "the last one does");

                Assert.AreEqual(packets[0].Span[14], packets[1].Span[14], "one picture, one picture ID");
            }
            finally
            {
                packets.Release();
            }
        }

        [TestMethod]
        public void ASuperframeIsSentWholeAndIsAKeyFrameWhenItOpensWithOne()
        {
            var track = new VP9Track();

            // a key frame, an inter frame, and a superframe index of two 2-byte sizes
            byte[] key = KeyFrame(320, 240, 40);
            byte[] inter = InterFrame(30);
            byte marker = 0xC0 | (1 << 3) | 1;
            byte[] superframe = key.Concat(inter)
                .Concat(new byte[] { marker, (byte)key.Length, 0, (byte)inter.Length, 0, marker }).ToArray();

            var packets = RtpPackets.Take();
            track.CreateRtpPackets(One(superframe), 3000, packets);

            try
            {
                Assert.IsTrue(packets.IsKeyFrame);
                Assert.HasCount(1, packets);
                CollectionAssert.AreEqual(superframe, packets[0].Span.Slice(20).ToArray(),
                    "the superframe and its index are a decoder's to take apart");
            }
            finally
            {
                packets.Release();
            }
        }

        [TestMethod]
        public void SomethingThatIsNotAVp9HeaderIsSentAsAnInterFrame()
        {
            var track = new VP9Track();

            var packets = RtpPackets.Take();
            track.CreateRtpPackets(One(new byte[] { 0x00, 0x01, 0x02 }), 3000, packets);

            try
            {
                Assert.IsFalse(packets.IsKeyFrame, "nothing a decoder could start on was recognised");
                Assert.AreEqual(0x40, packets[0].Span[12] & 0x40);
            }
            finally
            {
                packets.Release();
            }
        }
    }
}
