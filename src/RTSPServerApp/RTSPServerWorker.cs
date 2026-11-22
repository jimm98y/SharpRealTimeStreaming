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
        /// <summary>The stream the file is fed to, of whose clients the ticker asks.</summary>
        public RTSPStreamSource Source { get; set; }
        public int VideoRtpBaseTime { get; set; }
        public Timer VideoTimer { get; set; }
        public int AudioRtpBaseTime { get; set; }
        public Timer AudioTimer { get; set; }

        // what each wake-up of the worker's one timer runs of the tracks, null once stopped
        public Action VideoTick { get; set; }
        public Action AudioTick { get; set; }
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

        /// <summary>
        /// Of nobody watching, the track neither read nor fed while its clock runs on: of a client again, it is moved
        /// to where the clock says it plays now (<see cref="RTSPServerWorker"/>'s ResumeAtClock).
        /// </summary>
        public bool VideoIdle { get; set; }
        public bool AudioIdle { get; set; }

        /// <summary>How long once round the file is, in seconds, of its tracks' sample timings; 0 until a track resumes.</summary>
        public double DurationSeconds { get; set; }

        /// <summary>
        /// Of the picture resumed at the keyframe before the clock, the index of the one after: once that keyframe is fed,
        /// as the picture a client joining has at once, the track is read on at this one, at its time - -1 of none.
        /// </summary>
        public int VideoNextKeyFrame { get; set; } = -1;

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

    private System.Threading.Timer _ticker;

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
        // of none set, the library's default (RTSPServer.DEFAULT_MAX_CONNECTIONS, 100); of 0, no limit
        if (int.TryParse(_configuration["RTSPServerApp:MaxConnections"], out int maxConnections))
            _server.MaxConnections = maxConnections;
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
                        else if (inputTrack is SharpMP4.Tracks.VP9Track)
                        {
                            // Nothing out of band: what a decoder needs is in each key frame's header.
                            rtspVideoTrack = new SharpRTSPServer.VP9Track();
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

                        // The tick is queued to the thread pool every interval, whether or not
                        //  the last one has returned, and a send held up by a slow client lets the
                        //  next wake-up read later frames and send them first. One wake-up of the
                        //  track at a time keeps the frames in the order they were read, without
                        //  holding up the other track the way sending under the reader's lock would - and
                        //  one still sending, the next skipped, not waiting on a thread of the pool.
                        object videoFeedGate = new object();
                        var videoFeed = new List<ReadOnlyMemory<byte>>();
                        // the track's samples read into one, its data valid until the next is read - fed before then:
                        //  of a sample read of each, a MediaSample of each, half of what the server allocated
                        var videoSample = new MediaSample();
                        // the sample's units parsed into the track's list: of an enumerable of each, an iterator of each sample
                        var videoSampleUnits = new List<ArraySegment<byte>>();
                        // of the track's samples, their times: of the first resume, and kept
                        SampleTiming[] videoTimings = null;

                        mediaFileReader.VideoTick = () =>
                        {
                            if (!Monitor.TryEnter(videoFeedGate))
                                return;
                            try
                            {
                                // the other track of this file being fed, this wake left to it: of a wait, of each audio tick behind the picture of its
                                //  file, of 300 streams half the server's CPU spun in the lock; the next wake sends what is due
                                if (!Monitor.TryEnter(mediaFileReader))
                                    return;
                                try
                                {
                                    // of nobody watching, nothing read: of 1000 streams configured and no client, the
                                    //  server read and fed them all, a core and a half
                                    if (streamSource.ConnectionList.Count == 0)
                                    {
                                        mediaFileReader.VideoIdle = true;
                                        return;
                                    }
                                    if (mediaFileReader.VideoIdle)
                                    {
                                        mediaFileReader.VideoIdle = false;
                                        mediaFileReader.VideoRewinding = false;
                                        videoTimings ??= inputReader.GetSampleTimings(inputTrack.TrackID);
                                        mediaFileReader.VideoSentThroughSeconds = ResumeAtClock(mediaFileReader, inputReader, inputTrack.TrackID, videoTimings, sourceVideoTimescale, keyFrame: true);
                                    }

                                    // Send what the clock says is due, not one sample per wake-up. See
                                    // MediaFileReader.Clock for why the wake-up cannot be trusted to be
                                    // the sample duration.
                                    for (int sent = 0; sent < PACING_MAX_PER_WAKE; sent++)
                                    {
                                        double due = mediaFileReader.Clock.Elapsed.TotalSeconds + PACING_LEAD_SECONDS;
                                        if (mediaFileReader.VideoSentThroughSeconds > due)
                                            break;

                                        var sample = inputReader.ReadSample(inputTrack.TrackID, videoSample) ? videoSample : null;

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
                                            mediaFileReader.VideoTick = null;
                                            _server.RemoveStreamSource(streamSource);
                                            break;
                                        }

                                        var units = videoSampleUnits;
                                        units.Clear();
                                        inputReader.ParseSample(inputTrack.TrackID, sample.Data, units);

                                        // Where this sample sits in the file, and where that is in the
                                        // playout - which stops being the same thing once the file has been
                                        // round more than once.
                                        double videoSeconds = (double)sample.PTS / sourceVideoTimescale;
                                        double videoDecodeSeconds = (double)sample.DTS / sourceVideoTimescale;
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
                                            mediaFileReader.LoopOffsetSeconds + videoDecodeSeconds + videoSampleSeconds;

                                        long videoPts = (long)((mediaFileReader.LoopOffsetSeconds + videoSeconds) * VIDEO_RTP_CLOCK);

                                        // Fed under the lock, of the reader's buffer as it is, not copied: the
                                        //  server packetizes it into packets of its own and queues them, a write
                                        //  to a client no longer of the feeding - so the lock holds up only the
                                        //  stream's other track, of the packetizing. The lock is of each stream:
                                        //  its two tracks share a reader, other streams none - of one lock of all,
                                        //  1000 streams' ticks queued at once queued on it. Of the copy of each
                                        //  sample, 70% of what the server allocated.
                                        videoFeed.Clear();
                                        AddUnits(videoFeed, units);
                                        rtspVideoTrack.FeedInRawSamples((uint)unchecked(mediaFileReader.VideoRtpBaseTime + videoPts), videoFeed);

                                        // the keyframe before the clock sent, as the picture a client joining has at once:
                                        //  the pictures after it skipped, the next read the next keyframe, at its time
                                        if (mediaFileReader.VideoNextKeyFrame >= 0)
                                        {
                                            int next = mediaFileReader.VideoNextKeyFrame;
                                            mediaFileReader.VideoNextKeyFrame = -1;
                                            inputReader.SeekSample(inputTrack.TrackID, (uint)next);
                                            if (next < videoTimings.Length)
                                            {
                                                mediaFileReader.VideoSentThroughSeconds = mediaFileReader.LoopOffsetSeconds + videoTimings[next].DTS / (double)sourceVideoTimescale;
                                            }
                                            else
                                            {
                                                // of none after it, the first of the next time round: the end waited for
                                                mediaFileReader.VideoSentThroughSeconds = mediaFileReader.LoopOffsetSeconds + mediaFileReader.DurationSeconds;
                                                mediaFileReader.FurthestSeconds = Math.Max(mediaFileReader.FurthestSeconds, mediaFileReader.DurationSeconds);
                                            }
                                        }
                                    }
                                }
                                finally
                                {
                                    Monitor.Exit(mediaFileReader);
                                }
                            }
                            finally
                            {
                                Monitor.Exit(videoFeedGate);
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

                        // As the picture: one wake-up at a time, so frames go out in the order read.
                        object audioFeedGate = new object();
                        var audioFeed = new List<ReadOnlyMemory<byte>>();
                        var audioSample = new MediaSample();
                        var audioSampleUnits = new List<ArraySegment<byte>>();
                        SampleTiming[] audioTimings = null;

                        mediaFileReader.AudioTick = () =>
                        {
                            if (!Monitor.TryEnter(audioFeedGate))
                                return;
                            try
                            {
                                // the other track of this file being fed, this wake left to it: of a wait, of each audio tick behind the picture of its
                                //  file, of 300 streams half the server's CPU spun in the lock; the next wake sends what is due
                                if (!Monitor.TryEnter(mediaFileReader))
                                    return;
                                try
                                {
                                    // as the picture: of nobody watching, nothing read
                                    if (streamSource.ConnectionList.Count == 0)
                                    {
                                        mediaFileReader.AudioIdle = true;
                                        return;
                                    }
                                    if (mediaFileReader.AudioIdle)
                                    {
                                        mediaFileReader.AudioIdle = false;
                                        mediaFileReader.AudioRewinding = false;
                                        audioTimings ??= inputReader.GetSampleTimings(inputTrack.TrackID);
                                        mediaFileReader.AudioSentThroughSeconds = ResumeAtClock(mediaFileReader, inputReader, inputTrack.TrackID, audioTimings, sourceAudioTimescale, keyFrame: false);
                                    }

                                    // The same clock as the picture, which is the whole point - see
                                    // MediaFileReader.Clock.
                                    for (int sent = 0; sent < PACING_MAX_PER_WAKE; sent++)
                                    {
                                        double due = mediaFileReader.Clock.Elapsed.TotalSeconds + PACING_LEAD_SECONDS;
                                        if (mediaFileReader.AudioSentThroughSeconds > due)
                                            break;

                                        var sample = inputReader.ReadSample(inputTrack.TrackID, audioSample) ? audioSample : null;

                                        if (sample == null)
                                        {
                                            if (mediaFile.Shuffle)
                                            {
                                                StartFileAgain(mediaFileReader, inputReader.Tracks);
                                                continue;
                                            }

                                            // end streaming
                                            mediaFileReader.AudioTick = null;
                                            _server.RemoveStreamSource(streamSource);
                                            break;
                                        }

                                        var units = audioSampleUnits;
                                        units.Clear();
                                        inputReader.ParseSample(inputTrack.TrackID, sample.Data, units);
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
                                        // See the picture track: fed under the lock, of the reader's buffer.
                                        audioFeed.Clear();
                                        AddUnits(audioFeed, units);
                                        rtspAudioTrack.FeedInRawSamples((uint)unchecked(mediaFileReader.AudioRtpBaseTime + audioPts), audioFeed);
                                    }
                                }
                                finally
                                {
                                    Monitor.Exit(mediaFileReader);
                                }
                            }
                            finally
                            {
                                Monitor.Exit(audioFeedGate);
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
            mediaFileReader.Source = streamSource;

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

        // One timer of all the streams, each track's tick queued to the pool of it: of a timer of each track, the 2000 of
        //  1000 streams fought over the timer queue's lock, a fifth of the server's CPU. Of a work item of each track's,
        //  kept: of the pool's of a delegate and its state, an object of each tick queued, 2000 of each wake-up.
        var ticks = mediaFileReaders.Select(reader => (Reader: reader, Video: new TickWork(reader, video: true), Audio: new TickWork(reader, video: false))).ToArray();

        // Of the streams, those a client came to, ticked until nobody watches and their tracks are idle again; of the rest,
        //  nothing - of each wake the ticker looked at all 1000. Each idle to begin with, so its first client finds it where
        //  the clock plays now.
        var active = new List<(MediaFileReader Reader, TickWork Video, TickWork Audio)>();
        foreach (var tick in ticks)
        {
            tick.Reader.VideoIdle = tick.Reader.AudioIdle = true;
            if (tick.Reader.Source != null)
            {
                tick.Reader.Source.ConnectionsChanged += (s, e) =>
                {
                    lock (active)
                    {
                        if (!active.Contains(tick))
                            active.Add(tick);
                    }
                };
            }
        }

        _ticker = new System.Threading.Timer(_ =>
        {
            lock (active)
            {
                for (int i = active.Count - 1; i >= 0; i--)
                {
                    var (reader, video, audio) = active[i];
                    if (reader.Source.ConnectionList.Count == 0
                        && (reader.VideoTick == null || reader.VideoIdle) && (reader.AudioTick == null || reader.AudioIdle))
                    {
                        active.RemoveAt(i);
                        continue;
                    }
                    if (reader.VideoTick != null && Interlocked.Exchange(ref video.Queued, 1) == 0)
                        ThreadPool.UnsafeQueueUserWorkItem(video, preferLocal: false);
                    if (reader.AudioTick != null && Interlocked.Exchange(ref audio.Queued, 1) == 0)
                        ThreadPool.UnsafeQueueUserWorkItem(audio, preferLocal: false);
                }
            }
        }, null, 0, (int)PACING_WAKE_MS);

        return Task.CompletedTask;
    }

    // the units of a sample to its feed: of a list, by index - foreach of an IEnumerable boxed the list's enumerator, of
    //  each sample of each track
    private static void AddUnits(List<ReadOnlyMemory<byte>> feed, IEnumerable<ArraySegment<byte>> units)
    {
        if (units is IReadOnlyList<ArraySegment<byte>> list)
        {
            for (int i = 0; i < list.Count; i++)
                feed.Add(list[i]);
            return;
        }
        foreach (ArraySegment<byte> unit in units)
            feed.Add(unit);
    }

    // a track's tick, of the pool: its tick as it is now, of none once its stream has ended. Queued only of none queued and
    //  not run yet: of a pool behind, of each 10 ms a tick more of each track, and the pool's queue grew without end
    private sealed class TickWork(MediaFileReader reader, bool video) : IThreadPoolWorkItem
    {
        public int Queued;

        public void Execute()
        {
            Volatile.Write(ref Queued, 0);
            (video ? reader.VideoTick : reader.AudioTick)?.Invoke();
        }
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
    /// The most samples one wake-up will send.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Catching up is still the point, but not all at once. At 512 a track that fell behind - a
    /// wake-up that arrived late, the other track holding the lock through a read - sent everything
    /// it owed in a single burst, and the stream left here in bursts with pauses between them
    /// rather than paced. Measured at the far end of an RTSP to WebRTC proxy, frames arrived up to
    /// 2.3 seconds away from where their own timestamps put them.
    /// </para>
    /// <para>
    /// Four per wake-up at <see cref="PACING_WAKE_MS"/> is 400 samples a second, well over any
    /// frame rate this serves, so catching up still takes a fraction of a second - but spread over
    /// wake-ups instead of dumped into one. The same measurement drops to 0.49 seconds.
    /// </para>
    /// </remarks>
    private const int PACING_MAX_PER_WAKE = 4;

    // A track of no client since moved to where the clock says it plays now, of its timings rather than of reading it: the
    //  times round the file since counted off, then its last sample at or before - of the picture, its last keyframe, so a
    //  client joining has a picture at once, and the one after kept to read on at (MediaFileReader.VideoNextKeyFrame).
    //  Its playout time, as the track's sent through, back.
    private static double ResumeAtClock(MediaFileReader reader, VideoReader input, uint trackID, SampleTiming[] timings, uint timescale, bool keyFrame)
    {
        foreach (var timing in timings)
            reader.DurationSeconds = Math.Max(reader.DurationSeconds, (timing.PTS + timing.Duration) / (double)timescale);

        double at = reader.Clock.Elapsed.TotalSeconds - reader.LoopOffsetSeconds;
        if (reader.DurationSeconds > 0 && at >= reader.DurationSeconds)
        {
            double loops = Math.Floor(at / reader.DurationSeconds);
            reader.LoopOffsetSeconds += loops * reader.DurationSeconds;
            reader.FurthestSeconds = 0;
            at -= loops * reader.DurationSeconds;
        }

        int index = 0;
        for (int i = 0; i < timings.Length && timings[i].DTS <= at * timescale; i++)
        {
            if (!keyFrame || timings[i].IsSyncSample)
                index = i;
        }

        if (keyFrame)
        {
            // of none after it, the first of the next time round
            reader.VideoNextKeyFrame = timings.Length;
            for (int i = index + 1; i < timings.Length; i++)
            {
                if (timings[i].IsSyncSample)
                {
                    reader.VideoNextKeyFrame = i;
                    break;
                }
            }
        }

        input.SeekSample(trackID, (uint)index);
        double seconds = timings.Length > 0 ? timings[index].DTS / (double)timescale : 0;
        reader.FurthestSeconds = Math.Max(reader.FurthestSeconds, seconds);
        return reader.LoopOffsetSeconds + seconds;
    }

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
        _ticker?.Dispose();

        for (int i = 0; i < _mediaFileStreamReaders.Count; i++)
        {
            var stream = _mediaFileStreamReaders[i];
            stream.Dispose();
        }

        _mediaFileStreamReaders.Clear();

        _server?.Dispose();
    }
}
