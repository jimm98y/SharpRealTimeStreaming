// SharpRTSPServer
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

const string STREAM_ID = "stream1";

using (var server = new RTSPServer(port, ReadUsers(config, "Users")))
{
    server.AuthenticationScheme = ReadAuthenticationScheme(config["AllowBasicAuthentication"]);

    using (CancellationTokenSource cts = new CancellationTokenSource())
    {
        ProxyTrack rtspVideoTrack = null;
        ProxyTrack rtspAudioTrack = null;
        Task videoTask = null;
        Task audioTask = null;

        if (!string.IsNullOrEmpty(videoUri))
        {
            rtspVideoTrack = new ProxyTrack(TrackType.Video);
            videoTask = RunUdpClient(rtspVideoTrack, new Uri(videoUri, UriKind.Absolute), cts.Token);
        }

        if (!string.IsNullOrEmpty(audioUri))
        {
            rtspAudioTrack = new ProxyTrack(TrackType.Audio);
            audioTask = RunUdpClient(rtspAudioTrack, new Uri(audioUri, UriKind.Absolute), cts.Token);
        }

        var streamSource = new RTSPStreamSource(STREAM_ID, rtspVideoTrack, rtspAudioTrack);
        streamSource.OverrideSDP(sdp, true);

        // A client that arrives mid-group has nothing it can decode until the next keyframe.
        // Keeping the last one means it is given a picture straight away, and held on that
        // picture until the stream starts a group it can follow properly.
        streamSource.KeepLastKeyFrame = true;

        server.AddStreamSource(streamSource);

        rtspVideoTrack?.Start();
        rtspAudioTrack?.Start();

        try
        {
            server.StartListen();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.ToString());
        }

        Console.WriteLine($"RTSP URL is rtsp://{hostName}:{port}/{STREAM_ID} - authenticate as one of the configured users");

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
                Thread.Sleep(250);
            }
        }

        await cts.CancelAsync();

        if (process != null)
        {
            process.Kill();
        }
    }
}

// Basic sends the password in a reversible form, so it stays off unless the config asks for it.
/// <summary>
/// The users this server will authenticate, read from the Users section of appsettings.json.
/// </summary>
/// <remarks>
/// A name and a password apiece, so a server can have several. An empty or missing section means no
/// repository at all, which is a server that does not authenticate - only appropriate on a trusted
/// network.
/// </remarks>
static IUserRepository ReadUsers(IConfiguration configuration, string section)
{
    // Binds straight onto UserInfo, so the section is a list of the same shape the repository
    // hands back: a UserName and a Password apiece.
    UserInfo[] configured = configuration.GetSection(section).Get<UserInfo[]>();

    if (configured == null)
    {
        return null;
    }

    var users = new InMemoryUserRepository();

    foreach (UserInfo user in configured)
    {
        if (!string.IsNullOrEmpty(user?.UserName) && !string.IsNullOrEmpty(user.Password))
        {
            users.Add(user.UserName, user.Password);
        }
    }

    return users.Count > 0 ? users : null;
}

static RtspAuthenticationScheme ReadAuthenticationScheme(string allowBasicAuthentication)
{
    return bool.TryParse(allowBasicAuthentication, out bool allowBasic) && allowBasic
        ? RtspAuthenticationScheme.Basic
        : RtspAuthenticationScheme.Digest;
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
                        track.FeedInRawSamples(rtpTimestamp, new List<ReadOnlyMemory<byte>>() { rtp });
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
