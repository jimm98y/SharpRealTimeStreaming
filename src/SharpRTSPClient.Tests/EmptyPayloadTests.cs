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
using System.Buffers;
using System.Reflection;
using Rtsp;
using Rtsp.Messages;

namespace SharpRTSPClient.Tests
{
    /// <summary>
    /// A datagram with nothing in it still has to be handed back.
    /// </summary>
    /// <remarks>
    /// Zero length datagrams turn up as NAT keepalives, and anyone who knows the port can send one.
    /// The receive handlers took ownership of the payload with a using, but the check for an empty
    /// one returned before that using was reached.
    /// </remarks>
    [TestClass]
    public sealed class EmptyPayloadTests
    {
        /// <summary>
        /// A memory owner that records whether it was disposed, standing in for the pooled buffer a
        /// real payload arrives in.
        /// </summary>
        private sealed class TrackedOwner : IMemoryOwner<byte>
        {
            private readonly byte[] _buffer;

            public TrackedOwner(int size)
            {
                _buffer = new byte[size];
            }

            public bool Disposed { get; private set; }

            public Memory<byte> Memory => _buffer.AsMemory(0, _buffer.Length);

            public void Dispose()
            {
                Disposed = true;
            }
        }

        /// <summary>
        /// Hands a payload to one of the two receive handlers, for a track of the given kind.
        /// </summary>
        /// <remarks>
        /// There used to be four of these, a pair per kind, and this named one of them. Both pairs
        /// are gone: the two that are left take the track the payload arrived on.
        /// </remarks>
        private static void Raise(RTSPClient client, string handlerName, TrackKind kind, RtspDataEventArgs args)
        {
            MethodInfo handler = typeof(RTSPClient).GetMethod(handlerName,
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.IsNotNull(handler, handlerName + " is not where the test expects it");

            ClientTrack track = client.AddTrack(kind);

            object[] arguments = handlerName == "RtpDataReceived"
                ? new object[] { track, args }
                : new object[] { track, null, args };

            handler.Invoke(client, arguments);
        }

        [TestMethod]
        [DataRow("RtpDataReceived", TrackKind.Video)]
        [DataRow("RtpDataReceived", TrackKind.Audio)]
        [DataRow("RtcpControlDataReceived", TrackKind.Video)]
        [DataRow("RtcpControlDataReceived", TrackKind.Audio)]
        public void AnEmptyPayloadIsStillDisposed(string handlerName, TrackKind kind)
        {
            using var client = new RTSPClient();

            var owner = new TrackedOwner(0);
            var data = new RtspData(owner, 0);

            Raise(client, handlerName, kind, new RtspDataEventArgs(data));

            Assert.IsTrue(owner.Disposed, handlerName + " dropped an empty payload without disposing it");
        }

        [TestMethod]
        [DataRow(TrackKind.Video)]
        [DataRow(TrackKind.Audio)]
        public void AShortPayloadIsStillDisposed(TrackKind kind)
        {
            using var client = new RTSPClient();

            // too short to be anything, so every parse of it gives up early
            var owner = new TrackedOwner(3);
            var data = new RtspData(owner, 3);

            Raise(client, "RtcpControlDataReceived", kind, new RtspDataEventArgs(data));

            Assert.IsTrue(owner.Disposed, "a short payload was dropped without being disposed");
        }
    }
}
