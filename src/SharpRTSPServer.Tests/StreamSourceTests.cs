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
using System.Linq;

namespace SharpRTSPServer.Tests
{
    [TestClass]
    public class StreamSourceTests
    {
        private const string VideoOnlySdp =
            "v=0\n" +
            "o=- 0 0 IN IP4 127.0.0.1\n" +
            "s=test\n" +
            "m=video 0 RTP/AVP 96\n" +
            "a=rtpmap:96 H264/90000\n";

        private const string VideoAndAudioSdp =
            "v=0\n" +
            "o=- 0 0 IN IP4 127.0.0.1\n" +
            "s=test\n" +
            "m=video 0 RTP/AVP 96\n" +
            "a=rtpmap:96 H264/90000\n" +
            "m=audio 0 RTP/AVP 97\n" +
            "a=rtpmap:97 mpeg4-generic/44100/2\n";

        [TestMethod]
        public void StreamIdIsRequired()
        {
            Assert.ThrowsExactly<ArgumentNullException>(() => new RTSPStreamSource(null, null, null));
            Assert.ThrowsExactly<ArgumentNullException>(() => new RTSPStreamSource("", null, null));
            Assert.ThrowsExactly<ArgumentNullException>(() => new RTSPStreamSource("   ", null, null));
        }

        [TestMethod]
        public void OverrideSDP_AddsAControlAttributeAfterEachMediaSection()
        {
            var source = new RTSPStreamSource("stream1", null, null);

            source.OverrideSDP(VideoAndAudioSdp);

            var lines = source.Sdp.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

            // each m= line must be followed immediately by its control attribute, numbered in order
            int video = lines.IndexOf("m=video 0 RTP/AVP 96");
            int audio = lines.IndexOf("m=audio 0 RTP/AVP 97");
            Assert.AreEqual("a=control:trackID=0", lines[video + 1]);
            Assert.AreEqual("a=control:trackID=1", lines[audio + 1]);
        }

        [TestMethod]
        public void OverrideSDP_LeavesAnSdpThatAlreadyHasControlAttributesAlone()
        {
            var source = new RTSPStreamSource("stream1", null, null);
            string sdp = VideoOnlySdp + "a=control:trackID=7\n";

            source.OverrideSDP(sdp);

            Assert.AreEqual(sdp, source.Sdp);
            Assert.DoesNotContain("trackID=0", source.Sdp, "the existing control attribute must be left alone");
        }

        [TestMethod]
        public void OverrideSDP_IgnoresASessionLevelControlAttribute()
        {
            var source = new RTSPStreamSource("stream1", null, null);

            // a session level a=control says nothing about the individual tracks; taking it as proof
            // that the SDP was already munged left every media section without a trackID
            string sdp =
                "v=0\n" +
                "o=- 0 0 IN IP4 127.0.0.1\n" +
                "s=test\n" +
                "a=control:*\n" +
                "m=video 0 RTP/AVP 96\n" +
                "a=rtpmap:96 H264/90000\n";

            source.OverrideSDP(sdp);

            StringAssert.Contains(source.Sdp, "a=control:trackID=0");
        }

        [TestMethod]
        public void OverrideSDP_FillsInOnlyTheMediaSectionsThatAreMissingOne()
        {
            var source = new RTSPStreamSource("stream1", null, null);

            string sdp =
                "v=0\n" +
                "m=video 0 RTP/AVP 96\n" +
                "a=control:trackID=7\n" +
                "m=audio 0 RTP/AVP 97\n";

            source.OverrideSDP(sdp);

            var lines = source.Sdp.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

            // the video section keeps the control attribute it came with
            StringAssert.Contains(source.Sdp, "a=control:trackID=7");
            Assert.DoesNotContain("a=control:trackID=0", source.Sdp);

            // and the audio section gains one, numbered by its position
            int audio = lines.IndexOf("m=audio 0 RTP/AVP 97");
            Assert.AreEqual("a=control:trackID=1", lines[audio + 1]);
        }

        [TestMethod]
        public void OverrideSDP_RejectsNull()
        {
            var source = new RTSPStreamSource("stream1", null, null);

            Assert.ThrowsExactly<ArgumentNullException>(() => source.OverrideSDP(null));
        }

        [TestMethod]
        public void OverrideSDP_CanBeAskedNotToTouchTheSdp()
        {
            var source = new RTSPStreamSource("stream1", null, null);

            source.OverrideSDP(VideoOnlySdp, mungleSDP: false);

            Assert.AreEqual(VideoOnlySdp, source.Sdp);
        }

        [TestMethod]
        public void DisposingTheSourceDisposesItsTracks()
        {
            var video = new ProxyTrack(TrackType.Video);
            var audio = new ProxyTrack(TrackType.Audio);
            video.Start();
            audio.Start();

            var source = new RTSPStreamSource("stream1", video, audio);
            source.Dispose();

            Assert.IsFalse(video.IsReady);
            Assert.IsFalse(audio.IsReady);
        }
    }
}
