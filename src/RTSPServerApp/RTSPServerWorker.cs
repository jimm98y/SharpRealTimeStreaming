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
    private Timer _videoTimer;
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
            SharpH26X.Log.SinkDebug = (o, e) => { };
            SharpH26X.Log.SinkInfo = (o, e) => { };
            SharpISOBMFF.Log.SinkInfo = (o, e) => { };
            SharpISOBMFF.Log.SinkDebug = (o, e) => { };
            SharpMP4.Log.SinkInfo = (o, e) => { };
            SharpMP4.Log.SinkDebug = (o, e) => { };

            Stream inputFileStream = new BufferedStream(new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read));
            _isoStream = new IsoStream(inputFileStream);
            var fmp4 = new Container();
            fmp4.Read(_isoStream);

            Mp4Reader inputReader = new Mp4Reader();
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
                    else
                    {
                        continue;
                    }

                    _server.AddVideoTrack(rtspVideoTrack);

                    //rtspVideoTrack.FeedInRawSamples(0, videoUnits.ToList());

                    _videoTimer = new Timer(inputTrack.DefaultSampleDuration * 1000 / inputTrack.Timescale);
                    _videoTimer.Elapsed += (s, e) =>
                    {
                        Mp4Sample sample;

                        lock (_syncRoot)
                        {
                            sample = inputReader.ReadSample(inputTrack.TrackID);

                            if (sample == null)
                            {
                                foreach (var track in inputReader.Tracks)
                                {
                                    track.SampleIndex = 0;
                                    track.FragmentIndex = 0;
                                }
                                return;
                            }
                        }

                        IEnumerable<byte[]> units = inputReader.ParseSample(inputTrack.TrackID, sample.Data);
                        rtspVideoTrack.FeedInRawSamples((uint)sample.DTS, units.ToList());
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
                    else
                    {
                        continue;
                    }

                    _server.AddAudioTrack(rtspAudioTrack);

                    _audioTimer = new Timer(inputTrack.DefaultSampleDuration * 1000 / inputTrack.Timescale);
                    _audioTimer.Elapsed += (s, e) =>
                    {
                        Mp4Sample sample;

                        lock (_syncRoot)
                        {
                            sample = inputReader.ReadSample(inputTrack.TrackID);

                            if (sample == null)
                            {
                                foreach (var track in inputReader.Tracks)
                                {
                                    track.SampleIndex = 0;
                                    track.FragmentIndex = 0;
                                }
                                return;
                            }
                        }

                        IEnumerable<byte[]> units = inputReader.ParseSample(inputTrack.TrackID, sample.Data);
                        rtspAudioTrack.FeedInRawSamples((uint)sample.DTS, units.ToList());
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