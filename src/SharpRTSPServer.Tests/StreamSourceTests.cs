using System;
using System.Linq;
using SharpRTSPServer;

namespace SharpRTSPServer.Tests
{
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

        [Fact]
        public void StreamIdIsRequired()
        {
            Assert.Throws<ArgumentNullException>(() => new RTSPStreamSource(null, null, null));
            Assert.Throws<ArgumentNullException>(() => new RTSPStreamSource("", null, null));
            Assert.Throws<ArgumentNullException>(() => new RTSPStreamSource("   ", null, null));
        }

        [Fact]
        public void OverrideSDP_AddsAControlAttributeAfterEachMediaSection()
        {
            var source = new RTSPStreamSource("stream1", null, null);

            source.OverrideSDP(VideoAndAudioSdp);

            var lines = source.Sdp.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

            // each m= line must be followed immediately by its control attribute, numbered in order
            int video = lines.IndexOf("m=video 0 RTP/AVP 96");
            int audio = lines.IndexOf("m=audio 0 RTP/AVP 97");
            Assert.Equal("a=control:trackID=0", lines[video + 1]);
            Assert.Equal("a=control:trackID=1", lines[audio + 1]);
        }

        [Fact]
        public void OverrideSDP_LeavesAnSdpThatAlreadyHasControlAttributesAlone()
        {
            var source = new RTSPStreamSource("stream1", null, null);
            string sdp = VideoOnlySdp + "a=control:trackID=7\n";

            source.OverrideSDP(sdp);

            Assert.Equal(sdp, source.Sdp);
            Assert.DoesNotContain("trackID=0", source.Sdp);
        }

        [Fact]
        public void OverrideSDP_CanBeAskedNotToTouchTheSdp()
        {
            var source = new RTSPStreamSource("stream1", null, null);

            source.OverrideSDP(VideoOnlySdp, mungleSDP: false);

            Assert.Equal(VideoOnlySdp, source.Sdp);
        }

        [Fact]
        public void DisposingTheSourceDisposesItsTracks()
        {
            var video = new ProxyTrack(TrackType.Video);
            var audio = new ProxyTrack(TrackType.Audio);
            video.Start();
            audio.Start();

            var source = new RTSPStreamSource("stream1", video, audio);
            source.Dispose();

            Assert.False(video.IsReady);
            Assert.False(audio.IsReady);
        }
    }
}
