using Microsoft.Extensions.Logging;
using Rtsp;
using Rtsp.Messages;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SharpRTSPServer
{
    // RTSP Server Example (c) Roger Hardiman, 2016, 2018, 2020, modified by Lukas Volf, 2024
    // Released uder the MIT Open Source Licence
    //
    // Re-uses some code from the Multiplexer example of SharpRTSP
    //
    // Creates a server to listen for RTSP Commands (eg OPTIONS, DESCRIBE, SETUP, PLAY)
    // Accepts VPS/SPS/PPS/NAL H264/H265 video data and sends out to RTSP clients
    // 

    public class RTSPServer : IDisposable
    {
        public ITrack VideoTrack { get; set; }
        public ITrack AudioTrack { get; set; }

        private const uint GLOBAL_SSRC = 0x4321FADE; // 8 hex digits
        private const int RTSP_TIMEOUT = 60; // 60 seconds

        private readonly TcpListener _serverListener;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _logger;
        private CancellationTokenSource _stopping;
        private Thread _listenTread;

        public const int DYNAMIC_PAYLOAD_TYPE = 96; // Dynamic payload type base

        private readonly List<RTSPConnection> _connectionList = new List<RTSPConnection>(); // list of RTSP Listeners

        private int _sessionHandle = 1;
        private readonly NetworkCredential _credentials;
        private readonly Authentication _authentication;

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

        /// <summary>
        /// Starts the listen.
        /// </summary>
        public void StartListen()
        {
            _serverListener.Start();

            _stopping = new CancellationTokenSource();
            _listenTread = new Thread(new ThreadStart(AcceptConnection));
            _listenTread.Start();
        }

        /// <summary>
        /// Accepts the connection.
        /// </summary>
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
                    var rtsp_socket = new RtspTcpTransport(oneClient);
                    RtspListener newListener = new RtspListener(rtsp_socket, _loggerFactory.CreateLogger<RtspListener>());
                    newListener.MessageReceived += RTSPMessageReceived;

                    // Add the RtspListener to the RTSPConnections List
                    lock (_connectionList)
                    {
                        RTSPConnection new_connection = new RTSPConnection()
                        {
                            Listener = newListener,
                            ssrc = GLOBAL_SSRC,
                        };
                        _connectionList.Add(new_connection);
                    }

                    newListener.Start();
                }
            }
            catch (SocketException eex)
            {
                _logger.LogWarning("Got an error listening, I have to handle the stopping which also throw an error", eex);
            }
            catch (Exception ex)
            {
                _logger.LogError("Got an error listening...", ex);
                throw;
            }
        }

        public void StopListen()
        {
            _serverListener.Stop();
            _stopping?.Cancel();
            _listenTread?.Join();
        }

        // Process each RTSP message that is received
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
                        RtspResponse authorization_response = message.CreateResponse();
                        authorization_response.AddHeader("WWW-Authenticate: " + _authentication.GetServerResponse()); // 'Basic' or 'Digest'
                        authorization_response.ReturnCode = 401;
                        listener.SendMessage(authorization_response);

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
                    // to tell the Client if we are using Basic or Digest Authentication
                    RtspResponse authorization_response = message.CreateResponse();
                    authorization_response.AddHeader("WWW-Authenticate: " + _authentication.GetServerResponse());
                    authorization_response.ReturnCode = 401;
                    listener.SendMessage(authorization_response);
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
                    // Search for the Session in the Sessions List. Change the state to "PLAY"
                    const string range = "npt=0-";   // Playing the 'video' from 0 seconds until the end
                    string rtp_info = "url=" + message.RtspUri + ";seq=" + connection.video.sequenceNumber; // TODO Add rtptime  +";rtptime="+session.rtp_initial_timestamp;
                                                                                                            // Add audio too
                    rtp_info += ",url=" + message.RtspUri + ";seq=" + connection.audio.sequenceNumber; // TODO Add rtptime  +";rtptime="+session.rtp_initial_timestamp;

                    //    'RTP-Info: url=rtsp://192.168.1.195:8557/h264/track1;seq=33026;rtptime=3014957579,url=rtsp://192.168.1.195:8557/h264/track2;seq=42116;rtptime=3335975101'

                    // Send the reply
                    RtspResponse play_response = message.CreateResponse();
                    play_response.AddHeader("Range: " + range);
                    play_response.AddHeader("RTP-Info: " + rtp_info);
                    listener.SendMessage(play_response);

                    connection.video.must_send_rtcp_packet = true;
                    connection.audio.must_send_rtcp_packet = true;

                    // Allow video and audio to go to this client
                    connection.play = true;
                    return;
                case RtspRequestPause pauseMessage:
                    connection.play = false;
                    RtspResponse pause_response = message.CreateResponse();
                    listener.SendMessage(pause_response);
                    return;
                case RtspRequestGetParameter getParameterMessage:
                    // Create the reponse to GET_PARAMETER
                    RtspResponse getparameter_response = message.CreateResponse();
                    listener.SendMessage(getparameter_response);
                    return;
                case RtspRequestTeardown teardownMessage:
                    lock (_connectionList)
                    {
                        RemoveSession(connection);
                        listener.Dispose();
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
            RtspTransport transport_reply = null;
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
                transport_reply = new RtspTransport()
                {
                    SSrc = GLOBAL_SSRC.ToString("X8"), // Convert to Hex, padded to 8 characters
                    LowerTransport = RtspTransport.LowerTransportType.TCP,
                    Interleaved = new PortCouple(transport.Interleaved.First, transport.Interleaved.Second)
                };
            }
            else if (transport.LowerTransport == RtspTransport.LowerTransportType.UDP && !transport.IsMulticast)
            {
                Debug.Assert(transport.ClientPort != null, "If transport.ClientPort is null here the program did not handle well connection problem");

                // RTP over UDP mode
                // Create a pair of UDP sockets - One is for the Data (eg Video/Audio), one is for the RTCP
                var udp_pair = new UDPSocket(50000, 51000); // give a range of 500 pairs (1000 addresses) to try incase some address are in use
                udp_pair.SetDataDestination(listener.RemoteAdress.Split(':')[0], transport.ClientPort.First);
                udp_pair.SetControlDestination(listener.RemoteAdress.Split(':')[0], transport.ClientPort.Second);
                udp_pair.ControlReceived += (local_sender, local_e) =>
                {
                    // RTCP data received
                    _logger.LogDebug("RTCP data received {local_sender} {local_e.Data.Data.Length}", local_sender, local_e.Data.Data.Length);
                    var connection = ConnectionByRtpTransport(local_sender as IRtpTransport);
                    connection?.UpdateKeepAlive();
                    local_e.Data.Dispose();
                };
                udp_pair.Start(); // start listening for data on the UDP ports

                // Pass the Port of the two sockets back in the reply
                transport_reply = new RtspTransport()
                {
                    SSrc = GLOBAL_SSRC.ToString("X8"), // Convert to Hex, padded to 8 characters
                    LowerTransport = RtspTransport.LowerTransportType.UDP,
                    IsMulticast = false,
                    ServerPort = new PortCouple(udp_pair.DataPort, udp_pair.ControlPort),
                    ClientPort = transport.ClientPort
                };

                rtpTransport = udp_pair;
            }
            else if (transport.LowerTransport == RtspTransport.LowerTransportType.UDP && transport.IsMulticast)
            {
                // RTP over Multicast UDP mode}
                // Create a pair of UDP sockets in Multicast Mode
                // Pass the Ports of the two sockets back in the reply
                transport_reply = new RtspTransport()
                {
                    SSrc = GLOBAL_SSRC.ToString("X8"), // Convert to Hex, padded to 8 characters
                    LowerTransport = RtspTransport.LowerTransportType.UDP,
                    IsMulticast = true,
                    Port = new PortCouple(7000, 7001)  // FIX
                };

                // for now until implemented
                transport_reply = null;
            }

            if (transport_reply != null)
            {
                // Update the stream within the session with transport information
                // If a Session ID is passed in we should match SessionID with other SessionIDs but we can match on RemoteAddress
                string copy_of_session_id = "";
                lock (_connectionList)
                {
                    foreach (var setupConnection in _connectionList.Where(connection => connection.Listener.RemoteAdress == listener.RemoteAdress))
                    {
                        // Check the Track ID to determine if this is a SETUP for the Video Stream
                        // or a SETUP for an Audio Stream.
                        // In the SDP the H264/H265 video track is TrackID 0
                        // and the Audio Track is TrackID 1
                        RTPStream stream;
                        if (setupMessage.RtspUri.AbsolutePath.EndsWith("trackID=0")) stream = setupConnection.video;
                        else if (setupMessage.RtspUri.AbsolutePath.EndsWith("trackID=1")) stream = setupConnection.audio;
                        else continue;// error case - track unknown
                                      // found the connection
                                      // Add the transports to the stream
                        stream.rtpChannel = rtpTransport;
                        // When there is Video and Audio there are two SETUP commands.
                        // For the first SETUP command we will generate the connection.session_id and return a SessionID in the Reply.
                        // For the 2nd command the client will send is the SessionID.
                        if (string.IsNullOrEmpty(setupConnection.session_id))
                        {
                            setupConnection.session_id = _sessionHandle.ToString();
                            _sessionHandle++;
                        }
                        // ELSE, could check the Session passed in matches the Session we generated on last SETUP command
                        // Copy the Session ID, as we use it in the reply
                        copy_of_session_id = setupConnection.session_id;
                        break;
                    }
                }

                RtspResponse setup_response = setupMessage.CreateResponse();
                setup_response.Headers[RtspHeaderNames.Transport] = transport_reply.ToString();
                setup_response.Session = copy_of_session_id;
                setup_response.Timeout = RTSP_TIMEOUT;
                listener.SendMessage(setup_response);
            }
            else
            {
                RtspResponse setup_response = setupMessage.CreateResponse();
                // unsuported transport
                setup_response.ReturnCode = 461;
                listener.SendMessage(setup_response);
            }
        }

        private void HandleDescribe(RtspListener listener, RtspRequest message)
        {
            _logger.LogDebug("Request for {RtspUri}", message.RtspUri);

            // TODO. Check the requsted_url is valid. In this example we accept any RTSP URL

            // if the SPS and PPS are not defined yet, we have to return an error
            if ((VideoTrack == null || !VideoTrack.IsReady) || (AudioTrack != null && !AudioTrack.IsReady))
            {
                RtspResponse describe_response2 = message.CreateResponse();
                describe_response2.ReturnCode = 400; // 400 Bad Request
                listener.SendMessage(describe_response2);
                return;
            }

            StringBuilder sdp = new StringBuilder();

            // Generate the SDP
            // The sprop-parameter-sets provide the SPS and PPS for H264 video
            // The packetization-mode defines the H264 over RTP payloads used but is Optional
            sdp.Append("v=0\n");
            sdp.Append("o=user 123 0 IN IP4 0.0.0.0\n");
            sdp.Append("s=SharpRTSP Test Camera\n");
            sdp.Append("c=IN IP4 0.0.0.0\n");

            // VIDEO
            VideoTrack.BuildSDP(sdp);
            
            // AUDIO
            AudioTrack?.BuildSDP(sdp);

            byte[] sdp_bytes = Encoding.ASCII.GetBytes(sdp.ToString());

            // Create the reponse to DESCRIBE
            // This must include the Session Description Protocol (SDP)
            RtspResponse describe_response = message.CreateResponse();

            describe_response.AddHeader("Content-Base: " + message.RtspUri);
            describe_response.AddHeader("Content-Type: application/sdp");
            describe_response.Data = sdp_bytes;
            describe_response.AdjustContentLength();
            listener.SendMessage(describe_response);
        }

        private RTSPConnection ConnectionByRtpTransport(IRtpTransport rtpTransport)
        {
            if (rtpTransport is null) return null;
            lock (_connectionList)
            {
                return _connectionList.Find(c => c.video.rtpChannel == rtpTransport || c.audio.rtpChannel == rtpTransport);
            }
        }

        private RTSPConnection ConnectionBySessionId(string sessionId)
        {
            if (sessionId is null) return null;
            lock (_connectionList)
            {
                return _connectionList.Find(c => c.session_id == sessionId);
            }
        }

        public void CheckTimeouts(out int current_rtsp_count, out int current_rtsp_play_count)
        {
            DateTime now = DateTime.UtcNow;

            lock (_connectionList)
            {
                current_rtsp_count = _connectionList.Count;
                var timeOut = now.AddSeconds(-RTSP_TIMEOUT);
                // Convert to Array to allow us to delete from rtsp_list
                foreach (RTSPConnection connection in _connectionList.Where(c => timeOut > c.TimeSinceLastRtspKeepAlive).ToArray())
                {
                    _logger.LogDebug("Removing session {sessionId} due to TIMEOUT", connection.session_id);
                    RemoveSession(connection);
                }
                current_rtsp_play_count = _connectionList.Count(c => c.play);
            }
        }

        // Feed in Raw NALs - no 32 bit headers, no 00 00 00 01 headers
        public void FeedInRawNAL(uint timestamp, List<byte[]> nal_array)
        {
            CheckTimeouts(out int current_rtsp_count, out int current_rtsp_play_count);

            if (current_rtsp_play_count == 0) return;

            uint rtp_timestamp = timestamp; 

            // Build a list of 1 or more RTP packets
            // The last packet will have the M bit set to '1'
            (List<Memory<byte>> rtp_packets, List<IMemoryOwner<byte>> memoryOwners) = PrepareVideoRtpPackets(nal_array, rtp_timestamp, VideoTrack.PayloadType);

            lock (_connectionList)
            {
                // Go through each RTSP connection and output the NAL on the Video Session
                foreach (RTSPConnection connection in _connectionList.ToArray()) // ToArray makes a temp copy of the list.
                                                                                    // This lets us delete items in the foreach
                                                                                    // eg when there is Write Error
                {
                    // Only process Sessions in Play Mode
                    if (!connection.play) continue;

                    if (connection.video.rtpChannel is null) continue;
                    _logger.LogDebug("Sending video session {sessionId} {TransportLogName} Timestamp(clock)={timestamp_ms}. RTP timestamp={rtp_timestamp}. Sequence={sequenceNumber}",
                        connection.session_id, TransportLogName(connection.video.rtpChannel), timestamp, rtp_timestamp, connection.video.sequenceNumber);

                    if (connection.video.must_send_rtcp_packet)
                    {
                        if (!SendRTCP(rtp_timestamp, connection, connection.video))
                        {
                            RemoveSession(connection);
                        }
                    }

                    // There could be more than 1 RTP packet (if the data is fragmented)
                    foreach (var rtp_packet in rtp_packets)
                    {
                        // Add the specific data for each transmission
                        RTPPacketUtil.WriteSequenceNumber(rtp_packet.Span, connection.video.sequenceNumber);
                        connection.video.sequenceNumber++;

                        // Add the specific SSRC for each transmission
                        RTPPacketUtil.WriteSSRC(rtp_packet.Span, connection.ssrc);

                        Debug.Assert(connection.video.rtpChannel != null, "If connection.video.rptChannel is null here the program did not handle well connection problem");
                        try
                        {
                            // send the whole NAL. ** We could fragment the RTP packet into smaller chuncks that fit within the MTU
                            // Send to the IP address of the Client
                            // Send to the UDP Port the Client gave us in the SETUP command
                            connection.video.rtpChannel.WriteToDataPort(rtp_packet.Span);
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine("UDP Write Exception " + e);
                            Console.WriteLine("Error writing to listener " + connection.Listener.RemoteAdress);
                            Console.WriteLine("Removing session " + connection.session_id + " due to write error");
                            RemoveSession(connection);
                            break; // exit out of foreach loop
                        }
                    }
                    connection.video.octet_count += (uint)nal_array.Sum(nal => nal.Length); // QUESTION - Do I need to include the RTP header bytes/fragmenting bytes
                }
            }

            foreach (var owner in memoryOwners)
            {
                owner.Dispose();
            }
        }

        private bool SendRTCP(uint rtp_timestamp, RTSPConnection connection, RTPStream stream)
        {
            using (var rtcp_owner = MemoryPool<byte>.Shared.Rent(28))
            {
                var rtcpSenderReport = rtcp_owner.Memory.Slice(0, 28).Span;
                const bool hasPadding = false;
                const int reportCount = 0; // an empty report
                int length = (rtcpSenderReport.Length / 4) - 1; // num 32 bit words minus 1
                RTCPUtils.WriteRTCPHeader(rtcpSenderReport, RTCPUtils.RTCP_VERSION, hasPadding, reportCount, RTCPUtils.RTCP_PACKET_TYPE_SENDER_REPORT, length, connection.ssrc);
                RTCPUtils.WriteSenderReport(rtcpSenderReport, DateTime.UtcNow, rtp_timestamp, stream.rtp_packet_count, stream.octet_count);

                try
                {
                    Debug.Assert(stream.rtpChannel != null, "If stream.rtpChannel is null here the program did not handle well connection problem");
                    // Send to the IP address of the Client
                    // Send to the UDP Port the Client gave us in the SETUP command
                    stream.rtpChannel.WriteToControlPort(rtcpSenderReport);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error writing RTCP to listener {remoteAdress}", connection.Listener.RemoteAdress);
                    return false;
                }
                return true;

                // Clear the flag. A timer may set this to True again at some point to send regular Sender Reports
                //HACK  connection.must_send_rtcp_packet = false; // A Timer may set this to true again later in case it is used as a Keepalive (eg IndigoVision)
            }
        }

        private static (List<Memory<byte>>, List<IMemoryOwner<byte>>) PrepareVideoRtpPackets(List<byte[]> nal_array, uint rtp_timestamp, int videoPayloadType)
        {
            List<Memory<byte>> rtp_packets = new List<Memory<byte>>();
            List<IMemoryOwner<byte>> memoryOwners = new List<IMemoryOwner<byte>>();
            for (int x = 0; x < nal_array.Count; x++)
            {
                var raw_nal = nal_array[x];
                bool last_nal = false;
                if (x == nal_array.Count - 1)
                {
                    last_nal = true; // last NAL in our nal_array
                }

                // The H264 Payload could be sent as one large RTP packet (assuming the receiver can handle it)
                // or as a Fragmented Data, split over several RTP packets with the same Timestamp.
                bool fragmenting = false;

                int packetMTU = 1400; // 65535; 
                packetMTU += -8 - 20 - 16; // -8 for UDP header, -20 for IP header, -16 normal RTP header len. ** LESS RTP EXTENSIONS !!!

                if (raw_nal.Length > packetMTU) fragmenting = true;

                // INDIGO VISION DOES NOT SUPPORT FRAGMENTATION. Send as one jumbo RTP packet and let OS split over MTUs.
                // NOTE TO SELF... perhaps this was because the SDP did not have the extra packetization flag
                //  fragmenting = false;

                if (!fragmenting)
                {
                    // Put the whole NAL into one RTP packet.
                    // Note some receivers will have maximum buffers and be unable to handle large RTP packets.
                    // Also with RTP over RTSP there is a limit of 65535 bytes for the RTP packet.

                    // 12 is header size when there are no CSRCs or extensions
                    var owner = MemoryPool<byte>.Shared.Rent(12 + raw_nal.Length);
                    memoryOwners.Add(owner);
                    var rtp_packet = owner.Memory.Slice(0, 12 + raw_nal.Length);

                    // Create an single RTP fragment

                    // RTP Packet Header
                    // 0 - Version, P, X, CC, M, PT and Sequence Number
                    //32 - Timestamp. H264 uses a 90kHz clock
                    //64 - SSRC
                    //96 - CSRCs (optional)
                    //nn - Extension ID and Length
                    //nn - Extension header

                    const bool rtpPadding = false;
                    const bool rtpHasExtension = false;
                    const int rtp_csrc_count = 0;

                    RTPPacketUtil.WriteHeader(rtp_packet.Span,
                        RTPPacketUtil.RTP_VERSION,
                        rtpPadding,
                        rtpHasExtension, rtp_csrc_count, last_nal, videoPayloadType);

                    // sequence number and SSRC are set just before send

                    RTPPacketUtil.WriteTS(rtp_packet.Span, rtp_timestamp);

                    // Now append the raw NAL
                    raw_nal.CopyTo(rtp_packet.Slice(12));

                    rtp_packets.Add(rtp_packet);
                }
                else
                {
                    int data_remaining = raw_nal.Length;
                    int nal_pointer = 0;
                    int start_bit = 1;
                    int end_bit = 0;

                    // consume first byte of the raw_nal. It is used in the FU header
                    byte first_byte = raw_nal[0];
                    nal_pointer++;
                    data_remaining--;

                    while (data_remaining > 0)
                    {
                        int payload_size = Math.Min(packetMTU, data_remaining);
                        if (data_remaining == payload_size) end_bit = 1;

                        // 12 is header size. 2 bytes for FU-A header. Then payload
                        var destSize = 12 + 2 + payload_size;
                        var owner = MemoryPool<byte>.Shared.Rent(destSize);
                        memoryOwners.Add(owner);
                        var rtp_packet = owner.Memory.Slice(0, destSize);

                        // RTP Packet Header
                        // 0 - Version, P, X, CC, M, PT and Sequence Number
                        //32 - Timestamp. H264 uses a 90kHz clock
                        //64 - SSRC
                        //96 - CSRCs (optional)
                        //nn - Extension ID and Length
                        //nn - Extension header

                        const bool rtpPadding = false;
                        const bool rtpHasExtension = false;
                        const int rtp_csrc_count = 0;

                        RTPPacketUtil.WriteHeader(rtp_packet.Span, RTPPacketUtil.RTP_VERSION,
                            rtpPadding, rtpHasExtension, rtp_csrc_count, last_nal && end_bit == 1, videoPayloadType);

                        // sequence number and SSRC are set just before send
                        RTPPacketUtil.WriteTS(rtp_packet.Span, rtp_timestamp);

                        // Now append the Fragmentation Header (with Start and End marker) and part of the raw_nal
                        const byte f_bit = 0;
                        byte nri = (byte)(first_byte >> 5 & 0x03); // Part of the 1st byte of the Raw NAL (NAL Reference ID)
                        const byte type = 28; // FU-A Fragmentation

                        rtp_packet.Span[12] = (byte)((f_bit << 7) + (nri << 5) + type);
                        rtp_packet.Span[13] = (byte)((start_bit << 7) + (end_bit << 6) + (0 << 5) + (first_byte & 0x1F));

                        raw_nal.AsSpan(nal_pointer, payload_size).CopyTo(rtp_packet.Slice(14).Span);
                        nal_pointer += payload_size;
                        data_remaining -= payload_size;

                        rtp_packets.Add(rtp_packet);

                        start_bit = 0;
                    }
                }
            }

            return (rtp_packets, memoryOwners);
        }

        private void RemoveSession(RTSPConnection connection)
        {
            connection.play = false; // stop sending data
            connection.video.rtpChannel?.Dispose();
            connection.video.rtpChannel = null;
            connection.audio.rtpChannel?.Dispose();
            connection.Listener.Dispose();
            _connectionList.Remove(connection);
        }

        public void FeedInAACPacket(uint timestamp, byte[] aac_packet)
        {
            // append AU header (required for AAC)
            short frameLen = (short)(aac_packet.Length << 3);
            byte[] header = new byte[4];
            header[0] = 0x00;
            header[1] = 0x10; // 16 bits size of the header
            header[2] = (byte)((frameLen >> 8) & 0xFF);
            header[3] = (byte)(frameLen & 0xFF);
            FeedInAudioPacket(timestamp, header.Concat(aac_packet).ToArray(), AudioTrack.PayloadType);   
        }

        public void FeedInAudioPacket(uint timestamp, ReadOnlyMemory<byte> audio_packet, int audioPayloadType)
        {
            CheckTimeouts(out _, out int currentRtspPlayCount);

            // Console.WriteLine(current_rtsp_count + " RTSP clients connected. " + current_rtsp_play_count + " RTSP clients in PLAY mode");

            if (currentRtspPlayCount == 0) return;

            uint rtp_timestamp = timestamp;

            // Put the whole Audio Packet into one RTP packet.
            // 12 is header size when there are no CSRCs or extensions
            var size = 12 + audio_packet.Length;
            using (var owner = MemoryPool<byte>.Shared.Rent(size))
            {
                var rtp_packet = owner.Memory.Slice(0, size);
                // Create an single RTP fragment

                // RTP Packet Header
                // 0 - Version, P, X, CC, M, PT and Sequence Number
                //32 - Timestamp. H264 uses a 90kHz clock
                //64 - SSRC
                //96 - CSRCs (optional)
                //nn - Extension ID and Length
                //nn - Extension header

                const bool rtp_padding = false;
                const bool rtpHasExtension = false;
                int rtp_csrc_count = 0;
                const bool rtpMarker = true; // always 1 as this is the last (and only) RTP packet for this audio timestamp

                RTPPacketUtil.WriteHeader(rtp_packet.Span,
                    RTPPacketUtil.RTP_VERSION, rtp_padding, rtpHasExtension, rtp_csrc_count, rtpMarker, audioPayloadType);

                // sequence number is set just before send

                RTPPacketUtil.WriteTS(rtp_packet.Span, rtp_timestamp);

                // Now append the audio packet
                audio_packet.CopyTo(rtp_packet.Slice(12));

                // SEND THE RTSP PACKET
                lock (_connectionList)
                {
                    // Go through each RTSP connection and output the NAL on the Video Session
                    foreach (RTSPConnection connection in _connectionList.ToArray()) // ToArray makes a temp copy of the list.
                                                                                        // This lets us delete items in the foreach
                                                                                        // eg when there is Write Error
                    {
                        // Only process Sessions in Play Mode
                        if (!connection.play) continue;

                        // The client may have only subscribed to Video. Check if the client wants audio
                        if (connection.audio.rtpChannel is null) continue;

                        _logger.LogDebug("Sending audio session " + connection.session_id + " " + TransportLogName(connection.audio.rtpChannel) + " Timestamp(clock)=" + timestamp + 
                            ". RTP timestamp=" + rtp_timestamp + ". Sequence=" + connection.audio.sequenceNumber);
                        bool write_error = false;

                        if (connection.audio.must_send_rtcp_packet)
                        {
                            if (!SendRTCP(rtp_timestamp, connection, connection.audio))
                            {
                                RemoveSession(connection);
                            }
                        }

                        // There could be more than 1 RTP packet (if the data is fragmented)
                        {
                            // Add the specific data for each transmission
                            RTPPacketUtil.WriteSequenceNumber(rtp_packet.Span, connection.audio.sequenceNumber);
                            connection.audio.sequenceNumber++;

                            // Add the specific SSRC for each transmission
                            RTPPacketUtil.WriteSSRC(rtp_packet.Span, connection.ssrc);

                            try
                            {
                                // send the whole RTP packet
                                connection.audio.rtpChannel.WriteToDataPort(rtp_packet.Span);
                            }
                            catch (Exception e)
                            {
                                _logger.LogWarning(e, "UDP Write Exception");
                                _logger.LogWarning("Error writing to listener {address}", connection.Listener.RemoteAdress);
                                write_error = true;
                            }
                        }
                        if (write_error)
                        {
                            Console.WriteLine("Removing session " + connection.session_id + " due to write error");
                            RemoveSession(connection);
                        }

                        connection.audio.rtp_packet_count++;
                        connection.audio.octet_count += (uint)audio_packet.Length; // QUESTION - Do I need to include the RTP header bytes/fragmenting bytes
                    }
                }
            }
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
            }
        }

        #endregion // IDisposable

        // An RTPStream can be a Video Stream, Audio Stream or a MetaData Stream
        public class RTPStream
        {
            public int trackID;
            public bool must_send_rtcp_packet = false; // when true will send out a RTCP packet to match Wall Clock Time to RTP Payload timestamps
                                                       // 16 bit RTP packet sequence number used with this client connection
            public ushort sequenceNumber = 1;
            public IRtpTransport rtpChannel;        // Pair of UDP sockets (data and control) used when sending via UDP
            public DateTime time_since_last_rtcp_keepalive = DateTime.UtcNow; // Time since last RTCP message received - used to spot dead UDP clients
            public uint rtp_packet_count = 0;       // Used in the RTCP Sender Report to state how many RTP packets have been transmitted (for packet loss)
            public uint octet_count = 0;            // number of bytes of video that have been transmitted (for average bandwidth monitoring)
        }

        public class RTSPConnection
        {
            // The RTSP client connection
            public RtspListener Listener { get; set; }
            // set to true when Session is in Play mode
            public bool play;

            // Time since last RTSP message received - used to spot dead UDP clients
            public DateTime TimeSinceLastRtspKeepAlive { get; private set; } = DateTime.UtcNow;
            public uint ssrc = 0x12345678;             // SSRC value used with this client connection
                                                       // Client Hostname/IP Address
            public string session_id = "";             // RTSP Session ID used with this client connection

            public RTPStream video = new RTPStream();
            public RTPStream audio = new RTPStream();

            public void UpdateKeepAlive()
            {
                TimeSinceLastRtspKeepAlive = DateTime.UtcNow;
            }
        }
    }

    internal static class RTPPacketUtil
    {
        public const int RTP_VERSION = 2;

        public static void WriteHeader(Span<byte> rtp_packet, int rtp_version, bool rtp_padding, bool rtp_extension,
            int rtp_csrc_count, bool rtp_marker, int rtp_payload_type)
        {
            rtp_packet[0] = (byte)((rtp_version << 6) | ((rtp_padding ? 1 : 0) << 5) | ((rtp_extension ? 1 : 0) << 4) | rtp_csrc_count);
            rtp_packet[1] = (byte)(((rtp_marker ? 1 : 0) << 7) | (rtp_payload_type & 0x7F));
        }

        public static void WriteSequenceNumber(Span<byte> rtpPacket, ushort sequenceId)
        {
            BinaryPrimitives.WriteUInt16BigEndian(rtpPacket.Slice(2), sequenceId);
        }

        public static void WriteTS(Span<byte> rtp_packet, uint ts)
        {
            BinaryPrimitives.WriteUInt32BigEndian(rtp_packet.Slice(4), ts);
        }

        public static void WriteSSRC(Span<byte> rtp_packet, uint ssrc)
        {
            BinaryPrimitives.WriteUInt32BigEndian(rtp_packet.Slice(8), ssrc);
        }
    }

    internal static class RTCPUtils
    {
        public const int RTCP_VERSION = 2;
        public const int RTCP_PACKET_TYPE_SENDER_REPORT = 200;

        public static void WriteRTCPHeader(Span<byte> rtcp_sender_report, int version, bool hasPadding, int reportCount, int packetType, int length, uint ssrc)
        {
            rtcp_sender_report[0] = (byte)((version << 6) + ((hasPadding ? 1 : 0) << 5) + reportCount);
            rtcp_sender_report[1] = (byte)packetType;
            BinaryPrimitives.WriteUInt16BigEndian(rtcp_sender_report.Slice(2), (ushort)length);
            BinaryPrimitives.WriteUInt32BigEndian(rtcp_sender_report.Slice(4), ssrc);
        }

        public static void WriteSenderReport(Span<byte> rtcpSenderReport, DateTime now, uint rtp_timestamp, uint rtpPacketCount, uint octetCount)
        {
            // Bytes 8, 9, 10, 11 and 12,13,14,15 are the Wall Clock
            // Bytes 16,17,18,19 are the RTP payload timestamp

            // NTP Most Signigicant Word is relative to 0h, 1 Jan 1900
            // This will wrap around in 2036
            DateTime ntp_start_time = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            TimeSpan tmpTime = now - ntp_start_time;
            double totalSeconds = tmpTime.TotalSeconds; // Seconds and fractions of a second

            uint ntp_msw_seconds = (uint)Math.Truncate(totalSeconds); // whole number of seconds
            uint ntp_lsw_fractions = (uint)(totalSeconds % 1 * uint.MaxValue); // fractional part, scaled between 0 and MaxInt

            // cross check...   double ntp = ntp_msw_seconds + (ntp_lsw_fractions / UInt32.MaxValue);

            BinaryPrimitives.WriteUInt32BigEndian(rtcpSenderReport.Slice(8), ntp_msw_seconds);
            BinaryPrimitives.WriteUInt32BigEndian(rtcpSenderReport.Slice(12), ntp_lsw_fractions);
            BinaryPrimitives.WriteUInt32BigEndian(rtcpSenderReport.Slice(16), rtp_timestamp);
            BinaryPrimitives.WriteUInt32BigEndian(rtcpSenderReport.Slice(20), rtpPacketCount);
            BinaryPrimitives.WriteUInt32BigEndian(rtcpSenderReport.Slice(24), octetCount);
        }
    }

    public interface ITrack
    {
        int PayloadType { get; }
        bool IsReady { get; }
        StringBuilder BuildSDP(StringBuilder sdp);
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
            public CustomLoggerScope(/*CustomLogger logger,*/ TState state)
            {
                //_logger = logger;
                State = state;
            }

            //private readonly CustomLogger _logger;

            public TState State { get; }
            public void Dispose()
            { }
        }
        public IDisposable BeginScope<TState>(TState state)
        {
            return new CustomLoggerScope<TState>(/*this,*/ state);
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
}
