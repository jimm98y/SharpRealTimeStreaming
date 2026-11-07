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
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// What the server costs to serve many clients, and that serving them from shared threads has
    /// not cost anything in how the media arrives.
    /// </summary>
    /// <remarks>
    /// Each connection used to have a writer thread of its own. That is fine for tens of clients and
    /// ruinous for thousands - a megabyte of stack apiece, and a scheduler spending its time moving
    /// between them - so the threads are now shared, and a connection holds one only while it is
    /// actually being written to.
    /// </remarks>
    [TestClass]
    // Forty clients at once, and a count of threads that only means anything if the rest of the
    // suite is not making its own - so it does not share.
    [DoNotParallelize]
    public sealed class WriterPoolTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        private const int Clients = 40;

        private static RtspTestClient Play(int port, string baseUri)
        {
            var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            Assert.AreEqual(200, setup.StatusCode);
            Assert.AreEqual(200, client.Send("PLAY", baseUri, $"Session: {setup.Session}").StatusCode);

            return client;
        }

        [TestMethod]
        public void TheNumberOfThreadsDoesNotFollowTheNumberOfClients()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, new InMemoryUserRepository("admin", "password"));
            var videoTrack = new H264Track(Sps, Pps);
            server.AddStreamSource(new RTSPStreamSource("stream1", videoTrack, null));
            server.StartListen();

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            var clients = new List<RtspTestClient>();

            try
            {
                for (int i = 0; i < Clients; i++)
                {
                    clients.Add(Play(port, baseUri));
                }

                // all of them being written to, repeatedly
                for (int i = 0; i < 20; i++)
                {
                    videoTrack.FeedInRawSamples((uint)(i * 3000), new List<ReadOnlyMemory<byte>>
                    {
                        new ReadOnlyMemory<byte>(new byte[1200]),
                    });

                    Thread.Sleep(10);
                }

                Thread.Sleep(300);

                // Clients that are reading are written to in microseconds, so a few threads carry all
                // of them. The bound is generous - the point is that it is not one apiece, which at
                // this many clients it plainly would be.
                Assert.IsLessThan(Clients / 2, server.WriterThreadCount,
                    $"{server.WriterThreadCount} threads for {Clients} clients that are all keeping up");
            }
            finally
            {
                foreach (var client in clients)
                {
                    client.Dispose();
                }
            }
        }

        /// <summary>
        /// Every writer held by a connection whose write will not return, and one more with work.
        /// </summary>
        /// <remarks>
        /// This is what sharing the threads costs. A write to a client that has stopped reading does
        /// not come back until its socket is closed, so it holds the thread that picked it up - and
        /// with enough such clients they hold every thread there is, and the ones still watching are
        /// not written to at all. The pool answers by making more threads once its own have all been
        /// busy for longer than any healthy write takes.
        /// <para>
        /// Staged here rather than with real clients, because a write only blocks once the socket
        /// buffers at both ends are full, and how long that takes is the operating system business.
        /// A write that waits on demand says the same thing and says it every time.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void AWriteThatNeverReturnsDoesNotHoldUpTheOtherConnections()
        {
            using var pool = new RtpWriterPool(NullLogger.Instance, RtpWriterPool.DEFAULT_MAX_THREADS);
            using var stuck = new ManualResetEventSlim(false);

            // more of them than the pool keeps threads for, so between them they can hold all of it
            int stalledCount = Environment.ProcessorCount * 2;
            var queues = new List<OutboundQueue>();

            try
            {
                for (int i = 0; i < stalledCount; i++)
                {
                    var queue = new OutboundQueue(pool, 8, 1 << 20,
                        frame => stuck.Wait(TimeSpan.FromSeconds(30)), "stalled " + i, NullLogger.Instance);

                    queues.Add(queue);
                    queue.Enqueue(NewFrame());
                }

                // let them all be picked up and get stuck
                Thread.Sleep(500);

                var written = new ManualResetEventSlim(false);
                var watching = new OutboundQueue(pool, 8, 1 << 20,
                    frame => written.Set(), "watching", NullLogger.Instance);

                queues.Add(watching);
                watching.Enqueue(NewFrame());

                Assert.IsTrue(written.Wait(TimeSpan.FromSeconds(5)),
                    $"nothing was written for a connection that was ready while {stalledCount} " +
                    $"connections sat in writes that never returned ({pool.ThreadCount} threads)");
            }
            finally
            {
                stuck.Set();

                foreach (var queue in queues)
                {
                    queue.Dispose();
                }
            }
        }

        private static QueuedFrame NewFrame()
        {
            var frame = new QueuedFrame();
            var packets = RtpPackets.Take();
            packets.Rent(64);
            frame.Fill(packets);
            return frame;
        }

        [TestMethod]
        public void FramesStillArriveInTheOrderTheyWereProduced()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, new InMemoryUserRepository("admin", "password"));
            var videoTrack = new H264Track(Sps, Pps);
            server.AddStreamSource(new RTSPStreamSource("stream1", videoTrack, null));
            server.StartListen();

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            using var client = Play(port, baseUri);

            // More than one turn of the pool, so that the connection is put down and picked up again
            // part way through - which is the moment a second thread could take it on and interleave
            // its writes with the first.
            const int Frames = 60;

            for (int i = 0; i < Frames; i++)
            {
                videoTrack.FeedInRawSamples((uint)(i * 3000), new List<ReadOnlyMemory<byte>>
                {
                    new ReadOnlyMemory<byte>(new byte[] { 0x65, 0x11, 0x22, 0x33 }),
                });
            }

            var sequenceNumbers = new List<int>();

            for (int i = 0; i < Frames; i++)
            {
                var frame = client.ReadInterleaved();

                if (frame.Channel != 0)
                {
                    continue;
                }

                sequenceNumbers.Add((frame.Payload[2] << 8) | frame.Payload[3]);
            }

            Assert.IsGreaterThan(10, sequenceNumbers.Count, "not enough RTP arrived to say anything");

            // Consecutive and ascending. Two threads on one connection would show up here as a pair
            // out of order, which is what a receiver would have to put right or drop.
            for (int i = 1; i < sequenceNumbers.Count; i++)
            {
                int expected = (sequenceNumbers[i - 1] + 1) & 0xFFFF;

                Assert.AreEqual(expected, sequenceNumbers[i],
                    $"packet {i} arrived out of order: {string.Join(", ", sequenceNumbers.Take(i + 1))}");
            }
        }
    }
}
