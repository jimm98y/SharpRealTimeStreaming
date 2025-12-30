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

        // Time since last RTSP message received - used to spot dead UDP clients
        public DateTime TimeSinceLastRtspKeepAlive { get; private set; } = DateTime.UtcNow;

        /// <summary>
        /// Set to true when Session is in Play mode.
        /// </summary>
        public bool Play { get; set; }

        /// <summary>
        /// SSRC value used with this client connection.
        /// </summary>
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
