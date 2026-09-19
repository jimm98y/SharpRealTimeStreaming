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
    /// What the server allocates while it is streaming.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A server that makes garbage for every frame of every client makes it faster the more clients
    /// it has, and collecting it lands on the very threads that are trying to write media. The point
    /// here is not a number but a shape: what the server allocates must not grow with the number of
    /// people watching.
    /// </para>
    /// <para>
    /// This is measured across the whole process, which is blunt but is what was actually noticed -
    /// memory visibly going up and down in Task Manager while the server ran. Everything the test
    /// itself does while measuring is therefore kept out of the way: the clients are drained into a
    /// buffer each rather than through <see cref="RtspTestClient.ReadInterleaved"/>, which would
    /// allocate more per frame than the server ever did.
    /// </para>
    /// </remarks>
    [TestClass]
    [DoNotParallelize]
    public sealed class AllocationTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        /// <summary>
        /// Frames to send while measuring. Enough that anything per-frame shows up well clear of
        /// the noise, and not so many that the test drags.
        /// </summary>
        private const int FRAMES = 1000;

        /// <summary>
        /// What one frame may cost, sent to three clients at once.
        /// </summary>
        /// <remarks>
        /// Measured at around seventy bytes, which is the test's own loop rather than the server.
        /// The bar is well above that and still far below what a single careless thing on the send
        /// path costs: the debug line this test was written for came to seven hundred bytes per
        /// frame per client, so over two thousand here.
        /// </remarks>
        private const int MOST_BYTES_A_FRAME = 400;

        [TestMethod]
        public void StreamingToSeveralClientsAllocatesAlmostNothing()
        {
            const int Clients = 3;

            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, new InMemoryUserRepository("admin", "password"));

            var video = new H264Track(Sps, Pps);
            server.AddStreamSource(new RTSPStreamSource("stream1", video, null));
            server.StartListen();

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            var clients = new List<RtspTestClient>();
            var stop = new CancellationTokenSource();

            try
            {
                for (int i = 0; i < Clients; i++)
                {
                    var client = new RtspTestClient(port, "admin", "password");
                    client.Send("OPTIONS", baseUri);
                    client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

                    var setup = client.Send("SETUP", baseUri + "/trackID=0",
                        "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
                    Assert.AreEqual(200, setup.StatusCode, "the client should be set up");
                    Assert.AreEqual(200, client.Send("PLAY", baseUri, "Session: " + setup.Session).StatusCode);

                    clients.Add(client);

                    // Someone has to be reading, or the queues fill and the frames are dropped
                    // rather than written - and dropping them is free.
                    var mine = client;
                    new Thread(() =>
                    {
                        byte[] buffer = new byte[64 * 1024];
                        while (!stop.IsCancellationRequested)
                        {
                            try
                            {
                                if (!mine.DrainInto(buffer))
                                {
                                    return;
                                }
                            }
                            catch
                            {
                                return;
                            }
                        }
                    })
                    { IsBackground = true }.Start();
                }

                var samples = new List<ReadOnlyMemory<byte>> { new ReadOnlyMemory<byte>(new byte[1200]) };

                // Warmed up first: the pools start empty, the writer threads have not been made yet,
                // and the first frames pay for all of it.
                for (int i = 0; i < 300; i++)
                {
                    video.FeedInRawSamples((uint)(i * 3000), samples);
                    Thread.Sleep(2);
                }

                Thread.Sleep(500);

                // What the process allocates doing nothing at all, over the same sort of span, so
                // that the timers and whatever else ticks in the background are not charged to the
                // server.
                long idleFrom = GC.GetTotalAllocatedBytes(precise: true);
                Thread.Sleep(2000);
                double idleASecond = (GC.GetTotalAllocatedBytes(precise: true) - idleFrom) / 2.0;

                // The best of a few, because this counts the whole process and the rest of the test
                // run is in it - another test starting a server while this one measures is charged
                // to the server being measured. Anything the server itself allocates shows up in
                // every attempt; anything else is somebody else's and the lowest reading is the one
                // least contaminated by it.
                double aFrame = double.MaxValue;

                for (int attempt = 0; attempt < 3 && aFrame > MOST_BYTES_A_FRAME; attempt++)
                {
                    int from = attempt * FRAMES + 500;

                    long before = GC.GetTotalAllocatedBytes(precise: true);

                    for (int i = 0; i < FRAMES; i++)
                    {
                        video.FeedInRawSamples((uint)((i + from) * 3000), samples);
                        Thread.Sleep(2);
                    }

                    Thread.Sleep(500);
                    long busy = GC.GetTotalAllocatedBytes(precise: true) - before;

                    // The loop above takes about as long as the idle stretch and a quarter.
                    aFrame = Math.Min(aFrame, (busy - idleASecond * 2.5) / FRAMES);
                }

                Assert.IsLessThan(MOST_BYTES_A_FRAME, aFrame,
                    $"streaming allocated {aFrame:F0} bytes a frame across {Clients} clients");
            }
            finally
            {
                stop.Cancel();

                foreach (var client in clients)
                {
                    client.Dispose();
                }
            }
        }
    }
}
