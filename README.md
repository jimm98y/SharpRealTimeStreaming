# SharpRTSP client and server
This is a thin wrapper around the fantastic SharpRTSP, mostly based off their sample code with some API enhancements to make it easier to use. Added support for streaming Opus, AV1 and H266.

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

Subscribe events for video:
```cs
client.NewVideoStream += (sender, e) => { ... }
client.ReceivedVideoData += (sender, e) => { ... }
```

The `NewVideoStream` callback will contain the video codec in `e.StreamType` as well as codec-specific info such as SPS/PPS from the SDP in `e.StreamConfigurationData`.

Subscribe events for audio:
```cs
client.NewAudioStream += (sender, e) => { ... }
client.ReceivedAudioData += (sender, e) => { ... }
```

The `NewAudioStream` callback will contain the audio codec in `e.StreamType` as well as codec-specific info from the SDP in `e.StreamConfigurationData`.

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

SRTP is negotiated automatically: when the SDP offers a `SAVP`/`SAVPF` profile with a `crypto` attribute, the client derives the keys and decrypts RTP and RTCP for you. The derived contexts are exposed as `VideoContext` and `AudioContext`.

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

Authentication is also the only access control: any client that authenticates can reach every stream the server offers. If you need per-stream authorization, hook the `ReceivedRtspMessage` event and enforce it there. Passing a null or empty user name disables authentication entirely, which is only appropriate on a trusted network.

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