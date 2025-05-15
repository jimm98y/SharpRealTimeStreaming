using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpMp4;
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

    public RTSPServerWorker(IConfiguration configuration, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _loggerFactory = loggerFactory;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var _logger = _loggerFactory.CreateLogger<RTSPServerWorker>();

        var hostName = _configuration["RTSPServerApp:HostName"];
        var port = ushort.Parse(_configuration["RTSPServerApp:Port"]);
        var userName = _configuration["RTSPServerApp:UserName"];
        var password = _configuration["RTSPServerApp:Password"];
        var fileName = _configuration["RTSPServerApp:FilePath"];

        _server = new RTSPServer(port, userName, password, _loggerFactory);

        Dictionary<uint, IList<IList<byte[]>>> parsedMDAT;
        uint videoTrackId = 0;
        uint audioTrackId = 0;
        TrakBox audioTrackBox = null;
        TrakBox videoTrackBox = null;
        double videoFrameRate = 0;

        ITrack rtspVideoTrack = null;
        ITrack rtspAudioTrack = null;

        if (Path.GetExtension(fileName) == ".mp4")
        {
            // frag_bunny.mp4 audio is not playable in VLC on Windows 11 (works on MacOS)
            using (Stream fs = new BufferedStream(new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read)))
            {
                using (var fmp4 = await FragmentedMp4.ParseAsync(fs))
                {
                    videoTrackBox = fmp4.FindVideoTracks().FirstOrDefault();
                    audioTrackBox = fmp4.FindAudioTracks().FirstOrDefault();

                    parsedMDAT = await fmp4.ParseMdatAsync();

                    if (videoTrackBox != null)
                    {
                        videoTrackId = fmp4.FindVideoTrackID().First();
                        videoFrameRate = fmp4.CalculateFrameRate(videoTrackBox);

                        var h264VisualSample = videoTrackBox.GetMdia().GetMinf().GetStbl().GetStsd().Children.FirstOrDefault(x => x.Type == VisualSampleEntryBox.TYPE3 || x.Type == VisualSampleEntryBox.TYPE4) as VisualSampleEntryBox;
                        if (h264VisualSample != null)
                        {
                            var avcC = (h264VisualSample.Children.First(x => x.Type == AvcConfigurationBox.TYPE) as AvcConfigurationBox).AvcDecoderConfigurationRecord;
                            rtspVideoTrack = new SharpRTSPServer.H264Track(avcC.AvcProfileIndication, 0, avcC.AvcLevelIndication);
                            _server.AddVideoTrack(rtspVideoTrack);
                        }
                        else
                        {
                            var h265VisualSample = videoTrackBox.GetMdia().GetMinf().GetStbl().GetStsd().Children.FirstOrDefault(x => x.Type == VisualSampleEntryBox.TYPE6 || x.Type == VisualSampleEntryBox.TYPE7) as VisualSampleEntryBox;
                            if (h265VisualSample != null)
                            {
                                rtspVideoTrack = new SharpRTSPServer.H265Track();
                                _server.AddVideoTrack(rtspVideoTrack);
                            }
                            else
                            {
                                throw new NotSupportedException("No supported video found!");
                            }
                        }
                    }

                    if (audioTrackBox != null)
                    {
                        audioTrackId = fmp4.FindAudioTrackID().First();

                        var audioSampleEntry = audioTrackBox.GetAudioSampleEntryBox();
                        if (audioSampleEntry.Type == AudioSampleEntryBox.TYPE3) // AAC
                        {
                            var audioConfigDescriptor = audioSampleEntry.GetAudioSpecificConfigDescriptor();
                            int audioSamplingRate = audioConfigDescriptor.GetSamplingFrequency();
                            rtspAudioTrack = new SharpRTSPServer.AACTrack(await audioConfigDescriptor.ToBytes(), audioSamplingRate, audioConfigDescriptor.ChannelConfiguration);
                            _server.AddAudioTrack(rtspAudioTrack);
                        }
                        else
                        {
                            // unsupported audio
                        }
                    }
                }
            }
        }
        else
        {
            parsedMDAT = new Dictionary<uint, IList<IList<byte[]>>>();
            parsedMDAT.Add(videoTrackId, new List<IList<byte[]>>());

            var jpgFiles = Directory.GetFiles(fileName, "*.jpg");
            for (int i = 0; i < jpgFiles.Length; i++)
            {
                parsedMDAT[videoTrackId].Add(new List<byte[]>());
                parsedMDAT[videoTrackId][i].Add(File.ReadAllBytes(jpgFiles[i]));
            }

            rtspVideoTrack = new SharpRTSPServer.MJpegTrack();
            _server.AddVideoTrack(rtspVideoTrack);

            videoFrameRate = 1;
        }

        int videoIndex = 0;
        int audioIndex = 0;
        Timer audioTimer = null;
        Timer videoTimer = null;

        if (rtspVideoTrack != null)
        {
            var videoSamplingRate = SharpRTSPServer.H264Track.DEFAULT_CLOCK;
            var videoSampleDuration = videoSamplingRate / videoFrameRate;
            var videoTrack = parsedMDAT[videoTrackId];
            videoTimer = new Timer(videoSampleDuration * 1000 / videoSamplingRate);
            videoTimer.Elapsed += (s, e) =>
            {
                if (videoIndex == 0)
                {
                    if (rtspVideoTrack is SharpRTSPServer.H264Track h264VideoTrack)
                    {
                        h264VideoTrack.SetParameterSets(videoTrack[0][0], videoTrack[0][1]);
                        videoIndex++;
                    }
                    else if (rtspVideoTrack is SharpRTSPServer.H265Track h265VideoTrack)
                    {
                        h265VideoTrack.SetParameterSets(videoTrack[0][0], videoTrack[0][1], videoTrack[0][2]);
                        videoIndex++;
                    }
                }

                try
                {
                    rtspVideoTrack.FeedInRawSamples((uint)(videoIndex * videoSampleDuration), (List<byte[]>)videoTrack[videoIndex++ % videoTrack.Count]);
                }
                catch(Exception ex)
                {
                    _logger.LogError(ex, $"FeedInRawSamples failed: {ex.Message}");
                }

                if (videoIndex % videoTrack.Count == 0)
                {
                    Reset(ref videoIndex, videoTimer, ref audioIndex, audioTimer);
                }
            };
        }

        if (rtspAudioTrack != null)
        {
            var audioSampleDuration = SharpMp4.AACTrack.AAC_SAMPLE_SIZE;
            var audioTrack = parsedMDAT[audioTrackId];
            audioTimer = new Timer(audioSampleDuration * 1000 / (rtspAudioTrack as SharpRTSPServer.AACTrack).SamplingRate);
            audioTimer.Elapsed += (s, e) =>
            {
                rtspAudioTrack.FeedInRawSamples((uint)(audioIndex * audioSampleDuration), new List<byte[]>() { audioTrack[0][audioIndex++ % audioTrack[0].Count] });

                if (audioIndex % audioTrack[0].Count == 0)
                {
                    Reset(ref videoIndex, videoTimer, ref audioIndex, audioTimer);
                }
            };
        }

        _server.StartListen();

        _logger.LogInformation($"RTSP URL is rtsp://{userName}:{password}@{hostName}:{port}");

        videoTimer?.Start();
        audioTimer?.Start();
    }

    public override void Dispose()
    {
        base.Dispose();

        _server?.Dispose();
    }

    static void Reset(ref int videoIndex, Timer videoTimer, ref int audioIndex, Timer audioTimer)
    {
        videoTimer?.Stop();
        audioTimer?.Stop();
        videoIndex = 0;
        audioIndex = 0;
        videoTimer?.Start();
        audioTimer?.Start();
    }
}