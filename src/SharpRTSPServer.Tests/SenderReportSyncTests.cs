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
    /// Whether a player can line the audio up with the picture.
    /// </summary>
    /// <remarks>
    /// Two tracks carry two RTP clocks, counting at different rates from unrelated starting points.
    /// The only thing tying them together is the sender report: it says "at this wall clock instant,
    /// this stream's RTP clock read this". A player works out each stream's offset from that and
    /// plays them against a common timeline, so if the two reports disagree about what instant a
    /// given piece of media belongs to, the sound arrives that much before or after the picture.
    /// </remarks>
    [TestClass]
    public sealed class SenderReportSyncTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        private const int VIDEO_CLOCK = 90000;
        private const int AUDIO_CLOCK = 8000;

        /// <summary>A frame every 40ms, which is 25 a second.</summary>
        private const int FRAME_MS = 40;

        private const int RTCP_PACKET_TYPE_SENDER_REPORT = 200;

        /// <summary>
        /// The wall clock instant a sender report says its RTP timestamp belongs to, and that
        /// timestamp, as seconds.
        /// </summary>
        private readonly struct Report
        {
            public Report(double ntpSeconds, uint rtp)
            {
                NtpSeconds = ntpSeconds;
                Rtp = rtp;
            }

            public double NtpSeconds { get; }
            public uint Rtp { get; }
        }

        private static bool TryReadSenderReport(byte[] payload, out Report report)
        {
            report = default;

            if (payload == null || payload.Length < 28 || payload[1] != RTCP_PACKET_TYPE_SENDER_REPORT)
            {
                return false;
            }

            ulong seconds = ((ulong)payload[8] << 24) | ((ulong)payload[9] << 16)
                          | ((ulong)payload[10] << 8) | payload[11];

            ulong fraction = ((ulong)payload[12] << 24) | ((ulong)payload[13] << 16)
                           | ((ulong)payload[14] << 8) | payload[15];

            uint rtp = ((uint)payload[16] << 24) | ((uint)payload[17] << 16)
                     | ((uint)payload[18] << 8) | payload[19];

            report = new Report(seconds + fraction / 4294967296.0, rtp);
            return true;
        }

        [TestMethod]
        public void TheTwoTracksAgreeOnWhereTheyAreInTime()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, new InMemoryUserRepository("admin", "password"));

            // A report on every frame, so the test does not have to run for half a minute to see one.
            server.RtcpSenderReportInterval = TimeSpan.FromMilliseconds(1);

            var video = new H264Track(Sps, Pps) { ID = 0 };
            var audio = new PCMUTrack { ID = 1 };
            server.AddStreamSource(new RTSPStreamSource("stream1", video, audio));
            server.StartListen();

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            var first = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            client.Send("SETUP", baseUri + "/trackID=1",
                $"Transport: RTP/AVP/TCP;unicast;interleaved=2-3\r\nSession: {first.Session}");

            Assert.AreEqual(200, client.Send("PLAY", baseUri, "Session: " + first.Session).StatusCode);

            var picture = new List<ReadOnlyMemory<byte>> { new ReadOnlyMemory<byte>(new byte[400]) };
            var sound = new List<ReadOnlyMemory<byte>> { new ReadOnlyMemory<byte>(new byte[160]) };

            Report? lastVideo = null;
            Report? lastAudio = null;

            // Fed together, in real time, with timestamps that say so: frame k of each belongs to
            // the same instant, k * 40ms after the start.
            var producer = new Thread(() =>
            {
                for (int k = 0; k < 60; k++)
                {
                    video.FeedInRawSamples((uint)(k * (VIDEO_CLOCK * FRAME_MS / 1000)), picture);
                    audio.FeedInRawSamples((uint)(k * (AUDIO_CLOCK * FRAME_MS / 1000)), sound);
                    Thread.Sleep(FRAME_MS);
                }
            })
            { IsBackground = true };

            producer.Start();

            for (int i = 0; i < 400 && (lastVideo == null || lastAudio == null); i++)
            {
                var frame = client.ReadInterleaved();

                if (frame.Channel == 1 && TryReadSenderReport(frame.Payload, out Report v))
                {
                    lastVideo = v;
                }
                else if (frame.Channel == 3 && TryReadSenderReport(frame.Payload, out Report a))
                {
                    lastAudio = a;
                }
            }

            producer.Join();

            Assert.IsNotNull(lastVideo, "no sender report arrived for the video");
            Assert.IsNotNull(lastAudio, "no sender report arrived for the audio");

            // Where each stream says its own media timeline began, in wall clock terms. Both were
            // fed from zero at the same moment, so both should name the same instant.
            double videoStart = lastVideo.Value.NtpSeconds - lastVideo.Value.Rtp / (double)VIDEO_CLOCK;
            double audioStart = lastAudio.Value.NtpSeconds - lastAudio.Value.Rtp / (double)AUDIO_CLOCK;

            double driftMs = Math.Abs(videoStart - audioStart) * 1000.0;

            Assert.IsLessThan(FRAME_MS, driftMs,
                $"the reports disagree by {driftMs:F0}ms about where the streams are in time");
        }
    }
}
