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
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
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
        /// An RTP/RTCP pair is two consecutive ports, RTP on the first.
        /// </summary>
        private const int PORTS_PER_RTP_PAIR = 2;

        private const int NONCE_BYTES = 16;          // 128 bits of entropy for the digest nonce
        private const int SESSION_ID_BYTES = 12;     // 96 bits of entropy for the RTSP session ID

        /// <summary>
        /// How often idle connections are swept, in milliseconds.
        /// </summary>
        private const int REAP_INTERVAL = 10_000;

        /// <summary>
        /// Default value of <see cref="MaxConnections"/>.
        /// </summary>
        public const int DEFAULT_MAX_CONNECTIONS = 100;

        /// <summary>
        /// Default value of <see cref="RtpPortRangeStart"/>.
        /// </summary>
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
        private readonly IRtspListenSocket _serverListener;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _logger;

        // Where the next RTP/RTCP pair is looked for, see AllocateUdpPair. Read and advanced from the
        // RTSP receive thread of every connection, so it is guarded.
        private readonly object _rtpPortCursorLock = new object();
        private int _rtpPortCursor = -1;

        private CancellationTokenSource _stopping;
        private Task _listenThread;
        private Timer _reaperTimer;
        private readonly NetworkCredential _credentials;

        // Replaced when AuthenticationScheme changes. Read from the RTSP receive threads, which take
        // one copy per message, so a change never leaves a request half checked against two schemes.
        private volatile Authentication _authentication;
        private RtspAuthenticationScheme _authenticationScheme = RtspAuthenticationScheme.Digest;

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
                _authentication = CreateAuthentication(value);
            }
        }

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

            _authentication = CreateAuthentication(_authenticationScheme);

            RegisterRtspUriScheme();

            var tcpListener = new TcpListener(IPAddress.Any, portNumber);
            _serverListener = useHttpTunnel switch
            {
                true when tlsCertificate is null => new RtspOverHttpListenSocket(tcpListener, loggerFactory),
                true => new RtspOverHttpTLSListenSocket(tcpListener, tlsCertificate, userCertificateValidationCallback, loggerFactory),
                false when tlsCertificate is null => new RtspListenSocket(tcpListener, loggerFactory: loggerFactory),
                false => new RtspTlsListenSocket(tcpListener, tlsCertificate, userCertificateValidationCallback, loggerFactory),
            };

        }

        /// <summary>
        /// Builds the challenge for the given scheme, or null when no credentials were configured
        /// and the server is therefore open.
        /// </summary>
        private Authentication CreateAuthentication(RtspAuthenticationScheme scheme)
        {
            const string realm = "SharpRTSPServer";

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

                return new AuthenticationBasic(_credentials, realm);
            }

            return new AuthenticationDigest(_credentials, realm, RandomGenerator.NextHexToken(NONCE_BYTES), string.Empty);
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
            _serverListener.Start();
            _stopping = new CancellationTokenSource();
            _listenThread = Task.Factory.StartNew(async () => await AcceptConnection(_stopping.Token).ConfigureAwait(false),
                _stopping.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Current);

            // Idle connections are also swept from the media path, but a server with no media flowing
            // would never get there and would hold on to their sockets and UDP ports indefinitely.
            _reaperTimer = new Timer(_ => ReapIdleConnectionsSafely(), null, REAP_INTERVAL, REAP_INTERVAL);
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
                    continue;
                }

                try
                {
                    AdmitConnection(rtspSocket);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not admit the connection from {remoteEndPoint}", TryGetRemoteEndPoint(rtspSocket));
                    TryDispose(rtspSocket);
                }
            }
        }

        /// <summary>
        /// Takes on a freshly accepted connection, unless the server is already full.
        /// </summary>
        private void AdmitConnection(IRtspTransport rtspSocket)
        {
            _logger.LogDebug("Connection from {remoteEndPoint}", rtspSocket.RemoteEndPoint);

            RtspListener newListener = new RtspListener(rtspSocket, _loggerFactory.CreateLogger<RtspListener>());
            newListener.MessageReceived += RTSPMessageReceived;

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
                    RTSPConnection newConnection = new RTSPConnection()
                    {
                        Listener = newListener,
                        Transport = rtspSocket
                    };
                    _connectionList.Add(newConnection);
                }
            }

            if (!accepted)
            {
                _logger.LogWarning("Refusing connection from {remoteEndPoint}, the limit of {maxConnections} connections is reached",
                    rtspSocket.RemoteEndPoint, MaxConnections);
                newListener.MessageReceived -= RTSPMessageReceived;
                newListener.Dispose();
                return;
            }

            newListener.Start();
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

        private void TryDispose(IRtspTransport transport)
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

        /// <summary>
        /// Stops the server listener.
        /// </summary>
        public void StopListen()
        {
            _reaperTimer?.Dispose();
            _reaperTimer = null;

            _serverListener.Stop();
            _stopping?.Cancel();
            _listenThread?.Wait();
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
            Authentication authentication = _authentication;
            if (authentication != null)
            {
                if (message.Headers.ContainsKey("Authorization"))
                {
                    // The Header contained Authorization
                    // Check the message has the correct Authorization
                    // If it does not have the correct Authorization then close the RTSP connection
                    if (!authentication.IsValid(message))
                    {
                        // Send a 401 Authentication Failed reply, then close the RTSP Socket
                        RtspResponse authorizationResponse = message.CreateResponse();
                        authorizationResponse.AddHeader("WWW-Authenticate: " + authentication.GetServerResponse());
                        authorizationResponse.ReturnCode = 401;
                        listener.SendMessage(authorizationResponse);

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

            var streamSource = GetStreamSource(message.RtspUri);
            if (streamSource == null)
            {
                // invalid URI
                RtspResponse notFoundResponse = message.CreateResponse();
                notFoundResponse.ReturnCode = 404;
                listener.SendMessage(notFoundResponse);
                return;
            }

            // Update the RTSP Keepalive Timeout.
            // Match on the listener itself - matching on the remote address alone would pick the wrong
            // connection whenever two clients share a source address (localhost, NAT, ...).
            lock (_connectionList)
            {
                var oneConnection = _connectionList.Find(c => c.Listener == listener);
                if (oneConnection != null)
                {
                    oneConnection.UpdateKeepAlive();

                    if (!streamSource.ConnectionList.Contains(oneConnection))
                    {
                        streamSource.ConnectionList.Add(oneConnection);
                    }
                }
            }

            // Handle message without session
            switch (message)
            {
                case RtspRequestOptions optionsMessage:
                    listener.SendMessage(message.CreateResponse());
                    ReceivedRtspMessage?.Invoke(sender, new RtspMessageEventArgs(message));
                    return;
                case RtspRequestDescribe describeMessage:
                    HandleDescribe(listener, message);
                    ReceivedRtspMessage?.Invoke(sender, new RtspMessageEventArgs(message));
                    return;
                case RtspRequestSetup setupMessage:
                    HandleSetup(listener, setupMessage);
                    ReceivedRtspMessage?.Invoke(sender, new RtspMessageEventArgs(message));
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
                        string rtpInfo = "url=" + message.RtspUri + ";seq=" + connection.Video.SequenceNumber; // TODO Add rtptime  +";rtptime="+session.rtpInitialTimestamp;
                        rtpInfo += ",url=" + message.RtspUri + ";seq=" + connection.Audio.SequenceNumber; // TODO Add rtptime  +";rtptime="+session.rtpInitialTimestamp;

                        // 'RTP-Info: url=rtsp://192.168.1.195:8557/h264/track1;seq=33026;rtptime=3014957579,url=rtsp://192.168.1.195:8557/h264/track2;seq=42116;rtptime=3335975101'

                        // Send the reply
                        RtspResponse playResponse = message.CreateResponse();
                        playResponse.AddHeader("Range: " + range);
                        playResponse.AddHeader("RTP-Info: " + rtpInfo);
                        listener.SendMessage(playResponse);

                        connection.Video.MustSendRtcpPacket = true;
                        connection.Audio.MustSendRtcpPacket = true;

                        // Allow video and audio to go to this client
                        connection.Play = true;

                        ReceivedRtspMessage?.Invoke(sender, new RtspMessageEventArgs(message, connection));
                    }
                    return;
                case RtspRequestPause pauseMessage:
                    {
                        connection.Play = false;
                        RtspResponse pauseResponse = message.CreateResponse();
                        listener.SendMessage(pauseResponse);
                        ReceivedRtspMessage?.Invoke(sender, new RtspMessageEventArgs(message, connection));
                    }
                    return;
                case RtspRequestGetParameter getParameterMessage:
                    {
                        // Create the response to GET_PARAMETER
                        RtspResponse getParameterResponse = message.CreateResponse();
                        listener.SendMessage(getParameterResponse);
                        ReceivedRtspMessage?.Invoke(sender, new RtspMessageEventArgs(message, connection));
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
                        ReceivedRtspMessage?.Invoke(sender, new RtspMessageEventArgs(message, connection));
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
            if (streamSource.VideoTrack != null && setupMessage.RtspUri.AbsolutePath.EndsWith($"trackID={streamSource.VideoTrack.ID}"))
            {
                trackSSRC = streamSource.VideoTrack.SSRC;
                trackType = TrackType.Video;
            }
            else if (streamSource.AudioTrack != null && setupMessage.RtspUri.AbsolutePath.EndsWith($"trackID={streamSource.AudioTrack.ID}"))
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
                UDPSocket udpPair;
                try
                {
                    udpPair = AllocateUdpPair();
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
                udpPair.SetDataDestination(listener.RemoteEndPoint.Address.ToString().Split(':')[0], transport.ClientPort.First);
                udpPair.SetControlDestination(listener.RemoteEndPoint.Address.ToString().Split(':')[0], transport.ClientPort.Second);
                udpPair.ControlReceived += (localSender, localE) =>
                {
                    // RTCP data received
                    _logger.LogDebug("RTCP data received {localSender} {localE.Data.Data.Length}", localSender, localE.Data.Data.Length);
                    var connection = ConnectionByRtpTransport(localSender as IRtpTransport);
                    connection?.UpdateKeepAlive();
                    localE.Data.Dispose();
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
                // RTP over Multicast UDP is not implemented yet. Leaving transportReply null makes the
                // client fall back to a transport we do support, via the 461 reply below.
                _logger.LogWarning("Refusing multicast SETUP from {remoteEndPoint}, multicast is not supported", listener.RemoteEndPoint);
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
                    stream.SSRC = trackSSRC;
#pragma warning disable CS0618 // kept in step for anyone still reading the obsolete connection-wide value
                    connection.SSRC = trackSSRC;
#pragma warning restore CS0618

                    // a repeated SETUP for the same track would otherwise leak the sockets of the previous one
                    if (stream.RtpChannel != null && !ReferenceEquals(stream.RtpChannel, rtpTransport))
                    {
                        ReleaseTransport(stream.RtpChannel);
                    }

                    stream.RtpChannel = rtpTransport;

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
            if (!string.IsNullOrEmpty(streamSource.Sdp))
                return streamSource.Sdp; // sdp

            RTSPConnection connection = ConnectionByListener(listener);

            StringBuilder sdp = new StringBuilder();

            // Generate the SDP
            // The sprop-parameter-sets provide the SPS and PPS for H264 video
            // The packetization-mode defines the H264 over RTP payloads used but is Optional
            sdp.Append("v=0\n");
            sdp.Append("o=user 123 0 IN IP4 0.0.0.0\n");
            sdp.Append($"s={SessionName}\n");
            sdp.Append("c=IN IP4 0.0.0.0\n");

            // VIDEO
            if (streamSource.VideoTrack != null)
            {
                streamSource.VideoTrack.BuildSDP(sdp);

                if (streamSource.VideoTrack.RtpProfile == RtpProfiles.SAVP)
                {
                    byte[] masterKeySalt = connection.Video.PrepareSrtpContext(SrtpCryptoSuite);
                    byte[] mki = connection.Video.Context.EncodeRtpContext.Mki.ToArray();

                    string optionalMki = "";
                    if (mki.Length > 0)
                    {
                        // ffplay does not seem to support MKI or any optional parameters in crypto
                        optionalMki = $"|{new BigInteger(connection.Video.Context.EncodeRtpContext.Mki.ToArray())}:{connection.Video.Context.EncodeRtpContext.Mki.Length}";
                    }

                    // https://www.rfc-editor.org/rfc/rfc4568.txt
                    // appending a zero byte at the end to yield always positive value of the BigInteger
                    sdp.AppendLine($"a=crypto:1 {SrtpCryptoSuite} inline:{Convert.ToBase64String(masterKeySalt)}{optionalMki}");
                }
            }

            // AUDIO
            if (streamSource.AudioTrack != null)
            {
                streamSource.AudioTrack.BuildSDP(sdp);

                if (streamSource.AudioTrack.RtpProfile == RtpProfiles.SAVP)
                {
                    var masterKeySalt = connection.Audio.PrepareSrtpContext(SrtpCryptoSuite);
                    var mki = connection.Audio.Context.EncodeRtpContext.Mki;

                    string optionalMki = "";
                    if (mki.Length > 0)
                    {
                        // ffplay does not seem to support MKI or any optional parameters in crypto
                        optionalMki = $"|{new BigInteger(mki.ToArray())}:{mki.Length}";
                    }

                    // https://www.rfc-editor.org/rfc/rfc4568.txt
                    // appending a zero byte at the end to yield always positive value of the BigInteger
                    sdp.AppendLine($"a=crypto:1 {SrtpCryptoSuite} inline:{Convert.ToBase64String(masterKeySalt)}{optionalMki}");
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
            if (!connection.Play)
                return;

            bool writeError = false;
            uint writtenBytes = 0;
            uint writtenPackets = 0;
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
                    // Add the specific data for each transmission
                    RTPPacketUtil.WriteSequenceNumber(rtpPacket.Span, stream.SequenceNumber);
                    stream.SequenceNumber++;

                    // Add the specific SSRC for each transmission
                    RTPPacketUtil.WriteSSRC(rtpPacket.Span, stream.SSRC);
                }

                if (stream.Context != null)
                {
                    byte[] rtp = new byte[stream.Context.CalculateRequiredSrtpPayloadLength(rtpPacket.Length)];
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
                    _logger.LogWarning("UDP Write Exception " + e);
                    writeError = true;
                    break; // exit out of foreach loop
                }
            }

            if (writeError)
            {
                _logger.LogWarning("Error writing to listener " + connection.Listener.RemoteEndPoint.Address.ToString());
                _logger.LogWarning("Removing session " + connection.SessionId + " due to write error");
                RemoveSession(connection);
            }
            else
            {
                stream.OctetCount += writtenBytes;
                // the RTCP Sender Report reports this back to the receiver so it can work out packet loss
                stream.RtpPacketCount += writtenPackets;
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
                _logger.LogError(e, "Error writing RTCP to listener {remoteAdress}", connection.Listener.RemoteEndPoint.Address.ToString());
                return false;
            }
            return true;
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
            lock (_connectionList)
            {
                connection.Play = false; // stop sending data

                foreach (var stream in connection.Streams)
                {
                    ReleaseTransport(stream.RtpChannel);
                    stream.RtpChannel = null;
                }

                connection.Listener.Dispose();
                _connectionList.Remove(connection);
                foreach (var streamSource in StreamSources)
                {
                    streamSource.ConnectionList.Remove(connection);
                }
            }
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
        private UDPSocket AllocateUdpPair()
        {
            int cursor;
            lock (_rtpPortCursorLock)
            {
                cursor = _rtpPortCursor;
            }

            UDPSocket udpPair = null;

            if (cursor > RtpPortRangeStart && cursor + PORTS_PER_RTP_PAIR <= RtpPortRangeEnd)
            {
                try
                {
                    udpPair = new UDPSocket(cursor, RtpPortRangeEnd);
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is SocketException)
                {
                    // nothing free above the cursor - fall through and sweep the range from the start
                }
            }

            // The first allocation, and every one after the cursor has run off the end or found the
            // tail full. A full sweep is also what tells us the range is really exhausted, so this
            // is the call whose failure the caller turns into a 461.
            udpPair = udpPair ?? new UDPSocket(RtpPortRangeStart, RtpPortRangeEnd);

            lock (_rtpPortCursorLock)
            {
                int next = udpPair.DataPort + PORTS_PER_RTP_PAIR;
                _rtpPortCursor = next + PORTS_PER_RTP_PAIR <= RtpPortRangeEnd ? next : RtpPortRangeStart;
            }

            return udpPair;
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
            if (disposing)
            {
                StopListen();
                _stopping?.Dispose();

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
            CheckTimeouts(streamID, out _, out int currentRtspPlayCount);

            if (currentRtspPlayCount == 0)
                return false;

            return true;
        }

        public void FeedInRawRTP(string streamID, int streamType, uint rtpTimestamp, List<Memory<byte>> rtpPackets)
        {
            if (streamType != 0 && streamType != 1)
                throw new ArgumentException("Invalid streamType! Video = 0, Audio = 1");

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
                ITrack track = streamType == (int)TrackType.Video ? streamSource.VideoTrack : streamSource.AudioTrack;
                bool preserveSourceHeaders = track is ProxyTrack proxyTrack && proxyTrack.PreserveSourceHeaders;

                // Go through each RTSP connection and output the RTP on the Session
                foreach (RTSPConnection connection in streamSource.ConnectionList.ToArray()) // ToArray makes a temp copy of the list. This lets us delete items in the foreach eg when there is Write Error
                {
                    // Only process Sessions in Play Mode.
                    // Note: 'continue', not 'return' - one connection that is paused or not fully set up
                    // must not stop the data going to every other connection on this stream.
                    if (!connection.Play)
                        continue;

                    var stream = connection.Streams[streamType];

                    if (stream.RtpChannel == null)
                        continue;

                    // The RTP keeps the source's SSRC, so the sender reports have to name it too -
                    // a receiver ties the two together by SSRC and ignores a report that does not match.
                    if (preserveSourceHeaders)
                        stream.SSRC = track.SSRC;

                    _logger.LogDebug("Sending RTP session {sessionId} {TransportLogName} RTP timestamp={rtpTimestamp}. Sequence={sequenceNumber}",
                        connection.SessionId, TransportLogName(stream.RtpChannel), rtpTimestamp, stream.SequenceNumber);

                    if (stream.MustSendRtcpPacket)
                    {
                        if (!SendRTCPSenderReport(rtpTimestamp, connection, stream))
                        {
                            RemoveSession(connection);
                            continue;
                        }
                    }

                    SendRawRTP(connection, stream, rtpPackets, preserveSourceHeaders);
                }
            }
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
                    foreach (var stream in connection.Streams)
                    {
                        SendRTCPBye(connection, stream);
                    }

                    RemoveSession(connection);
                }

                this.StreamSources.Remove(streamSource);
            }
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
