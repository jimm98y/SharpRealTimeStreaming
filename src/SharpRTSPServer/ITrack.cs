// SharpRTSPServer
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

using System;
using System.Collections.Generic;
using System.Text;

namespace SharpRTSPServer
{
    public enum RtpProfiles
    {
        AVP,
        // AVPF,
        SAVP,
        // SAVPF
    }

    public interface ITrack
    {
        RtpProfiles RtpProfile { get; set; }

        uint SSRC { get; set; }

        IRtpSender Sink { get; set; }

        string StreamID { get; set; }

        /// <summary>
        /// Codec name.
        /// </summary>
        string Codec { get; }

        /// <summary>
        /// Track ID. Used to identify the track in the SDP, and its place in the stream it belongs to.
        /// </summary>
        int ID { get; set; }

        /// <summary>
        /// What kind of media this track carries.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="ID"/>, which says where the track sits in its stream. A stream
        /// can carry two audio tracks, and then one of them has an ID that no longer matches the
        /// kind - which is why the two are not the same thing.
        /// </remarks>
        TrackType Kind { get; }

        /// <summary>
        /// Payload type.
        /// </summary>
        int PayloadType { get; set; }

        /// <summary>
        /// Is the track ready?
        /// </summary>
        bool IsReady { get; }

        /// <summary>
        /// Build the SDP for this track.
        /// </summary>
        /// <param name="sdp">SDP <see cref="StringBuilder"/>.</param>
        /// <returns><see cref="StringBuilder"/>.</returns>
        StringBuilder BuildSDP(StringBuilder sdp);

        /// <summary>
        /// Creates RTP packets.
        /// </summary>
        /// <remarks>
        /// The packets are handed on to the sink, which releases them once the last client has been
        /// sent the frame - which is long after this returns. That is why a track asks
        /// <see cref="RtpPackets"/> for the memory rather than finding its own: a track that wrote
        /// into a buffer it kept, or handed back a slice of the sample it was given, would have it
        /// read long after the caller had moved on.
        /// </remarks>
        /// <param name="samples">An array of samples.</param>
        /// <param name="rtpTimestamp">RTP timestamp in the timescale of the track.</param>
        /// <param name="packets">
        /// Where to build them. Ask it for each packet by length and fill it in; it owns the memory
        /// and hands the lot on.
        /// </param>
        void CreateRtpPackets(List<ReadOnlyMemory<byte>> samples, uint rtpTimestamp, RtpPackets packets);

        void FeedInRawSamples(uint rtpTimestamp, List<ReadOnlyMemory<byte>> samples);

    }
}
