using Microsoft.Extensions.Logging;
using Rtsp;
using Rtsp.Messages;
using Rtsp.Onvif;
using Rtsp.Rtp;
using Rtsp.Sdp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace SharpRTSPClient
{
    /// <summary>
    /// RTSP client.
    /// </summary>
    public class RTSPClient : IDisposable
    {
        private static Random _rand = new Random();

        private readonly ILogger _logger;
        private readonly ILoggerFactory _loggerFactory;

        // Events that applications can receive
        public event EventHandler<NewStreamEventArgs> NewVideoStream;
        public event EventHandler<NewStreamEventArgs> NewAudioStream;
        public event EventHandler<SimpleDataEventArgs> ReceivedVideoData;
        public event EventHandler<SimpleDataEventArgs> ReceivedAudioData;

        public bool ProcessRTCP { get; set; } = true; // answer RTCP
        public event Received_RTCP_Delegate RawRtcpMessageReceived;
        public delegate void Received_RTCP_Delegate(byte[] rtcp, bool isVideo);

        public bool ProcessRTP { get; set; } = true;
        public event Received_RTP_Delegate RawRtpMessageReceived;
        public delegate void Received_RTP_Delegate(byte[] rtp, bool isVideo);

        public bool AutoPlay { get; set; } = true;

        public enum RTP_TRANSPORT { UDP, TCP, MULTICAST };
        public enum MEDIA_REQUEST { VIDEO_ONLY, AUDIO_ONLY, VIDEO_AND_AUDIO };
        private enum RTSP_STATUS { WaitingToConnect, Connecting, ConnectFailed, Connected };

        IRtspTransport _rtspSocket; // RTSP connection
        RTSP_STATUS _rtspSocketStatus = RTSP_STATUS.WaitingToConnect;
        // this wraps around a the RTSP tcp_socket stream
        RtspListener _rtspClient;
        RTP_TRANSPORT _rtpTransport = RTP_TRANSPORT.UDP; // Mode, either RTP over UDP or RTP over TCP using the RTSP socket
        // Communication for the RTP (video and audio) 
        IRtpTransport _videoRtpTransport;
        IRtpTransport _audioRtpTransport;

        Uri _uri;                         // RTSP URI (username & password will be stripped out
        string _session = "";             // RTSP Session
        private Authentication _authentication;
        private NetworkCredential _credentials = new NetworkCredential();
        bool _clientWantsVideo = false; // Client wants to receive Video
        bool _clientWantsAudio = false; // Client wants to receive Audio

        Uri _video_uri = null;            // URI used for the Video Track
        int _video_payload = -1;          // Payload Type for the Video. (often 96 which is the first dynamic payload value. Bosch use 35)

        Uri _audio_uri = null;            // URI used for the Audio Track
        int _audio_payload = -1;          // Payload Type for the Video. (often 96 which is the first dynamic payload value)
        string _audio_codec = "";         // Codec used with Payload Types (eg "PCMA" or "AMR")

        /// <summary>
        /// If true, the client must send an "onvif-replay" header on every play request.
        /// </summary>
        bool _playbackSession = false;

        // Used with RTSP keepalive
        bool _serverSupportsGetParameter = false;
        System.Timers.Timer _keepaliveTimer = null;

        IPayloadProcessor _videoPayloadProcessor = null;
        IPayloadProcessor _audioPayloadProcessor = null;
        private bool disposedValue;

        // setup messages still to send
        readonly Queue<RtspRequestSetup> _setupMessages = new Queue<RtspRequestSetup>();

        /// <summary>
        /// Called when the Setup command are completed, so we can start the right Play message (with or without playback informations)
        /// </summary>
        public event EventHandler SetupMessageCompleted;
        
        /// <summary>
        /// Video SSRC.
        /// </summary>
        public uint VideoSSRC { get; set; } = (uint)_rand.Next(10000, 19999);

        /// <summary>
        /// Audio SSRC.
        /// </summary>
        public uint AudioSSRC { get; set; } = (uint)_rand.Next(20000, 29999);

        // Constructor
        public RTSPClient() : this(new CustomLoggerFactory())
        { }

        public RTSPClient(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<RTSPClient>();
            _loggerFactory = loggerFactory;
        }

        public void Connect(string url, RTP_TRANSPORT rtpTransport, string username = null, string password = null, MEDIA_REQUEST mediaRequest = MEDIA_REQUEST.VIDEO_AND_AUDIO, bool playbackSession = false)
        {
            RtspUtils.RegisterUri();

            _logger.LogDebug("Connecting to {url} ", url);
            _uri = new Uri(url);

            _playbackSession = playbackSession;

            // Use URI to extract username and password
            // and to make a new URL without the username and password
            var hostname = _uri.Host;
            var port = _uri.Port;
            try
            {
                if (_uri.UserInfo.Length > 0)
                {
                    _credentials = new NetworkCredential(_uri.UserInfo.Split(':')[0], _uri.UserInfo.Split(':')[1]);
                    _uri = new Uri(_uri.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.UserInfo,
                                                 UriFormat.UriEscaped));
                }
                else
                {
                    _credentials = new NetworkCredential(username, password);
                }
            }
            catch
            {
                _credentials = new NetworkCredential();
            }

            // We can ask the RTSP server for Video, Audio or both. If we don't want audio we don't need to SETUP the audio channal or receive it
            _clientWantsVideo = (mediaRequest is MEDIA_REQUEST.VIDEO_ONLY || mediaRequest is MEDIA_REQUEST.VIDEO_AND_AUDIO);
            _clientWantsAudio = (mediaRequest is MEDIA_REQUEST.AUDIO_ONLY || mediaRequest is MEDIA_REQUEST.VIDEO_AND_AUDIO);

            // Connect to a RTSP Server. The RTSP session is a TCP connection
            _rtspSocketStatus = RTSP_STATUS.Connecting;
            try
            {
                if (_uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.InvariantCultureIgnoreCase))
                {
                    _rtspSocket = new RtspHttpTransport(_uri, _credentials);
                }
                else
                {
                    _rtspSocket = new RtspTcpTransport(_uri);
                }
            }
            catch
            {
                _rtspSocketStatus = RTSP_STATUS.ConnectFailed;
                _logger.LogWarning("Error - did not connect");
                return;
            }

            if (!_rtspSocket.Connected)
            {
                _rtspSocketStatus = RTSP_STATUS.ConnectFailed;
                _logger.LogWarning("Error - did not connect");
                return;
            }

            _rtspSocketStatus = RTSP_STATUS.Connected;

            // Connect a RTSP Listener to the RTSP Socket (or other Stream) to send RTSP messages and listen for RTSP replies
            _rtspClient = new RtspListener(_rtspSocket, _loggerFactory.CreateLogger<RtspListener>())
            {
                AutoReconnect = false
            };

            _rtspClient.MessageReceived += RtspMessageReceived;
            _rtspClient.Start(); // start listening for messages from the server (messages fire the MessageReceived event)

            // Check the RTP Transport
            // If the RTP transport is TCP then we interleave the RTP packets in the RTSP stream
            // If the RTP transport is UDP, we initialise two UDP sockets (one for video, one for RTCP status messages)
            // If the RTP transport is MULTICAST, we have to wait for the SETUP message to get the Multicast Address from the RTSP server
            this._rtpTransport = rtpTransport;
            if (rtpTransport == RTP_TRANSPORT.UDP)
            {
                _videoRtpTransport = new UDPSocket(50000, 51000); // give a range of 500 pairs (1000 addresses) to try incase some address are in use
                _audioRtpTransport = new UDPSocket(50000, 51000); // give a range of 500 pairs (1000 addresses) to try incase some address are in use
            }
            if (rtpTransport == RTP_TRANSPORT.TCP)
            {
                int nextFreeRtpChannel = 0;
                _videoRtpTransport = new RtpTcpTransport(_rtspClient)
                {
                    DataChannel = nextFreeRtpChannel++,
                    ControlChannel = nextFreeRtpChannel++,
                };
                _audioRtpTransport = new RtpTcpTransport(_rtspClient)
                {
                    DataChannel = nextFreeRtpChannel++,
                    ControlChannel = nextFreeRtpChannel++,
                };
            }
            if (rtpTransport == RTP_TRANSPORT.MULTICAST)
            {
                // Nothing to do. Will open Multicast UDP sockets after the SETUP command
            }

            // Send OPTIONS
            // In the Received Message handler we will send DESCRIBE, SETUP and PLAY
            RtspRequest options_message = new RtspRequestOptions
            {
                RtspUri = _uri
            };
            _rtspClient.SendMessage(options_message);
        }

        // return true if this connection failed, or if it connected but is no longer connected.
        public bool StreamingFinished() 
        {

            switch(_rtspSocketStatus)
        {
                case RTSP_STATUS.ConnectFailed:
                    return true;
                 case RTSP_STATUS.Connected:
                    return !(_rtspSocket?.Connected ?? false);
                default:
                    return false;
        }
    }

        public void Pause()
        {
            if (_rtspSocket is null || _uri is null)
            {
                throw new InvalidOperationException("Not connected");
            }

            // Send PAUSE
            RtspRequest pause_message = new RtspRequestPause
            {
                RtspUri = _uri,
                Session = _session
            };
            pause_message.AddAuthorization(_authentication, _uri, _rtspSocket.NextCommandIndex());
            _rtspClient?.SendMessage(pause_message);
        }

        public void Play()
        {
            if (_rtspSocket is null || _uri is null)
            {
                throw new InvalidOperationException("Not connected");
            }

            // Send PLAY
            var playMessage = new RtspRequestPlay
            {
                RtspUri = _uri,
                Session = _session
            };
            playMessage.AddAuthorization(_authentication, _uri, _rtspSocket.NextCommandIndex());

            //// Need for old sony camera SNC-CS20
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
        /// <param name="speed">Speed information (1.0 means normal speed, -1.0 backward speed), other values >1.0 and <-1.0 allow a different speed</param>
        public void Play(DateTime seekTime, double speed = 1.0)
        {
            if (_rtspSocket is null || _uri is null) { throw new InvalidOperationException("Not connected"); }
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
        /// <param name="speed">Speed information (1.0 means normal speed, -1.0 backward speed), other values >1.0 and <-1.0 allow a different speed</param>
        /// <exception cref="InvalidOperationException"></exception>
        public void Play(DateTime seekTimeFrom, DateTime seekTimeTo, double speed = 1.0)
        {
            if (_rtspSocket is null || _uri is null) { throw new InvalidOperationException("Not connected"); }
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

        public void Stop()
        {
            // Send TEARDOWN
            RtspRequest teardown_message = new RtspRequestTeardown
            {
                RtspUri = _uri,
                Session = _session
            };
            teardown_message.AddAuthorization(_authentication, _uri, _rtspSocket?.NextCommandIndex() ?? 0);
            _rtspClient?.SendMessage(teardown_message);

            // Stop the keepalive timer
            _keepaliveTimer?.Stop();

            // clear up any UDP sockets
            _videoRtpTransport?.Stop();
            _audioRtpTransport?.Stop();

            // Drop the RTSP session
            _rtspClient?.Stop();
        }

        // A Video RTP packet has been received.
        public void VideoRtpDataReceived(object sender, RtspDataEventArgs e)
        {
            if (e.Data.Data.IsEmpty)
                return;

            using (var data = e.Data)
            {
                var rtpPacket = new RtpPacket(data.Data.Span);

                if (rtpPacket.PayloadType != _video_payload)
                {
                    // Check the payload type in the RTP packet matches the Payload Type value from the SDP
                    _logger.LogDebug("Ignoring this Video RTP payload");
                    return; // ignore this data
                }

                RawRtpMessageReceived?.Invoke(data.Data.ToArray(), true);

                if (!ProcessRTP)
                {
                    return;
                }

                if (_videoPayloadProcessor is null)
                {
                    _logger.LogWarning("No video Processor");
                    return;
                }

                using (RawMediaFrame nal_units = _videoPayloadProcessor.ProcessPacket(rtpPacket)) // this will cache the Packets until there is a Frame
                {
                    if (nal_units.Any())
                    {
                        ReceivedVideoData?.Invoke(this, new SimpleDataEventArgs(nal_units.Data, nal_units.ClockTimestamp));
                    }
                }
            }
        }

        // RTP packet (or RTCP packet) has been received.
        public void AudioRtpDataReceived(object sender, RtspDataEventArgs e)
        {
            if (e.Data.Data.IsEmpty)
                return;

            using (var data = e.Data)
            {
                // Received some Audio Data on the correct channel.
                var rtpPacket = new RtpPacket(data.Data.Span);

                // Check the payload type in the RTP packet matches the Payload Type value from the SDP
                if (rtpPacket.PayloadType != _audio_payload)
                {
                    _logger.LogDebug("Ignoring this Audio RTP payload");
                    return; // ignore this data
                }

                RawRtpMessageReceived?.Invoke(data.Data.ToArray(), false);

                if (!ProcessRTP)
                {
                    return;
                }

                if (_audioPayloadProcessor is null)
                {
                    _logger.LogWarning("No parser for RTP payload {audioPayload}", _audio_payload);
                    return;
                }

                using (var audioFrames = _audioPayloadProcessor.ProcessPacket(rtpPacket))
                {
                    if (audioFrames.Any())
                    {
                        ReceivedAudioData?.Invoke(this, new SimpleDataEventArgs(audioFrames.Data, audioFrames.ClockTimestamp));
                    }
                }
            }
        }

        // RTCP packet has been received.
        public void VideoRtcpControlDataReceived(object sender, RtspDataEventArgs e)
        {
            if (e.Data.Data.IsEmpty)
                return;

            _logger.LogDebug("Received a RTCP message");

            using (var data = e.Data)
            {
                RawRtcpMessageReceived?.Invoke(data.Data.ToArray(), true);

                if (!ProcessRTCP)
                    return;

                var reports = ParseRTCPAndGenerateReponse(data, VideoSSRC);
                foreach (var report in reports)
                {
                    ((IRtpTransport)sender).WriteToControlPort(report);
                }
            }
        }

        public void AudioRtcpControlDataReceived(object sender, RtspDataEventArgs e)
        {
            if (e.Data.Data.IsEmpty)
                return;

            _logger.LogDebug("Received a RTCP message");

            using (var data = e.Data)
            {
                RawRtcpMessageReceived?.Invoke(data.Data.ToArray(), true);

                if (!ProcessRTCP)
                    return;

                var reports = ParseRTCPAndGenerateReponse(data, AudioSSRC);
                foreach (var report in reports)
                {
                    ((IRtpTransport)sender).WriteToControlPort(report);
                }
            }
        }

        private List<byte[]> ParseRTCPAndGenerateReponse(RtspData data, uint ssrc)
        {
            List<byte[]> reports = new List<byte[]>();

            // RTCP Packet
            // - Version, Padding and Receiver Report Count
            // - Packet Type
            // - Length
            // - SSRC
            // - payload

            // There can be multiple RTCP packets transmitted together. Loop ever each one

            int packetIndex = 0;
            var span = data.Data.Span;
            while (packetIndex < data.Data.Length)
            {
                int rtcp_version = (span[packetIndex + 0] >> 6);
                int rtcp_padding = (span[packetIndex + 0] >> 5) & 0x01;
                int rtcp_reception_report_count = (span[packetIndex + 0] & 0x1F);
                byte rtcp_packet_type = span[packetIndex + 1]; // Values from 200 to 207
                int rtcp_length = (int)(span[packetIndex + 2] << 8) + (int)(span[packetIndex + 3]); // number of 32 bit words
                uint rtcp_ssrc = (uint)(span[packetIndex + 4] << 24) + (uint)(span[packetIndex + 5] << 16)
                    + (uint)(span[packetIndex + 6] << 8) + span[packetIndex + 7];

                // 200 = SR = Sender Report
                // 201 = RR = Receiver Report
                // 202 = SDES = Source Description
                // 203 = Bye = Goodbye
                // 204 = APP = Application Specific Method
                // 207 = XR = Extended Reports

                _logger.LogDebug("RTCP Data. PacketType={rtcp_packet_type} SSRC={ssrc}", rtcp_packet_type, rtcp_ssrc);

                if (rtcp_packet_type == 200)
                {
                    // We have received a Sender Report
                    // Use it to convert the RTP timestamp into the UTC time

                    UInt32 ntp_msw_seconds = (uint)(span[packetIndex + 8] << 24) + (uint)(span[packetIndex + 9] << 16)
                    + (uint)(span[packetIndex + 10] << 8) + span[packetIndex + 11];

                    UInt32 ntp_lsw_fractions = (uint)(span[packetIndex + 12] << 24) + (uint)(span[packetIndex + 13] << 16)
                    + (uint)(span[packetIndex + 14] << 8) + span[packetIndex + 15];

                    UInt32 rtp_timestamp = (uint)(span[packetIndex + 16] << 24) + (uint)(span[packetIndex + 17] << 16)
                    + (uint)(span[packetIndex + 18] << 8) + span[packetIndex + 19];

                    double ntp = ntp_msw_seconds + (ntp_lsw_fractions / UInt32.MaxValue);

                    // NTP Most Signigicant Word is relative to 0h, 1 Jan 1900
                    // This will wrap around in 2036
                    var time = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

                    time = time.AddSeconds((double)ntp_msw_seconds); // adds 'double' (whole&fraction)

                    _logger.LogDebug("RTCP time (UTC) for RTP timestamp {timestamp} is {time}", rtp_timestamp, time);

                    // Send a Receiver Report
                    try
                    {
                        byte[] rtcp_receiver_report = new byte[8];
                        int version = 2;
                        int paddingBit = 0;
                        int reportCount = 0; // an empty report
                        int packetType = 201; // Receiver Report
                        int length = (rtcp_receiver_report.Length / 4) - 1; // num 32 bit words minus 1
                        rtcp_receiver_report[0] = (byte)((version << 6) + (paddingBit << 5) + reportCount);
                        rtcp_receiver_report[1] = (byte)(packetType);
                        rtcp_receiver_report[2] = (byte)((length >> 8) & 0xFF);
                        rtcp_receiver_report[3] = (byte)((length >> 0) & 0XFF);
                        rtcp_receiver_report[4] = (byte)((ssrc >> 24) & 0xFF);
                        rtcp_receiver_report[5] = (byte)((ssrc >> 16) & 0xFF);
                        rtcp_receiver_report[6] = (byte)((ssrc >> 8) & 0xFF);
                        rtcp_receiver_report[7] = (byte)((ssrc >> 0) & 0xFF);

                        reports.Add(rtcp_receiver_report);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug($"Error writing RTCP packet: {ex.Message}.");
                    }
                }

                packetIndex += (rtcp_length + 1) * 4;
            }

            return reports;
        }

        // RTSP Messages are OPTIONS, DESCRIBE, SETUP, PLAY etc
        private void RtspMessageReceived(object sender, RtspChunkEventArgs e)
        {
            if (!(e.Message is RtspResponse message))
                return;

            _logger.LogDebug("Received RTSP response to message {originalRequest}", message.OriginalRequest);

            // If message has a 401 - Unauthorized Error, then we re-send the message with Authorization
            // using the most recently received 'realm' and 'nonce'
            if (!message.IsOk)
            {
                _logger.LogDebug("Got Error in RTSP Reply {returnCode} {returnMessage}", message.ReturnCode, message.ReturnMessage);

                if (message.ReturnCode == 401 && message.OriginalRequest?.Headers.ContainsKey(RtspHeaderNames.Authorization) == true)
                {
                    // the authorization failed.
                    _logger.LogError("Fail to authenticate stoping here");
                    Stop();
                    return;
                }

                // Check if the Reply has an Authenticate header.
                if (message.ReturnCode == 401 && message.Headers.TryGetValue(RtspHeaderNames.WWWAuthenticate, out string value))
                {
                    // Process the WWW-Authenticate header
                    // EG:   Basic realm="AProxy"
                    // EG:   Digest realm="AXIS_WS_ACCC8E3A0A8F", nonce="000057c3Y810622bff50b36005eb5efeae118626a161bf", stale=FALSE
                    // EG:   Digest realm="IP Camera(21388)", nonce="534407f373af1bdff561b7b4da295354", stale="FALSE"

                    string www_authenticate = value ?? string.Empty;
                    _authentication = Authentication.Create(_credentials, www_authenticate);
                    _logger.LogDebug("WWW Authorize parsed for {authentication}", _authentication);
                }

                RtspMessage resend_message = message.OriginalRequest?.Clone() as RtspMessage;

                if (resend_message != null)
                {
                    resend_message.AddAuthorization(_authentication, _uri, _rtspSocket.NextCommandIndex());
                    _rtspClient?.SendMessage(resend_message);
                }

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
                    RtspRequest describe_message = new RtspRequestDescribe
                    {
                        RtspUri = _uri,
                        Headers = { { "Accept", "application/sdp" } },
                    };
                    describe_message.AddAuthorization(_authentication, _uri, _rtspSocket.NextCommandIndex());
                    _rtspClient?.SendMessage(describe_message);
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

                bool isVideoChannel = message.OriginalRequest.RtspUri == _video_uri;
                bool isAudioChannel = message.OriginalRequest.RtspUri == _audio_uri;
                Debug.Assert(isVideoChannel || isAudioChannel, "Unknown channel response");

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
                            && videoRtcpChannel.HasValue)
                        {
                            // Create the Pair of UDP Sockets in Multicast mode
                            if (isVideoChannel)
                            {
                                _videoRtpTransport = new MulticastUDPSocket(multicastAddress, videoDataChannel.Value, multicastAddress, videoRtcpChannel.Value);

                            }
                            else if (isAudioChannel)
                            {
                                _audioRtpTransport = new MulticastUDPSocket(multicastAddress, videoDataChannel.Value, multicastAddress, videoRtcpChannel.Value);
                            }
                        }
                    }

                    // check if the requested Interleaved channels have been modified by the camera
                    // in the SETUP Reply (Panasonic have a camera that does this)
                    if (transport.LowerTransport == RtspTransport.LowerTransportType.TCP)
                    {
                        RtpTcpTransport tcpTransport = null;
                        if (isVideoChannel)
                        {
                            tcpTransport = _videoRtpTransport as RtpTcpTransport;
                        }

                        if (isAudioChannel)
                        {
                            tcpTransport = _audioRtpTransport as RtpTcpTransport;
                        }
                        if (tcpTransport != null)
                        {
                            tcpTransport.DataChannel = transport.Interleaved?.First ?? tcpTransport.DataChannel;
                            tcpTransport.ControlChannel = transport.Interleaved?.Second ?? tcpTransport.ControlChannel;
                        }
                    }

                    if (isVideoChannel && _videoRtpTransport != null)
                    {
                        _videoRtpTransport.DataReceived += VideoRtpDataReceived;
                        _videoRtpTransport.ControlReceived += VideoRtcpControlDataReceived;
                        _videoRtpTransport.Start();
                    }

                    if (isAudioChannel && _audioRtpTransport != null)
                    {
                        _audioRtpTransport.DataReceived += AudioRtpDataReceived;
                        _audioRtpTransport.ControlReceived += AudioRtcpControlDataReceived;
                        _audioRtpTransport.Start();
                    }
                }

                // Check if we have another SETUP command to send, then remote it from the list
                if (_setupMessages.Count > 0)
                {
                    // send the next SETUP message, after adding in the 'session'
                    RtspRequestSetup next_setup = _setupMessages.Dequeue();
                    next_setup.Session = _session;
                    _rtspClient?.SendMessage(next_setup);
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
            _logger.LogDebug("Sdp:\n{sdp}", Encoding.UTF8.GetString(message.Data.Span.ToArray()));

            SdpFile sdp_data;
            using(var ms = new MemoryStream(message.Data.Span.ToArray()))
            using (StreamReader sdp_stream = new StreamReader(ms))
            {
                sdp_data = SdpFile.ReadLoose(sdp_stream);
            }

            // For old sony cameras, we need to use the control uri from the sdp
            var customControlUri = sdp_data.Attributs.FirstOrDefault(x => x.Key == "control");
            if (customControlUri != null && !string.Equals(customControlUri.Value, "*"))
            {
                _uri = new Uri(_uri, customControlUri.Value);
            }

            // Process each 'Media' Attribute in the SDP (each sub-stream)
            // to look for first supported video substream
            if (_clientWantsVideo)
            {
                foreach (Media media in sdp_data.Medias.Where(m => m.MediaType == Media.MediaTypes.video))
                {
                    // search the attributes for control, rtpmap and fmtp
                    // holds SPS and PPS in base64 (h264 video)
                    AttributFmtp fmtp = media.Attributs.FirstOrDefault(x => x.Key == "fmtp") as AttributFmtp;
                    AttributRtpMap rtpmap = media.Attributs.FirstOrDefault(x => x.Key == "rtpmap") as AttributRtpMap;
                    _video_uri = GetControlUri(media);

                    int fmtpPayloadNumber = -1;
                    if (fmtp != null)
                    {
                        fmtpPayloadNumber = fmtp.PayloadNumber;
                    }

                    // extract h265 donl if available...
                    bool h265HasDonl = false;

                    if ((rtpmap?.EncodingName?.ToUpper().Equals("H265") ?? false) && !string.IsNullOrEmpty(fmtp?.FormatParameter))
                    {
                        var param = H265Parameters.Parse(fmtp.FormatParameter);
                        if (param.ContainsKey("sprop-max-don-diff") && int.TryParse(param["sprop-max-don-diff"], out int donl) && donl > 0)
                        {
                            h265HasDonl = true;
                        }
                    }

                    // some cameras are really mess with the payload type.
                    // must check also the rtpmap for the corrent format to load (sending an h265 payload when giving an h264 stream [Some Bosch camera])

                    string payloadName = string.Empty;
                    if (rtpmap != null
                        && (((fmtpPayloadNumber > -1 && rtpmap.PayloadNumber == fmtpPayloadNumber) || fmtpPayloadNumber == -1)
                        && rtpmap.EncodingName != null))
                    {

                        // found a valid codec
                        payloadName = rtpmap.EncodingName.ToUpper();
                        switch(payloadName)
                        {
                            case "H264":
                                _videoPayloadProcessor = new H264Payload(_loggerFactory.CreateLogger<H264Payload>());
                                break;
                            case "H265":
                                _videoPayloadProcessor = new H265Payload(h265HasDonl, _loggerFactory.CreateLogger<H265Payload>());
                                break;
                            case "JPEG":
                                _videoPayloadProcessor = new JPEGPayload();
                                break;
                            case "MP4V-ES":
                                _videoPayloadProcessor = new RawPayload();
                                break;
                            default:
                                _videoPayloadProcessor = null;
                                break;
                        };
                        _video_payload = media.PayloadType;
                    }
                    else
                    {
                        _video_payload = media.PayloadType;
                        if (media.PayloadType < 96)
                        {
                            // PayloadType is a static value, so we can use it to determine the codec
                            switch(media.PayloadType)
                            {
                                case 26:
                                    _videoPayloadProcessor = new JPEGPayload();
                                    break;
                                case 33:
                                    _videoPayloadProcessor = new MP2TransportPayload();
                                    break;
                                default:
                                    _videoPayloadProcessor = null;
                                    break;
                            };
                            
                            switch(media.PayloadType)
                            {
                                case 26:
                                    payloadName = "JPEG";
                                    break;
                                case 33:
                                    payloadName = "MP2T";
                                    break;
                                default:
                                    payloadName = string.Empty;
                                    break;
                            };
                        }
                    }

                    IStreamConfigurationData streamConfigurationData = null;

                    if (_videoPayloadProcessor is H264Payload && fmtp?.FormatParameter != null)
                    {
                        // If the rtpmap contains H264 then split the fmtp to get the sprop-parameter-sets which hold the SPS and PPS in base64
                        var param = H264Parameters.Parse(fmtp.FormatParameter);
                        var sps_pps = param.SpropParameterSets;
                        if (sps_pps.Count >= 2)
                        {
                            byte[] sps = sps_pps[0];
                            byte[] pps = sps_pps[1];
                            streamConfigurationData = new H264StreamConfigurationData() { SPS = sps, PPS = pps };
                        }
                    }
                    else if (_videoPayloadProcessor is H265Payload && fmtp?.FormatParameter != null)
                    {
                        // If the rtpmap contains H265 then split the fmtp to get the sprop-vps, sprop-sps and sprop-pps
                        // The RFC makes the VPS, SPS and PPS OPTIONAL so they may not be present. In which we pass back NULL values
                        var param = H265Parameters.Parse(fmtp.FormatParameter);
                        var vps_sps_pps = param.SpropParameterSets;
                        if (vps_sps_pps.Count >= 3)
                        {
                            byte[] vps = vps_sps_pps[0];
                            byte[] sps = vps_sps_pps[1];
                            byte[] pps = vps_sps_pps[2];
                            streamConfigurationData = new H265StreamConfigurationData() { VPS = vps, SPS = sps, PPS = pps };
                        }
                    }

                    // Send the SETUP RTSP command if we have a matching Payload Decoder
                    if (_videoPayloadProcessor != null)
                    {
                        RtspTransport transport = CalculateTransport(_videoRtpTransport);

                        // Generate SETUP messages
                        if (transport != null)
                        {
                            RtspRequestSetup setup_message = new RtspRequestSetup()
                            {
                                RtspUri = _video_uri
                            };
                            setup_message.AddTransport(transport);
                            setup_message.AddAuthorization(_authentication, _uri, _rtspSocket.NextCommandIndex());
                            if (_playbackSession) { setup_message.AddRequireOnvifRequest(); }
                            // Add SETUP message to list of mesages to send
                            _setupMessages.Enqueue(setup_message);

                            NewVideoStream?.Invoke(this, new NewStreamEventArgs(payloadName, streamConfigurationData));
                        }
                        break;
                    }
                }
            }

            if (_clientWantsAudio)
            {
                foreach (Media media in sdp_data.Medias.Where(m => m.MediaType == Media.MediaTypes.audio))
                {
                    // search the attributes for control, rtpmap and fmtp
                    AttributFmtp fmtp = media.Attributs.FirstOrDefault(x => x.Key == "fmtp") as AttributFmtp;
                    AttributRtpMap rtpmap = media.Attributs.FirstOrDefault(x => x.Key == "rtpmap") as AttributRtpMap;

                    _audio_uri = GetControlUri(media);
                    _audio_payload = media.PayloadType;

                    IStreamConfigurationData streamConfigurationData = null;
                    if (media.PayloadType < 96)
                    {
                        // fixed payload type
                            switch(media.PayloadType)
                        {
                            case 0:
                                (_audioPayloadProcessor, _audio_codec) = (new G711Payload(), "PCMU");
                                break;
                            case 8:
                                (_audioPayloadProcessor, _audio_codec) = (new G711Payload(), "PCMA");
                                break;
                            default:
                                (_audioPayloadProcessor, _audio_codec) = (null, "");
                                break;
                        };
                    }
                    else
                    {
                        // dynamic payload type
                        _audio_codec = rtpmap?.EncodingName?.ToUpper() ?? string.Empty;
                        switch(_audio_codec)
                        {
                            // Create AAC RTP Parser
                            // Example fmtp is "96 profile-level-id=1;mode=AAC-hbr;sizelength=13;indexlength=3;indexdeltalength=3;config=1490"
                            // Example fmtp is ""96 streamtype=5;profile-level-id=1;mode=AAC-hbr;sizelength=13;indexlength=3;indexdeltalength=3;config=1210"
                            case "MPEG4-GENERIC":
                                {
                                    if (fmtp?["mode"].ToLower() == "aac-hbr")
                                        _audioPayloadProcessor = new AACPayload(fmtp["config"]);
                                }
                                break;
                            case "PCMA":
                                _audioPayloadProcessor = new G711Payload();
                                break;
                            case "PCMU":
                                _audioPayloadProcessor = new G711Payload();
                                break;
                            case "AMR":
                                _audioPayloadProcessor = new AMRPayload();
                                break;
                            default:
                                _audioPayloadProcessor = null;
                                break;
                        };
                        if (_audioPayloadProcessor is AACPayload aacPayloadProcessor)
                        {
                            _audio_codec = "AAC";
                            streamConfigurationData = new AacStreamConfigurationData()
                            {
                                ObjectType = aacPayloadProcessor.ObjectType,
                                FrequencyIndex = aacPayloadProcessor.FrequencyIndex,
                                SamplingFrequency = aacPayloadProcessor.SamplingFrequency,
                                ChannelConfiguration = aacPayloadProcessor.ChannelConfiguration
                            };
                        }
                    }

                    // Send the SETUP RTSP command if we have a matching Payload Decoder
                    if (_audioPayloadProcessor != null)
                    {
                        RtspTransport transport = CalculateTransport(_audioRtpTransport);

                        // Generate SETUP messages
                        if (transport != null)
                        {
                            RtspRequestSetup setup_message = new RtspRequestSetup()
                            {
                                RtspUri = _audio_uri,
                            };
                            setup_message.AddTransport(transport);
                            setup_message.AddAuthorization(_authentication, _uri, _rtspSocket.NextCommandIndex());
                            if (_playbackSession)
                            {
                                setup_message.AddRequireOnvifRequest();
                                setup_message.AddRateControlOnvifRequest(false);
                            }
                            // Add SETUP message to list of mesages to send
                            _setupMessages.Enqueue(setup_message);
                            NewAudioStream?.Invoke(this, new NewStreamEventArgs(_audio_codec, streamConfigurationData));
                        }
                        break;
                    }
                }
            }

            if (_setupMessages.Count == 0)
            {
                // No SETUP messages were generated
                // So we cannot continue
                throw new ApplicationException("Unable to setup media stream");
            }

            // Send the FIRST SETUP message and remove it from the list of Setup Messages
            _rtspClient?.SendMessage(_setupMessages.Dequeue());
        }

        private Uri GetControlUri(Media media)
        {
            Uri controlUri = null;
            var attrib = media.Attributs.FirstOrDefault(a => a.Key == "control");
            if (attrib != null)
            {
                string sdp_control = attrib.Value;

                if (sdp_control.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)
                    || sdp_control.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    // the "track" or "stream id"
                    string control = sdp_control; //absolute path
                    controlUri = new Uri(control);
                }
                else
                {
                    // add trailing / if necessary
                    var baseUriWithTrailingSlash = _uri.ToString().EndsWith("/") ? _uri : new Uri($"{_uri}/");
                    // relative path
                    controlUri = new Uri(baseUriWithTrailingSlash, sdp_control);
                }
            }
            return controlUri;
        }

        private RtspTransport CalculateTransport(IRtpTransport transport)
        {
            switch(_rtpTransport)
            {
                // Server interleaves the RTP packets over the RTSP connection
                // Example for TCP mode (RTP over RTSP)   Transport: RTP/AVP/TCP;interleaved=0-1
                case RTP_TRANSPORT.TCP:
                    return new RtspTransport()
                    {
                        LowerTransport = RtspTransport.LowerTransportType.TCP,
                        // Eg Channel 0 for RTP video data. Channel 1 for RTCP status reports
                        Interleaved = (transport as RtpTcpTransport)?.Channels ?? throw new ApplicationException("TCP transport asked and no tcp channel allocated"),
                    };
                case RTP_TRANSPORT.UDP:
                    return new RtspTransport()
                    {
                        LowerTransport = RtspTransport.LowerTransportType.UDP,
                        IsMulticast = false,
                        ClientPort = (transport as UDPSocket)?.Ports ?? throw new ApplicationException("UDP transport asked and no udp port allocated"),
                    };
                // Server sends the RTP packets to a Pair of UDP ports (one for data, one for rtcp control messages)
                // using Multicast Address and Ports that are in the reply to the SETUP message
                // Example for MULTICAST mode     Transport: RTP/AVP;multicast
                case RTP_TRANSPORT.MULTICAST:
                    return new RtspTransport()
                    {
                        LowerTransport = RtspTransport.LowerTransportType.UDP,
                        IsMulticast = true,
                        ClientPort = new PortCouple(5000, 5001)
                    };
                default:
                    return null;
            };
        }

        private void SendKeepAlive(object sender, System.Timers.ElapsedEventArgs e)
        {
            // Send Keepalive message
            // The ONVIF Standard uses SET_PARAMETER as "an optional method to keep an RTSP session alive"
            // RFC 2326 (RTSP Standard) says "GET_PARAMETER with no entity body may be used to test client or server liveness("ping")"

            // This code uses GET_PARAMETER (unless OPTIONS report it is not supported, and then it sends OPTIONS as a keepalive)

            RtspRequest keepAliveMessage;
            if (_serverSupportsGetParameter)
            {
                keepAliveMessage = new RtspRequestGetParameter
                {
                    RtspUri = _uri,
                    Session = _session
                };
            }
            else
            {
                keepAliveMessage = new RtspRequestOptions
                {
                    // RtspUri = new Uri(url)
                };
            }
                    

            keepAliveMessage.AddAuthorization(_authentication, _uri, _rtspSocket.NextCommandIndex());
            _rtspClient?.SendMessage(keepAliveMessage);
        }

        public void SendVideoRTCP(byte[] rtcp)
        {
            _videoRtpTransport.WriteToControlPort(rtcp);
        }

        public void SendAudioRTCP(byte[] rtcp)
        {
            _audioRtpTransport.WriteToControlPort(rtcp);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    Stop();

                    _rtspClient?.Dispose();
                    _videoRtpTransport?.Dispose();
                    _audioRtpTransport?.Dispose();
                }

                disposedValue = true;
            }
        }
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }

    public class NewStreamEventArgs : EventArgs
    {
        public NewStreamEventArgs(string streamType, IStreamConfigurationData streamConfigurationData)
        {
            StreamType = streamType;
            StreamConfigurationData = streamConfigurationData;
        }

        public string StreamType { get; }
        public IStreamConfigurationData StreamConfigurationData { get; }

        public override string ToString()
        {
            return $"{StreamType}:\r\n{StreamConfigurationData}";
        }
    }

    public interface IStreamConfigurationData
    { }

    public interface IVideoStreamConfigurationData : IStreamConfigurationData
    {
        IEnumerable<byte[]> GetNALUs();
    }

    public class H264StreamConfigurationData : IVideoStreamConfigurationData
    {
        public byte[] SPS { get; set; }
        public byte[] PPS { get; set; }

        public H264StreamConfigurationData()
        { }

        public H264StreamConfigurationData(byte[] sps, byte[] pps)
        {
            SPS = sps;
            PPS = pps;
        }

        public override string ToString()
        {
            return $"SPS: {Utilities.ToHexString(SPS)}\r\nPPS: {Utilities.ToHexString(PPS)}"; 
        }

        public IEnumerable<byte[]> GetNALUs()
        {
            return new byte[][] { SPS, PPS };
        }
    }

    public class H265StreamConfigurationData: IVideoStreamConfigurationData
    {
        public byte[] VPS { get; set; }
        public byte[] SPS { get; set; }
        public byte[] PPS { get; set; }

        public H265StreamConfigurationData()
        {   }

        public H265StreamConfigurationData(byte[] vps, byte[] sps, byte[] pps)
        {
            VPS = vps;
            SPS = sps;
            PPS = pps;
        }

        public override string ToString()
        {
            return $"VPS: {Utilities.ToHexString(VPS)}\r\nSPS: {Utilities.ToHexString(SPS)}\r\nPPS: {Utilities.ToHexString(PPS)}";
        }

        public IEnumerable<byte[]> GetNALUs()
        {
            return new byte[][] { VPS, SPS, PPS };
        }
    }

    public class AacStreamConfigurationData : IStreamConfigurationData
    {
        public AacStreamConfigurationData()
        {  }

        public AacStreamConfigurationData(int objectType, int frequencyIndex, int samplingFrequency, int channelConfiguration)
        {
            ObjectType = objectType;
            FrequencyIndex = frequencyIndex;
            SamplingFrequency = samplingFrequency;
            ChannelConfiguration = channelConfiguration;
        }

        public int ObjectType { get; set; }
        public int FrequencyIndex { get; set; }
        public int SamplingFrequency { get; set; }
        public int ChannelConfiguration { get; set; }
    }

    public class SimpleDataEventArgs : EventArgs
    {
        public SimpleDataEventArgs(IEnumerable<ReadOnlyMemory<byte>> data, DateTime timeStamp)
        {
            Data = data;
            TimeStamp = timeStamp;
        }

        public DateTime TimeStamp { get; }
        public IEnumerable<ReadOnlyMemory<byte>> Data { get; }

        public override string ToString()
        {
            return $"{TimeStamp}: Data {Data.Count()}";
        }
    }

    public static class RTSPMessageAuthExtensions
    {
        public static void AddAuthorization(this RtspMessage message, Authentication authentication, Uri uri, uint commandCounter)
        {
            if (authentication is null)
            {
                return;
            }

            string authorization = authentication.GetResponse(commandCounter, uri.AbsoluteUri, message.Method, new byte[0]);
            
            // remove if already one...
            message.Headers.Remove(RtspHeaderNames.Authorization);
            message.Headers.Add(RtspHeaderNames.Authorization, authorization);
        }
    }

    public class CustomLoggerFactory : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        {  }

        public ILogger CreateLogger(string categoryName)
        {
            return new CustomLogger();
        }

        public void Dispose()
        {  }
    }

    public class CustomLogger : ILogger
    {
        class CustomLoggerScope<TState> : IDisposable
        {
            public CustomLoggerScope(CustomLogger logger, TState state)
            {
                _logger = logger;
                State = state;
            }
            private CustomLogger _logger;
            public TState State { get; }
            public void Dispose()
            { }
        }
        public IDisposable BeginScope<TState>(TState state)
        {
            return new CustomLoggerScope<TState>(this, state);
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
                        if (SharpRTSPClient.Log.TraceEnabled)
                        {
                            SharpRTSPClient.Log.Trace(formatter.Invoke(state, exception));
                        }
                    }
                    break;

                case LogLevel.Debug:
                    {
                        if (SharpRTSPClient.Log.DebugEnabled)
                        {
                            SharpRTSPClient.Log.Debug(formatter.Invoke(state, exception));
                        }
                    }
                    break;

                case LogLevel.Information:
                    {
                        if (SharpRTSPClient.Log.InfoEnabled)
                        {
                            SharpRTSPClient.Log.Info(formatter.Invoke(state, exception));
                        }
                    }
                    break;

                case LogLevel.Warning:
                    {
                        if (SharpRTSPClient.Log.WarnEnabled)
                        {
                            SharpRTSPClient.Log.Warn(formatter.Invoke(state, exception));
                        }
                    }
                    break;

                case LogLevel.Error:
                case LogLevel.Critical:
                    {
                        if (SharpRTSPClient.Log.ErrorEnabled)
                        {
                            SharpRTSPClient.Log.Error(formatter.Invoke(state, exception));
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
