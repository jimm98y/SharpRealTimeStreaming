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
using SharpSRTP.SRTP;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// An overridden SDP brings its own "a=control" attributes, in its own order. Everything that
    /// addresses a track has to go by those rather than assume the generated shape.
    /// </summary>
    [TestClass]
    public sealed class SdpControlAttributeTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        /// <summary>Audio first, and control attributes a camera might really use.</summary>
        private const string AudioFirstSdp =
            "v=0\r\no=- 0 0 IN IP4 0.0.0.0\r\ns=Test\r\nc=IN IP4 0.0.0.0\r\n" +
            "m=audio 0 RTP/AVP 97\r\na=control:sound\r\na=rtpmap:97 mpeg4-generic/44100/1\r\n" +
            "m=video 0 RTP/AVP 96\r\na=control:picture\r\na=rtpmap:96 H264/90000\r\n";

        private const string AudioFirstSavpSdp =
            "v=0\r\no=- 0 0 IN IP4 0.0.0.0\r\ns=Test\r\nc=IN IP4 0.0.0.0\r\n" +
            "m=audio 0 RTP/SAVP 97\r\na=control:sound\r\na=rtpmap:97 mpeg4-generic/44100/1\r\n" +
            "m=video 0 RTP/SAVP 96\r\na=control:picture\r\na=rtpmap:96 H264/90000\r\n";

        /// <summary>
        /// The video track these tests add, which is the first one they add.
        /// </summary>
        /// <remarks>
        /// By position, not by kind. RTSPStreamSource used to answer "the video track" itself, which
        /// is a question with no answer for a stream carrying two of them - the test knows which one
        /// it put there.
        /// </remarks>
        private static ProxyTrack VideoOf(RTSPStreamSource source) => (ProxyTrack)source.Tracks[0];

        /// <summary>The audio track these tests add, which is the second one they add.</summary>
        private static ProxyTrack AudioOf(RTSPStreamSource source) => (ProxyTrack)source.Tracks[1];

        private static RTSPStreamSource NewSource(string sdp)
        {
            var source = new RTSPStreamSource("stream1",
                new ProxyTrack(TrackType.Video),
                new ProxyTrack(TrackType.Audio));
            source.OverrideSDP(sdp, true);
            return source;
        }

        [TestMethod]
        public void TheControlAttributeIsReadFromTheSdpByMediaType()
        {
            var source = NewSource(AudioFirstSdp);

            // by position the video section is second - reading it as the first one is what put each
            // track's keys and URLs on the other one
            Assert.AreEqual("picture", source.GetTrackControl(VideoOf(source)));
            Assert.AreEqual("sound", source.GetTrackControl(AudioOf(source)));
        }

        [TestMethod]
        public void AGeneratedSdpStillUsesTrackIds()
        {
            var source = new RTSPStreamSource("stream1", new H264Track(Sps, Pps), new AACTrack(new byte[] { 0x12, 0x10 }, 44100, 2));

            // Not the ProxyTracks the other tests use, so the tracks are taken as they were added.
            Assert.AreEqual("trackID=0", source.GetTrackControl(source.Tracks[0]));
            Assert.AreEqual("trackID=1", source.GetTrackControl(source.Tracks[1]));
        }

        [TestMethod]
        public void SetupFindsATrackByItsOwnControlAttribute()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");

            var source = NewSource(AudioFirstSdp);
            var videoTrack = VideoOf(source);
            var audioTrack = AudioOf(source);
            server.AddStreamSource(source);
            videoTrack.Start();
            audioTrack.Start();
            server.StartListen();

            string baseUri = "rtsp://127.0.0.1:" + port + "/stream1";
            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            // The client addresses the track the way the SDP told it to. This used to be a 404,
            // because the server only ever looked for "trackID=N".
            var video = client.Send("SETUP", baseUri + "/picture",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            Assert.AreEqual(200, video.StatusCode, "SETUP of the video track by its own control attribute");

            var audio = client.Send("SETUP", baseUri + "/sound",
                "Transport: RTP/AVP/TCP;unicast;interleaved=2-3", "Session: " + video.Session);
            Assert.AreEqual(200, audio.StatusCode, "SETUP of the audio track by its own control attribute");
        }

        [TestMethod]
        public void RtpInfoNamesTheControlUrlTheSdpAdvertised()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");

            var source = NewSource(AudioFirstSdp);
            VideoOf(source).Start();
            AudioOf(source).Start();
            server.AddStreamSource(source);
            server.StartListen();

            string baseUri = "rtsp://127.0.0.1:" + port + "/stream1";
            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            var video = client.Send("SETUP", baseUri + "/picture",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            Assert.AreEqual(200, video.StatusCode);

            var play = client.Send("PLAY", baseUri, "Session: " + video.Session);
            string rtpInfo = play.Match(@"RTP-Info:\s*(.+)");

            Assert.IsNotNull(rtpInfo);
            Assert.Contains("/picture", rtpInfo, "RTP-Info has to name the URL the client actually used");
            Assert.DoesNotContain("trackID=", rtpInfo, "this SDP never mentioned trackID");
        }

        [TestMethod]
        public void CryptoGoesIntoTheSectionThatBelongsToTheTrack()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password", false, null,
                SrtpCryptoSuites.AES_CM_128_HMAC_SHA1_80, null);

            var source = new RTSPStreamSource("stream1",
                new ProxyTrack(TrackType.Video) { RtpProfile = RtpProfiles.SAVP },
                new ProxyTrack(TrackType.Audio) { RtpProfile = RtpProfiles.SAVP });
            source.OverrideSDP(AudioFirstSavpSdp, true);
            VideoOf(source).Start();
            AudioOf(source).Start();
            server.AddStreamSource(source);
            server.StartListen();

            string baseUri = "rtsp://127.0.0.1:" + port + "/stream1";
            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);

            var describe = client.Send("DESCRIBE", baseUri, "Accept: application/sdp");
            Assert.AreEqual(200, describe.StatusCode);

            // both sections gain keys, and each has to be the key of the track that section describes
            var sections = SplitSections(describe.Body);
            Assert.HasCount(2, sections);

            string audioCrypto = CryptoOf(sections[0]);
            string videoCrypto = CryptoOf(sections[1]);

            Assert.IsNotNull(audioCrypto, "the audio section should have gained an a=crypto");
            Assert.IsNotNull(videoCrypto, "the video section should have gained an a=crypto");
            Assert.AreNotEqual(audioCrypto, videoCrypto, "the two streams must not share a key");

            // and the video key is the one the video stream will actually protect with, which is only
            // checkable by setting the video track up and seeing the media come back protected
            var setup = client.Send("SETUP", baseUri + "/picture",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            Assert.AreEqual(200, setup.StatusCode);
            Assert.AreEqual(200, client.Send("PLAY", baseUri, "Session: " + setup.Session).StatusCode);

            VideoOf(source).FeedInRawSamples(9000, new List<ReadOnlyMemory<byte>>
            {
                new ReadOnlyMemory<byte>(new byte[16])
            });

            for (int i = 0; i < 20; i++)
            {
                var frame = client.ReadInterleaved();
                if (frame.Channel == 0)
                {
                    Assert.HasCount(26, frame.Payload, "the video RTP should be protected, so ten bytes longer");
                    return;
                }
            }

            Assert.Fail("no video RTP arrived");
        }

        private static List<string> SplitSections(string sdp)
        {
            var sections = new List<string>();
            foreach (string line in (sdp ?? string.Empty).Split('\n'))
            {
                if (line.StartsWith("m="))
                {
                    sections.Add(string.Empty);
                }

                if (sections.Count > 0)
                {
                    sections[sections.Count - 1] += line + "\n";
                }
            }
            return sections;
        }

        private static string CryptoOf(string section)
        {
            foreach (string line in section.Split('\n'))
            {
                if (line.StartsWith("a=crypto:"))
                    return line.Trim();
            }
            return null;
        }
    }
}
