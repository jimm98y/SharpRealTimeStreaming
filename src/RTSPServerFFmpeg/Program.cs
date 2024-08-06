using Microsoft.Extensions.Configuration;
using SharpRTSPServer;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

IConfiguration config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
string hostName = config["HostName"];
ushort port = ushort.Parse(config["Port"]);
string userName = config["UserName"];
string password = config["Password"];

string ffmpegPath = config["FFmpegPath"]; // path to ffmpeg.exe
string ffmpegArgs = config["FFmpegArgs"]; // Arguments that will be passed to the ffmpeg process
string videoUri = config["VideoUri"]; // RTP video URI
string audioUri = config["AudioUri"]; // RTP audio URI
string sdpFile = config["SDPFile"]; // SDP file path (Optional in case ffmpegPath and ffmpegArgs are not specified. You have to launch ffmpeg before starting the server.)

SemaphoreSlim semaphore = new SemaphoreSlim(0);
StringBuilder sdpBuilder = new StringBuilder();
ProcessStartInfo info = new ProcessStartInfo();
Process process = null;
string lastLine = null;
string sdp = null;

if (!string.IsNullOrEmpty(ffmpegPath) && !string.IsNullOrEmpty(ffmpegArgs))
{
    // launch ffmpeg, parse the output and start streaming
    // ffmpeg.exe -re -stream_loop -1 -i frag_bunny.mp4 -vcodec copy -an -f rtp rtp://127.0.0.1:11111 -vn -acodec copy -f rtp rtp://127.0.0.1:11113
    info.FileName = ffmpegPath;
    info.Arguments = ffmpegArgs;
    info.RedirectStandardOutput = true;
    info.UseShellExecute = false;

    process = Process.Start(info);
    process.OutputDataReceived += Process_OutputDataReceived;
    process.BeginOutputReadLine();

    // wait until the SDP is read
    semaphore.Wait();

    sdp = sdpBuilder.ToString();
}
else if(!string.IsNullOrEmpty(sdpFile))
{
    // optionally, read SDP from a file
    sdp = File.ReadAllText(sdpFile);
}
else
{
    throw new Exception("Invalid configuration! Either ffmpegPath and ffmpegArgs, or SDPFile must be specified!");
}

if (string.IsNullOrEmpty(videoUri) && string.IsNullOrEmpty(audioUri))
    throw new Exception("Invalid configuration! Either VideoUri, AudioUri or both must be specified!");

using (var server = new RTSPServer(port, userName, password))
{
    if (!string.IsNullOrEmpty(videoUri))
        server.AddVideoTrack(new ProxyTrack(TrackType.Video, videoUri));

    if (!string.IsNullOrEmpty(audioUri))
        server.AddAudioTrack(new ProxyTrack(TrackType.Audio, audioUri));

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
        Thread.Sleep(250); 
    }

    if (process != null)
    {
        process.Kill();
    }
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
        sdpBuilder.AppendLine(e.Data);
    }
}
