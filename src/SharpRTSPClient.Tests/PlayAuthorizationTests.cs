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
using System.Text.RegularExpressions;
using System.Threading;

namespace SharpRTSPClient.Tests
{
    /// <summary>
    /// Every overload of Play authorizes its request. The timed ones, used for ONVIF playback,
    /// once went out bare: a Digest server cost a 401 and a round trip each time, and a server
    /// that refused without a fresh challenge stopped the client.
    /// </summary>
    [TestClass]
    public sealed class PlayAuthorizationTests
    {
        private const string Sdp =
            "v=0\r\no=- 0 0 IN IP4 0.0.0.0\r\ns=Test\r\nc=IN IP4 0.0.0.0\r\n" +
            "m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 H264/90000\r\n" +
            "a=fmtp:96 packetization-mode=1; sprop-parameter-sets=Z0IAHpZUBaHogA==,aM48gA==\r\n";

        private static readonly DateTime From = new DateTime(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime To = From.AddMinutes(5);

        [TestMethod]
        [DataRow(false, DisplayName = "Play()")]
        [DataRow(true, DisplayName = "Play() in a playback session")]
        public void PlayCarriesAuthorization(bool playbackSession)
        {
            AssertPlayIsAuthorized(playbackSession, client => client.Play());
        }

        [TestMethod]
        [DataRow(false, DisplayName = "Play(from)")]
        [DataRow(true, DisplayName = "Play(from) in a playback session")]
        public void PlayFromATimeCarriesAuthorization(bool playbackSession)
        {
            AssertPlayIsAuthorized(playbackSession, client => client.Play(From));
        }

        [TestMethod]
        [DataRow(false, DisplayName = "Play(from, to)")]
        [DataRow(true, DisplayName = "Play(from, to) in a playback session")]
        public void PlayOverATimeRangeCarriesAuthorization(bool playbackSession)
        {
            AssertPlayIsAuthorized(playbackSession, client => client.Play(From, To));
        }

        private static void AssertPlayIsAuthorized(bool playbackSession, Action<RTSPClient> play)
        {
            using var server = new FakeRtspServer(Sdp) { RequireAuthentication = true };

            using var setUp = new ManualResetEventSlim();
            using var client = new RTSPClient { AutoPlay = false };
            client.AcceptTrack = t => t.Kind == TrackKind.Video;
            client.SetupMessageCompleted += (s, e) => setUp.Set();

            client.Connect(server.BaseUri, RTPTransport.TCP, "admin", "password", playbackSession);

            Assert.IsTrue(setUp.Wait(8000), "the client never finished SETUP");
            Assert.IsFalse(server.Requests.Contains("PLAY"), "PLAY went out before the test asked for it");

            play(client);

            Assert.IsTrue(server.WaitForRequest("PLAY", 5000), "the client never sent PLAY");

            // Only the first PLAY tells: one sent bare is answered 401 and resent with credentials,
            // so a later one would carry them either way.
            string firstPlay = server.RawRequests.First(r => r.StartsWith("PLAY ", StringComparison.Ordinal));
            Assert.IsTrue(
                Regex.IsMatch(firstPlay, @"^Authorization:\s*Digest\s", RegexOptions.Multiline | RegexOptions.IgnoreCase),
                "PLAY went out without authorization:\r\n" + firstPlay);
        }
    }
}
