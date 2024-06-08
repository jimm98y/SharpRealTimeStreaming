
using SharpRTSPClient;

using (RTSPClient client = new RTSPClient())
{
    client.NewVideoStream += (sender, e) => Console.WriteLine(e.ToString());
    client.ReceivedVideoData += (sender, e) => Console.Write("*");
    client.NewAudioStream += (sender, e) => Console.WriteLine(e.ToString());
    client.ReceivedAudioData += (sender, e) => Console.Write("+");

    client.Connect("rtsp://stream.strba.sk:1935/strba/VYHLAD_JAZERO.stream", RTSPClient.RTP_TRANSPORT.TCP);

    while (!Console.KeyAvailable)
    { }
}