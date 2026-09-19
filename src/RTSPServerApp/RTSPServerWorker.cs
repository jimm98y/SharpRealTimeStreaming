// SharpRTSPServer
// Copyright (C) 2026 Lukas Volf
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

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
using System.Diagnostics;
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

        /// <summary>
        /// The one clock both tracks are paced by, running from the moment playout starts.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Each track used to be driven by a timer set to its own sample duration, and the rate a
        /// stream leaves at was therefore whatever those timers happened to do. They do not keep the
        /// period asked of them: a Windows timer fires on the system tick, so a period that is not a
        /// multiple of it is rounded up, and the callback's own work is added on top with nothing to
        /// take it off again. The two tracks therefore ran slow by different amounts, which is the
        /// same thing as the sound sliding away from the picture - and it accumulates, so a second
        /// time round the file was seconds out.
        /// </para>
        /// <para>
        /// So the timers no longer decide anything but when to look. What is sent is whatever this
        /// clock says is due, and both tracks read it, which is what makes them unable to separate
        /// however badly the timers behave.
        /// </para>
        /// </remarks>
        public Stopwatch Clock { get; } = new Stopwatch();

        /// <summary>
        /// How far into the playout each track has been sent, in seconds, counting across loops.
        /// </summary>
        /// <remarks>
        /// The sending condition is this against the clock, so a late wake-up sends everything it
        /// missed and an early one sends nothing. Being an absolute playout time, it carries across
        /// a loop unchanged - <see cref="LoopOffsetSeconds"/> has already moved on by the time the
        /// next sample is timed against it.
        /// </remarks>
        public double VideoSentThroughSeconds { get; set; }

        public double AudioSentThroughSeconds { get; set; }

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
    /// <summary>
    /// The users this server will authenticate, read from the Users section of appsettings.json.
    /// </summary>
    /// <remarks>
    /// A name and a password apiece, so a server can have several. An empty or missing section
    /// means no repository at all, which is a server that does not authenticate - only appropriate
    /// on a trusted network.
    /// </remarks>
    private static IUserRepository ReadUsers(IConfiguration configuration, string section)
    {
        // Binds straight onto UserInfo, so the section is a list of the same shape the repository
        // hands back: a UserName and a Password apiece.
        UserInfo[] configured = configuration.GetSection(section).Get<UserInfo[]>();

        if (configured == null)
        {
            return null;
        }

        var users = new InMemoryUserRepository();

        foreach (UserInfo user in configured)
        {
            if (!string.IsNullOrEmpty(user?.UserName) && !string.IsNullOrEmpty(user.Password))
            {
                users.Add(user.UserName, user.Password);
            }
        }

        return users.Count > 0 ? users : null;
    }

    /// <summary>
    /// Whether to keep the last keyframe of a stream to start a client on, from appsettings.json.
    /// </summary>
    /// <remarks>
    /// On when the setting is missing, which is what this sample has always done. Set it to false
    /// to have a client that joins mid-group wait for the next real keyframe instead.
    /// </remarks>
    private static bool ReadKeepLastKeyFrame(string value)
    {
        return !bool.TryParse(value, out bool keep) || keep;
    }

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

        bool keepLastKeyFrame = ReadKeepLastKeyFrame(_configuration["RTSPServerApp:KeepLastKeyFrame"]);

        MediaFile[] mediaFiles = _configuration.GetSection("RTSPServerApp:Media").Get<MediaFile[]>();
        if (mediaFiles == null)
            return Task.CompletedTask;

        _server = new RTSPServer(port, ReadUsers(_configuration, "RTSPServerApp:Users"), _loggerFactory);
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

                        mediaFileReader.VideoTimer = new Timer(PACING_WAKE_MS);
                        mediaFileReader.VideoTimer.Elapsed += (s, e) =>
                        {
                            lock (_syncRoot)
                            {
                                // Send what the clock says is due, not one sample per wake-up. See
                                // MediaFileReader.Clock for why the wake-up cannot be trusted to be
                                // the sample duration.
                                for (int sent = 0; sent < PACING_MAX_PER_WAKE; sent++)
                                {
                                    double due = mediaFileReader.Clock.Elapsed.TotalSeconds + PACING_LEAD_SECONDS;
                                    if (mediaFileReader.VideoSentThroughSeconds > due)
                                        break;

                                    var sample = inputReader.ReadSample(inputTrack.TrackID);

                                    if (sample == null)
                                    {
                                        if (mediaFile.Shuffle)
                                        {
                                            StartFileAgain(mediaFileReader, inputReader.Tracks);

                                            // The next time round begins here rather than at the next
                                            // wake-up, so a loop costs no gap in the picture.
                                            continue;
                                        }

                                        // end streaming
                                        mediaFileReader.VideoTimer.Stop();
                                        _server.RemoveStreamSource(streamSource);
                                        break;
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

                                            // Swallowed, not waited on: these carry no playout time, so
                                            // stopping here would cost the track a wake-up apiece and put
                                            // it behind the one that had none to swallow.
                                            continue;
                                        }

                                        mediaFileReader.VideoRewinding = false;
                                    }

                                    mediaFileReader.VideoLastSeconds = videoSeconds;
                                    mediaFileReader.FurthestSeconds = Math.Max(
                                        mediaFileReader.FurthestSeconds, videoSeconds + videoSampleSeconds);
                                    mediaFileReader.VideoSentThroughSeconds =
                                        mediaFileReader.LoopOffsetSeconds + videoSeconds + videoSampleSeconds;

                                    long videoPts = (long)((mediaFileReader.LoopOffsetSeconds + videoSeconds) * VIDEO_RTP_CLOCK);
                                    rtspVideoTrack.FeedInRawSamples((uint)unchecked(mediaFileReader.VideoRtpBaseTime + videoPts), units.Select(u => (ReadOnlyMemory<byte>)u).ToList());
                                }
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

                        mediaFileReader.AudioTimer = new Timer(PACING_WAKE_MS);
                        mediaFileReader.AudioTimer.Elapsed += (s, e) =>
                        {
                            lock (_syncRoot)
                            {
                                // The same clock as the picture, which is the whole point - see
                                // MediaFileReader.Clock.
                                for (int sent = 0; sent < PACING_MAX_PER_WAKE; sent++)
                                {
                                    double due = mediaFileReader.Clock.Elapsed.TotalSeconds + PACING_LEAD_SECONDS;
                                    if (mediaFileReader.AudioSentThroughSeconds > due)
                                        break;

                                    var sample = inputReader.ReadSample(inputTrack.TrackID);

                                    if (sample == null)
                                    {
                                        if (mediaFile.Shuffle)
                                        {
                                            StartFileAgain(mediaFileReader, inputReader.Tracks);
                                            continue;
                                        }

                                        // end streaming
                                        mediaFileReader.AudioTimer.Stop();
                                        _server.RemoveStreamSource(streamSource);
                                        break;
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

                                            // Swallowed, not waited on: these carry no playout time,
                                            // so stopping here would cost the track a wake-up apiece
                                            // and put it behind the one that had none to swallow.
                                            continue;
                                        }

                                        mediaFileReader.AudioRewinding = false;
                                    }

                                    mediaFileReader.AudioLastSeconds = audioSeconds;
                                    mediaFileReader.FurthestSeconds = Math.Max(
                                        mediaFileReader.FurthestSeconds, audioSeconds + audioSampleSeconds);
                                    mediaFileReader.AudioSentThroughSeconds =
                                        mediaFileReader.LoopOffsetSeconds + audioSeconds + audioSampleSeconds;

                                    long audioPts = (long)((mediaFileReader.LoopOffsetSeconds + audioSeconds) * audioRtpClock);
                                    rtspAudioTrack.FeedInRawSamples((uint)unchecked(mediaFileReader.AudioRtpBaseTime + audioPts), units.Select(u => (ReadOnlyMemory<byte>)u).ToList());
                                }
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
            // picture until the stream starts a group it can follow properly. It costs one frame
            // of memory per stream and starts that client on a picture the live stream has moved
            // on from, so it can be turned off.
            streamSource.KeepLastKeyFrame = keepLastKeyFrame;

            _server.AddStreamSource(streamSource);

            mediaFileReaders.Add(mediaFileReader);
        }

        _server.StartListen();

        foreach(var mediaFileReader in mediaFileReaders)
        {
            // Before the timers, so neither track can read a clock that is not running yet and
            // conclude that everything is due.
            mediaFileReader.Clock.Restart();
            mediaFileReader.VideoTimer?.Start();
            mediaFileReader.AudioTimer?.Start();

            _logger.LogInformation($"RTSP URL is rtsp://{hostName}:{port}/{mediaFileReader.StreamID} - authenticate as one of the configured users");
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

    /// <summary>
    /// How often to look at the clock, in milliseconds.
    /// </summary>
    /// <remarks>
    /// Shorter than either track's sample duration, so no sample waits on the wake-up rate, and
    /// long enough that the thread pool is not woken for nothing. The exact figure does not
    /// matter any more: what is sent is decided by the clock, so a wake-up that arrives late
    /// sends more and one that arrives early sends none.
    /// </remarks>
    private const double PACING_WAKE_MS = 10d;

    /// <summary>
    /// How far ahead of the clock to send, in seconds.
    /// </summary>
    /// <remarks>
    /// A packet has to reach the client before its moment, not at it, so the sender runs this
    /// much in front. It is the client's cue to buffer rather than starve; too much of it is
    /// just latency.
    /// </remarks>
    private const double PACING_LEAD_SECONDS = 0.05;

    /// <summary>
    /// The most samples one wake-up will send, as a guard rather than a policy.
    /// </summary>
    /// <remarks>
    /// Catching up is the point, so this is far above anything a real gap asks for. It is here
    /// so that a file which hands back nothing usable cannot spin the thread pool for ever.
    /// </remarks>
    private const int PACING_MAX_PER_WAKE = 512;

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