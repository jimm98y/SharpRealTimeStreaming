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
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

IConfiguration config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
string rtspUri = config["Uri"];
string userName = config["UserName"];
string password = config["Password"];

using (RTSPClient client = new RTSPClient())
{
    client.NewTrack += (sender, e) =>
        Console.WriteLine($"track {e.TrackIndex}: {e.Kind} {e.Codec} {e.StreamConfigurationData}");

    client.ReceivedData += (sender, e) =>
        Console.Write(e.Kind == TrackKind.Video ? "*" : e.Kind == TrackKind.Audio ? "+" : ".");
    client.Stopped += async (sender, e) => 
    {
        Console.WriteLine($"Stopped: {e.Reason}");

        // Only for the reasons that might come out differently next time. Reconnecting on one that
        // will not - the stream does not exist, the password is wrong, the server offered no key we
        // can use - is a one second loop that never ends and never succeeds.
        if (e.Reason == StoppedReason.ConnectionFailed
            || e.Reason == StoppedReason.RtcpBye
            || e.Reason == StoppedReason.ServerError)
        {
            await Task.Delay(1000); // Wait for 1 second before trying to reconnect 
            client.TryReconnect();
        }
    };

    client.AcceptTrack = _ => true;

    client.Connect(rtspUri, RTPTransport.UDP, userName, password, false, OnValidateCertificate, true);

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

bool OnValidateCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
{
#warning This is inherently insecure
    return true;
}