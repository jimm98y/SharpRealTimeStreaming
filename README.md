# SharpRTSP wrapper
This is just a thin wrapper around the fantastic SharpRTSP, mostly based off their sample code with some API enhancements to make it easier to use. Added support for streaming Opus, AV1 and H266.

## SharpRTSPClient
Simple RTSP client that supports H264, H265, H266, AV1 for video + AAC, Opus, PCMU and PCMA for audio.

[![NuGet version](https://img.shields.io/nuget/v/SharpRTSPClient.svg?style=flat-square)](https://www.nuget.org/packages/SharpRTSPClient)

## SharpRTSPServer
Simple RTSP server that supports H264, H265, H266, AV1 for video and AAC, Opus, PCMU and PCMA for audio. 

[![NuGet version](https://img.shields.io/nuget/v/SharpRTSPServer.svg?style=flat-square)](https://www.nuget.org/packages/SharpRTSPServer)

## FFmpeg RTSP Server
Sample RTSP server for ffmpeg RTP streams. Fully configurable in appsettings.json.

## Pcap RTSP Server
PoC of re-playing RTSP from a Wireshark PcapNg file. 

## Credits
Most of the work has been done by SharpRTSP (https://github.com/ngraziano/SharpRTSP), this is just a convenience wrapper around it.