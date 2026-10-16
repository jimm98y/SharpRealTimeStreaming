// SharpRTSPClient
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
using SharpMP4.Builders;
using SharpMP4.Tracks;
using SharpRTSPClient;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

IConfiguration config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
string rtspUri = config["Uri"];
string userName = config["UserName"];
string password = config["Password"];

using (Stream output = new BufferedStream(new FileStream("recording_out.mp4", FileMode.Create, FileAccess.Write, FileShare.Read)))
{
    IMp4Builder outputBuilder = new FragmentedMp4Builder(new SingleStreamOutput(output), 2000);

    using (RtspClient client = new RtspClient())
    {
        // One handler for every track the stream offers, rather than a pair per kind. The client
        // reports which track each frame came from, so the mapping from its tracks to the output
        // file's is just a lookup - and a stream carrying two qualities or two languages records
        // all of them instead of the first of each.
        var timingOfClientTrack = new Dictionary<int, RtpSampleTiming>();

        // The client may report the tracks' frames from threads of their own, and the builder takes
        // one sample at a time.
        object builderLock = new object();

        client.NewTrack += (sender, e) =>
        {
            ITrack outputTrack;

            // What the RTP timestamps count in. Every video payload's is 90 kHz where the rtpmap does
            // not say; an audio one's is its sampling rate, as the AAC case below fills in.
            int clockRate = e.ClockRate > 0 ? e.ClockRate : e.Kind == TrackKind.Video ? 90000 : 0;

            // The SDP may carry no parameter sets, in which case the client reports a configuration
            // without them. Recording cannot start without the parameter sets.
            switch (e.Codec)
            {
                case "H264":
                    {
                        if (!(e.StreamConfigurationData is H264StreamConfigurationData config) || config.SPS == null || config.PPS == null)
                        {
                            Console.WriteLine("Ignoring the H264 stream, the SDP carried no parameter sets.");
                            return;
                        }

                        outputTrack = new H264Track() { TimescaleOverride = (uint)clockRate };
                        WriteParameterSets(outputTrack, config.SPS, config.PPS);
                    }
                    break;

                case "H265":
                    {
                        if (!(e.StreamConfigurationData is H265StreamConfigurationData config) || config.SPS == null || config.PPS == null)
                        {
                            Console.WriteLine("Ignoring the H265 stream, the SDP carried no parameter sets.");
                            return;
                        }

                        outputTrack = new H265Track() { TimescaleOverride = (uint)clockRate };
                        WriteParameterSets(outputTrack, config.VPS, config.SPS, config.PPS);
                    }
                    break;

                case "H266":
                    {
                        if (!(e.StreamConfigurationData is H266StreamConfigurationData config) || config.SPS == null || config.PPS == null)
                        {
                            Console.WriteLine("Ignoring the H266 stream, the SDP carried no parameter sets.");
                            return;
                        }

                        outputTrack = new H266Track() { TimescaleOverride = (uint)clockRate };
                        WriteParameterSets(outputTrack, config.DCI, config.VPS, config.SPS, config.PPS, config.SEI);
                    }
                    break;

                case "AV1":
                    {
                        outputTrack = new AV1Track() { TimescaleOverride = (uint)clockRate };
                    }
                    break;

                case "AAC":
                    {
                        if (!(e.StreamConfigurationData is AACStreamConfigurationData config))
                        {
                            Console.WriteLine("Ignoring the AAC stream, the SDP carried no audio configuration.");
                            return;
                        }

                        uint samplingFrequency = (uint)config.SamplingFrequency;
                        if (samplingFrequency == 0)
                        {
                            samplingFrequency = SharpISOBMFF.AudioSpecificConfigDescriptor.SamplingFrequencyMap[(uint)config.FrequencyIndex];
                        }

                        outputTrack = new AACTrack((byte)config.ChannelConfiguration, samplingFrequency, 16);
                        if (clockRate == 0)
                        {
                            clockRate = (int)samplingFrequency;
                        }
                    }
                    break;

                default:
                    Console.WriteLine($"Ignoring the {e.Codec} {e.Kind} stream, it cannot be recorded to MP4.");
                    return;
            }

            lock (builderLock)
            {
                outputBuilder.AddTrack(outputTrack);
                timingOfClientTrack[e.TrackIndex] = new RtpSampleTiming(outputTrack, clockRate, isVideo: e.Kind == TrackKind.Video);
            }
        };

        client.ReceivedData += (sender, e) =>
        {
            lock (builderLock)
            {
                // A track whose codec could not be recorded never got an output track, so its frames
                // are dropped rather than written to whichever track happens to be there.
                if (!timingOfClientTrack.TryGetValue(e.TrackIndex, out RtpSampleTiming timing))
                {
                    return;
                }

                timing.Add(outputBuilder, e.Data.RtpTimestamp, Stopwatch.GetTimestamp(), e.Data.Data);
            }
        };

        client.Stopped += (sender, e) =>
        {
            Console.WriteLine("Stopped");
            client.TryReconnect();
        };

        // Every track the stream offers; the ones this recorder has no MP4 track for are dropped
        // in NewTrack above.
        client.AcceptTrack = _ => true;

        client.Connect(rtspUri, RtpTransport.TCP, userName, password, false, null, true);

        Console.WriteLine("Press any key to exit");
        // Asking whether a key has been pressed throws outright when there is no console, or when
        // its input comes from somewhere other than a keyboard - which is what happens when one of
        // these is run from a script. So it waits for the end of that input instead, which arrives
        // when whatever started it goes away.
        if (Console.IsInputRedirected)
        {
            Console.In.ReadToEnd();
        }
        else
        {
            while (!Console.KeyAvailable)
            {
                System.Threading.Thread.Sleep(250);
            }
        }

        client.Stop();

        // The last frame of each track, which no frame came after to time it.
        lock (builderLock)
        {
            foreach (var timing in timingOfClientTrack.Values)
            {
                timing.Flush(outputBuilder);
            }
        }
    }

    outputBuilder.FinalizeMedia();
}

