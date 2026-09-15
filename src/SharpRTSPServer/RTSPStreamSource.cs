using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SharpRTSPServer
{
    public class RTSPStreamSource : IDisposable
    {
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
            if (sdp == null)
                throw new ArgumentNullException(nameof(sdp));

            if (mungleSDP)
            {
                sdp = AddMissingTrackControlAttributes(sdp);
            }

            this.Sdp = sdp;
        }

        /// <summary>
        /// Gives every media section an "a=control:trackID=N" attribute, which is what SETUP uses to
        /// identify the track. Sections that already have one are left as they are, and an SDP that
        /// needs nothing added is returned untouched.
        /// </summary>
        /// <remarks>
        /// Only media level attributes count. A session level "a=control:" says nothing about the
        /// individual tracks, and treating it as if it did left every media section without one.
        /// </remarks>
        private static string AddMissingTrackControlAttributes(string sdp)
        {
            var lines = new List<string>();
            using (var textReader = new StringReader(sdp))
            {
                string line;
                while ((line = textReader.ReadLine()) != null)
                {
                    lines.Add(line);
                }
            }

            // work out which media sections already carry a control attribute
            var mediaHasControl = new List<bool>();
            foreach (string line in lines)
            {
                if (line.StartsWith("m="))
                {
                    mediaHasControl.Add(false);
                }
                else if (line.StartsWith("a=control:") && mediaHasControl.Count > 0)
                {
                    mediaHasControl[mediaHasControl.Count - 1] = true;
                }
            }

            if (mediaHasControl.Count == 0 || mediaHasControl.TrueForAll(hasControl => hasControl))
            {
                // nothing to add, so hand back exactly what we were given rather than reformatting it
                return sdp;
            }

            // SDP lines are CRLF terminated per RFC 4566
            const string LineEnding = "\r\n";

            StringBuilder builder = new StringBuilder();
            int mediaSection = -1;

            foreach (string line in lines)
            {
                builder.Append(line).Append(LineEnding);

                if (line.StartsWith("m="))
                {
                    mediaSection++;
                    if (!mediaHasControl[mediaSection])
                    {
                        builder.Append($"a=control:trackID={mediaSection}").Append(LineEnding);
                    }
                }
            }

            return builder.ToString();
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
