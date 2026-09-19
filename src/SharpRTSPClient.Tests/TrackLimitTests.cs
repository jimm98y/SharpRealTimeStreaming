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

using System.Text;
using System.Threading;

namespace SharpRTSPClient.Tests
{
    /// <summary>
    /// The description comes from the server, and every track taken from it costs the client a pair
    /// of UDP ports or a pair of interleaved channels. These are about what a description can make
    /// this client spend.
    /// </summary>
    [TestClass]
    // Binds real UDP ports out of the range every other client in the suite draws from, and walks
    // descriptions far larger than any of them. Run on its own, it does not compete with them.
    [DoNotParallelize]
    public class TrackLimitTests
    {
        private const string SdpHeader =
            "v=0\r\n" +
            "o=- 0 0 IN IP4 127.0.0.1\r\n" +
            "s=test\r\n" +
            "c=IN IP4 127.0.0.1\r\n" +
            "t=0 0\r\n";

        /// <summary>
        /// A description offering many sections, all of a codec this client has no reader for.
        /// </summary>
        private static string ManyUnplayableVideoSections(int count)
        {
            var sdp = new StringBuilder();

            for (int i = 0; i < count; i++)
            {
                sdp.Append("m=video 0 RTP/AVP ").Append(100 + (i % 20)).Append("\r\n")
                   .Append("a=rtpmap:").Append(100 + (i % 20)).Append(" SOMETHINGELSE/90000\r\n")
                   .Append("a=control:trackID=").Append(i).Append("\r\n");
            }

            return sdp.ToString();
        }

        private static int PortsHeldAfterDescribing(string sdp, int maxTracks)
        {
            using var server = new FakeRtspServer(SdpHeader + sdp);
            using var client = new RTSPClient { MaxTracks = maxTracks, AutoPlay = false };

            var settled = new ManualResetEventSlim(false);
            client.Stopped += (s, e) => settled.Set();
            client.SetupMessageCompleted += (s, e) => settled.Set();

            // UDP, so a transport that is built binds a real pair of ports out of a real range.
            // Its own corner of the range, clear of the one the other tests take from.
            client.SetRtpPortRange(53000, 53100);
            client.Connect(server.BaseUri, RTPTransport.UDP);

            settled.Wait(5000);

            int held = 0;
            foreach (var track in client.Tracks)
            {
                if (track.Transport != null)
                {
                    held++;
                }
            }

            client.Stop();
            return held;
        }

        [TestMethod]
        public void SectionsOfACodecWeCannotPlayDoNotEachBindAPairOfPorts()
        {
            // Built up front, every one of these sections bound a pair of ports and gave none of
            // them back until the session ended.
            int held = PortsHeldAfterDescribing(ManyUnplayableVideoSections(12), maxTracks: 64);

            Assert.AreEqual(0, held,
                "a track this client cannot play should not have cost it a transport");
        }

        [TestMethod]
        public void ADescriptionCannotOfferMoreTracksThanWeWillTakeOn()
        {
            using var server = new FakeRtspServer(SdpHeader + ManyUnplayableVideoSections(40));
            using var client = new RTSPClient { MaxTracks = 8, AutoPlay = false };

            var settled = new ManualResetEventSlim(false);
            client.Stopped += (s, e) => settled.Set();
            client.SetupMessageCompleted += (s, e) => settled.Set();

            client.Connect(server.BaseUri, RTPTransport.TCP);
            settled.Wait(5000);

            Assert.IsLessThanOrEqualTo(8, client.Tracks.Length,
                "the description must not be able to name more tracks than this client will set up");

            client.Stop();
        }

        [TestMethod]
        public void AnOrdinaryDescriptionIsUnaffected()
        {
            string sdp =
                "m=video 0 RTP/AVP 96\r\n" +
                "a=rtpmap:96 H264/90000\r\n" +
                "a=fmtp:96 packetization-mode=1;sprop-parameter-sets=Z0IAHpZUBQHtgIgAAAMACAAAAwHgeLFmwA==,aM48gA==\r\n" +
                "a=control:trackID=0\r\n";

            using var server = new FakeRtspServer(SdpHeader + sdp);
            using var client = new RTSPClient { AutoPlay = false };

            string codec = null;
            var settled = new ManualResetEventSlim(false);
            client.NewTrack += (s, e) => codec = e.Codec;
            client.Stopped += (s, e) => settled.Set();
            client.SetupMessageCompleted += (s, e) => settled.Set();

            client.Connect(server.BaseUri, RTPTransport.TCP);
            settled.Wait(5000);

            Assert.AreEqual("H264", codec, "a stream this client can play still plays");

            client.Stop();
        }
    }
}
