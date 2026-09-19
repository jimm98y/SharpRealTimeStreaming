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

using System;
using System.Linq;
using System.Threading;

namespace SharpRTSPClient.Tests
{
    /// <summary>
    /// Drives the client through a full handshake against a canned server, so the SDP is turned into
    /// codec choices by the real code path rather than a stub.
    /// </summary>
    [TestClass]
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
        private static Result Describe(string sdp, Func<TrackOffer, bool> acceptTrack = null)
        {
            var result = new Result();
            using var server = new FakeRtspServer(SdpHeader + sdp);
            using var client = new RTSPClient();

            var settled = new ManualResetEventSlim(false);

            // The first track of each kind, which is what these tests are about - worked out here
            // rather than handed over by the client, which reports every track and says which.
            client.NewTrack += (s, e) =>
            {
                if (e.Kind == TrackKind.Video && result.VideoCodec == null)
                {
                    result.VideoCodec = e.Codec;
                    result.VideoConfiguration = e.StreamConfigurationData;
                }
                else if (e.Kind == TrackKind.Audio && result.AudioCodec == null)
                {
                    result.AudioCodec = e.Codec;
                    result.AudioConfiguration = e.StreamConfigurationData;
                }
            };
            client.Stopped += (s, e) => { result.Stopped = e.Reason; settled.Set(); };
            client.SetupMessageCompleted += (s, e) => settled.Set();

            client.AutoPlay = false;

            if (acceptTrack != null)
            {
                client.AcceptTrack = acceptTrack;
            }

            client.Connect(server.BaseUri, RTPTransport.TCP);

            settled.Wait(5000);
            client.Stop();

            return result;
        }

        [TestMethod]
        [DataRow("m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 H264/90000\r\n", "H264")]
        [DataRow("m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 H265/90000\r\n", "H265")]
        [DataRow("m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 H266/90000\r\n", "H266")]
        [DataRow("m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 AV1/90000\r\n", "AV1")]
        [DataRow("m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 JPEG/90000\r\n", "JPEG")]
        public void DynamicVideoPayloadIsResolvedFromTheRtpMap(string media, string expectedCodec)
        {
            Assert.AreEqual(expectedCodec, Describe(media).VideoCodec);
        }

        [TestMethod]
        public void StaticJpegPayloadTypeIsRecognisedWithoutAnRtpMap()
        {
            // payload type 26 is JPEG by definition, so no rtpmap is needed
            var result = Describe("m=video 0 RTP/AVP 26\r\na=control:trackID=0\r\n");

            Assert.AreEqual("JPEG", result.VideoCodec);
        }

        [TestMethod]
        [DataRow("m=audio 0 RTP/AVP 0\r\na=control:trackID=1\r\n", "PCMU")]
        [DataRow("m=audio 0 RTP/AVP 8\r\na=control:trackID=1\r\n", "PCMA")]
        public void StaticAudioPayloadTypesAreRecognised(string media, string expectedCodec)
        {
            var sdp = "m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 H264/90000\r\n" + media;

            Assert.AreEqual(expectedCodec, Describe(sdp).AudioCodec);
        }

        [TestMethod]
        [DataRow("OPUS/48000/2", "OPUS")]
        [DataRow("PCMA/8000", "PCMA")]
        [DataRow("PCMU/8000", "PCMU")]
        public void DynamicAudioPayloadIsResolvedFromTheRtpMap(string rtpmap, string expectedCodec)
        {
            string sdp =
                "m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 H264/90000\r\n" +
                $"m=audio 0 RTP/AVP 97\r\na=control:trackID=1\r\na=rtpmap:97 {rtpmap}\r\n";

            Assert.AreEqual(expectedCodec, Describe(sdp).AudioCodec);
        }

        [TestMethod]
        public void H264ParameterSetsAreTakenFromTheFmtp()
        {
            // sprop-parameter-sets carries the SPS and PPS, base64 and comma separated
            string sdp =
                "m=video 0 RTP/AVP 96\r\n" +
                "a=control:trackID=0\r\n" +
                "a=rtpmap:96 H264/90000\r\n" +
                "a=fmtp:96 profile-level-id=4D002A; sprop-parameter-sets=Z0IAHpZUBaHogA==,aM48gA==\r\n";

            var configuration = Assert.IsInstanceOfType<H264StreamConfigurationData>(Describe(sdp).VideoConfiguration);

            Assert.IsNotNull(configuration.SPS);
            Assert.IsNotNull(configuration.PPS);
            Assert.AreEqual(0x67, configuration.SPS[0] & 0x7F); // SPS NAL type 7
            Assert.AreEqual(0x68, configuration.PPS[0] & 0x7F); // PPS NAL type 8
        }

