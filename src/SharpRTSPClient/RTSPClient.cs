// SharpRTSPClient
// Copyright (C) 2026 Lukas Volf
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using Microsoft.Extensions.Logging;
using Rtsp;
using Rtsp.Messages;
using Rtsp.Onvif;
using Rtsp.Rtp;
using Rtsp.Sdp;
using SharpSRTP.SRTP;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Numerics;
using System.Text;

namespace SharpRTSPClient
{
    public enum RTPTransport
    {
        UDP,
        TCP,
        MULTICAST
    }

    /// <summary>
    /// RTSP client.
    /// </summary>
    public class RTSPClient : IDisposable
    {
        /// <summary>
        /// Source of the SSRCs this client reports under.
        /// </summary>
        /// <remarks>
        /// A shared <see cref="Random"/> used to serve for this. It is not safe to use from several
        /// threads - two clients constructed at once can leave its state such that it returns zero
        /// for ever after - and these values go on the wire, where the less they say about the
        /// process that made them the better.
        /// </remarks>
        private static readonly System.Security.Cryptography.RandomNumberGenerator _rng =
            System.Security.Cryptography.RandomNumberGenerator.Create();

        /// <summary>
        /// A random SSRC, from the whole of the range RTP allows.
        /// </summary>
        /// <remarks>
        /// The whole range rather than a few thousand values, because what an SSRC has to do is not
        /// collide with the other sources in the session - and the narrow bands these used to be
        /// drawn from made that likelier for no gain.
        /// </remarks>
        private static uint NextSsrc()
        {
            byte[] raw = new byte[sizeof(uint)];
            _rng.GetBytes(raw);
            uint ssrc = BitConverter.ToUInt32(raw, 0);

            // zero goes on meaning "none" in the places this is compared against
            return ssrc == 0 ? 1u : ssrc;
        }

        private readonly ILogger _logger;
        private readonly ILoggerFactory _loggerFactory;

        // NewVideoStream, NewAudioStream, ReceivedVideoData and ReceivedAudioData used to live
        // here. Each reported the first track of its kind, from when a stream was one video and one
        // audio - so a stream offering two qualities or two languages raised them for one of the
        // pair and silently never mentioned the other. NewTrack and ReceivedData report every
        // track and say which.

        /// <summary>
        /// Media from any track of the stream, whatever kind it is and however many there are.
        /// </summary>
        /// <remarks>
        /// The video and audio events above report the first track of each kind, which is what a
        /// stream usually has and how this client has always been driven. This one reports every
        /// track, and says which it is - so a second audio track, or the data describing what is in
        /// the picture, reaches whoever asked for it.
        /// </remarks>
        public event EventHandler<TrackDataEventArgs> ReceivedData;

        /// <summary>
        /// Raised once per track the stream offers, as the description is read.
        /// </summary>
        public event EventHandler<NewTrackEventArgs> NewTrack;

        /// <summary>
        /// Accepts the first track of each kind and no more, which is what this client does when
        /// nothing else is asked for.
        /// </summary>
        /// <remarks>
        /// Handy to compose with: <c>AcceptTrack = t =&gt; RTSPClient.FirstOfEachKind(t) &amp;&amp;
        /// t.Kind != TrackKind.Application;</c>
        /// </remarks>
        public static readonly Func<TrackOffer, bool> FirstOfEachKind = offer => offer.AcceptedOfThisKind == 0;

        /// <summary>
        /// Decides which of the tracks a description offers this client sets up.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Asked once per track, in the order the tracks are set up, and only for tracks this client
        /// could actually play. Return false and the track is passed over: no transport is bound for
        /// it, no SETUP is sent, and the server never sends it. There is no undoing that later in
        /// the session, so this is a question about the description rather than about the media.
        /// </para>
        /// <para>
        /// <see cref="FirstOfEachKind"/> when nothing is set, which is the first video track, the
        /// first audio track and the first metadata track. Some others:
        /// </para>
        /// <code>
        /// client.AcceptTrack = _ =&gt; true;                                  // everything on offer
        /// client.AcceptTrack = t =&gt; t.Kind == TrackKind.Video;              // pictures only
        /// client.AcceptTrack = t =&gt; t.Codec == "H265";                      // the H265 one
        /// client.AcceptTrack = t =&gt; t.AcceptedSoFar &lt; 2;                // the first two, whatever they are
        /// </code>
        /// <para>
        /// It runs on the RTSP receive thread while the description is being read, so a predicate
        /// that blocks holds the handshake up. One that throws is logged and the track passed over -
        /// a filter that cannot decide must not be a way to pull a track in by accident.
        /// </para>
        /// <para>
        /// This replaced ReceiveAllTracks and a MediaRequest of VIDEO_ONLY, AUDIO_ONLY or
        /// VIDEO_AND_AUDIO. Between them those said which kinds and how many of each and nothing
        /// else; the metadata tracks were reachable only by turning every other extra track on too.
        /// </para>
        /// </remarks>
        public Func<TrackOffer, bool> AcceptTrack { get; set; }

        /// <summary>
        /// Default value of <see cref="MaxTracks"/>.
        /// </summary>
        public const int DEFAULT_MAX_TRACKS = 16;

        /// <summary>
        /// The most tracks this client will take from one description.
        /// </summary>
        /// <remarks>
        /// The description comes from the server, and every track taken from it costs a pair of UDP
        /// ports or a pair of interleaved channels. Without a bound, a server - or anything able to
        /// rewrite an unencrypted DESCRIBE on its way here - could name enough of them to use up the
        /// whole of <see cref="SetRtpPortRange"/>, and in TCP mode to run the interleaved channel
        /// number past the single byte that carries it, where it wraps and two tracks collide.
        /// <para>
        /// Sixteen is far more than any real stream offers. Raise it for one that genuinely carries
        /// more, together with the port range.
        /// </para>
        /// </remarks>
        public int MaxTracks { get; set; } = DEFAULT_MAX_TRACKS;

        /// <summary>
        /// Whether another track can be taken on at all, whatever the filter says.
        /// </summary>
        /// <remarks>
        /// <see cref="MaxTracks"/> is a bound on what a description can make this client spend, not
        /// a preference - so it is checked before the filter is asked, and a filter that says yes to
        /// everything still cannot take more than this.
        /// </remarks>
        private bool CanTakeAnotherTrack(string what)
        {
            int count;
            lock (_tracksLock)
            {
                count = _tracks.Count;
            }

            if (MaxTracks <= 0 || count < MaxTracks)
            {
                return true;
            }

            if (count == MaxTracks)
            {
                _logger.LogWarning(
                    "Ignoring the {what} stream and anything after it: this description offers more than the {max} tracks this client will set up",
                    what, MaxTracks);
            }

            return false;
        }

        /// <summary>
        /// Puts one track to <see cref="AcceptTrack"/>.
        /// </summary>
        /// <param name="candidate">
        /// The track being offered. It is already on the list - the description has to be read into
        /// something before there is anything to decide about - so it is left out of the counts
        /// below, which are about the tracks accepted before it.
        /// </param>
        /// <param name="codec">The codec the description named, or empty where it named none.</param>
        /// <param name="payloadType">The RTP payload type.</param>
        /// <param name="descriptionIndex">Which media section of the description this is.</param>
        private bool IsWanted(ClientTrack candidate, string codec, int payloadType, int descriptionIndex)
        {
            Func<TrackOffer, bool> filter = AcceptTrack ?? FirstOfEachKind;

            TrackKind kind = candidate.Kind;
            int acceptedSoFar = 0;
            int acceptedOfThisKind = 0;

            lock (_tracksLock)
            {
                foreach (ClientTrack accepted in _tracks)
                {
                    if (ReferenceEquals(accepted, candidate))
                    {
                        continue;
                    }

                    acceptedSoFar++;

                    if (accepted.Kind == kind)
                    {
                        acceptedOfThisKind++;
                    }
                }
            }

            var offer = new TrackOffer(descriptionIndex, kind, codec ?? string.Empty, payloadType,
                acceptedSoFar, acceptedOfThisKind);

            bool wanted;

            try
            {
                wanted = filter(offer);
            }
            catch (Exception ex)
            {
                // A filter that cannot decide must not be a way to pull a track in by accident, so
                // this refuses rather than defaulting to yes.
                _logger.LogError(ex, "An AcceptTrack filter threw for {offer}, passing the track over", offer);
                return false;
            }

            if (!wanted)
            {
                _logger.LogDebug("Passing over {offer}, the filter did not want it", offer);
            }

            return wanted;
        }

        /// <summary>
        /// Which media section of the description this media is, for the offer.
        /// </summary>
        private static int SectionIndexOf(SdpFile sdp, Media media)
        {
            for (int i = 0; i < sdp.Medias.Count; i++)
            {
                if (ReferenceEquals(sdp.Medias[i], media))
                {
                    return i;
                }
            }

            return -1;
        }
        public event EventHandler<StoppedEventArgs> Stopped;

        public bool ProcessRTCP { get; set; } = true; // answer RTCP

        /// <summary>
        /// Every RTP packet that arrives, on whichever track, before it is parsed into frames.
        /// </summary>
        /// <remarks>
        /// Every track, and saying which - so a second track of a kind, or one that is neither
        /// sound nor pictures, can be told apart from the first. There used to be a pair of these
        /// per kind, reporting the first video track and the first audio track and nothing else.
        /// </remarks>
        public event EventHandler<TrackRawRtpEventArgs> ReceivedRawRTP;

        /// <summary>
        /// Every RTCP packet that arrives, on whichever track.
        /// </summary>
        /// <remarks>
        /// The counterpart of <see cref="ReceivedRawRTP"/> for the reports.
        /// </remarks>
        public event EventHandler<TrackRawRtcpEventArgs> ReceivedRawRTCP;

        /// <summary>
        /// Default value of <see cref="ReceiverReportInterval"/>.
        /// </summary>
        public static readonly TimeSpan DEFAULT_RECEIVER_REPORT_INTERVAL = TimeSpan.FromSeconds(5);

        /// <summary>
        /// How long to leave between the receiver reports this client sends back.
        /// <see cref="DEFAULT_RECEIVER_REPORT_INTERVAL"/> by default.
        /// </summary>
        /// <remarks>
        /// One used to go back for every sender report that arrived, so a server that reported often
        /// - one per frame, in the case of this project's own server until recently - was answered
        /// just as often. Set it to zero to go back to answering every one.
        /// </remarks>
        public TimeSpan ReceiverReportInterval { get; set; } = DEFAULT_RECEIVER_REPORT_INTERVAL;
        // ReceivedRawVideoRTCP and ReceivedRawAudioRTCP were here; ReceivedRawRTCP replaces them.

        public bool ProcessRTP { get; set; } = true;
        // ReceivedRawVideoRTP and ReceivedRawAudioRTP were here; ReceivedRawRTP replaces them.

        public bool AutoPlay { get; set; } = true;

        /// <summary>
        /// Default value of <see cref="RtpPortRangeStart"/>.
        /// </summary>
        /// <remarks>
        /// Deliberately above the range SharpRTSPServer allocates from, so a client and a server
        /// sharing a machine do not compete for the same ports.
        /// </remarks>
        public const int DEFAULT_RTP_PORT_RANGE_START = 51000;

        /// <summary>
        /// Default value of <see cref="RtpPortRangeEnd"/>. 500 pairs.
        /// </summary>
        public const int DEFAULT_RTP_PORT_RANGE_END = 52000;

        /// <summary>
        /// First port of the range the UDP transports are allocated from.
        /// Change it with <see cref="SetRtpPortRange"/>.
        /// </summary>
        public int RtpPortRangeStart { get; private set; } = DEFAULT_RTP_PORT_RANGE_START;

        /// <summary>
        /// One past the last port of the range the UDP transports are allocated from.
        /// Change it with <see cref="SetRtpPortRange"/>.
        /// </summary>
        public int RtpPortRangeEnd { get; private set; } = DEFAULT_RTP_PORT_RANGE_END;

        /// <summary>
        /// Sets the range of local ports the video and audio UDP transports are allocated from. Each
        /// takes one consecutive RTP/RTCP pair, so a connection takes two pairs.
        /// </summary>
        /// <param name="firstPort">First port of the range, inclusive.</param>
        /// <param name="lastPort">Last port of the range, exclusive.</param>
        /// <remarks>
        /// Set this before <see cref="Connect(string, RTPTransport, string, string, bool, RemoteCertificateValidationCallback, bool)"/>.
        /// Keep it clear of the range an RTSP server on the same machine uses - they would otherwise
        /// take ports from each other.
        /// </remarks>
        public void SetRtpPortRange(int firstPort, int lastPort)
        {
            if (firstPort < IPEndPoint.MinPort || firstPort > IPEndPoint.MaxPort)
                throw new ArgumentOutOfRangeException(nameof(firstPort), firstPort, "The first port is not a port number.");

            if (lastPort < IPEndPoint.MinPort || lastPort > IPEndPoint.MaxPort)
                throw new ArgumentOutOfRangeException(nameof(lastPort), lastPort, "The last port is not a port number.");

            // the video and audio transports take a pair each, so the range has to hold two
            if (lastPort - firstPort < 4)
                throw new ArgumentOutOfRangeException(nameof(lastPort), lastPort, "The port range has to hold at least two RTP/RTCP pairs.");

            RtpPortRangeStart = firstPort;
            RtpPortRangeEnd = lastPort;
        }

