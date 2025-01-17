using Microsoft.Extensions.Configuration;
using SharpMp4;
using SharpRTSPServer;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Timers;

IConfiguration config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
string hostName = config["HostName"];
ushort port = ushort.Parse(config["Port"]);
string fileName = config["FilePath"];
string userName = config["UserName"];
string password = config["Password"];

using (var server = new RTSPServer(port, userName, password))
{
    Dictionary<uint, IList<IList<byte[]>>> parsedMDAT;
    uint videoTrackId = 0;
    uint audioTrackId = 0;
    TrakBox audioTrackBox = null;
    TrakBox videoTrackBox = null;
    double videoFrameRate = 0;

    ITrack rtspVideoTrack = null;
    ITrack rtspAudioTrack = null;

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
                    server.AddVideoTrack(rtspVideoTrack);
                }
                else
                {
                    var h265VisualSample = videoTrackBox.GetMdia().GetMinf().GetStbl().GetStsd().Children.FirstOrDefault(x => x.Type == VisualSampleEntryBox.TYPE6 || x.Type == VisualSampleEntryBox.TYPE7) as VisualSampleEntryBox;
                    if (h265VisualSample != null)
                    {
                        rtspVideoTrack = new SharpRTSPServer.H265Track();
                        server.AddVideoTrack(rtspVideoTrack);
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
                    server.AddAudioTrack(rtspAudioTrack);
                }
                else
                {
                    // unsupported audio
                }
            }
        }
    }

    int videoIndex = 0;
    int audioIndex = 0;
    Timer audioTimer = null;
    Timer videoTimer = null;

    if (videoTrackBox != null)
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
                }
                else if (rtspVideoTrack is SharpRTSPServer.H265Track h265VideoTrack)
                {
                    h265VideoTrack.SetParameterSets(videoTrack[0][0], videoTrack[0][1], videoTrack[0][2]);
                }
                videoIndex++;
            }

            rtspVideoTrack.FeedInRawSamples((uint)(videoIndex * videoSampleDuration), CreateReadOnlySequence(videoTrack[videoIndex++ % videoTrack.Count]));

            if (videoIndex % videoTrack.Count == 0)
            {
                Reset(ref videoIndex, videoTimer, ref audioIndex, audioTimer);
            }
        };
    }

    if (audioTrackBox != null)
    {
        var audioSampleDuration = SharpMp4.AACTrack.AAC_SAMPLE_SIZE;
        var audioTrack = parsedMDAT[audioTrackId];
        audioTimer = new Timer(audioSampleDuration * 1000 / (rtspAudioTrack as SharpRTSPServer.AACTrack).SamplingRate);
        audioTimer.Elapsed += (s, e) =>
        {
            rtspAudioTrack.FeedInRawSamples((uint)(audioIndex * audioSampleDuration), new ReadOnlySequence<byte>(audioTrack[0][audioIndex++ % audioTrack[0].Count]));

            if (audioIndex % audioTrack[0].Count == 0)
            {
                Reset(ref videoIndex, videoTimer, ref audioIndex, audioTimer);
            }
        };
    }

    try
    {
        server.StartListen();
    }
    catch (Exception ex)
    {
        Console.WriteLine(ex.ToString());
    }

    Console.WriteLine($"RTSP URL is rtsp://{userName}:{password}@{hostName}:{port}");

    videoTimer?.Start();
    audioTimer?.Start();

    Console.WriteLine("Press any key to exit");
    while (!Console.KeyAvailable)
    {
        System.Threading.Thread.Sleep(250);
    }
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


static ReadOnlySequence<byte> CreateReadOnlySequence(IList<byte[]> byteArrayList)
{
    if (byteArrayList is not { Count: > 0 })
    {
        return ReadOnlySequence<byte>.Empty;
    }

    if (byteArrayList.Count == 1)
    {
        return new ReadOnlySequence<byte>(byteArrayList[0]);
    }

    var firstSegment = new SequenceSegment(new ReadOnlyMemory<byte>(byteArrayList[0]));
    var lastSegment = firstSegment;

    for (var i = 1; i < byteArrayList.Count; i++)
    {
        var nextSegment = new SequenceSegment(new ReadOnlyMemory<byte>(byteArrayList[i]));
        lastSegment.Append(nextSegment);
        lastSegment = nextSegment;
    }

    return new ReadOnlySequence<byte>(firstSegment, 0, lastSegment, lastSegment.Memory.Length);
}

internal class SequenceSegment : ReadOnlySequenceSegment<byte>
{
    public SequenceSegment(ReadOnlyMemory<byte> memory)
    {
        Memory = memory;
        RunningIndex = 0;
    }

    public void Append(SequenceSegment next)
    {
        Next = next;
        next.RunningIndex = RunningIndex + Memory.Length;
    }
}