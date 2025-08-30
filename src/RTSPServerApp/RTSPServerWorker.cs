using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpISOBMFF;
using SharpISOBMFF.Extensions;
using SharpMP4.Readers;
using SharpRTSPServer;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Timer = System.Timers.Timer;

namespace RTSPServerApp;

internal class RTSPServerWorker : BackgroundService
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly IConfiguration _configuration;
    private RTSPServer _server;
    private int _videoRtpBaseTime;
    private Timer _videoTimer;
    private int _audioRtpBaseTime;
    private Timer _audioTimer;
    private IsoStream _isoStream;

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
        var fileName = _configuration["RTSPServerApp:FilePath"];

        _server = new RTSPServer(port, userName, password, _loggerFactory);

        ITrack rtspVideoTrack = null;
        ITrack rtspAudioTrack = null;

        if (Path.GetExtension(fileName).ToLowerInvariant() == ".mp4")
        {
            Stream inputFileStream = new BufferedStream(new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read));
            _isoStream = new IsoStream(inputFileStream);
            var fmp4 = new Container();
            fmp4.Read(_isoStream);

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

                    _server.AddVideoTrack(rtspVideoTrack);

                    //rtspVideoTrack.FeedInRawSamples(0, videoUnits.ToList());

                    _videoRtpBaseTime = Random.Shared.Next();
                    _videoTimer = new Timer(inputTrack.DefaultSampleDuration * 1000d / inputTrack.Timescale);
                    _videoTimer.Elapsed += (s, e) =>
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
                            rtspVideoTrack.FeedInRawSamples((uint)unchecked(_videoRtpBaseTime + sample.PTS), units.ToList());
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

                    _server.AddAudioTrack(rtspAudioTrack);

                    _audioRtpBaseTime = Random.Shared.Next();
                    _audioTimer = new Timer(inputTrack.DefaultSampleDuration * 1000d / inputTrack.Timescale);
                    _audioTimer.Elapsed += (s, e) =>
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
                            rtspAudioTrack.FeedInRawSamples((uint)unchecked(_audioRtpBaseTime + sample.PTS), units.ToList());
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
            _server.AddVideoTrack(rtspVideoTrack);

            _videoTimer = new Timer(1000);
            _videoTimer.Elapsed += (s, e) =>
            {
                rtspVideoTrack.FeedInRawSamples((uint)jpgFileIndex * 1000, new List<byte[]> { File.ReadAllBytes(jpgFiles[jpgFileIndex++ % jpgFiles.Length]) });
            };
        }

        _server.StartListen();

        _logger.LogInformation($"RTSP URL is rtsp://{userName}:{password}@{hostName}:{port}");

        _videoTimer?.Start();
        _audioTimer?.Start();

        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        base.Dispose();

        _videoTimer?.Stop();
        _audioTimer?.Stop();

        _server?.Dispose();
    }
}