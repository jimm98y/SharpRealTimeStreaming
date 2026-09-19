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

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// Starting, stopping and disposing the server, including the ways a caller can do it wrong.
    /// </summary>
    [TestClass]
    public sealed class ServerLifecycleExtraTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        private static RTSPServer NewServer(int port)
        {
            var server = new RTSPServer(port, "admin", "password");
            server.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), null));
            return server;
        }

        [TestMethod]
        public void DisposingTwiceIsHarmless()
        {
            var server = NewServer(TestPorts.FindFree());
            server.StartListen();

            server.Dispose();
            server.Dispose(); // used to cancel a cancellation source it had already disposed
        }

        [TestMethod]
        public void StopListenAfterDisposeIsHarmless()
        {
            var server = NewServer(TestPorts.FindFree());
            server.StartListen();

            server.Dispose();
            server.StopListen();
        }

        [TestMethod]
        public void StopListenTwiceIsHarmless()
        {
            using var server = NewServer(TestPorts.FindFree());
            server.StartListen();

            server.StopListen();
            server.StopListen();
        }

        [TestMethod]
        public void StartingTwiceIsRejected()
        {
            using var server = NewServer(TestPorts.FindFree());
            server.StartListen();

            // used to leave two accept loops running, with no way to reach the first one again
            Assert.ThrowsExactly<InvalidOperationException>(() => server.StartListen());
        }

        [TestMethod]
        public void StartingAfterDisposeIsRejected()
        {
            var server = NewServer(TestPorts.FindFree());
            server.Dispose();

            Assert.ThrowsExactly<ObjectDisposedException>(() => server.StartListen());
        }

        [TestMethod]
        public void StopListenReleasesThePortSoTheServerCanBeRestarted()
        {
            int port = TestPorts.FindFree();
            using (var server = NewServer(port))
            {
                server.StartListen();
                using var client = new RtspTestClient(port, "admin", "password");
                Assert.AreEqual(200, client.Send("OPTIONS", "rtsp://127.0.0.1:" + port + "/stream1").StatusCode);

                // StopListen has to have actually waited for the accept loop, otherwise this races
                server.StopListen();
            }

            using var restarted = NewServer(port);
            restarted.StartListen();

            using var afterRestart = new RtspTestClient(port, "admin", "password");
            Assert.AreEqual(200, afterRestart.Send("OPTIONS", "rtsp://127.0.0.1:" + port + "/stream1").StatusCode);
        }
    }
}
