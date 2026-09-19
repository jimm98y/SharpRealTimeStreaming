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
using System.Reflection;
using Rtsp;
using Rtsp.Messages;
using System.Threading;

namespace SharpRTSPClient.Tests
{
    /// <summary>
    /// The client reports and sends per track, not per kind.
    /// </summary>
    /// <remarks>
    /// It used to expose the first video track and the first audio track and nothing else - two of
    /// however many a stream carries, which is an arbitrary pair once a stream can carry more. The
    /// by-kind events and sends are still there as shortcuts for the ordinary stream of one of each.
    /// </remarks>
    [TestClass]
    public class PerTrackApiTests
    {
        private const string SdpHeader =
            "v=0\r\n" +
            "o=- 0 0 IN IP4 127.0.0.1\r\n" +
            "s=test\r\n" +
            "c=IN IP4 127.0.0.1\r\n" +
            "t=0 0\r\n";

        /// <summary>Two video tracks and one audio, so "the first of each kind" does not cover it.</summary>
        private const string ThreeTracks =
            "m=video 0 RTP/AVP 96\r\n" +
            "a=rtpmap:96 H264/90000\r\n" +
            "a=fmtp:96 packetization-mode=1;sprop-parameter-sets=Z0IAHpZUBQHtgIgAAAMACAAAAwHgeLFmwA==,aM48gA==\r\n" +
            "a=control:trackID=0\r\n" +
            "m=video 0 RTP/AVP 97\r\n" +
            "a=rtpmap:97 H264/90000\r\n" +
            "a=fmtp:97 packetization-mode=1;sprop-parameter-sets=Z0IAHpZUBQHtgIgAAAMACAAAAwHgeLFmwA==,aM48gA==\r\n" +
            "a=control:trackID=1\r\n" +
            "m=audio 0 RTP/AVP 0\r\n" +
            "a=control:trackID=2\r\n";

        private static RTSPClient Describe(string sdp, out FakeRtspServer server, bool allTracks = true)
        {
            server = new FakeRtspServer(SdpHeader + sdp);
            var client = new RTSPClient { AutoPlay = false };
            client.AcceptTrack = allTracks ? (_ => true) : RTSPClient.FirstOfEachKind;

            var settled = new ManualResetEventSlim(false);
            client.Stopped += (s, e) => settled.Set();
            client.SetupMessageCompleted += (s, e) => settled.Set();

            client.Connect(server.BaseUri, RTPTransport.TCP);
            settled.Wait(5000);
            return client;
        }

        [TestMethod]
        public void TheContextsAreNoLongerPartOfThePublicApi()
        {
            // Either all of them or none, and none is what was chosen: a SrtpSessionContext is live
            // crypto state, and SendRTCP is what callers actually wanted it for.
            Type client = typeof(RTSPClient);

            Assert.IsNull(client.GetProperty("VideoContext", BindingFlags.Public | BindingFlags.Instance));
            Assert.IsNull(client.GetProperty("AudioContext", BindingFlags.Public | BindingFlags.Instance));

            Assert.IsFalse(
                client.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                      .Any(p => p.PropertyType == typeof(SharpSRTP.SRTP.SrtpSessionContext)),
                "no SRTP context should be reachable from outside, whichever track it belongs to");
        }

        [TestMethod]
        public void EveryTrackIsCounted()
        {
            using var client = Describe(ThreeTracks, out var server);
            using (server)
            {
                Assert.AreEqual(3, client.TrackCount, "two video tracks and one audio");
                client.Stop();
            }
        }

