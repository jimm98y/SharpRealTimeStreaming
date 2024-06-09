
using SharpMp4;
using SharpRTSPServer;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

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

    var streamingTask = Task.Run(async () =>
    {
        while (true)
        {
            DateTime started = DateTime.Now;
            var videoStreaming = Task.Run(async () =>
            {
                DateTime videoStarted = DateTime.Now;
                uint elapsed = 0;
                var videoTrack = parsedMDAT[videoTrackId];
                uint sampleDuration = (uint)(90000d / 24d);

                server.FeedInRawSPSandPPS(videoTrack[0][0], videoTrack[0][1]);

                foreach (var au in videoTrack)
                {
                    server.FeedInRawNAL(elapsed, (List<byte[]>)au);
                    elapsed += sampleDuration;
                    await Task.Delay((int)(sampleDuration / 90));
                }

                Console.WriteLine($"Finished video {DateTime.Now.Subtract(videoStarted)}");
            });

            var audioStreaming = Task.Run(async () =>
            {
                DateTime audioStarted = DateTime.Now;
                uint elapsed = 0;
                var audioTrack = parsedMDAT[audioTrackId];
                uint sampleDuration = 1024;
                byte[] header = new byte[4];

                foreach (var frame in audioTrack[0])
                {
                    // add AU header
                    short frameLen = (short)(frame.Length << 3);
                    header[0] = 0x00;
                    header[1] = 0x10;
                    header[2] = (byte)((frameLen >> 8) & 0xFF);
                    header[3] = (byte)(frameLen & 0xFF);
                    server.FeedInAudioPacket(elapsed, header.Concat(frame).ToArray());

                    elapsed += sampleDuration;
                    await Task.Delay((int)(sampleDuration / 22.05d));
                }

                Console.WriteLine($"Finished audio {DateTime.Now.Subtract(audioStarted)}");
            });

            var playbackTasks = new[] { videoStreaming, audioStreaming };
            await Task.WhenAll(playbackTasks);

            Console.WriteLine($"Finished mp4 {DateTime.Now.Subtract(started)}");
        }
    });

    Console.WriteLine("Press any key to exit");
    while (!Console.KeyAvailable)
    {
        System.Threading.Thread.Sleep(250); 
    }
}

public struct Sample
{
    public byte[] Data { get; set; }
    public int Timestamp { get; set; }
    public int Timebase { get; set; }
    public Sample(byte[] data, int timestamp, int timebase)
    {
        this.Data = data;
        this.Timestamp = timestamp;
        this.Timebase = timebase;
    }
}

