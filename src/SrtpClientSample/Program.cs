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
    client.NewVideoStream += (sender, e) => Console.WriteLine(e.ToString());
    client.ReceivedVideoData += (sender, e) => Console.Write("*");
    client.NewAudioStream += (sender, e) => Console.WriteLine(e.ToString());
    client.ReceivedAudioData += (sender, e) => Console.Write("+");
    client.Stopped += async (sender, e) => 
    {
        Console.WriteLine($"Stopped: {e.Reason}");

        if (e.Reason != StoppedReason.NotFound && e.Reason != StoppedReason.Unauthorized)
        {
            await Task.Delay(1000); // Wait for 1 second before trying to reconnect 
            client.TryReconnect();
        }
    };

    client.Connect(rtspUri, RTPTransport.UDP, userName, password, MediaRequest.VIDEO_AND_AUDIO, false, OnValidateCertificate, true);

    Console.WriteLine("Press any key to exit");
    while (!Console.KeyAvailable)
    {
        System.Threading.Thread.Sleep(250);
    }
}

bool OnValidateCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
{
#warning This is inherently insecure
    return true;
}