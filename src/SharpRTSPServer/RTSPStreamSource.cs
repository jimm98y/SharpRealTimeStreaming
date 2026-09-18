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

        /// <summary>
        /// Where a track has got to on its own RTP clock, if it has had anything to send.
        /// </summary>
        /// <remarks>
        /// Read from the track rather than kept here, so that it is the same number whether the
        /// media is being sent or merely produced.
        /// </remarks>
        internal bool TryGetLastRtpTimestamp(int streamType, out uint rtpTimestamp)
        {
            rtpTimestamp = 0;

            ITrack track =
                streamType == (int)TrackType.Video ? VideoTrack :
                streamType == (int)TrackType.Audio ? AudioTrack :
                null;

            // A track of someone else's making need not be one of ours, and then there is nowhere to
            // have read this from - which is a reason to say nothing, not to guess.
            if (!(track is TrackBase known) || !known.LastRtpTimestamp.HasValue)
            {
                return false;
            }

            rtpTimestamp = known.LastRtpTimestamp.Value;
            return true;
        }

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
        /// <summary>
        /// One media section of an SDP, as far as the server needs to understand it.
        /// </summary>
        internal sealed class MediaSection
        {
            /// <summary>The media type: "video", "audio", and whatever else an SDP may carry.</summary>
            public string Kind { get; set; }

            /// <summary>The "a=control" value a client addresses this track by, if it has one.</summary>
            public string Control { get; set; }

            /// <summary>Whether the section already carries an "a=crypto" attribute.</summary>
            public bool HasCrypto { get; set; }
        }

        /// <summary>
        /// Splits an SDP into its media sections.
        /// </summary>
        /// <remarks>
        /// Only media level attributes count. A session level "a=control:" says nothing about the
        /// individual tracks, and treating it as if it did left every media section without one.
        /// </remarks>
        internal static List<MediaSection> ParseMediaSections(string sdp)
        {
            var sections = new List<MediaSection>();

            if (string.IsNullOrEmpty(sdp))
            {
                return sections;
            }

            using (var textReader = new StringReader(sdp))
            {
                string line;
                while ((line = textReader.ReadLine()) != null)
                {
                    if (line.StartsWith("m="))
                    {
                        // "m=<media> <port> <proto> <fmt>"
                        string kind = line.Substring(2).Split(' ')[0];
                        sections.Add(new MediaSection { Kind = kind });
                    }
                    else if (sections.Count > 0)
                    {
                        MediaSection current = sections[sections.Count - 1];

                        if (line.StartsWith("a=control:"))
                        {
                            current.Control = line.Substring("a=control:".Length).Trim();
                        }
                        else if (line.StartsWith("a=crypto:"))
                        {
                            current.HasCrypto = true;
                        }
                    }
                }
            }

            return sections;
        }

        /// <summary>
        /// The value a client uses to address one of this source's tracks, as the SDP advertises it.
        /// </summary>
        /// <remarks>
        /// An overridden SDP brings its own control attributes, and they are not always
        /// "trackID=N" - cameras use "track1", "video", or a whole URL. Assuming the generated
        /// form meant SETUP could not find the track at all, and the RTP-Info of a PLAY named a URL
        /// the client had never used. The section is matched by its media type rather than its
        /// position, so an SDP that lists audio first is read the right way round.
        /// </remarks>
        public string GetTrackControl(TrackType trackType)
        {
            ITrack track = trackType == TrackType.Video ? VideoTrack : AudioTrack;
            if (track == null)
            {
                return null;
            }

            string kind = trackType == TrackType.Video ? "video" : "audio";

            foreach (MediaSection section in ParseMediaSections(Sdp))
            {
                if (string.Equals(section.Kind, kind, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(section.Control))
                {
                    return section.Control;
                }
            }

            return $"trackID={track.ID}";
        }

        /// <summary>
        /// Which of this source's tracks a section belongs to, or null for one that is neither.
        /// </summary>
        internal static TrackType? TrackTypeOf(MediaSection section)
        {
            if (string.Equals(section.Kind, "video", StringComparison.OrdinalIgnoreCase))
                return TrackType.Video;

            if (string.Equals(section.Kind, "audio", StringComparison.OrdinalIgnoreCase))
                return TrackType.Audio;

            return null;
        }

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
