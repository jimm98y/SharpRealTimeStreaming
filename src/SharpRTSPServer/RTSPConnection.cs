using Rtsp;
using System;
using System.Collections.Generic;

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
        /// The media waiting to go out on this connection, and the thread that writes it. Created on
        /// the first frame and let go when the session is removed.
        /// </summary>
        internal OutboundQueue Outbound { get; set; }

        /// <summary>
        /// Scratch space for the packets of the frame being written, reused between frames.
        /// </summary>
        /// <remarks>
        /// Only ever touched while <see cref="SendLock"/> is held, which is what makes reusing it
        /// safe - one thread writes a connection at a time.
        /// </remarks>
        internal List<Memory<byte>> PacketsToSend { get; } = new List<Memory<byte>>();

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

        private volatile bool _play;

        /// <summary>
        /// Set to true when Session is in Play mode.
        /// </summary>
        /// <remarks>
        /// Volatile because the thread producing media reads it without taking any lock - taking one
        /// is what it must not do, since the lock it would want is held across writes to this client.
        /// PLAY sets it before it writes its reply, so a producer that sees it set has its media
        /// queued behind that reply rather than ahead of it.
        /// </remarks>
        public bool Play
        {
            get { return _play; }
            set { _play = value; }
        }

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
        public RTPStream Video { get { return StreamFor((int)TrackType.Video); } }

        /// <summary>
        /// Audio stream.
        /// </summary>
        public RTPStream Audio { get { return StreamFor((int)TrackType.Audio); } }

        private RTPStream[] _streams = new RTPStream[0];

        private readonly object _streamsLock = new object();

        /// <summary>
        /// The streams this connection has set up, indexed by the ID of the track each carries.
        /// </summary>
        /// <remarks>
        /// Read without locking, and grown by replacing the array rather than adding to it, so that
        /// the media path always sees one whole version of it. A connection that set up only the
        /// track with ID 3 has four of these, the first three unused - which costs nothing and means
        /// the index is always the track's own ID.
        /// </remarks>
        public RTPStream[] Streams => _streams;

        /// <summary>
        /// This connection's stream for a track, made if this is the first time it is asked for.
        /// </summary>
        public RTPStream StreamFor(int trackId)
        {
            if (trackId < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(trackId), trackId, "A track ID is not negative.");
            }

            lock (_streamsLock)
            {
                if (trackId >= _streams.Length)
                {
                    var grown = new RTPStream[trackId + 1];
                    Array.Copy(_streams, grown, _streams.Length);

                    for (int i = _streams.Length; i < grown.Length; i++)
                    {
                        grown[i] = new RTPStream();
                    }

                    _streams = grown;
                }

                return _streams[trackId];
            }
        }

        /// <summary>
        /// This connection's stream for a track, or null if it never set that one up.
        /// </summary>
        public RTPStream StreamOrNull(int trackId)
        {
            RTPStream[] streams = _streams;

            return trackId >= 0 && trackId < streams.Length ? streams[trackId] : null;
        }

        /// <summary>
        /// Update the keepalive.
        /// </summary>
        public void UpdateKeepAlive()
        {
            TimeSinceLastRtspKeepAlive = DateTime.UtcNow;
        }
    }
}
