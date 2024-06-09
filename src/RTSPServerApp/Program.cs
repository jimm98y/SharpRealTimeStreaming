
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
    AudioSampleEntryBox audioTrackInfo;

    using (Stream fs = new BufferedStream(new FileStream("frag_bunny.mp4", FileMode.Open, FileAccess.Read, FileShare.Read)))
    {
        using (var fmp4 = await FragmentedMp4.ParseAsync(fs))
        {
            videoTrackId = fmp4.FindVideoTrackID().First();
            audioTrackId = fmp4.FindAudioTrackID().First();
            parsedMDAT = await fmp4.ParseMdatAsync();
            audioTrackInfo = fmp4.FindAudioTracks().First().GetAudioSampleEntryBox();
        }
    }

    uint videoSampleDuration = 90000 / 24; // 24 fps
    uint audioSampleDuration = 1024;
    var videoTrack = parsedMDAT[videoTrackId];
    int videoIndex = 0;

    var audioTrack = parsedMDAT[audioTrackId];
    int audioIndex = 0;

    Timer _videoTimer = new Timer(videoSampleDuration * 1000 / 90000);
    Timer _audioTimer = new Timer(audioSampleDuration * 1000 / 22050);

    var esdsBox = audioTrackInfo.Children[0] as EsdsBox;
    var decoderConfigDescriptor = esdsBox.ESDescriptor.Descriptors.Single(x => x is DecoderConfigDescriptor) as DecoderConfigDescriptor;
    var audioConfigDescriptor = decoderConfigDescriptor.AudioSpecificConfig;
    string strAudioConfigDescriptor;
    using (var ms = new MemoryStream())
    {
        await AudioSpecificConfigDescriptor.BuildAsync(ms, AudioSpecificConfigDescriptor.OBJECT_TYPE_INDICATION, 0, audioConfigDescriptor);
        strAudioConfigDescriptor = Utilities.ToHexString(ms.ToArray());
    }

    _videoTimer.Elapsed += (s, e) =>
    {
        if (videoIndex == 0)
        {
            server.FeedInRawSPSandPPS(videoTrack[0][0], videoTrack[0][1]);
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