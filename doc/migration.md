# Migrating

Everything that changed shape, and what to write instead.

## Contents

- [Tracks are addressed by index, not by kind](#tracks-are-addressed-by-index-not-by-kind)
- [Which tracks to receive is one callback](#which-tracks-to-receive-is-one-callback)
- [A track has to say what kind of media it carries](#a-track-has-to-say-what-kind-of-media-it-carries)
- [The SRTP keys are not exposed](#the-srtp-keys-are-not-exposed)
- [Logging belongs to the client or server, not the process](#logging-belongs-to-the-client-or-server-not-the-process)
- [Multicast is off by default](#multicast-is-off-by-default)

---

## Tracks are addressed by index, not by kind

A stream is however many tracks it has: two qualities, two languages, a picture and the metadata
describing what is in it. The API that named a kind resolved to the **first** track of that kind, so
a second one was silently never reported and there was no way to ask about it.

### Client

| Removed | Use instead |
| --- | --- |
| `NewVideoStream`, `NewAudioStream` | `NewTrack` |
| `ReceivedVideoData`, `ReceivedAudioData` | `ReceivedData` |
| `ReceivedRawVideoRTP`, `ReceivedRawAudioRTP` | `ReceivedRawRTP` |
| `ReceivedRawVideoRTCP`, `ReceivedRawAudioRTCP` | `ReceivedRawRTCP` |
| `SendVideoRTCP(rtcp)`, `SendAudioRTCP(rtcp)` | `SendRTCP(trackIndex, rtcp)` |
| `VideoSSRC`, `AudioSSRC` | `GetSsrc(trackIndex)`, `SetSsrc(trackIndex, ssrc)` |
| `VideoContext`, `AudioContext` | nothing — see [the SRTP keys](#the-srtp-keys-are-not-exposed) |
| `NewStreamEventArgs` | `NewTrackEventArgs` |

Every one of the replacements carries `TrackIndex`, `Kind` and `Codec`, so the track a frame came
from is never in doubt. `TrackCount` says how many there are.

Before:

```cs
client.NewVideoStream += (s, e) => Console.WriteLine(e.StreamType);
client.ReceivedVideoData += (s, e) => Save(e.Data);
client.ReceivedAudioData += (s, e) => Play(e.Data);
```

After:

```cs
client.NewTrack += (s, e) => Console.WriteLine($"track {e.TrackIndex}: {e.Kind} {e.Codec}");

client.ReceivedData += (s, e) =>
{
    if (e.Kind == TrackKind.Video) Save(e.TrackIndex, e.Data);
    else if (e.Kind == TrackKind.Audio) Play(e.TrackIndex, e.Data);
};
```

If you really do want only the first track of a kind, that is now something you say rather than
something the library assumes — see the next section.

### Server

| Removed | Use instead |
| --- | --- |
| `RTSPStreamSource.VideoTrack`, `AudioTrack`, `MetadataTrack` | `Tracks`, `TrackById(id)`, `AddTrack(track)` |
| `RTSPStreamSource(id, video, audio)` | `RTSPStreamSource(id, params ITrack[])` — the same two-track call still compiles |
| `RTSPStreamSource.GetTrackControl(TrackType)` | `GetTrackControl(ITrack)` |
| `RTSPConnection.Video`, `Audio` | `Streams`, `StreamOrNull(trackId)` |
| `RTSPConnection.SSRC` | `RTPStream.SSRC` on the stream you mean |

Before:

```cs
var source = new RTSPStreamSource("stream1", h264Track, aacTrack);
ITrack video = source.VideoTrack;
```

After:

```cs
var source = new RTSPStreamSource("stream1", h264Track, aacTrack);  // unchanged
ITrack video = h264Track;                                           // you already have it
ITrack byId = source.TrackById(0);                                  // or ask for one
```

### Two of these were bugs, not just awkward

Worth knowing if you run more than one track of a kind:

- The server matched incoming RTCP to a session by looking at streams 0 and 1 only. A UDP session
  on any later track had its keepalives ignored — so the idle sweep dropped sessions whose clients
  were talking — and its reception reports discarded.
- The `RTP-Info` header of a `PLAY` cast a track ID to a kind. A stream with two video tracks named
  the second one under the first audio track's control URL, or under the session URL when there was
  no audio track.

Both are fixed.

---

## Which tracks to receive is one callback

`MediaRequest` and `ReceiveAllTracks` are gone. Between them they could say which kinds and how many
of each, and nothing else — not "the H265 one", not "the English audio", and the metadata tracks
were reachable only by turning every other extra track on at once.

| Removed | Use instead |
| --- | --- |
| `MediaRequest.VIDEO_ONLY` | `client.AcceptTrack = t => t.Kind == TrackKind.Video;` |
| `MediaRequest.AUDIO_ONLY` | `client.AcceptTrack = t => t.Kind == TrackKind.Audio;` |
| `MediaRequest.VIDEO_AND_AUDIO` | `client.AcceptTrack = t => t.Kind != TrackKind.Application;` |
| `ReceiveAllTracks = true` | `client.AcceptTrack = _ => true;` |
| `ReceiveAllTracks = false` | the default, or `RTSPClient.FirstOfEachKind` written out |

The `mediaRequest` parameter has been dropped from every `Connect` overload, so positional calls
need the argument removed:

```cs
// before
client.Connect(uri, RTPTransport.TCP, user, pass, MediaRequest.VIDEO_AND_AUDIO, false, null, true);

// after
client.AcceptTrack = t => t.Kind != TrackKind.Application;
client.Connect(uri, RTPTransport.TCP, user, pass, false, null, true);
```

`AcceptTrack` is asked once per track, before anything is bound for it — a track passed over costs
no transport, no `SETUP`, and nothing on the wire. It is only asked about tracks the client could
actually play. The offer carries the codec, the payload type, where the track came in the
description, and how many tracks have been accepted so far:

```cs
client.AcceptTrack = _ => true;                          // everything on offer
client.AcceptTrack = t => t.Codec == "H265";             // the H265 one of two video tracks
client.AcceptTrack = t => t.AcceptedSoFar < 2;           // the first two, whatever they are
client.AcceptTrack = t => t.Kind == TrackKind.Application;  // the metadata and nothing else
```

Unset, it behaves as `RTSPClient.FirstOfEachKind`: the first video track, the first audio track and
the first metadata track.

> **One behaviour change.** The first metadata track is now set up by default, where it used to
> require `ReceiveAllTracks`. If you do not want it: `client.AcceptTrack = t =>
> RTSPClient.FirstOfEachKind(t) && t.Kind != TrackKind.Application;`

`MaxTracks` is unchanged and still applies: it bounds what a description can make the client spend,
whatever the filter says.

---

## A track has to say what kind of media it carries

`TrackBase.Kind` no longer defaults to `(TrackType)ID`. It throws `NotImplementedException` naming
the track, so a track written outside this library has to say:

```cs
public override TrackType Kind => TrackType.Video;
```

The old default read a track's *place in its stream* as the *sort of media it carries*, which is
right only for a stream that is one video track followed by one audio track. A second video track
has ID 1 and reported itself as audio — and the kind is acted on: it decides which frames are thrown
away first when a client falls behind, whether a frame is held back waiting for a keyframe, and
which media section of an overridden SDP a track's control URL and keys belong to.

Every track this library ships already overrides it, so only your own tracks are affected.

---

## The SRTP keys are not exposed

`VideoContext` and `AudioContext` are gone and there is no per-track replacement. An
`SrtpSessionContext` is live crypto state: protecting a packet with one outside the client advances
the roll over counter and the replay state, and the far end can then no longer read what follows.

What they were wanted for is sending RTCP on a protected stream, which is now done with the keys
kept inside:

```cs
// before
byte[] protectedRtcp = RTSPClient.ProtectRtcp(client.VideoContext, report);
// ...and then somehow send it

// after
client.SendRTCP(trackIndex, report);
```

---

## Logging belongs to the client or server, not the process

The static `SharpRTSPClient.Log` and `SharpRTSPServer.Logging.Log` classes are gone. They were
process-wide, so two clients in one program wrote to the same place and turning trace on for a noisy
one turned it on for all of them.

```cs
// before
Log.SinkDebug = (m, ex) => myLog.Write(m);
Log.TraceEnabled = true;

// after
client.Logger = new DefaultLog { Sink = m => myLog.Write(m), IsTraceEnabled = true };
```

`ILog` is five methods and five switches and takes no dependency on anything. `NullLog.Instance`
says nothing at all. Hosts that already use `Microsoft.Extensions.Logging` can keep passing an
`ILoggerFactory` to the constructor, which overrides `Logger`.

`DefaultLog` and `NullLog` are named for their pairing with `ILog`; `NullLogger` would have collided
with `Microsoft.Extensions.Logging.Abstractions.NullLogger`.

---

## Multicast is off by default

`RTSPServer.MulticastEnabled` now defaults to `false`. Once a group is open the media goes onto the
local segment, where anything that joins the group receives it with no authentication at the RTP
layer — so a single client that authenticates could previously make the server publish the stream to
everything on the link.

```cs
server.MulticastEnabled = true;   // where that is the intent
```

Pair it with `SAVP` and `RTSPStreamSource.SharedSrtpKey` so only clients that were given the key can
read what the group carries.