// Parameter sets are optional in the SDP, so only the ones the camera actually sent are written.
static void WriteParameterSets(ITrack track, params byte[][] parameterSets)
{
    foreach (byte[] parameterSet in parameterSets)
    {
        if (parameterSet != null && parameterSet.Length > 0)
        {
            track.ProcessSample(parameterSet, out _, out _);
        }
    }
}

/// <summary>
/// Writes the frames of one track with the timing their RTP timestamps give, rather than the
/// track's default.
/// </summary>
/// <remarks>
/// An RTP timestamp is when a frame is shown, and frames arrive in the order they are decoded. Where
/// a video stream has B-frames the two differ: the decode times are the same timestamps put in order,
/// the i-th frame to arrive decoded at the i-th smallest. So the frames wait in a window long enough
/// for every timestamp a frame comes before to have arrived, and each is written with how far the next
/// decode time is from its own as its duration, and how far it is shown from its decode time as its
/// composition offset.
/// <para>
/// Audio is decoded in the order it is shown, so its frames are put back in that order as they wait,
/// should the transport have swapped any. A frame later than the window, whose time is already
/// written, is dropped: written anyway, it would push every frame after it a frame later.
/// </para>
/// </remarks>
sealed class RtpSampleTiming
{
    // Frames a video frame can come ahead of the earliest one it is shown after. B-pyramids reach 3
    // or 4; this leaves room.
    private const int VideoReorderWindow = 16;

    // Frames of audio the transport can swap and still have them put back in order.
    private const int AudioReorderWindow = 8;

    // A step between two frames' timestamps longer than this is not one frame to the next, and how far
    // apart they arrived is taken instead. A server starting a client on the last keyframe it kept sends
    // it with the timestamp it had then, well before the live frames that follow; and a sender setting
    // the stream up again starts its clock anywhere.
    private const int LargeStepSeconds = 2;

    private sealed class Frame
    {
        public List<byte[]> Units;
        public long PresentationTime;
    }

    private readonly ITrack _track;
    private readonly int _clockRate;
    private readonly bool _isVideo;
    private readonly int _window;

    // The frames waiting, video in the order it arrived and audio in the order it is shown; and the
    // presentation times of those not yet given out as a decode time, the smallest first.
    private readonly List<Frame> _frames = new List<Frame>();
    private readonly PriorityQueue<long, long> _decodeTimes = new PriorityQueue<long, long>();

    // The decode time of the last frame written, which every frame after it must come after.
    private long _lastDecodeTime;
    private bool _hasWritten;

    // The RTP timestamp of the last frame to arrive, where it lies on a clock that does not wrap, and
    // when it arrived; and the latest presentation time so far, which a frame after a large step
    // follows.
    private uint _lastRtpTimestamp;
    private long _lastPresentationTime;
    private long _lastArrival;
    private long _maxPresentationTime;
    private bool _hasTime;

    // What the conversion from the RTP clock to the track's timescale left over, so that rounding
    // does not add up to drift.
    private long _remainder;
    private long _lastDuration = -1;

    public RtpSampleTiming(ITrack track, int clockRate, bool isVideo)
    {
        _track = track;
        _clockRate = clockRate;
        _isVideo = isVideo;
        _window = isVideo ? VideoReorderWindow : AudioReorderWindow;
    }

