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
        private readonly List<ITrack> _tracks = new List<ITrack>();

        /// <summary>
        /// Every track this stream carries, in the order they were added.
        /// </summary>
        /// <remarks>
        /// A stream is not one video track and one audio track. It is however many tracks it has:
        /// two languages, two qualities, a picture and the data describing what is in it. Which
        /// track a request is about is the track's <see cref="ITrack.ID"/>, which is what its control
        /// URL names and what the media path indexes by; what sort of thing is in it is its
        /// <see cref="ITrack.Kind"/>, and several tracks may share one.
        /// </remarks>
        public IReadOnlyList<ITrack> Tracks => _tracks;

        /// <summary>
        /// The first video track, if there is one.
        /// </summary>
        /// <remarks>
        /// Here because a stream with one of each is the ordinary case and this is how it was always
        /// reached. Where a stream carries more than one, <see cref="Tracks"/> is what sees them all.
        /// </remarks>
        public ITrack VideoTrack
        {
            get => FirstOf(TrackType.Video);
            set => Replace(TrackType.Video, value);
        }

        /// <summary>
        /// Audio track.
        /// </summary>
        public ITrack AudioTrack
        {
            get => FirstOf(TrackType.Audio);
            set => Replace(TrackType.Audio, value);
        }

        /// <summary>
        /// The first metadata track, if there is one.
        /// </summary>
        public ITrack MetadataTrack
        {
            get => FirstOf(TrackType.Metadata);
            set => Replace(TrackType.Metadata, value);
        }

        /// <summary>
        /// Adds a track to this stream.
        /// </summary>
        /// <remarks>
        /// The track's ID is how everything else refers to it - the control URL clients set up, and
        /// the number the media path is fed against - so two tracks on one stream cannot share one.
        /// </remarks>
        public void AddTrack(ITrack track)
        {
            if (track == null)
                throw new ArgumentNullException(nameof(track));

            if (_tracks.Contains(track))
                return;

            ITrack clash = TrackById(track.ID);

            if (clash != null)
            {
                throw new ArgumentException(
                    $"This stream already has a track with ID {track.ID} ({clash.Codec}). A track's ID " +
                    "is what its control URL names, so two tracks on one stream must not share one.",
                    nameof(track));
            }

            _tracks.Add(track);
        }

        /// <summary>
        /// Takes a track off this stream.
        /// </summary>
        public bool RemoveTrack(ITrack track) => track != null && _tracks.Remove(track);

        /// <summary>
        /// The track with this ID, or null if the stream has none.
        /// </summary>
        public ITrack TrackById(int id)
        {
            foreach (ITrack track in _tracks)
            {
                if (track.ID == id)
                {
                    return track;
                }
            }

            return null;
        }

        private ITrack FirstOf(TrackType kind)
        {
            foreach (ITrack track in _tracks)
            {
                if (track.Kind == kind)
                {
                    return track;
                }
            }

            return null;
        }

        /// <summary>
        /// Puts a track in the place of the first one of its kind, for the properties that name one.
        /// </summary>
        private void Replace(TrackType kind, ITrack track)
        {
            ITrack existing = FirstOf(kind);

            if (existing != null)
            {
                int at = _tracks.IndexOf(existing);

                if (track == null)
                {
                    _tracks.RemoveAt(at);
                }
                else
                {
                    _tracks[at] = track;
                }

                return;
            }

            if (track != null)
            {
                AddTrack(track);
            }
        }

        /// <summary>
        /// SDP override.
        /// </summary>
        public string Sdp { get; private set; } = null;

        public HashSet<RTSPConnection> ConnectionList { get; } = new HashSet<RTSPConnection>(); // list of RTSP Listeners

        /// <summary>
        /// Whether this stream has ever produced a picture a decoder could start on.
        /// </summary>
        /// <remarks>
        /// Read by every connection watching, to decide whether holding a picture back is waiting
        /// for something that happens or just waiting. Set once and never cleared: a stream that has
        /// produced one keyframe produces more.
        /// </remarks>
        internal bool HasProducedKeyFrame { get; set; }

        /// <summary>
        /// Whether to keep the last picture a decoder could start on, to show a client that would
        /// otherwise have nothing.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Off by default, and a fallback rather than a first choice. A client joining a stream
        /// mid-group has nothing it can decode until the next keyframe; asking the encoder for one
        /// - see <see cref="RTSPServer.KeyFrameNeeded"/> - ends that wait properly and at no cost.
        /// This is for when there is nothing to ask, or nothing answers: after
        /// <see cref="RTSPServer.KeyFrameWait"/> has passed with no live keyframe, the client is
        /// sent the last one this stream produced.
        /// </para>
        /// <para>
        /// What it buys is a real picture immediately instead of a blank one. What it does not buy
        /// is a clean stream: the live pictures that follow it refer to ones between that keyframe
        /// and now, which the client still never saw, so it decodes them imperfectly until a real
        /// keyframe comes round. That is the trade - something wrong-but-visible against nothing at
        /// all - and it is why this is a choice rather than the behaviour.
        /// </para>
        /// <para>
        /// Costs one frame of memory per stream, whatever the audience.
        /// </para>
        /// </remarks>
        public bool KeepLastKeyFrame { get; set; }

        /// <summary>
        /// The last picture a decoder could start on, held for <see cref="KeepLastKeyFrame"/>.
        /// </summary>
        private QueuedFrame _lastKeyFrame;

        private readonly object _lastKeyFrameGate = new object();

        /// <summary>
        /// Keeps this frame as the one to fall back on, letting go of the one before it.
        /// </summary>
        internal void KeepAsLastKeyFrame(QueuedFrame frame)
        {
            frame.AddRef();

            QueuedFrame previous;

            lock (_lastKeyFrameGate)
            {
                previous = _lastKeyFrame;
                _lastKeyFrame = frame;
            }

            previous?.Release();
        }

        /// <summary>
        /// A share of the last picture a decoder could start on, or null if none is being kept.
        /// </summary>
        /// <remarks>
        /// The caller claims its own share; this one belongs to the stream until something replaces
        /// it.
        /// </remarks>
        internal QueuedFrame LastKeyFrameOrNull()
        {
            lock (_lastKeyFrameGate)
            {
                return _lastKeyFrame;
            }
        }

        internal void ForgetLastKeyFrame()
        {
            QueuedFrame previous;

            lock (_lastKeyFrameGate)
            {
                previous = _lastKeyFrame;
                _lastKeyFrame = null;
            }

            previous?.Release();
        }
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
        /// What moves this stream about in time, where anything can.
        /// </summary>
        /// <remarks>
        /// Set by whoever owns the media, since only they know where it comes from. Left null for a
        /// live stream: it plays from wherever it is, a client asking for anywhere else is told it
        /// cannot have it, and pausing stops the sending without stopping the world.
        /// </remarks>
        public IPlaybackControl PlaybackControl { get; set; }

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
        private readonly Dictionary<int, RTPStream> _groupKeys = new Dictionary<int, RTPStream>();

        /// <summary>
        /// The key a track's group uses, derived once and handed to everyone listening.
        /// </summary>
        internal RTPStream GroupKey(int trackId)
        {
            lock (_groupKeyLock)
            {
                if (!_groupKeys.TryGetValue(trackId, out RTPStream key))
                {
                    key = new RTPStream();
                    _groupKeys[trackId] = key;
                }

                return key;
            }
        }

        private readonly object _groupKeyLock = new object();

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
                lock (_groupKeyLock)
                {
                    foreach (RTPStream key in _groupKeys.Values)
                    {
                        key.ResetSrtpContext();
                    }
                }

                _ssrcsSeeded = false;
                _ssrcsIssued = 0;
            }
        }

        internal bool TryGetLastRtpTimestamp(int streamType, out uint rtpTimestamp)
        {
            rtpTimestamp = 0;

            ITrack track = TrackById(streamType);

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
            : this(streamID, Without(null, rtspVideoTrack, rtspAudioTrack))
        {
        }

        /// <param name="streamID">What a client names this stream by in a request URI.</param>
        /// <param name="tracks">The tracks this stream carries, in the order they belong in the SDP.</param>
        public RTSPStreamSource(string streamID, params ITrack[] tracks)
            : this(streamID, (IEnumerable<ITrack>)tracks)
        {
        }

        public RTSPStreamSource(string streamID, IEnumerable<ITrack> tracks)
        {
            if (string.IsNullOrWhiteSpace(streamID))
                throw new ArgumentNullException(nameof(streamID));

            StreamID = streamID;

            if (tracks == null)
            {
                return;
            }

            foreach (ITrack track in tracks)
            {
                if (track != null)
                {
                    AddTrack(track);
                }
            }
        }

        private static IEnumerable<ITrack> Without(ITrack _, params ITrack[] tracks) => tracks;

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
        /// <summary>
        /// The control URL of the first track of a kind, for streams that carry one of each.
        /// </summary>
        public string GetTrackControl(TrackType trackType)
        {
            ITrack track = FirstOf(trackType);

            return track == null ? null : GetTrackControl(track);
        }

        /// <summary>
        /// The control URL a client uses to set up this particular track.
        /// </summary>
        /// <remarks>
        /// Taken from the description where there is one, since an overridden SDP says what the
        /// tracks of the stream it describes are called and a client will use what it was given.
        /// </remarks>
        public string GetTrackControl(ITrack track)
        {
            if (track == null)
            {
                return null;
            }

            List<MediaSection> sections = ParseMediaSections(Sdp);
            List<ITrack> describes = TrackOfEachSection(sections);

            for (int i = 0; i < sections.Count; i++)
            {
                if (ReferenceEquals(describes[i], track) && !string.IsNullOrEmpty(sections[i].Control))
                {
                    return sections[i].Control;
                }
            }

            return $"trackID={track.ID}";
        }

        /// <summary>
        /// Which track each media section of a description is about.
        /// </summary>
        /// <remarks>
        /// By kind first and then by order within that kind: the second audio section describes the
        /// second audio track. Position alone is not enough, because a description is free to put
        /// the audio before the video and one that does would give every track the other one's
        /// control URL and keys. Kind alone is not enough either, once a stream can carry two tracks
        /// of one kind. A section describing something this stream has no track for maps to null.
        /// </remarks>
        internal List<ITrack> TrackOfEachSection(List<MediaSection> sections)
        {
            var taken = new HashSet<ITrack>();
            var describes = new List<ITrack>(sections.Count);

            foreach (MediaSection section in sections)
            {
                TrackType? kind = TrackTypeOf(section);
                ITrack match = null;

                if (kind != null)
                {
                    foreach (ITrack track in _tracks)
                    {
                        if (track.Kind == kind.Value && !taken.Contains(track))
                        {
                            match = track;
                            taken.Add(track);
                            break;
                        }
                    }
                }

                describes.Add(match);
            }

            return describes;
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

            if (string.Equals(section.Kind, "application", StringComparison.OrdinalIgnoreCase))
                return TrackType.Metadata;

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
                    foreach (ITrack track in _tracks.ToArray())
                    {
                        if (track is IDisposable disposableTrack)
                        {
                            disposableTrack.Dispose();
                        }
                    }

                    _tracks.Clear();

                    // The frame kept to fall back on holds pooled buffers, which are nobody's until
                    // it lets go of them.
                    ForgetLastKeyFrame();
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
