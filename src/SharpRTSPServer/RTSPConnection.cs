using Rtsp;
using System;

namespace SharpRTSPServer
{
    /// <summary>
    /// The RTSP client connection.
    /// </summary>
    public class RTSPConnection
    {
        /// <summary>
        /// RTSP conneciton listener.
        /// </summary>
        public RtspListener Listener { get; set; }

        /// <summary>
        /// Guards writing to this connection, so that what goes out on it goes out in one piece and
        /// in order.
        /// </summary>
        /// <remarks>
        /// One per connection, deliberately not the server wide list lock. Writing to a socket can
        /// block for as long as the client at the other end feels like not reading, and holding the
        /// list lock across that stopped every other connection, every RTSP request and every other
        /// stream until it finished.
        /// <para>
        /// Lock order: the connection list lock may be taken and then this one. Never the reverse -
        /// anything that needs the list after writing has to let go of this first.
        /// </para>
        /// </remarks>
        public object SendLock { get; } = new object();

        /// <summary>
        /// The transport the listener sits on. Kept so the server can notice that a client has gone
        /// away without tearing its session down, and release its RTP ports straight away.
        /// </summary>
        public IRtspTransport Transport { get; set; }

        /// <summary>
        /// True once the underlying connection has been closed by either end.
        /// </summary>
        public bool IsDisconnected
        {
            get
            {
                try
                {
                    return Transport != null && !Transport.Connected;
                }
                catch (ObjectDisposedException)
                {
                    return true;
                }
            }
        }

        // Time since last RTSP message received - used to spot dead UDP clients
        public DateTime TimeSinceLastRtspKeepAlive { get; private set; } = DateTime.UtcNow;

        /// <summary>
        /// Set to true when Session is in Play mode.
        /// </summary>
        public bool Play { get; set; }

        /// <summary>
        /// SSRC of whichever track was set up last on this connection.
        /// </summary>
        /// <remarks>
        /// A connection carries one RTP stream per track and each has its own SSRC, so a single value
        /// here can only ever describe one of them. Read <see cref="RTPStream.SSRC"/> off the stream
        /// you mean instead. Still assigned so that existing readers see what they always saw.
        /// </remarks>
        [Obsolete("A connection has one SSRC per stream. Use RTPStream.SSRC on Video, Audio or Streams[i].")]
        public uint SSRC { get; set; }

        /// <summary>
        /// RTSP Session ID used with this client connection.
        /// </summary>
        public string SessionId { get; set; } = "";

        /// <summary>
        /// Video stream.
        /// </summary>
        public RTPStream Video { get { return Streams[(int)TrackType.Video]; } }

        /// <summary>
        /// Audio stream.
        /// </summary>
        public RTPStream Audio { get { return Streams[(int)TrackType.Audio]; } }

        public RTPStream[] Streams { get; } = new RTPStream[]
        {
            new RTPStream(),
            new RTPStream()
        };

        /// <summary>
        /// Update the keepalive.
        /// </summary>
        public void UpdateKeepAlive()
        {
            TimeSinceLastRtspKeepAlive = DateTime.UtcNow;
        }
    }
}
