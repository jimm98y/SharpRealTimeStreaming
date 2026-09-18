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
        /// The next SSRC to hand a sender, and how many have been handed out under this key.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Counted up rather than drawn at random and remembered. Under one key what must never
        /// repeat is the pair of SSRC and packet number, and each sender numbers from the start of
        /// its own session - so an SSRC cannot be given out twice while the key stands, including to
        /// a session that begins after an earlier one has ended. Keeping every value ever used would
        /// say that correctly and grow without limit on a server that does not restart; counting says
        /// the same thing in four bytes, because everything before the counter has been used and
        /// everything from it has not.
        /// </para>
        /// <para>
        /// The count is what the key can cover. It resets when the key does, which is whenever the
        /// stream is left with nobody on it - see <see cref="ReleaseSharedSrtpKey"/>.
        /// </para>
        /// </remarks>
        private uint _nextSsrc;

        private long _ssrcsIssued;

        private bool _ssrcsSeeded;

        private readonly object _ssrcLock = new object();

        /// <summary>
        /// SSRCs that are spoken for and must not be handed to anyone: the tracks' own.
        /// </summary>
        private readonly HashSet<uint> _ssrcsSpokenFor = new HashSet<uint>();

        /// <summary>
        /// An SSRC no sender on this stream is using or has used under the key it holds now.
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
                if (!_ssrcsSeeded)
                {
                    // Started somewhere unguessable rather than at zero, since these go out on the
                    // wire and there is no reason to say how long this stream has been up.
                    _nextSsrc = RandomGenerator.NextUInt32();
                    _ssrcsSeeded = true;
                }

                // Every value the counter can take, less the handful the tracks have. Reaching this
                // needs four thousand million senders on one stream without it once being left
                // empty, and the honest answer at that point is to stop rather than to repeat one.
                if (_ssrcsIssued >= uint.MaxValue - _ssrcsSpokenFor.Count)
                {
                    throw new InvalidOperationException(
                        "This stream has no SSRC left to give out under the key it is using. It is " +
                        "re-keyed whenever no client is on it, so this means it has never been left " +
                        "empty for four thousand million sessions.");
                }

                while (true)
                {
                    uint candidate = _nextSsrc++;

                    // Zero goes on meaning "none", and a track's own belongs to whatever sends under
                    // it - the group, or a stream forwarding what it was given.
                    if (candidate != 0 && !_ssrcsSpokenFor.Contains(candidate))
                    {
                        _ssrcsIssued++;
                        return candidate;
                    }
                }
            }
        }

        /// <summary>
        /// Keeps an SSRC out of the hands of everything else on this stream.
        /// </summary>
        internal void ReserveSsrc(uint ssrc)
        {
            lock (_ssrcLock)
            {
                _ssrcsSpokenFor.Add(ssrc);
            }
        }

        /// <summary>
        /// Gives up the key this stream shares, and with it every SSRC handed out under it.
        /// </summary>
        /// <remarks>
        /// Called when the last client on the stream has gone. Nobody holds the key at that moment,
        /// so nothing is broken by replacing it - and once it is replaced, the SSRCs used under it
        /// mean nothing, because the keystream is worked out from the key as well. That is what stops
        /// a server that runs for years from slowly running out of them.
        /// <para>
        /// It is also the better thing to do with a key: it lasts as long as somebody is listening
        /// and no longer, rather than for the life of the process.
        /// </para>
        /// </remarks>
        internal void ReleaseSharedSrtpKey()
        {
            lock (_ssrcLock)
            {
                foreach (RTPStream key in GroupKeys)
                {
                    key.ResetSrtpContext();
                }

                _ssrcsSeeded = false;
                _ssrcsIssued = 0;
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
