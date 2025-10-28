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