        /// <summary>
        /// Takes an RTP/RTCP port pair, looking from <paramref name="firstPort"/> before falling back
        /// to the whole range.
        /// </summary>
        /// <remarks>
        /// UDPSocket always restarts its scan at the port it is handed, so allocating the audio
        /// transport from the start of the range makes it fail a bind on the pair the video
        /// transport just took. That failure is caught and retried, but it is a first-chance
        /// SocketException ("Only one usage of each socket address ... is normally permitted") in
        /// the debugger of anyone using the library.
        /// </remarks>
        private UDPSocket AllocateUdpPairAfter(int firstPort)
        {
            if (firstPort > RtpPortRangeStart && firstPort + 2 <= RtpPortRangeEnd)
            {
                try
                {
                    return new UDPSocket(firstPort, RtpPortRangeEnd);
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is SocketException)
                {
                    // nothing free above there - sweep the whole range instead
                }
            }

            return new UDPSocket(RtpPortRangeStart, RtpPortRangeEnd);
        }

        public enum RtspStatus { WaitingToConnect, Connecting, ConnectFailed, Connected };

        private IRtspTransport _rtspSocket; // RTSP connection
        private volatile RtspStatus _rtspSocketStatus = RtspStatus.WaitingToConnect;
        
        // this wraps around a the RTSP tcpSocket stream
        private RtspListener _rtspClient;
        private RTPTransport _rtpTransport = RTPTransport.UDP; // Mode, either RTP over UDP or RTP over TCP using the RTSP socket

        /// <summary>
        /// The tracks of the stream being received, in the order the description offered them.
        /// </summary>
        /// <remarks>
        /// There used to be two of everything here instead - a video transport and an audio one, a
        /// video payload type and an audio one - and a stream offering anything else, or two of a
        /// kind, had nowhere to put it.
        /// </remarks>
        private readonly List<ClientTrack> _tracks = new List<ClientTrack>();

        private readonly object _tracksLock = new object();

        /// <summary>
        /// The tracks, as a copy safe to walk while the session is being torn down.
        /// </summary>
        internal ClientTrack[] Tracks
        {
            get { lock (_tracksLock) { return _tracks.ToArray(); } }
        }

        /// <summary>
        /// The track at this place in the description, or null if there is none.
        /// </summary>
        internal ClientTrack TrackAt(int trackIndex)
        {
            lock (_tracksLock)
            {
                return trackIndex >= 0 && trackIndex < _tracks.Count ? _tracks[trackIndex] : null;
            }
        }

        /// <summary>
        /// How many tracks of the description this client has set up.
        /// </summary>
        /// <remarks>
        /// However many <see cref="AcceptTrack"/> let through, which is the first of each kind
        /// unless something else was asked for. The index of each is what <see cref="NewTrack"/>
        /// and <see cref="ReceivedData"/> report, and what <see cref="SendRTCP(int, byte[])"/>
        /// takes.
        /// </remarks>
        public int TrackCount
        {
            get { lock (_tracksLock) { return _tracks.Count; } }
        }

        // TrackOf and EnsureTrack were here: "the first track of this kind", which is a question
        // with no answer once a stream can carry two of one. Everything works from a track or its
        // index now - see TrackAt.

        /// <summary>
        /// Takes back a track that was added and then turned out not to be wanted.
        /// </summary>
        /// <remarks>
        /// Only ever the one just added, which is why the indices stay right: the track's index is
        /// the count of the tracks before it, so removing the last one hands the same index to
        /// whichever track is accepted next.
        /// </remarks>
        private void DropTrack(ClientTrack track)
        {
            lock (_tracksLock)
            {
                if (_tracks.Count > 0 && ReferenceEquals(_tracks[_tracks.Count - 1], track))
                {
                    _tracks.RemoveAt(_tracks.Count - 1);
                }
            }
        }

        internal ClientTrack AddTrack(TrackKind kind)
        {
            lock (_tracksLock)
            {
                var made = new ClientTrack { Kind = kind, Index = _tracks.Count };
                _tracks.Add(made);
                return made;
            }
        }

        private Uri _uri = null;                  // RTSP URI (username & password will be stripped out)
        private string _session = "";             // RTSP Session
        private Authentication _authentication;
        private string _lastNonce;
        private NetworkCredential _credentials = new NetworkCredential();
        private RemoteCertificateValidationCallback _userCertificateSelectionCallback = null;
        private bool _autoReconnect = false;

        /// <summary>
        /// If true, the client must send an "onvif-replay" header on every play request.
        /// </summary>
        private bool _playbackSession = false;

        // Used with RTSP keepalive
        private bool _serverSupportsGetParameter = false;
        private System.Timers.Timer _keepaliveTimer = null;

        private bool _disposedValue;

        /// <summary>The next pair of interleaved channels to give a track, when media rides the RTSP connection.</summary>
        private int _nextInterleavedChannel;

        /// <summary>The last pair of UDP ports handed out, so the next track takes the ones after them.</summary>
        private UDPSocket _lastUdpPair;

        // setup messages still to send. Filled and drained on the RTSP receive thread, but cleared
        // from whichever thread tears the session down, so every access is guarded.
        private readonly Queue<RtspRequestSetup> _setupMessages = new Queue<RtspRequestSetup>();
        private readonly object _setupMessagesLock = new object();

        /// <summary>
        /// Called when the Setup command are completed, so we can start the right Play message (with or without playback informations)
        /// </summary>
        public event EventHandler SetupMessageCompleted;
        
        /// <summary>
        /// The SSRC this client reports under on a track.
        /// </summary>
        /// <param name="trackIndex">
        /// Which track, as <see cref="NewTrackEventArgs.TrackIndex"/> reports it.
        /// </param>
        /// <remarks>
        /// Drawn at random when the track is set up. This used to be a VideoSSRC and an AudioSSRC -
        /// one apiece for the first track of each kind, which left every track after those two with
        /// an SSRC nothing could read or set.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">There is no track with that index.</exception>
        public uint GetSsrc(int trackIndex)
        {
            return SsrcOf(RequireTrack(trackIndex));
        }

        /// <summary>
        /// Sets the SSRC this client reports under on a track.
        /// </summary>
        /// <remarks>
        /// Rarely wanted: an SSRC only has to not collide with the other sources in the session, and
        /// the one drawn when the track was set up does that. Set it before playing, since it is
        /// what the reports already sent were attributed to.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">There is no track with that index.</exception>
        public void SetSsrc(int trackIndex, uint ssrc)
        {
            RequireTrack(trackIndex).Ssrc = ssrc;
        }

        /// <summary>
        /// The track at this index, or a complaint naming the index that was not there.
        /// </summary>
        private ClientTrack RequireTrack(int trackIndex)
        {
            ClientTrack track = TrackAt(trackIndex);

            if (track == null)
            {
                throw new ArgumentOutOfRangeException(nameof(trackIndex), trackIndex,
                    "This client has set up no track with that index.");
            }

            return track;
        }
        
        // The SRTP contexts used to be public, as VideoContext and AudioContext - the keys of the
        // first video track and the first audio track, from when a stream was one of each. Two of
        // however many tracks a stream carries is an arbitrary pair to hand out, and a
        // SrtpSessionContext is live crypto state rather than a value: protecting a packet with one
        // out of band advances the roll over counter and the replay state, and the far end then
        // cannot read what follows. What a caller actually wanted was to send RTCP on a track,
        // which SendRTCP does with the context kept inside.

        static RTSPClient()
        {
            try
            {
                RtspUtils.RegisterUri();
            }
            catch (Exception ex)
            {
                // Process wide and done once, before any client exists, so there is no instance
                // logger to report it to. Registration failing because another instance got there
                // first is the ordinary case and not worth a line anywhere.
                System.Diagnostics.Debug.WriteLine("Could not register the RTSP URI schemes: " + ex.Message);
            }
        }

        /// <summary>
        /// Default ctor.
        /// </summary>
        public RTSPClient()
        {
            // Over this client's own logger, read per message, so assigning Logger later works.
            _loggerFactory = new CustomLoggerFactory(() => Logger);
            _logger = _loggerFactory.CreateLogger<RTSPClient>();
        }

        /// <summary>
        /// Ctor.
        /// </summary>
        /// <param name="loggerFactory">Logger factory <see cref="ILoggerFactory"/>.</param>
        public RTSPClient(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<RTSPClient>();
            _loggerFactory = loggerFactory;
        }

        /// <summary>
        /// Where this client says what it is doing and what it could not do.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This client's own, not the process's. It used to be a static class, so two clients in one
        /// program wrote to the same place and switching trace on for a noisy one switched it on for
        /// all of them. Assign <see cref="NullLog.Instance"/> for a client that should say
        /// nothing, or an <see cref="ILog"/> of your own to send it somewhere.
        /// </para>
        /// <para>
        /// Ignored when an <see cref="ILoggerFactory"/> was passed to the constructor: that is the
        /// host saying where its logging goes, and this would be a second answer to the same
        /// question. Can be assigned at any time, including while the client is connected.
        /// </para>
        /// </remarks>
        public ILog Logger { get; set; } = new DefaultLog();

        /// <summary>
        /// Connects to the specified RTSP server.
        /// </summary>
        /// <param name="url">URL to connect to.</param>
        /// <param name="rtpTransport">Type of the RTP transport <see cref="RTPTransport"/>.</param>
        /// <param name="username">User name.</param>
        /// <param name="password">Password.</param>
        /// <param name="playbackSession">Playback session.</param>
        /// <param name="userCertificateSelectionCallback">Callback for user certificate selection.</param>
        /// <param name="autoReconnect">Automatically try to reconnect after losing the connection.</param>
        /// <remarks>
        /// Which of the offered tracks are set up is <see cref="AcceptTrack"/>, set before this is
        /// called. It used to be a mediaRequest parameter here, which could say only which kinds.
        /// </remarks>
        public void Connect(
            string url,
            RTPTransport rtpTransport,
            string username = null,
            string password = null,
            bool playbackSession = false,
            RemoteCertificateValidationCallback userCertificateSelectionCallback = null,
            bool autoReconnect = false)
        {
            if (string.IsNullOrEmpty(url)) 
                throw new ArgumentNullException(nameof(url));

            Connect(new Uri(url), rtpTransport, username, password, playbackSession, userCertificateSelectionCallback, autoReconnect);
        }

        /// <summary>
        /// Connects to the specified RTSP server.
        /// </summary>
        /// <param name="uri">The URI of the RTSP server.</param>
        /// <param name="rtpTransport">Type of the RTP transport <see cref="RTPTransport"/>.</param>
        /// <param name="username">User name.</param>
        /// <param name="password">Password.</param>
        /// <param name="playbackSession">Playback session.</param>
        /// <param name="userCertificateSelectionCallback">Callback for user certificate selection.</param>
        /// <param name="autoReconnect">Automatically try to reconnect after losing the connection.</param>
        public void Connect(
            Uri uri,
            RTPTransport rtpTransport,
            string username = null,
            string password = null,
            bool playbackSession = false,
            RemoteCertificateValidationCallback userCertificateSelectionCallback = null,
            bool autoReconnect = false)
        {
            if (uri == null) 
                throw new ArgumentNullException(nameof(uri));

            // Use URI to extract username and password and to make a new URL without the username and password
            var (strippedUri, credentials) = ExtractCredentials(uri, username, password);

            Connect(strippedUri, rtpTransport, credentials, playbackSession, userCertificateSelectionCallback, autoReconnect);
        }

        /// <summary>
        /// Connects to the specified RTSP server.
        /// </summary>
        /// <param name="uri">The URI of the RTSP server.</param>
        /// <param name="rtpTransport">Type of the RTP transport <see cref="RTPTransport"/>.</param>
        /// <param name="credentials">Network credentials.</param>
        /// <param name="playbackSession">Playback session.</param>
        /// <param name="userCertificateSelectionCallback">Callback for user certificate selection.</param>
        /// <param name="autoReconnect">Automatically try to reconnect after losing the connection.</param>
        public void Connect(
            Uri uri,
            RTPTransport rtpTransport,
            NetworkCredential credentials = null,
            bool playbackSession = false,
            RemoteCertificateValidationCallback userCertificateSelectionCallback = null,
            bool autoReconnect = false)
        {
            if (_rtspClient != null)
                throw new InvalidOperationException("You must first call Stop() before re-connecting!");

            _logger.LogDebug("Connecting to {url} ", uri);

            this._uri = uri;
            // Check the RTP Transport
            // If the RTP transport is TCP then we interleave the RTP packets in the RTSP stream
            // If the RTP transport is UDP, we initialise two UDP sockets (one for video, one for RTCP status messages)
            // If the RTP transport is MULTICAST, we have to wait for the SETUP message to get the Multicast Address from the RTSP server
            this._rtpTransport = rtpTransport;
            this._credentials = credentials ?? new NetworkCredential();
            this._playbackSession = playbackSession;
            this._userCertificateSelectionCallback = userCertificateSelectionCallback;
            this._autoReconnect = autoReconnect;

            // start from a clean handshake, whatever happened to any previous attempt
            lock (_setupMessagesLock)
            {
                _setupMessages.Clear();
            }

            // Connect to a RTSP Server. The RTSP session is a TCP connection
            _rtspSocketStatus = RtspStatus.Connecting;

            try
            {
                _rtspSocket = Rtsp.RtspUtils.CreateRtspTransportFromUrl(_uri, _credentials, _userCertificateSelectionCallback);
            }
            catch
            {
                _rtspSocketStatus = RtspStatus.ConnectFailed;
                _logger.LogWarning("Error - did not connect");
                Stopped?.Invoke(this, new StoppedEventArgs(StoppedReason.ConnectionFailed));
                return;
            }

            if (!_rtspSocket.Connected)
            {
                _rtspSocketStatus = RtspStatus.ConnectFailed;
                _logger.LogWarning("Error - did not connect");
                Stopped?.Invoke(this, new StoppedEventArgs(StoppedReason.ConnectionFailed));
                return;
            }

            _rtspSocketStatus = RtspStatus.Connected;

            // Connect a RTSP Listener to the RTSP Socket (or other Stream) to send RTSP messages and listen for RTSP replies
            _rtspClient = new RtspListener(_rtspSocket, _loggerFactory.CreateLogger<RtspListener>())
            {
                AutoReconnect = _autoReconnect
            };

            _rtspClient.MessageReceived += RtspMessageReceived;
            _rtspClient.Start(); // start listening for messages from the server (messages fire the MessageReceived event)

            // Transports are made when the description says how many tracks there are, rather than
            // two of them here on the assumption that a stream is one video and one audio.
            _nextInterleavedChannel = 0;
            _lastUdpPair = null;

            /*
            if (rtpTransport == RtpTransport.MULTICAST)
            {
                // Nothing to do. Will open Multicast UDP sockets after the SETUP command
            }
            */

            // Send OPTIONS
            // In the Received Message handler we will send DESCRIBE, SETUP and PLAY
            RtspRequest optionsMessage = new RtspRequestOptions
            {
                RtspUri = _uri
            };

            _rtspClient.SendMessage(optionsMessage);
        }

