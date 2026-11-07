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
    /// Taking a stalled client apart must not stop the server either.
    /// </summary>
    /// <remarks>
    /// The counterpart of <see cref="SlowClientTests"/>, which is about writing to a client that has
    /// stopped reading. This is about ending one. Shutting a session down waits for its writer to
    /// come out of the transport, which for a client that has stopped reading it does not do until
    /// the socket is closed - so those waits must happen with the connection list let go.
    /// <para>
    /// They used to happen under it. Every caller that dropped a session already held that lock, and
    /// the lock is re-entrant, so the "outside the list lock" half of the teardown was inside it
    /// after all - and an idle sweep that dropped several stalled clients at once held the whole
    /// server for as long as all of them took.
    /// </para>
    /// </remarks>
    [TestClass]
    public sealed class TeardownBlockingTests
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

            return client;
        }

        [TestMethod]
        public void DroppingStalledClientsDoesNotHoldUpTheRestOfTheServer()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, new InMemoryUserRepository("admin", "password"));
            var videoTrack = new H264Track(Sps, Pps);
            server.AddStreamSource(new RTSPStreamSource("stream1", videoTrack, null));
            server.StartListen();

            string baseUri = "rtsp://127.0.0.1:" + port + "/stream1";

            // Several of them, because the cost of doing this under the lock is per session: the
            // sweep that drops them was one wait after another with the server held throughout.
            var stalled = new List<RtspTestClient>();
            for (int i = 0; i < 4; i++)
            {
                stalled.Add(PlayAndStopReading(port, baseUri));
            }

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
                // let the writers get stuck in writes to clients that are not reading
                Thread.Sleep(1500);

                // Drops every connection that has gone away or timed out, which is the sweep the
                // reaper runs on its timer - and the place the waits used to pile up.
                var sweep = Task.Run(() => server.CheckTimeouts("stream1", out _, out _));

                var stopwatch = Stopwatch.StartNew();
                using var other = new RtspTestClient(port, "admin", "password");
                int status = other.Send("OPTIONS", baseUri).StatusCode;
                stopwatch.Stop();

                Assert.AreEqual(200, status, "another client should still be answered while a sweep is running");
                Assert.IsLessThan(3000, stopwatch.ElapsedMilliseconds,
                    "dropping stalled clients held up everyone else's requests");

                sweep.Wait(TimeSpan.FromSeconds(30));
            }
            finally
            {
                stop.Cancel();

                foreach (var client in stalled)
                {
                    client.Dispose();
                }

                feeder.Wait(TimeSpan.FromSeconds(10));
            }
        }

        [TestMethod]
        public void TearingDownAStalledClientLetsAnotherClientCarryOn()
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

                // Taking the stalled stream away drops its client, which is a teardown that has to
                // wait for a writer sitting in a write that will not come back.
                var removing = Task.Run(() =>
                    server.RemoveStreamSource(server.GetStreamSources()[0]));

                // Meanwhile the other stream, which has nothing to do with any of it, keeps going.
                livelyTrack.FeedInRawSamples(9000, new List<ReadOnlyMemory<byte>>
                {
                    new ReadOnlyMemory<byte>(new byte[] { 0x65, 0x11, 0x22, 0x33 })
                });

                bool arrived = false;
                for (int i = 0; i < 20 && !arrived; i++)
                {
                    arrived = reader.ReadInterleaved().Channel == 0;
                }

                Assert.IsTrue(arrived, "the second stream's media never arrived while the first was being taken apart");

                removing.Wait(TimeSpan.FromSeconds(30));
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
