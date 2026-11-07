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
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace SharpRTSPClient.Tests
{
    /// <summary>
    /// One question per track decides what this client sets up.
    /// </summary>
    /// <remarks>
    /// It used to be two settings - a MediaRequest of VIDEO_ONLY, AUDIO_ONLY or VIDEO_AND_AUDIO and
    /// a ReceiveAllTracks flag - which between them could say which kinds and how many of each, and
    /// nothing else.
    /// </remarks>
    [TestClass]
    public class TrackFilterTests
    {
        private const string SdpHeader =
            "v=0\r\n" +
            "o=- 0 0 IN IP4 127.0.0.1\r\n" +
            "s=test\r\n" +
            "c=IN IP4 127.0.0.1\r\n" +
            "t=0 0\r\n";

        private const string H264Fmtp =
            "a=fmtp:%PT% packetization-mode=1;sprop-parameter-sets=Z0IAHpZUBQHtgIgAAAMACAAAAwHgeLFmwA==,aM48gA==\r\n";

        private static string Video(int payloadType, string codec, int control) =>
            $"m=video 0 RTP/AVP {payloadType}\r\n" +
            $"a=rtpmap:{payloadType} {codec}/90000\r\n" +
            (codec == "H264" ? H264Fmtp.Replace("%PT%", payloadType.ToString()) : "") +
            $"a=control:trackID={control}\r\n";

        private static string Audio(int payloadType, int control) =>
            $"m=audio 0 RTP/AVP {payloadType}\r\n" +
            $"a=control:trackID={control}\r\n";

        private static string Metadata(int control) =>
            $"m=application 0 RTP/AVP 107\r\n" +
            $"a=rtpmap:107 vnd.onvif.metadata/90000\r\n" +
            $"a=control:trackID={control}\r\n";

        /// <summary>Two video tracks of different codecs, two audio, and a metadata track.</summary>
        private static readonly string RichSdp =
            Video(96, "H264", 0) + Video(97, "H265", 1) + Audio(0, 2) + Audio(8, 3) + Metadata(4);

        private sealed class Outcome
        {
            public readonly List<NewTrackEventArgs> Tracks = new List<NewTrackEventArgs>();
            public readonly List<TrackOffer> Offers = new List<TrackOffer>();
            public int TrackCount;
        }

        private static Outcome Describe(string sdp, Func<TrackOffer, bool> filter)
        {
            var outcome = new Outcome();

            using var server = new FakeRtspServer(SdpHeader + sdp);
            using var client = new RTSPClient { AutoPlay = false };

            if (filter != null)
            {
                client.AcceptTrack = offer =>
                {
                    lock (outcome.Offers) { outcome.Offers.Add(offer); }
                    return filter(offer);
                };
            }

            var settled = new ManualResetEventSlim(false);
            client.NewTrack += (s, e) => { lock (outcome.Tracks) { outcome.Tracks.Add(e); } };
            client.Stopped += (s, e) => settled.Set();
            client.SetupMessageCompleted += (s, e) => settled.Set();

            client.Connect(server.BaseUri, RTPTransport.TCP);
            settled.Wait(5000);

            outcome.TrackCount = client.TrackCount;
            client.Stop();
            return outcome;
        }

        [TestMethod]
        public void WithNoFilterItIsTheFirstOfEachKind()
        {
            var outcome = Describe(RichSdp, null);

            Assert.AreEqual(3, outcome.TrackCount, "one video, one audio, one metadata");

            CollectionAssert.AreEqual(
                new[] { TrackKind.Video, TrackKind.Audio, TrackKind.Application },
                outcome.Tracks.Select(t => t.Kind).ToArray());

            Assert.AreEqual("H264", outcome.Tracks[0].Codec, "the first video track, not the second");
        }

        [TestMethod]
        public void EverythingOnOfferCanBeTaken()
        {
            var outcome = Describe(RichSdp, _ => true);

            Assert.AreEqual(5, outcome.TrackCount, "two video, two audio, one metadata");
        }

        [TestMethod]
        public void NothingCanBeTaken()
        {
            var outcome = Describe(RichSdp, _ => false);

            Assert.AreEqual(0, outcome.TrackCount);
            Assert.IsEmpty(outcome.Tracks);
        }

        [TestMethod]
        public void AKindCanBeAskedForOnItsOwn()
        {
            var outcome = Describe(RichSdp, t => t.Kind == TrackKind.Audio);

            Assert.AreEqual(2, outcome.TrackCount);
            Assert.IsTrue(outcome.Tracks.All(t => t.Kind == TrackKind.Audio));
        }

        [TestMethod]
        public void OneCodecCanBeSingledOutFromTwoOfTheSameKind()
        {
            // The thing neither of the old settings could say: two video tracks, and this one.
            var outcome = Describe(RichSdp, t => t.Codec == "H265");

            Assert.AreEqual(1, outcome.TrackCount);
            Assert.AreEqual("H265", outcome.Tracks[0].Codec);
        }

        [TestMethod]
        public void TheMetadataTrackCanBeTakenWithoutTakingEveryOtherExtra()
        {
            // It used to need ReceiveAllTracks, which also pulled in the second video and the
            // second audio track.
            var outcome = Describe(RichSdp, t => t.Kind == TrackKind.Application);

            Assert.AreEqual(1, outcome.TrackCount);
            Assert.AreEqual(TrackKind.Application, outcome.Tracks[0].Kind);
        }

        [TestMethod]
        public void TheOfferSaysWhereTheTrackCameInTheDescription()
        {
            var outcome = Describe(RichSdp, _ => true);

            // Offered a kind at a time, so the description order is not the offer order - which is
            // exactly why the offer carries it.
            TrackOffer metadata = outcome.Offers.Single(o => o.Kind == TrackKind.Application);
            Assert.AreEqual(4, metadata.DescriptionIndex, "the metadata section is the fifth");

            TrackOffer h265 = outcome.Offers.Single(o => o.Codec == "H265");
            Assert.AreEqual(1, h265.DescriptionIndex);
        }

        [TestMethod]
        public void TheOfferCountsWhatHasAlreadyBeenAccepted()
        {
            var outcome = Describe(RichSdp, _ => true);

            // AcceptedSoFar is the index the track will have, so it runs 0..4 as each is taken.
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4 },
                outcome.Offers.Select(o => o.AcceptedSoFar).ToArray());

            // and AcceptedOfThisKind restarts per kind: video 0,1  audio 0,1  application 0
            CollectionAssert.AreEqual(new[] { 0, 1, 0, 1, 0 },
                outcome.Offers.Select(o => o.AcceptedOfThisKind).ToArray());
        }

        [TestMethod]
        public void RefusingATrackDoesNotConsumeAnIndex()
        {
            var outcome = Describe(RichSdp, t => t.Codec != "H264");

            // The H264 track was offered first and refused, so the H265 one still becomes track 0.
            Assert.AreEqual("H265", outcome.Tracks[0].Codec);
            Assert.AreEqual(0, outcome.Tracks[0].TrackIndex);

            Assert.AreEqual(4, outcome.TrackCount);
        }

        [TestMethod]
        public void ATrackThisClientCannotPlayIsNeverOffered()
        {
            string unplayable =
                Video(96, "SOMETHINGELSE", 0) + Video(97, "H264", 1);

            var outcome = Describe(unplayable, _ => true);

            Assert.AreEqual(1, outcome.TrackCount, "only the H264 track can be played");
            Assert.AreEqual(1, outcome.Offers.Count, "there is nothing to decide about a codec we cannot read");
            Assert.AreEqual("H264", outcome.Offers[0].Codec);
        }

        [TestMethod]
        public void AFilterThatThrowsPassesTheTrackOverRatherThanTakingIt()
        {
            var outcome = Describe(RichSdp, t => throw new InvalidOperationException("no idea"));

            Assert.AreEqual(0, outcome.TrackCount,
                "a filter that cannot decide must not be a way to pull a track in by accident");
        }

        [TestMethod]
        public void TheHardCapStillBoundsWhatAFilterCanTake()
        {
            // MaxTracks is a bound on what a description can make this client spend, not a
            // preference, so a filter saying yes to everything does not get past it.
            var outcome = new Outcome();

            using var server = new FakeRtspServer(SdpHeader + RichSdp);
            using var client = new RTSPClient { AutoPlay = false, MaxTracks = 2 };
            client.AcceptTrack = _ => true;

            var settled = new ManualResetEventSlim(false);
            client.Stopped += (s, e) => settled.Set();
            client.SetupMessageCompleted += (s, e) => settled.Set();

            client.Connect(server.BaseUri, RTPTransport.TCP);
            settled.Wait(5000);

            Assert.IsLessThanOrEqualTo(2, client.TrackCount);
            client.Stop();
        }

        [TestMethod]
        public void NeitherOldSettingIsPartOfThePublicApiAnyMore()
        {
            Type client = typeof(RTSPClient);

            Assert.IsNull(client.GetProperty("ReceiveAllTracks"));
            Assert.IsNull(typeof(RTSPClient).Assembly.GetType("SharpRTSPClient.MediaRequest"),
                "MediaRequest should be gone, not merely unused");
        }
    }
}
