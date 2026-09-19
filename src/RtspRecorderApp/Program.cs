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
using System.IO;

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

            // The SDP may carry no fmtp at all, in which case the client reports the codec with no
            // configuration data. Recording cannot start without the parameter sets.
            switch(e.StreamType)
            {
                case "H264":
                    {
                        if (!(e.StreamConfigurationData is H264StreamConfigurationData config))
                        {
                            Console.WriteLine("Ignoring the H264 stream, the SDP carried no parameter sets.");
                            return;
                        }

                        outputTrack = new H264Track();
                        WriteParameterSets(outputTrack, config.SPS, config.PPS);
                    }
                    break;

                case "H265":
                    {
                        if (!(e.StreamConfigurationData is H265StreamConfigurationData config))
                        {
                            Console.WriteLine("Ignoring the H265 stream, the SDP carried no parameter sets.");
                            return;
                        }

                        outputTrack = new H265Track();
                        WriteParameterSets(outputTrack, config.VPS, config.SPS, config.PPS);
                    }
                    break;

                case "H266":
                    {
                        if (!(e.StreamConfigurationData is H266StreamConfigurationData config))
                        {
                            Console.WriteLine("Ignoring the H266 stream, the SDP carried no parameter sets.");
                            return;
                        }

                        outputTrack = new H266Track();
                        WriteParameterSets(outputTrack, config.DCI, config.VPS, config.SPS, config.PPS, config.SEI);
                    }
                    break;

                case "AV1":
                    {
                        outputTrack = new AV1Track();
                    }
                    break;

                default:
                    Console.WriteLine($"Ignoring the {e.StreamType} video stream, it cannot be recorded to MP4.");
                    return;
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
                    }
                    break;

                default:
                    Console.WriteLine($"Ignoring the {e.StreamType} audio stream, it cannot be recorded to MP4.");
                    return;
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
