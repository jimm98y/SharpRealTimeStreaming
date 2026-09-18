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
        /// <summary>
        /// The group this stream is sent to, once anybody has asked for it that way.
        /// </summary>
        internal MulticastDelivery Multicast { get; set; }

        /// <summary>
        /// Whether this stream's SRTP keys belong to the stream rather than to each client.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Protecting media sent to a group needs one key that every member of the group holds,
        /// which is what RFC 4568 puts in the SDP and what RFC 3711 is built for. This server
        /// otherwise derives a key for each client, which is better where each client has media of
        /// its own and impossible where they are all listening to the same stream.
        /// </para>
        /// <para>
        /// It is off by default and worth understanding before turning on: every client that asks
        /// for this stream is handed the same key, so any of them can read what the others receive,
        /// and one that keeps the key can read the stream after its session is over. That is what a
        /// group is - the key is shared with whoever is allowed to listen - but it is a real change
        /// from a key that belongs to one client.
        /// </para>
        /// <para>
        /// Clients may be sent the stream one at a time as well as by group. What makes that safe is
        /// that each sender is given an SSRC of its own: SRTP works its keystream out from the key,
        /// the SSRC and the packet number, so senders that differ in their SSRC never share one. Were
        /// they all to send under the same SSRC, as they do when each holds a key of its own, two
        /// different packets would be protected by the same keystream and anyone holding both could
        /// recover both from the pair.
        /// </para>
        /// </remarks>
        public bool SharedSrtpKey { get; set; }

        /// <summary>
        /// The keys the group uses, one per track, derived once and handed to everyone.
        /// </summary>
        internal RTPStream[] GroupKeys { get; } = { new RTPStream(), new RTPStream() };

        /// <summary>
        /// Every SSRC this stream has sent under since its key was derived.
        /// </summary>
        /// <remarks>
        /// Not the ones in use - the ones ever used. Under a shared key, what must never repeat is
        /// the pair of SSRC and packet number: each sender numbers from the start of its own session,
        /// so handing a finished session's SSRC to a new one would send different media under the
        /// same keystream. They are therefore retired rather than returned. One entry costs four
        /// bytes and a session, which is not a rate anything grows at.
        /// </remarks>
        private readonly HashSet<uint> _ssrcsUsed = new HashSet<uint>();

        private readonly object _ssrcLock = new object();

        /// <summary>
        /// An SSRC no sender on this stream has used before.
        /// </summary>
        /// <remarks>
        /// What makes one key safe for several senders. SRTP works its keystream out from the key,
        /// the SSRC and the packet number, so senders that differ in their SSRC never share one -
        /// which is what lets clients that were all handed the same key be sent media separately.
        /// </remarks>
        internal uint ReserveSsrc()
        {
            lock (_ssrcLock)
            {
                while (true)
                {
                    uint candidate = RandomGenerator.NextUInt32();

                    // Zero is not used, so that it can go on meaning "none" where a stream has not
                    // been set up.
                    if (candidate != 0 && _ssrcsUsed.Add(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        /// <summary>
        /// Puts an SSRC beyond use on this stream, for one this stream did not choose.
        /// </summary>
        internal void ReserveSsrc(uint ssrc)
        {
            lock (_ssrcLock)
            {
                _ssrcsUsed.Add(ssrc);
            }
        }

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
