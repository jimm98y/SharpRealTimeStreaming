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

using System.Collections.Generic;
using System.Threading;

namespace SharpRTSPClient.Tests
{
    /// <summary>
    /// What the client does with an SDP that is odd rather than wrong.
    /// </summary>
    /// <remarks>
    /// The SDP comes from the far end, so it is worth being deliberate about which parts of it are
    /// worth refusing a stream over and which are not.
    /// </remarks>
    [TestClass]
    public sealed class MalformedSdpTests
    {
        private const string Head = "v=0\r\no=- 0 0 IN IP4 0.0.0.0\r\ns=Test\r\nc=IN IP4 0.0.0.0\r\n";

        private sealed class Outcome
        {
            public bool ReachedPlay { get; set; }
            public List<StoppedReason> Stops { get; set; }
        }

        private static Outcome Connect(string sdp, MediaRequest request = MediaRequest.VIDEO_ONLY)
        {
            using var server = new FakeRtspServer(sdp);

            var stops = new List<StoppedReason>();
            using var client = new RTSPClient();
            client.Stopped += (s, e) => { lock (stops) stops.Add(e.Reason); };

            client.Connect(server.BaseUri, RTPTransport.TCP, "admin", "password",
                request, false, null, false);

            bool reachedPlay = server.WaitForRequest("PLAY", 3000);
            Thread.Sleep(200);

            lock (stops)
            {
                return new Outcome { ReachedPlay = reachedPlay, Stops = new List<StoppedReason>(stops) };
            }
        }

        [TestMethod]
        public void MalformedParameterSetsAreIgnoredRatherThanFatal()
        {
            // A stream with no fmtp at all plays, because the parameter sets arrive in the stream
            // itself. One whose fmtp is present and unreadable has no business being worse than that,
            // and it used to end the session as a bare protocol error naming nothing.
            var outcome = Connect(Head +
                "m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 H264/90000\r\n" +
                "a=fmtp:96 packetization-mode=1; sprop-parameter-sets=!!!!notbase64!!!!,aM48gA==\r\n");

            Assert.IsTrue(outcome.ReachedPlay, "the stream should still play");
            CollectionAssert.DoesNotContain(outcome.Stops, StoppedReason.ProtocolError);
        }

        [TestMethod]
        public void AStreamWithNoFormatParametersStillPlays()
        {
            // the case the one above is measured against
            var outcome = Connect(Head +
                "m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 H264/90000\r\n");

            Assert.IsTrue(outcome.ReachedPlay);
        }

        [TestMethod]
        public void APayloadNumberNoRtpPacketCouldCarryIsNotSetUp()
        {
            // The field in an RTP header is seven bits. 9999 can be advertised but never received, so
            // taking it at face value gave a session that played and then discarded everything that
            // arrived for not matching - no media, and no error either.
            var outcome = Connect(Head +
                "m=video 0 RTP/AVP 9999\r\na=control:trackID=0\r\na=rtpmap:9999 H264/90000\r\n");

            Assert.IsFalse(outcome.ReachedPlay, "a stream that can never deliver should not be set up");
            CollectionAssert.Contains(outcome.Stops, StoppedReason.UnsupportedMedia);
        }

        [TestMethod]
        public void AUsablePayloadNumberIsStillAccepted()
        {
            var outcome = Connect(Head +
                "m=video 0 RTP/AVP 127\r\na=control:trackID=0\r\na=rtpmap:127 H264/90000\r\n");

            Assert.IsTrue(outcome.ReachedPlay, "127 is the highest an RTP packet can carry");
        }

        [TestMethod]
        public void AGoodStreamIsTakenEvenWhenAnotherIsUnusable()
        {
            // the first section can never deliver, the second can
            var outcome = Connect(Head +
                "m=video 0 RTP/AVP 9999\r\na=control:trackID=0\r\na=rtpmap:9999 H264/90000\r\n" +
                "m=video 0 RTP/AVP 96\r\na=control:trackID=1\r\na=rtpmap:96 H264/90000\r\n");

            Assert.IsTrue(outcome.ReachedPlay, "the usable stream should still be found");
        }

        [TestMethod]
        public void AnUnknownCodecIsStillReportedAsUnsupported()
        {
            // unchanged behaviour, kept honest while the guards around it moved
            var outcome = Connect(Head +
                "m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 NOSUCHCODEC/90000\r\n");

            Assert.IsFalse(outcome.ReachedPlay);
            CollectionAssert.Contains(outcome.Stops, StoppedReason.UnsupportedMedia);
        }
    }
}
