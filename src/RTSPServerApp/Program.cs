
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
    AudioSampleEntryBox sourceAudioTrackInfo;

    using (Stream fs = new BufferedStream(new FileStream("frag_bunny.mp4", FileMode.Open, FileAccess.Read, FileShare.Read)))
    {
        using (var fmp4 = await FragmentedMp4.ParseAsync(fs))
        {
            videoTrackId = fmp4.FindVideoTrackID().First();
            audioTrackId = fmp4.FindAudioTrackID().First();
            parsedMDAT = await fmp4.ParseMdatAsync();
            sourceAudioTrackInfo = fmp4.FindAudioTracks().First().GetAudioSampleEntryBox();
        }
    }

    uint videoSampleDuration = (uint)(90000d / 24d);
    uint audioSampleDuration = 1024;
    var videoTrack = parsedMDAT[videoTrackId];
    int videoIndex = 0;

    var audioTrack = parsedMDAT[audioTrackId];
    int audioIndex = 0;

    Timer _videoTimer = new Timer(videoSampleDuration / 90);
    Timer _audioTimer = new Timer((uint)(audioSampleDuration / 22.05d));

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
            _videoTimer.Stop();
            _audioTimer.Stop();

            videoIndex = 0;
            audioIndex = 0;

            _videoTimer.Start();
            _audioTimer.Start();
        }
    };
    _audioTimer.Elapsed += (s, e) =>
    {
        var frame = audioTrack[0][audioIndex++ % audioTrack[0].Count];
        // add AU header
        short frameLen = (short)(frame.Length << 3);
        byte[] header = new byte[4];
        header[0] = 0x00;
        header[1] = 0x10;
        header[2] = (byte)((frameLen >> 8) & 0xFF);
        header[3] = (byte)(frameLen & 0xFF);
        server.FeedInAudioPacket((uint)audioIndex * audioSampleDuration, header.Concat(frame).ToArray());

        if (videoIndex % videoTrack.Count == 0)
        {
            _videoTimer.Stop();
            _audioTimer.Stop();

            videoIndex = 0;
            audioIndex = 0;

            _videoTimer.Start();
            _audioTimer.Start();
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