    /// <summary>
    /// Takes a frame, and writes those whose timing is now known.
    /// </summary>
    /// <param name="arrival">When the frame arrived, in <see cref="Stopwatch"/> ticks.</param>
    public void Add(IMp4Builder builder, uint rtpTimestamp, long arrival, IEnumerable<ReadOnlyMemory<byte>> units)
    {
        long presentationTime = 0;
        if (_hasTime)
        {
            // Unchecked, as the timestamp wraps around after 2^32 ticks.
            int step = unchecked((int)(rtpTimestamp - _lastRtpTimestamp));
            if (Math.Abs((long)step) > (long)_clockRate * LargeStepSeconds)
            {
                // The timestamps say nothing of how far apart the two frames are, and the time between
                // their arrivals is all there is. Measured from the latest frame so far, as the frames
                // still waiting to be put in order are all before this one.
                long arrived = (long)((double)(arrival - _lastArrival) / Stopwatch.Frequency * _clockRate);
                presentationTime = _maxPresentationTime + Math.Max(1, arrived);
            }
            else
            {
                presentationTime = _lastPresentationTime + step;
            }
        }

        _maxPresentationTime = _hasTime ? Math.Max(_maxPresentationTime, presentationTime) : presentationTime;
        _hasTime = true;
        _lastRtpTimestamp = rtpTimestamp;
        _lastPresentationTime = presentationTime;
        _lastArrival = arrival;

        long decodeTime = presentationTime;
        if (_hasWritten && presentationTime <= _lastDecodeTime)
        {
            if (!_isVideo)
            {
                return;
            }

            // A picture is decoded in the order it came, so it still goes in, decoded straight after
            // the last one written.
            decodeTime = _lastDecodeTime + 1;
        }

        // The client reuses its buffers, so the frame is copied out of them while it waits.
        var frame = new Frame { Units = units.Select(unit => unit.ToArray()).ToList(), PresentationTime = presentationTime };
        int at = _frames.Count;
        while (!_isVideo && at > 0 && _frames[at - 1].PresentationTime > presentationTime)
        {
            at--;
        }

        _frames.Insert(at, frame);
        _decodeTimes.Enqueue(decodeTime, decodeTime);

        // A frame is written once the next decode time is known too, which needs the window full
        // behind it.
        while (_frames.Count > _window)
        {
            WriteNext(builder);
        }
    }

    /// <summary>
    /// Writes the frames still waiting, the last with the duration of the one before it.
    /// </summary>
    public void Flush(IMp4Builder builder)
    {
        while (_frames.Count > 0)
        {
            WriteNext(builder);
        }
    }

    private void WriteNext(IMp4Builder builder)
    {
        Frame frame = _frames[0];
        _frames.RemoveAt(0);
        long decodeTime = _decodeTimes.Dequeue();
        _lastDecodeTime = decodeTime;
        _hasWritten = true;

        long duration = _decodeTimes.TryPeek(out long nextDecodeTime, out _) ? nextDecodeTime - decodeTime : _lastDuration;
        if (duration < 0)
        {
            duration = _lastDuration;
        }

        Write(builder, frame, ToTrackTime(duration), (int)Rescale(frame.PresentationTime - decodeTime));
        _lastDuration = duration;
    }

    private int ToTrackTime(long rtpTicks)
    {
        if (rtpTicks < 0 || _clockRate <= 0 || _track.Timescale == 0 || _track.Timescale == _clockRate)
        {
            return (int)rtpTicks;
        }

        long ticks = rtpTicks * _track.Timescale + _remainder;
        _remainder = ticks % _clockRate;
        return (int)(ticks / _clockRate);
    }

    private long Rescale(long rtpTicks) =>
        _clockRate <= 0 || _track.Timescale == 0 || _track.Timescale == _clockRate ? rtpTicks : rtpTicks * _track.Timescale / _clockRate;

    private void Write(IMp4Builder builder, Frame frame, int duration, int compositionOffset)
    {
        if (_isVideo)
        {
            // Every unit of the picture is given its timing: the builder writes the picture with it.
            // The client hands out H.264, H.265 and H.266 NAL units behind their start codes.
            foreach (byte[] unit in frame.Units)
            {
                if (_track is H26XTrackBase)
                {
                    builder.ProcessAnnexBTrackSample(_track.TrackID, unit, duration, compositionOffset);
                }
                else
                {
                    builder.ProcessTrackSample(_track.TrackID, unit, duration, compositionOffset);
                }
            }
        }
        else
        {
            // An audio frame's units are access units of their own, which the packet carried
            // together: the frame's duration is shared out among them, the last given what the
            // division leaves.
            int count = frame.Units.Count;
            int each = duration < 0 ? -1 : duration / count;
            for (int i = 0; i < count; i++)
            {
                int unitDuration = duration < 0 ? -1 : i == count - 1 ? duration - each * (count - 1) : each;
                builder.ProcessTrackSample(_track.TrackID, frame.Units[i], unitDuration);
            }
        }
    }
}

