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
using System.Buffers.Binary;
using System.Collections.Generic;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// A frame is copied once and shared by every client watching, so what each of them receives has
    /// to be intact - a share given up too early would hand the buffer back while someone was still
    /// writing from it.
    /// </summary>
    [TestClass]
    public sealed class SharedFrameTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        private static RtspTestClient Play(int port, string baseUri)
        {
            var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");
            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            Assert.AreEqual(200, setup.StatusCode);
            Assert.AreEqual(200, client.Send("PLAY", baseUri, "Session: " + setup.Session).StatusCode);
            return client;
        }

        /// <summary>A NAL whose body is the frame number repeated, so a mixed up buffer is obvious.</summary>
        private static List<ReadOnlyMemory<byte>> NumberedNal(byte number, int size = 600)
        {
            var nal = new byte[size];
            nal[0] = 0x65;
            for (int i = 1; i < size; i++)
            {
                nal[i] = number;
            }
            return new List<ReadOnlyMemory<byte>> { new ReadOnlyMemory<byte>(nal) };
        }

        private static byte[] PayloadOf(byte[] rtpPacket) => rtpPacket.AsSpan(12).ToArray();

        private static ushort Seq(byte[] rtpPacket) => BinaryPrimitives.ReadUInt16BigEndian(rtpPacket.AsSpan(2));

        [TestMethod]
        public void EveryClientGetsTheWholeFrameIntact()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, new InMemoryUserRepository("admin", "password"));
            var videoTrack = new H264Track(Sps, Pps);
            server.AddStreamSource(new RTSPStreamSource("stream1", videoTrack, null));
            server.StartListen();

            string baseUri = "rtsp://127.0.0.1:" + port + "/stream1";

            using var first = Play(port, baseUri);
            using var second = Play(port, baseUri);
            using var third = Play(port, baseUri);

            const int frames = 40;
            for (byte i = 0; i < frames; i++)
            {
                videoTrack.FeedInRawSamples((uint)(i * 3000), NumberedNal(i));
            }

            foreach (var client in new[] { first, second, third })
            {
                for (byte expected = 0; expected < frames; expected++)
                {
                    byte[] payload = PayloadOf(NextRtp(client));

                    Assert.AreEqual(0x65, payload[0], "the NAL header should be the one that was fed");
                    for (int i = 1; i < payload.Length; i++)
                    {
                        Assert.AreEqual(expected, payload[i],
                            $"frame {expected} came out with byte {i} belonging to something else");
                    }
                }
            }
        }

        [TestMethod]
        public void EachClientGetsItsOwnSequenceNumbering()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, new InMemoryUserRepository("admin", "password"));
            var videoTrack = new H264Track(Sps, Pps);
            server.AddStreamSource(new RTSPStreamSource("stream1", videoTrack, null));
            server.StartListen();

            string baseUri = "rtsp://127.0.0.1:" + port + "/stream1";

            using var first = Play(port, baseUri);
            using var second = Play(port, baseUri);

            const int frames = 60;
            for (byte i = 0; i < frames; i++)
            {
                videoTrack.FeedInRawSamples((uint)(i * 3000), NumberedNal(i));
            }

            // One frame is shared by both connections, but the sequence number stamped into it is
            // each connection's own - counted separately, from one. Stamping them into the shared
            // bytes had each client writing over the other, so what either received was whichever
            // got there last.
            foreach (var client in new[] { first, second })
            {
                for (int expected = 1; expected <= frames; expected++)
                {
                    Assert.AreEqual((ushort)expected, Seq(NextRtp(client)),
                        "each connection numbers what it sends for itself");
                }
            }
        }

        [TestMethod]
        public void SamplesWithNothingInThemAreNotSent()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, new InMemoryUserRepository("admin", "password"));
            var videoTrack = new H264Track(Sps, Pps);
            server.AddStreamSource(new RTSPStreamSource("stream1", videoTrack, null));
            server.StartListen();

            using var client = Play(port, "rtsp://127.0.0.1:" + port + "/stream1");

            // An empty sample used to go out as an RTP packet with no payload for the receiver to
            // puzzle over, and a trailing one took the marker bit with it.
            videoTrack.FeedInRawSamples(9000, new List<ReadOnlyMemory<byte>>
            {
                ReadOnlyMemory<byte>.Empty,
                new ReadOnlyMemory<byte>(new byte[] { 0x65, 0xAA }),
                ReadOnlyMemory<byte>.Empty,
                new ReadOnlyMemory<byte>(new byte[] { 0x65, 0xBB }),
                ReadOnlyMemory<byte>.Empty,
            });

            byte[] firstPacket = NextRtp(client);
            CollectionAssert.AreEqual(new byte[] { 0x65, 0xAA }, PayloadOf(firstPacket));
            Assert.AreEqual(0, MarkerOf(firstPacket), "the marker belongs on the last one sent");

            byte[] secondPacket = NextRtp(client);
            CollectionAssert.AreEqual(new byte[] { 0x65, 0xBB }, PayloadOf(secondPacket));
            Assert.AreEqual(1, MarkerOf(secondPacket), "the last one actually sent carries the marker");
        }

        [TestMethod]
        public void ASampleListOfNothingButEmptiesSendsNothing()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, new InMemoryUserRepository("admin", "password"));
            var videoTrack = new H264Track(Sps, Pps);
            server.AddStreamSource(new RTSPStreamSource("stream1", videoTrack, null));
            server.StartListen();

            using var client = Play(port, "rtsp://127.0.0.1:" + port + "/stream1");

            videoTrack.FeedInRawSamples(9000, new List<ReadOnlyMemory<byte>>
            {
                ReadOnlyMemory<byte>.Empty,
                ReadOnlyMemory<byte>.Empty,
            });

            // then something real, which should be the first thing that arrives
            videoTrack.FeedInRawSamples(12000, new List<ReadOnlyMemory<byte>>
            {
                new ReadOnlyMemory<byte>(new byte[] { 0x65, 0xCC }),
            });

            CollectionAssert.AreEqual(new byte[] { 0x65, 0xCC }, PayloadOf(NextRtp(client)));
        }

        private static int MarkerOf(byte[] rtpPacket) => rtpPacket[1] >> 7;

        private static byte[] NextRtp(RtspTestClient client)
        {
            for (int i = 0; i < 20; i++)
            {
                var frame = client.ReadInterleaved();
                if (frame.Channel == 0)
                    return frame.Payload;
            }

            Assert.Fail("no RTP arrived");
            return null;
        }
    }
}