        /// <summary>
        /// Takes the credentials out of the URI's user info, if it has any, and returns the URI stripped
        /// of them. Falls back to the supplied user name and password when the URI carries none.
        /// </summary>
        /// <param name="uri">The URI to read from.</param>
        /// <param name="username">User name to use when the URI carries no user info.</param>
        /// <param name="password">Password to use when the URI carries no user info.</param>
        /// <returns>The URI without the user info, and the credentials to authenticate with.</returns>
        internal static (Uri Uri, NetworkCredential Credentials) ExtractCredentials(Uri uri, string username, string password)
        {
            if (uri.UserInfo.Length == 0)
            {
                return (uri, new NetworkCredential(username, password));
            }

            // The user info is percent encoded in the URI, and the password may itself contain a ':',
            // so it has to be split on the first separator only and then unescaped.
            int separator = uri.UserInfo.IndexOf(':');
            string uriUserName = separator < 0 ? uri.UserInfo : uri.UserInfo.Substring(0, separator);
            string uriPassword = separator < 0 ? string.Empty : uri.UserInfo.Substring(separator + 1);

            Uri uriWithoutCredentials = new Uri(uri.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.UserInfo, UriFormat.UriEscaped));

            return (uriWithoutCredentials, new NetworkCredential(
                Uri.UnescapeDataString(uriUserName),
                Uri.UnescapeDataString(uriPassword)));
        }

