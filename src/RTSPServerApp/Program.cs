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
    double videoFrameRate = 0;
    int audioSamplingRate = 0;

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
                    server.VideoTrack = new SharpRTSPServer.H264Track(avcC.AvcProfileIndication, 0, avcC.AvcLevelIndication);
                }
                else
                {
                    var h265VisualSample = videoTrackBox.GetMdia().GetMinf().GetStbl().GetStsd().Children.FirstOrDefault(x => x.Type == VisualSampleEntryBox.TYPE6 || x.Type == VisualSampleEntryBox.TYPE7) as VisualSampleEntryBox;
                    if(h265VisualSample != null)
                    {
                        server.VideoTrack = new SharpRTSPServer.H265Track();
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
                audioSampleEntry = audioTrackBox.GetAudioSampleEntryBox();

                var aac = audioTrackBox.GetMdia().GetMinf().GetStbl().GetStsd().Children.FirstOrDefault(x => x.Type == AudioSampleEntryBox.TYPE3) as AudioSampleEntryBox;
                if (aac != null)
                {
                    var audioConfigDescriptor = audioSampleEntry.GetAudioSpecificConfigDescriptor();
                    audioSamplingRate = audioConfigDescriptor.GetSamplingFrequency();
                    server.AudioTrack = new SharpRTSPServer.AACTrack(await audioConfigDescriptor.ToBytes(), audioSamplingRate, audioConfigDescriptor.ChannelConfiguration);
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
                if (server.VideoTrack is SharpRTSPServer.H264Track h264VideoTrack)
                {
                    h264VideoTrack.SetParameterSets(videoTrack[0][0], videoTrack[0][1]);
                }
                else if (server.VideoTrack is SharpRTSPServer.H265Track h265VideoTrack)
                {
                    h265VideoTrack.SetParameterSets(videoTrack[0][0], videoTrack[0][1], videoTrack[0][2]);
                }
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
