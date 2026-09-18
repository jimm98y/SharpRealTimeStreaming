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
        /// The RTP port of each track in the group, indexed by <see cref="TrackType"/>. RTCP is the
        /// port after it, as it is everywhere else.
        /// </summary>
        public int[] RtpPort { get; } = { 0, 0 };

        /// <summary>
        /// The clients listening to this group. They receive nothing directly; they are counted, so
        /// that the group lasts exactly as long as somebody wants it.
        /// </summary>
        public HashSet<RTSPConnection> Listeners { get; } = new HashSet<RTSPConnection>();

        /// <summary>
        /// Whether a track has been set up for the group yet.
        /// </summary>
        public bool Carries(TrackType trackType) => RtpPort[(int)trackType] != 0;
    }
}
