using Microsoft.Extensions.Logging;
using SharpRTSPClient;
using SharpRTSPServer;
using System.Configuration;
using System.Net.Sockets;


TrackBase track = null;
RTSPServer server = new RTSPServer(8863, "admin", "admin");


server.StartListen();

RTSPClient client = new RTSPClient();

client.NewVideoStream += (sender, e) =>
{
    if (e.StreamType == "H265")
    {
        H265StreamConfigurationData streamConfigurationData = (H265StreamConfigurationData)e.StreamConfigurationData;
        track = new H265Track(streamConfigurationData.VPS, streamConfigurationData.SPS, streamConfigurationData.PPS);
        RTSPStreamSource streamSource = new RTSPStreamSource("stream1", track, null);
        server.AddStreamSource(streamSource);
    }

};
client.ReceivedVideoData += (sender, e) =>
{
    var trimmedData = e.Data
        .Select(x => x.Length > 4 ? x.Slice(4) : ReadOnlyMemory<byte>.Empty)
        .ToList();

    foreach (var nalUnitMem in trimmedData)
    {
        var nalUnit = nalUnitMem.Span;
        if (nalUnit.Length > 1)
        {
            int nal_unit_type = (nalUnit[0] >> 1) & 0x3F;
            string description = nal_unit_type switch
            {
                1 => "NON IDR NAL",
                19 => "IDR NAL",
                32 => "VPS NAL",
                33 => "SPS NAL",
                34 => "PPS NAL",
                39 => "SEI NAL",
                _ => "OTHER NAL",
            };

            if (nal_unit_type == 19)
            {
                track.IDRPacket = (trimmedData, e.RtpTimestamp);
            }

            Console.WriteLine($"NAL Type = {nal_unit_type} {description}");
        }
    }

    track.FeedInRawSamples(e.RtpTimestamp, trimmedData);
};
client.NewAudioStream += (sender, e) => Console.WriteLine(e.ToString());
client.ReceivedAudioData += (sender, e) => Console.Write("+");
client.Stopped += (sender, e) =>
{

};

client.Connect("rtsp://admin:asdqwe123@192.168.2.60:554/Streaming/Channels/101?transportmode=unicast&profile=Profile_1", RTPTransport.TCP, "admin", "asdqwe123", MediaRequest.VIDEO_AND_AUDIO, false, null, true);

Console.WriteLine("Press any key to exit");
while (!Console.KeyAvailable)
{
    System.Threading.Thread.Sleep(250);
}

Console.ReadLine();


