# SharpRTSP client and server
This is a thin wrapper around the fantastic SharpRTSP, mostly based off their sample code with some API enhancements to make it easier to use. Added support for streaming Opus, AV1 and H266.

Upgrading from an earlier version? See [doc/migration.md](doc/migration.md).

## SharpRTSPClient
Simple RTSP client that supports MJPEG, H264, H265, H266, AV1 for video and AAC, Opus, PCMU and PCMA for audio.

[![NuGet version](https://img.shields.io/nuget/v/SharpRTSPClient.svg?style=flat-square)](https://www.nuget.org/packages/SharpRTSPClient)

Create the RTSP client:
```cs
using (RTSPClient client = new RTSPClient())
{
...
}
```

Subscribe to the tracks the stream offers, and to the media arriving on them:
```cs
client.NewTrack += (sender, e) => { ... }
client.ReceivedData += (sender, e) => { ... }
```

`NewTrack` is raised once per track as the description is read. It carries the track's index in
`e.TrackIndex`, what sort of media it is in `e.Kind`, the codec in `e.Codec`, and codec-specific
information from the SDP - SPS/PPS and the like - in `e.StreamConfigurationData`.

`ReceivedData` carries the frame in `e.Data`, and the same `e.TrackIndex` and `e.Kind` so you can
tell which track it came from. `ReceivedRawRTP` and `ReceivedRawRTCP` report the packets themselves
the same way.

For re-connection, you can optionally subscribe the `Stopped` event:
```cs
client.Stopped += (sender, e) => 
{ 
   client.TryReconnect();
}
```
Connect to the RTSP stream:
```cs
client.Connect("rtsp://localhost:8554/stream1", RTPTransport.TCP); 
```
Now you will start receiving callbacks with audio/video payload. 

To disconnect the RTSP stream, call `Stop` or just dispose the client:
```cs
client.Stop();
```

### Encrypted streams

For RTSPS, use an `rtsps://` URL. A certificate validation callback can be supplied if the server presents a certificate your machine does not trust:
```cs
client.Connect("rtsps://localhost:8322/stream1", RTPTransport.TCP, "admin", "password",
    userCertificateSelectionCallback: (sender, cert, chain, errors) => errors == SslPolicyErrors.None);
```

SRTP is negotiated automatically: when the SDP offers a `SAVP`/`SAVPF` profile with a `crypto`
attribute, the client derives the keys and decrypts RTP and RTCP for you, on every track it sets up.
The keys stay inside the client. To send RTCP on a protected stream, hand the client the
unprotected bytes and it will protect them with the right track's keys:

```cs
client.SendRTCP(trackIndex, client.BuildRtcpReceiverReport(ssrc));
```

A stream that describes itself as encrypted but gives no key this client can use is not played: the
client stops with `StoppedReason.EncryptionUnavailable` rather than carry on in the clear. Anyone
able to alter the SDP could otherwise arrange that by deleting one line of it.

The keys are not exposed at all, per track or otherwise. An `SrtpSessionContext` is live crypto
state: protecting a packet with one outside the client advances the roll over counter and the replay
state, and the far end can then no longer read what follows - so `SendRTCP` is what there is.

### Streams with more than one track of a kind

A stream is however many tracks it has: two qualities, two languages, or the metadata describing
what is in the picture. Every one of them is reported the same way, and says which it is:

```cs
client.AcceptTrack = _ => true;   // take all of them, not just the first of each kind

client.NewTrack += (s, e) => Console.WriteLine($"track {e.TrackIndex}: {e.Kind} {e.Codec}");
client.ReceivedData += (s, e) => Save(e.TrackIndex, e.Data);
client.ReceivedRawRTP += (s, e) => Inspect(e.TrackIndex, e.Data);
client.ReceivedRawRTCP += (s, e) => Inspect(e.TrackIndex, e.Data);
```

Every one of these says which track it is about, and `TrackCount` says how many there are.
`SendRTCP` and `GetSsrc`/`SetSsrc` take the same index.

Which of the offered tracks are set up is `AcceptTrack`, one question per track:

```cs
client.AcceptTrack = _ => true;                            // everything on offer
client.AcceptTrack = t => t.Kind == TrackKind.Video;       // pictures only
client.AcceptTrack = t => t.Codec == "H265";               // the H265 one of two video tracks
client.AcceptTrack = t => t.AcceptedSoFar < 2;             // the first two, whatever they are
```

It is asked before anything is bound for the track, so one passed over costs no transport, no
`SETUP` and nothing on the wire, and it is only asked about tracks this client could actually play.
Unset, it takes the first track of each kind. `MaxTracks` bounds what a description can make the
client set up whatever the filter says.

### Knowing why a stream ended

The `Stopped` event reports a `StoppedReason` so you can decide whether reconnecting makes sense:

| Reason | Meaning |
| --- | --- |
| `ConnectionFailed` | The TCP/TLS connection could not be established. |
| `Unauthorized` | The credentials were rejected. |
| `NotFound` | The server does not have the requested stream. |
| `RtcpBye` | The server sent an RTCP BYE for the stream we were receiving. |
| `ServerError` | The server rejected a request with an error we cannot recover from. |
| `UnsupportedMedia` | The SDP described no media this client can play. |
| `ProtocolError` | The RTSP dialog failed unexpectedly. Details are in the log. |
| `EncryptionUnavailable` | The SDP described the media as encrypted but gave no key this client can use, so it stopped rather than accept the media unprotected. |

Retrying is only worthwhile for `ConnectionFailed`, `RtcpBye` and sometimes `ServerError`; the rest will fail again the same way.

## SharpRTSPServer
Simple RTSP server that supports MJPEG, H264, H265, H266, AV1 for video and AAC, Opus, PCMU and PCMA for audio. 

[![NuGet version](https://img.shields.io/nuget/v/SharpRTSPServer.svg?style=flat-square)](https://www.nuget.org/packages/SharpRTSPServer)

Create the server on port 8554:
```cs
using(var server = new RTSPServer(8554, "admin", "password"))
{
...
}
```

Create tracks for the media you want to stream. For instance H264 video with AAC audio:
```cs
var h264Track = new H264Track();
h264Track.SetParameterSets(sps, pps); // Sequence Parameter Set (SPS) and Picture Parameter Set (PPS) are provided by your video source as byte[] 

var aacTrack = new AACTrack(audioSpecificConfig, samplingRate, channelCount); // audioSpecificConfig is provided by your audio source as byte[], samplingRate and channelCount are also properties of the audio source
```

Create a stream source from the tracks an add it to the server:
```cs
var streamSource = new RTSPStreamSource("stream1", h264Track, aacTrack);
server.AddStreamSource(streamSource);
```

Multiple streams can be added, each identified by unique stream ID:
```cs
var streamSource2 = new RTSPStreamSource("stream2", h265Track, null);
server.AddStreamSource(streamSource2);
```

Start listening for incoming requests:
```cs
server.StartListen();
```

To stream video, use the track instances and call `FeedInRawSamples` in regular intervals:
```cs
h264Track.FeedInRawSamples(rtpVideoBaseTime + videoPTS, new List<byte[]> { nal1, nal2, ... });
```

The same applies to audio:
```cs
aacTrack.FeedInRawSamples(rtpAudioBaseTime + audioPTS, new List<byte[]> { aacFrame });
```

### Available tracks

| Video | Audio |
| --- | --- |
| `H264Track`, `H265Track`, `H266Track`, `AV1Track`, `MJpegTrack` | `AACTrack`, `OpusTrack`, `PCMATrack`, `PCMUTrack` |

`ProxyTrack` forwards RTP that has already been packetized elsewhere, which is what the FFmpeg and PCAPNG samples use. Pair it with `RTSPStreamSource.OverrideSDP` to supply the SDP yourself.

### RTSPS, SRTP and HTTP tunnelling

The full constructor takes a TLS certificate, an HTTP tunnelling flag and an SRTP crypto suite:
```cs
using (var server = new RTSPServer(8322, "admin", "password",
    useHttpTunnel: false,
    tlsCertificate: certificate,
    srtpCryptoSuite: SrtpCryptoSuites.AES_CM_128_HMAC_SHA1_80,
    loggerFactory: null))
{
    ...
}
```

Passing a certificate makes the server listen for `rtsps://`; combining it with `useHttpTunnel` gives RTSP over HTTPS. To stream SRTP, also set the track's profile:
```cs
h264Track.RtpProfile = RtpProfiles.SAVP;
```
The server then generates per-connection keys and advertises them in the SDP `a=crypto` attribute.

The key lives in that attribute, so the DESCRIBE carrying it has to be encrypted or anyone on the
path can read the key and, with it, the media. Configure SRTP together with a TLS certificate and
offer the stream over `rtsps://`; the server logs a warning if you ask for SRTP without one.

### Multicast

Multicast is **off** by default. A group is not a client: once one is open the media goes onto the
local segment, where anything that joins the group and listens on the port receives it, with no
authentication at the RTP layer at all. Turn it on where that is what you want:

```cs
server.MulticastEnabled = true;
server.MulticastAddress = "239.1.1.1";
server.MulticastTimeToLive = 1; // one hop, so it stays on the local link
```

To keep a group readable only by clients that were given the key, set the track to `SAVP` and the
stream source's `SharedSrtpKey`, so every member is handed the one key the group sends under.

### Limits and access control

`MaxConnections` caps how many clients the server will hold at once (100 by default, `0` disables the limit). Connections that go quiet for longer than the 60 second RTSP timeout are dropped and their sockets released.

The server challenges clients with **Digest** access authentication by default. Basic is available for clients and hardware decoders that cannot do Digest, but it is off unless you ask for it:

```cs
server.AuthenticationScheme = RtspAuthenticationScheme.Basic;
```

Basic sends the user name and password base64 encoded, which anyone who can read the traffic can reverse, so only enable it together with a TLS certificate. The server logs a warning if you turn it on without one. Set the scheme before calling `StartListen`.

The sample servers expose this in `appsettings.json`, off by default:
```json
{
  "UserName": "admin",
  "Password": "password",
  "AllowBasicAuthentication": false
}
```

The client side needs no configuration: it answers whichever of the two schemes a server challenges it with.

Passing a null or empty user name disables authentication entirely, which is only appropriate on a trusted network.

Repeated failures from one address are answered slowly, so that guessing a password over a series of
fresh connections is not free:

```cs
server.FailedAuthenticationsBeforeDelay = 5;              // per address, in a row
server.FailedAuthenticationDelay = TimeSpan.FromSeconds(1);
```

An address that authenticates successfully starts clean again, so a client that mistyped its password
once pays nothing. Set either to zero to turn the throttle off.

#### Per-stream authorization

Authentication says who a client is; it says nothing about which streams it may have. Handle
`AuthorizeStream` to decide:

```cs
server.AuthorizeStream += (sender, e) =>
{
    if (!TenantOwns(e.UserName, e.StreamID))
    {
        e.Deny(404); // or 403 to say no outright, 401 to invite them to be somebody else
    }
};
```

It is raised for every request that names a stream — DESCRIBE, SETUP, PLAY and the rest — after the
request has authenticated and before the connection is attached to the stream, so a refused client is
never sent any of its media. `e.UserName` is the user the request actually proved it is; everything
else on the request came from the client. A handler that throws refuses the request. With no handler
the server behaves as it always did: any client that authenticates reaches every stream.

## Logging

Both libraries report through an `ILog` that belongs to the client or server rather than to the
process. Nothing is shared between two of them, so one noisy stream can be followed without turning
trace on for everything else:

```cs
var client = new RTSPClient();
client.Logger = new DefaultLog { Sink = line => myLog.Write(line) };

// or say nothing at all
var quiet = new RTSPServer(8554, "admin", "password") { Logger = NullLog.Instance };
```

`ILog` is five methods and five switches, and implementing it takes no dependency on anything:

```cs
public interface ILog
{
    void LogError(string error);
    void LogWarning(string warning);
    void LogInfo(string info);
    void LogDebug(string debug);
    void LogTrace(string trace);

    bool IsErrorEnabled { get; set; }
    bool IsWarningEnabled { get; set; }
    bool IsInfoEnabled { get; set; }
    bool IsDebugEnabled { get; set; }
    bool IsTraceEnabled { get; set; }
}
```

`DefaultLog` is what a client or server uses when it is not given anything: it writes to the debug
output, and its `Sink` can be pointed elsewhere without implementing the interface. Trace is off on
it, deliberately - that is the level the per-packet lines are written at, and formatting them costs
more than sending the media does.

Hosts that already use `Microsoft.Extensions.Logging` can pass an `ILoggerFactory` to the
constructor instead, and the messages arrive structured. Doing that overrides `Logger`.

`DefaultLog` and `NullLog` are named for their pairing with `ILog`; `NullLogger` would have collided
with `Microsoft.Extensions.Logging.Abstractions.NullLogger`.

## Samples

### RTSP Server App
Simple RTSP server that supports streaming video from multiple MP4 files.

### RTSP Recorder App
Demonstrates how to record RTSP and save it as mp4.

### FFmpeg RTSP Server
Sample RTSP server for ffmpeg RTP streams. Fully configurable in appsettings.json.

### PCAPNG RTSP Server
PoC of re-playing RTSP from a Wireshark PcapNg file. 

## Credits
Most of the work has been done by SharpRTSP (https://github.com/ngraziano/SharpRTSP), this is just a convenience wrapper around it.