        [TestMethod]
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

            Assert.AreEqual("AAC", result.AudioCodec);
            var configuration = Assert.IsInstanceOfType<AACStreamConfigurationData>(result.AudioConfiguration);

            // config=1210 is AudioSpecificConfig: object type 2 (AAC LC), frequency index 4, 2 channels
            Assert.AreEqual(2, configuration.ObjectType);
            Assert.AreEqual(4, configuration.FrequencyIndex);
            Assert.AreEqual(2, configuration.ChannelConfiguration);

            // the payload processor reports the index but leaves the frequency at zero, so the
            // client has to resolve it - callers used to get 0 Hz here
            Assert.AreEqual(44100, configuration.SamplingFrequency);
        }

        [TestMethod]
        [DataRow(0, 96000)]
        [DataRow(3, 48000)]
        [DataRow(4, 44100)]
        [DataRow(8, 16000)]
        [DataRow(12, 7350)]
        [DataRow(13, 0)]   // reserved
        [DataRow(15, 0)]   // frequency written out explicitly, not indexed
        [DataRow(-1, 0)]
        [DataRow(99, 0)]
        public void SamplingFrequencyIsResolvedFromTheIndex(int frequencyIndex, int expected)
        {
            Assert.AreEqual(expected, AACStreamConfigurationData.GetSamplingFrequency(frequencyIndex));
        }

        [TestMethod]
        public void AudioOnlyRequestIgnoresTheVideoTrack()
        {
            string sdp =
                "m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 H264/90000\r\n" +
                "m=audio 0 RTP/AVP 8\r\na=control:trackID=1\r\n";

            var result = Describe(sdp, t => t.Kind == TrackKind.Audio);

            Assert.IsNull(result.VideoCodec);
            Assert.AreEqual("PCMA", result.AudioCodec);
        }

        [TestMethod]
        public void VideoOnlyRequestIgnoresTheAudioTrack()
        {
            string sdp =
                "m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 H264/90000\r\n" +
                "m=audio 0 RTP/AVP 8\r\na=control:trackID=1\r\n";

            var result = Describe(sdp, t => t.Kind == TrackKind.Video);

            Assert.AreEqual("H264", result.VideoCodec);
            Assert.IsNull(result.AudioCodec);
        }

        [TestMethod]
        public void AnSdpWithNoPlayableMediaStopsTheClientInsteadOfThrowing()
        {
            // an unknown dynamic codec with no static fallback
            string sdp = "m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 THEORA/90000\r\n";

            var result = Describe(sdp);

            Assert.AreEqual(StoppedReason.UnsupportedMedia, result.Stopped);
            Assert.IsNull(result.VideoCodec);
        }

        [TestMethod]
        public void TheHandshakeFollowsOptionsDescribeSetup()
        {
            using var server = new FakeRtspServer(SdpHeader +
                "m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 H264/90000\r\n");
            using var client = new RTSPClient { AutoPlay = false };

            var settled = new ManualResetEventSlim(false);
            client.SetupMessageCompleted += (s, e) => settled.Set();

            client.Connect(server.BaseUri, RTPTransport.TCP);
            Assert.IsTrue(settled.Wait(5000), "the client never finished the SETUP handshake");
            client.Stop();

            CollectionAssert.AreEqual(new[] { "OPTIONS", "DESCRIBE", "SETUP" }, server.Requests.Take(3).ToArray());
        }

        [TestMethod]
        public void AutoPlaySendsPlayOnceSetupCompletes()
        {
            using var server = new FakeRtspServer(SdpHeader +
                "m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 H264/90000\r\n");
            using var client = new RTSPClient { AutoPlay = true };

            client.Connect(server.BaseUri, RTPTransport.TCP);

            Assert.IsTrue(server.WaitForRequest("PLAY"), "AutoPlay should have sent a PLAY");
            client.Stop();
        }

        [TestMethod]
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

            Assert.IsTrue(server.WaitForRequest("TEARDOWN"), "Stop should have sent a TEARDOWN");
        }
    }
}
