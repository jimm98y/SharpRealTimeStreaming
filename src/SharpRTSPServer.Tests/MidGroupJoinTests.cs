using System;
using System.Collections.Generic;
using System.Threading;
using SharpRTSPServer;

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

            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            Assert.AreEqual(200, client.Send("PLAY", baseUri, "Session: " + setup.Session).StatusCode);

            // Joined just after a keyframe, which is the common case and the worst one: a whole
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
