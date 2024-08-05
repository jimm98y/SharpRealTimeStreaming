using Microsoft.Extensions.Configuration;
using SharpRTSPServer;
using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

IConfiguration config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
string hostName = config["HostName"];
ushort port = ushort.Parse(config["Port"]);
string userName = config["UserName"];
string password = config["Password"];

string ffmpegPath = config["FFmpegPath"];
string ffmpegArgs = config["FFmpegArgs"];
string videoUri = config["VideoUri"];
string audioUri = config["AudioUri"];

// ffmpeg.exe -re -stream_loop -1 -i frag_bunny.mp4 -vcodec copy -an -f rtp rtp://127.0.0.1:11111 -vn -acodec copy -f rtp rtp://127.0.0.1:11113
ProcessStartInfo info = new ProcessStartInfo();
info.FileName = ffmpegPath;
info.Arguments = ffmpegArgs;
info.RedirectStandardOutput = true;
info.UseShellExecute = false;

string lastLine = null;
SemaphoreSlim semaphore = new SemaphoreSlim(0);
StringBuilder sdp = new StringBuilder();
var process = Process.Start(info);
process.OutputDataReceived += Process_OutputDataReceived;
process.BeginOutputReadLine();

// wait until the SDP is read
semaphore.Wait();

using (var server = new RTSPServer(port, userName, password))
{
    if (!string.IsNullOrEmpty(videoUri))
        server.AddVideoTrack(new ProxyTrack(0, videoUri));

    if (!string.IsNullOrEmpty(audioUri))
        server.AddAudioTrack(new ProxyTrack(1, audioUri));

    server.OverrideSDP(sdp.ToString(), true);

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

    process.Kill();
}

void Process_OutputDataReceived(object sender, DataReceivedEventArgs e)
{
    if(lastLine == "" && e.Data == "")
    {
        semaphore.Release();
        return;
    }

    lastLine = e.Data;

    if (!string.IsNullOrEmpty(e.Data) && !e.Data.StartsWith("SDP:"))
    {
        sdp.AppendLine(e.Data);
    }
}
