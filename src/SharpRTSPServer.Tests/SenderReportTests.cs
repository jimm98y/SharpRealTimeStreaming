// SharpRTSPServer
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
using System.Threading;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// Sender reports go out on an interval, not one before every packet.
    /// </summary>
    /// <remarks>
    /// The flag that asks for one was set when a stream started playing and never cleared, so every
    /// single packet was preceded by a report - twice the packets on the wire, where RFC 3550 has
    /// RTCP at a few per cent of what the session sends.
    /// </remarks>
    [TestClass]
    public sealed class SenderReportTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        private static List<ReadOnlyMemory<byte>> Nal()
        {
            return new List<ReadOnlyMemory<byte>> { new ReadOnlyMemory<byte>(new byte[] { 0x65, 0x11, 0x22, 0x33 }) };
        }

        private static RtspTestClient Play(int port, string baseUri)
        {
            var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");
            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            Assert.AreEqual(200, setup.StatusCode);
            Assert.AreEqual(200, client.Send("PLAY", baseUri, "Session: " + setup.Session).StatusCode);
            return client;
        }

        private static (int Rtp, int Rtcp) Exchange(RTSPServer server, H264Track track, RtspTestClient client, int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                track.FeedInRawSamples((uint)(i * 3000), Nal());
                Thread.Sleep(5);
            }

            int rtp = 0, rtcp = 0;
            try
            {
                for (int i = 0; i < frames * 2 && rtp < frames; i++)
                {
                    if (client.ReadInterleaved().Channel == 0) rtp++;
                    else rtcp++;
                }
            }
            catch (System.IO.IOException)
            {
                // nothing more waiting, which is the point when reports are rare
            }

            return (rtp, rtcp);
        }

        [TestMethod]
        public void OneReportStartsTheStreamAndThenTheyAreRare()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password")
            {
                // far longer than this test runs, so only the opening report should appear
                RtcpSenderReportInterval = TimeSpan.FromMinutes(5)
            };

            var videoTrack = new H264Track(Sps, Pps);
            server.AddStreamSource(new RTSPStreamSource("stream1", videoTrack, null));
            server.StartListen();

            using var client = Play(port, "rtsp://127.0.0.1:" + port + "/stream1");
            var counts = Exchange(server, videoTrack, client, 30);

            Assert.AreEqual(30, counts.Rtp, "all of the media should arrive");
            Assert.AreEqual(1, counts.Rtcp,
                "one report to open the stream, and then none until the interval comes round");
        }

        [TestMethod]
        public void TheOpeningReportIsSentStraightAway()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password")
            {
                RtcpSenderReportInterval = TimeSpan.FromMinutes(5)
            };

            var videoTrack = new H264Track(Sps, Pps);
            server.AddStreamSource(new RTSPStreamSource("stream1", videoTrack, null));
            server.StartListen();

            using var client = Play(port, "rtsp://127.0.0.1:" + port + "/stream1");

            // A client needs the wall clock to RTP timestamp mapping before it can line audio up
            // against video, so it must not have to wait an interval for the first one.
            videoTrack.FeedInRawSamples(9000, Nal());

            Assert.AreNotEqual(0, client.ReadInterleaved().Channel,
                "the first thing on the wire should be the opening sender report");
        }

        [TestMethod]
        public void ReportsComeRoundAgainOnTheInterval()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password")
            {
                RtcpSenderReportInterval = TimeSpan.FromMilliseconds(200)
            };

            var videoTrack = new H264Track(Sps, Pps);
            server.AddStreamSource(new RTSPStreamSource("stream1", videoTrack, null));
            server.StartListen();

            using var client = Play(port, "rtsp://127.0.0.1:" + port + "/stream1");

            // a second's worth at a fifth of a second apart is a handful, not one per packet
            var counts = Exchange(server, videoTrack, client, 60);

            Assert.AreEqual(60, counts.Rtp);
            Assert.IsGreaterThan(1, counts.Rtcp, "the interval should have come round more than once");
            Assert.IsLessThan(30, counts.Rtcp, "but nothing like one per packet");
        }
    }
}
