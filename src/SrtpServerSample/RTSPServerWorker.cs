using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpISOBMFF;
using SharpISOBMFF.Extensions;
using SharpMP4.Readers;
using SharpRTSPServer;
using SharpSRTP.SRTP;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Timer = System.Timers.Timer;

namespace SrtpServerSample
{
    internal class RTSPServerWorker : BackgroundService
    {
        public class MediaFile
        {
            public string FilePath { get; set; }
            public string StreamID { get; set; }
        }

        public class MediaFileReader : IDisposable
        {
            private bool _disposedValue;
            public string StreamID { get; }
            public int VideoRtpBaseTime { get; set; }
            public Timer VideoTimer { get; set; }
            public int AaudioRtpBaseTime { get; set; }
            public Timer AudioTimer { get; set; }
            public IsoStream IsoStream { get; set; }

            public MediaFileReader(string streamID)
            {
                StreamID = streamID;
            }

            protected virtual void Dispose(bool disposing)
            {
                if (!_disposedValue)
                {
                    if (disposing)
                    {
                        AudioTimer?.Dispose();
                        VideoTimer?.Dispose();
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

        private readonly ILoggerFactory _loggerFactory;
        private readonly IConfiguration _configuration;
        private RTSPServer _server;

        private List<MediaFileReader> _mediaFileStreamReaders = new List<MediaFileReader>();

        private readonly object _syncRoot = new object();

        public RTSPServerWorker(IConfiguration configuration, ILoggerFactory loggerFactory)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(loggerFactory);

            _loggerFactory = loggerFactory;
            _configuration = configuration;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var _logger = _loggerFactory.CreateLogger<RTSPServerWorker>();

            var hostName = _configuration["RTSPServerApp:HostName"];
            var port = ushort.Parse(_configuration["RTSPServerApp:Port"]);
            var userName = _configuration["RTSPServerApp:UserName"];
            var password = _configuration["RTSPServerApp:Password"];

            MediaFile[] mediaFiles = _configuration.GetSection("RTSPServerApp:Media").Get<MediaFile[]>();
            if (mediaFiles == null)
                return Task.CompletedTask;

            // generate self-signed TLS certificate
            X509Certificate2 serverCertificate = CertificateUtils.GenerateECDSAServerCertificate("localhost", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(30));
            _server = new RTSPServer(
                port, 
                userName,
                password, 
                false, 
                serverCertificate, // use RTSPS (RTSP over TLS)
                SrtpCryptoSuites.AES_CM_128_HMAC_SHA1_80, // use SAVP to protect the RTP/RTCP (SRTP using AES_CM_128_HMAC_SHA1_80)
                _loggerFactory);
            List<MediaFileReader> mediaFileReaders = new List<MediaFileReader>();

            foreach (var mediaFile in mediaFiles)
            {
                var mediaFileReader = new MediaFileReader(mediaFile.StreamID);
                ITrack rtspVideoTrack = null;
                ITrack rtspAudioTrack = null;

                string fileName = mediaFile.FilePath;
                if (Path.GetExtension(fileName).ToLowerInvariant() == ".mp4")
                {
                    Stream inputFileStream = new BufferedStream(new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read));
                    mediaFileReader.IsoStream = new IsoStream(inputFileStream);
                    var fmp4 = new Container();
                    fmp4.Read(mediaFileReader.IsoStream);

                    VideoReader inputReader = new VideoReader();
                    inputReader.Parse(fmp4);
                    IEnumerable<SharpMP4.Tracks.ITrack> inputTracks = inputReader.GetTracks();
                    IEnumerable<byte[]> videoUnits = null;

                    foreach (var inputTrack in inputTracks)
                    {
                        if (inputTrack.HandlerType == HandlerTypes.Video)
                        {
                            videoUnits = inputTrack.GetContainerSamples();

                            if (inputTrack is SharpMP4.Tracks.H264Track)
                            {
                                var h264Track = new SharpRTSPServer.H264Track();
                                h264Track.SetParameterSets(videoUnits.First(), videoUnits.Skip(1).First());
                                rtspVideoTrack = h264Track;
                            }
                            else if (inputTrack is SharpMP4.Tracks.H265Track)
                            {
                                var h265Track = new SharpRTSPServer.H265Track();
                                h265Track.SetParameterSets(videoUnits.First(), videoUnits.Skip(1).First(), videoUnits.Skip(2).First());
                                rtspVideoTrack = h265Track;
                            }
                            else if (inputTrack is SharpMP4.Tracks.H266Track)
                            {
                                var h266Track = new SharpRTSPServer.H266Track();
                                h266Track.SetParameterSets(null, null, videoUnits.First(), videoUnits.Skip(1).First(), null);
                                rtspVideoTrack = h266Track;
                            }
                            else if (inputTrack is SharpMP4.Tracks.AV1Track)
                            {
                                var av1Track = new SharpRTSPServer.AV1Track();
                                av1Track.SetOBUs(videoUnits.ToList());
                                rtspVideoTrack = av1Track;
                            }
                            else
                            {
                                continue;
                            }

                            mediaFileReader.VideoRtpBaseTime = Random.Shared.Next();
                            mediaFileReader.VideoTimer = new Timer(inputTrack.DefaultSampleDuration * 1000d / inputTrack.Timescale);
                            mediaFileReader.VideoTimer.Elapsed += (s, e) =>
                            {
                                lock (_syncRoot)
                                {
                                    var sample = inputReader.ReadSample(inputTrack.TrackID);

                                    if (sample == null)
                                    {
                                        foreach (var track in inputReader.Tracks)
                                        {
                                            track.Value.SampleIndex = 0;
                                            track.Value.FragmentIndex = 0;
                                        }
                                        return;
                                    }

                                    IEnumerable<byte[]> units = inputReader.ParseSample(inputTrack.TrackID, sample.Data);
                                    rtspVideoTrack.FeedInRawSamples((uint)unchecked(mediaFileReader.VideoRtpBaseTime + sample.PTS), units.ToList());
                                }
                            };

                            break;
                        }
                    }

                    foreach (var inputTrack in inputTracks)
                    {
                        if (inputTrack.HandlerType == HandlerTypes.Sound)
                        {
                            if (inputTrack is SharpMP4.Tracks.AACTrack aac)
                            {
                                rtspAudioTrack = new SharpRTSPServer.AACTrack(aac.AudioSpecificConfig.ToBytes(), (int)aac.SamplingRate, aac.ChannelCount);
                            }
                            else if (inputTrack is SharpMP4.Tracks.OpusTrack opus)
                            {
                                rtspAudioTrack = new SharpRTSPServer.OpusTrack();
                            }
                            else
                            {
                                continue;
                            }

                            mediaFileReader.AaudioRtpBaseTime = Random.Shared.Next();
                            mediaFileReader.AudioTimer = new Timer(inputTrack.DefaultSampleDuration * 1000d / inputTrack.Timescale);
                            mediaFileReader.AudioTimer.Elapsed += (s, e) =>
                            {
                                lock (_syncRoot)
                                {
                                    var sample = inputReader.ReadSample(inputTrack.TrackID);

                                    if (sample == null)
                                    {
                                        foreach (var track in inputReader.Tracks)
                                        {
                                            track.Value.SampleIndex = 0;
                                            track.Value.FragmentIndex = 0;
                                        }
                                        return;
                                    }

                                    IEnumerable<byte[]> units = inputReader.ParseSample(inputTrack.TrackID, sample.Data);
                                    rtspAudioTrack.FeedInRawSamples((uint)unchecked(mediaFileReader.AaudioRtpBaseTime + sample.PTS), units.ToList());
                                }
                            };

                            break;
                        }
                    }
                }
                else
                {
                    string[] jpgFiles = Directory.GetFiles(fileName, "*.jpg");
                    int jpgFileIndex = 0;

                    rtspVideoTrack = new SharpRTSPServer.MJpegTrack();

                    mediaFileReader.VideoTimer = new Timer(1000);
                    mediaFileReader.VideoTimer.Elapsed += (s, e) =>
                    {
                        rtspVideoTrack.FeedInRawSamples((uint)jpgFileIndex * 1000, new List<byte[]> { File.ReadAllBytes(jpgFiles[jpgFileIndex++ % jpgFiles.Length]) });
                    };
                }

                // set SAVP profile to make sure we also encrypt RTP
                if (rtspVideoTrack != null)
                {
                    rtspVideoTrack.RtpProfile = RtpProfiles.SAVP;
                }

                if (rtspAudioTrack != null)
                {
                    rtspAudioTrack.RtpProfile = RtpProfiles.SAVP;
                }

                var streamSource = new RTSPStreamSource(mediaFile.StreamID, rtspVideoTrack, rtspAudioTrack);
                _server.AddStreamSource(streamSource);

                mediaFileReaders.Add(mediaFileReader);
            }

            _server.StartListen();

            foreach (var mediaFileReader in mediaFileReaders)
            {
                mediaFileReader.VideoTimer?.Start();
                mediaFileReader.AudioTimer?.Start();

                _logger.LogInformation($"RTSP URL is rtsps://{userName}:{password}@{hostName}:{port}/{mediaFileReader.StreamID}");
            }

            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            base.Dispose();

            for (int i = 0; i < _mediaFileStreamReaders.Count; i++)
            {
                var stream = _mediaFileStreamReaders[i];
                stream.Dispose();
            }

            _mediaFileStreamReaders.Clear();

            _server?.Dispose();
        }
    }
}