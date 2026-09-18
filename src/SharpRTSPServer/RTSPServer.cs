using Microsoft.Extensions.Logging;
using Rtsp;
using Rtsp.Messages;
using SharpRTSPServer.Logging;
using SharpSRTP.SRTP;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SharpRTSPServer
{
    /// <summary>
    /// RTSP Server Example (c) Roger Hardiman, 2016, 2018, 2020, modified by Lukas Volf, 2024
    /// Released under the MIT Open Source Licence
    ///
    /// Re-uses some code from the Multiplexer example of SharpRTSP
    ///
    /// Creates a server to listen for RTSP Commands (eg OPTIONS, DESCRIBE, SETUP, PLAY)
    /// Accepts VPS/SPS/PPS/NAL H264/H265 video data and sends out to RTSP clients
    /// </summary>
    /// <remarks>
    /// Stream with ffmpeg: ffmpeg.exe -re -stream_loop -1 -i frag_bunny.mp4 -vcodec copy -an -f rtp rtp://127.0.0.1:11111 -vn -acodec copy -f rtp rtp://127.0.0.1:11113
    /// </remarks>
    public class RTSPServer : IRtpSender, IDisposable
    {
        /// <summary>
        /// Dynamic RTP payload type.
        /// </summary>
        public const int DYNAMIC_PAYLOAD_TYPE = 96;  // Dynamic payload type base

        private const int RTSP_TIMEOUT = 60;         // 60 seconds

        /// <summary>
        /// SDP lines are CRLF terminated per RFC 4566.
        /// </summary>
        private const string SDP_LINE_ENDING = "\r\n";

        /// <summary>
        /// An RTP/RTCP pair is two consecutive ports, RTP on the first.
        /// </summary>
        private const int PORTS_PER_RTP_PAIR = 2;

        private const int NONCE_BYTES = 16;          // 128 bits of entropy for the digest nonce

        /// <summary>
        /// The realm clients authenticate against.
        /// </summary>
        private const string AUTHENTICATION_REALM = "SharpRTSPServer";
        private const int SESSION_ID_BYTES = 12;     // 96 bits of entropy for the RTSP session ID

        /// <summary>
        /// How often idle connections are swept, in milliseconds.
        /// </summary>
        private const int REAP_INTERVAL = 10_000;

        /// <summary>
        /// How many rotated out nonces are still accepted, beyond the current one.
        /// </summary>
        private const int NONCE_GRACE_COUNT = 1;

        /// <summary>
        /// How many connections may be part way through their handshake at once. Past this the accept
        /// loop waits, so a flood of half open connections cannot use up memory without bound.
        /// </summary>
        private const int MAX_PENDING_HANDSHAKES = 64;

        /// <summary>
        /// How long an RTCP packet waits for its turn on a connection before being given up on.
        /// </summary>
        private static readonly TimeSpan RTCP_SEND_LOCK_TIMEOUT = TimeSpan.FromSeconds(2);

        private const string PUBLIC_HEADER = "Public";

        /// <summary>
        /// The methods this server answers, as reported to a client that asks with OPTIONS.
        /// </summary>
        /// <remarks>
        /// Every one of these is handled; anything not on it is refused with 501. Keep the two in
        /// step - a client is entitled to believe this and to send nothing else.
        /// </remarks>
        public const string SupportedMethods =
            "OPTIONS, DESCRIBE, SETUP, PLAY, PAUSE, GET_PARAMETER, SET_PARAMETER, TEARDOWN";

        /// <summary>
        /// Whether this server implements the method a request asks for.
        /// </summary>
        /// <remarks>
        /// The counterpart of <see cref="SupportedMethods"/>, which is what clients are told. A
        /// method belongs in both or neither.
        /// </remarks>
        private static bool IsImplemented(RtspRequest message)
        {
            switch (message)
            {
                case RtspRequestOptions _:
                case RtspRequestDescribe _:
                case RtspRequestSetup _:
                case RtspRequestPlay _:
                case RtspRequestPause _:
                case RtspRequestGetParameter _:
                case RtspRequestSetParameter _:
                case RtspRequestTeardown _:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// How long closing a connection waits for its writer to leave the transport before giving up
        /// on disposing it. The socket is already closed by then, so a write in progress has failed.
        /// </summary>
        private static readonly TimeSpan CLOSE_WAIT_FOR_WRITER = TimeSpan.FromSeconds(5);

        /// <summary>
        /// How long closing a connection waits for its writer before deciding this is not the
        /// ordinary end of a session and the socket has to be closed to break the write.
        /// </summary>
        private static readonly TimeSpan QUIET_CLOSE_WAIT_FOR_WRITER = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// How long handing a connection's UDP ports back waits for its writer to be out of them. A
        /// UDP send waits on nothing at the far end, so this only has to cover the send itself.
        /// </summary>
        private static readonly TimeSpan RELEASE_WAIT_FOR_WRITER = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Default value of <see cref="MaxQueuedFramesPerConnection"/>. At twenty five frames a
        /// second this is a couple of seconds of video.
        /// </summary>
        public const int DEFAULT_MAX_QUEUED_FRAMES = 64;

        /// <summary>
        /// Default value of <see cref="MaxQueuedBytesPerConnection"/>.
        /// </summary>
        public const long DEFAULT_MAX_QUEUED_BYTES = 4L * 1024 * 1024;

        /// <summary>
        /// Default value of <see cref="RtcpSenderReportInterval"/>.
        /// </summary>
        public static readonly TimeSpan DEFAULT_RTCP_INTERVAL = TimeSpan.FromSeconds(5);

        /// <summary>
        /// How long the accept loop waits after a failure before trying again.
        /// </summary>
        private static readonly TimeSpan ACCEPT_RETRY_DELAY = TimeSpan.FromMilliseconds(50);

        /// <summary>
        /// Default value of <see cref="HandshakeTimeout"/>.
        /// </summary>
        public static readonly TimeSpan DEFAULT_HANDSHAKE_TIMEOUT = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Default value of <see cref="NonceLifetime"/>.
        /// </summary>
        /// <remarks>
        /// Zero, meaning the nonce is not rotated. See <see cref="NonceLifetime"/> for why the
        /// hardening is not the default.
        /// </remarks>
        public static readonly TimeSpan DEFAULT_NONCE_LIFETIME = TimeSpan.Zero;

        /// <summary>
        /// Default value of <see cref="MaxConnections"/>.
        /// </summary>
        public const int DEFAULT_MAX_CONNECTIONS = 100;

        /// <summary>
        /// Default value of <see cref="RtpPortRangeStart"/>.
        /// </summary>
        /// <summary>
        /// Default value of <see cref="MulticastPortRangeStart"/>.
        /// </summary>
        public const int DEFAULT_MULTICAST_PORT_RANGE_START = 52000;

        /// <summary>
        /// Default value of <see cref="MulticastPortRangeEnd"/>.
        /// </summary>
        public const int DEFAULT_MULTICAST_PORT_RANGE_END = 52500;

        public const int DEFAULT_RTP_PORT_RANGE_START = 50000;

        /// <summary>
        /// Default value of <see cref="RtpPortRangeEnd"/>.
        /// </summary>
        /// <remarks>
        /// 500 pairs. The range is deliberately below the one SharpRTSPClient allocates from, so a
        /// client and a server sharing a machine do not compete for the same ports.
        /// </remarks>
        public const int DEFAULT_RTP_PORT_RANGE_END = 51000;

        /// <summary>
        /// Session name.
        /// </summary>
        public string SessionName { get; set; } = "SharpRTSP";

        private readonly List<RTSPConnection> _connectionList = new List<RTSPConnection>(); // list of RTSP Listeners

        /// <summary>
        /// The threads that write media to clients, shared by all of them. Made when the server
        /// starts listening, so that the settings governing it can be changed until then.
        /// </summary>
        private RtpWriterPool _writers;
        private readonly IRtspListenSocket _serverListener;
        private readonly TcpListener _tcpListener;
        private readonly RemoteCertificateValidationCallback _userCertificateValidationCallback;
        private readonly bool _handshakeOffTheAcceptLoop;
        private readonly SemaphoreSlim _pendingHandshakes = new SemaphoreSlim(MAX_PENDING_HANDSHAKES, MAX_PENDING_HANDSHAKES);
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _logger;

        // Where the next RTP/RTCP pair is looked for, see AllocateUdpPair. Read and advanced from the
        // RTSP receive thread of every connection, so it is guarded.
        private readonly object _rtpPortCursorLock = new object();
        private int _rtpPortCursor = -1;

        private CancellationTokenSource _stopping;
        private bool _disposed;
        private Task _listenThread;
        private Timer _reaperTimer;
        private readonly NetworkCredential _credentials;

        // Newest first. The first is what clients are challenged with; the rest are nonces that have
        // just been rotated out and are still accepted, so that rotating does not fail a request that
        // was already on its way. Replaced wholesale, and read from the RTSP receive threads, which
        // take one copy per message - so a change never leaves a request half checked.
        private volatile Authentication[] _authentications = new Authentication[0];
        private RtspAuthenticationScheme _authenticationScheme = RtspAuthenticationScheme.Digest;
        private Timer _nonceTimer;

        /// <summary>
        /// Event raised when an RTSP message is received. Point of extensibility.
        /// </summary>
        public event EventHandler<RtspMessageEventArgs> ReceivedRtspMessage;

        public string SrtpCryptoSuite { get; set; } = null;

        /// <summary>
        /// Largest number of simultaneous client connections the server will hold. Connections beyond
        /// this are closed straight away, so that a client opening sockets and walking away cannot use
        /// up all of the server's memory and UDP ports. Set to zero for no limit.
        /// </summary>
        public int MaxConnections { get; set; } = DEFAULT_MAX_CONNECTIONS;

        /// <summary>
        /// How many client connections the server is holding, counted against
        /// <see cref="MaxConnections"/>.
        /// </summary>
        /// <remarks>
        /// Includes connections that have been accepted but have not sent a request yet, which is
        /// what the limit counts too - so this is the number the limit is actually applied to,
        /// rather than the number that have got as far as asking for a stream.
        /// </remarks>
        public int ConnectionCount
        {
            get { lock (_connectionList) { return _connectionList.Count; } }
        }

        /// <summary>
        /// First port of the range a UDP SETUP allocates its RTP/RTCP pair from.
        /// Change it with <see cref="SetRtpPortRange"/>.
        /// </summary>
        public int RtpPortRangeStart { get; private set; } = DEFAULT_RTP_PORT_RANGE_START;

        /// <summary>
        /// One past the last port of the range a UDP SETUP allocates its RTP/RTCP pair from.
        /// Change it with <see cref="SetRtpPortRange"/>.
        /// </summary>
        public int RtpPortRangeEnd { get; private set; } = DEFAULT_RTP_PORT_RANGE_END;

        /// <summary>
        /// Sets the range of local ports a UDP SETUP allocates its RTP/RTCP pair from. Each SETUP
        /// takes one consecutive pair, so the range holds (<paramref name="lastPort"/> -
        /// <paramref name="firstPort"/>) / 2 simultaneous UDP sessions. Once they are all taken,
        /// SETUP answers 461 and the client falls back to a TCP transport.
        /// </summary>
        /// <param name="firstPort">First port of the range, inclusive.</param>
        /// <param name="lastPort">Last port of the range, exclusive.</param>
        /// <remarks>
        /// Set this before <see cref="StartListen"/>. Open the range on any firewall or NAT in front
        /// of the server, and keep it clear of the range an RTSP client on the same machine uses -
        /// they would otherwise take ports from each other.
        /// </remarks>
        public void SetRtpPortRange(int firstPort, int lastPort)
        {
            ValidateRtpPortRange(firstPort, lastPort);

            RtpPortRangeStart = firstPort;
            RtpPortRangeEnd = lastPort;

            lock (_rtpPortCursorLock)
            {
                _rtpPortCursor = -1; // the old cursor means nothing in the new range
            }
        }

        internal static void ValidateRtpPortRange(int firstPort, int lastPort)
        {
            if (firstPort < IPEndPoint.MinPort || firstPort > IPEndPoint.MaxPort)
                throw new ArgumentOutOfRangeException(nameof(firstPort), firstPort, "The first port is not a port number.");

            if (lastPort < IPEndPoint.MinPort || lastPort > IPEndPoint.MaxPort)
                throw new ArgumentOutOfRangeException(nameof(lastPort), lastPort, "The last port is not a port number.");

            // an RTP/RTCP pair is two consecutive ports, so a range narrower than that holds nothing
            if (lastPort - firstPort < 2)
                throw new ArgumentOutOfRangeException(nameof(lastPort), lastPort, "The port range has to hold at least one RTP/RTCP pair.");
        }

        /// <summary>
        /// How clients are challenged to authenticate. <see cref="RtspAuthenticationScheme.Digest"/>
        /// by default.
        /// </summary>
        /// <remarks>
        /// Switching to <see cref="RtspAuthenticationScheme.Basic"/> makes clients send the password
        /// in a reversible form, so only do it for clients that cannot do Digest, and preferably only
        /// with a TLS certificate configured. Set this before calling <see cref="StartListen"/>.
        /// </remarks>
        public RtspAuthenticationScheme AuthenticationScheme
        {
            get { return _authenticationScheme; }
            set
            {
                if (_authenticationScheme == value)
                    return;

                _authenticationScheme = value;
                ResetAuthentication();
            }
        }

        /// <summary>
        /// How long a Digest nonce is offered to clients before a fresh one replaces it.
        /// <see cref="DEFAULT_NONCE_LIFETIME"/> by default.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The nonce used to be made once and kept for the life of the server, which left a captured
        /// Authorization header replayable for exactly as long as the server ran. Rotating bounds
        /// that to roughly twice this, since the nonce just rotated out is still accepted so that a
        /// request already in flight is not failed.
        /// </para>
        /// <para>
        /// Off by default, and deliberately so. A client that answers under a rotated out nonce is
        /// told the nonce is stale and challenged again, which is what RFC 2617 asks for and costs a
        /// round trip and no user interaction - but only for a client that acts on it. One that
        /// treats any 401 as a wrong password gives up instead, and a long running session that
        /// would otherwise have run for days ends. Whether the clients on the other end do the right
        /// thing is not something the server can know, so turning this on is the deployment's call.
        /// </para>
        /// <para>
        /// Set it to <see cref="TimeSpan.Zero"/> for no rotation. Nothing rotates while the server is
        /// not listening, and Basic has no nonce to rotate.
        /// </para>
        /// </remarks>
        public TimeSpan NonceLifetime { get; set; } = DEFAULT_NONCE_LIFETIME;

        /// <summary>
        /// How long a newly accepted connection has to finish its handshake before it is dropped.
        /// <see cref="DEFAULT_HANDSHAKE_TIMEOUT"/> by default.
        /// </summary>
        /// <remarks>
        /// A connection that has been accepted but has not said anything yet costs a socket and, for
        /// TLS, a slot among the handshakes in flight. Without a limit one that never speaks holds
        /// what it took for as long as it likes. Does not apply to the HTTP tunnel, whose handshake
        /// the library reads inside the accept.
        /// </remarks>
        public TimeSpan HandshakeTimeout { get; set; } = DEFAULT_HANDSHAKE_TIMEOUT;

        /// <summary>
        /// How many frames may be waiting to go out on one connection before the oldest are dropped.
        /// <see cref="DEFAULT_MAX_QUEUED_FRAMES"/> by default.
        /// </summary>
        /// <remarks>
        /// A client that stops reading cannot be allowed to grow a queue without bound, and it is
        /// live media, so a client that has fallen behind is better served by what is happening now
        /// than by working through what it missed. Frames are dropped whole, so a client never
        /// receives half of one. Raise it for a client that is expected to stall briefly and catch
        /// up; lower it to keep the delay to a struggling client shorter.
        /// </remarks>
        public int MaxQueuedFramesPerConnection { get; set; } = DEFAULT_MAX_QUEUED_FRAMES;

        /// <summary>
        /// How many bytes of media may be waiting to go out on one connection before the oldest are
        /// dropped. <see cref="DEFAULT_MAX_QUEUED_BYTES"/> by default.
        /// </summary>
        /// <remarks>
        /// Alongside <see cref="MaxQueuedFramesPerConnection"/>, because the two bound different
        /// things: a frame count bounds how far behind a client may fall, a byte count bounds what
        /// that costs. Sixty four frames of audio and sixty four of high bitrate video are not
        /// remotely the same amount of memory.
        /// </remarks>
        public long MaxQueuedBytesPerConnection { get; set; } = DEFAULT_MAX_QUEUED_BYTES;

        /// <summary>
        /// The most threads the server will use to write media, across all of its clients.
        /// </summary>
        /// <remarks>
        /// Not a thread per client: a write to a client that is reading takes microseconds, so a few
        /// threads carry a great many of them. The number matters only for clients that have stopped
        /// reading, since a write to one of those holds its thread until the socket is closed - so
        /// this is really how many stalled clients the server tolerates before the ones behind them
        /// start to wait. Set it before <see cref="StartListen"/>; it is read once.
        /// </remarks>
        public int MaxWriterThreads { get; set; } = RtpWriterPool.DEFAULT_MAX_THREADS;

        /// <summary>
        /// Whether a client may ask to be sent the media over multicast.
        /// </summary>
        /// <remarks>
        /// On, because a server that refuses sends every client its own copy of the same frames. Turn
        /// it off on a network where sending to a group is unwelcome; clients then fall back to a
        /// transport of their own, as they did when it was not implemented.
        /// </remarks>
        public bool MulticastEnabled { get; set; } = true;

        /// <summary>
        /// The group the media is sent to.
        /// </summary>
        /// <remarks>
        /// From the administratively scoped range, which is the part of the multicast space set aside
        /// for use within one organisation and never routed onto the internet. Streams share the
        /// group and are told apart by their ports, which is how a receiver filters them anyway.
        /// </remarks>
        public string MulticastAddress { get; set; } = "239.1.1.1";

        /// <summary>
        /// How far multicast media is allowed to travel, in routed hops.
        /// </summary>
        /// <remarks>
        /// One by default, which keeps it on the local link. Anything more needs the routing between
        /// here and there to have been arranged for it, so it is a deliberate choice rather than
        /// something to be helpful about.
        /// </remarks>
        public int MulticastTimeToLive { get; set; } = 1;

        /// <summary>
        /// First port of the range multicast groups take their ports from.
        /// </summary>
        /// <remarks>
        /// Its own range, separate from the one unicast sessions use. A group's ports are not a
        /// client's: they are named in the reply to every client that joins, and stay until the last
        /// of them has gone.
        /// </remarks>
        public int MulticastPortRangeStart { get; private set; } = DEFAULT_MULTICAST_PORT_RANGE_START;

        /// <summary>
        /// Last port of the range multicast groups take their ports from.
        /// </summary>
        public int MulticastPortRangeEnd { get; private set; } = DEFAULT_MULTICAST_PORT_RANGE_END;

        /// <summary>
        /// Sets the range of ports multicast groups take their ports from.
        /// </summary>
        public void SetMulticastPortRange(int firstPort, int lastPort)
        {
            ValidateRtpPortRange(firstPort, lastPort);

            MulticastPortRangeStart = firstPort;
            MulticastPortRangeEnd = lastPort;
        }

        /// <summary>
        /// How many threads the server is currently using to write media, across all of its clients.
        /// </summary>
        /// <remarks>
        /// Worth watching. It follows the number of clients that are being written to at this
        /// instant, so in normal service it settles at a handful whatever the audience; if it is
        /// near <see cref="MaxWriterThreads"/> then that many clients are sitting in writes that
        /// have not come back, which means they have stopped reading.
        /// </remarks>
        public int WriterThreadCount => _writers?.ThreadCount ?? 0;

        /// <summary>
        /// How often a sender report goes out on a playing stream.
        /// <see cref="DEFAULT_RTCP_INTERVAL"/> by default.
        /// </summary>
        /// <remarks>
        /// A sender report carries the mapping between wall clock and RTP timestamps, which is what
        /// lets a receiver line up audio against video. One is sent as soon as a stream starts
        /// playing, and then at this interval. It used to be one before every single packet, which is
        /// not what RFC 3550 has in mind - RTCP is meant to be a few per cent of what the session
        /// sends, not half of it.
        /// </remarks>
        public TimeSpan RtcpSenderReportInterval { get; set; } = DEFAULT_RTCP_INTERVAL;

        /// <summary>
        /// The streams this server offers. Guarded by the connection list lock.
        /// </summary>
        private readonly List<RTSPStreamSource> StreamSources = new List<RTSPStreamSource>();

        /// <summary>
        /// TLS certificate used for RTSPS and HTTPS.
        /// </summary>
        public X509Certificate2 TlsCertificate { get; private set; } = null;

        /// <summary>
        /// Use RTSP/RTSPS over HTTP/HTTPS.
        /// </summary>
        public bool UseHttpTunnel { get; private set; } = false;

        /// <summary>
        /// Initializes a new instance of the <see cref="RTSPServer"/> class.
        /// </summary>
        /// <param name="portNumber">Port number.</param>
        /// <param name="userName">User name.</param>
        /// <param name="password">Password.</param>
        public RTSPServer(
            int portNumber,
            string userName,
            string password)
            : this(portNumber, userName, password, null)
        { }

        /// <summary>
        /// Initializes a new instance of the <see cref="RTSPServer"/> class.
        /// </summary>
        /// <param name="portNumber">Port number.</param>
        /// <param name="userName">User name.</param>
        /// <param name="password">Password.</param>
        /// <param name="loggerFactory">Logger factory.</param>
        public RTSPServer(
            int portNumber,
            string userName,
            string password,
            ILoggerFactory loggerFactory)
            : this(portNumber, userName, password, false, null, loggerFactory)
        { }

        /// <summary>
        /// Initializes a new instance of the <see cref="RTSPServer"/> class.
        /// </summary>
        /// <param name="portNumber">Port number.</param>
        /// <param name="userName">User name.</param>
        /// <param name="password">Password.</param>
        /// <param name="useHttpTunnel">RTSP over HTTP.</param>
        /// <param name="tlsCertificate">TLS certificate used for RTSPS and HTTPS.</param>
        /// <param name="loggerFactory">Logger factory.</param>
        /// <param name="userCertificateValidationCallback">Certificate validation callback.</param>
        public RTSPServer(
            int portNumber,
            string userName,
            string password,
            bool useHttpTunnel,
            X509Certificate2 tlsCertificate,
            ILoggerFactory loggerFactory,
            RemoteCertificateValidationCallback userCertificateValidationCallback = null)
            : this(portNumber, userName, password, useHttpTunnel, tlsCertificate, null, loggerFactory, userCertificateValidationCallback)
        { }

        /// <summary>
        /// Initializes a new instance of the <see cref="RTSPServer"/> class.
        /// </summary>
        /// <param name="portNumber">Port number.</param>
        /// <param name="userName">User name.</param>
        /// <param name="password">Password.</param>
        /// <param name="useHttpTunnel">RTSP over HTTP.</param>
        /// <param name="tlsCertificate">TLS certificate used for RTSPS and HTTPS.</param>
        /// <param name="srtpCryptoSuite">SRTP crypto suite <see cref="SrtpCryptoSuites"/>.</param>
        /// <param name="loggerFactory">Logger factory.</param>
        /// <param name="userCertificateValidationCallback">Certificate validation callback.</param>
        public RTSPServer(
            int portNumber,
            string userName,
            string password,
            bool useHttpTunnel,
            X509Certificate2 tlsCertificate,
            string srtpCryptoSuite,
            ILoggerFactory loggerFactory,
            RemoteCertificateValidationCallback userCertificateValidationCallback = null)
        {
            if (portNumber < IPEndPoint.MinPort || portNumber > IPEndPoint.MaxPort)
            {
                throw new ArgumentOutOfRangeException(nameof(portNumber), portNumber, "Port number must be between System.Net.IPEndPoint.MinPort and System.Net.IPEndPoint.MaxPort");
            }

            Contract.EndContractBlock();

            if (loggerFactory == null)
                loggerFactory = new CustomLoggerFactory();

            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<RTSPServer>();

            this.UseHttpTunnel = useHttpTunnel;
            this.TlsCertificate = tlsCertificate;
            this.SrtpCryptoSuite = srtpCryptoSuite;

            _credentials = !string.IsNullOrEmpty(userName) && !string.IsNullOrEmpty(password)
                ? new NetworkCredential(userName, password)
                : new NetworkCredential();

            ResetAuthentication();

            RegisterRtspUriScheme();

            var tcpListener = CreateListener(portNumber);
            _tcpListener = tcpListener;
            _userCertificateValidationCallback = userCertificateValidationCallback;
            _serverListener = useHttpTunnel switch
            {
                true when tlsCertificate is null => new RtspOverHttpListenSocket(tcpListener, loggerFactory),
                true => new RtspOverHttpTLSListenSocket(tcpListener, tlsCertificate, userCertificateValidationCallback, loggerFactory),
                false when tlsCertificate is null => new RtspListenSocket(tcpListener, loggerFactory: loggerFactory),
                false => new RtspTlsListenSocket(tcpListener, tlsCertificate, userCertificateValidationCallback, loggerFactory),
            };

            // The listen socket accepts and completes the TLS handshake in the same call, so one
            // client that connects and then says nothing holds the accept loop and no other client
            // gets in. Where we can build the transport ourselves - a TLS server that is not
            // tunnelled, the tunnel handshake being the library's to read - the accept is taken here
            // instead and the handshake done away from the loop, under a timeout.
            //
            // Only where there is a handshake to wait on. A plaintext server has none, so it keeps
            // the straightforward path, where a connection is counted against MaxConnections the
            // moment it is accepted rather than once it has negotiated.
            _handshakeOffTheAcceptLoop = !useHttpTunnel && tlsCertificate != null;

        }

        /// <summary>
        /// Builds the challenge for the given scheme, or null when no credentials were configured
        /// and the server is therefore open.
        /// </summary>
        private Authentication CreateAuthentication(RtspAuthenticationScheme scheme)
        {
            if (string.IsNullOrEmpty(_credentials?.UserName) || string.IsNullOrEmpty(_credentials.Password))
            {
                return null;
            }

            if (scheme == RtspAuthenticationScheme.Basic)
            {
                if (TlsCertificate == null)
                {
                    _logger.LogWarning(
                        "Basic authentication is enabled without a TLS certificate. The user name and password " +
                        "will be sent in a reversible form and can be read off the network. Use Digest, or " +
                        "configure a TLS certificate so the connection is encrypted.");
                }

                return new AuthenticationBasic(_credentials, AUTHENTICATION_REALM);
            }

            return new AuthenticationDigest(_credentials, AUTHENTICATION_REALM, RandomGenerator.NextHexToken(NONCE_BYTES), string.Empty);
        }

        /// <summary>
        /// Whether a request answered its challenge correctly, but under a nonce this server has
        /// since rotated away.
        /// </summary>
        /// <remarks>
        /// Checked by rebuilding the challenge as the client saw it and asking whether the answer
        /// fits. Only someone who knows the password can produce an answer that does, so this does
        /// not let a replayed header in - it is refused either way. It only decides whether the
        /// client is told to try again, or told it got the password wrong.
        /// </remarks>
        private bool HasStaleNonce(RtspRequest message)
        {
            if (_authenticationScheme != RtspAuthenticationScheme.Digest)
            {
                return false;
            }

            if (!message.Headers.TryGetValue("Authorization", out string authorization) || string.IsNullOrEmpty(authorization))
            {
                return false;
            }

            var nonce = Regex.Match(authorization, "nonce=\"([^\"]+)\"");
            if (!nonce.Success)
            {
                return false;
            }

            try
            {
                var asTheClientSawIt = new AuthenticationDigest(_credentials, AUTHENTICATION_REALM, nonce.Groups[1].Value, string.Empty);
                return asTheClientSawIt.IsValid(message);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not check whether the nonce was merely stale");
                return false;
            }
        }

        /// <summary>
        /// Starts again from a single fresh challenge, forgetting any nonce handed out so far.
        /// </summary>
        private void ResetAuthentication()
        {
            Authentication authentication = CreateAuthentication(_authenticationScheme);
            _authentications = authentication == null ? new Authentication[0] : new[] { authentication };
        }

        /// <summary>
        /// Puts a fresh nonce in front, keeping the one it replaces acceptable for a while.
        /// </summary>
        private void RotateNonce()
        {
            try
            {
                // Basic carries no nonce, so there would be nothing to rotate and every rotation
                // would just be a challenge the client has to answer again for no gain.
                if (_authenticationScheme != RtspAuthenticationScheme.Digest)
                {
                    return;
                }

                Authentication fresh = CreateAuthentication(RtspAuthenticationScheme.Digest);
                if (fresh == null)
                {
                    // no credentials configured, so the server is open and there is nothing to rotate
                    return;
                }

                Authentication[] previous = _authentications;
                var rotated = new List<Authentication>(NONCE_GRACE_COUNT + 1) { fresh };

                for (int i = 0; i < previous.Length && rotated.Count <= NONCE_GRACE_COUNT; i++)
                {
                    rotated.Add(previous[i]);
                }

                _authentications = rotated.ToArray();
                _logger.LogDebug("Rotated the authentication nonce, {count} still accepted", rotated.Count);
            }
            catch (Exception ex)
            {
                // this runs on a pooled thread, an escaping exception would take the process down
                _logger.LogError(ex, "Error rotating the authentication nonce");
            }
        }

        /// <summary>
        /// Registers the rtsp/rtsps URI schemes. Registration is process wide and can only happen once,
        /// so a second server - or a race between two of them - must not be allowed to fail here.
        /// </summary>
        private static void RegisterRtspUriScheme()
        {
            try
            {
                RtspUtils.RegisterUri();
            }
            catch (InvalidOperationException)
            {
                // already registered by another instance, which is exactly what we wanted
            }
        }

        /// <summary>
        /// Starts the server listener.
        /// </summary>
        public void StartListen()
        {
            ThrowIfDisposed();

            // Starting twice used to overwrite the cancellation source, the accept task and the timer
            // without stopping any of them, leaving two accept loops running and no way to reach the
            // first one again.
            if (_listenThread != null)
            {
                throw new InvalidOperationException("The server is already listening. Call StopListen before starting it again.");
            }

            _serverListener.Start();
            _stopping = new CancellationTokenSource();
            _writers = _writers ?? new RtpWriterPool(_logger, MaxWriterThreads);

            // Unwrap, so the task handed back is the accept loop itself rather than the one that
            // starts it - the outer task completes at the first await, and StopListen waiting on it
            // would return while the loop was still running.
            _listenThread = Task.Factory.StartNew(() => AcceptConnection(_stopping.Token),
                _stopping.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();

            // Idle connections are also swept from the media path, but a server with no media flowing
            // would never get there and would hold on to their sockets and UDP ports indefinitely.
            _reaperTimer = new Timer(_ => ReapIdleConnectionsSafely(), null, REAP_INTERVAL, REAP_INTERVAL);

            if (NonceLifetime > TimeSpan.Zero)
            {
                _nonceTimer = new Timer(_ => RotateNonce(), null, NonceLifetime, NonceLifetime);
            }
        }

        private void ReapIdleConnectionsSafely()
        {
            try
            {
                lock (_connectionList)
                {
                    ReapIdleConnections();
                }
            }
            catch (Exception ex)
            {
                // this runs on a pooled thread, an escaping exception would take the process down
                _logger.LogError(ex, "Error while sweeping idle connections");
            }
        }

        private async Task AcceptConnection(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                IRtspTransport rtspSocket;

                try
                {
                    if (_handshakeOffTheAcceptLoop)
                    {
                        // Accept only. Building the transport - which is where TLS negotiates - and
                        // admitting the connection happen on their own task, so a client that never
                        // speaks delays nobody but itself.
                        await _pendingHandshakes.WaitAsync(cancellationToken).ConfigureAwait(false);

                        TcpClient tcpClient;
                        try
                        {
                            tcpClient = await AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch
                        {
                            _pendingHandshakes.Release();
                            throw;
                        }

                        _ = Task.Run(() => HandshakeAndAdmitAsync(tcpClient), CancellationToken.None);
                        continue;
                    }

                    // Wait for an incoming TCP Connection
                    rtspSocket = await _serverListener.AcceptAsync(cancellationToken);
                }
                catch (Exception ex) when (cancellationToken.IsCancellationRequested)
                {
                    // StopListen stops the listener and cancels the token, and whichever of the two
                    // the accept notices first surfaces as an exception. Nothing has gone wrong.
                    _logger.LogDebug(ex, "The accept loop is stopping");
                    return;
                }
                catch (Exception ex) when (ex is ObjectDisposedException || ex is InvalidOperationException)
                {
                    // The listener itself is gone, so there is nothing left to accept on and retrying
                    // would spin. Anything else is the fault of the one connection being accepted.
                    _logger.LogWarning(ex, "The listener is no longer usable, the server has stopped accepting connections");
                    return;
                }
                catch (Exception ex)
                {
                    // One connection that cannot be accepted must not take the server with it. A
                    // client that opens the RTSPS port and sends something that is not a TLS
                    // ClientHello fails the handshake inside the accept, and that used to end this
                    // loop for good: the server stopped taking new connections while everything
                    // already connected carried on, so nothing looked wrong from the outside.
                    _logger.LogWarning(ex, "Could not accept a connection");

                    // A failure that repeats - running out of file handles, say - would otherwise
                    // spin this loop as fast as the machine allows.
                    await Task.Delay(ACCEPT_RETRY_DELAY, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    AdmitConnection(rtspSocket);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not admit the connection from {remoteEndPoint}", TryGetRemoteEndPoint(rtspSocket));
                    TryCloseTransport(rtspSocket);
                }
            }
        }

        private async Task<TcpClient> AcceptTcpClientAsync(CancellationToken cancellationToken)
        {
#if NET6_0_OR_GREATER
            return await _tcpListener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
#else
            using (cancellationToken.Register(() => _tcpListener.Stop()))
            {
                return await _tcpListener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
#endif
        }

        /// <summary>
        /// Finishes a connection off the accept loop: negotiates whatever the transport needs, then
        /// admits it. Anything that takes longer than <see cref="HandshakeTimeout"/> is dropped.
        /// </summary>
        private async Task HandshakeAndAdmitAsync(TcpClient tcpClient)
        {
            try
            {
                // Building the transport is where TLS negotiates, and it blocks until the client
                // completes the handshake or never does. So that is the part with a deadline on it,
                // on a dedicated thread rather than a pooled one - the pool adds threads only
                // slowly, and a handful of connections sitting in a handshake was enough to make an
                // honest client wait seconds for a thread to run its own on.
                Task<IRtspTransport> handshake = Task.Factory.StartNew(
                    () => BuildTransport(tcpClient),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);

                Task finished = await Task.WhenAny(handshake, Task.Delay(HandshakeTimeout)).ConfigureAwait(false);

                if (!ReferenceEquals(finished, handshake))
                {
                    // Closing the socket is what unblocks it, since it is sitting on a read that will
                    // never be answered.
                    _logger.LogWarning("Dropping the connection from {remoteEndPoint}, it did not finish its handshake in time",
                        TryGetRemoteEndPoint(tcpClient));
                    TryClose(tcpClient);

                    // let it unwind on its own rather than holding this task, and its slot, on it
                    _ = handshake.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
                    return;
                }

                // Registering is the server's own bookkeeping, and it takes the connection list lock
                // that the media path holds too. Timing it would drop a blameless client whenever the
                // server itself was busy, so it happens after the clock has stopped.
                AdmitConnection(await handshake.ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                // One connection that cannot be brought up must not take the server with it, which is
                // the whole point of doing this away from the accept loop.
                _logger.LogWarning(ex, "Could not bring up the connection from {remoteEndPoint}", TryGetRemoteEndPoint(tcpClient));
                TryClose(tcpClient);
            }
            finally
            {
                _pendingHandshakes.Release();
            }
        }

        /// <summary>
        /// Wraps an accepted socket in the transport this server speaks, negotiating TLS if it has a
        /// certificate. This is what the listen socket would otherwise do inside the accept.
        /// </summary>
        private IRtspTransport BuildTransport(TcpClient tcpClient)
        {
            if (TlsCertificate == null)
            {
                return new RtspTcpTransport(tcpClient);
            }

            return new RtspTcpTlsTransport(tcpClient, TlsCertificate, _userCertificateValidationCallback);
        }

        private static string TryGetRemoteEndPoint(TcpClient tcpClient)
        {
            try
            {
                return tcpClient?.Client?.RemoteEndPoint?.ToString() ?? "unknown";
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        private void TryClose(TcpClient tcpClient)
        {
            try
            {
                tcpClient?.Close();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error closing a socket");
            }
        }

        /// <summary>
        /// Takes on a freshly accepted connection, unless the server is already full.
        /// </summary>
        private void AdmitConnection(IRtspTransport rtspSocket)
        {
            RtspListener listener = RegisterConnection(rtspSocket);
            listener?.Start();
        }

        /// <summary>
        /// Puts a freshly accepted connection on the books, and hands back the listener to start, or
        /// null if the server is already full and the connection has been let go.
        /// </summary>
        private RtspListener RegisterConnection(IRtspTransport rtspSocket)
        {
            _logger.LogDebug("Connection from {remoteEndPoint}", rtspSocket.RemoteEndPoint);

            RtspListener newListener = new RtspListener(rtspSocket, _loggerFactory.CreateLogger<RtspListener>());
            newListener.MessageReceived += RTSPMessageReceived;

            // Built before the lock is taken. The accept loop is single threaded, so every moment
            // spent holding this lock is a moment the next connection waits to be counted - and the
            // limit is only as accurate as that counting is prompt.
            var candidate = new RTSPConnection()
            {
                Listener = newListener,
                Transport = rtspSocket
            };

            // Made with the connection rather than on its first frame, so that a producer only ever
            // has to read it - making one would need a lock, and the producer is the one thread that
            // must not wait for anything here.
            candidate.Outbound = new OutboundQueue(
                _writers,
                MaxQueuedFramesPerConnection,
                MaxQueuedBytesPerConnection,
                frame => WriteQueuedFrame(candidate, frame),
                TryGetRemoteEndPoint(rtspSocket),
                _logger);

            // Add the RtspListener to the RTSPConnections List
            bool accepted;
            lock (_connectionList)
            {
                // sweep first, so that connections that have already gone away do not count
                // towards the limit and keep a legitimate client out
                ReapIdleConnections();

                accepted = MaxConnections <= 0 || _connectionList.Count < MaxConnections;
                if (accepted)
                {
                    _connectionList.Add(candidate);
                }
            }

            if (!accepted)
            {
                candidate.Outbound.Dispose();
            }

            if (!accepted)
            {
                _logger.LogWarning("Refusing connection from {remoteEndPoint}, the limit of {maxConnections} connections is reached",
                    rtspSocket.RemoteEndPoint, MaxConnections);
                newListener.MessageReceived -= RTSPMessageReceived;
                newListener.Dispose();
                return null;
            }

            return newListener;
        }

        /// <summary>
        /// Takes a connection back off the books, for one that was registered and then turned out not
        /// to be worth keeping.
        /// </summary>
        private void DropConnection(RtspListener listener)
        {
            lock (_connectionList)
            {
                foreach (RTSPConnection connection in _connectionList.Where(c => c.Listener == listener).ToArray())
                {
                    RemoveSession(connection);
                }
            }
        }

        /// <summary>
        /// The remote end point of a transport that may already be broken, for a log line.
        /// </summary>
        private static string TryGetRemoteEndPoint(IRtspTransport transport)
        {
            try
            {
                return transport?.RemoteEndPoint?.ToString() ?? "unknown";
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        private void TryCloseTransport(IRtspTransport transport)
        {
            try
            {
                transport?.Close();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error closing a transport");
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(RTSPServer));
            }
        }

        /// <summary>
        /// Listens on every address of the machine, over IPv6 as well as IPv4 where it can.
        /// </summary>
        /// <remarks>
        /// A dual mode socket takes both, and hands an IPv4 client back as an address mapped into
        /// IPv6 - which is why anywhere that reads a client address puts it back through
        /// <see cref="MediaDestination"/> before using it. A machine with IPv6 turned off falls back
        /// to IPv4 rather than failing to start.
        /// </remarks>
        private static TcpListener CreateListener(int portNumber)
        {
            if (Socket.OSSupportsIPv6)
            {
                try
                {
                    var dualStack = new TcpListener(IPAddress.IPv6Any, portNumber);
                    dualStack.Server.DualMode = true;
                    return dualStack;
                }
                catch (Exception)
                {
                    // the machine will not have it, so ask for what it will
                }
            }

            return new TcpListener(IPAddress.Any, portNumber);
        }

        /// <summary>
        /// The address to send this client's media to, as a socket API will accept it.
        /// </summary>
        /// <remarks>
        /// Two things have to come off. An IPv4 client arriving on a dual mode socket is reported as
        /// an IPv4 address mapped into IPv6, and sending to it in that form asks for an IPv6 socket
        /// where an IPv4 one is wanted. A link local IPv6 address carries a scope, which means
        /// something only on the machine that wrote it down.
        /// <para>
        /// This used to be the address with everything after the first colon cut off, which does
        /// nothing at all to an IPv4 address - there is no port on it to remove - and reduces an IPv6
        /// address to nothing, so the media went nowhere.
        /// </para>
        /// </remarks>
        internal static string MediaDestination(IPAddress address)
        {
            if (address == null)
            {
                return null;
            }

            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }
            else if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
            {
                address = new IPAddress(address.GetAddressBytes());
            }

            return address.ToString();
        }

        /// <summary>
        /// The address family a client's media has to be sent in.
        /// </summary>
        /// <remarks>
        /// An IPv4 client arriving on a dual mode listener is reported as an IPv4 address mapped into
        /// IPv6, and is served from an IPv4 socket like any other - the mapping is how the listener
        /// describes it, not how the client is reached.
        /// </remarks>
        internal static AddressFamily MediaFamily(IPAddress address)
        {
            if (address == null || address.IsIPv4MappedToIPv6)
            {
                return AddressFamily.InterNetwork;
            }

            return address.AddressFamily;
        }

        /// <summary>
        /// Stops the server listener.
        /// </summary>
        public void StopListen()
        {
            _reaperTimer?.Dispose();
            _reaperTimer = null;

            _nonceTimer?.Dispose();
            _nonceTimer = null;

            _serverListener.Stop();

            // Cancel before waiting, and take local copies: calling this twice, or racing Dispose,
            // must not end up cancelling a source that has already been disposed.
            CancellationTokenSource stopping = _stopping;
            Task listenThread = _listenThread;
            _stopping = null;
            _listenThread = null;

            if (stopping != null)
            {
                try
                {
                    stopping.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // already stopped
                }
            }

            if (listenThread != null)
            {
                try
                {
                    listenThread.Wait();
                }
                catch (AggregateException ex)
                {
                    // the loop stops on cancellation, which arrives here as an exception rather than
                    // as a fault worth reporting
                    _logger.LogDebug(ex, "The accept loop ended with an exception");
                }
            }

            stopping?.Dispose();
        }

        private void RTSPMessageReceived(object sender, RtspChunkEventArgs e)
        {
            // Cast the 'sender' and 'e' into the RTSP Listener (the Socket) and the RTSP Message
            RtspListener listener = sender as RtspListener ?? throw new ArgumentException("Invalid sender", nameof(sender));

            // Anything that escapes here is swallowed by the listener, which then closes the socket
            // and leaves the client waiting without a reply. Answer with a 500 instead so the failure
            // is visible on both ends.
            try
            {
                HandleRtspRequest(listener, sender, e);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling an RTSP request from {remoteEndPoint}", listener.RemoteEndPoint);
                TrySendInternalServerError(listener, e.Message as RtspRequest);
            }
        }

        /// <summary>
        /// Raises <see cref="ReceivedRtspMessage"/> without letting a handler fail the request.
        /// </summary>
        /// <remarks>
        /// This is a point of extensibility, so the code on the other end of it is not ours. An
        /// exception from there used to come back as a 500 for a request the server had already
        /// handled correctly, and on some paths after the reply had gone out.
        /// </remarks>
        private void RaiseReceivedRtspMessage(object sender, RtspMessageEventArgs args)
        {
            try
            {
                ReceivedRtspMessage?.Invoke(sender, args);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A ReceivedRtspMessage handler threw");
            }
        }

        /// <summary>
        /// Best effort error reply. The connection may already be gone, which is not worth reporting.
        /// </summary>
        private void TrySendInternalServerError(RtspListener listener, RtspRequest request)
        {
            if (request == null)
            {
                return;
            }

            try
            {
                RtspResponse errorResponse = request.CreateResponse();
                errorResponse.ReturnCode = 500; // Internal Server Error
                listener.SendMessage(errorResponse);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not send the error reply");
            }
        }

        private void HandleRtspRequest(RtspListener listener, object sender, RtspChunkEventArgs e)
        {

            if (!(e.Message is RtspRequest message))
            {
                _logger.LogWarning("RTSP message is not a request. Invalid dialog.");
                return;
            }

            // Log the request line only. Dumping the whole message would put the client's
            // Authorization header - and with Basic auth its credentials - into the log.
            _logger.LogDebug("RTSP {method} {uri} received from {remoteEndPoint}",
                message.RequestTyped, message.RtspUri, listener.RemoteEndPoint);

            // Check if the RTSP Message has valid authentication (validating against username,password,realm and nonce).
            // One snapshot for the whole check, so a scheme change cannot validate against one scheme
            // and then challenge with the other.
            Authentication[] authentications = _authentications;
            if (authentications.Length > 0)
            {
                // Challenge with the newest, but accept any that is still within its grace period.
                Authentication authentication = authentications[0];

                if (message.Headers.ContainsKey("Authorization"))
                {
                    // The Header contained Authorization
                    // Check the message has the correct Authorization
                    // If it does not have the correct Authorization then close the RTSP connection
                    if (!authentications.Any(candidate => candidate.IsValid(message)))
                    {
                        // Answering correctly under a nonce we no longer hold is not a failed login,
                        // it is a session that has outlived the nonce it started with. RFC 2617 calls
                        // that stale: the client redoes the digest against the new nonce and carries
                        // on, and only a client that cannot do that is any worse off than before.
                        bool staleNonce = HasStaleNonce(message);

                        RtspResponse authorizationResponse = message.CreateResponse();
                        authorizationResponse.AddHeader("WWW-Authenticate: " + authentication.GetServerResponse()
                            + (staleNonce ? ", stale=\"true\"" : string.Empty));
                        authorizationResponse.ReturnCode = 401;
                        listener.SendMessage(authorizationResponse);

                        if (staleNonce)
                        {
                            // The connection stays: tearing it down would end a session the client is
                            // entitled to continue, and it is what the client sees as the server
                            // hanging up on it mid stream.
                            _logger.LogDebug("Challenging {remoteEndPoint} again, its nonce has expired", listener.RemoteEndPoint);
                            return;
                        }

                        // Go through RemoveSession rather than just dropping it from the list. A
                        // connection that had already started playing is also in the stream source's
                        // list and owns UDP sockets; leaving those behind kept it streaming to nobody,
                        // invisible to both the idle sweep and the connection limit.
                        lock (_connectionList)
                        {
                            foreach (var staleConnection in _connectionList.Where(c => c.Listener == listener).ToArray())
                            {
                                RemoveSession(staleConnection);
                            }
                        }

                        listener.Dispose();
                        return;
                    }
                }
                else
                {
                    // Send a 401 Authentication Failed, with the challenge in WWW-Authenticate
                    //  so the client knows which scheme and realm to authenticate against
                    RtspResponse authorizationResponse = message.CreateResponse();
                    authorizationResponse.AddHeader("WWW-Authenticate: " + authentication.GetServerResponse());
                    authorizationResponse.ReturnCode = 401;
                    listener.SendMessage(authorizationResponse);
                    return;
                }
            }

            // Update the RTSP Keepalive Timeout. This belongs to the connection rather than to any one
            // stream, and has to happen before the URI is looked at: a client that uses OPTIONS on the
            // base URL as its keepalive would otherwise never refresh its session.
            // Match on the listener itself - matching on the remote address alone would pick the wrong
            // connection whenever two clients share a source address (localhost, NAT, ...).
            lock (_connectionList)
            {
                _connectionList.Find(c => c.Listener == listener)?.UpdateKeepAlive();
            }

            // OPTIONS says what the server supports. It is not about any one stream, and clients and
            // health checks send it to the base URL, which used to be answered 404.
            if (message is RtspRequestOptions)
            {
                RtspResponse optionsResponse = message.CreateResponse();

                // What this server actually answers, rather than the list the transport fills in by
                // default. That one named ANNOUNCE, SET_PARAMETER and REDIRECT, and a client that
                // took it at its word got no reply at all.
                optionsResponse.Headers[PUBLIC_HEADER] = SupportedMethods;

                listener.SendMessage(optionsResponse);
                RaiseReceivedRtspMessage(sender, new RtspMessageEventArgs(message));
                return;
            }

            // Before the URI or the session are looked at, because neither is the client's mistake:
            // a method this server does not implement should be told so, not given a complaint about
            // a session it was never going to have.
            if (!IsImplemented(message))
            {
                _logger.LogDebug("Refusing {method} from {remoteEndPoint}, this server does not implement it",
                    message.RequestTyped, listener.RemoteEndPoint);

                RtspResponse notImplemented = message.CreateResponse();
                notImplemented.ReturnCode = 501; // Not Implemented
                listener.SendMessage(notImplemented);
                return;
            }

            var streamSource = GetStreamSource(message.RtspUri);
            if (streamSource == null)
            {
                // invalid URI
                RtspResponse notFoundResponse = message.CreateResponse();
                notFoundResponse.ReturnCode = 404;
                listener.SendMessage(notFoundResponse);
                return;
            }

            lock (_connectionList)
            {
                var oneConnection = _connectionList.Find(c => c.Listener == listener);
                if (oneConnection != null && !streamSource.ConnectionList.Contains(oneConnection))
                {
                    streamSource.ConnectionList.Add(oneConnection);
                }
            }

            // Handle message without session
            switch (message)
            {
                case RtspRequestDescribe describeMessage:
                    HandleDescribe(listener, message);
                    RaiseReceivedRtspMessage(sender, new RtspMessageEventArgs(message));
                    return;
                case RtspRequestSetup setupMessage:
                    HandleSetup(listener, setupMessage);
                    RaiseReceivedRtspMessage(sender, new RtspMessageEventArgs(message));
                    return;
            }

            // handle message needing session from here
            var connection = ConnectionBySessionId(message.Session);

            // The session must belong to the connection the request arrived on, otherwise any client could
            // control (and TEARDOWN) another client's session just by naming its session ID.
            if (connection != null && connection.Listener != listener)
            {
                _logger.LogWarning("Session {sessionId} does not belong to {remoteEndPoint}, rejecting", message.Session, listener.RemoteEndPoint);
                connection = null;
            }

            if (connection is null)
            {
                // Session ID was not found in the list of Sessions. Send a 454 error
                RtspResponse notFound = message.CreateResponse();
                notFound.ReturnCode = 454; // Session Not Found
                listener.SendMessage(notFound);
                return;
            }

            switch (message)
            {
                case RtspRequestPlay playMessage:
                    {
                        // Search for the Session in the Sessions List. Change the state to "PLAY"
                        const string range = "npt=0-"; // Playing the 'video' from 0 seconds until the end

                        // 'RTP-Info: url=rtsp://192.168.1.195:8557/h264/track1;seq=33026;rtptime=3014957579,url=rtsp://192.168.1.195:8557/h264/track2;seq=42116;rtptime=3335975101'
                        // One entry per stream that was actually set up, naming that track's control
                        // URL. Both entries used to be sent whatever the source held, so a video only
                        // stream announced an audio track that had never been set up and never would
                        // send, under the session URL rather than either track's own.
                        // The rtptime is what pairs a track's RTP clock with the start of the range
                        // above, and without it a receiver has nothing to line the tracks up by until
                        // the first sender report arrives. It is the timestamp of the last frame this
                        // track produced: the first frame this client receives is the next one, so
                        // the pairing is out by at most a frame, and it is what a live source can
                        // honestly say. A track that has produced nothing yet says nothing, rather
                        // than naming a time that means nothing.
                        // A client listening to a group has no stream of its own, so what it is told
                        // about is the group's - which is the numbering it will actually receive.
                        RTPStream[] playing = connection.Streams
                            .Select((stream, trackId) => stream.IsMulticast
                                ? streamSource.Multicast?.Sender.Streams[trackId]
                                : stream.RtpChannel != null ? stream : null)
                            .ToArray();

                        string rtpInfo = string.Join(",",
                            playing
                                .Select((stream, trackId) => new { stream, trackId })
                                .Where(x => x.stream != null)
                                .Select(x =>
                                {
                                    string entry = $"url={TrackControlUri(message.RtspUri, streamSource.GetTrackControl((TrackType)x.trackId))};seq={x.stream.SequenceNumber}";

                                    if (streamSource.TryGetLastRtpTimestamp(x.trackId, out uint rtpTimestamp))
                                    {
                                        entry += $";rtptime={rtpTimestamp}";
                                    }

                                    return entry;
                                }));

                        // Send the reply
                        RtspResponse playResponse = message.CreateResponse();
                        playResponse.AddHeader("Range: " + range);
                        if (!string.IsNullOrEmpty(rtpInfo))
                        {
                            playResponse.AddHeader("RTP-Info: " + rtpInfo);
                        }
                        // Answer and start playing under this connection's send lock, which the media
                        // path takes too. Done outside it, a sample produced between the response
                        // going out and the session being marked as playing was dropped, so a client
                        // that fed the moment its PLAY was answered lost whatever fell in that
                        // window; and marking it first without the lock would let the media overtake
                        // the response on the wire. It is the connection's lock rather than the
                        // server's, so answering a slow client does not stall everyone else.
                        lock (connection.SendLock)
                        {
                            foreach (var stream in connection.Streams)
                            {
                                if (stream.RtpChannel != null)
                                {
                                    stream.MustSendRtcpPacket = true;
                                }
                            }

                            // Marked as playing before the reply goes out, not after. What keeps the
                            // media behind the reply is this lock, which every write to this
                            // connection takes - so a producer that sees the session playing while
                            // the reply is still being written waits here and goes out after it.
                            // Setting the flag afterwards instead left a window where the client had
                            // the reply and fed a sample, and the sample was turned away for a
                            // session that was not marked as playing yet.
                            connection.Play = true;

                            listener.SendMessage(playResponse);
                        }

                        // Outside the connection's lock, because the group is not this connection and
                        // taking one lock while holding another is how the two orders meet.
                        StartMulticastIfListening(connection, streamSource);

                        RaiseReceivedRtspMessage(sender, new RtspMessageEventArgs(message, connection));
                    }
                    return;
                case RtspRequestPause pauseMessage:
                    {
                        connection.Play = false;
                        RtspResponse pauseResponse = message.CreateResponse();
                        listener.SendMessage(pauseResponse);
                        RaiseReceivedRtspMessage(sender, new RtspMessageEventArgs(message, connection));
                    }
                    return;
                case RtspRequestGetParameter getParameterMessage:
                    {
                        // Create the response to GET_PARAMETER
                        RtspResponse getParameterResponse = message.CreateResponse();
                        listener.SendMessage(getParameterResponse);
                        RaiseReceivedRtspMessage(sender, new RtspMessageEventArgs(message, connection));
                    }
                    return;
                case RtspRequestSetParameter setParameterMessage:
                    {
                        // With no body this is the keepalive that ONVIF clients send, and answering
                        // it is all that is wanted. With a body it is asking to set something, and
                        // this server has no parameters to set - so say that rather than pretend.
                        RtspResponse setParameterResponse = message.CreateResponse();

                        if (setParameterMessage.Data.Length > 0)
                        {
                            setParameterResponse.ReturnCode = 451; // Parameter Not Understood
                        }

                        listener.SendMessage(setParameterResponse);
                        RaiseReceivedRtspMessage(sender, new RtspMessageEventArgs(message, connection));
                    }
                    return;
                case RtspRequestTeardown teardownMessage:
                    {
                        // Acknowledge before dropping the connection. RFC 2326 requires a response to
                        // TEARDOWN, and RemoveSession disposes this listener as part of the cleanup.
                        listener.SendMessage(message.CreateResponse());

                        lock (_connectionList)
                        {
                            RemoveSession(connection);
                        }
                        RaiseReceivedRtspMessage(sender, new RtspMessageEventArgs(message, connection));
                    }
                    return;
                default:
                    {
                        // Not reached while this and IsImplemented agree, and here so that they
                        // cannot quietly stop agreeing: a method added to one and not the other is
                        // answered rather than dropped.
                        RtspResponse unhandled = message.CreateResponse();
                        unhandled.ReturnCode = 501; // Not Implemented
                        listener.SendMessage(unhandled);
                    }
                    return;
            }
        }

        private void HandleSetup(RtspListener listener, RtspRequestSetup setupMessage)
        {
            // Check the RTSP transport
            // If it is UDP or Multicast, create the sockets
            // If it is RTP over RTSP we send data via the RTSP Listener

            // FIXME client may send more than one possible transport.
            // very rare
            RtspTransport transport;
            try
            {
                // A SETUP that names no transport at all is not asking for a default one. The parsed
                // form of an absent header is a transport like any other - and its multicast flag
                // happens to be set - so without this a client that said nothing would be answered
                // with a multicast group it never asked to join.
                if (!setupMessage.Headers.ContainsKey(RtspHeaderNames.Transport))
                {
                    _logger.LogWarning("SETUP from {remoteEndPoint} named no transport", listener.RemoteEndPoint);
                    SendUnsupportedTransport(listener, setupMessage);
                    return;
                }

                RtspTransport[] transports = setupMessage.GetTransports();
                transport = transports.Length > 0 ? transports[0] : null;
            }
            catch (Exception ex)
            {
                // the Transport header is client supplied, so failing to parse it is their error
                _logger.LogWarning(ex, "Could not parse the Transport header from {remoteEndPoint}", listener.RemoteEndPoint);
                SendUnsupportedTransport(listener, setupMessage);
                return;
            }

            // Construct the Transport: reply from the Server to the client
            RtspTransport transportReply = null;
            IRtpTransport rtpTransport = null;

            // Set when this SETUP is for a group rather than for a stream of this client's own. The
            // client's own stream is not reached until further down, where the session is looked up.
            MulticastDelivery multicastDelivery = null;


            RTSPStreamSource streamSource = GetStreamSource(setupMessage.RtspUri);
            if (streamSource == null)
            {
                // track not found
                RtspResponse setupResponse = setupMessage.CreateResponse();
                setupResponse.ReturnCode = 404;
                listener.SendMessage(setupResponse);
                return;
            }

            uint trackSSRC;
            TrackType trackType;
            if (streamSource.VideoTrack != null && AddressesTrack(setupMessage.RtspUri, streamSource.GetTrackControl(TrackType.Video)))
            {
                trackSSRC = streamSource.VideoTrack.SSRC;
                trackType = TrackType.Video;
            }
            else if (streamSource.AudioTrack != null && AddressesTrack(setupMessage.RtspUri, streamSource.GetTrackControl(TrackType.Audio)))
            {
                trackSSRC = streamSource.AudioTrack.SSRC;
                trackType = TrackType.Audio;
            }
            else
            {
                // track not found
                RtspResponse setupResponse = setupMessage.CreateResponse();
                setupResponse.ReturnCode = 404;
                listener.SendMessage(setupResponse);
                return;
            }

            if (transport == null)
            {
                // no transport we could even look at
                SendUnsupportedTransport(listener, setupMessage);
                return;
            }

            // SAVP means the media is encrypted under a key the client can only have got from the
            // SDP. A SETUP that skipped DESCRIBE has no key, so there is nothing we could send that
            // it could read - and sending it unprotected instead would quietly undo the encryption
            // the server was configured for, which is what used to happen.
            ITrack setupTrack = trackType == TrackType.Video ? streamSource.VideoTrack : streamSource.AudioTrack;
            if (setupTrack.RtpProfile == RtpProfiles.SAVP)
            {
                bool wantsMulticast = transport != null
                    && transport.LowerTransport == RtspTransport.LowerTransportType.UDP
                    && transport.IsMulticast;

                // One key and several senders is safe only where the senders differ in their SSRC,
                // which is what the keystream is worked out from. A track that forwards what it
                // receives keeps the SSRC and the numbering of whatever produced it, so every client
                // would send identical packets under the one key - two plaintexts under one
                // keystream, which anyone holding both could unwind. A group is still fine: there is
                // one sender, whatever SSRC it uses.
                if (streamSource.SharedSrtpKey && !wantsMulticast && PreservesSourceHeaders(setupTrack))
                {
                    _logger.LogWarning(
                        "Refusing a unicast SETUP of {trackType} on {streamID} from {remoteEndPoint}: it forwards the source's own headers, which every client would then send under the one shared key",
                        trackType, streamSource.StreamID, listener.RemoteEndPoint);
                    SendUnsupportedTransport(listener, setupMessage);
                    return;
                }

                RTSPConnection existingConnection = ConnectionByListener(listener);
                if (existingConnection == null || existingConnection.Streams[(int)trackType].Context == null)
                {
                    _logger.LogWarning(
                        "Refusing SETUP of the SAVP track {trackType} from {remoteEndPoint}: there are no SRTP keys for it, the client did not DESCRIBE first",
                        trackType, listener.RemoteEndPoint);

                    RtspResponse noKeysResponse = setupMessage.CreateResponse();
                    noKeysResponse.ReturnCode = 400; // Bad Request
                    listener.SendMessage(noKeysResponse);
                    return;
                }
            }

            if (transport.LowerTransport == RtspTransport.LowerTransportType.TCP)
            {
                if (transport.Interleaved == null)
                {
                    // interleaved channels are required for RTP over RTSP, and the header is
                    // client supplied, so a missing one is answered rather than dereferenced
                    _logger.LogWarning("SETUP from {remoteEndPoint} asked for TCP without interleaved channels", listener.RemoteEndPoint);
                    SendUnsupportedTransport(listener, setupMessage);
                    return;
                }

                rtpTransport = new RtpTcpTransport(listener)
                {
                    DataChannel = transport.Interleaved.First,
                    ControlChannel = transport.Interleaved.Second,
                };
                // RTP over RTSP mode
                transportReply = new RtspTransport()
                {
                    SSrc = trackSSRC.ToString("X8"), // Convert to Hex, padded to 8 characters,
                    LowerTransport = RtspTransport.LowerTransportType.TCP,
                    Interleaved = new PortCouple(transport.Interleaved.First, transport.Interleaved.Second)
                };
            }
            else if (transport.LowerTransport == RtspTransport.LowerTransportType.UDP && !transport.IsMulticast)
            {
                if (transport.ClientPort == null)
                {
                    // we have nowhere to send the RTP without the client's ports
                    _logger.LogWarning("SETUP from {remoteEndPoint} asked for UDP without a client port", listener.RemoteEndPoint);
                    SendUnsupportedTransport(listener, setupMessage);
                    return;
                }

                // RTP over UDP mode
                // Create a pair of UDP sockets - One is for the Data (eg Video/Audio), one is for the RTCP
                RtpUdpTransport udpPair;
                try
                {
                    udpPair = AllocateUdpPair(MediaFamily(listener.RemoteEndPoint.Address));
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is SocketException)
                {
                    // Every pair in the range is taken. Tell the client rather than letting this
                    // surface as an unhandled error, and let it fall back to a TCP transport.
                    // UDPSocket scans the range itself, swallowing the SocketException from each
                    // port it cannot bind, and reports the range being used up as an
                    // InvalidOperationException - so it is that, not a SocketException, that
                    // normally arrives here.
                    _logger.LogError(ex, "Ran out of UDP ports for SETUP from {remoteEndPoint}", listener.RemoteEndPoint);
                    SendUnsupportedTransport(listener, setupMessage);
                    return;
                }
                string destination = MediaDestination(listener.RemoteEndPoint.Address);
                udpPair.SetDataDestination(destination, transport.ClientPort.First);
                udpPair.SetControlDestination(destination, transport.ClientPort.Second);
                udpPair.ControlReceived += (localSender, localE) =>
                {
                    // This runs on the library's receive thread, so anything that escapes is an
                    // unhandled exception on a thread we do not own. The payload is handed back
                    // whatever happens, rather than only when nothing went wrong.
                    using (localE.Data)
                    {
                        try
                        {
                            // RTCP data received
                            _logger.LogDebug("RTCP data received {localSender} {length}", localSender, localE.Data.Data.Length);
                            var connection = ConnectionByRtpTransport(localSender as IRtpTransport);
                            connection?.UpdateKeepAlive();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error handling an RTCP packet");
                        }
                    }
                };
                udpPair.Start(); // start listening for data on the UDP ports

                // Pass the Port of the two sockets back in the reply
                transportReply = new RtspTransport()
                {
                    SSrc = trackSSRC.ToString("X8"), // Convert to Hex, padded to 8 characters,
                    LowerTransport = RtspTransport.LowerTransportType.UDP,
                    IsMulticast = false,
                    ServerPort = new PortCouple(udpPair.DataPort, udpPair.ControlPort),
                    ClientPort = transport.ClientPort
                };

                rtpTransport = udpPair;
            }
            else if (transport.LowerTransport == RtspTransport.LowerTransportType.UDP && transport.IsMulticast)
            {
                if (!MulticastEnabled)
                {
                    _logger.LogWarning("Refusing multicast SETUP from {remoteEndPoint}, multicast is turned off on this server",
                        listener.RemoteEndPoint);
                    SendUnsupportedTransport(listener, setupMessage);
                    return;
                }

                // Protecting a group needs a key everyone in it holds. Without one, each client has
                // been handed a key of its own in its own SDP, and none of them could read a stream
                // sent under any of the others.
                if (setupTrack.RtpProfile == RtpProfiles.SAVP && !streamSource.SharedSrtpKey)
                {
                    _logger.LogWarning(
                        "Refusing multicast SETUP of the protected track {trackType} from {remoteEndPoint}: its SRTP key belongs to each client, set SharedSrtpKey on the stream for a key the group can share",
                        trackType, listener.RemoteEndPoint);
                    SendUnsupportedTransport(listener, setupMessage);
                    return;
                }

                // A group is one address, and a client can only join one of its own family. Telling
                // an IPv6 client to listen to an IPv4 group names somewhere it cannot go.
                if (!IPAddress.TryParse(MulticastAddress, out IPAddress group) ||
                    group.AddressFamily != MediaFamily(listener.RemoteEndPoint.Address))
                {
                    _logger.LogWarning(
                        "Refusing multicast SETUP from {remoteEndPoint}: the group {group} is not in the family this client arrived on",
                        listener.RemoteEndPoint, MulticastAddress);
                    SendUnsupportedTransport(listener, setupMessage);
                    return;
                }

                MulticastDelivery delivery;

                try
                {
                    delivery = JoinMulticastGroup(streamSource, trackType);
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is SocketException)
                {
                    _logger.LogWarning(ex, "Could not open a multicast group for {streamID} {trackType}",
                        streamSource.StreamID, trackType);
                    SendUnsupportedTransport(listener, setupMessage);
                    return;
                }

                // The client is told where to listen and is then left alone. It has no transport of
                // its own - the media goes to the group once, however many are listening - so its
                // stream is marked rather than given a channel, and the fan out passes over it.
                multicastDelivery = delivery;

                transportReply = new RtspTransport()
                {
                    SSrc = delivery.Sender.Streams[(int)trackType].SSRC.ToString("X8"),
                    LowerTransport = RtspTransport.LowerTransportType.UDP,
                    IsMulticast = true,
                    Destination = delivery.GroupAddress,
                    Port = new PortCouple(delivery.RtpPort[(int)trackType], delivery.RtpPort[(int)trackType] + 1),
                    TTL = MulticastTimeToLive,
                };
            }

            if (transportReply != null)
            {
                // Update the stream within the session with transport information.
                // The SETUP applies to the connection it arrived on - matching on the remote address would
                // pick the wrong connection whenever two clients share a source address (localhost, NAT, ...).
                string copyOfSessionId;

                lock (_connectionList)
                {
                    var connection = _connectionList.Find(x => x.Listener == listener);
                    if (connection == null)
                    {
                        // the connection was dropped (eg. timed out) between arrival and handling of this SETUP
                        ReleaseTransport(rtpTransport);

                        // A group made for this client a moment ago, which it is now never going to
                        // listen to. Left alone it would go on holding its sockets with nobody to
                        // ever take it apart, since what takes a group apart is its last listener
                        // leaving and it would never have had one.
                        //
                        // Done here rather than outside the lock, which is where a group being shut
                        // down is normally seen to: nothing has ever been written to this one, so
                        // there is no writer to wait for.
                        if (multicastDelivery != null && multicastDelivery.Listeners.Count == 0)
                        {
                            RTSPConnection orphan = CloseMulticastGroup(streamSource);

                            if (orphan != null)
                            {
                                ReleaseUdpTransports(orphan);
                            }
                        }

                        RtspResponse goneResponse = setupMessage.CreateResponse();
                        goneResponse.ReturnCode = 454; // Session Not Found
                        listener.SendMessage(goneResponse);
                        return;
                    }

                    // In the SDP the H264/H265 video track is TrackID 0 and the Audio Track is TrackID 1
                    RTPStream stream = connection.Streams[(int)trackType];

                    // The SSRC belongs to the stream this SETUP is for. Putting it on the connection
                    // meant the second SETUP overwrote the first, so both streams went out under one
                    // SSRC while each SETUP reply had announced a different one.
                    //
                    // Where the key belongs to the stream, every client holds the same one, and what
                    // keeps that safe is that no two senders share an SSRC - so a client sent the
                    // media one to one is given one nothing has used before. A client listening to a
                    // group sends nothing itself and takes the group's.
                    stream.SSRC = streamSource.SharedSrtpKey && multicastDelivery == null
                        ? streamSource.ReserveSsrc()
                        : trackSSRC;

                    // and the reply has to name the one it will actually hear
                    transportReply.SSrc = stream.SSRC.ToString("X8");
                    stream.RequiresSrtp = setupTrack.RtpProfile == RtpProfiles.SAVP;
#pragma warning disable CS0618 // kept in step for anyone still reading the obsolete connection-wide value
                    connection.SSRC = trackSSRC;
#pragma warning restore CS0618

                    // a repeated SETUP for the same track would otherwise leak the sockets of the previous one
                    if (stream.RtpChannel != null && !ReferenceEquals(stream.RtpChannel, rtpTransport))
                    {
                        ReleaseTransport(stream.RtpChannel);
                    }

                    stream.RtpChannel = rtpTransport;

                    // A client listening to a group is counted rather than written to, so that the
                    // group lasts exactly as long as somebody wants it.
                    stream.IsMulticast = multicastDelivery != null;

                    if (multicastDelivery != null)
                    {
                        multicastDelivery.Listeners.Add(connection);
                    }

                    // When there is Video and Audio there are two SETUP commands.
                    // For the first SETUP command we will generate the connection.SessionId and return a SessionID in the Reply.
                    // For the 2nd command the client will send us the SessionID.
                    if (string.IsNullOrEmpty(connection.SessionId))
                    {
                        connection.SessionId = RandomGenerator.NextHexToken(SESSION_ID_BYTES);
                    }

                    // Copy the Session ID, as we use it in the reply
                    copyOfSessionId = connection.SessionId;
                }

                RtspResponse setupResponse = setupMessage.CreateResponse();
                setupResponse.Headers[RtspHeaderNames.Transport] = transportReply.ToString();
                setupResponse.Session = copyOfSessionId;
                setupResponse.Timeout = RTSP_TIMEOUT;
                listener.SendMessage(setupResponse);
            }
            else
            {
                RtspResponse setupResponse = setupMessage.CreateResponse();

                // unsuported transport
                setupResponse.ReturnCode = 461;
                listener.SendMessage(setupResponse);
            }
        }

        /// <summary>
        /// Tells the client we cannot provide the transport it asked for, instead of leaving it
        /// waiting on a connection we dropped.
        /// </summary>
        private static void SendUnsupportedTransport(RtspListener listener, RtspRequest setupMessage)
        {
            RtspResponse setupResponse = setupMessage.CreateResponse();
            setupResponse.ReturnCode = 461; // Unsupported Transport
            listener.SendMessage(setupResponse);
        }

        private void HandleDescribe(RtspListener listener, RtspRequest message)
        {
            _logger.LogDebug("Request for {RtspUri}", message.RtspUri);

            // TODO. Check the requstedUrl is valid. In this example we accept any RTSP URL

            var StreamSource = GetStreamSource(message.RtspUri);

            // if the SPS and PPS are not defined yet, we have to return an error
            if (StreamSource.VideoTrack == null || !StreamSource.VideoTrack.IsReady || (StreamSource.AudioTrack != null && !StreamSource.AudioTrack.IsReady))
            {
                RtspResponse describeResponse2 = message.CreateResponse();
                describeResponse2.ReturnCode = 400; // 400 Bad Request
                listener.SendMessage(describeResponse2);
                return;
            }

            string sdp = GenerateSDP(StreamSource, listener);
            if (sdp == null)
            {
                // the connection went away between this request arriving and being handled
                RtspResponse goneResponse = message.CreateResponse();
                goneResponse.ReturnCode = 454; // Session Not Found
                listener.SendMessage(goneResponse);
                return;
            }

            byte[] sdpBytes = Encoding.UTF8.GetBytes(sdp);

            // Create the response to DESCRIBE
            // This must include the Session Description Protocol (SDP)
            RtspResponse describeResponse = message.CreateResponse();

            describeResponse.AddHeader("Content-Base: " + message.RtspUri);
            describeResponse.AddHeader("Content-Type: application/sdp");
            describeResponse.Data = sdpBytes;
            describeResponse.AdjustContentLength();
            listener.SendMessage(describeResponse);
        }

        /// <summary>
        /// The "a=crypto" attribute for a stream, deriving the SRTP keys it announces.
        /// </summary>
        /// <remarks>
        /// The key lives in the SDP, which is why it is made here: a client that never receives the
        /// SDP has no way to read anything the server would send it.
        /// </remarks>
        private string BuildCryptoAttribute(RTSPStreamSource streamSource, RTPStream stream, TrackType trackType)
        {
            if (string.IsNullOrEmpty(SrtpCryptoSuite))
            {
                throw new InvalidOperationException(
                    "A track asked for the SAVP profile but the server has no SRTP crypto suite configured. " +
                    "Pass one to the RTSPServer constructor, or leave the track on AVP.");
            }

            // A stream whose key belongs to the stream derives it once and announces the same one to
            // everybody, which is the only way a group can read what is sent to it. The client's own
            // stream is given the same keys, so that the SETUP which follows can tell this client has
            // been told them.
            RTPStream keyHolder = streamSource.SharedSrtpKey ? streamSource.GroupKeys[(int)trackType] : stream;

            byte[] masterKeySalt = keyHolder.PrepareSrtpContext(SrtpCryptoSuite);

            if (!ReferenceEquals(keyHolder, stream))
            {
                stream.Context = keyHolder.Context;
            }

            var mki = keyHolder.Context.EncodeRtpContext.Mki;

            string optionalMki = "";
            if (mki.Length > 0)
            {
                // ffplay does not seem to support MKI or any optional parameters in crypto
                // appending a zero byte at the end to yield always positive value of the BigInteger
                optionalMki = $"|{new BigInteger(mki.ToArray())}:{mki.Length}";
            }

            // https://www.rfc-editor.org/rfc/rfc4568.txt
            return $"a=crypto:1 {SrtpCryptoSuite} inline:{Convert.ToBase64String(masterKeySalt)}{optionalMki}";
        }

        /// <summary>
        /// Adds an "a=crypto" attribute to each media section of an overridden SDP whose track asked
        /// for SAVP and that does not already carry one.
        /// </summary>
        /// <remarks>
        /// The keys are per connection, so this runs per DESCRIBE rather than once when the SDP is
        /// set. A section that already has a crypto attribute is left alone - whoever wrote that SDP
        /// is managing the keys themselves, and SETUP will tell them if the server disagrees.
        /// </remarks>
        private string AddMissingCryptoAttributes(string sdp, RTSPStreamSource streamSource, RTSPConnection connection)
        {
            if ((streamSource.VideoTrack == null || streamSource.VideoTrack.RtpProfile != RtpProfiles.SAVP)
                && (streamSource.AudioTrack == null || streamSource.AudioTrack.RtpProfile != RtpProfiles.SAVP))
            {
                // nothing to add, so hand back exactly what we were given rather than reformatting it
                return sdp;
            }

            var lines = new List<string>();
            using (var textReader = new StringReader(sdp))
            {
                string line;
                while ((line = textReader.ReadLine()) != null)
                {
                    lines.Add(line);
                }
            }

            // Sections are matched to tracks by their media type, not their position. An SDP that
            // lists audio before video used to get each track's keys written into the other one's
            // section, so the client decrypted with the wrong key and simply saw nothing.
            List<RTSPStreamSource.MediaSection> sections = RTSPStreamSource.ParseMediaSections(sdp);

            StringBuilder builder = new StringBuilder();
            int mediaSection = -1;

            foreach (string line in lines)
            {
                builder.Append(line).Append(SDP_LINE_ENDING);

                if (!line.StartsWith("m="))
                {
                    continue;
                }

                mediaSection++;

                if (mediaSection >= sections.Count || sections[mediaSection].HasCrypto)
                {
                    continue;
                }

                TrackType? trackType = RTSPStreamSource.TrackTypeOf(sections[mediaSection]);
                if (trackType == null)
                {
                    continue;
                }

                ITrack track = trackType == TrackType.Video ? streamSource.VideoTrack : streamSource.AudioTrack;
                if (track != null && track.RtpProfile == RtpProfiles.SAVP)
                {
                    RTPStream stream = connection.Streams[(int)trackType];
                    builder.Append(BuildCryptoAttribute(streamSource, stream, trackType.Value)).Append(SDP_LINE_ENDING);
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// The control URL of one track, built from the control attribute the SDP advertises.
        /// </summary>
        private static string TrackControlUri(Uri sessionUri, string control)
        {
            if (string.IsNullOrEmpty(control))
            {
                return sessionUri.ToString();
            }

            // a control attribute is allowed to be a whole URL, in which case it stands alone
            if (Uri.TryCreate(control, UriKind.Absolute, out Uri absolute))
            {
                return absolute.ToString();
            }

            return sessionUri.ToString().TrimEnd('/') + "/" + control.TrimStart('/');
        }

        /// <summary>
        /// Whether a SETUP URI addresses the track with the given control attribute.
        /// </summary>
        private static bool AddressesTrack(Uri setupUri, string control)
        {
            if (setupUri == null || string.IsNullOrEmpty(control))
            {
                return false;
            }

            if (Uri.TryCreate(control, UriKind.Absolute, out Uri absolute))
            {
                return string.Equals(setupUri.ToString().TrimEnd('/'), absolute.ToString().TrimEnd('/'),
                    StringComparison.OrdinalIgnoreCase);
            }

            string path = setupUri.AbsolutePath.TrimEnd('/');
            string wanted = control.Trim('/');

            return path.EndsWith("/" + wanted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, wanted, StringComparison.OrdinalIgnoreCase);
        }

        private RTSPStreamSource GetStreamSource(Uri rtspUri)
        {
            string streamID = rtspUri.AbsolutePath.TrimStart('/').Split('/').First();
            return GetStreamSource(streamID);
        }

        /// <remarks>
        /// Takes the connection list lock, which also guards <see cref="StreamSources"/>. The lock is
        /// re-entrant, so callers that already hold it can call this too.
        /// </remarks>
        private RTSPStreamSource GetStreamSource(string streamID)
        {
            lock (_connectionList)
            {
                return StreamSources.FirstOrDefault(x => x.StreamID == streamID);
            }
        }

        private string GenerateSDP(RTSPStreamSource streamSource, RtspListener listener)
        {
            RTSPConnection connection = ConnectionByListener(listener);
            if (connection == null)
            {
                // dropped between the request arriving and being handled
                return null;
            }

            if (!string.IsNullOrEmpty(streamSource.Sdp))
            {
                // An overridden SDP used to be handed back as it stood, which meant no SRTP keys were
                // ever derived for it - so a server configured for SAVP sent the media in the clear.
                return AddMissingCryptoAttributes(streamSource.Sdp, streamSource, connection);
            }

            StringBuilder sdp = new StringBuilder();

            // Generate the SDP
            // The sprop-parameter-sets provide the SPS and PPS for H264 video
            // The packetization-mode defines the H264 over RTP payloads used but is Optional
            sdp.Append("v=0\r\n");
            sdp.Append("o=user 123 0 IN IP4 0.0.0.0\r\n");
            sdp.Append($"s={SessionName}\r\n");
            sdp.Append("c=IN IP4 0.0.0.0\r\n");

            // VIDEO
            if (streamSource.VideoTrack != null)
            {
                streamSource.VideoTrack.BuildSDP(sdp);

                if (streamSource.VideoTrack.RtpProfile == RtpProfiles.SAVP)
                {
                    sdp.Append(BuildCryptoAttribute(streamSource, connection.Video, TrackType.Video)).Append(SDP_LINE_ENDING);
                }
            }

            // AUDIO
            if (streamSource.AudioTrack != null)
            {
                streamSource.AudioTrack.BuildSDP(sdp);

                if (streamSource.AudioTrack.RtpProfile == RtpProfiles.SAVP)
                {
                    sdp.Append(BuildCryptoAttribute(streamSource, connection.Audio, TrackType.Audio)).Append(SDP_LINE_ENDING);
                }
            }

            return sdp.ToString();
        }

        private RTSPConnection ConnectionByRtpTransport(IRtpTransport rtpTransport)
        {
            if (rtpTransport == null)
                return null;

            lock (_connectionList)
            {
                return _connectionList.Find(c => c.Video.RtpChannel == rtpTransport || c.Audio.RtpChannel == rtpTransport);
            }
        }

        private RTSPConnection ConnectionBySessionId(string sessionId)
        {
            if (sessionId == null)
                return null;

            lock (_connectionList)
            {
                return _connectionList.Find(c => c.SessionId == sessionId);
            }
        }

        private RTSPConnection ConnectionByListener(RtspListener listener)
        {
            if (listener == null)
                return null;

            lock (_connectionList)
            {
                return _connectionList.Find(c => c.Listener == listener);
            }
        }

        public void SendRawRTP(RTSPConnection connection, RTPStream stream, List<Memory<byte>> rtpPackets)
        {
            SendRawRTP(connection, stream, rtpPackets, false);
        }

        /// <summary>
        /// Sends RTP packets to one connection.
        /// </summary>
        /// <param name="connection">The connection to send on.</param>
        /// <param name="stream">The stream of that connection the packets belong to.</param>
        /// <param name="rtpPackets">The packets to send.</param>
        /// <param name="preserveSourceHeaders">
        /// Leave the SSRC and sequence numbers already in the packets alone, for a track forwarding
        /// RTP produced elsewhere. Otherwise both are overwritten with this server's own.
        /// </param>
        public void SendRawRTP(RTSPConnection connection, RTPStream stream, List<Memory<byte>> rtpPackets, bool preserveSourceHeaders)
        {
            if (!TrySendRawRTP(connection, stream, rtpPackets, preserveSourceHeaders))
            {
                // outside the send lock: see RTSPConnection.SendLock for why that order matters
                lock (_connectionList)
                {
                    RemoveSession(connection);
                }
            }
        }

        /// <summary>
        /// Writes one queued frame. Runs on that connection's own writer thread.
        /// </summary>
        /// <remarks>
        /// The sequence numbers and the sender report are produced here rather than when the frame
        /// was queued, so that they describe what is actually going out and in what order - a frame
        /// dropped while waiting must not leave a gap in the numbering the client is given.
        /// </remarks>
        private void WriteQueuedFrame(RTSPConnection connection, QueuedFrame frame)
        {
            bool dropConnection = false;

            lock (connection.SendLock)
            {
                if (!connection.Play)
                {
                    return;
                }

                RTPStream stream = connection.Streams[frame.StreamType];
                if (stream.RtpChannel == null)
                {
                    return;
                }

                if (frame.PreserveSourceHeaders)
                {
                    stream.SSRC = frame.SourceSsrc;
                }

                // Asked first, because the call itself is not free: it gathers its arguments into an
                // array and boxes the two numbers, once per frame and per client watching, whether
                // or not anything is listening.
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Sending RTP session {sessionId} {TransportLogName} RTP timestamp={rtpTimestamp}. Sequence={sequenceNumber}",
                        connection.SessionId, TransportLogName(stream.RtpChannel), frame.RtpTimestamp, stream.SequenceNumber);
                }

                // Decided here rather than when the frame was queued, so the report describes what is
                // actually going out and when. A report that fails is not on its own a reason to drop
                // the connection - the packet that follows it will say so more reliably.
                if (IsSenderReportDue(stream))
                {
                    if (SendRTCPSenderReport(frame.RtpTimestamp, connection, stream))
                    {
                        stream.MustSendRtcpPacket = false;
                        stream.LastSenderReportUtc = DateTime.UtcNow;
                    }
                }

                // Kept on the connection rather than made afresh. It is only ever touched under the
                // send lock, which is held here, and a list per frame per client is a lot of small
                // garbage for something whose contents are thrown away immediately.
                List<Memory<byte>> packets = connection.PacketsToSend;
                packets.Clear();

                for (int i = 0; i < frame.Packets.Count; i++)
                {
                    packets.Add(frame.Packets[i].AsMemory(0, frame.Lengths[i]));
                }

                dropConnection = !TrySendRawRTP(connection, stream, packets, frame.PreserveSourceHeaders);
            }

            // outside the send lock: see RTSPConnection.SendLock for why that order matters
            if (dropConnection)
            {
                lock (_connectionList)
                {
                    RemoveSession(connection);
                }
            }
        }

        /// <summary>
        /// Says that a write to a client failed, as loudly as the reason deserves.
        /// </summary>
        /// <remarks>
        /// A client going away is the ordinary end of a session, not a fault: players close, networks
        /// drop, people stop watching. It used to be three warnings and a stack trace each time, which
        /// buries the failures that are worth looking at among the ones that are not.
        /// </remarks>
        private void ReportWriteFailure(RTSPConnection connection, Exception exception, string what)
        {
            string remoteEndPoint = DescribeRemoteEndPoint(connection);

            if (exception == null || IsClientGone(exception))
            {
                _logger.LogDebug("Session {sessionId} at {remoteEndPoint} has gone away, dropping it ({what})",
                    connection.SessionId, remoteEndPoint, what);
                return;
            }

            _logger.LogWarning(exception, "Error writing {what} to session {sessionId} at {remoteEndPoint}, dropping it",
                what, connection.SessionId, remoteEndPoint);
        }

        /// <summary>
        /// Whether an exception from a write means the client is no longer there.
        /// </summary>
        private static bool IsClientGone(Exception exception)
        {
            for (Exception e = exception; e != null; e = e.InnerException)
            {
                if (e is ObjectDisposedException || e is IOException)
                {
                    return true;
                }

                if (e is SocketException socket)
                {
                    switch (socket.SocketErrorCode)
                    {
                        case SocketError.ConnectionReset:
                        case SocketError.ConnectionAborted:
                        case SocketError.Shutdown:
                        case SocketError.NotConnected:
                        case SocketError.OperationAborted:
                        case SocketError.Interrupted:
                            return true;
                    }
                }

                // The transport reports a closed connection as a plain exception carrying a message,
                // so for that one there is nothing better to go on than the message.
                if (e.Message.IndexOf("Connection is lost", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The remote end point of a connection that may already have been torn down, for a log line.
        /// </summary>
        private static string DescribeRemoteEndPoint(RTSPConnection connection)
        {
            try
            {
                return connection?.Listener?.RemoteEndPoint?.ToString() ?? "an unknown address";
            }
            catch (Exception)
            {
                return "an unknown address";
            }
        }

        /// <summary>
        /// Whether this stream owes its client a sender report.
        /// </summary>
        /// <remarks>
        /// One as soon as the stream starts playing, so the client gets the mapping between wall
        /// clock and RTP timestamps straight away, and then one per interval.
        /// </remarks>
        private bool IsSenderReportDue(RTPStream stream)
        {
            if (stream.MustSendRtcpPacket)
            {
                return true;
            }

            TimeSpan interval = RtcpSenderReportInterval;
            if (interval <= TimeSpan.Zero)
            {
                return false;
            }

            return DateTime.UtcNow - stream.LastSenderReportUtc >= interval;
        }

        /// <summary>
        /// Writes RTP to one connection, reporting whether it got there rather than acting on it.
        /// </summary>
        /// <returns>False if the connection could not be written to and should be dropped.</returns>
        private bool TrySendRawRTP(RTSPConnection connection, RTPStream stream, List<Memory<byte>> rtpPackets, bool preserveSourceHeaders)
        {
            lock (connection.SendLock)
            {
            if (!connection.Play)
                return true;

            if (!CanSend(stream, "RTP"))
                return true;

            bool writeError = false;
            Exception writeException = null;
            uint writtenBytes = 0;
            uint writtenPackets = 0;
            List<byte[]> rented = null;

            try
            {
            // There could be more than 1 RTP packet (if the data is fragmented)
            foreach (var r in rtpPackets)
            {
                var rtpPacket = r;

                if (preserveSourceHeaders)
                {
                    // Forwarded untouched. The sequence number is still followed, so the RTP-Info of a
                    // later PLAY and anything else reading it reports what actually went out.
                    stream.SequenceNumber = (ushort)(RTPPacketUtil.ReadSequenceNumber(rtpPacket.Span) + 1);
                }
                else
                {
                    // Stamped into a copy, not into what was handed in. One frame is shared by every
                    // connection watching the stream, and the sequence number and SSRC below are
                    // this connection's - writing them into the shared bytes meant each client
                    // stamping over the others, and whichever won was what they all received.
                    byte[] stamped = ArrayPool<byte>.Shared.Rent(rtpPacket.Length);
                    rented = rented ?? new List<byte[]>(rtpPackets.Count);
                    rented.Add(stamped);

                    rtpPacket.CopyTo(stamped);
                    rtpPacket = stamped.AsMemory(0, rtpPacket.Length);

                    // Add the specific data for each transmission
                    RTPPacketUtil.WriteSequenceNumber(rtpPacket.Span, stream.SequenceNumber);
                    stream.SequenceNumber++;

                    // Add the specific SSRC for each transmission
                    RTPPacketUtil.WriteSSRC(rtpPacket.Span, stream.SSRC);
                }

                if (stream.Context != null)
                {
                    // Pooled, like the stamping above. This is per packet and per client, so on an
                    // encrypted stream with an audience it was the largest single source of garbage
                    // on the send path - and collecting it lands on these same writer threads.
                    int required = stream.Context.CalculateRequiredSrtpPayloadLength(rtpPacket.Length);
                    byte[] rtp = ArrayPool<byte>.Shared.Rent(required);
                    rented = rented ?? new List<byte[]>(rtpPackets.Count);
                    rented.Add(rtp);

                    rtpPacket.CopyTo(rtp);
                    int ret = stream.Context.ProtectRtp(rtp, rtpPacket.Length, out var len);
                    if (ret != 0) throw new Exception("Protect failed!");
                    rtpPacket = rtp.AsMemory().Slice(0, len);
                }

                //Debug.Assert(connection.Streams[streamType].RtpChannel != null, "If connection.Streams[streamType].RtpChannel is null here the program did not handle well connection problem");
                try
                {
                    // send the whole NAL. ** We could fragment the RTP packet into smaller chuncks that fit within the MTU
                    // Send to the IP address of the Client
                    // Send to the UDP Port the Client gave us in the SETUP command
                    var channel = stream.RtpChannel;
                    if (channel != null)
                    {
                        channel.WriteToDataPort(rtpPacket.Span);
                        writtenBytes += (uint)rtpPacket.Span.Length;
                        writtenPackets++;
                    }
                    else
                    {
                        writeError = true;
                    }
                }
                catch (Exception e)
                {
                    writeError = true;
                    writeException = e;
                    break; // exit out of foreach loop
                }
            }

            if (writeError)
            {
                ReportWriteFailure(connection, writeException, "RTP");
                return false;
            }

            stream.OctetCount += writtenBytes;
            // the RTCP Sender Report reports this back to the receiver so it can work out packet loss
            stream.RtpPacketCount += writtenPackets;
            return true;
            }
            finally
            {
                if (rented != null)
                {
                    foreach (byte[] buffer in rented)
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
            }
            }
        }

        private bool SendRTCPSenderReport(uint rtpTimestamp, RTSPConnection connection, RTPStream stream)
        {
            using (var rtcpOwner = MemoryPool<byte>.Shared.Rent(28))
            {
                var rtcpSenderReport = rtcpOwner.Memory.Slice(0, 28).Span;
                const bool hasPadding = false;
                const int reportCount = 0; // an empty report
                int length = (rtcpSenderReport.Length / 4) - 1; // num 32 bit words minus 1
                RTCPUtils.WriteRTCPHeader(rtcpSenderReport, RTCPUtils.RTCP_VERSION, hasPadding, reportCount, RTCPUtils.RTCP_PACKET_TYPE_SENDER_REPORT, length, stream.SSRC);
                RTCPUtils.WriteSenderReport(rtcpSenderReport, DateTime.UtcNow, rtpTimestamp, stream.RtpPacketCount, stream.OctetCount);

                return SendRawRTCP(connection, stream, rtcpSenderReport);

                // Clear the flag. A timer may set this to True again at some point to send regular Sender Reports
                //HACK  connection.must_send_rtcp_packet = false; // A Timer may set this to true again later in case it is used as a Keepalive (eg IndigoVision)
            }
        }

        private bool SendRTCPBye(RTSPConnection connection, RTPStream stream)
        {
            using (var rtcpOwner = MemoryPool<byte>.Shared.Rent(8))
            {
                var rtcpBye = rtcpOwner.Memory.Slice(0, 8).Span;
                const bool hasPadding = false;
                const int sourceCount = 1; 
                int length = (rtcpBye.Length / 4) - 1; // num 32 bit words minus 1
                RTCPUtils.WriteRTCPHeader(rtcpBye, RTCPUtils.RTCP_VERSION, hasPadding, sourceCount, RTCPUtils.RTCP_PACKET_TYPE_BYE, length, stream.SSRC);

                return SendRawRTCP(connection, stream, rtcpBye);
            }
        }

        public bool SendRawRTCP(RTSPConnection connection, RTPStream stream, Span<byte> rtcpSenderReport)
        {
            if (!CanSend(stream, "RTCP"))
                return false;

            // The same lock the RTP takes, so a report keeps its place among the packets it reports
            // on. Only for as long as it is worth waiting, though: a writer stuck sending to a client
            // that has stopped reading holds this until TCP gives up on it, which is a minute or so,
            // and a courtesy report is not worth delaying a teardown by that. Where the caller
            // already holds the lock - the normal path, reporting on a frame it is about to send -
            // this is taken again straight away.
            if (!Monitor.TryEnter(connection.SendLock, RTCP_SEND_LOCK_TIMEOUT))
            {
                _logger.LogDebug("Skipping RTCP for session {sessionId}, it is busy writing", connection.SessionId);
                return false;
            }

            try
            {
            try
            {
                Debug.Assert(stream.RtpChannel != null, "If stream.rtpChannel is null here the program did not handle well connection problem");

                if (stream.Context != null)
                {
                    byte[] rtcp = new byte[stream.Context.EncodeRtcpContext.CalculateRequiredSrtcpPayloadLength(rtcpSenderReport.Length)];
                    rtcpSenderReport.CopyTo(rtcp);
                    int ret = stream.Context.EncodeRtcpContext.ProtectRtcp(rtcp, rtcpSenderReport.Length, out var len);
                    if (ret != 0) throw new Exception("Protect failed!");
                    rtcpSenderReport = rtcp.AsSpan().Slice(0, len);
                }

                // Send to the IP address of the Client
                // Send to the UDP Port the Client gave us in the SETUP command
                stream.RtpChannel?.WriteToControlPort(rtcpSenderReport);
            }
            catch (Exception e)
            {
                ReportWriteFailure(connection, e, "RTCP");
                return false;
            }
            return true;
            }
            finally
            {
                Monitor.Exit(connection.SendLock);
            }
        }

        /// <summary>
        /// Drops a connection and releases its transports.
        /// </summary>
        /// <remarks>
        /// Takes the connection list lock itself rather than relying on callers to hold it - some
        /// (like <see cref="SendRawRTP(RTSPConnection, RTPStream, List{Memory{byte}})"/>) are public
        /// and can be reached without it. The lock is
        /// re-entrant, so the callers that do already hold it are unaffected.
        /// </remarks>
        private void RemoveSession(RTSPConnection connection)
        {
            RtspListener listener;
            List<RTSPConnection> finishedGroups;

            lock (_connectionList)
            {
                // Deliberately not under the connection's send lock. The writer holds that while it
                // is in a write, and a write to a client that has stopped reading does not come back
                // until the socket is closed - which is what this method is on its way to doing. So
                // waiting for the lock here would wait for the very thing this is here to end.
                if (!_connectionList.Contains(connection))
                {
                    // already gone, and a second disposal of its transports is not wanted
                    return;
                }

                connection.Play = false; // stop sending data

                // Before the connection is taken off the lists, since that is where the groups it was
                // listening to are found. What it leaves empty is shut down further down, outside
                // this lock.
                finishedGroups = LeaveMulticastGroups(connection);

                // stops the writer taking any more work, and hands back what it was still holding
                connection.Outbound?.Dispose();
                connection.Outbound = null;

                // Deliberately left attached. Taking a transport off a connection is half of
                // releasing it, and doing that here - with the writer possibly part way through a
                // frame on it - makes the rest of that frame fail on a channel that went null
                // underneath it, and reports a session that ended perfectly well as a lost client.
                // Both halves happen together below, with the writer held out.
                listener = connection.Listener;

                _connectionList.Remove(connection);
                foreach (var streamSource in StreamSources)
                {
                    streamSource.ConnectionList.Remove(connection);

                    // The last one out takes the key with them. Nobody is holding it, so replacing it
                    // breaks nothing - and it is what gives the stream its SSRCs back, since what
                    // must not repeat is an SSRC under the key it was used with.
                    if (streamSource.SharedSrtpKey && streamSource.ConnectionList.Count == 0)
                    {
                        streamSource.ReleaseSharedSrtpKey();
                    }
                }
            }

            // Outside the list lock, so waiting for the writer cannot hold up the rest of the server.
            ShutDownMulticastSenders(finishedGroups);
            ReleaseUdpTransports(connection);
            CloseConnection(connection, listener);
        }

        /// <summary>
        /// Whether a track sends on what it was given, headers and all, rather than restamping it.
        /// </summary>
        private static bool PreservesSourceHeaders(ITrack track) =>
            track is ProxyTrack proxyTrack && proxyTrack.PreserveSourceHeaders;

        /// <summary>
        /// Makes sure this stream has a group carrying the given track, and hands it back.
        /// </summary>
        /// <remarks>
        /// Called under the connection list lock, which is what keeps two clients setting up the same
        /// track at the same time from opening two groups for it.
        /// </remarks>
        private MulticastDelivery JoinMulticastGroup(RTSPStreamSource streamSource, TrackType trackType)
        {
            lock (_connectionList)
            {
                MulticastDelivery delivery = streamSource.Multicast;

                if (delivery == null)
                {
                    delivery = new MulticastDelivery
                    {
                        GroupAddress = MulticastAddress,
                        Sender = new RTSPConnection
                        {
                            SessionId = "multicast " + streamSource.StreamID,
                        },
                    };

                    delivery.Sender.Outbound = new OutboundQueue(
                        _writers,
                        MaxQueuedFramesPerConnection,
                        MaxQueuedBytesPerConnection,
                        frame => WriteQueuedFrame(delivery.Sender, frame),
                        "the " + streamSource.StreamID + " multicast group",
                        _logger);

                    streamSource.Multicast = delivery;

                    // In the list frames are handed to, and only that one. It is not a client: there
                    // is no connection to time out, no session to tear down and nobody to answer.
                    streamSource.ConnectionList.Add(delivery.Sender);
                }

                if (!delivery.Carries(trackType))
                {
                    // The port the group listens on, which is a number the clients are told and not
                    // one this server binds. Sending to a group does not mean sending from the port
                    // it arrives on, and binding it here would be taking a port out of the hands of
                    // anything on this machine that wanted to listen to the group.
                    int groupPort = NextMulticastPort();

                    // The sockets it is sent from are ordinary ones, out of the ordinary range, in
                    // the family of the group they send to.
                    RtpUdpTransport group = AllocateUdpPair(
                        IPAddress.Parse(delivery.GroupAddress).AddressFamily);

                    try
                    {
                        group.MulticastTimeToLive = MulticastTimeToLive;
                        group.SetDataDestination(delivery.GroupAddress, groupPort);
                        group.SetControlDestination(delivery.GroupAddress, groupPort + 1);
                        group.Start();
                    }
                    catch (Exception)
                    {
                        ReleaseTransport(group);
                        throw;
                    }

                    RTPStream stream = delivery.Sender.Streams[(int)trackType];
                    stream.RtpChannel = group;

                    // Under the key the SDP announced to everybody, which is what makes what the
                    // group sends readable by all of them and by nobody else.
                    stream.Context = streamSource.SharedSrtpKey
                        ? streamSource.GroupKeys[(int)trackType].Context
                        : null;
                    stream.SSRC = trackType == TrackType.Video
                        ? streamSource.VideoTrack?.SSRC ?? 0
                        : streamSource.AudioTrack?.SSRC ?? 0;

                    // Put beyond use, so that no client sent the stream one to one is ever handed the
                    // SSRC the group is already sending under.
                    streamSource.ReserveSsrc(stream.SSRC);
                    stream.MustSendRtcpPacket = true;

                    delivery.RtpPort[(int)trackType] = groupPort;

                    _logger.LogInformation("Sending {streamID} {trackType} to {group}:{port}, ttl {ttl}",
                        streamSource.StreamID, trackType, delivery.GroupAddress, groupPort, MulticastTimeToLive);
                }

                return delivery;
            }
        }

        /// <summary>
        /// The next unused port for a group to be listened to on.
        /// </summary>
        /// <remarks>
        /// Handed out rather than bound. Nothing here listens on it - it is where the receivers do -
        /// so all this has to do is not name the same one twice while two groups are running.
        /// Called under the connection list lock.
        /// </remarks>
        private int NextMulticastPort()
        {
            for (int port = MulticastPortRangeStart; port + 1 < MulticastPortRangeEnd; port += PORTS_PER_RTP_PAIR)
            {
                if (_multicastPortsInUse.Contains(port))
                {
                    continue;
                }

                _multicastPortsInUse.Add(port);
                return port;
            }

            throw new InvalidOperationException(
                $"Every multicast port between {MulticastPortRangeStart} and {MulticastPortRangeEnd} is in use.");
        }

        /// <summary>
        /// The ports groups are currently being listened to on. Guarded by the connection list lock.
        /// </summary>
        private readonly HashSet<int> _multicastPortsInUse = new HashSet<int>();

        /// <summary>
        /// Starts the group for a client that has just begun playing, if it is listening to one.
        /// </summary>
        /// <remarks>
        /// The group sends nothing until somebody is playing, and keeps sending while anybody still
        /// is. It is the second of those that makes it a group: the media does not restart, pause or
        /// stop because one of the clients did.
        /// </remarks>
        private void StartMulticastIfListening(RTSPConnection connection, RTSPStreamSource streamSource)
        {
            bool listening = false;

            foreach (RTPStream stream in connection.Streams)
            {
                listening |= stream.IsMulticast;
            }

            if (!listening)
            {
                return;
            }

            lock (_connectionList)
            {
                MulticastDelivery delivery = streamSource.Multicast;

                if (delivery == null || !delivery.Listeners.Contains(connection))
                {
                    return;
                }

                delivery.Sender.Play = true;
            }
        }

        /// <summary>
        /// Takes a client out of whatever groups it was listening to, and shuts down any that nobody
        /// is left listening to.
        /// </summary>
        /// <remarks>
        /// Called while the connection list lock is held, as the session is being removed.
        /// </remarks>
        private List<RTSPConnection> LeaveMulticastGroups(RTSPConnection connection)
        {
            List<RTSPConnection> finished = null;

            foreach (RTSPStreamSource streamSource in StreamSources)
            {
                MulticastDelivery delivery = streamSource.Multicast;

                if (delivery == null || !delivery.Listeners.Remove(connection))
                {
                    continue;
                }

                if (delivery.Listeners.Count > 0)
                {
                    continue;
                }

                // The last one has gone, so there is nobody to send to.
                _logger.LogInformation("Nobody is listening to the {streamID} multicast group any more, shutting it down",
                    streamSource.StreamID);

                (finished = finished ?? new List<RTSPConnection>()).Add(CloseMulticastGroup(streamSource));
            }

            return finished;
        }

        /// <summary>
        /// Takes a stream's group apart and hands back its sender, for the caller to finish with.
        /// </summary>
        /// <remarks>
        /// Everything that can be done while the connection list is held is done here: the group is
        /// taken off the stream, out of the list frames are handed to, and its port given back.
        /// Saying goodbye and letting the sockets go are not, because both wait on whatever is
        /// writing the group, and every other connection and request on the server is waiting behind
        /// this lock. <see cref="ShutDownMulticastSenders"/> does those, outside it.
        /// </remarks>
        private RTSPConnection CloseMulticastGroup(RTSPStreamSource streamSource)
        {
            MulticastDelivery delivery = streamSource.Multicast;

            if (delivery == null)
            {
                return null;
            }

            streamSource.Multicast = null;
            streamSource.ConnectionList.Remove(delivery.Sender);
            delivery.Listeners.Clear();

            foreach (int groupPort in delivery.RtpPort)
            {
                _multicastPortsInUse.Remove(groupPort);
            }

            delivery.Sender.Play = false;
            delivery.Sender.Outbound?.Dispose();
            delivery.Sender.Outbound = null;

            return delivery.Sender;
        }

        /// <summary>
        /// Says goodbye to a group that nobody is listening to any more, and lets its sockets go.
        /// </summary>
        private void ShutDownMulticastSenders(List<RTSPConnection> senders)
        {
            if (senders == null)
            {
                return;
            }

            foreach (RTSPConnection sender in senders)
            {
                if (sender == null)
                {
                    continue;
                }

                foreach (RTPStream stream in sender.Streams)
                {
                    if (stream.RtpChannel != null)
                    {
                        SendRTCPBye(sender, stream);
                    }
                }

                ReleaseUdpTransports(sender);
            }
        }

        /// <summary>
        /// Hands a connection's UDP ports back, once its writer is out of them.
        /// </summary>
        /// <remarks>
        /// Disposing a socket while the writer is in the middle of sending on it is how a torn down
        /// session ends up throwing from inside a write - the writer holds the reference it read
        /// before the teardown started, and finds it disposed underneath itself.
        /// <para>
        /// A UDP send does not block on anything the far end does, so the wait is short. If it
        /// somehow expires the ports are handed back anyway: there are only five hundred pairs, and
        /// a write that loses the race throws where it is already handled.
        /// </para>
        /// </remarks>
        private void ReleaseUdpTransports(RTSPConnection connection)
        {
            if (!connection.Streams.Any(stream => IsUdp(stream.RtpChannel)))
            {
                return;
            }

            bool exclusive = Monitor.TryEnter(connection.SendLock, RELEASE_WAIT_FOR_WRITER);

            try
            {
                if (!exclusive)
                {
                    _logger.LogDebug("Session {sessionId} is still writing, handing its UDP ports back regardless",
                        connection.SessionId);
                }

                DetachAndRelease(connection, IsUdp);
            }
            finally
            {
                if (exclusive)
                {
                    Monitor.Exit(connection.SendLock);
                }
            }
        }

        private static bool IsUdp(IRtpTransport transport) =>
            transport is RtpUdpTransport || transport is UDPSocket || transport is MulticastUDPSocket;

        /// <summary>
        /// Takes the connection's matching transports off it and releases them, in that order and
        /// without letting go in between.
        /// </summary>
        /// <remarks>
        /// Called with the connection's send lock held, so a writer is either not in a frame yet - in
        /// which case it will find the connection no longer playing - or has finished the one it was
        /// in. Either way no write sees half of this.
        /// </remarks>
        private void DetachAndRelease(RTSPConnection connection, Func<IRtpTransport, bool> matches)
        {
            foreach (RTPStream stream in connection.Streams)
            {
                IRtpTransport transport = stream.RtpChannel;

                if (transport == null || !matches(transport))
                {
                    continue;
                }

                stream.RtpChannel = null;
                ReleaseTransport(transport);
            }
        }

        /// <summary>
        /// Shuts the socket and everything on it down, away from the connection list lock.
        /// </summary>
        /// <remarks>
        /// Closing an interleaved transport, or the listener itself, can wait on a write already in
        /// progress - and a write to a client that has stopped reading does not come back until TCP
        /// gives up on it, which is a minute or so. Doing that while holding the connection list
        /// would stall every other connection, every request and every other stream for that minute,
        /// which is the stall the outbound queue exists to prevent in the first place.
        /// </remarks>
        private void CloseConnection(RTSPConnection connection, RtspListener listener)
        {
            Action close = () =>
            {
                // A session that ends the ordinary way has no writer to interrupt, so the listener
                // is asked to stop first and closes its own socket in its own time. Closing the
                // socket underneath it instead makes the read it has outstanding fail, which is a
                // warning and a stack trace on every clean goodbye for something that went perfectly
                // well.
                bool exclusive = Monitor.TryEnter(connection.SendLock, QUIET_CLOSE_WAIT_FOR_WRITER);

                if (!exclusive)
                {
                    // Something is still writing. Closing the socket is what fails a write that is
                    // stuck, and it is safe to do underneath one - it is the disposing that is not -
                    // so that comes first here and the wait for the writer comes after it.
                    TryCloseTransport(connection.Transport);

                    // The wait is longer now because the socket is closed, so a write that was stuck
                    // in it has failed. If it somehow has not, the transport is left to the finalizer
                    // rather than pulled out from under the thread using it: disposing one while a
                    // write is in it is the kind of fault that lands later and somewhere else.
                    exclusive = Monitor.TryEnter(connection.SendLock, CLOSE_WAIT_FOR_WRITER);
                }

                if (!exclusive)
                {
                    _logger.LogWarning(
                        "Session {sessionId} is still being written to, leaving its transports to be collected rather than disposing them underneath it",
                        connection.SessionId);
                    return;
                }

                try
                {
                    // whatever was not handed back already - the interleaved ones, which share the
                    // connection's own socket rather than holding a port of their own
                    DetachAndRelease(connection, transport => true);

                    try
                    {
                        // Which closes the connection socket too, so nothing else has to.
                        listener?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error disposing an RTSP listener");
                    }

                    // In case the listener did not own it, or there never was one.
                    TryCloseTransport(connection.Transport);
                }
                finally
                {
                    Monitor.Exit(connection.SendLock);
                }
            };

            // Always off this thread. Waiting for the writer is a wait, however short, and the
            // callers of this hold nothing but must not be delayed by it.
            Task.Run(close);
        }

        /// <summary>
        /// Takes the next free RTP/RTCP port pair out of the configured range.
        /// </summary>
        /// <remarks>
        /// UDPSocket always restarts its scan at the port it is handed, so allocating from the start
        /// of the range every time makes each SETUP fail a bind on every pair this server already
        /// holds. Those failures are caught and retried, but each one is a first-chance
        /// SocketException ("Only one usage of each socket address ... is normally permitted"), so a
        /// second SETUP on the same session reports one, a third two, and so on. Carrying a cursor
        /// past the last pair handed out means the common case binds on the first try.
        /// </remarks>
        private RtpUdpTransport AllocateUdpPair(AddressFamily addressFamily)
        {
            int cursor;
            lock (_rtpPortCursorLock)
            {
                cursor = _rtpPortCursor;
            }

            RtpUdpTransport udpPair = null;

            if (cursor > RtpPortRangeStart && cursor + PORTS_PER_RTP_PAIR <= RtpPortRangeEnd)
            {
                try
                {
                    udpPair = new RtpUdpTransport(cursor, RtpPortRangeEnd, addressFamily, _logger);
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is SocketException)
                {
                    // nothing free above the cursor - fall through and sweep the range from the start
                }
            }

            // The first allocation, and every one after the cursor has run off the end or found the
            // tail full. A full sweep is also what tells us the range is really exhausted, so this
            // is the call whose failure the caller turns into a 461.
            udpPair = udpPair ?? new RtpUdpTransport(RtpPortRangeStart, RtpPortRangeEnd, addressFamily, _logger);

            lock (_rtpPortCursorLock)
            {
                int next = udpPair.DataPort + PORTS_PER_RTP_PAIR;
                _rtpPortCursor = next + PORTS_PER_RTP_PAIR <= RtpPortRangeEnd ? next : RtpPortRangeStart;
            }

            return udpPair;
        }

        /// <summary>
        /// Whether anything may go out on this stream.
        /// </summary>
        /// <remarks>
        /// A stream whose track asked for SAVP but that has no SRTP context cannot be sent on at all.
        /// Sending it in the clear would be worse than sending nothing: the server was configured to
        /// encrypt, and nothing downstream would report that it had not.
        /// </remarks>
        private bool CanSend(RTPStream stream, string what)
        {
            if (!stream.RequiresSrtp || stream.Context != null)
            {
                return true;
            }

            // at frame rate this would be a log flood, so it is said once per stream
            if (!stream.ReportedMissingSrtpKeys)
            {
                stream.ReportedMissingSrtpKeys = true;
                _logger.LogError("Dropping {what} for a stream that asked for SAVP but has no SRTP keys - refusing to send it unprotected", what);
            }

            return false;
        }

        /// <summary>
        /// Shuts a transport down and releases it.
        /// </summary>
        /// <remarks>
        /// Stop has to come first. Disposing a UDP pair on its own leaves its sockets bound until a
        /// finalizer eventually runs, so the ports stay taken - and there are only 500 pairs to hand
        /// out, after which SETUP fails with "Only one usage of each socket address ... is normally
        /// permitted". The client releases its own transports the same way.
        /// </remarks>
        private void ReleaseTransport(IRtpTransport transport)
        {
            if (transport == null)
            {
                return;
            }

            try
            {
                transport.Stop();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error stopping an RTP transport");
            }

            try
            {
                transport.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disposing an RTP transport");
            }
        }

        private static string TransportLogName(IRtpTransport transport)
        {
            switch (transport)
            {
                case RtpTcpTransport _:
                    return "TCP";
                case RtpUdpTransport _:
                    return "UDP";
                case MulticastUDPSocket _:
                    return "Multicast";
                case UDPSocket _:
                    return "UDP";
                default:
                    return "";
            }
        }

        #region IDisposable

        /// <summary>
        /// Dispose.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                _disposed = true;

                StopListen();

                DisconnectAllClients();

                List<RTSPStreamSource> streamSources;
                lock (_connectionList)
                {
                    streamSources = StreamSources.ToList();
                    StreamSources.Clear();
                }

                foreach (var streamSource in streamSources)
                {
                    if (streamSource is IDisposable disposableStreamSource)
                    {
                        disposableStreamSource.Dispose();
                    }
                }

                _pendingHandshakes.Dispose();

                // After the clients, since it is what writes to them
                _writers?.Dispose();
            }
        }

        /// <summary>
        /// Says goodbye to every connected client and releases their sockets. Without this the
        /// listeners and UDP pairs would stay open until they are finalized.
        /// </summary>
        private void DisconnectAllClients()
        {
            lock (_connectionList)
            {
                foreach (RTSPConnection connection in _connectionList.ToArray())
                {
                    foreach (var stream in connection.Streams)
                    {
                        if (stream.RtpChannel != null)
                        {
                            SendRTCPBye(connection, stream);
                        }
                    }

                    RemoveSession(connection);
                }
            }
        }

        #endregion // IDisposable

        #region Track sink

        /// <summary>
        /// Drops connections that have timed out and reports how many are left on the given stream.
        /// </summary>
        /// <param name="streamID">Stream to report on.</param>
        /// <param name="currentRtspCount">Number of connections on the stream.</param>
        /// <param name="currentRtspPlayCount">Number of those connections that are playing.</param>
        public void CheckTimeouts(string streamID, out int currentRtspCount, out int currentRtspPlayCount)
        {
            lock (_connectionList)
            {
                ReapIdleConnections();

                var streamSource = GetStreamSource(streamID);
                if (streamSource == null)
                {
                    currentRtspCount = 0;
                    currentRtspPlayCount = 0;
                    return;
                }

                currentRtspCount = streamSource.ConnectionList.Count;
                currentRtspPlayCount = streamSource.ConnectionList.Count(c => c.Play);
            }
        }

        /// <summary>
        /// Removes every connection that has not been heard from within <see cref="RTSP_TIMEOUT"/>.
        /// </summary>
        /// <remarks>
        /// The caller must hold the connection list lock. Runs on a timer as well as from the media
        /// path, so that idle connections and their UDP sockets are released even when nothing is
        /// being streamed.
        /// </remarks>
        private void ReapIdleConnections()
        {
            DateTime timeOut = DateTime.UtcNow.AddSeconds(-RTSP_TIMEOUT);

            // Convert to Array to allow us to delete from the connection list while iterating
            foreach (RTSPConnection connection in _connectionList.ToArray())
            {
                // A client that goes away without a TEARDOWN would otherwise keep its UDP port pair
                // for the whole RTSP timeout. There are only 500 pairs, so a client that reconnects
                // in a loop exhausts them long before any of them are handed back.
                if (connection.IsDisconnected)
                {
                    _logger.LogDebug("Removing session {sessionId}, the connection was closed", connection.SessionId);
                    RemoveSession(connection);
                    continue;
                }

                if (timeOut > connection.TimeSinceLastRtspKeepAlive)
                {
                    _logger.LogDebug("Removing session {sessionId} due to TIMEOUT", connection.SessionId);
                    RemoveSession(connection);
                }
            }
        }

        public bool CanAcceptNewSamples(string streamID)
        {
            // Deliberately not CheckTimeouts. This is asked for every frame of every stream, and that
            // sweeps every connection on the server looking for ones to drop - work with a timer of
            // its own, and done again on every accept. All this needs to know is whether anyone is
            // listening, so that a producer can skip packetizing into the void.
            lock (_connectionList)
            {
                RTSPStreamSource streamSource = GetStreamSource(streamID);

                if (streamSource == null)
                {
                    return false;
                }

                foreach (RTSPConnection connection in streamSource.ConnectionList)
                {
                    if (connection.Play)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public void FeedInRawRTP(string streamID, int streamType, uint rtpTimestamp, List<Memory<byte>> rtpPackets)
        {
            if (streamType != 0 && streamType != 1)
                throw new ArgumentException("Invalid streamType! Video = 0, Audio = 1");

            RTSPConnection[] connections;
            ITrack track;
            bool preserveSourceHeaders;

            // The list lock is held just long enough to read the list, not across the writing. A
            // write blocks for as long as the client at the other end declines to read, and doing
            // that under this lock stopped every other connection, every RTSP request and every
            // other stream until it finished.
            lock (_connectionList)
            {
                var streamSource = GetStreamSource(streamID);
                if (streamSource == null)
                {
                    _logger.LogWarning("Dropping RTP for unknown stream {streamID}", streamID);
                    return;
                }

                // A track that forwards RTP from elsewhere can ask for it to go out exactly as it
                // arrived, rather than being restamped as if this server had produced it.
                track = streamType == (int)TrackType.Video ? streamSource.VideoTrack : streamSource.AudioTrack;
                preserveSourceHeaders = track is ProxyTrack proxyTrack && proxyTrack.PreserveSourceHeaders;

                // ToArray makes a temp copy of the list, so the list itself can change while we write
                connections = streamSource.ConnectionList.ToArray();
            }

            // Copied once, not once per client. Everything about the frame is the same for all of
            // them, and the copy is the one cost here that grows with the size of the audience.
            var frame = new QueuedFrame
            {
                StreamType = streamType,
                RtpTimestamp = rtpTimestamp,
                PreserveSourceHeaders = preserveSourceHeaders,

                // The RTP keeps the source's SSRC, so the sender reports have to name it too - a
                // receiver ties the two together by SSRC and ignores one that does not match.
                SourceSsrc = preserveSourceHeaders ? track.SSRC : 0u,
            };
            try
            {
                // inside the try: renting the buffers can fail partway, and the ones already taken
                // still have to go back
                frame.Take(rtpPackets);

                // Go through each RTSP connection and output the RTP on the Session
                foreach (RTSPConnection connection in connections)
                {
                    // No lock here, deliberately. The lock that orders writes to a connection is held
                    // by its writer for as long as a write takes, and a write to a client that has
                    // stopped reading takes until TCP gives up - so waiting for it here would put the
                    // producer back exactly where this queue is meant to take it out of. Whether the
                    // connection is playing is set by PLAY before it writes its reply, so a frame
                    // produced the instant that reply arrived is queued behind it rather than dropped.
                    //
                    // Note: 'continue', not 'return' - one connection that is paused or not fully set
                    // up must not stop the data going to every other connection on this stream.
                    if (!connection.Play)
                        continue;

                    RTPStream stream = connection.Streams[streamType];
                    OutboundQueue outbound = connection.Outbound;

                    if (stream.RtpChannel == null || outbound == null)
                        continue;

                    // Handed to the connection rather than written here. Writing took as long as the
                    // client took to read, and every client on the stream waited its turn on this one
                    // thread, so one that stopped reading held up the media for all of them.
                    frame.AddRef();
                    outbound.Enqueue(frame);
                }
            }
            finally
            {
                // the producer's own share, now that every connection has been offered the frame
                frame.Release();
            }

            // Nothing is dropped here any more. A frame is handed to a connection rather than written
            // to it, so whether it could be sent is not known until later - the writer drops the
            // connection when its own write fails.
        }

        #endregion // Track sink

        #region Tracks

        public void AddStreamSource(RTSPStreamSource streamSource)
        {
            if (streamSource == null)
                throw new ArgumentNullException(nameof(streamSource));

            if (streamSource.VideoTrack != null)
            {
                streamSource.VideoTrack.Sink = this;
                streamSource.VideoTrack.StreamID = streamSource.StreamID;
            }

            if (streamSource.AudioTrack != null)
            {
                streamSource.AudioTrack.Sink = this;
                streamSource.AudioTrack.StreamID = streamSource.StreamID;
            }

            // the list is read by the RTSP and media threads under this lock, so it has to be taken to write it too
            lock (_connectionList)
            {
                if (StreamSources.Any(x => x.StreamID == streamSource.StreamID))
                {
                    throw new ArgumentException($"A stream source with the ID '{streamSource.StreamID}' has already been added.", nameof(streamSource));
                }

                // SSRCs are drawn at random, so a clash here means they were assigned by hand.
                // Left in place it would break demultiplexing on the receiver.
                foreach (uint ssrc in TrackSSRCs(streamSource))
                {
                    if (StreamSources.SelectMany(TrackSSRCs).Contains(ssrc) ||
                        TrackSSRCs(streamSource).Count(x => x == ssrc) > 1)
                    {
                        throw new ArgumentException($"SSRC {ssrc} is already used by another track. Every track must have a unique SSRC.", nameof(streamSource));
                    }
                }

                StreamSources.Add(streamSource);
            }
        }

        /// <summary>
        /// The SSRCs of whichever tracks a stream source has.
        /// </summary>
        private static List<uint> TrackSSRCs(RTSPStreamSource streamSource)
        {
            var ssrcs = new List<uint>(2);

            if (streamSource.VideoTrack != null)
                ssrcs.Add(streamSource.VideoTrack.SSRC);

            if (streamSource.AudioTrack != null)
                ssrcs.Add(streamSource.AudioTrack.SSRC);

            return ssrcs;
        }

        public void RemoveStreamSource(RTSPStreamSource streamSource)
        {
            if (streamSource == null)
                throw new ArgumentNullException(nameof(streamSource));

            RTSPConnection finishedGroup;

            lock (_connectionList)
            {
                if (!this.StreamSources.Contains(streamSource))
                {
                    return;
                }

                if (streamSource.VideoTrack != null)
                {
                    streamSource.VideoTrack.Sink = null;
                }

                if (streamSource.AudioTrack != null)
                {
                    streamSource.AudioTrack.Sink = null;
                }

                foreach (RTSPConnection connection in streamSource.ConnectionList.ToArray())
                {
                    // Not the group's own sender: it has no session to remove and RemoveSession would
                    // pass over it, so it is taken apart below rather than left holding its sockets.
                    if (streamSource.Multicast != null && ReferenceEquals(connection, streamSource.Multicast.Sender))
                    {
                        continue;
                    }

                    foreach (var stream in connection.Streams)
                    {
                        SendRTCPBye(connection, stream);
                    }

                    RemoveSession(connection);
                }

                // Whatever is left of the group. Usually nothing, because the last client to be
                // removed above took it with them - but a group outlives its listeners if a SETUP
                // created one and then could not finish, and a stream being taken away is the end of
                // its group whether anyone was listening or not.
                finishedGroup = CloseMulticastGroup(streamSource);

                this.StreamSources.Remove(streamSource);
            }

            ShutDownMulticastSenders(new List<RTSPConnection> { finishedGroup });
        }

        public ReadOnlyCollection<RTSPStreamSource> GetStreamSources()
        {
            lock (_connectionList)
            {
                // a snapshot, so the caller cannot observe the list changing underneath them
                return new ReadOnlyCollection<RTSPStreamSource>(StreamSources.ToList());
            }
        }

        #endregion // Tracks
    }
}
