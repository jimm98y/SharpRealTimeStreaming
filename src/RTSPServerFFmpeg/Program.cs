using Microsoft.Extensions.Configuration;
using SharpRTSPServer;
using System;

IConfiguration config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
string hostName = config["HostName"];
ushort port = ushort.Parse(config["Port"]);
string userName = config["UserName"];
string password = config["Password"];

// ffmpeg.exe -re -stream_loop -1 -i C:\Git\SharpMediaCoder\src\SharpMediaPlayer\frag_bunny.mp4 -vcodec copy -an -f rtp rtp://127.0.0.1:11111 -vn -acodec copy -f rtp rtp://127.0.0.1:11113
using (var server = new RTSPServer(port, userName, password))
{
    server.AddVideoTrack(new ProxyTrack(0, "rtp://127.0.0.1:11111"));
    server.AddAudioTrack(new ProxyTrack(1, "rtp://127.0.0.1:11113"));
    // original
    string sdp =
        "v=0\r\n" +
        "o=- 0 0 IN IP4 127.0.0.1\r\n" +
        "s=No Name\r\n" +
        "t=0 0\r\n" +
        "a=tool:libavformat 60.3.100\r\n" +
        "m=video 11111 RTP/AVP 96\r\n" +
        "c=IN IP4 127.0.0.1\r\n" +
        "b=AS:587\r\n" +
        "a=rtpmap:96 H264/90000\r\n" +
        "a=fmtp:96 packetization-mode=1; sprop-parameter-sets=Z2QAHqzZQKAv+WagwCDW4AAAAwAgAAAGAeLFssA=,aOvjyyLA; profile-level-id=64001E\r\n" +
        "m=audio 11113 RTP/AVP 97\r\n" +
        "c=IN IP4 127.0.0.1\r\n" +
        "b=AS:69\r\n" +
        "a=rtpmap:97 MPEG4-GENERIC/44100/1\r\n" +
        "a=fmtp:97 profile-level-id=1;mode=AAC-hbr;sizelength=13;indexlength=3;indexdeltalength=3; config=120856E500\r\n\r\n";    

    server.OverrideSDP(sdp, true);

    try
    {
        server.StartListen();
    }
    catch (Exception ex)
    {
        Console.WriteLine(ex.ToString());
    }

    Console.WriteLine($"RTSP URL is rtsp://{userName}:{password}@{hostName}:{port}");

    Console.WriteLine("Press any key to exit");
    while (!Console.KeyAvailable)
    {
        System.Threading.Thread.Sleep(250); 
    }
}
