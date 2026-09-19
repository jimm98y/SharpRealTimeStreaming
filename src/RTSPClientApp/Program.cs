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
using SharpRTSPClient;
using System;
using System.Threading.Tasks;

IConfiguration config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
string rtspUri = config["Uri"];
string userName = config["UserName"];
string password = config["Password"];

using (RTSPClient client = new RTSPClient())
{
    // Every track the stream offers, and which one each frame belongs to. The events named after
    // a kind reported the first video track and the first audio track and nothing else.
    client.NewTrack += (sender, e) =>
        Console.WriteLine($"track {e.TrackIndex}: {e.Kind} {e.Codec} {e.StreamConfigurationData}");

    client.ReceivedData += (sender, e) =>
        Console.Write(e.Kind == TrackKind.Video ? "*" : e.Kind == TrackKind.Audio ? "+" : ".");
    client.Stopped += async (sender, e) => 
    {
        Console.WriteLine($"Stopped: {e.Reason}");

        if (e.Reason != StoppedReason.NotFound && e.Reason != StoppedReason.Unauthorized)
        {
            await Task.Delay(1000); // Wait for 1 second before trying to reconnect 
            client.TryReconnect();
        }
    };

    // Everything the stream offers, rather than the first video and audio track. Leave AcceptTrack
    // unset for that older behaviour, or narrow it: t => t.Kind == TrackKind.Video, t => t.Codec == "H265".
    client.AcceptTrack = _ => true;

    client.Connect(rtspUri, RTPTransport.TCP, userName, password, false, null, true);

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
}