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
using System.Globalization;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// What the server actually puts in the RTP headers it sends, read back off the wire through an
    /// interleaved TCP transport.
    /// </summary>
    [TestClass]
    public sealed class RtpHeaderTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        private const int VideoChannel = 0;
        private const int AudioChannel = 2;

        private static uint Ssrc(byte[] rtpPacket) => BinaryPrimitives.ReadUInt32BigEndian(rtpPacket.AsSpan(8));

        private static ushort Seq(byte[] rtpPacket) => BinaryPrimitives.ReadUInt16BigEndian(rtpPacket.AsSpan(2));

        private static List<ReadOnlyMemory<byte>> One(byte[] sample)
        {
            return new List<ReadOnlyMemory<byte>> { new ReadOnlyMemory<byte>(sample) };
        }

        /// <summary>
        /// Drives a session to PLAY with both tracks interleaved on the RTSP connection.
        /// </summary>
        private static RtspTestClient Play(int port)
        {
            string baseUri = "rtsp://127.0.0.1:" + port + "/stream1";
            var client = new RtspTestClient(port, "admin", "password");

            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            var video = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            Assert.AreEqual(200, video.StatusCode);

            var audio = client.Send("SETUP", baseUri + "/trackID=1",
                "Transport: RTP/AVP/TCP;unicast;interleaved=2-3",
                "Session: " + video.Session);
            Assert.AreEqual(200, audio.StatusCode);

            Assert.AreEqual(200, client.Send("PLAY", baseUri, "Session: " + video.Session).StatusCode);
            return client;
        }

        /// <summary>
        /// Reads frames until one arrives on the wanted channel, skipping the RTCP that shares the
        /// connection.
        /// </summary>
        private static byte[] NextOn(RtspTestClient client, int channel)
        {
            for (int i = 0; i < 20; i++)
            {
                var frame = client.ReadInterleaved();
                if (frame.Channel == channel)
                    return frame.Payload;
            }

            Assert.Fail("nothing arrived on channel " + channel);
            return null;
        }

        [TestMethod]
        public void VideoAndAudioAreSentUnderTheirOwnSsrc()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, new InMemoryUserRepository("admin", "password"));

            var videoTrack = new H264Track(Sps, Pps);
            var audioTrack = new AACTrack(new byte[] { 0x12, 0x10 }, 44100, 2);
            server.AddStreamSource(new RTSPStreamSource("stream1", videoTrack, audioTrack));
            server.StartListen();

            using var client = Play(port);

            videoTrack.FeedInRawSamples(9000, One(new byte[] { 0x65, 0x11, 0x22, 0x33 }));
            uint videoSsrc = Ssrc(NextOn(client, VideoChannel));

            audioTrack.FeedInRawSamples(1024, One(new byte[] { 0x21, 0x22, 0x23, 0x24 }));
            uint audioSsrc = Ssrc(NextOn(client, AudioChannel));

            // The SSRC used to live on the connection, so the second SETUP overwrote the first and
            // both streams went out under one SSRC - contradicting the SETUP replies, which had
            // already announced a different one for each track.
            Assert.AreEqual(videoTrack.SSRC, videoSsrc, "video RTP should carry the video track's SSRC");
            Assert.AreEqual(audioTrack.SSRC, audioSsrc, "audio RTP should carry the audio track's SSRC");
            Assert.AreNotEqual(videoSsrc, audioSsrc, "two streams of one session must not share an SSRC");
        }

        [TestMethod]
        public void TheSetupReplyAnnouncesTheSsrcTheRtpCarries()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, new InMemoryUserRepository("admin", "password"));

            var videoTrack = new H264Track(Sps, Pps);
            var audioTrack = new AACTrack(new byte[] { 0x12, 0x10 }, 44100, 2);
            server.AddStreamSource(new RTSPStreamSource("stream1", videoTrack, audioTrack));
            server.StartListen();

            string baseUri = "rtsp://127.0.0.1:" + port + "/stream1";
            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            var video = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            string announced = video.Match("ssrc=([0-9A-Fa-f]+)");

            var audio = client.Send("SETUP", baseUri + "/trackID=1",
                "Transport: RTP/AVP/TCP;unicast;interleaved=2-3",
                "Session: " + video.Session);
            Assert.AreEqual(200, audio.StatusCode);

            Assert.AreEqual(200, client.Send("PLAY", baseUri, "Session: " + video.Session).StatusCode);

            videoTrack.FeedInRawSamples(9000, One(new byte[] { 0x65, 0x11, 0x22, 0x33 }));

            Assert.IsNotNull(announced, "the video SETUP reply should announce an SSRC");
            Assert.AreEqual(
                uint.Parse(announced, NumberStyles.HexNumber),
                Ssrc(NextOn(client, VideoChannel)),
                "the second SETUP must not change what the first stream sends");
        }

        [TestMethod]
        public void AProxyTrackInPassthroughForwardsTheSourceSsrcAndSequenceNumbers()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, new InMemoryUserRepository("admin", "password"));

            var videoTrack = new ProxyTrack(TrackType.Video) { PreserveSourceHeaders = true };
            var audioTrack = new ProxyTrack(TrackType.Audio) { PreserveSourceHeaders = true };
            var streamSource = new RTSPStreamSource("stream1", videoTrack, audioTrack);
            streamSource.OverrideSDP(Sdp, true);
            server.AddStreamSource(streamSource);
            videoTrack.Start();
            audioTrack.Start();
            server.StartListen();

            using var client = Play(port);

            // sequence numbers with a gap in them, as a lossy source would produce
            foreach (ushort seq in new ushort[] { 40000, 40001, 40005 })
            {
                videoTrack.FeedInRawSamples(9000, One(RtpPacket(seq, 0x3C790025)));

                byte[] sent = NextOn(client, VideoChannel);
                Assert.AreEqual(0x3C790025u, Ssrc(sent), "the source SSRC should survive");
                Assert.AreEqual(seq, Seq(sent), "the source sequence number should survive, gap and all");
            }
        }

        [TestMethod]
        public void AProxyTrackWithoutPassthroughIsStillRestamped()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, new InMemoryUserRepository("admin", "password"));

            var videoTrack = new ProxyTrack(TrackType.Video);
            var audioTrack = new ProxyTrack(TrackType.Audio);
            var streamSource = new RTSPStreamSource("stream1", videoTrack, audioTrack);
            streamSource.OverrideSDP(Sdp, true);
            server.AddStreamSource(streamSource);
            videoTrack.Start();
            audioTrack.Start();
            server.StartListen();

            using var client = Play(port);

            videoTrack.FeedInRawSamples(9000, One(RtpPacket(40000, 0x3C790025)));

            byte[] sent = NextOn(client, VideoChannel);
            Assert.AreEqual(videoTrack.SSRC, Ssrc(sent), "the default is still the server's own SSRC");
            Assert.AreNotEqual((ushort)40000, Seq(sent), "the default is still the server's own numbering");
        }

        private static byte[] RtpPacket(ushort seq, uint ssrc)
        {
            var packet = new byte[16];
            packet[0] = 0x80;
            packet[1] = 96;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), seq);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), ssrc);
            return packet;
        }

        private const string Sdp =
            "v=0\r\n" +
            "o=- 0 0 IN IP4 0.0.0.0\r\n" +
            "s=Test\r\n" +
            "c=IN IP4 0.0.0.0\r\n" +
            "m=video 0 RTP/AVP 96\r\n" +
            "a=control:trackID=0\r\n" +
            "a=rtpmap:96 H264/90000\r\n" +
            "m=audio 0 RTP/AVP 97\r\n" +
            "a=control:trackID=1\r\n" +
            "a=rtpmap:97 mpeg4-generic/44100/1\r\n";
    }
}
