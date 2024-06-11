
using SharpMp4;
using SharpRTSPServer;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Timers;

const string userName = "admin";
const string password = "password";
const string hostName = "localhost";
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
    SharpMp4.TrakBox trackBoxAAC = null;
    SharpMp4.TrakBox trackBoxH264 = null;
    AudioSampleEntryBox audioTrackInfo;

    // frag_bunny.mp4 audio is not playable in VLC on Windows 11 (works on MacOS)
    using (Stream fs = new BufferedStream(new FileStream("frag_bunny.mp4", FileMode.Open, FileAccess.Read, FileShare.Read)))
    {
        using (var fmp4 = await FragmentedMp4.ParseAsync(fs))
        {
            videoTrackId = fmp4.FindVideoTrackID().First();
            audioTrackId = fmp4.FindAudioTrackID().First();
            parsedMDAT = await fmp4.ParseMdatAsync();

            trackBoxAAC = fmp4.FindAudioTracks().First();
            trackBoxH264 = fmp4.FindVideoTracks().First();
            audioTrackInfo = trackBoxAAC.GetAudioSampleEntryBox();
        }
    }

    uint videoSampleDuration = SharpRTSPServer.H264Track.DEFAULT_CLOCK / 24; // TODO 24 fps = timescale / sampleDuration = 600 / 25
    uint audioSampleDuration = SharpMp4.AACTrack.AAC_SAMPLE_SIZE;
    var videoTrack = parsedMDAT[videoTrackId];
    int videoIndex = 0;

    var audioTrack = parsedMDAT[audioTrackId];
    int audioIndex = 0;

    var audioConfigDescriptor = audioTrackInfo.GetAudioSpecificConfigDescriptor();
    int audioSamplingRate = 22050; // audioConfigDescriptor.GetSamplingFrequency();

    Timer _videoTimer = new Timer(videoSampleDuration * 1000 / videoSampleDuration);
    Timer _audioTimer = new Timer(audioSampleDuration * 1000 / audioSamplingRate);

    server.VideoTrack = new SharpRTSPServer.H264Track();
    server.AudioTrack = new SharpRTSPServer.AACTrack(audioSamplingRate, audioConfigDescriptor.ChannelConfiguration, await audioConfigDescriptor.ToBytes());

    _videoTimer.Elapsed += (s, e) =>
    {
        if (videoIndex == 0)
        {
            (server.VideoTrack as SharpRTSPServer.H264Track).SetSPS_PPS(videoTrack[0][0], videoTrack[0][1]);
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
        server.FeedInAACPacket((uint)audioIndex * audioSampleDuration, audioTrack[0][audioIndex++ % audioTrack[0].Count]);

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
