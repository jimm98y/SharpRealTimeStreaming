using SharpMp4;
using SharpRTSPServer;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Timers;

// TODO config file
const string hostName = "127.0.0.1";
const ushort port = 8554;
const string fileName = "frag_bunny.mp4";
const string userName = "admin";
const string password = "password";

using (var server = new RTSPServer(port, userName, password))
{
    Dictionary<uint, IList<IList<byte[]>>> parsedMDAT;
    uint videoTrackId = 0;
    uint audioTrackId = 0;
    TrakBox audioTrackBox = null;
    TrakBox videoTrackBox = null;
    AudioSampleEntryBox audioSampleEntry = null;
    double videoFrameRate = 24;

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
            }
            
            if (audioTrackBox != null)
            {
                audioTrackId = fmp4.FindAudioTrackID().First();
                audioSampleEntry = audioTrackBox.GetAudioSampleEntryBox();
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
        var rtspVideoTrack = new SharpRTSPServer.H264Track();
        server.VideoTrack = rtspVideoTrack;
        videoTimer = new Timer(videoSampleDuration * 1000 / videoSamplingRate);
        videoTimer.Elapsed += (s, e) =>
        {
            if (videoIndex == 0)
            {
                rtspVideoTrack.SetParameterSets(videoTrack[0][0], videoTrack[0][1]);
                videoIndex++;
            }

            server.FeedInRawVideoSamples((uint)(videoIndex * videoSampleDuration), (List<byte[]>)videoTrack[videoIndex++ % videoTrack.Count]);

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
        var audioConfigDescriptor = audioSampleEntry.GetAudioSpecificConfigDescriptor();
        var audioSamplingRate = audioConfigDescriptor.GetSamplingFrequency();
        var rtspAudioTrack = new SharpRTSPServer.AACTrack(audioSamplingRate, audioConfigDescriptor.ChannelConfiguration);
        rtspAudioTrack.SetConfigDescriptor(await audioConfigDescriptor.ToBytes());
        server.AudioTrack = rtspAudioTrack;
        audioTimer = new Timer(audioSampleDuration * 1000 / audioSamplingRate);
        audioTimer.Elapsed += (s, e) =>
        {
            server.FeedInRawAudioSamples((uint)(audioIndex * audioSampleDuration), new List<byte[]>() { audioTrack[0][audioIndex++ % audioTrack[0].Count] });

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