        [TestMethod]
        public void SendingRtcpOnATrackThatIsNotThereIsRefused()
        {
            using var client = Describe(ThreeTracks, out var server);
            using (server)
            {
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => client.SendRTCP(9, new byte[8]));
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => client.SendRTCP(-1, new byte[8]));
                client.Stop();
            }
        }

        [TestMethod]
        public void EveryTrackCanBeSentRtcpIncludingTheSecondOfItsKind()
        {
            using var client = Describe(ThreeTracks, out var server);
            using (server)
            {
                // The point of the change: the second video track is reachable, where before only
                // the first video and first audio were.
                for (int i = 0; i < client.TrackCount; i++)
                {
                    client.SendRTCP(i, client.BuildRtcpReceiverReport(1234));
                }

                client.Stop();
            }
        }

        [TestMethod]
        public void AStreamOfOneOfEachIsStillJustTracksZeroAndOne()
        {
            string oneOfEach =
                "m=video 0 RTP/AVP 96\r\n" +
                "a=rtpmap:96 H264/90000\r\n" +
                "a=fmtp:96 packetization-mode=1;sprop-parameter-sets=Z0IAHpZUBQHtgIgAAAMACAAAAwHgeLFmwA==,aM48gA==\r\n" +
                "a=control:trackID=0\r\n" +
                "m=audio 0 RTP/AVP 0\r\n" +
                "a=control:trackID=1\r\n";

            using var client = Describe(oneOfEach, out var server, allTracks: false);
            using (server)
            {
                Assert.AreEqual(2, client.TrackCount);
                client.SendRTCP(0, client.BuildRtcpReceiverReport(1));
                client.SendRTCP(1, client.BuildRtcpReceiverReport(2));
                client.Stop();
            }
        }

        [TestMethod]
        public void EachTrackReportsUnderAnSsrcOfItsOwn()
        {
            using var client = Describe(ThreeTracks, out var server);
            using (server)
            {
                var ssrcs = new List<uint>();
                for (int i = 0; i < client.TrackCount; i++)
                {
                    ssrcs.Add(client.GetSsrc(i));
                }

                // Two tracks reporting under one SSRC cannot be told apart by whatever reads the
                // reports. There used to be one SSRC for video and one for audio, so a stream with
                // two video tracks had both of them reporting as the same source.
                CollectionAssert.AllItemsAreUnique(ssrcs);

                client.SetSsrc(2, 0xDEADBEEF);
                Assert.AreEqual(0xDEADBEEFu, client.GetSsrc(2));

                client.Stop();
            }
        }

        [TestMethod]
        public void TheSsrcOfATrackThatIsNotThereIsRefused()
        {
            using var client = Describe(ThreeTracks, out var server);
            using (server)
            {
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => client.GetSsrc(9));
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => client.SetSsrc(9, 1));
                client.Stop();
            }
        }

        [TestMethod]
        public void TheRawRtpEventIsRaisedForEveryTrackAndSaysWhich()
        {
            var seen = new List<TrackRawRtpEventArgs>();

            using var client = Describe(ThreeTracks, out var server);
            using (server)
            {
                client.ReceivedRawRTP += (s, e) => { lock (seen) { seen.Add(e); } };

                // Drive the receive path directly: the fake server describes but does not stream.
                FeedRtp(client, trackIndex: 1, payloadType: 97);

                lock (seen)
                {
                    Assert.AreEqual(1, seen.Count, "the second video track should have reported");
                    Assert.AreEqual(1, seen[0].TrackIndex);
                    Assert.AreEqual(TrackKind.Video, seen[0].Kind);
                    Assert.AreEqual("H264", seen[0].Codec);
                    Assert.IsNotNull(seen[0].Data);
                }

                client.Stop();
            }
        }

        [TestMethod]
        public void NothingOnTheClientResolvesATrackByItsKindAnyMore()
        {
            // The by-kind API is gone rather than kept alongside: every one of these named the first
            // track of a kind, which is a track a stream carrying two of them does not have.
            string[] removed =
            {
                "NewVideoStream", "NewAudioStream",
                "ReceivedVideoData", "ReceivedAudioData",
                "ReceivedRawVideoRTP", "ReceivedRawAudioRTP",
                "ReceivedRawVideoRTCP", "ReceivedRawAudioRTCP",
                "SendVideoRTCP", "SendAudioRTCP",
                "VideoSSRC", "AudioSSRC",
                "VideoContext", "AudioContext",
            };

            Type client = typeof(RTSPClient);

            foreach (string name in removed)
            {
                Assert.IsNull(client.GetMember(name, BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(),
                    name + " is still part of the public API");
            }
        }

        /// <summary>
        /// Hands one RTP packet to the receive path of a given track.
        /// </summary>
        private static void FeedRtp(RTSPClient client, int trackIndex, int payloadType)
        {
            var packet = new byte[20];
            packet[0] = 0x80;                  // version 2
            packet[1] = (byte)payloadType;
            packet[3] = 1;                     // sequence number
            packet[12] = 0x65;                 // an IDR NAL, so the H264 parser is happy

            MethodInfo received = typeof(RTSPClient).GetMethod("RtpDataReceived",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(received, "RtpDataReceived is not where the test expects it");

            var data = new RtspData(new Owner(packet), packet.Length);
            received.Invoke(client, new object[] { client.TrackAt(trackIndex), new RtspDataEventArgs(data) });
        }

        private sealed class Owner : System.Buffers.IMemoryOwner<byte>
        {
            private readonly byte[] _buffer;
            public Owner(byte[] buffer) { _buffer = buffer; }
            public Memory<byte> Memory => _buffer.AsMemory();
            public void Dispose() { }
        }
    }
}
