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

        /// <summary>
        /// How far into the playout the current time through the file begins, in seconds.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A file played on a loop has presentation times that go back to zero every time round,
        /// and RTP timestamps that went back with them would tell the client the stream had
        /// jumped into the past. This carries the playout forward across the joins: every
        /// timestamp sent is this plus the time within the file, so the clock only ever advances.
        /// </para>
        /// <para>
        /// One value for the whole file rather than one per track, which is the point. Each track
        /// used to notice the end on its own and start again on its own, so the sound and the
        /// picture went round at different moments and drifted apart by the difference every time
        /// - a few seconds a loop, never recovered.
        /// </para>
        /// </remarks>
        public double LoopOffsetSeconds { get; set; }

        /// <summary>
        /// The furthest into the file any track of it has reached this time round, in seconds.
        /// </summary>
        /// <remarks>
        /// What the loop advances by, so that the track which ran out first does not carry the
        /// others back over ground they had already covered.
        /// </remarks>
        public double FurthestSeconds { get; set; }

        /// <summary>
        /// Whether a track is still handing back samples from where it was before the loop.
        /// </summary>
        /// <remarks>
        /// Putting a track back to its first sample does not take effect at once: the reader has
        /// a fragment in hand and goes on returning what is left of it. Those samples belong to
        /// the end of the file, and feeding them once the offset has moved on sends them a whole
        /// file into the future and then jumps back - which is the tail of the sound playing over
        /// the beginning of the next time round. They are skipped until the track's own times go
        /// backwards, which is the reader actually arriving at the start.
        /// </remarks>
        public bool VideoRewinding { get; set; }

        public bool AudioRewinding { get; set; }

        /// <summary>Where each track was last time it was read, to notice that going backwards.</summary>
        public double VideoLastSeconds { get; set; }

        public double AudioLastSeconds { get; set; }

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
                    IsoStream?.Dispose(); // otherwise the file handle stays open for the life of the process
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

    /// <summary>
    /// Basic sends the password in a reversible form, so it stays off unless the config asks for it.
    /// </summary>
    private static RtspAuthenticationScheme ReadAuthenticationScheme(string allowBasicAuthentication)
    {
        return bool.TryParse(allowBasicAuthentication, out bool allowBasic) && allowBasic
            ? RtspAuthenticationScheme.Basic
            : RtspAuthenticationScheme.Digest;
    }

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
        _server.AuthenticationScheme = ReadAuthenticationScheme(_configuration["RTSPServerApp:AllowBasicAuthentication"]);
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
                            var parameterSets = videoUnits.Take(2).ToList();
                            if (parameterSets.Count < 2)
                            {
                                _logger.LogWarning("Skipping {fileName}: the H264 track has no SPS and PPS.", fileName);
                                continue;
                            }

                            var h264Track = new SharpRTSPServer.H264Track();
                            h264Track.SetParameterSets(parameterSets[0], parameterSets[1]);
                            rtspVideoTrack = h264Track;
                        }
                        else if (inputTrack is SharpMP4.Tracks.H265Track)
                        {
                            var parameterSets = videoUnits.Take(3).ToList();
                            if (parameterSets.Count < 3)
                            {
                                _logger.LogWarning("Skipping {fileName}: the H265 track has no VPS, SPS and PPS.", fileName);
                                continue;
                            }

                            var h265Track = new SharpRTSPServer.H265Track();
                            h265Track.SetParameterSets(parameterSets[0], parameterSets[1], parameterSets[2]);
                            rtspVideoTrack = h265Track;
                        }
                        else if (inputTrack is SharpMP4.Tracks.H266Track)
                        {
                            var parameterSets = videoUnits.Take(2).ToList();
                            if (parameterSets.Count < 2)
                            {
                                _logger.LogWarning("Skipping {fileName}: the H266 track has no SPS and PPS.", fileName);
                                continue;
                            }

                            var h266Track = new SharpRTSPServer.H266Track();
                            h266Track.SetParameterSets(null, null, parameterSets[0], parameterSets[1], null);
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

                        double videoSampleSeconds = inputTrack.DefaultSampleDuration / (double)inputTrack.Timescale;

                        mediaFileReader.VideoTimer = new Timer(videoSampleSeconds * 1000d);
                        mediaFileReader.VideoTimer.Elapsed += (s, e) =>
                        {
                            lock (_syncRoot)
                            {
                                var sample = inputReader.ReadSample(inputTrack.TrackID);

                                if (sample == null)
                                {
                                    if (mediaFile.Shuffle)
                                    {
                                        StartFileAgain(mediaFileReader, inputReader.Tracks);
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

                                // Where this sample sits in the file, and where that is in the
                                // playout - which stops being the same thing once the file has been
                                // round more than once.
                                double videoSeconds = (double)sample.PTS / sourceVideoTimescale;
                                if (mediaFileReader.VideoRewinding)
                                {
                                    // Near the beginning of the file, not merely earlier than before.
                                    // Putting the track back to its first sample rewinds the reader as far
                                    // as the fragment it had in hand and no further, so what comes back
                                    // next is the last second or so of the file over again - which is
                                    // exactly what this is here to swallow.
                                    if (videoSeconds >= START_OF_FILE_SECONDS)
                                    {
                                        mediaFileReader.VideoLastSeconds = videoSeconds;
                                        return;
                                    }

                                    mediaFileReader.VideoRewinding = false;
                                }

                                mediaFileReader.VideoLastSeconds = videoSeconds;
                                mediaFileReader.FurthestSeconds = Math.Max(
                                    mediaFileReader.FurthestSeconds, videoSeconds + videoSampleSeconds);

                                long videoPts = (long)((mediaFileReader.LoopOffsetSeconds + videoSeconds) * VIDEO_RTP_CLOCK);
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

                        uint sourceAudioTimescale = GetMediaTimescale(fmp4, inputTrack.TrackID);
                        int audioRtpClock = AudioRtpClockOf(rtspAudioTrack);

                        double audioSampleSeconds = inputTrack.DefaultSampleDuration / (double)inputTrack.Timescale;

                        mediaFileReader.AudioTimer = new Timer(audioSampleSeconds * 1000d);
                        mediaFileReader.AudioTimer.Elapsed += (s, e) =>
                        {
                            lock (_syncRoot)
                            {
                                var sample = inputReader.ReadSample(inputTrack.TrackID);

                                if (sample == null)
                                {
                                    if (mediaFile.Shuffle)
                                    {
                                        StartFileAgain(mediaFileReader, inputReader.Tracks);
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
                                // As the video above: the file's units are not the clock the SDP
                                // declares. For audio the two usually agree, which is why this went
                                // unnoticed, but a file that counts otherwise should still play.
                                double audioSeconds = (double)sample.PTS / sourceAudioTimescale;
                                if (mediaFileReader.AudioRewinding)
                                {
                                    // Near the beginning of the file, not merely earlier than before - see
                                    // the video track above for why.
                                    if (audioSeconds >= START_OF_FILE_SECONDS)
                                    {
                                        mediaFileReader.AudioLastSeconds = audioSeconds;
                                        return;
                                    }

                                    mediaFileReader.AudioRewinding = false;
                                }

                                mediaFileReader.AudioLastSeconds = audioSeconds;
                                mediaFileReader.FurthestSeconds = Math.Max(
                                    mediaFileReader.FurthestSeconds, audioSeconds + audioSampleSeconds);

                                long audioPts = (long)((mediaFileReader.LoopOffsetSeconds + audioSeconds) * audioRtpClock);
                                rtspAudioTrack.FeedInRawSamples((uint)unchecked(mediaFileReader.AudioRtpBaseTime + audioPts), units.Select(u => (ReadOnlyMemory<byte>)u).ToList());
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

            // A client that arrives mid-group has nothing it can decode until the next keyframe.
            // Keeping the last one means it is given a picture straight away, and held on that
            // picture until the stream starts a group it can follow properly.
            streamSource.KeepLastKeyFrame = true;

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
    /// <summary>
    /// The clock an audio track RTP timestamps are counted in, which for these codecs is the
    /// sampling rate.
    /// </summary>
    private static int AudioRtpClockOf(ITrack audioTrack)
    {
        switch (audioTrack)
        {
            case SharpRTSPServer.AACTrack aac:
                return aac.SamplingRate;

            case SharpRTSPServer.OpusTrack _:
                return 48000; // Opus is always carried at 48 kHz, whatever it was encoded at

            default:
                return 90000;
        }
    }


    /// <summary>
    /// Starts the file again, carrying the playout clock forward over the join.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every track goes back to the beginning together, whichever of them ran out. They used to go
    /// back one at a time, as each noticed its own end - so a file whose sound is longer than its
    /// picture restarted the picture while the sound played on, and the two were that far apart
    /// from then on, and further again every time round.
    /// </para>
    /// <para>
    /// The offset advances by the furthest any track reached, so nothing is sent with a timestamp
    /// it has already used. Whatever the longer track had left is not sent: a loop has to cut
    /// somewhere, and cutting both tracks at the same instant is what keeps them together.
    /// </para>
    /// </remarks>
    /// <summary>
    /// How near the beginning of a file counts as the file having started again.
    /// </summary>
    /// <remarks>
    /// One sample is a few tens of milliseconds, so anything under a second is the start. It has to
    /// be a distance from the start rather than a step backwards, because the reader rewinds only as
    /// far as the fragment it had in hand: what it hands back after a loop is the last second or so
    /// of the file over again, which is earlier than before and still not the beginning.
    /// </remarks>
    private const double START_OF_FILE_SECONDS = 1.0;

    private static void StartFileAgain(MediaFileReader reader, IEnumerable<KeyValuePair<uint, TrackContext>> tracks)
    {
        reader.LoopOffsetSeconds += reader.FurthestSeconds;
        reader.FurthestSeconds = 0;

        // Until each track's times go backwards, what it hands back is still the end of the file.
        reader.VideoRewinding = true;
        reader.AudioRewinding = true;

        foreach (var track in tracks)
        {
            track.Value.SampleIndex = 0;
            track.Value.FragmentIndex = 0;
        }
    }

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