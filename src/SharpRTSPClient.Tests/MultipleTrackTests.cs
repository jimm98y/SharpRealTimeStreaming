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
    /// Streams that offer more than one video track and one audio track.
    /// </summary>
    /// <remarks>
    /// The client used to hold two of everything as separate fields - a video transport and an audio
    /// one, a video payload type and an audio one - and take the first track of each kind. A stream
    /// offering a second language, or the data describing what is in the picture, had nowhere to put
    /// it. What identifies a track now is its place in the description; its kind only says what sort
    /// of thing is in it.
    /// </remarks>
    [TestClass]
    public class MultipleTrackTests
    {
        private const string SdpHeader =
            "v=0\r\n" +
            "o=- 0 0 IN IP4 127.0.0.1\r\n" +
            "s=test\r\n" +
            "c=IN IP4 127.0.0.1\r\n" +
            "t=0 0\r\n";

        private sealed class Seen
        {
            public readonly List<NewTrackEventArgs> Tracks = new List<NewTrackEventArgs>();
            public string VideoCodec;
            public string AudioCodec;
            public StoppedReason? Stopped;
        }

        private static Seen Describe(string sdp, bool receiveAllTracks)
        {
            return Describe(sdp, receiveAllTracks ? (_ => true) : RTSPClient.FirstOfEachKind);
        }

        private static Seen Describe(string sdp, Func<TrackOffer, bool> acceptTrack)
        {
            var seen = new Seen();
            using var server = new FakeRtspServer(SdpHeader + sdp);
            using var client = new RTSPClient { AutoPlay = false, AcceptTrack = acceptTrack };

            var settled = new ManualResetEventSlim(false);

            client.NewTrack += (s, e) => { lock (seen.Tracks) { seen.Tracks.Add(e); } };
            // The first track of each kind, which is what these assertions are about. The client
            // no longer works that out for anyone: it reports every track and says which, and the
            // "first of a kind" is a question only the caller knows whether it wants answered.
            client.NewTrack += (s, e) =>
            {
                if (e.Kind == TrackKind.Video && seen.VideoCodec == null) seen.VideoCodec = e.Codec;
                if (e.Kind == TrackKind.Audio && seen.AudioCodec == null) seen.AudioCodec = e.Codec;
            };
            client.Stopped += (s, e) => { seen.Stopped = e.Reason; settled.Set(); };
            client.SetupMessageCompleted += (s, e) => settled.Set();

            client.Connect(server.BaseUri, RTPTransport.TCP);

            settled.Wait(5000);
            client.Stop();

            return seen;
        }

        private const string TwoAudioTracks =
            "m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 H264/90000\r\n" +
            "m=audio 0 RTP/AVP 0\r\na=control:trackID=1\r\n" +
            "m=audio 0 RTP/AVP 8\r\na=control:trackID=2\r\n";

        private const string WithMetadata =
            "m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 H264/90000\r\n" +
            "m=application 0 RTP/AVP 107\r\na=control:trackID=2\r\na=rtpmap:107 vnd.onvif.metadata/90000\r\n";

        [TestMethod]
        public void OnlyTheFirstOfEachKindIsTakenByDefault()
        {
            var seen = Describe(TwoAudioTracks, receiveAllTracks: false);

            // What this client has always done, and what anything written against it expects. A
            // second track set up is a second track being sent.
            Assert.AreEqual("H264", seen.VideoCodec);
            Assert.AreEqual("PCMU", seen.AudioCodec, "the first audio track, not the second");

            Assert.HasCount(2, seen.Tracks, "one video and one audio: " +
                string.Join(", ", seen.Tracks.Select(t => $"{t.Kind}/{t.Codec}")));
        }

        [TestMethod]
        public void EveryTrackIsTakenWhenAskedFor()
        {
            var seen = Describe(TwoAudioTracks, receiveAllTracks: true);

            Assert.HasCount(3, seen.Tracks, "both audio tracks should be offered: " +
                string.Join(", ", seen.Tracks.Select(t => $"{t.Kind}/{t.Codec}")));

            var audio = seen.Tracks.Where(t => t.Kind == TrackKind.Audio).ToArray();

            Assert.HasCount(2, audio);
            Assert.AreEqual("PCMU", audio[0].Codec);
            Assert.AreEqual("PCMA", audio[1].Codec, "the second audio track should be reported too");

            // and they are told apart by where they came in the description
            Assert.AreNotEqual(audio[0].TrackIndex, audio[1].TrackIndex);
        }

        [TestMethod]
        public void TheVideoAndAudioEventsStillReportTheFirstOfEachKind()
        {
            var seen = Describe(TwoAudioTracks, receiveAllTracks: true);

            // Taking every track must not change what the events that name a kind report, since
            // everything written against this client reads them.
            Assert.AreEqual("H264", seen.VideoCodec);
            Assert.AreEqual("PCMU", seen.AudioCodec);
        }

        [TestMethod]
        public void MetadataIsTakenByDefaultLikeAnyOtherFirstOfItsKind()
        {
            var seen = Describe(WithMetadata, receiveAllTracks: false);

            // It used to need ReceiveAllTracks, which also pulled in every second video and audio
            // track. The default is now one rule for every kind alike: the first of each.
            Assert.IsTrue(seen.Tracks.Any(t => t.Kind == TrackKind.Application),
                "the first metadata track is the first of its kind");

            Assert.AreEqual("H264", seen.VideoCodec, "and the video should be unaffected");
        }

        [TestMethod]
        public void MetadataCanBeLeftOutWithoutLeavingOutTheOtherExtras()
        {
            var seen = Describe(WithMetadata,
                t => RTSPClient.FirstOfEachKind(t) && t.Kind != TrackKind.Application);

            Assert.IsFalse(seen.Tracks.Any(t => t.Kind == TrackKind.Application),
                "a client that said not to should not set it up");

            Assert.AreEqual("H264", seen.VideoCodec, "and the video should be unaffected");
        }

        [TestMethod]
        public void MetadataIsOfferedWhenItIsAskedFor()
        {
            var seen = Describe(WithMetadata, receiveAllTracks: true);

            var metadata = seen.Tracks.SingleOrDefault(t => t.Kind == TrackKind.Application);

            Assert.IsNotNull(metadata, "the metadata track should be offered: " +
                string.Join(", ", seen.Tracks.Select(t => $"{t.Kind}/{t.Codec}")));

            Assert.AreEqual("vnd.onvif.metadata", metadata.Codec);
            Assert.AreEqual(107, metadata.PayloadType);
        }

        [TestMethod]
        public void AStreamOfOnlyMetadataIsStillSomethingToPlay()
        {
            var seen = Describe(
                "m=application 0 RTP/AVP 107\r\na=control:trackID=0\r\na=rtpmap:107 vnd.onvif.metadata/90000\r\n",
                receiveAllTracks: true);

            Assert.IsNull(seen.Stopped,
                "a stream with nothing but metadata is playable when metadata is what was asked for");

            Assert.HasCount(1, seen.Tracks);
            Assert.AreEqual(TrackKind.Application, seen.Tracks[0].Kind);
        }
    }
}
