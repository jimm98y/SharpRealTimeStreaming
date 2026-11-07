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

using System.Collections.Generic;

namespace SharpRTSPServer
{
    /// <summary>
    /// One stream sent once, to a group, for however many clients are listening to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unicast and multicast are shaped differently, and this is where the difference lives. A
    /// unicast client has media of its own: its own sockets, its own sequence numbering, its own
    /// SSRC, and a frame is written once per client watching. A multicast group has one of each, and
    /// a frame is written once however many are listening - which is the entire point of it. Several
    /// clients writing to one group would send every packet as many times as there were viewers, each
    /// stamping its own sequence numbers over the others.
    /// </para>
    /// <para>
    /// So the group gets a sender of its own. It is an <see cref="RTSPConnection"/> like any other -
    /// it has the streams, the send lock and the queue that the writing already knows what to do
    /// with - but no client is at the other end of it. Frames reach it because it is in the stream
    /// source's connection list; the clients that asked for multicast are in there too, and are
    /// passed over because they have no transport of their own.
    /// </para>
    /// <para>
    /// It lasts as long as somebody is listening. Each client that sets up a multicast track joins,
    /// each one that goes leaves, and when the last one has gone the group is shut down and its
    /// ports go back.
    /// </para>
    /// </remarks>
    internal sealed class MulticastDelivery
    {
        /// <summary>
        /// The pseudo connection the group's media is queued to and written from.
        /// </summary>
        public RTSPConnection Sender { get; set; }

        /// <summary>
        /// The group the media is sent to.
        /// </summary>
        public string GroupAddress { get; set; }

        /// <summary>
        /// The RTP port each track of the group is listened to on, by the track's ID. RTCP is the
        /// port after it, as it is everywhere else.
        /// </summary>
        /// <remarks>
        /// By ID rather than by kind, because a stream may carry more than one track of a kind and
        /// each needs a port of its own.
        /// </remarks>
        public Dictionary<int, int> RtpPort { get; } = new Dictionary<int, int>();

        /// <summary>
        /// The port a track of this group is listened to on, or zero if it has none.
        /// </summary>
        public int PortOf(int trackId) => RtpPort.TryGetValue(trackId, out int port) ? port : 0;

        /// <summary>
        /// The clients listening to this group. They receive nothing directly; they are counted, so
        /// that the group lasts exactly as long as somebody wants it.
        /// </summary>
        public HashSet<RTSPConnection> Listeners { get; } = new HashSet<RTSPConnection>();

        /// <summary>
        /// Whether a track has been set up for the group yet.
        /// </summary>
        public bool Carries(int trackId) => RtpPort.ContainsKey(trackId);
    }
}
