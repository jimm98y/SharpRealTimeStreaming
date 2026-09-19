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

using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// A client going away is the ordinary end of a session, and is reported as such.
    /// </summary>
    /// <remarks>
    /// Players close, networks drop, people stop watching. It used to be three warnings and a stack
    /// trace every time, which buries the failures worth looking at among the ones that are not.
    /// </remarks>
    [TestClass]
    public sealed class DisconnectLoggingTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        private sealed class Entry
        {
            public LogLevel Level { get; set; }
            public string Message { get; set; }
            public Exception Exception { get; set; }
            public string Category { get; set; }
        }

        private sealed class CapturingLoggerFactory : ILoggerFactory
        {
            public List<Entry> Entries { get; } = new List<Entry>();

            public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);
            public void AddProvider(ILoggerProvider provider) { }
            public void Dispose() { }

            /// <summary>Only what the server itself said - the transport underneath it is not ours.</summary>
            public IEnumerable<Entry> FromTheServer =>
                Entries.Where(e => e.Category != null && e.Category.StartsWith("SharpRTSPServer"));

            private sealed class CapturingLogger : ILogger
            {
                private readonly CapturingLoggerFactory _factory;
                private readonly string _category;

                public CapturingLogger(CapturingLoggerFactory factory, string category)
                {
                    _factory = factory;
                    _category = category;
                }

                public IDisposable BeginScope<TState>(TState state) => null;
                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                    Func<TState, Exception, string> formatter)
                {
                    lock (_factory.Entries)
                    {
                        _factory.Entries.Add(new Entry
                        {
                            Level = logLevel,
                            Message = formatter(state, exception),
                            Exception = exception,
                            Category = _category,
                        });
                    }
                }
            }
        }

        [TestMethod]
        public void AClientThatVanishesIsNotReportedAsAFault()
        {
            var logs = new CapturingLoggerFactory();

            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password", false, null, logs);
            var videoTrack = new H264Track(Sps, Pps);
            server.AddStreamSource(new RTSPStreamSource("stream1", videoTrack, null));
            server.StartListen();

            string baseUri = "rtsp://127.0.0.1:" + port + "/stream1";

            var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");
            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            Assert.AreEqual(200, client.Send("PLAY", baseUri, "Session: " + setup.Session).StatusCode);

            // gone, without a TEARDOWN - a player being closed, or a network going away
            client.Dispose();
            Thread.Sleep(200);

            // keep feeding until the writes start failing
            for (int i = 0; i < 40; i++)
            {
                videoTrack.FeedInRawSamples((uint)(i * 3000), new List<ReadOnlyMemory<byte>>
                {
                    new ReadOnlyMemory<byte>(Idr())
                });
                Thread.Sleep(20);
            }

            Thread.Sleep(500);

            List<Entry> loud;
            lock (logs.Entries)
            {
                loud = logs.FromTheServer.Where(e => e.Level >= LogLevel.Warning).ToList();
            }

            Assert.IsEmpty(loud,
                "a client that closed its connection is not a fault: " +
                string.Join(" | ", loud.Select(e => e.Level + " " + e.Message)));
        }

        /// <summary>
        /// A picture a decoder could start on, which is what the server will now send to a client
        /// that has only just arrived. A NAL of zeroes is not one, and is not valid H264 either.
        /// </summary>
        private static byte[] Idr()
        {
            var nal = new byte[600];
            nal[0] = 0x65; // one NAL, IDR slice
            return nal;
        }

        [TestMethod]
        public void TheDepartureIsStillRecorded()
        {
            var logs = new CapturingLoggerFactory();

            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password", false, null, logs);
            var videoTrack = new H264Track(Sps, Pps);
            server.AddStreamSource(new RTSPStreamSource("stream1", videoTrack, null));
            server.StartListen();

            string baseUri = "rtsp://127.0.0.1:" + port + "/stream1";

            var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");
            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            client.Send("PLAY", baseUri, "Session: " + setup.Session);

            client.Dispose();
            Thread.Sleep(200);

            for (int i = 0; i < 40; i++)
            {
                videoTrack.FeedInRawSamples((uint)(i * 3000), new List<ReadOnlyMemory<byte>>
                {
                    new ReadOnlyMemory<byte>(Idr())
                });
                Thread.Sleep(20);
            }

            Thread.Sleep(500);

            bool saidSo;
            lock (logs.Entries)
            {
                // quiet is not the same as silent - it still has to be findable when someone looks
                saidSo = logs.FromTheServer.Any(e => e.Message != null && e.Message.Contains("has gone away"));
            }

            Assert.IsTrue(saidSo, "the session going should still be recorded, just not shouted about");
        }
    }
}
