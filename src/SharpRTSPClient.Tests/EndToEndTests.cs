using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using SharpRTSPClient;

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

            using var server = new SharpRTSPServer.RTSPServer(port, "admin", "password");
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
        [TestMethod]
        public void ReceivingDoesNotWriteALineForEveryPacket()
        {
            int traced = 0;

            var wasEnabled = Log.TraceEnabled;
            var oldSink = Log.SinkTrace;
            Log.SinkTrace = (m, ex) => Interlocked.Increment(ref traced);

            try
            {
                // deliberately not touching Log.TraceEnabled: the default is what is under test
                int port = FreePort();

                using var server = new SharpRTSPServer.RTSPServer(port, "admin", "password");
                // H265, because that is the parser which reports every NAL at trace level.
                var video = new SharpRTSPServer.H265Track(HevcVps, HevcSps, HevcPps);
                server.AddStreamSource(new SharpRTSPServer.RTSPStreamSource("stream1", video, null));
                server.StartListen();

                using var client = new RTSPClient();

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
                Assert.AreEqual(0, Volatile.Read(ref traced),
                    "the client wrote trace lines while simply receiving a stream");
            }
            finally
            {
                Log.SinkTrace = oldSink;
                Log.TraceEnabled = wasEnabled;
            }
        }
    }
}
