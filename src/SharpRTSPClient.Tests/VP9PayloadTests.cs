// SharpRTSPClient
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

using Rtsp.Rtp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace SharpRTSPClient.Tests
{
    /// <summary>
    /// VP9 taken apart by the server's track and put back together by the client.
    /// </summary>
    [TestClass]
    public sealed class VP9PayloadTests
    {
        /// <summary>A profile 0 key frame of 320x240, then filler.</summary>
        private static byte[] KeyFrame(int length)
        {
            // frame_marker 2, profile 0, show_existing 0, KEY_FRAME, show_frame 1, error_resilient 0,
            // sync code, color_space 1 and color_range 0, then 319 and 239 as 16 bits each
            byte[] header = { 0x82, 0x49, 0x83, 0x42, 0x20, 0x13, 0xF0, 0x0E, 0xF0 };
            var frame = new byte[length];
            for (int i = 0; i < length; i++) frame[i] = (byte)(i * 3);
            header.CopyTo(frame, 0);
            return frame;
        }

        private static byte[] InterFrame(int length)
        {
            var frame = new byte[length];
            for (int i = 0; i < length; i++) frame[i] = (byte)(i * 5);
            frame[0] = 0x86;
            return frame;
        }

        private ushort _sequence = 1000;

        /// <summary>
        /// The packets the server's track makes of one picture, numbered as the server would number
        /// them on the way out.
        /// </summary>
        private List<byte[]> Packetize(SharpRTSPServer.VP9Track track, uint timestamp, params byte[][] frames)
        {
            var packets = SharpRTSPServer.RtpPackets.Take();
            track.CreateRtpPackets(frames.Select(f => (ReadOnlyMemory<byte>)f).ToList(), timestamp, packets);

            var result = new List<byte[]>();
            for (int i = 0; i < packets.Count; i++)
            {
                byte[] packet = packets[i].ToArray();
                packet[2] = (byte)(_sequence >> 8);
                packet[3] = (byte)_sequence;
                _sequence++;
                result.Add(packet);
            }

            packets.Release();
            return result;
        }

        /// <summary>The frames of each picture the payload hands up, copied out before they are released.</summary>
        private static List<List<byte[]>> Depacketize(VP9Payload payload, IEnumerable<byte[]> packets)
        {
            var pictures = new List<List<byte[]>>();

            foreach (byte[] packet in packets)
            {
                using RawMediaFrame frame = payload.ProcessPacket(new RtpPacket(packet));
                if (frame.Any())
                {
                    pictures.Add(frame.Data.Select(d => d.ToArray()).ToList());
                }
            }

            return pictures;
        }

        [TestMethod]
        public void AFrameCutIntoManyPacketsComesBackWhole()
        {
            var track = new SharpRTSPServer.VP9Track { PacketMTU = 200 };
            byte[] key = KeyFrame(3000);
            byte[] inter = InterFrame(900);

            var packets = Packetize(track, 3000, key).Concat(Packetize(track, 6000, inter)).ToList();
            Assert.IsGreaterThan(10, packets.Count, "the frames should have been cut up");

            var pictures = Depacketize(new VP9Payload(), packets);

            Assert.HasCount(2, pictures);
            Assert.HasCount(1, pictures[0]);
            CollectionAssert.AreEqual(key, pictures[0][0], "the descriptors should be gone and nothing else");
            CollectionAssert.AreEqual(inter, pictures[1][0]);
        }

        [TestMethod]
        public void TheFramesOfOnePictureComeBackSeparately()
        {
            var track = new SharpRTSPServer.VP9Track();
            byte[] key = KeyFrame(100);
            byte[] inter = InterFrame(80);

            var pictures = Depacketize(new VP9Payload(), Packetize(track, 3000, key, inter));

            // joined, they would be neither frame - a decoder is given each as it stands
            Assert.HasCount(1, pictures);
            Assert.HasCount(2, pictures[0]);
            CollectionAssert.AreEqual(key, pictures[0][0]);
            CollectionAssert.AreEqual(inter, pictures[0][1]);
        }

        [TestMethod]
        public void NothingIsHandedUpBeforeTheFirstKeyFrame()
        {
            var track = new SharpRTSPServer.VP9Track();

            var packets = Packetize(track, 3000, InterFrame(100))
                .Concat(Packetize(track, 6000, InterFrame(100)))
                .Concat(Packetize(track, 9000, KeyFrame(100)))
                .Concat(Packetize(track, 12000, InterFrame(100)))
                .ToList();

            var pictures = Depacketize(new VP9Payload(), packets);

            Assert.HasCount(2, pictures, "a decoder can do nothing with what comes before a key frame");
            Assert.AreEqual(0x82, pictures[0][0][0], "and the first it is given is the key frame");
        }

        [TestMethod]
        public void AFrameThatLostAPacketIsDroppedAndTheNextOneIsNot()
        {
            var track = new SharpRTSPServer.VP9Track { PacketMTU = 200 };

            var first = Packetize(track, 3000, KeyFrame(1000));
            var second = Packetize(track, 6000, InterFrame(1000));
            var third = Packetize(track, 9000, InterFrame(100));

            first.RemoveAt(2);

            var pictures = Depacketize(new VP9Payload(), first.Concat(second).Concat(third));

            // The first picture ends with a marker and no frame in it, which is no picture at all;
            // the others follow from a key frame the decoder never had, but they are whole and
            // concealing that is the decoder's business.
            Assert.HasCount(2, pictures, "the broken key frame goes, the whole pictures after it do not");
            Assert.AreEqual(1000, pictures[0][0].Length);
            Assert.AreEqual(0x86, pictures[0][0][0]);
            Assert.AreEqual(100, pictures[1][0].Length);
        }

        [TestMethod]
        public void ALostEndOfPictureDoesNotJoinItOntoTheNext()
        {
            var track = new SharpRTSPServer.VP9Track { PacketMTU = 200 };
            byte[] key = KeyFrame(1000);
            byte[] next = KeyFrame(1000);

            var first = Packetize(track, 3000, key);
            var second = Packetize(track, 6000, next);

            // the last packet of the first picture is lost, and with it the marker and the E bit
            first.RemoveAt(first.Count - 1);

            var pictures = Depacketize(new VP9Payload(), first.Concat(second));
            Assert.HasCount(1, pictures);
            CollectionAssert.AreEqual(next, pictures[0][0]);
        }

        [TestMethod]
        public void TheDescriptorIsReadInEitherMode()
        {
            // flexible, picture ID of 7 bits, layer indices, two reference diffs, B and E
            byte[] flexible = { 0xFC, 0x05, 0x02, 0x03, 0x02 };
            Assert.IsTrue(VP9Payload.TryReadDescriptor(flexible.Concat(new byte[] { 0xAA }).ToArray(), out var d));
            Assert.AreEqual(5, d.Length);
            Assert.IsTrue(d.InterPicturePredicted);
            Assert.AreEqual(1, d.SpatialId);

            // non-flexible, 15 bit picture ID, layer indices and TL0PICIDX, and an SS of two spatial
            // layers with their sizes and a picture group of one picture with one reference
            byte[] nonFlexible =
            {
                0xAE, 0x81, 0x23, 0x00, 0x07,
                0x38, 0x01, 0x40, 0x00, 0xB4, 0x02, 0x80, 0x01, 0x68,
                0x01, 0x04, 0x01,
            };
            Assert.IsTrue(VP9Payload.TryReadDescriptor(nonFlexible.Concat(new byte[] { 0xAA }).ToArray(), out d));
            Assert.AreEqual(nonFlexible.Length, d.Length);
            Assert.IsTrue(d.StartOfFrame);
            Assert.IsTrue(d.EndOfFrame);
            Assert.IsFalse(d.InterPicturePredicted);

            // a descriptor that says more follows than there is
            Assert.IsFalse(VP9Payload.TryReadDescriptor(new byte[] { 0x80 }, out _));
            Assert.IsFalse(VP9Payload.TryReadDescriptor(new byte[] { 0x92, 0x01 }, out _));
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        [TestMethod]
        public void TheClientPlaysVp9FromTheServer()
        {
            int port = FreePort();

            using var server = new SharpRTSPServer.RTSPServer(port, new SharpRTSPServer.InMemoryUserRepository("admin", "password"));
            var video = new SharpRTSPServer.VP9Track();
            server.AddStreamSource(new SharpRTSPServer.RTSPStreamSource("stream1", video, null));
            server.StartListen();

            using var client = new RTSPClient();

            string codec = null;
            byte[] received = null;
            var arrived = new ManualResetEventSlim();

            client.NewTrack += (s, e) => codec = e.Codec;
            client.ReceivedData += (s, e) =>
            {
                if (received == null)
                {
                    // released once the handler returns, so copied
                    received = e.Data.Data.First().ToArray();
                    arrived.Set();
                }
            };

            client.Connect($"rtsp://127.0.0.1:{port}/stream1", RTPTransport.TCP, "admin", "password");
            client.Play();

            byte[] key = KeyFrame(5000);
            var samples = new List<ReadOnlyMemory<byte>> { new ReadOnlyMemory<byte>(key) };

            for (int i = 0; i < 100 && !arrived.IsSet; i++)
            {
                video.FeedInRawSamples((uint)((i + 1) * 3000), samples);
                Thread.Sleep(20);
            }

            Assert.IsTrue(arrived.Wait(TimeSpan.FromSeconds(5)), "the client received nothing the server sent");
            Assert.AreEqual("VP9", codec);
            CollectionAssert.AreEqual(key, received, "the key frame should arrive as it was sent");

            client.Stop();
        }
    }
}
