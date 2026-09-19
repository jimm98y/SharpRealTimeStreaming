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
    /// What a client gets when it joins a stream that is already running.
    /// </summary>
    /// <remarks>
    /// A client almost never arrives at the start of a group of pictures. Everything from where it
    /// arrives until the next keyframe is described in terms of pictures it never saw, so a decoder
    /// handed them makes rubbish of them - ffmpeg says "co located POCs unavailable" and then
    /// "mmco: unref short failure" - until the group ends. Meanwhile the sound, which has no such
    /// dependency, plays from the first packet, so the two start a whole group apart.
    /// </remarks>
    [TestClass]
    public sealed class MidGroupJoinTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        /// <summary>A picture that refers to earlier ones, which is most of them.</summary>
        private static List<ReadOnlyMemory<byte>> Predicted()
        {
            var nal = new byte[400];
            nal[0] = 0x41; // non-IDR slice
            return new List<ReadOnlyMemory<byte>> { new ReadOnlyMemory<byte>(nal) };
        }

        /// <summary>A picture a decoder can start on.</summary>
        private static List<ReadOnlyMemory<byte>> Idr()
        {
            var nal = new byte[400];
            nal[0] = 0x65; // IDR slice
            return new List<ReadOnlyMemory<byte>> { new ReadOnlyMemory<byte>(nal) };
        }

        /// <summary>The NAL type of an RTP packet's payload, past the twelve byte header.</summary>
        private static int NalTypeOf(byte[] rtpPacket) => rtpPacket[12] & 0x1F;

        private const int NAL_IDR = 5;

        /// <summary>
        /// A client already watching, so that the stream is running rather than starting.
        /// </summary>
        /// <remarks>
        /// Nothing reads from it. Its queue is bounded and drops what it cannot take, which is what
        /// a client that stops reading does anyway.
        /// </remarks>
        private static RtspTestClient Playing(int port, string baseUri)
        {
            var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            client.Send("PLAY", baseUri, "Session: " + setup.Session);

            return client;
        }

        [TestMethod]
        public void TheFirstPictureAClientGetsIsOneItCanDecode()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");

            // Longer than this test can possibly take, so that the wait ending of its own accord -
            // which is a safety valve for producers whose frames cannot be read, not the behaviour
            // under test - cannot decide the result on a loaded machine.
            server.KeyFrameWait = TimeSpan.FromMinutes(5);

            var video = new H264Track(Sps, Pps);
            server.AddStreamSource(new RTSPStreamSource("stream1", video, null));
            server.StartListen();

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            // Somebody is already watching, which is what makes this a running stream rather than
            // one that starts when the client below asks for it.
            using var watching = Playing(port, baseUri);

            // And a keyframe has gone by before the client below turns up: that is what says this is
            // a stream with keyframes worth waiting for.
            video.FeedInRawSamples(3750, Idr());
            Thread.Sleep(20);

            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            Assert.AreEqual(200, client.Send("PLAY", baseUri, "Session: " + setup.Session).StatusCode);

            // Joined just after that keyframe, which is the common case and the worst one: a whole
            // group of predicted pictures before the next one it could start on.
            for (int i = 0; i < 40; i++)
            {
                video.FeedInRawSamples((uint)((i + 1) * 3750), Predicted());
                Thread.Sleep(5);
            }

            video.FeedInRawSamples(41 * 3750, Idr());

            for (int i = 0; i < 5; i++)
            {
                video.FeedInRawSamples((uint)((i + 42) * 3750), Predicted());
                Thread.Sleep(5);
            }

            byte[] firstPicture = null;

            for (int i = 0; i < 20 && firstPicture == null; i++)
            {
                var frame = client.ReadInterleaved();

                if (frame.Channel == 0)
                {
                    firstPicture = frame.Payload;
                }
            }

            Assert.IsNotNull(firstPicture, "no picture arrived at all");

            Assert.AreEqual(NAL_IDR, NalTypeOf(firstPicture),
                "the first picture sent was one the client had no way to decode");
        }

        /// <summary>
        /// A producer that starts its stream when the first client asks for it.
        /// </summary>
        /// <remarks>
        /// The keyframe goes past while the client is still saying SETUP and PLAY. Nothing was
        /// playing at that moment, so the frame was turned away before it was ever packetised - and
        /// it was the one frame the client actually needed. Everything after it is described in
        /// terms of it, so the client decoded rubbish until the next keyframe came round a whole
        /// group later, while the sound played on without it.
        /// </remarks>
        [TestMethod]
        public void AKeyFrameFedBeforeTheClientSaysPlayStillReachesIt()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");
            server.KeyFrameWait = TimeSpan.FromMinutes(5);

            var video = new H264Track(Sps, Pps);
            server.AddStreamSource(new RTSPStreamSource("stream1", video, null));
            server.StartListen();

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");

            // The stream starts here, because this is the first client to ask for it - and the file
            // it is read from begins with a keyframe. PLAY has not been sent yet.
            video.FeedInRawSamples(3750, Idr());

            for (int i = 0; i < 3; i++)
            {
                video.FeedInRawSamples((uint)((i + 2) * 3750), Predicted());
            }

            Assert.AreEqual(200, client.Send("PLAY", baseUri, "Session: " + setup.Session).StatusCode);

            for (int i = 0; i < 3; i++)
            {
                video.FeedInRawSamples((uint)((i + 5) * 3750), Predicted());
                Thread.Sleep(5);
            }

            byte[] firstPicture = null;

            for (int i = 0; i < 20 && firstPicture == null; i++)
            {
                var frame = client.ReadInterleaved();

                if (frame.Channel == 0)
                {
                    firstPicture = frame.Payload;
                }
            }

            Assert.IsNotNull(firstPicture, "no picture arrived at all");

            Assert.AreEqual(NAL_IDR, NalTypeOf(firstPicture),
                "the keyframe fed before PLAY never reached the client");
        }

        /// <summary>
        /// Starting the media when PLAY has been answered, which is the arrangement that needs no
        /// keyframe kept at all.
        /// </summary>
        /// <remarks>
        /// A producer that starts on the first request hands its keyframe over while the client is
        /// still setting up. Starting it when PLAY completes instead means the first frame it
        /// produces goes to a client that is already listening. The message event is raised for
        /// exactly that moment - after the session is marked as playing and after the response has
        /// gone out, and only when the PLAY was accepted - so a handler may start producing there
        /// and have the first frame arrive.
        /// </remarks>
        [TestMethod]
        public void MediaStartedWhenPlayIsAnsweredArrivesFromItsFirstFrame()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");
            server.KeyFrameWait = TimeSpan.FromMinutes(5);

            var video = new H264Track(Sps, Pps);
            var source = new RTSPStreamSource("stream1", video, null);

            server.AddStreamSource(source);

            server.ReceivedRtspMessage += (s, e) =>
            {
                if (e.Message is Rtsp.Messages.RtspRequestPlay)
                {
                    // the stream starts here, and starts where a file does - with a keyframe
                    video.FeedInRawSamples(3750, Idr());

                    for (int i = 0; i < 3; i++)
                    {
                        video.FeedInRawSamples((uint)((i + 2) * 3750), Predicted());
                    }
                }
            };

            server.StartListen();

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");

            Assert.AreEqual(200, client.Send("PLAY", baseUri, "Session: " + setup.Session).StatusCode);

            byte[] firstPicture = null;

            for (int i = 0; i < 20 && firstPicture == null; i++)
            {
                var frame = client.ReadInterleaved();

                if (frame.Channel == 0)
                {
                    firstPicture = frame.Payload;
                }
            }

            Assert.IsNotNull(firstPicture, "no picture arrived at all");

            Assert.AreEqual(NAL_IDR, NalTypeOf(firstPicture),
                "the first frame of a stream started on PLAY did not reach the client");
        }

        /// <summary>
        /// The picture kept from earlier, for a client that would otherwise see nothing.
        /// </summary>
        /// <remarks>
        /// A client joining mid-group has nothing it can decode until the next keyframe. Asking the
        /// encoder for one ends that properly; where there is nothing to ask, or nothing answers,
        /// the last keyframe the stream produced at least gives it a real picture to show. What
        /// follows still decodes imperfectly until a live keyframe arrives - this buys something
        /// visible rather than a clean stream.
        /// </remarks>
        [TestMethod]
        public void AClientWithNothingToShowIsSentTheLastKeyFrameKept()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");

            // Short, because the fallback is what happens when this runs out.
            server.KeyFrameWait = TimeSpan.FromMilliseconds(200);

            var video = new H264Track(Sps, Pps);
            var source = new RTSPStreamSource("stream1", video, null);
            source.KeepLastKeyFrame = true;

            server.AddStreamSource(source);
            server.StartListen();

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            using var setupOnly = new RtspTestClient(port, "admin", "password");
            setupOnly.Send("OPTIONS", baseUri);
            setupOnly.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            // The stream runs for a while, with its keyframe well in the past by the time the client
            // below arrives.
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

            // Nothing but predicted pictures from here, so the only keyframe that can arrive is the
            // one that was kept.
            for (int i = 0; i < 30; i++)
            {
                video.FeedInRawSamples((uint)((i + 20) * 3750), Predicted());
                Thread.Sleep(20);
            }

            byte[] firstPicture = null;

            for (int i = 0; i < 20 && firstPicture == null; i++)
            {
                var frame = client.ReadInterleaved();

                if (frame.Channel == 0)
                {
                    firstPicture = frame.Payload;
                }
            }

            Assert.IsNotNull(firstPicture, "no picture arrived at all");

            Assert.AreEqual(NAL_IDR, NalTypeOf(firstPicture),
                "the client was started on a picture it could not decode, with a keyframe kept");
        }

        /// <summary>
        /// The keyframe a stream produces before anyone has connected to it.
        /// </summary>
        /// <remarks>
        /// A producer that starts its stream when the first client asks for it hands the keyframe
        /// over while that client is still connecting - and a stream that only produces once
        /// somebody is attached is a stream whose very first keyframe is always the one that got
        /// away, leaving the client to wait a whole group for the next.
        /// </remarks>
        [TestMethod]
        public void AKeyFrameProducedBeforeAnyoneConnectsIsStillThereForTheFirstClient()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");
            server.KeyFrameWait = TimeSpan.FromMinutes(5);

            var video = new H264Track(Sps, Pps);
            var source = new RTSPStreamSource("stream1", video, null);
            source.KeepLastKeyFrame = true;

            server.AddStreamSource(source);
            server.StartListen();

            // Nobody has connected yet. This is the beginning of the file.
            video.FeedInRawSamples(3750, Idr());

            for (int i = 0; i < 3; i++)
            {
                video.FeedInRawSamples((uint)((i + 2) * 3750), Predicted());
            }

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            Assert.AreEqual(200, client.Send("PLAY", baseUri, "Session: " + setup.Session).StatusCode);

            // Only predicted pictures from here, so the keyframe can only be the one kept.
            for (int i = 0; i < 20; i++)
            {
                video.FeedInRawSamples((uint)((i + 10) * 3750), Predicted());
                Thread.Sleep(10);
            }

            byte[] firstPicture = null;

            for (int i = 0; i < 20 && firstPicture == null; i++)
            {
                var frame = client.ReadInterleaved();

                if (frame.Channel == 0)
                {
                    firstPicture = frame.Payload;
                }
            }

            Assert.IsNotNull(firstPicture, "no picture arrived at all");

            Assert.AreEqual(NAL_IDR, NalTypeOf(firstPicture),
                "the keyframe the stream opened with was gone by the time the first client arrived");
        }

        /// <summary>
        /// A stream whose keyframes cannot be recognised is not held up waiting for one.
        /// </summary>
        /// <remarks>
        /// Whether a frame can be started on is read out of the bytes, and a producer may hand over
        /// something no track here reads that way. Waiting then means every client starts late and
        /// still imperfectly, which is worse in both directions than not waiting at all.
        /// </remarks>
        [TestMethod]
        public void AStreamWithNoRecognisableKeyFrameIsNotHeldUp()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");

            // Far longer than this test runs: if anything waits, nothing arrives.
            server.KeyFrameWait = TimeSpan.FromMinutes(5);

            var video = new H264Track(Sps, Pps);
            server.AddStreamSource(new RTSPStreamSource("stream1", video, null));
            server.StartListen();

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            Assert.AreEqual(200, client.Send("PLAY", baseUri, "Session: " + setup.Session).StatusCode);

            byte[] arrived = null;

            for (int i = 0; i < 30 && arrived == null; i++)
            {
                video.FeedInRawSamples((uint)((i + 1) * 3750), Predicted());
                Thread.Sleep(10);

                var frame = client.ReadInterleaved();

                if (frame.Channel == 0)
                {
                    arrived = frame.Payload;
                }
            }

            Assert.IsNotNull(arrived,
                "nothing arrived: a stream whose keyframes cannot be recognised was held up for one");
        }

        /// <summary>
        /// The kept picture is held, not broken up by the live pictures that follow it.
        /// </summary>
        /// <remarks>
        /// The frames after the kept keyframe describe changes to pictures this client never saw, so
        /// sending them on top of it takes a correct still picture and turns it into a moving mess.
        /// It keeps the still until the next live keyframe starts the stream again properly, which
        /// is a clean cut rather than a recovery.
        /// </remarks>
        [TestMethod]
        public void TheKeptPictureIsHeldUntilTheNextGroupRatherThanBrokenUp()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");
            server.KeyFrameWait = TimeSpan.FromMinutes(5);

            var video = new H264Track(Sps, Pps);
            var source = new RTSPStreamSource("stream1", video, null);
            source.KeepLastKeyFrame = true;

            server.AddStreamSource(source);
            server.StartListen();

            // The stream opens with a keyframe, before anyone has connected, and runs on.
            video.FeedInRawSamples(3750, Idr());

            for (int i = 0; i < 5; i++)
            {
                video.FeedInRawSamples((uint)((i + 2) * 3750), Predicted());
            }

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            Assert.AreEqual(200, client.Send("PLAY", baseUri, "Session: " + setup.Session).StatusCode);

            // The rest of the group this client arrived in the middle of.
            for (int i = 0; i < 12; i++)
            {
                video.FeedInRawSamples((uint)((i + 10) * 3750), Predicted());
                Thread.Sleep(5);
            }

            // Then the stream starts a new group, which is where this client can join properly.
            video.FeedInRawSamples(30 * 3750, Idr());

            for (int i = 0; i < 4; i++)
            {
                video.FeedInRawSamples((uint)((i + 31) * 3750), Predicted());
                Thread.Sleep(5);
            }

            var pictures = new List<int>();

            for (int i = 0; i < 25 && pictures.Count < 3; i++)
            {
                var frame = client.ReadInterleaved();

                if (frame.Channel == 0)
                {
                    pictures.Add(NalTypeOf(frame.Payload));
                }
            }

            Assert.IsGreaterThanOrEqualTo(2, pictures.Count, "too little arrived to say anything");

            Assert.AreEqual(NAL_IDR, pictures[0], "the client was not started on the keyframe kept for it");

            Assert.AreEqual(NAL_IDR, pictures[1],
                "the still picture was broken up by pictures referring to frames the client never saw, "
                + "instead of being held until the next group");
        }

        [TestMethod]
        public void TheEncoderIsAskedForAPictureTheClientCanStartOn()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");

            // Longer than this test can take, so that the wait ending of its own accord - after
            // which there is nothing to ask for - cannot decide the result on a loaded machine.
            server.KeyFrameWait = TimeSpan.FromMinutes(5);

            var video = new H264Track(Sps, Pps);
            server.AddStreamSource(new RTSPStreamSource("stream1", video, null));

            var asked = new ManualResetEventSlim(false);
            server.KeyFrameNeeded += (s, e) => asked.Set();

            server.StartListen();

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            // A stream already running, with a keyframe behind it - there is no sense asking an
            // encoder for something this stream has never been seen to produce.
            using var watching = Playing(port, baseUri);
            video.FeedInRawSamples(3750, Idr());
            Thread.Sleep(20);

            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            client.Send("PLAY", baseUri, "Session: " + setup.Session);

            for (int i = 0; i < 5 && !asked.IsSet; i++)
            {
                video.FeedInRawSamples((uint)((i + 1) * 3750), Predicted());
                Thread.Sleep(20);
            }

            Assert.IsTrue(asked.Wait(TimeSpan.FromSeconds(2)),
                "the server never said a keyframe would let this client start");
        }
    }
}
