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
        [DataRow("m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 VP9/90000\r\na=fmtp:96 profile-id=0\r\n", "VP9")]
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
        [DataRow("a=fmtp:96 profile-id=2\r\n", 2)]
        [DataRow("a=fmtp:96 max-fr=30; profile-id=1\r\n", 1)]
        [DataRow("a=fmtp:96 max-fr=30\r\n", 0)]
        [DataRow("", 0)] // no fmtp at all is profile 0 as well
        public void VP9ProfileIsTakenFromTheFmtp(string fmtp, int expectedProfile)
        {
            string sdp =
                "m=video 0 RTP/AVP 96\r\n" +
                "a=control:trackID=0\r\n" +
                "a=rtpmap:96 VP9/90000\r\n" +
                fmtp;

            var configuration = Assert.IsInstanceOfType<VP9StreamConfigurationData>(Describe(sdp).VideoConfiguration);

            Assert.AreEqual(expectedProfile, configuration.ProfileId);
        }

        private static string Video(string codec, string fmtp)
        {
            return "m=video 0 RTP/AVP 96\r\n" +
                "a=control:trackID=0\r\n" +
                $"a=rtpmap:96 {codec}/90000\r\n" +
                (fmtp == null ? "" : $"a=fmtp:96 {fmtp}\r\n");
        }

        [TestMethod]
        [DataRow("H264", "profile-level-id=64001E;packetization-mode=1;x-google-custom=7")]
        [DataRow("H265", "profile-id=1;level-id=120;sprop-max-don-diff=0")]
        [DataRow("H266", "profile-id=1;level-id=51")]
        [DataRow("AV1", "profile=0;level-idx=8;tier=0")]
        [DataRow("VP9", "profile-id=2;max-fr=30")]
        public void TheVideoFmtpIsKeptAsWritten(string codec, string fmtp)
        {
            // what the parsed properties leave out, such as x-google-custom, is still there to read
            Assert.AreEqual(fmtp, Describe(Video(codec, fmtp)).VideoConfiguration.Fmtp);
        }

        [TestMethod]
        [DataRow("H264")]
        [DataRow("H265")]
        [DataRow("H266")]
        [DataRow("AV1")]
        [DataRow("VP9")]
        public void WithoutAnFmtpTheRawFmtpIsNull(string codec)
        {
            Assert.IsNull(Describe(Video(codec, null)).VideoConfiguration.Fmtp);
        }

        [TestMethod]
        [DataRow("opus/48000/2", "sprop-stereo=1;maxplaybackrate=24000")]
        [DataRow("mpeg4-generic/44100/2", "streamtype=5;profile-level-id=1;mode=AAC-hbr;sizelength=13;indexlength=3;indexdeltalength=3;config=1210")]
        public void TheAudioFmtpIsKeptAsWritten(string rtpmap, string fmtp)
        {
            string sdp =
                "m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 H264/90000\r\n" +
                $"m=audio 0 RTP/AVP 97\r\na=control:trackID=1\r\na=rtpmap:97 {rtpmap}\r\na=fmtp:97 {fmtp}\r\n";

            Assert.AreEqual(fmtp, Describe(sdp).AudioConfiguration.Fmtp);
        }

        [TestMethod]
        public void H264ProfileAndPacketizationModeAreTakenFromTheFmtp()
        {
            var configuration = Assert.IsInstanceOfType<H264StreamConfigurationData>(
                Describe(Video("H264", "profile-level-id=64001E; packetization-mode=1")).VideoConfiguration);

            Assert.AreEqual("64001E", configuration.ProfileLevelId);
            Assert.AreEqual(1, configuration.PacketizationMode);
            Assert.IsNull(configuration.SPS);
            Assert.IsNull(configuration.PPS);
        }

        [TestMethod]
        public void H264WithoutAnFmtpStillHasAConfiguration()
        {
            // The parameter sets arrive in the stream; the profile is the SPS's to say.
            var configuration = Assert.IsInstanceOfType<H264StreamConfigurationData>(Describe(Video("H264", null)).VideoConfiguration);

            Assert.IsNull(configuration.ProfileLevelId);
            Assert.AreEqual(0, configuration.PacketizationMode);
            Assert.IsNull(configuration.SPS);
        }

        [TestMethod]
        [DataRow("profile-level-id=64001")]
        [DataRow("profile-level-id=xyz123")]
        [DataRow("packetization-mode=3")]
        public void AMalformedH264FmtpIsReportedUnparsedAndKeepsTheTrack(string fmtp)
        {
            var result = Describe(Video("H264", fmtp));

            Assert.AreEqual("H264", result.VideoCodec);
            var configuration = Assert.IsInstanceOfType<UnparsedStreamConfigurationData>(result.VideoConfiguration);
            Assert.AreEqual(fmtp, configuration.Fmtp);
        }

        [TestMethod]
        public void H265ProfileTierLevelAndDonAreTakenFromTheFmtp()
        {
            var configuration = Assert.IsInstanceOfType<H265StreamConfigurationData>(
                Describe(Video("H265", "profile-id=2; tier-flag=1; level-id=153; sprop-max-don-diff=2")).VideoConfiguration);

            Assert.AreEqual(2, configuration.ProfileId);
            Assert.AreEqual(1, configuration.TierFlag);
            Assert.AreEqual(153, configuration.LevelId);
            Assert.AreEqual(2, configuration.MaxDonDiff);
        }

        [TestMethod]
        public void H265WithoutAnFmtpHasTheRfcDefaults()
        {
            var configuration = Assert.IsInstanceOfType<H265StreamConfigurationData>(Describe(Video("H265", null)).VideoConfiguration);

            Assert.AreEqual(0, configuration.ProfileSpace);
            Assert.AreEqual(1, configuration.ProfileId);
            Assert.AreEqual(0, configuration.TierFlag);
            Assert.AreEqual(93, configuration.LevelId);
            Assert.AreEqual(0, configuration.MaxDonDiff);
            Assert.IsNull(configuration.SPS);
        }

        [TestMethod]
        public void H266ProfileTierAndLevelAreTakenFromTheFmtp()
        {
            var configuration = Assert.IsInstanceOfType<H266StreamConfigurationData>(
                Describe(Video("H266", "profile-id=17; tier-flag=1; level-id=83")).VideoConfiguration);

            Assert.AreEqual(17, configuration.ProfileId);
            Assert.AreEqual(1, configuration.TierFlag);
            Assert.AreEqual(83, configuration.LevelId);
            Assert.AreEqual(0, configuration.MaxDonDiff);
        }

        [TestMethod]
        [DataRow("profile=1; level-idx=8; tier=1", 1, 8, 1)]
        [DataRow(null, 0, 5, 0)] // the defaults of the AV1 payload format
        public void AV1ProfileLevelAndTierAreTakenFromTheFmtp(string fmtp, int profile, int levelIdx, int tier)
        {
            var configuration = Assert.IsInstanceOfType<AV1StreamConfigurationData>(Describe(Video("AV1", fmtp)).VideoConfiguration);

            Assert.AreEqual(profile, configuration.Profile);
            Assert.AreEqual(levelIdx, configuration.LevelIdx);
            Assert.AreEqual(tier, configuration.Tier);
        }

        [TestMethod]
        [DataRow("a=fmtp:97 sprop-stereo=1; useinbandfec=1\r\n", true, true)]
        [DataRow("a=fmtp:97 useinbandfec=0\r\n", false, false)]
        [DataRow("", false, false)]
        public void OpusStereoIsTakenFromTheFmtp(string fmtp, bool spropStereo, bool useInbandFec)
        {
            string sdp =
                "m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 H264/90000\r\n" +
                "m=audio 0 RTP/AVP 97\r\na=control:trackID=1\r\na=rtpmap:97 opus/48000/2\r\n" + fmtp;

            var result = Describe(sdp);

            Assert.AreEqual("OPUS", result.AudioCodec);
            var configuration = Assert.IsInstanceOfType<OpusStreamConfigurationData>(result.AudioConfiguration);
            Assert.AreEqual(spropStereo, configuration.SpropStereo);
            Assert.AreEqual(useInbandFec, configuration.UseInbandFec);
        }

        [TestMethod]
        public void AMalformedOpusFmtpIsReportedUnparsedAndKeepsTheTrack()
        {
            string sdp =
                "m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 H264/90000\r\n" +
                "m=audio 0 RTP/AVP 97\r\na=control:trackID=1\r\na=rtpmap:97 opus/48000/2\r\na=fmtp:97 sprop-stereo=yes\r\n";

            var result = Describe(sdp);

            Assert.AreEqual("OPUS", result.AudioCodec);
            var configuration = Assert.IsInstanceOfType<UnparsedStreamConfigurationData>(result.AudioConfiguration);
            Assert.AreEqual("sprop-stereo=yes", configuration.Fmtp);
        }

        [TestMethod]
        [DataRow("4")]
        [DataRow("-1")]
        [DataRow("two")]
        public void AnUnknownVP9ProfileIsReportedUnparsedAndKeepsTheTrack(string profile)
        {
            string sdp =
                "m=video 0 RTP/AVP 96\r\n" +
                "a=control:trackID=0\r\n" +
                "a=rtpmap:96 VP9/90000\r\n" +
                $"a=fmtp:96 profile-id={profile}\r\n";

            var result = Describe(sdp);

            Assert.AreEqual("VP9", result.VideoCodec);
            var configuration = Assert.IsInstanceOfType<UnparsedStreamConfigurationData>(result.VideoConfiguration);
            Assert.AreEqual($"profile-id={profile}", configuration.Fmtp);
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
