using Microsoft.Extensions.Logging;
using Rtsp;
using Rtsp.Messages;
using SharpRTSPServer.Logging;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SharpRTSPServer
{
    /// <summary>
    /// RTSP Server Example (c) Roger Hardiman, 2016, 2018, 2020, modified by Lukas Volf, 2024
    /// Released uder the MIT Open Source Licence
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

        private static readonly Random _rand = new Random();

        /// <summary>
        /// Session name.
        /// </summary>
        public string SessionName { get; set; } = "SharpRTSP Test Camera";

        private readonly List<RTSPConnection> _connectionList = new List<RTSPConnection>(); // list of RTSP Listeners
        private readonly IRtspListenSocket _serverListener;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _logger;
        private CancellationTokenSource _stopping;
        private Task _listenTread;
        private int _sessionHandle = 1;
        private readonly NetworkCredential _credentials;
        private readonly Authentication _authentication;

        private List<RTSPStreamSource> StreamSources = new List<RTSPStreamSource>();

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
        public RTSPServer(int portNumber, string userName, string password) : this(portNumber, userName, password, null)
        { }

        /// <summary>
        /// Initializes a new instance of the <see cref="RTSPServer"/> class.
        /// </summary>
        /// <param name="portNumber">Port number.</param>
        /// <param name="userName">User name.</param>
        /// <param name="password">Password.</param>
        /// <param name="loggerFactory">Logger factory.</param>
        public RTSPServer(int portNumber, string userName, string password, ILoggerFactory loggerFactory) : this(portNumber, userName, password, false, null, loggerFactory)
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
        public RTSPServer(int portNumber, string userName, string password, bool useHttpTunnel, X509Certificate2 tlsCertificate, ILoggerFactory loggerFactory)
        {
            if (portNumber < IPEndPoint.MinPort || portNumber > IPEndPoint.MaxPort)
            {
                throw new ArgumentOutOfRangeException(nameof(portNumber), portNumber, "Port number must be between System.Net.IPEndPoint.MinPort and System.Net.IPEndPoint.MaxPort");
            }

            Contract.EndContractBlock();

            if (loggerFactory == null)
                loggerFactory = new CustomLoggerFactory();

            if (!string.IsNullOrEmpty(userName) && !string.IsNullOrEmpty(password))
            {
                const string realm = "SharpRTSPServer";
                _credentials = new NetworkCredential(userName, password);
                _authentication = new AuthenticationDigest(_credentials, realm, _rand.Next(100000000, 999999999).ToString(), string.Empty);
            }
            else
            {
                _credentials = new NetworkCredential();
                _authentication = null;
            }

            this.UseHttpTunnel = useHttpTunnel;
            this.TlsCertificate = tlsCertificate;

            RtspUtils.RegisterUri();

            var tcpListener = new TcpListener(IPAddress.Any, portNumber);
            _serverListener = useHttpTunnel switch
            {
                true when tlsCertificate is null => new RtspOverHttpListenSocket(tcpListener, loggerFactory),
                true => new RtspOverHttpTLSListenSocket(tcpListener, tlsCertificate, loggerFactory: loggerFactory),
                false when tlsCertificate is null => new RtspListenSocket(tcpListener, loggerFactory: loggerFactory),
                false => new RtspTlsListenSocket(tcpListener, tlsCertificate, loggerFactory: loggerFactory),
            };

            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<RTSPServer>();
        }

        /// <summary>
        /// Starts the server listener.
        /// </summary>
        public void StartListen()
        {
            _serverListener.Start();
            _stopping = new CancellationTokenSource();
            _listenTread = Task.Factory.StartNew(async () => await AcceptConnection(_stopping.Token).ConfigureAwait(false),
                _stopping.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Current);
        }

        private async Task AcceptConnection(CancellationToken cancellationToken)
        {
            try
            {
                while (_stopping?.IsCancellationRequested == false)
                {
                    // Wait for an incoming TCP Connection
                    IRtspTransport rtspSocket = await _serverListener.AcceptAsync(cancellationToken);
                    _logger.LogDebug("Connection from {remoteEndPoint}", rtspSocket.RemoteEndPoint);

                    RtspListener newListener = new RtspListener(rtspSocket, _loggerFactory.CreateLogger<RtspListener>());
                    newListener.MessageReceived += RTSPMessageReceived;

                    // Add the RtspListener to the RTSPConnections List
                    lock (_connectionList)
                    {
                        RTSPConnection newConnection = new RTSPConnection()
                        {
                            Listener = newListener
                        };
                        _connectionList.Add(newConnection);
                    }

                    newListener.Start();
                }
            }
            catch (SocketException eex)
            {
                _logger.LogWarning("Got an error listening, I have to handle the stopping which also throw an error: {eex}", eex);
            }
            catch (Exception ex)
            {
                _logger.LogError("Got an error listening... {ex}", ex);
                throw;
            }
        }

        /// <summary>
        /// Stops the server listener.
        /// </summary>
        public void StopListen()
        {
            _serverListener.Stop();
            _stopping?.Cancel();
            _listenTread?.Wait();
        }

        private void RTSPMessageReceived(object sender, RtspChunkEventArgs e)
        {
            // Cast the 'sender' and 'e' into the RTSP Listener (the Socket) and the RTSP Message
            RtspListener listener = sender as RtspListener ?? throw new ArgumentException("Invalid sender", nameof(sender));

            if (!(e.Message is RtspRequest message))
            {
                _logger.LogWarning("RTSP message is not a request. Invalid dialog.");
                return;
            }

            _logger.LogDebug("RTSP message received {message}", message);

            // Check if the RTSP Message has valid authentication (validating against username,password,realm and nonce)
            if (_authentication != null)
            {
                if (message.Headers.ContainsKey("Authorization"))
                {
                    // The Header contained Authorization
                    // Check the message has the correct Authorization
                    // If it does not have the correct Authorization then close the RTSP connection
                    if (!_authentication.IsValid(message))
                    {
                        // Send a 401 Authentication Failed reply, then close the RTSP Socket
                        RtspResponse authorizationResponse = message.CreateResponse();
                        authorizationResponse.AddHeader("WWW-Authenticate: " + _authentication.GetServerResponse()); // 'Basic' or 'Digest'
                        authorizationResponse.ReturnCode = 401;
                        listener.SendMessage(authorizationResponse);

                        lock (_connectionList)
                        {
                            _connectionList.RemoveAll(c => c.Listener == listener);
                        }
                        listener.Dispose();
                        return;
                    }
                }
                else
                {
                    // Send a 401 Authentication Failed with extra info in WWW-Authenticate
                    //  to tell the Client if we are using Basic or Digest Authentication
                    RtspResponse authorizationResponse = message.CreateResponse();
                    authorizationResponse.AddHeader("WWW-Authenticate: " + _authentication.GetServerResponse());
                    authorizationResponse.ReturnCode = 401;
                    listener.SendMessage(authorizationResponse);
                    return;
                }
            }

            var streamSource = GetStreamSource(message.RtspUri);
            if(streamSource == null)
            {
                // invalid URI
                RtspResponse notFoundResponse = message.CreateResponse();
                notFoundResponse.ReturnCode = 404;
                listener.SendMessage(notFoundResponse);
                return;
            }

            // Update the RTSP Keepalive Timeout
            lock (_connectionList)
            {
                foreach (var oneConnection in _connectionList.Where(c => c.Listener.RemoteEndPoint.Address == listener.RemoteEndPoint.Address))
                {
                    // found the connection
                    oneConnection.UpdateKeepAlive();

                    if(!streamSource.ConnectionList.Contains(oneConnection))
                    {
                        streamSource.ConnectionList.Add(oneConnection);
                    }
                    break;
                }
            }

            // Handle message without session
            switch (message)
            {
                case RtspRequestOptions optionsMessage:
                    listener.SendMessage(message.CreateResponse());
                    return;
                case RtspRequestDescribe describeMessage:
                    HandleDescribe(listener, message);
                    return;
                case RtspRequestSetup setupMessage:
                    HandleSetup(listener, setupMessage);
                    return;
            }

            // handle message needing session from here
            var connection = ConnectionBySessionId(message.Session);
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
                    }
                    return;
                case RtspRequestPause pauseMessage:
                    {
                        connection.Play = false;
                        RtspResponse pauseResponse = message.CreateResponse();
                        listener.SendMessage(pauseResponse);
                    }
                    return;
                case RtspRequestGetParameter getParameterMessage:
                    {
                        // Create the reponse to GET_PARAMETER
                        RtspResponse getParameterResponse = message.CreateResponse();
                        listener.SendMessage(getParameterResponse);
                    }
                    return;
                case RtspRequestTeardown teardownMessage:
                    {
                        lock (_connectionList)
                        {
                            RemoveSession(connection);
                            listener.Dispose();
                        }
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
            RtspTransport transport = setupMessage.GetTransports()[0];

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
            if (streamSource.VideoTrack != null && setupMessage.RtspUri.AbsolutePath.EndsWith($"trackID={streamSource.VideoTrack.ID}"))
            {
                trackSSRC = streamSource.VideoTrack.SSRC;
            }
            else if (streamSource.AudioTrack != null && setupMessage.RtspUri.AbsolutePath.EndsWith($"trackID={streamSource.AudioTrack.ID}"))
            {
                trackSSRC = streamSource.AudioTrack.SSRC;
            }
            else
            {
                // track not found
                RtspResponse setupResponse = setupMessage.CreateResponse();
                setupResponse.ReturnCode = 404;
                listener.SendMessage(setupResponse);
                return;
            }

            if (transport.LowerTransport == RtspTransport.LowerTransportType.TCP)
            {
                Debug.Assert(transport.Interleaved != null, "If transport.Interleaved is null here the program did not handle well connection problem");
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
                Debug.Assert(transport.ClientPort != null, "If transport.ClientPort is null here the program did not handle well connection problem");

                // RTP over UDP mode
                // Create a pair of UDP sockets - One is for the Data (eg Video/Audio), one is for the RTCP
                var udpPair = new UDPSocket(50000, 51000); // give a range of 500 pairs (1000 addresses) to try incase some address are in use
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
                // RTP over Multicast UDP mode}
                // Create a pair of UDP sockets in Multicast Mode
                // Pass the Ports of the two sockets back in the reply
                transportReply = new RtspTransport()
                {
                    SSrc = trackSSRC.ToString("X8"), // Convert to Hex, padded to 8 characters,
                    LowerTransport = RtspTransport.LowerTransportType.UDP,
                    IsMulticast = true,
                    Port = new PortCouple(7000, 7001)  // FIX
                };

                // for now until implemented
                transportReply = null;
            }

            if (transportReply != null)
            {
                // Update the stream within the session with transport information
                // If a Session ID is passed in we should match SessionID with other SessionIDs but we can match on RemoteAddress
                string copyOfSessionId = "";

                lock (_connectionList)
                {
                    foreach (var setupConnection in _connectionList.Where(connection => connection.Listener.RemoteEndPoint.Address == listener.RemoteEndPoint.Address))
                    {
                        // Check the Track ID to determine if this is a SETUP for the Video Stream
                        // or a SETUP for an Audio Stream.
                        // In the SDP the H264/H265 video track is TrackID 0
                        // and the Audio Track is TrackID 1
                        RTPStream stream;
                        if (setupMessage.RtspUri.AbsolutePath.EndsWith($"trackID={streamSource.VideoTrack?.ID}"))
                        {
                            stream = setupConnection.Video;
                        }
                        else if (setupMessage.RtspUri.AbsolutePath.EndsWith($"trackID={streamSource.AudioTrack?.ID}"))
                        {
                            stream = setupConnection.Audio;
                        }
                        else
                        {
                            continue;// error case - track unknown
                                     // found the connection
                                     // Add the transports to the stream
                        }
                        stream.RtpChannel = rtpTransport;
                        // When there is Video and Audio there are two SETUP commands.
                        // For the first SETUP command we will generate the connection.sessionId and return a SessionID in the Reply.
                        // For the 2nd command the client will send is the SessionID.
                        if (string.IsNullOrEmpty(setupConnection.SessionId))
                        {
                            setupConnection.SessionId = _sessionHandle.ToString();
                            _sessionHandle++;
                        }
                        // ELSE, could check the Session passed in matches the Session we generated on last SETUP command
                        // Copy the Session ID, as we use it in the reply
                        copyOfSessionId = setupConnection.SessionId;
                        break;
                    }
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

            string sdp = GenerateSDP(StreamSource);
            byte[] sdpBytes = Encoding.ASCII.GetBytes(sdp);

            // Create the reponse to DESCRIBE
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

        private RTSPStreamSource GetStreamSource(string streamID)
        {
            return StreamSources.FirstOrDefault(x => x.StreamID == streamID);
        }

        private string GenerateSDP(RTSPStreamSource streamSource)
        {
            if (!string.IsNullOrEmpty(streamSource.Sdp))
                return streamSource.Sdp; // sdp

            StringBuilder sdp = new StringBuilder();

            // Generate the SDP
            // The sprop-parameter-sets provide the SPS and PPS for H264 video
            // The packetization-mode defines the H264 over RTP payloads used but is Optional
            sdp.Append("v=0\n");
            sdp.Append("o=user 123 0 IN IP4 0.0.0.0\n");
            sdp.Append($"s={SessionName}\n");
            sdp.Append("c=IN IP4 0.0.0.0\n");

            // VIDEO
            streamSource.VideoTrack?.BuildSDP(sdp);

            // AUDIO
            streamSource.AudioTrack?.BuildSDP(sdp);

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

        public void SendRawRTP(RTSPConnection connection, RTPStream stream, List<Memory<byte>> rtpPackets)
        {
            if (!connection.Play)
                return;

            bool writeError = false;
            uint writtenBytes = 0;
            // There could be more than 1 RTP packet (if the data is fragmented)
            foreach (var rtpPacket in rtpPackets)
            {
                // Add the specific data for each transmission
                RTPPacketUtil.WriteSequenceNumber(rtpPacket.Span, stream.SequenceNumber);
                stream.SequenceNumber++;

                // Add the specific SSRC for each transmission
                RTPPacketUtil.WriteSSRC(rtpPacket.Span, connection.SSRC);

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
            }
        }

        private bool SendRTCP(uint rtpTimestamp, RTSPConnection connection, RTPStream stream)
        {
            using (var rtcpOwner = MemoryPool<byte>.Shared.Rent(28))
            {
                var rtcpSenderReport = rtcpOwner.Memory.Slice(0, 28).Span;
                const bool hasPadding = false;
                const int reportCount = 0; // an empty report
                int length = (rtcpSenderReport.Length / 4) - 1; // num 32 bit words minus 1
                RTCPUtils.WriteRTCPHeader(rtcpSenderReport, RTCPUtils.RTCP_VERSION, hasPadding, reportCount, RTCPUtils.RTCP_PACKET_TYPE_SENDER_REPORT, length, connection.SSRC);
                RTCPUtils.WriteSenderReport(rtcpSenderReport, DateTime.UtcNow, rtpTimestamp, stream.RtpPacketCount, stream.OctetCount);
                return SendRawRTCP(connection, stream, rtcpSenderReport);

                // Clear the flag. A timer may set this to True again at some point to send regular Sender Reports
                //HACK  connection.must_send_rtcp_packet = false; // A Timer may set this to true again later in case it is used as a Keepalive (eg IndigoVision)
            }
        }

        public bool SendRawRTCP(RTSPConnection connection, RTPStream stream, Span<byte> rtcpSenderReport)
        {
            try
            {
                Debug.Assert(stream.RtpChannel != null, "If stream.rtpChannel is null here the program did not handle well connection problem");
                // Send to the IP address of the Client
                // Send to the UDP Port the Client gave us in the SETUP command
                stream.RtpChannel.WriteToControlPort(rtcpSenderReport);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error writing RTCP to listener {remoteAdress}", connection.Listener.RemoteEndPoint.Address.ToString());
                return false;
            }
            return true;
        }

        private void RemoveSession(RTSPConnection connection)
        {
            connection.Play = false; // stop sending data
            connection.Video.RtpChannel?.Dispose();
            connection.Video.RtpChannel = null;
            connection.Audio.RtpChannel?.Dispose();
            connection.Audio.RtpChannel = null;
            connection.Listener.Dispose();
            _connectionList.Remove(connection);
            foreach(var streamSource in StreamSources)
            {
                streamSource.ConnectionList.Remove(connection);
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

                var streamSources = StreamSources.ToList();
                for (int i = 0; i < StreamSources.Count; i++)
                {
                    var streamSource = streamSources[i];
                    if (streamSource is IDisposable disposableStreamSource)
                    {
                        disposableStreamSource.Dispose();
                    }
                }

                StreamSources.Clear();                
            }
        }
                
        #endregion // IDisposable

        #region Track sink

        /// <summary>
        /// Check timeouts.
        /// </summary>
        /// <param name="streamID"></param>
        /// <param name="currentRtspCount"></param>
        /// <param name="currentRtspPlayCount"></param>
        public void CheckTimeouts(string streamID, out int currentRtspCount, out int currentRtspPlayCount)
        {
            DateTime now = DateTime.UtcNow;

            lock (_connectionList)
            {
                var streamSource = GetStreamSource(streamID);

                currentRtspCount = streamSource.ConnectionList.Count;
                var timeOut = now.AddSeconds(-RTSP_TIMEOUT);

                // Convert to Array to allow us to delete from rtsp_list
                foreach (RTSPConnection connection in _connectionList.Where(c => timeOut > c.TimeSinceLastRtspKeepAlive).ToArray())
                {
                    _logger.LogDebug("Removing session {sessionId} due to TIMEOUT", connection.SessionId);
                    RemoveSession(connection);
                }

                currentRtspPlayCount = streamSource.ConnectionList.Count(c => c.Play);
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

                // Go through each RTSP connection and output the RTP on the Session
                foreach (RTSPConnection connection in streamSource.ConnectionList.ToArray()) // ToArray makes a temp copy of the list. This lets us delete items in the foreach eg when there is Write Error
                {
                    // Only process Sessions in Play Mode
                    if (!connection.Play)
                        return;

                    var stream = connection.Streams[streamType];

                    if (stream.RtpChannel == null)
                        return;

                    _logger.LogDebug("Sending RTP session {sessionId} {TransportLogName} RTP timestamp={rtpTimestamp}. Sequence={sequenceNumber}",
                        connection.SessionId, TransportLogName(stream.RtpChannel), rtpTimestamp, stream.SequenceNumber);

                    if (stream.MustSendRtcpPacket)
                    {
                        if (!SendRTCP(rtpTimestamp, connection, stream))
                        {
                            RemoveSession(connection);
                            continue;
                        }
                    }

                    SendRawRTP(connection, stream, rtpPackets);
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

            this.StreamSources.Add(streamSource);
        }

        #endregion // Tracks
    }
}
