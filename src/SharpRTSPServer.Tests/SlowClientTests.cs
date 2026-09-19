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
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// One client that stops reading must not take the rest of the server with it.
    /// </summary>
    /// <remarks>
    /// Writing to a socket blocks once the far end stops emptying it. That write used to happen
    /// while the server held the lock that guards its connection list, which every RTSP request and
    /// every other stream also needs - so one client that went quiet stopped all of them.
    /// </remarks>
    [TestClass]
    public sealed class SlowClientTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        /// <summary>Big enough that a few of them fill the socket buffers at both ends.</summary>
        private static List<ReadOnlyMemory<byte>> BigNal()
        {
            var nal = new byte[1400];
            nal[0] = 0x65;
            return new List<ReadOnlyMemory<byte>> { new ReadOnlyMemory<byte>(nal) };
        }

        private static RtspTestClient PlayAndStopReading(int port, string baseUri)
        {
            var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            Assert.AreEqual(200, setup.StatusCode);
            Assert.AreEqual(200, client.Send("PLAY", baseUri, "Session: " + setup.Session).StatusCode);

            // and from here it simply never reads again
            return client;
        }

        [TestMethod]
        public void AClientThatStopsReadingDoesNotBlockAnotherClientsRequests()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, new InMemoryUserRepository("admin", "password"));
            var videoTrack = new H264Track(Sps, Pps);
            server.AddStreamSource(new RTSPStreamSource("stream1", videoTrack, null));
            server.StartListen();

            string baseUri = "rtsp://127.0.0.1:" + port + "/stream1";
            var stalled = PlayAndStopReading(port, baseUri);

            var stop = new CancellationTokenSource();
            var feeder = Task.Run(() =>
            {
                uint timestamp = 0;
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        videoTrack.FeedInRawSamples(timestamp += 3000, BigNal());
                    }
                    catch (Exception)
                    {
                        return;
                    }
                }
            });

            try
            {
                // give the feeder time to fill the stalled client's buffers and block in a write
                Thread.Sleep(1500);

                var stopwatch = Stopwatch.StartNew();
                using var other = new RtspTestClient(port, "admin", "password");
                int status = other.Send("OPTIONS", baseUri).StatusCode;
                stopwatch.Stop();

                Assert.AreEqual(200, status, "another client should still be answered");
                Assert.IsLessThan(3000, stopwatch.ElapsedMilliseconds,
                    "a client that stopped reading held up everyone else's requests");
            }
            finally
            {
                stop.Cancel();

                // disposing the stalled client is what releases the write the feeder is sitting in
                stalled.Dispose();
                feeder.Wait(TimeSpan.FromSeconds(10));
            }
        }

        [TestMethod]
        public void AClientThatStopsReadingDoesNotBlockAnotherStreamsMedia()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, new InMemoryUserRepository("admin", "password"));

            var stalledTrack = new H264Track(Sps, Pps);
            var livelyTrack = new H264Track(Sps, Pps);
            server.AddStreamSource(new RTSPStreamSource("stream1", stalledTrack, null));
            server.AddStreamSource(new RTSPStreamSource("stream2", livelyTrack, null));
            server.StartListen();

            string stalledUri = "rtsp://127.0.0.1:" + port + "/stream1";
            string livelyUri = "rtsp://127.0.0.1:" + port + "/stream2";

            var stalled = PlayAndStopReading(port, stalledUri);

            using var reader = new RtspTestClient(port, "admin", "password");
            reader.Send("OPTIONS", livelyUri);
            reader.Send("DESCRIBE", livelyUri, "Accept: application/sdp");
            var setup = reader.Send("SETUP", livelyUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            Assert.AreEqual(200, reader.Send("PLAY", livelyUri, "Session: " + setup.Session).StatusCode);

            var stop = new CancellationTokenSource();
            var feeder = Task.Run(() =>
            {
                uint timestamp = 0;
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        stalledTrack.FeedInRawSamples(timestamp += 3000, BigNal());
                    }
                    catch (Exception)
                    {
                        return;
                    }
                }
            });

            try
            {
                Thread.Sleep(1500);

                // the other stream has nothing to do with the client that went quiet
                livelyTrack.FeedInRawSamples(9000, new List<ReadOnlyMemory<byte>>
                {
                    new ReadOnlyMemory<byte>(new byte[] { 0x65, 0x11, 0x22, 0x33 })
                });

                for (int i = 0; i < 20; i++)
                {
                    if (reader.ReadInterleaved().Channel == 0)
                        return;
                }

                Assert.Fail("the second stream's media never arrived");
            }
            finally
            {
                stop.Cancel();
                stalled.Dispose();
                feeder.Wait(TimeSpan.FromSeconds(10));
            }
        }
    }
}
