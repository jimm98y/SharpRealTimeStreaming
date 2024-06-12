
using SharpMp4;
using SharpRTSPServer;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Timers;

const string userName = "admin";
const string password = "password";
const string hostName = "127.0.0.1";
const string fileName = "frag_bunny.mp4";
const ushort port = 8554;

using (var server = new RTSPServer(port, userName, password))
{
    try
    {
        server.StartListen();
    }
    catch (Exception ex)
    {
        Console.WriteLine(ex.ToString());
    }

    Console.WriteLine($"RTSP URL is rtsp://{userName}:{password}@{hostName}:{port}");

    Dictionary<uint, IList<IList<byte[]>>> parsedMDAT;
    uint videoTrackId = 0;
    uint audioTrackId = 0;
    TrakBox trackBoxAAC = null;
    TrakBox trackBoxH264 = null;
    AudioSampleEntryBox audioSampleEntry;
    double videoFrameRate = 24;

    // frag_bunny.mp4 audio is not playable in VLC on Windows 11 (works on MacOS)
    using (Stream fs = new BufferedStream(new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read)))
    {
        using (var fmp4 = await FragmentedMp4.ParseAsync(fs))
        {
            videoTrackId = fmp4.FindVideoTrackID().First();
            audioTrackId = fmp4.FindAudioTrackID().First();
            parsedMDAT = await fmp4.ParseMdatAsync();

            trackBoxAAC = fmp4.FindAudioTracks().First();
            audioSampleEntry = trackBoxAAC.GetAudioSampleEntryBox();

            trackBoxH264 = fmp4.FindVideoTracks().First();
            videoFrameRate = fmp4.CalculateFrameRate(trackBoxH264);
        }
    }

    int videoSamplingRate = SharpRTSPServer.H264Track.DEFAULT_CLOCK;
    uint videoSampleDuration = (uint)(videoSamplingRate / videoFrameRate); 
    uint audioSampleDuration = SharpMp4.AACTrack.AAC_SAMPLE_SIZE;
    var videoTrack = parsedMDAT[videoTrackId];
    int videoIndex = 0;

    var audioTrack = parsedMDAT[audioTrackId];
    int audioIndex = 0;
    var audioConfigDescriptor = audioSampleEntry.GetAudioSpecificConfigDescriptor();
    int audioSamplingRate = audioConfigDescriptor.GetSamplingFrequency();

    Timer _videoTimer = new Timer(videoSampleDuration * 1000 / videoSamplingRate);
    Timer _audioTimer = new Timer(audioSampleDuration * 1000 / audioSamplingRate);

    var rtspVideoTrack = new SharpRTSPServer.H264Track();
    var rtspAudioTrack = new SharpRTSPServer.AACTrack(audioSamplingRate, audioConfigDescriptor.ChannelConfiguration, await audioConfigDescriptor.ToBytes());
    server.VideoTrack = rtspVideoTrack;
    server.AudioTrack = rtspAudioTrack;

    _videoTimer.Elapsed += (s, e) =>
    {
        if (videoIndex == 0)
        {
            rtspVideoTrack.SetSPS_PPS(videoTrack[0][0], videoTrack[0][1]);
            videoIndex++;
        }

        server.FeedInRawNAL((uint)videoIndex * videoSampleDuration, (List<byte[]>)videoTrack[videoIndex++ % videoTrack.Count]);

        if (videoIndex % videoTrack.Count == 0)
        {
            Reset(out videoIndex, out audioIndex, _videoTimer, _audioTimer);
        }
    };
    _audioTimer.Elapsed += (s, e) =>
    {
        server.FeedInAudioPacket((uint)audioIndex * audioSampleDuration, SharpRTSPServer.AACTrack.AppendAUHeader(audioTrack[0][audioIndex++ % audioTrack[0].Count]));

        if (audioIndex % audioTrack[0].Count == 0)
        {
            Reset(out videoIndex, out audioIndex, _videoTimer, _audioTimer);
        }
    };

    _videoTimer.Start();
    _audioTimer.Start();

    Console.WriteLine("Press any key to exit");
    while (!Console.KeyAvailable)
    {
        System.Threading.Thread.Sleep(250); 
    }
}

static void Reset(out int videoIndex, out int audioIndex, Timer _videoTimer, Timer _audioTimer)
{
    _videoTimer.Stop();
    _audioTimer.Stop();
    videoIndex = 0;
    audioIndex = 0;
    _videoTimer.Start();
    _audioTimer.Start();
}
