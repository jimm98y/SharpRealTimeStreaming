# SharpRealTimeStreaming
This is just a thin wrapper around the fantastic SharpRTSP, mostly based off their sample code with some API enhancements to make it easier to use. Added support for streaming AAC and H265 from fragmented MP4.

## SharpRTSPClient
Simple RTSP client that supports H264, H265 for video and AAC for audio.

## SharpRTSPServer
Simple RTSP server that supports H264, H265 for video and AAC, PCMU and PCMA for audio. 

## FFmpeg RTSP Server
Sample RTSP server for ffmpeg RTP streams. Fully configurable in appsettings.json.

## Remarks
Still work in progress, APIs are subject to change.

## Credits
Most of the work has been done by SharpRTSP (https://github.com/ngraziano/SharpRTSP), this is just a convenience wrapper around it.