public static class Mp4Ext
{
    public static async Task<Dictionary<uint, IList<IList<byte[]>>>> ParseMdatAsync(this FragmentedMp4 fmp4)
    {
        var ret = new Dictionary<uint, IList<IList<byte[]>>>();
        var videoTrak = fmp4.GetMoov().GetTrak().First(x => x.GetMdia().GetMinf().GetVmhd() != null);
        uint videoTrackId = videoTrak.GetTkhd().TrackId;
        if (!ret.ContainsKey(videoTrackId))
            ret.Add(videoTrackId, new List<IList<byte[]>>() { new List<byte[]>() });

        int nalLengthSize = 0;
        var h264VisualSample = videoTrak.GetMdia().GetMinf().GetStbl().GetStsd().Children.FirstOrDefault(x => x.Type == VisualSampleEntryBox.TYPE3 || x.Type == VisualSampleEntryBox.TYPE4) as VisualSampleEntryBox;
        if (h264VisualSample != null)
        {
            AvcConfigurationBox avcC = h264VisualSample.Children.First(x => x.Type == AvcConfigurationBox.TYPE) as AvcConfigurationBox;
            nalLengthSize = avcC.AvcDecoderConfigurationRecord.LengthSizeMinusOne + 1; // 4 bytes

            foreach (var sps in avcC.AvcDecoderConfigurationRecord.SequenceParameterSets)
            {
                ret[videoTrackId][0].Add(H264SpsNalUnit.Build(sps));
            }

            foreach (var pps in avcC.AvcDecoderConfigurationRecord.PictureParameterSets)
            {
                ret[videoTrackId][0].Add(H264PpsNalUnit.Build(pps));
            }
        }
        else
        {
            var h265VisualSample = videoTrak.GetMdia().GetMinf().GetStbl().GetStsd().Children.FirstOrDefault(x => x.Type == VisualSampleEntryBox.TYPE6 || x.Type == VisualSampleEntryBox.TYPE7) as VisualSampleEntryBox;
            if (h265VisualSample != null)
            {
                HevcConfigurationBox hvcC = h265VisualSample.Children.First(x => x.Type == HevcConfigurationBox.TYPE) as HevcConfigurationBox;
                nalLengthSize = hvcC.HevcDecoderConfigurationRecord.LengthSizeMinusOne + 1; // 4 bytes

                foreach (var array in hvcC.HevcDecoderConfigurationRecord.NalArrays)
                {
                    foreach (var item in array.NalUnits)
                    {
                        ret[videoTrackId][0].Add(item);
                    }
                }
            }
            else
            {
                throw new NotSupportedException("No supported video track found!");
            }
        }

        try
        {
            // to parse MDAT, we need trun box - as it turned out, our sample MDAT has audio/video multiplexed together in a single MDAT
            MoofBox moof = null;
            for (int i = 0; i < fmp4.Children.Count; i++)
            {
                if (fmp4.Children[i].Type == "moof")
                {
                    moof = fmp4.Children[i] as MoofBox;
                }
                else if (fmp4.Children[i].Type == "mdat")
                {
                    var mdat = fmp4.Children[i] as MdatBox;
                    var mdatStorage = mdat.GetStorage();

                    Stream stream = mdatStorage.Stream;
                    stream.Seek(0, SeekOrigin.Begin);

                    // plan how to read the MDAT in the correct order
                    IEnumerable<TrunBox> plan = moof.GetTraf().SelectMany(x => x.GetTrun()).OrderBy(x => x.DataOffset);

                    foreach (var trun in plan)
                    {
                        uint trackId = (trun.GetParent() as TrafBox).GetTfhd().TrackId;

                        if (!ret.ContainsKey(trackId))
                            ret.Add(trackId, new List<IList<byte[]>>() { new List<byte[]>() });

                        bool isVideo = trackId == videoTrak.GetTkhd().TrackId;
                        for (int j = 0; j < trun.Entries.Count; j++)
                        {
                            var entry = trun.Entries[j];
                            int sampleSize = (int)entry.SampleSize; // in case of video, this is the size of AU which consists of 1 or more NALU
                            if (isVideo)
                            {
                                int nalUnitLength = 0;
                                int auTotalRead = 0;
                                int nalPerAUReadCount = 0;
                                int auIndex = ret[trackId].Count;

                                ret[trackId].Add(new List<byte[]>());

                                do
                                {
                                    switch (nalLengthSize)
                                    {
                                        case 1:
                                            nalUnitLength = (int)IsoReaderWriter.ReadByte(stream);
                                            break;
                                        case 2:
                                            nalUnitLength = (int)IsoReaderWriter.ReadUInt16(stream);
                                            break;
                                        case 3:
                                            nalUnitLength = (int)IsoReaderWriter.ReadUInt24(stream);
                                            break;
                                        case 4:
                                            nalUnitLength = (int)IsoReaderWriter.ReadUInt32(stream);
                                            break;

                                        default:
                                            throw new Exception($"NAL unit length {nalLengthSize} not supported!");
                                    }

                                    byte[] fragment = new byte[nalUnitLength];
                                    await stream.ReadExactlyAsync(fragment, 0, nalUnitLength);

                                    auTotalRead += nalLengthSize + nalUnitLength;
                                    nalPerAUReadCount++;

                                    ret[trackId][auIndex].Add(fragment);
                                }
                                while (auTotalRead != sampleSize);

                                auIndex++;
                            }
                            else
                            {
                                byte[] fragment = new byte[sampleSize];
                                await stream.ReadExactlyAsync(fragment, 0, sampleSize);
                                ret[trackId][0].Add(fragment);
                            }
                        }
                    }
                }
            }
        }
        catch (EndOfStreamException ex)
        {
            // likely corrupted/incomplete file
        }

        return ret;
    }

}