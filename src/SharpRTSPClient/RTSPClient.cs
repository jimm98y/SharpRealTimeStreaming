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
        private static readonly Random _rand = new Random();

        private readonly ILogger _logger;
        private readonly ILoggerFactory _loggerFactory;

        // Events that applications can receive
        public event EventHandler<NewStreamEventArgs> NewVideoStream;
        public event EventHandler<NewStreamEventArgs> NewAudioStream;
        public event EventHandler<SimpleDataEventArgs> ReceivedVideoData;
        public event EventHandler<SimpleDataEventArgs> ReceivedAudioData;

        public bool ProcessRTCP { get; set; } = true; // answer RTCP
        public event ReceivedRTCPDelegate RawRtcpMessageReceived;
        public delegate void ReceivedRTCPDelegate(byte[] rtcp, bool isVideo);

        public bool ProcessRTP { get; set; } = true;
        public event ReceivedRTPDelegate RawRtpMessageReceived;
        public delegate void ReceivedRTPDelegate(byte[] rtp, bool isVideo);

        public bool AutoPlay { get; set; } = true;

        public enum RtpTransport 
        { 
            UDP, 
            TCP, 
            MULTICAST 
        };

        public enum MediaRequest 
        { 
            VIDEO_ONLY, 
            AUDIO_ONLY, 
            VIDEO_AND_AUDIO
        };

        private enum RtspStatus { WaitingToConnect, Connecting, ConnectFailed, Connected };

        private IRtspTransport _rtspSocket; // RTSP connection
        private RtspStatus _rtspSocketStatus = RtspStatus.WaitingToConnect;
        
        // this wraps around a the RTSP tcpSocket stream
        private RtspListener _rtspClient;
        private RtpTransport _rtpTransport = RtpTransport.UDP; // Mode, either RTP over UDP or RTP over TCP using the RTSP socket

        private IRtpTransport _videoRtpTransport;
        private IRtpTransport _audioRtpTransport;

        private Uri _uri;                         // RTSP URI (username & password will be stripped out)
        private string _session = "";             // RTSP Session
        private Authentication _authentication;
        private NetworkCredential _credentials = new NetworkCredential();
        private bool _clientWantsVideo = false;   // Client wants to receive Video
        private bool _clientWantsAudio = false;   // Client wants to receive Audio

        private Uri _videoUri = null;            // URI used for the Video Track
        private int _videoPayload = -1;          // Payload Type for the Video. (often 96 which is the first dynamic payload value. Bosch use 35)

        private Uri _audioUri = null;            // URI used for the Audio Track
        private int _audioPayload = -1;          // Payload Type for the Video. (often 96 which is the first dynamic payload value)
        private string _audioCodec = "";         // Codec used with Payload Types (eg "PCMA" or "AMR")

        /// <summary>
        /// If true, the client must send an "onvif-replay" header on every play request.
        /// </summary>
        private bool _playbackSession = false;

        // Used with RTSP keepalive
        private bool _serverSupportsGetParameter = false;
        private System.Timers.Timer _keepaliveTimer = null;

        private IPayloadProcessor _videoPayloadProcessor = null;
        private IPayloadProcessor _audioPayloadProcessor = null;
        private bool _disposedValue;

        // setup messages still to send
        private readonly Queue<RtspRequestSetup> _setupMessages = new Queue<RtspRequestSetup>();

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

        /// <summary>
        /// Default ctor.
        /// </summary>
        public RTSPClient() : this(new CustomLoggerFactory())
        { }

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
        /// Connect.
        /// </summary>
        /// <param name="url">URL to connect to.</param>
        /// <param name="rtpTransport">Type of the RTP transport <see cref="RtpTransport"/>.</param>
        /// <param name="username">User name.</param>
        /// <param name="password">Password.</param>
        /// <param name="mediaRequest">Media request type <see cref="MediaRequest>."/></param>
        /// <param name="playbackSession">Playback session.</param>
        public void Connect(string url, RtpTransport rtpTransport, string username = null, string password = null, MediaRequest mediaRequest = MediaRequest.VIDEO_AND_AUDIO, bool playbackSession = false)
        {
            RtspUtils.RegisterUri();

            _logger.LogDebug("Connecting to {url} ", url);
            _uri = new Uri(url);

            _playbackSession = playbackSession;

            // Use URI to extract username and password and to make a new URL without the username and password
            var hostname = _uri.Host;
            var port = _uri.Port;
            try
            {
                if (_uri.UserInfo.Length > 0)
                {
                    _credentials = new NetworkCredential(_uri.UserInfo.Split(':')[0], _uri.UserInfo.Split(':')[1]);
                    _uri = new Uri(_uri.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.UserInfo, UriFormat.UriEscaped));
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
            _clientWantsVideo = (mediaRequest is MediaRequest.VIDEO_ONLY || mediaRequest is MediaRequest.VIDEO_AND_AUDIO);
            _clientWantsAudio = (mediaRequest is MediaRequest.AUDIO_ONLY || mediaRequest is MediaRequest.VIDEO_AND_AUDIO);

            // Connect to a RTSP Server. The RTSP session is a TCP connection
            _rtspSocketStatus = RtspStatus.Connecting;
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
                _rtspSocketStatus = RtspStatus.ConnectFailed;
                _logger.LogWarning("Error - did not connect");
                return;
            }

            if (!_rtspSocket.Connected)
            {
                _rtspSocketStatus = RtspStatus.ConnectFailed;
                _logger.LogWarning("Error - did not connect");
                return;
            }

            _rtspSocketStatus = RtspStatus.Connected;

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
            
            if (rtpTransport == RtpTransport.UDP)
            {
                // give a range of 500 pairs (1000 addresses) to try incase some address are in use
                _videoRtpTransport = new UDPSocket(50000, 51000); 
                _audioRtpTransport = new UDPSocket(50000, 51000);
            }

            if (rtpTransport == RtpTransport.TCP)
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
        /// Play.
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
        /// <param name="speed">Speed information (1.0 means normal speed, -1.0 backward speed), other values >1.0 and <-1.0 allow a different speed</param>
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
        /// <param name="speed">Speed information (1.0 means normal speed, -1.0 backward speed), other values >1.0 and <-1.0 allow a different speed</param>
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
        /// Stop.
        /// </summary>
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

        /// <summary>
        /// Send RTCP in the video channel.
        /// </summary>
        /// <param name="rtcp">RTCP message bytes.</param>
        public void SendVideoRTCP(byte[] rtcp)
        {
            _videoRtpTransport.WriteToControlPort(rtcp);
        }

        /// <summary>
        /// Send RTCP in the audio channel.
        /// </summary>
        /// <param name="rtcp">RTCP message bytes.</param>
        public void SendAudioRTCP(byte[] rtcp)
        {
            _audioRtpTransport.WriteToControlPort(rtcp);
        }

        private void VideoRtpDataReceived(object sender, RtspDataEventArgs e)
        {
            if (e.Data.Data.IsEmpty)
                return;

            using (var data = e.Data)
            {
                var rtpPacket = new RtpPacket(data.Data.Span);

                if (rtpPacket.PayloadType != _videoPayload)
                {
                    // Check the payload type in the RTP packet matches the Payload Type value from the SDP
                    _logger.LogDebug("Ignoring this Video RTP payload");
                    return;
                }

                RawRtpMessageReceived?.Invoke(data.Data.ToArray(), true);

                if (!ProcessRTP)
                {
                    return;
                }

                if (_videoPayloadProcessor == null)
                {
                    _logger.LogWarning("No video Processor");
                    return;
                }

                using (RawMediaFrame nalUnits = _videoPayloadProcessor.ProcessPacket(rtpPacket)) // this will cache the Packets until there is a Frame
                {
                    if (nalUnits.Any())
                    {
                        ReceivedVideoData?.Invoke(this, new SimpleDataEventArgs(nalUnits.Data, nalUnits.ClockTimestamp));
                    }
                }
            }
        }

        private void AudioRtpDataReceived(object sender, RtspDataEventArgs e)
        {
            if (e.Data.Data.IsEmpty)
                return;

            using (var data = e.Data)
            {
                // Received some Audio Data on the correct channel.
                var rtpPacket = new RtpPacket(data.Data.Span);

                // Check the payload type in the RTP packet matches the Payload Type value from the SDP
                if (rtpPacket.PayloadType != _audioPayload)
                {
                    _logger.LogDebug("Ignoring this Audio RTP payload");
                    return; 
                }

                RawRtpMessageReceived?.Invoke(data.Data.ToArray(), false);

                if (!ProcessRTP)
                {
                    return;
                }

                if (_audioPayloadProcessor == null)
                {
                    _logger.LogWarning("No parser for audio RTP payload {audioPayload}", _audioPayload);
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

        private void VideoRtcpControlDataReceived(object sender, RtspDataEventArgs e)
        {
            if (e.Data.Data.IsEmpty)
                return;

            _logger.LogDebug("Received video RTCP message");

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

        private void AudioRtcpControlDataReceived(object sender, RtspDataEventArgs e)
        {
            if (e.Data.Data.IsEmpty)
                return;

            _logger.LogDebug("Received audio RTCP message");

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
                //int rtcpVersion = (span[packetIndex + 0] >> 6);
                //int rtcpPadding = (span[packetIndex + 0] >> 5) & 0x01;
                //int rtcpReceptionReportCount = (span[packetIndex + 0] & 0x1F);
                byte rtcpPacketType = span[packetIndex + 1]; // Values from 200 to 207
                int rtcpLength = (int)(span[packetIndex + 2] << 8) + (int)(span[packetIndex + 3]); // number of 32 bit words
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

                    UInt32 ntpMswSeconds = (uint)(span[packetIndex + 8] << 24) + (uint)(span[packetIndex + 9] << 16)
                    + (uint)(span[packetIndex + 10] << 8) + span[packetIndex + 11];

                    //UInt32 ntpLswFractions = (uint)(span[packetIndex + 12] << 24) + (uint)(span[packetIndex + 13] << 16)
                    //+ (uint)(span[packetIndex + 14] << 8) + span[packetIndex + 15];

                    UInt32 rtpTimestamp = (uint)(span[packetIndex + 16] << 24) + (uint)(span[packetIndex + 17] << 16)
                    + (uint)(span[packetIndex + 18] << 8) + span[packetIndex + 19];

                    //double ntp = ntpMswSeconds + (ntpLswFractions / UInt32.MaxValue);

                    // NTP Most Signigicant Word is relative to 0h, 1 Jan 1900
                    // This will wrap around in 2036
                    var time = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

                    time = time.AddSeconds(ntpMswSeconds); // adds 'double' (whole&fraction)

                    _logger.LogDebug("RTCP time (UTC) for RTP timestamp {timestamp} is {time}", rtpTimestamp, time);

                    // Send a Receiver Report
                    try
                    {
                        byte[] rtcpReceiverReport = new byte[8];
                        int version = 2;
                        int paddingBit = 0;
                        int reportCount = 0; // an empty report
                        int packetType = 201; // Receiver Report
                        int length = (rtcpReceiverReport.Length / 4) - 1; // num 32 bit words minus 1
                        rtcpReceiverReport[0] = (byte)((version << 6) + (paddingBit << 5) + reportCount);
                        rtcpReceiverReport[1] = (byte)(packetType);
                        rtcpReceiverReport[2] = (byte)((length >> 8) & 0xFF);
                        rtcpReceiverReport[3] = (byte)((length >> 0) & 0XFF);
                        rtcpReceiverReport[4] = (byte)((ssrc >> 24) & 0xFF);
                        rtcpReceiverReport[5] = (byte)((ssrc >> 16) & 0xFF);
                        rtcpReceiverReport[6] = (byte)((ssrc >> 8) & 0xFF);
                        rtcpReceiverReport[7] = (byte)((ssrc >> 0) & 0xFF);

                        reports.Add(rtcpReceiverReport);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug($"Error writing RTCP packet: {ex.Message}.");
                    }
                }

                packetIndex += (rtcpLength + 1) * 4;
            }

            return reports;
        }

        private void RtspMessageReceived(object sender, RtspChunkEventArgs e)
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

                if (message.ReturnCode == 401 && message.OriginalRequest?.Headers.ContainsKey(RtspHeaderNames.Authorization) == true)
                {
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
                    string wwwAuthenticate = value ?? string.Empty;
                    _authentication = Authentication.Create(_credentials, wwwAuthenticate);
                    _logger.LogDebug("WWW Authorize parsed for {authentication}", _authentication);
                }

                RtspMessage resendMessage = message.OriginalRequest?.Clone() as RtspMessage;

                if (resendMessage != null)
                {
                    resendMessage.AddAuthorization(_authentication, _uri, _rtspSocket.NextCommandIndex());
                    _rtspClient?.SendMessage(resendMessage);
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

                bool isVideoChannel = message.OriginalRequest.RtspUri == _videoUri;
                bool isAudioChannel = message.OriginalRequest.RtspUri == _audioUri;
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
                    RtspRequestSetup nextSetup = _setupMessages.Dequeue();
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
            _logger.LogDebug("SDP:\n{sdp}", Encoding.UTF8.GetString(message.Data.Span.ToArray()));

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
                _uri = new Uri(_uri, customControlUri.Value);
            }

            // Process each 'Media' Attribute in the SDP (each sub-stream)
            //  to look for first supported video substream
            if (_clientWantsVideo)
            {
                foreach (Media media in sdpData.Medias.Where(m => m.MediaType == Media.MediaTypes.video))
                {
                    // search the attributes for control, rtpmap and fmtp
                    // holds SPS and PPS in base64 (h264 video)
                    AttributFmtp fmtp = media.Attributs.FirstOrDefault(x => x.Key == "fmtp") as AttributFmtp;
                    AttributRtpMap rtpmap = media.Attributs.FirstOrDefault(x => x.Key == "rtpmap") as AttributRtpMap;
                    _videoUri = GetControlUri(media);

                    int fmtpPayloadNumber = -1;
                    if (fmtp != null)
                    {
                        fmtpPayloadNumber = fmtp.PayloadNumber;
                    }

                    // extract h265 donl if available...
                    bool h265HasDonl = false;

                    if ((rtpmap?.EncodingName?.ToUpperInvariant().Equals("H265") ?? false) && !string.IsNullOrEmpty(fmtp?.FormatParameter))
                    {
                        var param = H265Parameters.Parse(fmtp.FormatParameter);
                        if (param.ContainsKey("sprop-max-don-diff") && int.TryParse(param["sprop-max-don-diff"], out int donl) && donl > 0)
                        {
                            h265HasDonl = true;
                        }
                    }

                    // some cameras are really mess with the payload type.
                    // must check also the rtpmap for the correct format to load (sending an h265 payload when giving an h264 stream [Some Bosch camera])
                    string payloadName = string.Empty;
                    if (rtpmap != null && ((fmtpPayloadNumber > -1 && rtpmap.PayloadNumber == fmtpPayloadNumber) || fmtpPayloadNumber == -1) && rtpmap.EncodingName != null)
                    {
                        // found a valid codec
                        payloadName = rtpmap.EncodingName.ToUpperInvariant();
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
                        _videoPayload = media.PayloadType;
                    }
                    else
                    {
                        _videoPayload = media.PayloadType;
                        if (media.PayloadType < 96)
                        {
                            // PayloadType is a static value, so we can use it to determine the codec
                            switch(media.PayloadType)
                            {
                                case 26:
                                    {
                                        _videoPayloadProcessor = new JPEGPayload();
                                        payloadName = "JPEG";
                                    }
                                    break;
                                case 33:
                                    {
                                        _videoPayloadProcessor = new MP2TransportPayload();
                                        payloadName = "MP2T";
                                    }
                                    break;
                                default:
                                    {
                                        _videoPayloadProcessor = null;
                                        payloadName = string.Empty;
                                    }
                                    break;
                            };
                        }
                    }

                    IStreamConfigurationData streamConfigurationData = null;

                    if (_videoPayloadProcessor is H264Payload && fmtp?.FormatParameter != null)
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
                    else if (_videoPayloadProcessor is H265Payload && fmtp?.FormatParameter != null)
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
                    }

                    // Send the SETUP RTSP command if we have a matching Payload Decoder
                    if (_videoPayloadProcessor != null)
                    {
                        RtspTransport transport = CalculateTransport(_videoRtpTransport);

                        // Generate SETUP messages
                        if (transport != null)
                        {
                            RtspRequestSetup setupMessage = new RtspRequestSetup()
                            {
                                RtspUri = _videoUri
                            };
                            setupMessage.AddTransport(transport);
                            setupMessage.AddAuthorization(_authentication, _uri, _rtspSocket.NextCommandIndex());
                            if (_playbackSession) { setupMessage.AddRequireOnvifRequest(); }
                            
                            // Add SETUP message to list of mesages to send
                            _setupMessages.Enqueue(setupMessage);
                            NewVideoStream?.Invoke(this, new NewStreamEventArgs(payloadName, streamConfigurationData));
                        }
                        break;
                    }
                }
            }

            if (_clientWantsAudio)
            {
                foreach (Media media in sdpData.Medias.Where(m => m.MediaType == Media.MediaTypes.audio))
                {
                    // search the attributes for control, rtpmap and fmtp
                    AttributFmtp fmtp = media.Attributs.FirstOrDefault(x => x.Key == "fmtp") as AttributFmtp;
                    AttributRtpMap rtpmap = media.Attributs.FirstOrDefault(x => x.Key == "rtpmap") as AttributRtpMap;

                    _audioUri = GetControlUri(media);
                    _audioPayload = media.PayloadType;

                    IStreamConfigurationData streamConfigurationData = null;
                    if (media.PayloadType < 96)
                    {
                        // fixed payload type
                        switch(media.PayloadType)
                        {
                            case 0:
                                (_audioPayloadProcessor, _audioCodec) = (new G711Payload(), "PCMU");
                                break;
                            case 8:
                                (_audioPayloadProcessor, _audioCodec) = (new G711Payload(), "PCMA");
                                break;
                            default:
                                (_audioPayloadProcessor, _audioCodec) = (null, "");
                                break;
                        };
                    }
                    else
                    {
                        // dynamic payload type
                        _audioCodec = rtpmap?.EncodingName?.ToUpperInvariant() ?? string.Empty;
                        switch(_audioCodec)
                        {
                            // Create AAC RTP Parser
                            // Example fmtp is "96 profile-level-id=1;mode=AAC-hbr;sizelength=13;indexlength=3;indexdeltalength=3;config=1490"
                            // Example fmtp is ""96 streamtype=5;profile-level-id=1;mode=AAC-hbr;sizelength=13;indexlength=3;indexdeltalength=3;config=1210"
                            case "MPEG4-GENERIC":
                                {
                                    if (fmtp?["mode"].ToLowerInvariant() == "aac-hbr")
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
                            _audioCodec = "AAC";
                            streamConfigurationData = new AACStreamConfigurationData()
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
                            RtspRequestSetup setupMessage = new RtspRequestSetup()
                            {
                                RtspUri = _audioUri,
                            };
                            setupMessage.AddTransport(transport);
                            setupMessage.AddAuthorization(_authentication, _uri, _rtspSocket.NextCommandIndex());
                            if (_playbackSession)
                            {
                                setupMessage.AddRequireOnvifRequest();
                                setupMessage.AddRateControlOnvifRequest(false);
                            }
                            // Add SETUP message to list of mesages to send
                            _setupMessages.Enqueue(setupMessage);
                            NewAudioStream?.Invoke(this, new NewStreamEventArgs(_audioCodec, streamConfigurationData));
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
                string sdpControl = attrib.Value;

                if (sdpControl.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) || 
                    sdpControl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
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
            return controlUri;
        }

        private RtspTransport CalculateTransport(IRtpTransport transport)
        {
            switch(_rtpTransport)
            {
                // Server interleaves the RTP packets over the RTSP connection
                // Example for TCP mode (RTP over RTSP)   Transport: RTP/AVP/TCP;interleaved=0-1
                case RtpTransport.TCP:
                    return new RtspTransport()
                    {
                        LowerTransport = RtspTransport.LowerTransportType.TCP,
                        // Eg Channel 0 for RTP video data. Channel 1 for RTCP status reports
                        Interleaved = (transport as RtpTcpTransport)?.Channels ?? throw new ApplicationException("TCP transport asked and no tcp channel allocated"),
                    };
                case RtpTransport.UDP:
                    return new RtspTransport()
                    {
                        LowerTransport = RtspTransport.LowerTransportType.UDP,
                        IsMulticast = false,
                        ClientPort = (transport as UDPSocket)?.Ports ?? throw new ApplicationException("UDP transport asked and no udp port allocated"),
                    };
                // Server sends the RTP packets to a Pair of UDP ports (one for data, one for rtcp control messages)
                // using Multicast Address and Ports that are in the reply to the SETUP message
                // Example for MULTICAST mode     Transport: RTP/AVP;multicast
                case RtpTransport.MULTICAST:
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
                { };
            }

            keepAliveMessage.AddAuthorization(_authentication, _uri, _rtspSocket.NextCommandIndex());
            _rtspClient?.SendMessage(keepAliveMessage);
        }

        #region IDisposable

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    Stop();

                    _rtspClient?.Dispose();
                    _videoRtpTransport?.Dispose();
                    _audioRtpTransport?.Dispose();
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
            if (authentication == null)
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