        /// <summary>
        /// Attempt to reconnect when a connection to the server is lost.
        /// </summary>
        /// <exception cref="InvalidOperationException">Reconnect can only be called after calling Connect.</exception>
        /// <summary>
        /// Disposes a transport that has already been stopped.
        /// </summary>
        /// <remarks>
        /// Stop is what frees the UDP ports, so this is not about leaking those - it is the rest of
        /// what the transport holds, and the contract it declares by being IDisposable.
        /// </remarks>
        private void ReleaseTransport(IRtpTransport transport)
        {
            if (transport is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error disposing an RTP transport");
                }
            }
        }

        public void TryReconnect()
        {
            if (_uri == null)
                throw new InvalidOperationException("You must first call Connect() before re-connecting!");

            Connect(_uri, _rtpTransport, _credentials, _playbackSession, _userCertificateSelectionCallback, _autoReconnect);
        }

        /// <summary>
        /// Returns true if this connection failed, or if it connected but is no longer connected.
        /// </summary>
        /// <returns></returns>
        public bool StreamingFinished() 
        {
            switch(_rtspSocketStatus)
            {
                case RtspStatus.ConnectFailed:
                    return true;
                case RtspStatus.Connected:
                    return !(_rtspSocket?.Connected ?? false);
                default:
                    return false;
            }
        }

        /// <summary>
        /// Returns the current RTSP status.
        /// </summary>
        /// <returns>The current RTSP status.</returns>
        public RtspStatus GetRtspStatus()
        {
            return _rtspSocketStatus;
        }

        /// <summary>
        /// Pause.
        /// </summary>
        /// <exception cref="InvalidOperationException"></exception>
        public void Pause()
        {
            if (_rtspSocket == null || _uri == null)
            {
                throw new InvalidOperationException("Not connected");
            }
            RtspRequest pause_message = new RtspRequestPause
            {
                RtspUri = _uri,
                Session = _session
            };
            pause_message.AddAuthorization(_authentication, _uri, _rtspSocket.NextCommandIndex());
            _rtspClient?.SendMessage(pause_message);
        }

        /// <summary>
        /// Start playing.
        /// </summary>
        /// <exception cref="InvalidOperationException"></exception>
        public void Play()
        {
            if (_rtspSocket == null || _uri == null)
            {
                throw new InvalidOperationException("Not connected");
            }
            var playMessage = new RtspRequestPlay
            {
                RtspUri = _uri,
                Session = _session
            };
            playMessage.AddAuthorization(_authentication, _uri, _rtspSocket.NextCommandIndex());
            // Need for old sony camera SNC-CS20
            playMessage.Headers.Add("range", "npt=0.000-");
            if (_playbackSession)
            {
                playMessage.AddRequireOnvifRequest();
                playMessage.AddRateControlOnvifRequest(false);
            }
            _rtspClient?.SendMessage(playMessage);
        }

        /// <summary>
        /// Generate a Play request from required time
        /// </summary>
        /// <param name="seekTime">The playback time to start from</param>
        /// <param name="speed">Speed information (1.0 means normal speed, -1.0 backward speed), other values &gt;1.0 and &lt;-1.0 allow a different speed</param>
        public void Play(DateTime seekTime, double speed = 1.0)
        {
            if (_rtspSocket == null || _uri == null) { throw new InvalidOperationException("Not connected"); }
            var playMessage = new RtspRequestPlay
            {
                RtspUri = _uri,
                Session = _session,
            };
            playMessage.AddPlayback(seekTime, speed);
            if (_playbackSession)
            {
                playMessage.AddRequireOnvifRequest();
                playMessage.AddRateControlOnvifRequest(false);
            }
            _rtspClient?.SendMessage(playMessage);
        }

        /// <summary>
        /// Generate a Play request with a time range
        /// </summary>
        /// <param name="seekTimeFrom">Starting time for playback</param>
        /// <param name="seekTimeTo">Ending time for playback</param>
        /// <param name="speed">Speed information (1.0 means normal speed, -1.0 backward speed), other values &gt;1.0 and &lt;-1.0 allow a different speed</param>
        /// <exception cref="InvalidOperationException"></exception>
        public void Play(DateTime seekTimeFrom, DateTime seekTimeTo, double speed = 1.0)
        {
            if (_rtspSocket == null || _uri == null) { throw new InvalidOperationException("Not connected"); }
            if (seekTimeFrom > seekTimeTo) { throw new ArgumentOutOfRangeException(nameof(seekTimeFrom), "Starting seek cannot be major than ending seek."); }
            var playMessage = new RtspRequestPlay
            {
                RtspUri = _uri,
                Session = _session,
            };
            playMessage.AddPlayback(seekTimeFrom, seekTimeTo, speed);
            if (_playbackSession)
            {
                playMessage.AddRequireOnvifRequest();
                playMessage.AddRateControlOnvifRequest(false);
            }
            _rtspClient?.SendMessage(playMessage);
        }

        /// <summary>
        /// Stop playing.
        /// </summary>
        public void Stop()
        {
            StopClient();
        }

        private void StopClient()
        {
            // Only when there is a session to tear down. Stopping during the handshake - refusing a
            // stream that came without the key it needs, say - used to send a TEARDOWN naming no
            // session at all, which the server can only answer with "session not found".
            if (!string.IsNullOrEmpty(_session))
            {
                // Send TEARDOWN
                RtspRequest teardown_message = new RtspRequestTeardown
                {
                    RtspUri = _uri,
                    Session = _session
                };
                teardown_message.AddAuthorization(_authentication, _uri, _rtspSocket?.NextCommandIndex() ?? 0);
                _rtspClient?.SendMessage(teardown_message);
            }

            TeardownClient();
        }

        private void TeardownClient()
        {
            _rtspSocketStatus = RtspStatus.WaitingToConnect;

            // a reconnect gets a new stream, so the SSRC we learned no longer applies
            foreach (ClientTrack track in Tracks)
            {
                track.Rtcp.Reset();
            }

            // Drop any SETUP messages left over from an interrupted handshake. A reconnect builds a
            // fresh set from the new DESCRIBE, and sending a stale one first would use the old URI.
            lock (_setupMessagesLock)
            {
                _setupMessages.Clear();
            }

            // Stop the keepalive timer
            var keepaliveTimer = _keepaliveTimer;
            if (keepaliveTimer != null)
            {
                keepaliveTimer.Elapsed -= SendKeepAlive;
                keepaliveTimer.Dispose();
                _keepaliveTimer = null;
            }

            // clear up any UDP sockets
            foreach (ClientTrack track in Tracks)
            {
                IRtpTransport transport = track.Transport;

                if (transport == null)
                {
                    continue;
                }

                transport.Stop();
                transport.DataReceived -= track.OnData;
                transport.ControlReceived -= track.OnControl;
                ReleaseTransport(transport);
                track.Transport = null;
            }

            lock (_tracksLock)
            {
                _tracks.Clear();
            }

            _lastUdpPair = null;
            _nextInterleavedChannel = 0;

            // Drop the RTSP session
            var rtspClient = _rtspClient;
            if (rtspClient != null)
            {
                rtspClient.MessageReceived -= RtspMessageReceived;
                rtspClient.Stop();
                _rtspClient = null;
            }

            _rtspSocket = null; // closed by rtspClient.Stop()
        }

        /// <summary>
        /// Somewhere for a track's media to arrive, of whichever sort was asked for.
        /// </summary>
        /// <remarks>
        /// One per track, because two tracks arriving on one pair of ports or one pair of interleaved
        /// channels could not be told apart.
        /// </remarks>
        private IRtpTransport CreateTransport()
        {
            switch (_rtpTransport)
            {
                case RTPTransport.TCP:
                    return new RtpTcpTransport(_rtspClient)
                    {
                        DataChannel = _nextInterleavedChannel++,
                        ControlChannel = _nextInterleavedChannel++,
                    };

                case RTPTransport.UDP:
                    // the range holds one RTP/RTCP pair per transport, see SetRtpPortRange
                    UDPSocket pair = _lastUdpPair == null
                        ? new UDPSocket(RtpPortRangeStart, RtpPortRangeEnd)
                        : AllocateUdpPairAfter(_lastUdpPair.ControlPort + 1);

                    _lastUdpPair = pair;
                    return pair;

                default:
                    // multicast: the sockets are opened once the reply says which group to join
                    return null;
            }
        }

        /// <summary>
        /// Sends RTCP on one track, protecting it first where the stream is encrypted.
        /// </summary>
        /// <param name="trackIndex">
        /// Which track, as <see cref="NewTrackEventArgs.TrackIndex"/> reports it.
        /// </param>
        /// <param name="rtcp">RTCP message bytes, unprotected.</param>
        /// <remarks>
        /// This is what the SRTP context was wanted for, and it keeps the context inside: the keys
        /// and the roll over counter belong to the session, and protecting a packet with them
        /// elsewhere leaves the far end unable to read what follows.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">There is no track with that index.</exception>
        public void SendRTCP(int trackIndex, byte[] rtcp)
        {
            SendRTCP(RequireTrack(trackIndex), rtcp);
        }

        // SendVideoRTCP and SendAudioRTCP were here, sending on the first track of each kind.
        // SendRTCP takes the track index instead, so every track can be sent to.

        private void SendRTCP(ClientTrack track, byte[] rtcp)
        {
            if (track == null)
            {
                return;
            }

            if (track.Context != null)
            {
                rtcp = ProtectRtcp(track.Context, rtcp);
            }

            track.Transport?.WriteToControlPort(rtcp);
        }

        /// <summary>
        /// Wraps an RTCP packet as SRTCP.
        /// </summary>
        internal static byte[] ProtectRtcp(SrtpSessionContext context, byte[] rtcp)
        {
            byte[] rtcpBuffer = new byte[context.EncodeRtcpContext.CalculateRequiredSrtcpPayloadLength(rtcp.Length)];

            // the packet has to be copied into the (larger) output buffer first - ProtectRtcp works in place
            Buffer.BlockCopy(rtcp, 0, rtcpBuffer, 0, rtcp.Length);

            int ret = context.EncodeRtcpContext.ProtectRtcp(rtcpBuffer, rtcp.Length, out int len);
            if (ret != 0)
            {
                throw new InvalidOperationException($"Failed to protect the RTCP packet, SRTP returned {ret}.");
            }

            byte[] protectedRtcp = new byte[len];
            Buffer.BlockCopy(rtcpBuffer, 0, protectedRtcp, 0, len);
            return protectedRtcp;
        }

        /// <summary>
        /// An empty receiver report, for a channel that has had nothing to report on.
        /// </summary>
        public byte[] BuildRtcpReceiverReport(uint ssrc) => BuildRtcpReceiverReport(ssrc, 0, null);

        /// <summary>
        /// A receiver report saying how the media has actually been arriving.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This used to be an empty report - the right shape, the right length, and a report count of
        /// nothing, so it named no source and said nothing about any of them. A sender receiving one
        /// learns that somebody is listening and not one thing more: not what share of its packets
        /// went missing, not how unevenly they are turning up, not how long the round trip is. All of
        /// which is what a sender would change its behaviour on.
        /// </para>
        /// <para>
        /// It carries one report block, about the source this channel is receiving from, as RFC 3550
        /// section 6.4.2 lays it out.
        /// </para>
        /// </remarks>
        internal byte[] BuildRtcpReceiverReport(uint ssrc, uint aboutSsrc, RtcpChannelState channel)
        {
            ReceptionReport report = channel?.Reception.TakeReport() ?? new ReceptionReport();

            if (!report.HasData || aboutSsrc == 0)
            {
                // Nothing has arrived to report on, so there is nothing to say about anyone. The
                // report still goes, because it is also what says this receiver is still here.
                byte[] empty = new byte[8];
                WriteRtcpHeader(empty, reportCount: 0, length: 1, ssrc: ssrc);
                return empty;
            }

            // eight bytes of header and sender, then twenty four of report block
            byte[] rtcp = new byte[32];
            WriteRtcpHeader(rtcp, reportCount: 1, length: 7, ssrc: ssrc);

            WriteUInt32(rtcp, 8, aboutSsrc);

            rtcp[12] = report.FractionLost;

            // twenty four bits, signed, so a receiver that took in duplicates reports fewer than none
            int lost = report.CumulativeLost;
            lost = lost > 0x7FFFFF ? 0x7FFFFF : lost < -0x800000 ? -0x800000 : lost;

            rtcp[13] = (byte)((lost >> 16) & 0xFF);
            rtcp[14] = (byte)((lost >> 8) & 0xFF);
            rtcp[15] = (byte)(lost & 0xFF);

            WriteUInt32(rtcp, 16, report.ExtendedHighestSequence);
            WriteUInt32(rtcp, 20, report.Jitter);
            WriteUInt32(rtcp, 24, report.LastSenderReport);
            WriteUInt32(rtcp, 28, report.DelaySinceLastSenderReport);

            return rtcp;
        }

        private static void WriteRtcpHeader(byte[] rtcp, int reportCount, int length, uint ssrc)
        {
            const int version = 2;
            const int paddingBit = 0;
            const int packetType = 201; // Receiver Report

            rtcp[0] = (byte)((version << 6) + (paddingBit << 5) + reportCount);
            rtcp[1] = packetType;
            rtcp[2] = (byte)((length >> 8) & 0xFF);
            rtcp[3] = (byte)(length & 0xFF);

            WriteUInt32(rtcp, 4, ssrc);
        }

        private static void WriteUInt32(byte[] target, int at, uint value)
        {
            target[at] = (byte)((value >> 24) & 0xFF);
            target[at + 1] = (byte)((value >> 16) & 0xFF);
            target[at + 2] = (byte)((value >> 8) & 0xFF);
            target[at + 3] = (byte)(value & 0xFF);
        }

        /// <summary>
        /// Media arriving on one track.
        /// </summary>
        /// <remarks>
        /// One of these, not one per kind. The two it replaced were the same forty lines twice over,
        /// differing in which four fields they read and which two events they raised - which is why a
        /// stream with a third track had nowhere to go.
        /// </remarks>
        private void RtpDataReceived(ClientTrack track, RtspDataEventArgs e)
        {
            // Inside the using, not before it: returning here handed the payload back to nobody,
            // when every other path in this method takes ownership of it.
            using (var data = e.Data)
            {
                if (data.Data.IsEmpty)
                    return;

                var rtpData = data.Data;

                if (track.Context != null)
                {
                    byte[] decoded = rtpData.ToArray();
                    if (track.Context.DecodeRtpContext.UnprotectRtp(decoded, decoded.Length, out var len) == 0)
                    {
                        // The decrypted bytes are already in this array - Take().ToArray() walked
                        // them through an iterator into a second copy of themselves, for every
                        // packet of a protected stream.
                        rtpData = decoded.AsMemory(0, len);
                    }
                    else
                    {
                        _logger.LogError("Unprotect RTP failed");
                        return;
                    }
                }

                var rtpPacket = new RtpPacket(rtpData.Span);

                if (rtpPacket.PayloadType != track.PayloadType)
                {
                    // Check the payload type in the RTP packet matches the Payload Type value from the SDP
                    _logger.LogDebug("Ignoring RTP with payload type {payloadType} on the {kind} track",
                        rtpPacket.PayloadType, track.Kind);
                    return;
                }

                // remember who is actually sending us media, so we can ignore RTCP BYE from anyone else
                track.Rtcp.LearnRemoteSsrc(rtpPacket.Ssrc);

                // and what arrived, so the report sent back says something
                track.Rtcp.Reception.RecordPacket((ushort)rtpPacket.SequenceNumber, rtpPacket.Timestamp, track.Rtcp.ClockRate);

                EventHandler<TrackRawRtpEventArgs> anyTrackListener = ReceivedRawRTP;

                // Built only where something will read it. It was made for every packet of every
                // stream whether or not anyone had subscribed.
                if (anyTrackListener != null)
                {
                    var raw = new RawRtpDataEventArgs(
                        rtpData,
                        rtpPacket.CsrcCount,
                        rtpPacket.ExtensionHeaderId,
                        rtpPacket.HasPadding,
                        rtpPacket.IsMarker,
                        rtpPacket.IsWellFormed,
                        rtpPacket.PayloadSize,
                        rtpPacket.PayloadType,
                        rtpPacket.SequenceNumber,
                        rtpPacket.Ssrc,
                        rtpPacket.Timestamp,
                        rtpPacket.Version,
                        CalculatePayloadStart(rtpPacket));

                    anyTrackListener(this,
                        new TrackRawRtpEventArgs(track.Index, track.Kind, track.Codec, raw));
                }

                if (!ProcessRTP)
                {
                    return;
                }

                if (track.Processor == null)
                {
                    _logger.LogWarning("No parser for the {kind} RTP payload {payloadType}", track.Kind, track.PayloadType);
                    return;
                }

                using (RawMediaFrame frames = track.Processor.ProcessPacket(rtpPacket)) // this will cache the Packets until there is a Frame
                {
                    if (frames.Any())
                    {
                        bool synced = track.Rtcp.TryMapToSenderClock(frames.RtpTimestamp, out DateTime senderTime);

                        var simple = new SimpleDataEventArgs(
                            frames.Data,
                            synced ? senderTime : frames.ClockTimestamp,
                            frames.RtpTimestamp,
                            synced);

                        ReceivedData?.Invoke(this, new TrackDataEventArgs(track.Index, track.Kind, track.Codec, simple));
                    }
                }
            }
        }

        private static int CalculatePayloadStart(RtpPacket rtpPacket)
        {
            // Note: e.PayloadSize includes also extensions and is incorrect for RTP forwarding,
            //  we have to calculate the correct size using 12 + e.CsrcCount * 4
            return 12 + rtpPacket.CsrcCount * 4;
        }

        /// <summary>
        /// Reports arriving on one track.
        /// </summary>
        private void RtcpControlDataReceived(ClientTrack track, object sender, RtspDataEventArgs e)
        {
            _logger.LogDebug("Received RTCP on the {kind} track", track.Kind);

            using (var data = e.Data)
            {
                if (data.Data.IsEmpty)
                    return;

                var rtcpData = data.Data;

                if (track.Context != null)
                {
                    byte[] decoded = rtcpData.ToArray();
                    if (track.Context.DecodeRtcpContext.UnprotectRtcp(decoded, decoded.Length, out var len) == 0)
                    {
                        rtcpData = decoded.AsMemory(0, len);
                    }
                    else
                    {
                        _logger.LogError("Unprotect RTCP failed");
                        return;
                    }
                }

                var raw = new RawRtcpDataEventArgs(rtcpData);

                ReceivedRawRTCP?.Invoke(this,
                    new TrackRawRtcpEventArgs(track.Index, track.Kind, track.Codec, raw));

                if (!ProcessRTCP)
                    return;

                var reports = ParseRTCPAndGenerateResponse(rtcpData, SsrcOf(track), track.Rtcp);

                foreach (var report in reports)
                {
                    byte[] outgoing = track.Context != null ? ProtectRtcp(track.Context, report) : report;

                    ((IRtpTransport)sender).WriteToControlPort(outgoing);
                }
            }
        }

        /// <summary>
        /// The SSRC this client reports under on a track, drawn the first time it is asked for.
        /// </summary>
        /// <remarks>
        /// One per track, because two tracks reporting under one SSRC cannot be told apart by
        /// whatever is reading the reports.
        /// </remarks>
        private uint SsrcOf(ClientTrack track)
        {
            if (track.Ssrc == 0)
            {
                track.Ssrc = NextSsrc();
            }

            return track.Ssrc;
        }

        private const int RTCP_HEADER_SIZE = 8;
        private const int RTCP_SENDER_REPORT_SIZE = 20;

        internal List<byte[]> ParseRTCPAndGenerateResponse(Memory<byte> data, uint ssrc, RtcpChannelState channel)
        {
            List<byte[]> reports = new List<byte[]>();

            // RTCP Packet
            // - Version, Padding and Receiver Report Count
            // - Packet Type
            // - Length
            // - SSRC
            // - payload

            // Everything below is parsed straight off the wire, so every read has to be bounds checked -
            // on UDP transport these datagrams can come from anyone who can reach the port.

            // There can be multiple RTCP packets transmitted together. Loop ever each one
            int packetIndex = 0;
            var span = data.Span;

            while (packetIndex + RTCP_HEADER_SIZE <= data.Length)
            {
                //int rtcpVersion = (span[packetIndex + 0] >> 6);
                //int rtcpPadding = (span[packetIndex + 0] >> 5) & 0x01;
                //int rtcpReceptionReportCount = (span[packetIndex + 0] & 0x1F);
                byte rtcpPacketType = span[packetIndex + 1]; // Values from 200 to 207
                int rtcpLength = (int)(span[packetIndex + 2] << 8) + (int)(span[packetIndex + 3]); // number of 32 bit words
                int rtcpPacketLength = (rtcpLength + 1) * 4;

                // a packet in a compound RTCP packet must not claim to run past the end of the datagram
                if (rtcpPacketLength < RTCP_HEADER_SIZE || packetIndex + rtcpPacketLength > data.Length)
                {
                    _logger.LogWarning("Discarding malformed RTCP packet of declared length {rtcpPacketLength} at offset {packetIndex} of {length}",
                        rtcpPacketLength, packetIndex, data.Length);
                    break;
                }

                uint rtcpSsrc = (uint)(span[packetIndex + 4] << 24) + (uint)(span[packetIndex + 5] << 16)
                    + (uint)(span[packetIndex + 6] << 8) + span[packetIndex + 7];

                // 200 = SR = Sender Report
                // 201 = RR = Receiver Report
                // 202 = SDES = Source Description
                // 203 = Bye = Goodbye
                // 204 = APP = Application Specific Method
                // 207 = XR = Extended Reports

                _logger.LogDebug("RTCP Data. PacketType={rtcpPacketType} SSRC={ssrc}", rtcpPacketType, rtcpSsrc);

                if (rtcpPacketType == 200)
                {
                    // We have received a Sender Report
                    // Use it to convert the RTP timestamp into the UTC time

                    if (rtcpPacketLength < RTCP_SENDER_REPORT_SIZE)
                    {
                        _logger.LogWarning("Discarding truncated RTCP Sender Report of length {rtcpPacketLength}", rtcpPacketLength);
                    }
                    else
                    {
                        UInt32 ntpMswSeconds = (uint)(span[packetIndex + 8] << 24) + (uint)(span[packetIndex + 9] << 16)
                        + (uint)(span[packetIndex + 10] << 8) + span[packetIndex + 11];

                        UInt32 ntpLswFractions = (uint)(span[packetIndex + 12] << 24) + (uint)(span[packetIndex + 13] << 16)
                        + (uint)(span[packetIndex + 14] << 8) + span[packetIndex + 15];

                        UInt32 rtpTimestamp = (uint)(span[packetIndex + 16] << 24) + (uint)(span[packetIndex + 17] << 16)
                        + (uint)(span[packetIndex + 18] << 8) + span[packetIndex + 19];

                        // The fraction matters. Lip sync is a matter of tens of milliseconds and the
                        // seconds word alone is a second wide, so reading only that would be two
                        // orders of magnitude too coarse to be worth anything. Divided by 2^32 as a
                        // double, because uint over uint is integer division and comes out zero -
                        // which is what the line that used to be commented out here would have done.
                        double ntpSeconds = ntpMswSeconds + ntpLswFractions / 4294967296.0;

                        // NTP Most Significant Word is relative to 0h, 1 Jan 1900
                        // This will wrap around in 2036
                        var time = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(ntpSeconds);

                        // What makes the two streams of a session comparable: this pairs a wall clock
                        // time with the RTP timestamp of that same instant on this stream.
                        channel.RecordSenderReport(time, rtpTimestamp);

                        // The middle of the timestamp is what a report echoes back, and the moment it
                        // arrived is what says how long the answer waited here. Between them the
                        // sender can work out the round trip, which it has no other way of knowing.
                        uint middle32 = (ntpMswSeconds << 16) | (ntpLswFractions >> 16);
                        channel.Reception.RecordSenderReportArrival(middle32);

                        _logger.LogDebug("RTCP time (UTC) for RTP timestamp {timestamp} is {time}", rtpTimestamp, time);

                        // Send a Receiver Report, if one is due. Answering every sender report meant
                        // reporting at whatever rate the far end chose, which for a server that
                        // reports per frame is a report per frame back.
                        if (channel.ClaimReceiverReportSlot(ReceiverReportInterval))
                        {
                            reports.Add(BuildRtcpReceiverReport(ssrc, rtcpSsrc, channel));
                        }
                    }
                }
                else if (rtcpPacketType == 203)
                {
                    // We have received a BYE message.
                    // Only honour it from the source we are actually receiving media from, otherwise a single
                    // spoofed UDP datagram from anywhere would be enough to tear the session down.
                    uint? remoteSsrc = channel.RemoteSsrc;

                    if (remoteSsrc.HasValue && remoteSsrc.Value != rtcpSsrc)
                    {
                        _logger.LogWarning("Ignoring RTCP BYE for unknown SSRC {rtcpSsrc}, streaming from SSRC {remoteSsrc}", rtcpSsrc, remoteSsrc.Value);
                    }
                    else
                    {
                        _logger.LogDebug("RTCP BYE message received");

                        Stopped?.Invoke(this, new StoppedEventArgs(StoppedReason.RtcpBye));
                        TeardownClient();
                        break;
                    }
                }

                packetIndex += rtcpPacketLength;
            }

            return reports;
        }

        /// <summary>
        /// Whether a challenge says the password was right and only the nonce had expired.
        /// </summary>
        private static bool IsStale(string challenge)
        {
            var stale = System.Text.RegularExpressions.Regex.Match(
                challenge ?? string.Empty, "stale\\s*=\\s*\"?(true)\"?",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return stale.Success;
        }

        /// <summary>
        /// The nonce a challenge carries, used to tell a fresh challenge from the same one repeated.
        /// </summary>
        private static string NonceOf(string challenge)
        {
            var nonce = System.Text.RegularExpressions.Regex.Match(challenge ?? string.Empty, "nonce=\"([^\"]+)\"");
            return nonce.Success ? nonce.Groups[1].Value : null;
        }

        private void RtspMessageReceived(object sender, RtspChunkEventArgs e)
        {
            // This runs on the listener's receive thread. Anything that escapes here is an unhandled
            // exception on a background thread, which takes the whole process down, so the dialog is
            // wrapped and turned into a Stopped event the caller can react to.
            try
            {
                HandleRtspResponse(e);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling the RTSP response, stopping");
                TeardownClient();
                Stopped?.Invoke(this, new StoppedEventArgs(StoppedReason.ProtocolError));
            }
        }

        private void HandleRtspResponse(RtspChunkEventArgs e)
        {
            if (!(e.Message is RtspResponse message))
                return;

            // RTSP Messages are OPTIONS, DESCRIBE, SETUP, PLAY etc
            _logger.LogDebug("Received RTSP response to message {originalRequest}", message.OriginalRequest);

            // If message has a 401 - Unauthorized Error, then we re-send the message with Authorization
            // using the most recently received 'realm' and 'nonce'
            if (!message.IsOk)
            {
                _logger.LogDebug("Got Error in RTSP Reply {returnCode} {returnMessage}", message.ReturnCode, message.ReturnMessage);

                if (message.ReturnCode == 404)
                {
                    _logger.LogError("Fail to find resource stopping here");
                    StopClient();
                    Stopped?.Invoke(this, new StoppedEventArgs(StoppedReason.NotFound));
                    return;
                }

                if (message.ReturnCode == 401 && message.OriginalRequest?.Headers.ContainsKey(RtspHeaderNames.Authorization) == true)
                {
                    // A nonce does not last forever. A server that rotates its own says so with
                    // stale, meaning the password was right and only the nonce was old, and the
                    // answer is to redo the digest against the new one. Treating that as a wrong
                    // password ended sessions that were perfectly entitled to carry on - a keepalive
                    // an hour into a stream would stop the client for good.
                    if (message.Headers.TryGetValue(RtspHeaderNames.WWWAuthenticate, out string staleChallenge)
                        && IsStale(staleChallenge)
                        && NonceOf(staleChallenge) != _lastNonce)
                    {
                        _logger.LogDebug("The nonce went stale, authenticating again");

                        _lastNonce = NonceOf(staleChallenge);
                        _authentication = Authentication.Create(_credentials, staleChallenge);

                        if (message.OriginalRequest?.Clone() is RtspRequest staleRetry)
                        {
                            staleRetry.AddAuthorization(_authentication, _uri, _rtspSocket?.NextCommandIndex() ?? 0);
                            _rtspClient?.SendMessage(staleRetry);
                            return;
                        }
                    }

                    _logger.LogError("Fail to authenticate stopping here");
                    StopClient();
                    Stopped?.Invoke(this, new StoppedEventArgs(StoppedReason.Unauthorized));
                    return;
                }

                // Check if the Reply has an Authenticate header.
                // Only a 401 carrying a challenge is worth resending - retrying on any other error would put
                // the client into an endless request loop against a server that keeps rejecting us.
                if (message.ReturnCode == 401 && message.Headers.TryGetValue(RtspHeaderNames.WWWAuthenticate, out string value))
                {
                    // Process the WWW-Authenticate header
                    // EG:   Basic realm="AProxy"
                    // EG:   Digest realm="AXIS_WS_ACCC8E3A0A8F", nonce="000057c3Y810622bff50b36005eb5efeae118626a161bf", stale=FALSE
                    // EG:   Digest realm="IP Camera(21388)", nonce="534407f373af1bdff561b7b4da295354", stale="FALSE"
                    string wwwAuthenticate = value ?? string.Empty;
                    _lastNonce = NonceOf(wwwAuthenticate);
                    _authentication = Authentication.Create(_credentials, wwwAuthenticate);
                    _logger.LogDebug("WWW Authorize parsed for {authentication}", _authentication);

                    if (message.OriginalRequest?.Clone() is RtspRequest resendMessage)
                    {
                        resendMessage.AddAuthorization(_authentication, _uri, _rtspSocket?.NextCommandIndex() ?? 0);
                        _rtspClient?.SendMessage(resendMessage);
                        return;
                    }
                }

                _logger.LogError("Stopping, the server replied {returnCode} {returnMessage}", message.ReturnCode, message.ReturnMessage);
                StopClient();
                Stopped?.Invoke(this, new StoppedEventArgs(StoppedReason.ServerError));
                return;
            }

            // If we get a reply to OPTIONS then start the Keepalive Timer and send DESCRIBE
            if (message.OriginalRequest is RtspRequestOptions)
            {
                // Check the capabilities returned by OPTIONS
                // The Public: header contains the list of commands the RTSP server supports
                // Eg. DESCRIBE, SETUP, TEARDOWN, PLAY, PAUSE, OPTIONS, ANNOUNCE, RECORD, GET_PARAMETER
                var supportedCommand = RTSPHeaderUtils.ParsePublicHeader(message);
                _serverSupportsGetParameter = supportedCommand.Contains("GET_PARAMETER", StringComparer.OrdinalIgnoreCase);

                if (_keepaliveTimer == null)
                {
                    // Start a Timer to send an Keepalive RTSP command every 20 seconds
                    _keepaliveTimer = new System.Timers.Timer();
                    _keepaliveTimer.Elapsed += SendKeepAlive;
                    _keepaliveTimer.Interval = 20 * 1000;
                    _keepaliveTimer.Enabled = true;

                    // Send DESCRIBE
                    RtspRequest describeMessage = new RtspRequestDescribe
                    {
                        RtspUri = _uri,
                        Headers = { { "Accept", "application/sdp" } },
                    };
                    describeMessage.AddAuthorization(_authentication, _uri, _rtspSocket.NextCommandIndex());
                    _rtspClient?.SendMessage(describeMessage);
                }
                else
                {
                    // If the Keepalive Timer was not null, the OPTIONS reply may have come from a Keepalive
                    // So no need to generate a DESCRIBE message
                    // do nothing
                }
            }

            // If we get a reply to DESCRIBE (which was our second command), then process SDP and send the SETUP
            if (message.OriginalRequest is RtspRequestDescribe)
            {
                HandleDescribeResponse(message);
            }

            // If we get a reply to SETUP (which was our third command), then we
            // (i) check if the Interleaved Channel numbers have been modified by the camera (eg Panasonic cameras)
            // (ii) check if we have any more SETUP commands to send out (eg if we are doing SETUP for Video and Audio)
            // (iii) send a PLAY command if all the SETUP command have been sent
            if (message.OriginalRequest is RtspRequestSetup)
            {
                _logger.LogDebug("Got reply from Setup. Session is {session}", message.Session);

                _session = message.Session ?? ""; // Session value used with Play, Pause, Teardown and and additional Setups
                if (_keepaliveTimer != null && message.Timeout > 0 && message.Timeout > _keepaliveTimer.Interval / 1000)
                {
                    _keepaliveTimer.Interval = message.Timeout * 1000 / 2;
                }

                // Which track this reply is about, found by the URL it was set up with. It used to be
                // a choice between the video one and the audio one, which is a stream with one of
                // each and nothing else.
                ClientTrack setupTrack = null;

                foreach (ClientTrack candidate in Tracks)
                {
                    if (message.OriginalRequest.RtspUri == candidate.ControlUri)
                    {
                        setupTrack = candidate;
                        break;
                    }
                }

                if (setupTrack == null)
                {
                    _logger.LogWarning("Ignoring a SETUP reply for {uri}, which is not a track we asked for",
                        message.OriginalRequest.RtspUri);
                }

                // Check the Transport header
                var transportString = message.Headers[RtspHeaderNames.Transport];
                if (transportString != null)
                {
                    RtspTransport transport = RtspTransport.Parse(transportString);

                    // Check if Transport header includes Multicast
                    if (transport.IsMulticast)
                    {
                        string multicastAddress = transport.Destination;
                        var videoDataChannel = transport.Port?.First;
                        var videoRtcpChannel = transport.Port?.Second;

                        if (!string.IsNullOrEmpty(multicastAddress)
                            && videoDataChannel.HasValue
                            && videoRtcpChannel.HasValue
                            && setupTrack != null)
                        {
                            // Create the Pair of UDP Sockets in Multicast mode
                            setupTrack.Transport = new MulticastUDPSocket(
                                multicastAddress, videoDataChannel.Value,
                                multicastAddress, videoRtcpChannel.Value);
                        }
                    }

                    // check if the requested Interleaved channels have been modified by the camera
                    // in the SETUP Reply (Panasonic have a camera that does this)
                    if (transport.LowerTransport == RtspTransport.LowerTransportType.TCP
                        && setupTrack?.Transport is RtpTcpTransport tcpTransport)
                    {
                        tcpTransport.DataChannel = transport.Interleaved?.First ?? tcpTransport.DataChannel;
                        tcpTransport.ControlChannel = transport.Interleaved?.Second ?? tcpTransport.ControlChannel;
                    }

                    if (setupTrack?.Transport != null)
                    {
                        // Kept on the track so the same delegate can be taken off again when the
                        // session ends.
                        ClientTrack bound = setupTrack;

                        bound.OnData = (s2, e2) => RtpDataReceived(bound, e2);
                        bound.OnControl = (s2, e2) => RtcpControlDataReceived(bound, s2, e2);

                        bound.Transport.DataReceived += bound.OnData;
                        bound.Transport.ControlReceived += bound.OnControl;
                        bound.Transport.Start();
                    }
                }

                // Check if we have another SETUP command to send, then remote it from the list
                RtspRequestSetup nextSetup = null;
                lock (_setupMessagesLock)
                {
                    if (_setupMessages.Count > 0)
                    {
                        nextSetup = _setupMessages.Dequeue();
                    }
                }

                if (nextSetup != null)
                {
                    // send the next SETUP message, after adding in the 'session'
                    nextSetup.Session = _session;
                    _rtspClient?.SendMessage(nextSetup);
                }
                else
                {
                    // use the event for setup completed, so the main program can call the Play command with or without the playback request.
                    SetupMessageCompleted?.Invoke(this, EventArgs.Empty);

                    if (AutoPlay)
                    {
                        Play();
                    }
                }
            }

            // If we get a reply to PLAY (which was our fourth command), then we should have video being received
            if (message.OriginalRequest is RtspRequestPlay)
            {
                _logger.LogDebug("Got reply from Play {command} ", message.Command);
            }
        }

        private void HandleDescribeResponse(RtspResponse message)
        {
            if (message.Data.IsEmpty)
            {
                _logger.LogWarning("Invalid SDP");
                return;
            }

            // Examine the SDP
            string sdpText = Encoding.UTF8.GetString(message.Data.Span.ToArray());

            // Redacted, because an SDP describing a protected stream carries the SRTP master key in
            // its crypto attribute - so logging it verbatim wrote the key that protects the media
            // into the log, where it long outlives the session and is readable by anyone who can
            // read the log. The samples run at Debug.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("SDP:\n{sdp}", RedactKeys(sdpText));
            }

            SdpFile sdpData;
            using(var ms = new MemoryStream(message.Data.Span.ToArray()))
            using (StreamReader sdpStream = new StreamReader(ms))
            {
                sdpData = SdpFile.ReadLoose(sdpStream);
            }

            // For old sony cameras, we need to use the control uri from the sdp
            var customControlUri = sdpData.Attributs.FirstOrDefault(x => x.Key == "control");
            if (customControlUri != null && !string.Equals(customControlUri.Value, "*"))
            {
                var sessionControlUri = new Uri(_uri, customControlUri.Value);

                // the SDP is server supplied, so it must not be able to aim us at a different host
                if (IsSameOrigin(_uri, sessionControlUri))
                {
                    _uri = sessionControlUri;
                }
                else
                {
                    _logger.LogWarning("Ignoring the session control URI {controlUri} from the SDP, it points away from {uri}", sessionControlUri, _uri);
                }
            }

            // Process each 'Media' Attribute in the SDP (each sub-stream)
            //  to look for first supported video substream
            {
                foreach (Media media in sdpData.Medias.Where(m => m.MediaType == Media.MediaTypes.video))
                {
                    if (!IsUsablePayloadType(media.PayloadType, "video"))
                    {
                        continue;
                    }

                    // search the attributes for control, rtpmap and fmtp
                    // holds SPS and PPS in base64 (h264 video)
                    if (!CanTakeAnotherTrack("video"))
                    {
                        break;
                    }

                    ClientTrack videoTrack = AddTrack(TrackKind.Video);

                    AttributFmtp fmtp = media.Attributs.FirstOrDefault(x => x.Key == "fmtp") as AttributFmtp;
                    AttributRtpMap rtpmap = media.Attributs.FirstOrDefault(x => x.Key == "rtpmap") as AttributRtpMap;
                    videoTrack.ControlUri = GetControlUri(media);

                    int fmtpPayloadNumber = -1;
                    if (fmtp != null)
                    {
                        fmtpPayloadNumber = fmtp.PayloadNumber;
                    }

                    // extract h265/h266 donl if available...
                    bool hasDonl = false;

                    if (!string.IsNullOrEmpty(fmtp?.FormatParameter))
                    {
                        if (rtpmap?.EncodingName?.ToUpperInvariant().Equals("H265") ?? false)
                        {
                            var param = H265Parameters.Parse(fmtp.FormatParameter);
                            if (param.ContainsKey("sprop-max-don-diff") && int.TryParse(param["sprop-max-don-diff"], out int donl) && donl > 0)
                            {
                                hasDonl = true;
                            }
                        }
                        else if (rtpmap?.EncodingName?.ToUpperInvariant().Equals("H266") ?? false)
                        {
                            var param = H266Parameters.Parse(fmtp.FormatParameter);
                            if (param.ContainsKey("sprop-max-don-diff") && int.TryParse(param["sprop-max-don-diff"], out int donl) && donl > 0)
                            {
                                hasDonl = true;
                            }
                        }
                    }

                    // some cameras are really mess with the payload type.
                    // must check also the rtpmap for the correct format to load (sending an h265 payload when giving an h264 stream [Some Bosch camera])
                    string payloadName = string.Empty;
                    if (rtpmap != null && ((fmtpPayloadNumber > -1 && rtpmap.PayloadNumber == fmtpPayloadNumber) || fmtpPayloadNumber == -1) && rtpmap.EncodingName != null)
                    {
                        // found a valid codec
                        payloadName = rtpmap.EncodingName.ToUpperInvariant();
                        switch (payloadName)
                        {
                            case "H264":
                                videoTrack.Processor = new H264Payload(_loggerFactory.CreateLogger<H264Payload>());
                                break;
                            case "H265":
                                videoTrack.Processor = new H265Payload(hasDonl, _loggerFactory.CreateLogger<H265Payload>());
                                break;
                            case "H266":
                                videoTrack.Processor = new H266Payload(hasDonl, _loggerFactory.CreateLogger<H266Payload>());
                                break;
                            case "AV1":
                                videoTrack.Processor = new AV1Payload(_loggerFactory.CreateLogger<AV1Payload>());
                                break;
                            case "JPEG":
                                videoTrack.Processor = new JPEGPayload();
                                break;
                            case "MP4V-ES":
                                videoTrack.Processor = new RawPayload();
                                break;
                            default:
                                videoTrack.Processor = null;
                                break;
                        }
                        videoTrack.PayloadType = media.PayloadType;
                    }
                    else
                    {
                        videoTrack.PayloadType = media.PayloadType;
                        if (media.PayloadType < 96)
                        {
                            // PayloadType is a static value, so we can use it to determine the codec
                            switch (media.PayloadType)
                            {
                                case 26:
                                    {
                                        videoTrack.Processor = new JPEGPayload();
                                        payloadName = "JPEG";
                                    }
                                    break;
                                case 33:
                                    {
                                        videoTrack.Processor = new MP2TransportPayload();
                                        payloadName = "MP2T";
                                    }
                                    break;
                                default:
                                    {
                                        videoTrack.Processor = null;
                                        payloadName = string.Empty;
                                    }
                                    break;
                            }
                        }
                    }

                    IStreamConfigurationData streamConfigurationData = null;

                    try
                    {
                        if (videoTrack.Processor is H264Payload && fmtp?.FormatParameter != null)
                        {
                            // If the rtpmap contains H264 then split the fmtp to get the sprop-parameter-sets which hold the SPS and PPS in base64
                            var param = H264Parameters.Parse(fmtp.FormatParameter);
                            var spsPps = param.SpropParameterSets;
                            if (spsPps.Count >= 2)
                            {
                                byte[] sps = spsPps[0];
                                byte[] pps = spsPps[1];
                                streamConfigurationData = new H264StreamConfigurationData(sps, pps);
                            }
                        }
                        else if (videoTrack.Processor is H265Payload && fmtp?.FormatParameter != null)
                        {
                            // If the rtpmap contains H265 then split the fmtp to get the sprop-vps, sprop-sps and sprop-pps
                            // The RFC makes the VPS, SPS and PPS OPTIONAL so they may not be present. In which we pass back NULL values
                            var param = H265Parameters.Parse(fmtp.FormatParameter);
                            var vpsSpsPps = param.SpropParameterSets;
                            if (vpsSpsPps.Count >= 3)
                            {
                                byte[] vps = vpsSpsPps[0];
                                byte[] sps = vpsSpsPps[1];
                                byte[] pps = vpsSpsPps[2];
                                streamConfigurationData = new H265StreamConfigurationData(vps, sps, pps);
                            }
                            else if (vpsSpsPps.Count >= 2)
                            {
                                // some implementations only send SPS and PPS, e.g. some HikVision cameras
                                byte[] sps = vpsSpsPps[0];
                                byte[] pps = vpsSpsPps[1];
                                streamConfigurationData = new H265StreamConfigurationData(null, sps, pps);
                            }
                        }
                        else if (videoTrack.Processor is H266Payload && fmtp?.FormatParameter != null)
                        {
                            // If the rtpmap contains H266 then split the fmtp to get the sprop-dci, sprop-vps, sprop-sps, sprop-pps and sprop-sei
                            // The RFC makes the DCI, VPS, SPS and PPS OPTIONAL so they may not be present. In which we pass back NULL values
                            var param = H266Parameters.Parse(fmtp.FormatParameter);
                            var vpsSpsPps = param.SpropParameterSets;
                            if (vpsSpsPps.Count >= 5)
                            {
                                byte[] dci = vpsSpsPps[0];
                                byte[] vps = vpsSpsPps[1];
                                byte[] sps = vpsSpsPps[2];
                                byte[] pps = vpsSpsPps[3];
                                byte[] sei = vpsSpsPps[4];
                                streamConfigurationData = new H266StreamConfigurationData(dci, vps, sps, pps, sei);
                            }
                        }
                        else if (videoTrack.Processor is AV1Payload && fmtp?.FormatParameter != null)
                        {
                            var param = AV1Parameters.Parse(fmtp.FormatParameter);
                            // TODO: the rtpmap contains AV1
                        }

                        }
                        catch (Exception ex)
                        {
                            // A stream whose fmtp is missing altogether plays perfectly well, because the
                            // parameter sets arrive in the stream itself - so one that is present and
                            // malformed has no business being worse than that. It used to take the whole
                            // session down as a bare protocol error, naming nothing.
                            _logger.LogWarning(ex,
                                "Ignoring the format parameters of the video stream, they could not be read: {formatParameter}",
                                fmtp?.FormatParameter);
                            streamConfigurationData = null;
                        }

                    // Kept because a sender report is useless without it: it says when a given RTP
                    // timestamp happened, and turning the gap to another timestamp into a span of
                    // time needs to know how fast this stream's clock runs.
                    videoTrack.Rtcp.ClockRate = ClockRateOf(rtpmap, DEFAULT_VIDEO_CLOCK_RATE);

                    // Send the SETUP RTSP command if we have a matching Payload Decoder
                    if (videoTrack.Processor == null)
                    {
                        // Nothing here can read it, so it is not a track this client has - it is not
                        // offered, and it does not count against the tracks that were.
                        DropTrack(videoTrack);
                        continue;
                    }

                    {
                        // Whether this client wants the track at all. Asked here rather than
                        // earlier because this is the first point at which the codec is known, and
                        // before anything is bound for it: a track passed over costs no transport,
                        // no SETUP, and nothing on the wire.
                        if (!IsWanted(videoTrack, payloadName, media.PayloadType, SectionIndexOf(sdpData, media)))
                        {
                            DropTrack(videoTrack);
                            continue;
                        }

                        // Made here rather than when the track was, because this is the first point at
                        // which the track is known to be one this client can play. Built up front, a
                        // description offering many sections of a codec nothing here reads bound a pair
                        // of UDP ports for every one of them - and never gave them back until the
                        // session ended.
                        videoTrack.Transport = videoTrack.Transport ?? CreateTransport();

                        RtspTransport transport = CalculateTransport(videoTrack.Transport);

                        // Generate SETUP messages
                        if (transport != null)
                        {
                            RtspRequestSetup setupMessage = new RtspRequestSetup()
                            {
                                RtspUri = videoTrack.ControlUri
                            };
                            setupMessage.AddTransport(transport);
                            setupMessage.AddAuthorization(_authentication, _uri, _rtspSocket.NextCommandIndex());
                            if (_playbackSession) { setupMessage.AddRequireOnvifRequest(); }

                            // Add SETUP message to list of mesages to send
                            lock (_setupMessagesLock)
                            {
                                _setupMessages.Enqueue(setupMessage);
                            }

                            videoTrack.Context = PrepareSrtpContext(media);
                            if (!HasTheKeyItNeeds(media, videoTrack.Context, "video"))
                            {
                                return;
                            }

                            videoTrack.Codec = payloadName;
                            videoTrack.Configuration = streamConfigurationData;

                            NewTrack?.Invoke(this, new NewTrackEventArgs(videoTrack.Index, TrackKind.Video,
                                media.PayloadType, payloadName, streamConfigurationData));
                        }

                    }
                }
            }

            {
                foreach (Media media in sdpData.Medias.Where(m => m.MediaType == Media.MediaTypes.audio))
                {
                    if (!IsUsablePayloadType(media.PayloadType, "audio"))
                    {
                        continue;
                    }

                    // search the attributes for control, rtpmap and fmtp
                    if (!CanTakeAnotherTrack("audio"))
                    {
                        break;
                    }

                    ClientTrack audioTrack = AddTrack(TrackKind.Audio);

                    AttributFmtp fmtp = media.Attributs.FirstOrDefault(x => x.Key == "fmtp") as AttributFmtp;
                    AttributRtpMap rtpmap = media.Attributs.FirstOrDefault(x => x.Key == "rtpmap") as AttributRtpMap;

                    audioTrack.ControlUri = GetControlUri(media);
                    audioTrack.PayloadType = media.PayloadType;
                    audioTrack.Rtcp.ClockRate = ClockRateOf(rtpmap, DEFAULT_AUDIO_CLOCK_RATE);

                    IStreamConfigurationData streamConfigurationData = null;
                    if (media.PayloadType < 96)
                    {
                        // fixed payload type
                        switch(media.PayloadType)
                        {
                            case 0:
                                (audioTrack.Processor, audioTrack.Codec) = (new G711Payload(), "PCMU");
                                break;
                            case 8:
                                (audioTrack.Processor, audioTrack.Codec) = (new G711Payload(), "PCMA");
                                break;
                            default:
                                (audioTrack.Processor, audioTrack.Codec) = (null, "");
                                break;
                        }
                    }
                    else
                    {
                        // dynamic payload type
                        audioTrack.Codec = rtpmap?.EncodingName?.ToUpperInvariant() ?? string.Empty;
                        switch(audioTrack.Codec)
                        {
                            // Create AAC RTP Parser
                            // Example fmtp is "96 profile-level-id=1;mode=AAC-hbr;sizelength=13;indexlength=3;indexdeltalength=3;config=1490"
                            // Example fmtp is ""96 streamtype=5;profile-level-id=1;mode=AAC-hbr;sizelength=13;indexlength=3;indexdeltalength=3;config=1210"
                            case "MPEG4-GENERIC":
                                {
                                    if (fmtp?["mode"].ToLowerInvariant() == "aac-hbr")
                                        audioTrack.Processor = new AACPayload(fmtp["config"]);
                                }
                                break;
                            case "PCMA":
                                audioTrack.Processor = new G711Payload();
                                break;
                            case "PCMU":
                                audioTrack.Processor = new G711Payload();
                                break;
                            case "AMR":
                                audioTrack.Processor = new AMRPayload();
                                break;
                            case "OPUS":
                                audioTrack.Processor = new OpusPayload();
                                break;
                            case "G726-16":
                            case "G726-24":
                            case "G726-32":
                            case "G726-40":
                                // The payload is the ADPCM and nothing else - no header, no framing.
                                // What a decoder needs in order to read it is the rate, which is in
                                // the codec name and is reported to the caller as part of it.
                                audioTrack.Processor = new RawPayload();
                                break;
                            default:
                                audioTrack.Processor = null;
                                break;
                        }
                        if (audioTrack.Processor is AACPayload aacPayloadProcessor)
                        {
                            audioTrack.Codec = "AAC";
                            // The payload processor reports the index but leaves the frequency itself
                            // at zero, so derive it here rather than hand callers a meaningless value.
                            int samplingFrequency = aacPayloadProcessor.SamplingFrequency > 0
                                ? aacPayloadProcessor.SamplingFrequency
                                : AACStreamConfigurationData.GetSamplingFrequency(aacPayloadProcessor.FrequencyIndex);

                            streamConfigurationData = new AACStreamConfigurationData()
                            {
                                ObjectType = aacPayloadProcessor.ObjectType,
                                FrequencyIndex = aacPayloadProcessor.FrequencyIndex,
                                SamplingFrequency = samplingFrequency,
                                ChannelConfiguration = aacPayloadProcessor.ChannelConfiguration
                            };
                        }
                    }

                    // Send the SETUP RTSP command if we have a matching Payload Decoder
                    if (audioTrack.Processor == null)
                    {
                        // Nothing here can read it, so it is not a track this client has - it is not
                        // offered, and it does not count against the tracks that were.
                        DropTrack(audioTrack);
                        continue;
                    }

                    {
                        // Whether this client wants the track at all. Asked here rather than
                        // earlier because this is the first point at which the codec is known, and
                        // before anything is bound for it: a track passed over costs no transport,
                        // no SETUP, and nothing on the wire.
                        if (!IsWanted(audioTrack, audioTrack.Codec, media.PayloadType, SectionIndexOf(sdpData, media)))
                        {
                            DropTrack(audioTrack);
                            continue;
                        }

                        // Made here rather than when the track was, because this is the first point at
                        // which the track is known to be one this client can play. Built up front, a
                        // description offering many sections of a codec nothing here reads bound a pair
                        // of UDP ports for every one of them - and never gave them back until the
                        // session ended.
                        audioTrack.Transport = audioTrack.Transport ?? CreateTransport();

                        RtspTransport transport = CalculateTransport(audioTrack.Transport);

                        // Generate SETUP messages
                        if (transport != null)
                        {
                            RtspRequestSetup setupMessage = new RtspRequestSetup()
                            {
                                RtspUri = audioTrack.ControlUri,
                            };
                            setupMessage.AddTransport(transport);
                            setupMessage.AddAuthorization(_authentication, _uri, _rtspSocket.NextCommandIndex());
                            if (_playbackSession)
                            {
                                setupMessage.AddRequireOnvifRequest();
                                setupMessage.AddRateControlOnvifRequest(false);
                            }
                            // Add SETUP message to list of mesages to send
                            lock (_setupMessagesLock)
                            {
                                _setupMessages.Enqueue(setupMessage);
                            }

                            audioTrack.Context = PrepareSrtpContext(media);
                            if (!HasTheKeyItNeeds(media, audioTrack.Context, "audio"))
                            {
                                return;
                            }


                            audioTrack.Configuration = streamConfigurationData;

                            NewTrack?.Invoke(this, new NewTrackEventArgs(audioTrack.Index, TrackKind.Audio,
                                media.PayloadType, audioTrack.Codec, streamConfigurationData));
                        }

                    }
                }
            }

            // Everything that is neither sound nor pictures: metadata, and anything else a stream
            // chooses to describe as an application section. Offered like any other track, where it
            // used to be reachable only by turning every extra track of every kind on at once.
            {
                foreach (Media media in sdpData.Medias.Where(m => m.MediaType == Media.MediaTypes.application))
                {
                    if (!IsUsablePayloadType(media.PayloadType, "application"))
                    {
                        continue;
                    }

                    if (!CanTakeAnotherTrack("application"))
                    {
                        break;
                    }

                    ClientTrack track = AddTrack(TrackKind.Application);
                    track.ControlUri = GetControlUri(media);
                    track.PayloadType = media.PayloadType;

                    AttributRtpMap rtpmap = media.Attributs.FirstOrDefault(x => x.Key == "rtpmap") as AttributRtpMap;
                    track.Codec = rtpmap?.EncodingName ?? "";
                    track.Rtcp.ClockRate = ClockRateOf(rtpmap, DEFAULT_VIDEO_CLOCK_RATE);

                    // Nothing here reads what is inside a document, so the packets are put back
                    // together and handed over as they are. What says where one ends is the marker
                    // bit, since this kind of payload has no framing of its own.
                    track.Processor = new MarkerFramedPayload();

                    // Whether this client wants the track at all, before anything is bound for it.
                    if (!IsWanted(track, track.Codec, media.PayloadType, SectionIndexOf(sdpData, media)))
                    {
                        DropTrack(track);
                        continue;
                    }

                    // Made here rather than when the track was, because this is the first point at
                    // which the track is known to be one this client can play. Built up front, a
                    // description offering many sections of a codec nothing here reads bound a pair
                    // of UDP ports for every one of them - and never gave them back until the
                    // session ended.
                    track.Transport = track.Transport ?? CreateTransport();

                    RtspTransport transport = CalculateTransport(track.Transport);

                    if (transport == null)
                    {
                        continue;
                    }

                    var setupMessage = new RtspRequestSetup { RtspUri = track.ControlUri };
                    setupMessage.AddTransport(transport);
                    setupMessage.AddAuthorization(_authentication, _uri, _rtspSocket.NextCommandIndex());
                    if (_playbackSession) { setupMessage.AddRequireOnvifRequest(); }

                    lock (_setupMessagesLock)
                    {
                        _setupMessages.Enqueue(setupMessage);
                    }

                    track.Context = PrepareSrtpContext(media);
                    if (!HasTheKeyItNeeds(media, track.Context, "application"))
                    {
                        return;
                    }

                    NewTrack?.Invoke(this, new NewTrackEventArgs(track.Index, TrackKind.Application,
                        media.PayloadType, track.Codec, null));
                }
            }

            RtspRequestSetup firstSetup = null;
            lock (_setupMessagesLock)
            {
                if (_setupMessages.Count > 0)
                {
                    firstSetup = _setupMessages.Dequeue();
                }
            }

            if (firstSetup == null)
            {
                // The SDP described nothing we can play, so there is no session to set up.
                _logger.LogError("Unable to set up a media stream, the SDP has no supported media");
                TeardownClient();
                Stopped?.Invoke(this, new StoppedEventArgs(StoppedReason.UnsupportedMedia));
                return;
            }

            // Send the FIRST SETUP message and remove it from the list of Setup Messages
            _rtspClient?.SendMessage(firstSetup);
        }

        /// <summary>
        /// An SDP with the key material in its crypto attributes taken out, for logging.
        /// </summary>
        /// <remarks>
        /// RFC 4568 puts the SRTP master key and salt in "a=crypto" as "inline:" followed by base64.
        /// Everything else in the line - the tag, the suite, the lifetime, the MKI - says what is
        /// being done rather than with what, and is worth keeping in a log.
        /// </remarks>
        internal static string RedactKeys(string sdp)
        {
            if (string.IsNullOrEmpty(sdp) || sdp.IndexOf("inline:", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return sdp;
            }

            // Up to the next character that ends the key: a separator within the parameter, or
            // whitespace ending it. The rest of the line is left as it is.
            return System.Text.RegularExpressions.Regex.Replace(
                sdp,
                "inline:[^|;\\s]+",
                "inline:<redacted>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Whether a media section says its RTP is encrypted.
        /// </summary>
        internal static bool RequiresSrtp(Media media)
        {
            return media?.RtpType != null
                && (media.RtpType.EndsWith("/SAVP") || media.RtpType.EndsWith("/SAVPF"));
        }

        public virtual SrtpSessionContext PrepareSrtpContext(Media media)
        {
            if (RequiresSrtp(media))
            {
                var crypto = media.Attributs.FirstOrDefault(x => x.Key == "crypto");
                if (crypto != null)
                {
                    byte[] MKI = null;
                    byte[] masterKeySalt = null;

                    string[] cryptoParts = crypto.Value.Split(' ');
                    if (cryptoParts.Length == 3)
                    {
                        string cryptoSuite = cryptoParts[1];

                        if (cryptoParts[2].StartsWith("inline:"))
                        {
                            string[] inlineParts = cryptoParts[2].Substring(7).Split('|');

                            // the SDP comes from the server, so it cannot be assumed to be well formed
                            try
                            {
                                masterKeySalt = Convert.FromBase64String(inlineParts[0]);
                            }
                            catch (FormatException ex)
                            {
                                _logger.LogError(ex, "Invalid base64 master key/salt in the SDP crypto attribute");
                                return null;
                            }

                            if (inlineParts.Length > 1)
                            {
                                if (inlineParts.Length > 2)
                                {
                                    string lifetime = inlineParts[1];
                                    MKI = ParseMKI(inlineParts[2]);
                                }
                                else if (inlineParts[1].Contains(':'))
                                {
                                    MKI = ParseMKI(inlineParts[1]);
                                }
                            }

                            try
                            {
                                SrtpKeys keys = SrtpProtocol.CreateMasterKeys(cryptoSuite, MKI, masterKeySalt);
                                return SrtpProtocol.CreateSrtpSessionContext(keys);
                            }
                            catch (Exception ex)
                            {
                                // A key of the wrong length, or a suite we do not implement. Both used
                                // to travel up to the catch-all and reach the caller as a bare protocol
                                // error, with nothing pointing at the crypto attribute that caused it.
                                _logger.LogError(ex,
                                    "Cannot use the SDP crypto attribute: suite {cryptoSuite}, {keyLength} byte key",
                                    cryptoSuite, masterKeySalt.Length);
                                return null;
                            }
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Largest MKI we are prepared to accept from an SDP, in bytes. RFC 4568 puts the limit at 128.
        /// </summary>
        private const int MAX_MKI_LENGTH = 128;

        /// <summary>
        /// Parses the "&lt;mki&gt;:&lt;length&gt;" part of an SDP crypto attribute.
        /// The value comes straight from the server, so every field is validated before it is used.
        /// </summary>
        /// <summary>
        /// Checks that a stream describing itself as encrypted came with a key we can use, and stops
        /// the client if it did not.
        /// </summary>
        /// <remarks>
        /// Without this the context stays null, and a null context means the receive path never
        /// attempts to decrypt - so the client played a stream that was supposed to be encrypted as
        /// though it were plain RTP, and said nothing. Anyone able to alter the SDP could arrange
        /// that by deleting one line of it.
        /// </remarks>
        private bool HasTheKeyItNeeds(Media media, SrtpSessionContext context, string what)
        {
            if (!RequiresSrtp(media) || context != null)
            {
                return true;
            }

            _logger.LogError(
                "The {what} stream is {rtpType} but the SDP gave no usable key for it, so it will not be played",
                what, media.RtpType);

            StopClient();
            Stopped?.Invoke(this, new StoppedEventArgs(StoppedReason.EncryptionUnavailable));
            return false;
        }

        /// <summary>
        /// The clock rate of a stream whose rtpmap did not give one. 90 kHz is what every video
        /// payload RFC 3551 defines uses.
        /// </summary>
        private const int DEFAULT_VIDEO_CLOCK_RATE = 90000;

        /// <summary>
        /// The clock rate of an audio stream whose rtpmap did not give one. Only the static payload
        /// types come without an rtpmap, and those are all 8 kHz.
        /// </summary>
        private const int DEFAULT_AUDIO_CLOCK_RATE = 8000;

        /// <summary>
        /// The clock rate an rtpmap declares, falling back to the one the payload type implies.
        /// </summary>
        private int ClockRateOf(AttributRtpMap rtpmap, int fallback)
        {
            if (rtpmap?.ClockRate != null && int.TryParse(rtpmap.ClockRate, out int clockRate) && clockRate > 0)
            {
                return clockRate;
            }

            _logger.LogDebug("No usable clock rate in the rtpmap, assuming {fallback}", fallback);
            return fallback;
        }

        /// <summary>
        /// Whether a payload number from the SDP is one that can ever turn up in an RTP packet.
        /// </summary>
        /// <remarks>
        /// The field in an RTP header is seven bits wide, so anything above 127 can be advertised but
        /// never received. Taken at face value it produced a session that set up, played, reported
        /// nothing, and quietly discarded every packet that arrived for not matching.
        /// </remarks>
        private bool IsUsablePayloadType(int payloadType, string what)
        {
            const int maxPayloadType = 127;

            if (payloadType >= 0 && payloadType <= maxPayloadType)
            {
                return true;
            }

            _logger.LogWarning(
                "Ignoring the {what} stream, its payload type {payloadType} is outside the 0 to {maxPayloadType} an RTP packet can carry",
                what, payloadType, maxPayloadType);
            return false;
        }

        internal byte[] ParseMKI(string sdpMki)
        {
            string[] mkiParts = sdpMki.Split(':');
            if (mkiParts.Length != 2)
            {
                return null;
            }

            if (!int.TryParse(mkiParts[0], out int mki) || mki < 0)
            {
                _logger.LogWarning("Ignoring SDP crypto MKI with an invalid value {mki}", mkiParts[0]);
                return null;
            }

            if (!int.TryParse(mkiParts[1], out int mkiLength) || mkiLength <= 0 || mkiLength > MAX_MKI_LENGTH)
            {
                _logger.LogWarning("Ignoring SDP crypto MKI with an invalid length {mkiLength}", mkiParts[1]);
                return null;
            }

            byte[] mkiValue = new BigInteger(mki).ToByteArray();
            if (mkiValue.Length > mkiLength)
            {
                _logger.LogWarning("Ignoring SDP crypto MKI {mki} which does not fit into the declared length {mkiLength}", mki, mkiLength);
                return null;
            }

            byte[] MKI = new byte[mkiLength];
            Buffer.BlockCopy(mkiValue, 0, MKI, 0, mkiValue.Length);
            return MKI;
        }

        private Uri GetControlUri(Media media)
        {
            Uri controlUri = null;
            var attrib = media.Attributs.FirstOrDefault(a => a.Key == "control");
            if (attrib != null)
            {
                string sdpControl = attrib.Value;

                if (sdpControl.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) ||
                    sdpControl.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase) ||
                    sdpControl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    sdpControl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    )
                {
                    // the "track" or "stream id"
                    string control = sdpControl; //absolute path
                    controlUri = new Uri(control);
                }
                else
                {
                    // add trailing / if necessary
                    var baseUriWithTrailingSlash = _uri.ToString().EndsWith("/") ? _uri : new Uri($"{_uri}/");
                    // relative path
                    controlUri = new Uri(baseUriWithTrailingSlash, sdpControl);
                }
            }

            if (controlUri != null && !IsSameOrigin(_uri, controlUri))
            {
                _logger.LogWarning("Ignoring the control URI {controlUri} from the SDP, it points away from {uri}", controlUri, _uri);
                return null;
            }

            return controlUri;
        }

        /// <summary>
        /// Checks that a URI taken from the SDP still points at the server we connected to.
        /// </summary>
        /// <remarks>
        /// The SDP is supplied by the server, and the URIs in it are used to build authenticated
        /// requests. An absolute URI naming a different host would send the digest response - computed
        /// over that URI - somewhere we never meant to talk to.
        /// </remarks>
        internal static bool IsSameOrigin(Uri expected, Uri actual)
        {
            if (expected == null || actual == null)
            {
                return false;
            }

            return string.Equals(expected.Scheme, actual.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(expected.Host, actual.Host, StringComparison.OrdinalIgnoreCase)
                && expected.Port == actual.Port;
        }

        private RtspTransport CalculateTransport(IRtpTransport transport)
        {
            switch(_rtpTransport)
            {
                // Server interleaves the RTP packets over the RTSP connection
                // Example for TCP mode (RTP over RTSP)   Transport: RTP/AVP/TCP;interleaved=0-1
                case RTPTransport.TCP:
                    return new RtspTransport()
                    {
                        LowerTransport = RtspTransport.LowerTransportType.TCP,
                        // Eg Channel 0 for RTP video data. Channel 1 for RTCP status reports
                        Interleaved = (transport as RtpTcpTransport)?.Channels ?? throw new InvalidOperationException("TCP transport asked and no tcp channel allocated"),
                    };
                case RTPTransport.UDP:
                    return new RtspTransport()
                    {
                        LowerTransport = RtspTransport.LowerTransportType.UDP,
                        IsMulticast = false,
                        ClientPort = (transport as UDPSocket)?.Ports ?? throw new InvalidOperationException("UDP transport asked and no udp port allocated"),
                    };
                // Server sends the RTP packets to a Pair of UDP ports (one for data, one for rtcp control messages)
                // using Multicast Address and Ports that are in the reply to the SETUP message
                // Example for MULTICAST mode     Transport: RTP/AVP;multicast
                case RTPTransport.MULTICAST:
                    return new RtspTransport()
                    {
                        LowerTransport = RtspTransport.LowerTransportType.UDP,
                        IsMulticast = true,
                        ClientPort = new PortCouple(5000, 5001)
                    };
                default:
                    return null;
            }
        }

        private void SendKeepAlive(object sender, System.Timers.ElapsedEventArgs e)
        {
            // Send Keepalive message
            // The ONVIF Standard uses SET_PARAMETER as "an optional method to keep an RTSP session alive"
            // RFC 2326 (RTSP Standard) says "GET_PARAMETER with no entity body may be used to test client or server liveness("ping")"

            // This code uses GET_PARAMETER (unless OPTIONS report it is not supported, and then it sends OPTIONS as a keepalive)

            try
            {
                // The timer can fire while the session is being torn down, so take one copy of everything
                // this needs and give up quietly if the teardown got there first.
                var rtspSocket = _rtspSocket;
                var rtspClient = _rtspClient;
                var uri = _uri;

                if (rtspSocket == null || rtspClient == null || uri == null)
                {
                    return;
                }

                RtspRequest keepAliveMessage;
                if (_serverSupportsGetParameter)
                {
                    keepAliveMessage = new RtspRequestGetParameter
                    {
                        RtspUri = uri,
                        Session = _session
                    };
                }
                else
                {
                    keepAliveMessage = new RtspRequestOptions
                    {
                        RtspUri = uri,
                        Session = _session
                    };
                }

                keepAliveMessage.AddAuthorization(_authentication, uri, rtspSocket.NextCommandIndex());
                rtspClient.SendMessage(keepAliveMessage);
            }
            catch (Exception ex)
            {
                // this runs on a timer thread, an escaping exception would take the process down
                _logger.LogWarning(ex, "Failed to send the RTSP keepalive");
            }
        }

        #region IDisposable

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    StopClient();
                }

                _disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        #endregion // IDisposable
    }

    public interface IStreamConfigurationData
    { }

    public enum StoppedReason
    {
        Unknown,
        ConnectionFailed,
        Unauthorized,
        RtcpBye,
        NotFound,

        /// <summary>
        /// The server rejected a request with an error we cannot recover from.
        /// </summary>
        ServerError,

        /// <summary>
        /// The server's SDP did not describe any media this client can play.
        /// </summary>
        UnsupportedMedia,

        /// <summary>
        /// The RTSP dialog failed unexpectedly. The exception is written to the log.
        /// </summary>
        ProtocolError,

        /// <summary>
        /// The server described the media as encrypted but did not provide a key this client can
        /// use, so it stopped rather than carry on and accept the media unencrypted.
        /// </summary>
        EncryptionUnavailable,
    }

    public class StoppedEventArgs : EventArgs
    {
        public StoppedEventArgs(StoppedReason reason)
        {
            this.Reason = reason;
        }

        public StoppedReason Reason { get; private set; }
    }

    public class SimpleDataEventArgs : EventArgs
    {
        public SimpleDataEventArgs(IEnumerable<ReadOnlyMemory<byte>> data, DateTime timestamp, uint rtpTimestamp)
            : this(data, timestamp, rtpTimestamp, false)
        {
        }

        public SimpleDataEventArgs(IEnumerable<ReadOnlyMemory<byte>> data, DateTime timestamp, uint rtpTimestamp, bool hasSenderSync)
        {
            Data = data;
            Timestamp = timestamp;
            RtpTimestamp = rtpTimestamp;
            HasSenderSync = hasSenderSync;
        }

        /// <summary>
        /// Whether <see cref="Timestamp"/> is this frame's time on the sender's clock, and so
        /// comparable with the other stream of the same session.
        /// </summary>
        /// <remarks>
        /// <para>
        /// False until a sender report has arrived for this stream, which is what pairs its RTP
        /// clock with a wall clock. Until then the two streams cannot be lined up at all: each runs
        /// at its own rate from a starting point the sender picked at random, so their RTP
        /// timestamps say nothing about one another. A sender that never reports is never
        /// synchronisable, and this stays false for the life of the session.
        /// </para>
        /// <para>
        /// The sender's clock need not agree with this machine's, or with real time. Both streams
        /// are timed by the one clock at the far end, which is all that lining them up against
        /// each other requires.
        /// </para>
        /// </remarks>
        public bool HasSenderSync { get; }

        public DateTime Timestamp { get; }
        public uint RtpTimestamp { get; }
        public IEnumerable<ReadOnlyMemory<byte>> Data { get; }

        public override string ToString()
        {
            return $"{Timestamp}: Data {Data.Count()}";
        }
    }

    public class RawRtpDataEventArgs : EventArgs
    {
        public ReadOnlyMemory<byte> Data { get; }
        public int CsrcCount { get; }
        public int? ExtensionHeaderId { get; }
        public bool HasPadding { get; }
        public bool IsMarker { get; }
        public bool IsWellFormed { get; }
        public int PayloadSize { get; }
        public int PayloadType { get; }
        public int SequenceNumber { get; }
        public uint Ssrc { get; }
        public uint Timestamp { get; }
        public int Version { get; }
        public int PayloadStart { get; }

        public RawRtpDataEventArgs(
            ReadOnlyMemory<byte> data,
            int csrcCount,
            int? extensionHeaderId, 
            bool hasPadding, 
            bool isMarker, 
            bool isWellFormed, 
            int payloadSize, 
            int payloadType,
            int sequenceNumber, 
            uint ssrc, 
            uint timestamp,
            int version,
            int payloadStart)
        {
            Data = data;
            CsrcCount = csrcCount;
            ExtensionHeaderId = extensionHeaderId;
            HasPadding = hasPadding;
            IsMarker = isMarker;
            IsWellFormed = isWellFormed;
            PayloadSize = payloadSize;
            PayloadType = payloadType;
            SequenceNumber = sequenceNumber;
            Ssrc = ssrc;
            Timestamp = timestamp;
            Version = version;
            PayloadStart = payloadStart;
        }

        public override string ToString()
        {
            return $"RTP {Timestamp}: Data {Data.Length}";
        }
    }

    public class RawRtcpDataEventArgs : EventArgs
    {
        public RawRtcpDataEventArgs(ReadOnlyMemory<byte> data)
        {
            Data = data;
        }

        public ReadOnlyMemory<byte> Data { get; }

        public override string ToString()
        {
            return $"Data {Data.Length}";
        }
    }

    public static class RTSPMessageAuthExtensions
    {
        public static void AddAuthorization(this RtspRequest request, Authentication authentication, Uri uri, uint commandCounter)
        {
            if (authentication == null)
            {
                return;
            }

            string authorization = authentication.GetResponse(commandCounter, uri.AbsoluteUri, request.Request, new byte[0]);

            // remove if already one...
            request.Headers.Remove(RtspHeaderNames.Authorization);
            request.Headers.Add(RtspHeaderNames.Authorization, authorization);
        }
    }

    /// <summary>
    /// Hands out <see cref="ILogger"/>s that write to one <see cref="ILog"/>.
    /// </summary>
    /// <remarks>
    /// What a client builds for itself when it is not given an <see cref="ILoggerFactory"/>. The
    /// logger is looked up per message rather than captured, so <see cref="RTSPClient.Logger"/>
    /// can be assigned after the client exists.
    /// </remarks>
    public class CustomLoggerFactory : ILoggerFactory
    {
        private readonly Func<ILog> _logger;

        /// <summary>
        /// A factory over a logger that may change, which is what a client uses for its own.
        /// </summary>
        public CustomLoggerFactory(Func<ILog> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// A factory over one fixed logger.
        /// </summary>
        public CustomLoggerFactory(ILog logger)
            : this(() => logger)
        {
        }

        /// <summary>
        /// A factory over a <see cref="DefaultLog"/> of its own.
        /// </summary>
        public CustomLoggerFactory()
            : this(new DefaultLog())
        {
        }

        public void AddProvider(ILoggerProvider provider)
        {  }

        public ILogger CreateLogger(string categoryName)
        {
            return new CustomLogger(_logger);
        }

        public void Dispose()
        {  }
    }

    /// <summary>
    /// Presents an <see cref="ILog"/> as the <see cref="ILogger"/> the client's own code writes to.
    /// </summary>
    /// <remarks>
    /// The client is written against <see cref="ILogger"/> throughout, because its messages are
    /// structured - named values rather than strings already glued together - and that is worth
    /// keeping for a host that has somewhere structured to put them. <see cref="ILog"/> is the
    /// simpler thing a host can implement without taking a dependency on anything, and this is what
    /// joins the two.
    /// <para>
    /// The logger is read through a delegate rather than held, so that assigning
    /// <see cref="RTSPClient.Logger"/> after the client is built takes effect. It used to read a
    /// static class, which is why every client in a process shared one.
    /// </para>
    /// </remarks>
    public class CustomLogger : ILogger
    {
        private readonly Func<ILog> _logger;

        /// <param name="logger">Where to look for the logger each time something is written.</param>
        public CustomLogger(Func<ILog> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        class CustomLoggerScope<TState> : IDisposable
        {
            public CustomLoggerScope(TState state)
            {
                State = state;
            }

            public TState State { get; }
            public void Dispose()
            { }
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            return new CustomLoggerScope<TState>(state);
        }

        /// <summary>
        /// Reports whether anything would actually be written at this level.
        /// </summary>
        /// <remarks>
        /// This gates the per-packet logging on the RTP receive path. Answering "true" unconditionally
        /// made every call site build its message and box its arguments before the sink threw the
        /// result away.
        /// </remarks>
        public bool IsEnabled(LogLevel logLevel)
        {
            ILog logger = _logger();

            if (logger == null)
            {
                return false;
            }

            switch (logLevel)
            {
                case LogLevel.Trace:
                    return logger.IsTraceEnabled;
                case LogLevel.Debug:
                    return logger.IsDebugEnabled;
                case LogLevel.Information:
                    return logger.IsInfoEnabled;
                case LogLevel.Warning:
                    return logger.IsWarningEnabled;
                case LogLevel.Error:
                case LogLevel.Critical:
                    return logger.IsErrorEnabled;
                case LogLevel.None:
                    return false;
                default:
                    return true;
            }
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            ILog logger = _logger();

            if (logger == null || formatter == null)
            {
                return;
            }

            switch (logLevel)
            {
                case LogLevel.Trace:
                    logger.Trace(formatter.Invoke(state, exception), exception);
                    break;

                case LogLevel.Debug:
                    logger.Debug(formatter.Invoke(state, exception), exception);
                    break;

                case LogLevel.Information:
                    logger.Info(formatter.Invoke(state, exception), exception);
                    break;

                case LogLevel.Warning:
                    logger.Warning(formatter.Invoke(state, exception), exception);
                    break;

                case LogLevel.Error:
                case LogLevel.Critical:
                    logger.Error(formatter.Invoke(state, exception), exception);
                    break;

                case LogLevel.None:
                    break;

                default:
                    Debug.WriteLine($"Unknown trace level: {logLevel}");
                    break;
            }
        }
    }

    internal class Utilities
    {
        public static byte[] FromHexString(string hex)
        {
#if !NETCOREAPP
            byte[] raw = new byte[hex.Length / 2];
            for (int i = 0; i < raw.Length; i++)
            {
                raw[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return raw;
#else
            return Convert.FromHexString(hex);
#endif
        }

        public static string ToHexString(byte[] data)
        {
#if !NETCOREAPP
            string hexString = BitConverter.ToString(data);
            hexString = hexString.Replace("-", "");
            return hexString;
#else
            return Convert.ToHexString(data);
#endif
        }
    }
}
