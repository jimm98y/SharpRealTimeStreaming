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
    public class MediaFile
    {
        public string FilePath { get; set; }
        public string StreamID { get; set; }
        public bool Shuffle { get; set; }
    }

    public class MediaFileReader : IDisposable
    {
        private bool _disposedValue;
        public string StreamID { get; }
        public int VideoRtpBaseTime { get; set; }
        public Timer VideoTimer { get; set; }
        public int AudioRtpBaseTime { get; set; }
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

        _server = new RTSPServer(port, userName, password, _loggerFactory);
        List<MediaFileReader> mediaFileReaders = new List<MediaFileReader>();


        foreach (var mediaFile in mediaFiles)
        {
            var mediaFileReader = new MediaFileReader(mediaFile.StreamID);
            ITrack rtspVideoTrack = null;
            ITrack rtspAudioTrack = null;

            string fileName = mediaFile.FilePath;
            RTSPStreamSource streamSource = null;

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

                        // sample.PTS is expressed in the MP4 *media* timescale (mdhd, e.g. 12288 for this file),
                        //  but the video RTP/SDP clock is 90000 Hz. Feeding the raw PTS makes the RTP timestamps
                        //  advance ~7x too slowly, so the client renders choppy video. We must rescale the PTS
                        //  into the 90 kHz domain. Note: inputTrack.Timescale cannot be used as the divisor - for
                        //  fragmented MP4 it falls back to the SPS VUI timing (48 here), which does not match the
                        //  media timescale the PTS values are actually expressed in.
                        uint sourceVideoTimescale = GetMediaTimescale(fmp4, inputTrack.TrackID);
                        const int VIDEO_RTP_CLOCK = 90000;

                        mediaFileReader.VideoTimer = new Timer(inputTrack.DefaultSampleDuration * 1000d / inputTrack.Timescale);
                        mediaFileReader.VideoTimer.Elapsed += (s, e) =>
                        {
                            lock (_syncRoot)
                            {
                                var sample = inputReader.ReadSample(inputTrack.TrackID);

                                if (sample == null)
                                {
                                    if (mediaFile.Shuffle)
                                    {
                                        foreach (var track in inputReader.Tracks)
                                        {
                                            track.Value.SampleIndex = 0;
                                            track.Value.FragmentIndex = 0;
                                        }
                                    }
                                    else
                                    {
                                        // end streaming
                                        mediaFileReader.VideoTimer.Stop();
                                        _server.RemoveStreamSource(streamSource);
                                    }

                                    return;
                                }

                                IEnumerable<byte[]> units = inputReader.ParseSample(inputTrack.TrackID, sample.Data);

                                long videoPts = (long)sample.PTS * VIDEO_RTP_CLOCK / sourceVideoTimescale;
                                rtspVideoTrack.FeedInRawSamples((uint)unchecked(mediaFileReader.VideoRtpBaseTime + videoPts), units.Select(u => (ReadOnlyMemory<byte>)u).ToList());
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

                        mediaFileReader.AudioRtpBaseTime = Random.Shared.Next();
                        mediaFileReader.AudioTimer = new Timer(inputTrack.DefaultSampleDuration * 1000d / inputTrack.Timescale);
                        mediaFileReader.AudioTimer.Elapsed += (s, e) =>
                        {
                            lock (_syncRoot)
                            {
                                var sample = inputReader.ReadSample(inputTrack.TrackID);

                                if (sample == null)
                                {
                                    if (mediaFile.Shuffle)
                                    {
                                        foreach (var track in inputReader.Tracks)
                                        {
                                            track.Value.SampleIndex = 0;
                                            track.Value.FragmentIndex = 0;
                                        }
                                    }
                                    else
                                    {
                                        // end streaming
                                        mediaFileReader.AudioTimer.Stop();
                                        _server.RemoveStreamSource(streamSource);
                                    }
                                    
                                    return;
                                }

                                IEnumerable<byte[]> units = inputReader.ParseSample(inputTrack.TrackID, sample.Data);
                                rtspAudioTrack.FeedInRawSamples((uint)unchecked(mediaFileReader.AudioRtpBaseTime + sample.PTS), units.Select(u => (ReadOnlyMemory<byte>)u).ToList());
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

                     rtspVideoTrack.FeedInRawSamples((uint)jpgFileIndex * 1000, new List<ReadOnlyMemory<byte>> { File.ReadAllBytes(jpgFiles[jpgFileIndex++ % jpgFiles.Length]) });

                    if(jpgFileIndex % jpgFiles.Length == 0)
                    {
                        if(!mediaFile.Shuffle)
                        {
                            mediaFileReader.VideoTimer.Stop();
                            _server.RemoveStreamSource(streamSource);
                        }
                    }

                };
            }

            streamSource = new RTSPStreamSource(mediaFile.StreamID, rtspVideoTrack, rtspAudioTrack);
            _server.AddStreamSource(streamSource);

            mediaFileReaders.Add(mediaFileReader);
        }

        _server.StartListen();

        foreach(var mediaFileReader in mediaFileReaders)
        {
            mediaFileReader.VideoTimer?.Start();
            mediaFileReader.AudioTimer?.Start();

            _logger.LogInformation($"RTSP URL is rtsp://{userName}:{password}@{hostName}:{port}/{mediaFileReader.StreamID}");
        }

        return Task.CompletedTask;
    }

    // Returns the media (mdhd) timescale for the given track. This is the clock the sample PTS/DTS values are
    //  expressed in, which (for fragmented MP4) is not necessarily the same as ITrack.Timescale.
    private static uint GetMediaTimescale(Container container, uint trackID)
    {
        foreach (var moov in container.Children.OfType<MovieBox>())
        {
            foreach (var trak in moov.Children.OfType<TrackBox>())
            {
                var tkhd = trak.Children.OfType<TrackHeaderBox>().FirstOrDefault();
                if (tkhd == null || tkhd.TrackID != trackID)
                    continue;

                var mdhd = trak.Children.OfType<MediaBox>().Single()
                    .Children.OfType<MediaHeaderBox>().Single();
                return mdhd.Timescale;
            }
        }

        throw new InvalidOperationException($"Could not find media timescale for track {trackID}.");
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