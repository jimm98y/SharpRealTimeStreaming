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
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace SharpRTSPClient.Tests
{
    /// <summary>
    /// What the client allocates while it is receiving a stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured against the real server rather than a fake, because what is being weighed is the
    /// whole path a packet takes - off the socket, through the payload parser, out to whoever is
    /// listening - and a fake that writes bytes down a socket cannot stand in for that.
    /// </para>
    /// <para>
    /// Much of what is left is not ours: the payload parsers live in SharpRTSP and build a frame
    /// object and two arrays for every frame they finish. The bar here is set to catch our own half
    /// getting worse, and in particular to catch per-packet logging being turned back on by default
    /// - which is what it cost on the server, and costs about the same here.
    /// </para>
    /// </remarks>
    [TestClass]
    [DoNotParallelize]
    public sealed class ClientAllocationTests
    {
        private static readonly byte[] Vps = { 0x40, 0x01, 0x0C, 0x01 };
        private static readonly byte[] Sps = { 0x42, 0x01, 0x01, 0x01, 0x60, 0x00, 0x00, 0x03 };
        private static readonly byte[] Pps = { 0x44, 0x01, 0xC1, 0x72, 0xB4, 0x62, 0x40 };

        private const int FRAMES = 1000;

        /// <summary>
        /// What one packet may cost the client, receiving.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Measured repeatably at 665 to 671 bytes, nearly all of it inside SharpRTSP: a frame
        /// object and two array copies for every frame the payload parser finishes, and a boxed
        /// enumerator to ask whether it finished one. Our own share is about eighty, the two event
        /// arguments handed to whoever is listening.
        /// </para>
        /// <para>
        /// The bar is well above that because this is a whole-process measurement on whatever
        /// machine happens to run it, and well below what any of the things worth catching cost:
        /// H264's per-packet logging, which boxes six counters a frame, came to five hundred bytes
        /// a packet on its own.
        /// </para>
        /// </remarks>
        private const int MOST_BYTES_A_PACKET = 900;

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        [TestMethod]
        public void ReceivingAStreamAllocatesLittlePerPacket()
        {
            int port = FreePort();

            using var server = new SharpRTSPServer.RTSPServer(port, new SharpRTSPServer.InMemoryUserRepository("admin", "password"));
            var video = new SharpRTSPServer.H265Track(Vps, Sps, Pps);
            server.AddStreamSource(new SharpRTSPServer.RTSPStreamSource("stream1", video, null));
            server.StartListen();

            using var client = new RTSPClient();

            int frames = 0;
            client.ReceivedData += (s, e) => Interlocked.Increment(ref frames);

            client.Connect($"rtsp://127.0.0.1:{port}/stream1", RTPTransport.TCP, "admin", "password");
            client.Play();

            // An IDR, which is what the parser waits for before it hands anything over, small enough
            // to be one packet so that a packet and a frame are the same thing here.
            var idr = new byte[600];
            idr[0] = 0x26;
            idr[1] = 0x01;

            var samplesIn = new List<ReadOnlyMemory<byte>> { new ReadOnlyMemory<byte>(idr) };

            // The parsers, the pools and the writer threads all cost something the first time.
            for (int i = 0; i < 200; i++)
            {
                video.FeedInRawSamples((uint)((i + 1) * 3000), samplesIn);
                Thread.Sleep(2);
            }

            Thread.Sleep(300);

            // What the process allocates doing nothing at all, so that the timers and whatever else
            // ticks in the background are not charged to receiving.
            long idleFrom = GC.GetTotalAllocatedBytes(precise: true);
            Thread.Sleep(2000);
            double idleASecond = (GC.GetTotalAllocatedBytes(precise: true) - idleFrom) / 2.0;

            int from = Volatile.Read(ref frames);
            long before = GC.GetTotalAllocatedBytes(precise: true);

            for (int i = 0; i < FRAMES; i++)
            {
                video.FeedInRawSamples((uint)((i + 300) * 3000), samplesIn);
                Thread.Sleep(2);
            }

            Thread.Sleep(300);

            long busy = GC.GetTotalAllocatedBytes(precise: true) - before;
            int arrived = Volatile.Read(ref frames) - from;

            client.Stop();

            Assert.IsGreaterThan(FRAMES / 2, arrived, "too few packets arrived to say anything");

            // The loop above takes about as long as the idle stretch and a quarter.
            double aPacket = (busy - idleASecond * 2.5) / arrived;

            Assert.IsLessThan(MOST_BYTES_A_PACKET, aPacket,
                $"receiving allocated {aPacket:F0} bytes a packet over {arrived} packets");
        }
    }
}
