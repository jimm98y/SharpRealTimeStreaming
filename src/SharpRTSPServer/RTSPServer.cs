using Microsoft.Extensions.Logging;
using Rtsp;
using Rtsp.Messages;
using Rtsp.Sdp;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

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
        /// Video track. Must be set before starting the server.
        /// </summary>
        private ITrack VideoTrack;

        /// <summary>
        /// Audio track.
        /// </summary>
        private ITrack AudioTrack;

        /// <summary>
        /// SSRC.
        /// </summary>
        public uint SSRC { get; set; } = (uint)_rand.Next(0, int.MaxValue); // 8 hex digits

        /// <summary>
        /// Session name.
        /// </summary>
        public string SessionName { get; set; } = "SharpRTSP Test Camera";

        private readonly List<RTSPConnection> _connectionList = new List<RTSPConnection>(); // list of RTSP Listeners
        private readonly TcpListener _serverListener;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _logger;
        private CancellationTokenSource _stopping;
        private Thread _listenTread;
        private int _sessionHandle = 1;
        private string _sdp = null;
        private readonly NetworkCredential _credentials;
        private readonly Authentication _authentication;

        public bool IsRTSPS { get; set; } = false;

        /// <summary>
        /// Initializes a new instance of the <see cref="RTSPServer"/> class.
        /// </summary>
        /// <param name="portNumber">Port number.</param>
        /// <param name="userName">username.</param>
        /// <param name="password">password.</param>
        public RTSPServer(int portNumber, string userName, string password) : this(portNumber, userName, password, new CustomLoggerFactory())
        { }

        /// <summary>
        /// Initializes a new instance of the <see cref="RTSPServer"/> class.
        /// </summary>
        /// <param name="portNumber">Port number.</param>
        /// <param name="userName">username.</param>
        /// <param name="password">password.</param>
        public RTSPServer(int portNumber, string userName, string password, ILoggerFactory loggerFactory)
        {
            if (portNumber < IPEndPoint.MinPort || portNumber > IPEndPoint.MaxPort)
            {
                throw new ArgumentOutOfRangeException(nameof(portNumber), portNumber, "Port number must be between System.Net.IPEndPoint.MinPort and System.Net.IPEndPoint.MaxPort");
            }

            Contract.EndContractBlock();

            if (!string.IsNullOrEmpty(userName) && !string.IsNullOrEmpty(password))
            {
                const string realm = "SharpRTSPServer";
                _credentials = new NetworkCredential(userName, password);
                _authentication = new AuthenticationDigest(_credentials, realm, new Random().Next(100000000, 999999999).ToString(), string.Empty);
            }
            else
            {
                _credentials = new NetworkCredential();
                _authentication = null;
            }

            RtspUtils.RegisterUri();
            _serverListener = new TcpListener(IPAddress.Any, portNumber);
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<RTSPServer>();
        }

        public void AddVideoTrack(ITrack track)
        {
            if (track != null)
            {
                track.Sink = this;
            }
            this.VideoTrack = track;
        }

        public void AddAudioTrack(ITrack track)
        {
            if (track != null)
            {
                track.Sink = this;
            }
            this.AudioTrack = track;
        }

        /// <summary>
        /// Starts the server listener.
        /// </summary>
        public void StartListen()
        {
            _serverListener.Start();
            _stopping = new CancellationTokenSource();
            _listenTread = new Thread(new ThreadStart(AcceptConnection));
            _listenTread.Start();
        }

        private void AcceptConnection()
        {
            try
            {
                while (_stopping?.IsCancellationRequested == false)
                {
                    // Wait for an incoming TCP Connection
                    TcpClient oneClient = _serverListener.AcceptTcpClient();
                    _logger.LogDebug("Connection from {remoteEndPoint}", oneClient.Client.RemoteEndPoint);

                    // Hand the incoming TCP connection over to the RTSP classes
                    RtspTcpTransport rtspSocket;

                    if (IsRTSPS)
                    {
                        // TODO: untested
                        rtspSocket = new RtspTcpTlsTransport(oneClient);
                    }
                    else
                    {
                        rtspSocket = new RtspTcpTransport(oneClient);
                    }

                    RtspListener newListener = new RtspListener(rtspSocket, _loggerFactory.CreateLogger<RtspListener>());
                    newListener.MessageReceived += RTSPMessageReceived;

                    // Add the RtspListener to the RTSPConnections List
                    lock (_connectionList)
                    {
                        RTSPConnection newConnection = new RTSPConnection()
                        {
                            Listener = newListener,
                            SSRC = SSRC,
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
            _listenTread?.Join();
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

            // Update the RTSP Keepalive Timeout
            lock (_connectionList)
            {
                foreach (var oneConnection in _connectionList.Where(c => c.Listener.RemoteAdress == listener.RemoteAdress))
                {
                    // found the connection
                    oneConnection.UpdateKeepAlive();
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
                    SSrc = SSRC.ToString("X8"), // Convert to Hex, padded to 8 characters
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
                udpPair.SetDataDestination(listener.RemoteAdress.Split(':')[0], transport.ClientPort.First);
                udpPair.SetControlDestination(listener.RemoteAdress.Split(':')[0], transport.ClientPort.Second);
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
                    SSrc = SSRC.ToString("X8"), // Convert to Hex, padded to 8 characters
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
                    SSrc = SSRC.ToString("X8"), // Convert to Hex, padded to 8 characters
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
                    foreach (var setupConnection in _connectionList.Where(connection => connection.Listener.RemoteAdress == listener.RemoteAdress))
                    {
                        // Check the Track ID to determine if this is a SETUP for the Video Stream
                        // or a SETUP for an Audio Stream.
                        // In the SDP the H264/H265 video track is TrackID 0
                        // and the Audio Track is TrackID 1
                        RTPStream stream;
                        if (setupMessage.RtspUri.AbsolutePath.EndsWith($"trackID={VideoTrack?.ID}")) stream = setupConnection.Video;
                        else if (setupMessage.RtspUri.AbsolutePath.EndsWith($"trackID={AudioTrack?.ID}")) stream = setupConnection.Audio;
                        else continue;// error case - track unknown
                                      // found the connection
                                      // Add the transports to the stream
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

            // if the SPS and PPS are not defined yet, we have to return an error
            if (VideoTrack == null || !VideoTrack.IsReady || (AudioTrack != null && !AudioTrack.IsReady))
            {
                RtspResponse describeResponse2 = message.CreateResponse();
                describeResponse2.ReturnCode = 400; // 400 Bad Request
                listener.SendMessage(describeResponse2);
                return;
            }

            string sdp = GenerateSDP();
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

        private string GenerateSDP()
        {
            if(!string.IsNullOrEmpty(_sdp))
                return _sdp; // sdp

            StringBuilder sdp = new StringBuilder();

            // Generate the SDP
            // The sprop-parameter-sets provide the SPS and PPS for H264 video
            // The packetization-mode defines the H264 over RTP payloads used but is Optional
            sdp.Append("v=0\n");
            sdp.Append("o=user 123 0 IN IP4 0.0.0.0\n");
            sdp.Append($"s={SessionName}\n");
            sdp.Append("c=IN IP4 0.0.0.0\n");

            // VIDEO
            VideoTrack?.BuildSDP(sdp);

            // AUDIO
            AudioTrack?.BuildSDP(sdp);

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

        /// <summary>
        /// Check timeouts.
        /// </summary>
        /// <param name="currentRtspCount"></param>
        /// <param name="currentRtspPlayCount"></param>
        public void CheckTimeouts(out int currentRtspCount, out int currentRtspPlayCount)
        {
            DateTime now = DateTime.UtcNow;

            lock (_connectionList)
            {
                currentRtspCount = _connectionList.Count;
                var timeOut = now.AddSeconds(-RTSP_TIMEOUT);

                // Convert to Array to allow us to delete from rtsp_list
                foreach (RTSPConnection connection in _connectionList.Where(c => timeOut > c.TimeSinceLastRtspKeepAlive).ToArray())
                {
                    _logger.LogDebug("Removing session {sessionId} due to TIMEOUT", connection.SessionId);
                    RemoveSession(connection);
                }

                currentRtspPlayCount = _connectionList.Count(c => c.Play);
            }
        }

        public bool CanAcceptNewSamples()
        {
            CheckTimeouts(out _, out int currentRtspPlayCount);

            if (currentRtspPlayCount == 0)
                return false;

            return true;
        }

        public void FeedInRawRTP(int streamType, uint rtpTimestamp, List<IMemoryOwner<byte>> rtpPackets)
        {
            if (streamType != 0 && streamType != 1)
                throw new ArgumentException("Invalid streamType! Video = 0, Audio = 1");

            lock (_connectionList)
            {
                // Go through each RTSP connection and output the RTP on the Session
                foreach (RTSPConnection connection in _connectionList.ToArray()) // ToArray makes a temp copy of the list. This lets us delete items in the foreach eg when there is Write Error
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

        public void SendRawRTP(RTSPConnection connection, RTPStream stream, List<IMemoryOwner<byte>> rtpPackets)
        {
            if (!connection.Play)
                return;

            bool writeError = false;
            uint writtenBytes = 0;
            // There could be more than 1 RTP packet (if the data is fragmented)
            foreach (var rtpPacket in rtpPackets)
            {
                // Add the specific data for each transmission
                var rtpPacketSpan = rtpPacket.Memory.Span;
                RTPPacketUtil.WriteSequenceNumber(rtpPacketSpan, stream.SequenceNumber);
                stream.SequenceNumber++;

                // Add the specific SSRC for each transmission
                RTPPacketUtil.WriteSSRC(rtpPacketSpan, connection.SSRC);

                //Debug.Assert(connection.Streams[streamType].RtpChannel != null, "If connection.Streams[streamType].RtpChannel is null here the program did not handle well connection problem");
                try
                {
                    // send the whole NAL. ** We could fragment the RTP packet into smaller chuncks that fit within the MTU
                    // Send to the IP address of the Client
                    // Send to the UDP Port the Client gave us in the SETUP command
                    var channel = stream.RtpChannel;
                    if (channel != null)
                    {
                        channel.WriteToDataPort(rtpPacketSpan);
                        writtenBytes += (uint)rtpPacketSpan.Length;
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
                _logger.LogWarning("Error writing to listener " + connection.Listener.RemoteAdress);
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
            using (var rtcpOwner = AdjustedSizeMemoryOwner.Rent(28))
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
                _logger.LogError(e, "Error writing RTCP to listener {remoteAdress}", connection.Listener.RemoteAdress);
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
        }

        private static string TransportLogName(IRtpTransport transport)
        {
            switch(transport)
            {
                case RtpTcpTransport _:
                    return "TCP";
                case MulticastUDPSocket _:
                    return "Multicast";
                case UDPSocket _:
                    return "UDP";
                default:
                    return "";
            };
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

                if(VideoTrack is IDisposable disposableVideoTrack)
                {
                    disposableVideoTrack.Dispose();
                    VideoTrack = null;
                }

                if (AudioTrack is IDisposable disposableAudioTrack)
                {
                    disposableAudioTrack.Dispose();
                    AudioTrack = null;
                }
            }
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
                        while(true)
                        {
                            string line = textReader.ReadLine();

                            if (line == null)
                                break;

                            builder.AppendLine(line);

                            if(line.StartsWith("m="))
                            {
                                builder.AppendLine($"a=control:trackID={mediaIndex++}");
                            }
                        }
                    }

                    sdp = builder.ToString();
                }
            }

            this._sdp = sdp;
        }

        #endregion // IDisposable

        /// <summary>
        /// An RTPStream can be a Video Stream, Audio Stream or a Metadata Stream.
        /// </summary>
        public class RTPStream
        {
            /// <summary>
            /// When true will send out a RTCP packet to match Wall Clock Time to RTP Payload timestamps.
            /// </summary>
            public bool MustSendRtcpPacket { get; set; } = false;

            /// <summary>
            /// Sequence number.
            /// </summary>
            public ushort SequenceNumber { get; set; } = 1;

            /// <summary>
            /// Pair of UDP sockets (data and control) used when sending via UDP.
            /// </summary>
            public IRtpTransport RtpChannel { get; set; }

            // <summary>
            // Time since last RTCP message received - used to spot dead UDP clients.
            // </summary>
            //public DateTime TimeSinceLastRtcpKeepalive { get; set; } = DateTime.UtcNow; 

            /// <summary>
            /// Used in the RTCP Sender Report to state how many RTP packets have been transmitted (for packet loss)
            /// </summary>
            public uint RtpPacketCount { get; set; } = 0;

            /// <summary>
            /// Number of bytes of video that have been transmitted (for average bandwidth monitoring)
            /// </summary>
            public uint OctetCount { get; set; } = 0;
        }

        /// <summary>
        /// The RTSP client connection.
        /// </summary>
        public class RTSPConnection
        {
            /// <summary>
            /// RTSP conneciton listener.
            /// </summary>
            public RtspListener Listener { get; set; }

            // Time since last RTSP message received - used to spot dead UDP clients
            public DateTime TimeSinceLastRtspKeepAlive { get; private set; } = DateTime.UtcNow;

            /// <summary>
            /// Set to true when Session is in Play mode.
            /// </summary>
            public bool Play { get; set; }

            /// <summary>
            /// SSRC value used with this client connection.
            /// </summary>
            public uint SSRC { get; set; } = (uint)_rand.Next(0, int.MaxValue);

            /// <summary>
            /// RTSP Session ID used with this client connection.
            /// </summary>
            public string SessionId { get; set; } = ""; 

            /// <summary>
            /// Video stream.
            /// </summary>
            public RTPStream Video { get { return Streams[(int)TrackType.Video]; } }

            /// <summary>
            /// Audio stream.
            /// </summary>
            public RTPStream Audio { get { return Streams[(int)TrackType.Audio]; } }

            public RTPStream[] Streams { get; } = new RTPStream[]
            {
                new RTPStream(),
                new RTPStream()
            };

            /// <summary>
            /// Update the keepalive.
            /// </summary>
            public void UpdateKeepAlive()
            {
                TimeSinceLastRtspKeepAlive = DateTime.UtcNow;
            }
        }
    }

    public interface IRtpSender
    {
        void FeedInRawRTP(int streamType, uint rtpTimestamp, List<IMemoryOwner<byte>> rtpPackets);
        bool CanAcceptNewSamples();
    }

    public interface ITrack
    {
        IRtpSender Sink { get; set; }

        /// <summary>
        /// Codec name.
        /// </summary>
        string Codec { get; }

        /// <summary>
        /// Track ID. Used to identify the track in the SDP.
        /// </summary>
        int ID { get; set; }

        /// <summary>
        /// Payload type.
        /// </summary>
        int PayloadType { get; set; }

        /// <summary>
        /// Is the track ready?
        /// </summary>
        bool IsReady { get; }

        /// <summary>
        /// Build the SDP for this track.
        /// </summary>
        /// <param name="sdp">SDP <see cref="StringBuilder"/>.</param>
        /// <returns><see cref="StringBuilder"/>.</returns>
        StringBuilder BuildSDP(StringBuilder sdp);

        /// <summary>
        /// Creates RTP packets.
        /// </summary>
        /// <param name="samples">An array of samples.</param>
        /// <param name="rtpTimestamp">RTP timestamp in the timescale of the track.</param>
        /// <returns>RTP packets.</returns>
        List<IMemoryOwner<byte>> CreateRtpPackets(ReadOnlySequence<byte> samples, uint rtpTimestamp);
        
        void FeedInRawSamples(uint rtpTimestamp, ReadOnlySequence<byte> samples);
    }

    public class CustomLoggerFactory : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        { }

        public ILogger CreateLogger(string categoryName)
        {
            return new CustomLogger();
        }

        public void Dispose()
        { }
    }

    public class CustomLogger : ILogger
    {
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
        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            switch (logLevel)
            {
                case LogLevel.Trace:
                    {
                        if (SharpRTSPServer.Log.TraceEnabled)
                        {
                            SharpRTSPServer.Log.Trace(formatter.Invoke(state, exception));
                        }
                    }
                    break;

                case LogLevel.Debug:
                    {
                        if (SharpRTSPServer.Log.DebugEnabled)
                        {
                            SharpRTSPServer.Log.Debug(formatter.Invoke(state, exception));
                        }
                    }
                    break;

                case LogLevel.Information:
                    {
                        if (SharpRTSPServer.Log.InfoEnabled)
                        {
                            SharpRTSPServer.Log.Info(formatter.Invoke(state, exception));
                        }
                    }
                    break;

                case LogLevel.Warning:
                    {
                        if (SharpRTSPServer.Log.WarnEnabled)
                        {
                            SharpRTSPServer.Log.Warn(formatter.Invoke(state, exception));
                        }
                    }
                    break;

                case LogLevel.Error:
                case LogLevel.Critical:
                    {
                        if (SharpRTSPServer.Log.ErrorEnabled)
                        {
                            SharpRTSPServer.Log.Error(formatter.Invoke(state, exception));
                        }
                    }
                    break;

                default:
                    {
                        Debug.WriteLine($"Unknown trace level: {logLevel}");
                    }
                    break;
            }
        }
    }

    internal static class Utilities
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

    public enum TrackType : int
    {
        Video = 0,
        Audio = 1
    }
}
