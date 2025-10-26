using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SharpRTSPServer
{
    public class RTSPStreamSource : IDisposable
    {
        private static readonly Random _rand = new Random();

        /// <summary>
        /// Stream ID.
        /// </summary>
        public string StreamID { get; }

        /// <summary>
        /// Video track. Must be set before starting the server.
        /// </summary>
        public ITrack VideoTrack { get; set; }

        /// <summary>
        /// Audio track.
        /// </summary>
        public ITrack AudioTrack { get; set; }

        /// <summary>
        /// SDP override.
        /// </summary>
        public string Sdp { get; private set; } = null;

        public HashSet<RTSPConnection> ConnectionList { get; } = new HashSet<RTSPConnection>(); // list of RTSP Listeners

        public RTSPStreamSource(string streamID, ITrack rtspVideoTrack, ITrack rtspAudioTrack)
        {
            if (string.IsNullOrWhiteSpace(streamID))
                throw new ArgumentNullException(nameof(streamID));

            StreamID = streamID;
            VideoTrack = rtspVideoTrack;
            AudioTrack = rtspAudioTrack;
        }

        public void OverrideSDP(string sdp, bool mungleSDP = true)
        {
            if (mungleSDP)
            {
                if (!sdp.Contains("a=control:"))
                {
                    StringBuilder builder = new StringBuilder();
                    int mediaIndex = 0;

                    // we have to fill in the trackID to identify the session in RTSP
                    using (var textReader = new StringReader(sdp))
                    {
                        while (true)
                        {
                            string line = textReader.ReadLine();

                            if (line == null)
                                break;

                            builder.AppendLine(line);

                            if (line.StartsWith("m="))
                            {
                                builder.AppendLine($"a=control:trackID={mediaIndex++}");
                            }
                        }
                    }

                    sdp = builder.ToString();
                }
            }

            this.Sdp = sdp;
        }

        private bool _disposedValue;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    if (VideoTrack != null && VideoTrack is IDisposable disposableVideoTrack)
                    {
                        disposableVideoTrack.Dispose();
                        VideoTrack = null;
                    }

                    if (AudioTrack != null && AudioTrack is IDisposable disposableAudioTrack)
                    {
                        disposableAudioTrack.Dispose();
                        AudioTrack = null;
                    }
                }

                _disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
