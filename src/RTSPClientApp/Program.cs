
using SharpRTSPClient;
using System;

const string rtspUri = "rtsp://127.0.0.1:8554";
const string userName = "admin";
const string password = "password";

using (RTSPClient client = new RTSPClient())
{
    client.NewVideoStream += (sender, e) => Console.WriteLine(e.ToString());
    client.ReceivedVideoData += (sender, e) => Console.Write("*");
    client.NewAudioStream += (sender, e) => Console.WriteLine(e.ToString());
    client.ReceivedAudioData += (sender, e) => Console.Write("+");

    client.Connect(rtspUri, RTSPClient.RTP_TRANSPORT.TCP, userName, password);

    Console.WriteLine("Press any key to exit");
    while (!Console.KeyAvailable)
    {
        System.Threading.Thread.Sleep(250);
    }
}