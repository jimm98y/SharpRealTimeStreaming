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
    /// Our client against our server.
    /// </summary>
    /// <remarks>
    /// Everything else here tests the client against a fake written alongside it, which agrees with
    /// whatever the client already does - so the two have never been run against each other, and a
    /// server change that broke the client would be found by whoever was using it rather than here.
    /// That is how a PLAY regression got out: the server started refusing the Range header that this
    /// client sends on every PLAY, and nothing noticed.
    /// </remarks>
    [TestClass]
    public sealed class EndToEndTests
    {
        /// <summary>SPS for a small H264 stream, enough for an SDP a client will accept.</summary>
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E, 0xD9, 0x00, 0xB0, 0x3D, 0xA1, 0x00, 0x00 };

        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        private static readonly byte[] HevcVps = { 0x40, 0x01, 0x0C, 0x01 };
        private static readonly byte[] HevcSps = { 0x42, 0x01, 0x01, 0x01, 0x60, 0x00, 0x00, 0x03 };
        private static readonly byte[] HevcPps = { 0x44, 0x01, 0xC1, 0x72, 0xB4, 0x62, 0x40 };

        /// <summary>
        /// A port nothing else is on. Asking for zero and reading back what was given is the only
        /// way that does not race with whoever takes the port between the check and the bind.
        /// </summary>
        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        [TestMethod]
        public void TheClientPlaysWhatTheServerSends()
        {
            int port = FreePort();

            using var server = new SharpRTSPServer.RTSPServer(port, new SharpRTSPServer.InMemoryUserRepository("admin", "password"));
            var video = new SharpRTSPServer.H264Track(Sps, Pps);
            server.AddStreamSource(new SharpRTSPServer.RTSPStreamSource("stream1", video, null));
            server.StartListen();

            using var client = new RTSPClient();

            int frames = 0;
            var arrived = new ManualResetEventSlim();

            client.ReceivedData += (s, e) =>
            {
                Interlocked.Increment(ref frames);
                arrived.Set();
            };

            client.Connect($"rtsp://127.0.0.1:{port}/stream1", RTPTransport.TCP, "admin", "password");
            client.Play();

            // An IDR, which is what the client's parser waits for before it will hand anything over.
            var idr = new byte[600];
            idr[0] = 0x65;

            var samples = new List<ReadOnlyMemory<byte>> { new ReadOnlyMemory<byte>(idr) };

            for (int i = 0; i < 100 && frames == 0; i++)
            {
                video.FeedInRawSamples((uint)((i + 1) * 3000), samples);
                Thread.Sleep(20);
            }

            Assert.IsTrue(arrived.Wait(TimeSpan.FromSeconds(5)),
                "the client received nothing the server sent");

            client.Stop();
        }

        /// <summary>
        /// A client left as it comes does not write a log line for every packet it receives.
        /// </summary>
        /// <remarks>
        /// The payload parsers report every NAL they see at trace level, and the sink is reached
        /// before anyone has said they want it - so with trace on by default, receiving a stream
        /// wrote a line per packet and formatted a string to do it. This is the direct form of what
        /// <see cref="ClientAllocationTests"/> can only see as a number.
        /// </remarks>
        /// <summary>
        /// An <see cref="ILog"/> that counts trace lines and keeps the default enablement.
        /// </summary>
        /// <remarks>
        /// Trace off and the rest on, as <see cref="DefaultLog"/> has them, because whether the
        /// default writes a line per packet is the whole question here.
        /// </remarks>
        private sealed class TraceCounter : ILog
        {
            private int _traces;

            public int Traces => Volatile.Read(ref _traces);

            public void LogTrace(string trace) { Interlocked.Increment(ref _traces); }

            public void LogError(string error) { }
            public void LogWarning(string warning) { }
            public void LogInfo(string info) { }
            public void LogDebug(string debug) { }

            public bool IsErrorEnabled { get; set; } = true;
            public bool IsWarningEnabled { get; set; } = true;
            public bool IsInfoEnabled { get; set; } = true;
            public bool IsDebugEnabled { get; set; } = true;
            public bool IsTraceEnabled { get; set; } = false;
        }

        [TestMethod]
        public void ReceivingDoesNotWriteALineForEveryPacket()
        {
            // The client's own logger, counting only what reaches trace level - the sink on
            // DefaultLog takes every level, and the handshake writes plenty at the others.
            // Deliberately left at the default enablement: the default is what is under test.
            var counting = new TraceCounter();

            {
                int port = FreePort();

                using var server = new SharpRTSPServer.RTSPServer(port, new SharpRTSPServer.InMemoryUserRepository("admin", "password"));
                // H265, because that is the parser which reports every NAL at trace level.
                var video = new SharpRTSPServer.H265Track(HevcVps, HevcSps, HevcPps);
                server.AddStreamSource(new SharpRTSPServer.RTSPStreamSource("stream1", video, null));
                server.StartListen();

                using var client = new RTSPClient { Logger = counting };

                int frames = 0;
                client.ReceivedData += (s, e) => Interlocked.Increment(ref frames);

                client.Connect($"rtsp://127.0.0.1:{port}/stream1", RTPTransport.TCP, "admin", "password");
                client.Play();

                var idr = new byte[600];
                idr[0] = 0x26;
                idr[1] = 0x01;
                var samples = new List<ReadOnlyMemory<byte>> { new ReadOnlyMemory<byte>(idr) };

                for (int i = 0; i < 60; i++)
                {
                    video.FeedInRawSamples((uint)((i + 1) * 3000), samples);
                    Thread.Sleep(5);
                }

                Thread.Sleep(300);
                client.Stop();

                Assert.IsGreaterThan(0, Volatile.Read(ref frames), "nothing arrived, so nothing was proved");
                Assert.AreEqual(0, counting.Traces,
                    "the client wrote trace lines while simply receiving a stream");
            }
        }
    }
}
