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

using Rtsp;
using Rtsp.Rtp;
using SharpSRTP.SRTP;
using System;

namespace SharpRTSPClient
{
    /// <summary>
    /// What kind of media a track carries.
    /// </summary>
    /// <remarks>
    /// A kind, not a position: a stream may offer two of the same - two languages, two qualities -
    /// and which track is which is decided by its place in the description, not by what is in it.
    /// </remarks>
    public enum TrackKind
    {
        Video,
        Audio,

        /// <summary>
        /// Data about the media rather than the media itself, carried as documents in an
        /// "application" section of the description.
        /// </summary>
        Application,
    }

    /// <summary>
    /// One track of the stream being received, and everything that belongs to it alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client used to hold two of each of these as separate fields - a video transport and an
    /// audio one, a video payload type and an audio one, and two copies of the code that used them.
    /// A stream that offered anything else, or two of a kind, had nowhere to put it.
    /// </para>
    /// <para>
    /// Each track has its own transport, and so its own ports or its own pair of interleaved
    /// channels, its own keys where the media is protected, and its own reckoning of the sender's
    /// clock. Nothing here is shared with any other track.
    /// </para>
    /// </remarks>
    internal sealed class ClientTrack
    {
        /// <summary>
        /// Where this track came in the description, which is the order it is set up in.
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// What sort of media this track carries.
        /// </summary>
        public TrackKind Kind { get; set; }

        /// <summary>
        /// The URL this track is set up and controlled with.
        /// </summary>
        public Uri ControlUri { get; set; }

        /// <summary>
        /// The payload type the description said this track uses. Packets carrying anything else are
        /// not this track's and are passed over.
        /// </summary>
        public int PayloadType { get; set; } = -1;

        /// <summary>
        /// The codec as the description named it, for reporting to whoever is listening.
        /// </summary>
        public string Codec { get; set; } = "";

        /// <summary>
        /// Puts the packets of this track back into frames. Null where nothing here can read it, in
        /// which case the raw packets are still reported and the frames are not.
        /// </summary>
        public IPayloadProcessor Processor { get; set; }

        /// <summary>
        /// The sockets, or the pair of interleaved channels, this track arrives on.
        /// </summary>
        public IRtpTransport Transport { get; set; }

        /// <summary>
        /// The keys this track is protected with, or null where it is not protected.
        /// </summary>
        public SrtpSessionContext Context { get; set; }

        /// <summary>
        /// What is known of the sender's clock on this track, and when it was last reported to.
        /// </summary>
        public RtcpChannelState Rtcp { get; } = new RtcpChannelState();

        /// <summary>
        /// The SSRC this client reports under on this track.
        /// </summary>
        public uint Ssrc { get; set; }

        /// <summary>
        /// Whatever the description held for this track, for callers that want to read it themselves.
        /// </summary>
        public IStreamConfigurationData Configuration { get; set; }

        /// <summary>
        /// The handlers this track's transport was subscribed with.
        /// </summary>
        /// <remarks>
        /// Kept so that the same delegate can be taken off again. Subscribing with one lambda and
        /// unsubscribing with another leaves the first one attached, and a transport that goes on
        /// delivering into a torn down session is a use after free with extra steps.
        /// </remarks>
        public EventHandler<RtspDataEventArgs> OnData { get; set; }

        public EventHandler<RtspDataEventArgs> OnControl { get; set; }
    }
}
