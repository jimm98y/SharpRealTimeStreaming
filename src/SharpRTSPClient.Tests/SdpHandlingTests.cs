using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace SharpRTSPClient.Tests
{
    /// <summary>
    /// Drives the client through a full handshake against a canned server, so the SDP is turned into
    /// codec choices by the real code path rather than a stub.
    /// </summary>
    public class SdpHandlingTests
    {
        private const string SdpHeader =
            "v=0\r\n" +
            "o=- 0 0 IN IP4 127.0.0.1\r\n" +
            "s=test\r\n" +
            "c=IN IP4 127.0.0.1\r\n" +
            "t=0 0\r\n";

        private sealed class Result
        {
            public string VideoCodec;
            public string AudioCodec;
            public IStreamConfigurationData VideoConfiguration;
            public IStreamConfigurationData AudioConfiguration;
            public StoppedReason? Stopped;
        }

        /// <summary>
        /// Connects a client to a server serving the given SDP and reports what it made of it.
        /// </summary>
        private static Result Describe(string sdp, MediaRequest mediaRequest = MediaRequest.VIDEO_AND_AUDIO)
        {
            var result = new Result();
            using var server = new FakeRtspServer(SdpHeader + sdp);
            using var client = new RTSPClient();

            var settled = new ManualResetEventSlim(false);

            client.NewVideoStream += (s, e) =>
            {
                result.VideoCodec = e.StreamType;
                result.VideoConfiguration = e.StreamConfigurationData;
            };
            client.NewAudioStream += (s, e) =>
            {
                result.AudioCodec = e.StreamType;
                result.AudioConfiguration = e.StreamConfigurationData;
            };
            client.Stopped += (s, e) => { result.Stopped = e.Reason; settled.Set(); };
            client.SetupMessageCompleted += (s, e) => settled.Set();

            client.AutoPlay = false;
            client.Connect(server.BaseUri, RTPTransport.TCP, mediaRequest: mediaRequest);

            settled.Wait(5000);
            client.Stop();

            return result;
        }

        [Theory]
        [InlineData("m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 H264/90000\r\n", "H264")]
        [InlineData("m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 H265/90000\r\n", "H265")]
        [InlineData("m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 H266/90000\r\n", "H266")]
        [InlineData("m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 AV1/90000\r\n", "AV1")]
        [InlineData("m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 JPEG/90000\r\n", "JPEG")]
        public void DynamicVideoPayloadIsResolvedFromTheRtpMap(string media, string expectedCodec)
        {
            Assert.Equal(expectedCodec, Describe(media).VideoCodec);
        }

        [Fact]
        public void StaticJpegPayloadTypeIsRecognisedWithoutAnRtpMap()
        {
            // payload type 26 is JPEG by definition, so no rtpmap is needed
            var result = Describe("m=video 0 RTP/AVP 26\r\na=control:trackID=0\r\n");

            Assert.Equal("JPEG", result.VideoCodec);
        }

        [Theory]
        [InlineData("m=audio 0 RTP/AVP 0\r\na=control:trackID=1\r\n", "PCMU")]
        [InlineData("m=audio 0 RTP/AVP 8\r\na=control:trackID=1\r\n", "PCMA")]
        public void StaticAudioPayloadTypesAreRecognised(string media, string expectedCodec)
        {
            var sdp = "m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 H264/90000\r\n" + media;

            Assert.Equal(expectedCodec, Describe(sdp).AudioCodec);
        }

        [Theory]
        [InlineData("OPUS/48000/2", "OPUS")]
        [InlineData("PCMA/8000", "PCMA")]
        [InlineData("PCMU/8000", "PCMU")]
        public void DynamicAudioPayloadIsResolvedFromTheRtpMap(string rtpmap, string expectedCodec)
        {
            string sdp =
                "m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 H264/90000\r\n" +
                $"m=audio 0 RTP/AVP 97\r\na=control:trackID=1\r\na=rtpmap:97 {rtpmap}\r\n";

            Assert.Equal(expectedCodec, Describe(sdp).AudioCodec);
        }

        [Fact]
        public void H264ParameterSetsAreTakenFromTheFmtp()
        {
            // sprop-parameter-sets carries the SPS and PPS, base64 and comma separated
            string sdp =
                "m=video 0 RTP/AVP 96\r\n" +
                "a=control:trackID=0\r\n" +
                "a=rtpmap:96 H264/90000\r\n" +
                "a=fmtp:96 profile-level-id=4D002A; sprop-parameter-sets=Z0IAHpZUBaHogA==,aM48gA==\r\n";

            var configuration = Assert.IsType<H264StreamConfigurationData>(Describe(sdp).VideoConfiguration);

            Assert.NotNull(configuration.SPS);
            Assert.NotNull(configuration.PPS);
            Assert.Equal(0x67, configuration.SPS[0] & 0x7F); // SPS NAL type 7
            Assert.Equal(0x68, configuration.PPS[0] & 0x7F); // PPS NAL type 8
        }

        [Fact]
        public void AacConfigurationIsDecodedFromTheFmtp()
        {
            string sdp =
                "m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 H264/90000\r\n" +
                "m=audio 0 RTP/AVP 97\r\n" +
                "a=control:trackID=1\r\n" +
                "a=rtpmap:97 mpeg4-generic/44100/2\r\n" +
                "a=fmtp:97 streamtype=5; profile-level-id=1; mode=AAC-hbr; sizelength=13; " +
                "indexlength=3; indexdeltalength=3; config=1210\r\n";

            var result = Describe(sdp);

            Assert.Equal("AAC", result.AudioCodec);
            var configuration = Assert.IsType<AACStreamConfigurationData>(result.AudioConfiguration);

            // config=1210 is AudioSpecificConfig: object type 2 (AAC LC), frequency index 4, 2 channels
            Assert.Equal(2, configuration.ObjectType);
            Assert.Equal(4, configuration.FrequencyIndex);
            Assert.Equal(2, configuration.ChannelConfiguration);

            // the payload processor reports the index but leaves the frequency at zero, so the
            // client has to resolve it - callers used to get 0 Hz here
            Assert.Equal(44100, configuration.SamplingFrequency);
        }

        [Theory]
        [InlineData(0, 96000)]
        [InlineData(3, 48000)]
        [InlineData(4, 44100)]
        [InlineData(8, 16000)]
        [InlineData(12, 7350)]
        [InlineData(13, 0)]   // reserved
        [InlineData(15, 0)]   // frequency written out explicitly, not indexed
        [InlineData(-1, 0)]
        [InlineData(99, 0)]
        public void SamplingFrequencyIsResolvedFromTheIndex(int frequencyIndex, int expected)
        {
            Assert.Equal(expected, AACStreamConfigurationData.GetSamplingFrequency(frequencyIndex));
        }

        [Fact]
        public void AudioOnlyRequestIgnoresTheVideoTrack()
        {
            string sdp =
                "m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 H264/90000\r\n" +
                "m=audio 0 RTP/AVP 8\r\na=control:trackID=1\r\n";

            var result = Describe(sdp, MediaRequest.AUDIO_ONLY);

            Assert.Null(result.VideoCodec);
            Assert.Equal("PCMA", result.AudioCodec);
        }

        [Fact]
        public void VideoOnlyRequestIgnoresTheAudioTrack()
        {
            string sdp =
                "m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 H264/90000\r\n" +
                "m=audio 0 RTP/AVP 8\r\na=control:trackID=1\r\n";

            var result = Describe(sdp, MediaRequest.VIDEO_ONLY);

            Assert.Equal("H264", result.VideoCodec);
            Assert.Null(result.AudioCodec);
        }

        [Fact]
        public void AnSdpWithNoPlayableMediaStopsTheClientInsteadOfThrowing()
        {
            // an unknown dynamic codec with no static fallback
            string sdp = "m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 THEORA/90000\r\n";

            var result = Describe(sdp);

            Assert.Equal(StoppedReason.UnsupportedMedia, result.Stopped);
            Assert.Null(result.VideoCodec);
        }

        [Fact]
        public void TheHandshakeFollowsOptionsDescribeSetup()
        {
            using var server = new FakeRtspServer(SdpHeader +
                "m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 H264/90000\r\n");
            using var client = new RTSPClient { AutoPlay = false };

            var settled = new ManualResetEventSlim(false);
            client.SetupMessageCompleted += (s, e) => settled.Set();

            client.Connect(server.BaseUri, RTPTransport.TCP);
            Assert.True(settled.Wait(5000), "the client never finished the SETUP handshake");
            client.Stop();

            Assert.Equal(new[] { "OPTIONS", "DESCRIBE", "SETUP" }, server.Requests.Take(3));
        }

        [Fact]
        public void AutoPlaySendsPlayOnceSetupCompletes()
        {
            using var server = new FakeRtspServer(SdpHeader +
                "m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 H264/90000\r\n");
            using var client = new RTSPClient { AutoPlay = true };

            client.Connect(server.BaseUri, RTPTransport.TCP);

            Assert.True(server.WaitForRequest("PLAY"), "AutoPlay should have sent a PLAY");
            client.Stop();
        }

        [Fact]
        public void StopSendsTeardown()
        {
            using var server = new FakeRtspServer(SdpHeader +
                "m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 H264/90000\r\n");
            using var client = new RTSPClient { AutoPlay = false };

            var settled = new ManualResetEventSlim(false);
            client.SetupMessageCompleted += (s, e) => settled.Set();

            client.Connect(server.BaseUri, RTPTransport.TCP);
            settled.Wait(5000);
            client.Stop();

            Assert.True(server.WaitForRequest("TEARDOWN"), "Stop should have sent a TEARDOWN");
        }
    }
}
