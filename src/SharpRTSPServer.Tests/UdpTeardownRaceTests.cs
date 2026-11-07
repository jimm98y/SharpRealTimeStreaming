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
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpSRTP.SRTP;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// Several UDP sessions ending at once, while media is still flowing to all of them.
    /// </summary>
    /// <remarks>
    /// Tearing a session down hands its UDP ports back at once, because there are only so many of
    /// them. That used to happen with no regard for the connection's writer, which by then had
    /// already read the transport it was about to send on - so the socket was disposed underneath a
    /// write in progress, and the write threw from inside the socket. Nothing was corrupted, but it
    /// is a first-chance exception per teardown, on a path that has nothing wrong with it.
    /// </remarks>
    [TestClass]
    // Runs the producer as hard as it will go, on purpose - the window this is about only opens
    // while a writer is inside a send. That leaves nothing for anyone else, so it does not share.
    [DoNotParallelize]
    public sealed class UdpTeardownRaceTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        private const int Clients = 5;
        private const int Rounds = 10;

        /// <summary>
        /// Collects what the server said, so the test can ask whether a write failed.
        /// </summary>
        /// <remarks>
        /// Watching for the exception itself is no good here: whether the write finds the transport
        /// stopped or disposed decides what it throws, and the two are different types. What is
        /// constant is that the write failed, and the server said the session had gone away.
        /// </remarks>
        private sealed class CapturingLoggerFactory : ILoggerFactory
        {
            private readonly List<string> _lines = new List<string>();

            public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);
            public void AddProvider(ILoggerProvider provider) { }
            public void Dispose() { }

            public IReadOnlyList<string> Lines
            {
                get { lock (_lines) { return _lines.ToArray(); } }
            }

            private sealed class CapturingLogger : ILogger
            {
                private readonly CapturingLoggerFactory _factory;

                public CapturingLogger(CapturingLoggerFactory factory) => _factory = factory;

                public IDisposable BeginScope<TState>(TState state) => null;
                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                    Func<TState, Exception, string> formatter)
                {
                    lock (_factory._lines)
                    {
                        _factory._lines.Add(formatter(state, exception));
                    }
                }
            }
        }

        [TestMethod]
        public void SessionsEndingWhileTheyAreBeingWrittenToDoNotThrowFromInsideTheWrite()
        {
            SessionsEndingWhileTheyAreBeingWrittenTo(protect: false);
        }

        /// <summary>
        /// The same, with the media protected - which is how it was met.
        /// </summary>
        /// <remarks>
        /// Protecting each packet is real work done inside the write, so a writer spends more of its
        /// time in the part of it that a teardown must not interrupt. It is the same race either
        /// way; encryption only makes it easier to lose.
        /// </remarks>
        [TestMethod]
        public void ProtectedSessionsEndingWhileTheyAreBeingWrittenToDoNotThrowEither()
        {
            SessionsEndingWhileTheyAreBeingWrittenTo(protect: true);
        }

        private static void SessionsEndingWhileTheyAreBeingWrittenTo(bool protect)
        {
            var logs = new CapturingLoggerFactory();

            int port = TestPorts.FindFree();

            using var server = protect
                ? new RTSPServer(port, new InMemoryUserRepository("admin", "password"), false, null,
                    SrtpCryptoSuites.AES_CM_128_HMAC_SHA1_80, logs)
                : new RTSPServer(port, new InMemoryUserRepository("admin", "password"), false, null, logs);

            server.SetRtpPortRange(53000, 53400);

            var videoTrack = protect
                ? new H264Track(Sps, Pps) { RtpProfile = RtpProfiles.SAVP }
                : new H264Track(Sps, Pps);
            server.AddStreamSource(new RTSPStreamSource("stream1", videoTrack, null));
            server.StartListen();

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            // As hard as the producer can go, and frames big enough to be many packets each, so a
            // writer is inside a send almost all of the time. The window this is about is one
            // transport reference, read and then used - at a gentle frame rate the writers are idle
            // when the teardowns arrive, and nothing races anything.
            var feeding = true;
            var feeder = Task.Run(() =>
            {
                uint timestamp = 0;

                while (Volatile.Read(ref feeding))
                {
                    videoTrack.FeedInRawSamples(timestamp += 3000, new List<ReadOnlyMemory<byte>>
                    {
                        new ReadOnlyMemory<byte>(new byte[120000]),
                    });
                }
            });

            try
            {
                for (int round = 0; round < Rounds; round++)
                {
                    var clients = new List<RtspTestClient>();
                    var sessions = new List<string>();

                    for (int i = 0; i < Clients; i++)
                    {
                        var client = new RtspTestClient(port, "admin", "password");
                        client.Send("OPTIONS", baseUri);
                        client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

                        var setup = client.Send("SETUP", baseUri + "/trackID=0",
                            "Transport: RTP/AVP;unicast;client_port="
                                + (41000 + i * 2) + "-" + (41001 + i * 2));
                        Assert.AreEqual(200, setup.StatusCode, $"client {i} should be set up");

                        Assert.AreEqual(200, client.Send("PLAY", baseUri, $"Session: {setup.Session}").StatusCode);

                        clients.Add(client);
                        sessions.Add(setup.Session);
                    }

                    // long enough for every one of them to be carrying media
                    Thread.Sleep(120);

                    // all of them at once, which is the case that produced this
                    Parallel.For(0, Clients, i =>
                    {
                        clients[i].Send("TEARDOWN", baseUri, $"Session: {sessions[i]}");
                        clients[i].Dispose();
                    });

                    Thread.Sleep(60);
                }
            }
            finally
            {
                Volatile.Write(ref feeding, false);
                feeder.Wait(TimeSpan.FromSeconds(10));
            }

            Thread.Sleep(300);

            // Every one of these sessions ended with a TEARDOWN it was answered on, so there is no
            // client here that went away - a write that failed did so because the transport was
            // taken from underneath it.
            var failed = logs.Lines
                .Where(line => line != null && line.Contains("has gone away"))
                .ToArray();

            Assert.IsEmpty(failed,
                $"{failed.Length} write(s) failed on sessions that were torn down in an orderly way:" +
                Environment.NewLine + string.Join(Environment.NewLine, failed.Take(5)));
        }

        [TestMethod]
        public void ThePortsStillComeBackAtOnce()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, new InMemoryUserRepository("admin", "password"));

            // one pair, so a second SETUP can only succeed if the first one's pair came back
            server.SetRtpPortRange(53400, 53402);

            var videoTrack = new H264Track(Sps, Pps);
            server.AddStreamSource(new RTSPStreamSource("stream1", videoTrack, null));
            server.StartListen();

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            using (var client = new RtspTestClient(port, "admin", "password"))
            {
                client.Send("OPTIONS", baseUri);
                client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

                var setup = client.Send("SETUP", baseUri + "/trackID=0",
                    "Transport: RTP/AVP;unicast;client_port=41200-41201");
                Assert.AreEqual(200, setup.StatusCode);
                Assert.AreEqual(200, client.Send("PLAY", baseUri, $"Session: {setup.Session}").StatusCode);

                videoTrack.FeedInRawSamples(3000, new List<ReadOnlyMemory<byte>>
                {
                    new ReadOnlyMemory<byte>(new byte[4000]),
                });

                client.Send("TEARDOWN", baseUri, $"Session: {setup.Session}");
            }

            // Waiting for the writer must not mean waiting to get the ports back - the range is
            // finite, and a server that hands them back late runs out under ordinary use.
            using var second = new RtspTestClient(port, "admin", "password");
            second.Send("OPTIONS", baseUri);
            second.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            var reused = second.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP;unicast;client_port=41200-41201");

            Assert.AreEqual(200, reused.StatusCode, "the only pair in the range should have come straight back");
            Assert.AreEqual(53400, int.Parse(reused.Match(@"server_port=(\d+)")));
        }
    }
}
