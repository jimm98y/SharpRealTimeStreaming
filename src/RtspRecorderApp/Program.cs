using Microsoft.Extensions.Configuration;
using SharpMP4.Builders;
using SharpMP4.Tracks;
using SharpRTSPClient;
using System;
using System.IO;

SharpH26X.Log.SinkDebug = (o, e) => { };
SharpH26X.Log.SinkInfo = (o, e) => { };
SharpAV1.Log.SinkInfo = (o, e) => { };
SharpAV1.Log.SinkDebug = (o, e) => { };
SharpISOBMFF.Log.SinkInfo = (o, e) => { };
SharpISOBMFF.Log.SinkDebug = (o, e) => { };
SharpMP4.Log.SinkInfo = (o, e) => { };
SharpMP4.Log.SinkDebug = (o, e) => { };

IConfiguration config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
string rtspUri = config["Uri"];
string userName = config["UserName"];
string password = config["Password"];

using (Stream output = new BufferedStream(new FileStream("recording_out.mp4", FileMode.Create, FileAccess.Write, FileShare.Read)))
{
    IMp4Builder outputBuilder = new FragmentedMp4Builder(new SingleStreamOutput(output), 2000);

    using (RTSPClient client = new RTSPClient())
    {
        uint videoTrackID = 0;
        uint audioTrackID = 0;
        client.NewVideoStream += (sender, e) =>
        {
            ITrack outputTrack;

            switch(e.StreamType)
            {
                case "H264":
                    {
                        var config = e.StreamConfigurationData as H264StreamConfigurationData;
                        outputTrack = new H264Track();
                        outputTrack.ProcessSample(config.SPS, out _, out _);
                        outputTrack.ProcessSample(config.PPS, out _, out _);
                    }
                    break;

                case "H265":
                    {
                        var config = e.StreamConfigurationData as H265StreamConfigurationData;
                        outputTrack = new H265Track();
                        outputTrack.ProcessSample(config.VPS, out _, out _);
                        outputTrack.ProcessSample(config.SPS, out _, out _);
                        outputTrack.ProcessSample(config.PPS, out _, out _);
                    }
                    break;

                case "H266":
                    {
                        var config = e.StreamConfigurationData as H266StreamConfigurationData;
                        outputTrack = new H266Track();
                        outputTrack.ProcessSample(config.DCI, out _, out _);
                        outputTrack.ProcessSample(config.VPS, out _, out _);
                        outputTrack.ProcessSample(config.SPS, out _, out _);
                        outputTrack.ProcessSample(config.PPS, out _, out _);
                        outputTrack.ProcessSample(config.SEI, out _, out _);
                    }
                    break;

                case "AV1":
                    {
                        outputTrack = new AV1Track();
                    }
                    break;

                default:
                    throw new NotSupportedException();
            }

            outputBuilder.AddTrack(outputTrack);
            videoTrackID = outputTrack.TrackID;
        };

        client.ReceivedVideoData += (sender, e) =>
        {
            foreach (var unit in e.Data)
            {
                var sample = unit.ToArray();
                outputBuilder.ProcessTrackSample(videoTrackID, sample);
            }
        };

        client.NewAudioStream += (sender, e) =>
        {
            ITrack outputTrack;

            switch (e.StreamType)
            {
                case "AAC":
                    {
                        AACStreamConfigurationData config = e.StreamConfigurationData as AACStreamConfigurationData;
                        uint samplingFrequency = (uint)config.SamplingFrequency;
                        if (samplingFrequency == 0)
                        {
                            samplingFrequency = SharpISOBMFF.AudioSpecificConfigDescriptor.SamplingFrequencyMap[(uint)config.FrequencyIndex];
                        }
                        outputTrack = new AACTrack((byte)config.ChannelConfiguration, samplingFrequency, 16);
                    }
                    break;

                default:
                    throw new NotSupportedException();
            }

            outputBuilder.AddTrack(outputTrack);
            audioTrackID = outputTrack.TrackID;
        };

        client.ReceivedAudioData += (sender, e) =>
        {
            foreach (var unit in e.Data)
            {
                var sample = unit.ToArray();
                outputBuilder.ProcessTrackSample(audioTrackID, sample);
            }
        };

        client.Stopped += (sender, e) =>
        {
            Console.WriteLine("Stopped");
            client.TryReconnect();
        };

        client.Connect(rtspUri, RTPTransport.TCP, userName, password, MediaRequest.VIDEO_AND_AUDIO, false, null, true);

        Console.WriteLine("Press any key to exit");
        while (!Console.KeyAvailable)
        {
            System.Threading.Thread.Sleep(250);
        }

        client.Stop();
    }

    outputBuilder.FinalizeMedia();
}
