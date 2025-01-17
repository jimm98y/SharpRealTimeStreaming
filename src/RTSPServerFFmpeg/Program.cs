using Microsoft.Extensions.Configuration;
using SharpRTSPServer;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
    using (CancellationTokenSource cts = new CancellationTokenSource())
    {
        ProxyTrack videoTrack = null;
        ProxyTrack audioTrack = null;
        Task videoTask = null;
        Task audioTask = null;

        if (!string.IsNullOrEmpty(videoUri))
        {
            videoTrack = new ProxyTrack(TrackType.Video);
            videoTask = RunUdpClient(videoTrack, new Uri(videoUri, UriKind.Absolute), cts.Token);
            server.AddVideoTrack(videoTrack);
        }

        if (!string.IsNullOrEmpty(audioUri))
        {
            audioTrack = new ProxyTrack(TrackType.Audio);
            audioTask = RunUdpClient(audioTrack, new Uri(audioUri, UriKind.Absolute), cts.Token);
            server.AddAudioTrack(audioTrack);
        }

        server.OverrideSDP(sdp, true);

        videoTrack?.Start();
        audioTrack?.Start();

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

        await cts.CancelAsync();

        if (process != null)
        {
            process.Kill();
        }
    }
}

Task RunUdpClient(ProxyTrack track, Uri uri, CancellationToken cancellationToken)
{
    return Task.Run(() =>
    {
        try
        {
            using (UdpClient udpClient = new UdpClient(uri.Port))
            {
                IPEndPoint remoteEndPoint = new IPEndPoint(IPAddress.Parse(uri.Host), 0);

                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        byte[] rtp = udpClient.Receive(ref remoteEndPoint);
                        uint rtpTimestamp = RTPPacketUtil.ReadTS(rtp);
                        track.FeedInRawSamples(rtpTimestamp, new(rtp));
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine(e.ToString());
                    }
                }
            }
        }
        catch (Exception ee)
        {
            Debug.WriteLine(ee.ToString());
        }
    }, cancellationToken);
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
