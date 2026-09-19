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
using System.Threading;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// Keeping the last keyframe is a choice, and turning it off really does turn it off.
    /// </summary>
    /// <remarks>
    /// <see cref="MidGroupJoinTests"/> covers what it buys when it is on: a client that arrives
    /// mid-group is given a real picture at once. What it costs is a frame of memory per stream and
    /// a client started on a picture the live stream has moved on from, so a deployment that would
    /// rather have a clean stream late than a stale one now can say so.
    /// </remarks>
    [TestClass]
    public sealed class KeepLastKeyFrameTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        private const int NAL_IDR = 5;

        private static List<ReadOnlyMemory<byte>> Predicted()
        {
            var nal = new byte[400];
            nal[0] = 0x41; // non-IDR slice
            return new List<ReadOnlyMemory<byte>> { new ReadOnlyMemory<byte>(nal) };
        }

        private static List<ReadOnlyMemory<byte>> Idr()
        {
            var nal = new byte[400];
            nal[0] = 0x65; // IDR slice
            return new List<ReadOnlyMemory<byte>> { new ReadOnlyMemory<byte>(nal) };
        }

        private static int NalTypeOf(byte[] rtpPacket) => rtpPacket[12] & 0x1F;

        [TestMethod]
        public void ItIsOffUnlessItIsAskedFor()
        {
            var source = new RTSPStreamSource("stream1", new H264Track(Sps, Pps), null);

            Assert.IsFalse(source.KeepLastKeyFrame,
                "it costs a frame of memory per stream and starts a client on a stale picture");
        }

        [TestMethod]
        public void NothingIsKeptWhileItIsOff()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, new InMemoryUserRepository("admin", "password"));

            var video = new H264Track(Sps, Pps);
            var source = new RTSPStreamSource("stream1", video, null) { KeepLastKeyFrame = false };

            server.AddStreamSource(source);
            server.StartListen();

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            // Somebody has to be attached, or the stream declines the samples altogether.
            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            video.FeedInRawSamples(3750, Idr());
            Thread.Sleep(100);

            Assert.IsNull(source.LastKeyFrameOrNull(),
                "a keyframe was held on to by a stream that was told not to");
        }

        [TestMethod]
        public void TheKeptFrameIsLetGoWhenItIsTurnedOff()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, new InMemoryUserRepository("admin", "password"));

            var video = new H264Track(Sps, Pps);
            var source = new RTSPStreamSource("stream1", video, null) { KeepLastKeyFrame = true };

            server.AddStreamSource(source);
            server.StartListen();

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            video.FeedInRawSamples(3750, Idr());
            Thread.Sleep(100);

            Assert.IsNotNull(source.LastKeyFrameOrNull(), "the test proves nothing if none was kept");

            // The buffers it holds are pooled and belong to nobody until it lets go of them.
            source.ForgetLastKeyFrame();

            Assert.IsNull(source.LastKeyFrameOrNull());
        }

        [TestMethod]
        public void AClientJoiningMidGroupWaitsRatherThanBeingStartedOnAStalePicture()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, new InMemoryUserRepository("admin", "password"));

            // Short, so that the wait runs out during the test - with a keyframe kept this is when
            // the stale one would be sent, and with it off nothing should be.
            server.KeyFrameWait = TimeSpan.FromMilliseconds(200);

            var video = new H264Track(Sps, Pps);
            var source = new RTSPStreamSource("stream1", video, null) { KeepLastKeyFrame = false };

            server.AddStreamSource(source);
            server.StartListen();

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            using var setupOnly = new RtspTestClient(port, "admin", "password");
            setupOnly.Send("OPTIONS", baseUri);
            setupOnly.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            // The stream's keyframe is well in the past by the time the client below arrives.
            video.FeedInRawSamples(3750, Idr());

            for (int i = 0; i < 5; i++)
            {
                video.FeedInRawSamples((uint)((i + 2) * 3750), Predicted());
            }

            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            Assert.AreEqual(200, client.Send("PLAY", baseUri, "Session: " + setup.Session).StatusCode);

            // Only predicted pictures from here. With a keyframe kept the client would be started on
            // it; without one there is no keyframe that can reach this client at all.
            for (int i = 0; i < 30; i++)
            {
                video.FeedInRawSamples((uint)((i + 20) * 3750), Predicted());
                Thread.Sleep(20);
            }

            for (int i = 0; i < 20; i++)
            {
                var frame = client.ReadInterleaved();

                if (frame.Channel == 0)
                {
                    Assert.AreNotEqual(NAL_IDR, NalTypeOf(frame.Payload),
                        "a keyframe reached a client of a stream that keeps none, so one was kept after all");
                }
            }
        }

        [TestMethod]
        public void TurningItOnStillKeepsOne()
        {
            // The counterpart of the above, so that neither test can pass by the stream simply
            // never producing anything.
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, new InMemoryUserRepository("admin", "password"));

            var video = new H264Track(Sps, Pps);
            var source = new RTSPStreamSource("stream1", video, null) { KeepLastKeyFrame = true };

            server.AddStreamSource(source);
            server.StartListen();

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            video.FeedInRawSamples(3750, Idr());
            Thread.Sleep(100);

            Assert.IsNotNull(source.LastKeyFrameOrNull());
        }

        [TestMethod]
        public void AStreamThatKeepsNoneTakesNoSamplesWithNobodyAttached()
        {
            // Keeping one is also what makes a stream packetise for a client that has not arrived
            // yet, so turning it off puts that back to nothing as well.
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, new InMemoryUserRepository("admin", "password"));

            var video = new H264Track(Sps, Pps);
            var keeps = new RTSPStreamSource("keeps", video, null) { KeepLastKeyFrame = true };
            var doesNot = new RTSPStreamSource("doesNot", new H264Track(Sps, Pps), null) { KeepLastKeyFrame = false };

            server.AddStreamSource(keeps);
            server.AddStreamSource(doesNot);
            server.StartListen();

            Assert.IsTrue(server.CanAcceptNewSamples("keeps"),
                "a stream keeping a keyframe takes samples so the first one is not the one that got away");

            Assert.IsFalse(server.CanAcceptNewSamples("doesNot"),
                "a stream keeping none has nobody to packetise for");
        }
    }
}
