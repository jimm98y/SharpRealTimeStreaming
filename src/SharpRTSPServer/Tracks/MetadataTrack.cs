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
    /// <summary>
    /// Data about the media, carried alongside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What a camera saw and where, sent as XML documents in their own RTP stream. It is a track
    /// like any other - described in the SDP, set up, played and torn down the same way - and having
    /// it separate from the picture is the point: a viewer that does not want it never sets it up,
    /// and losing a packet of it does not disturb the video.
    /// </para>
    /// <para>
    /// A document is sent whole, cut across as many packets as it needs, with the marker bit on the
    /// last of them. There is no payload header: a receiver puts the pieces back together in
    /// sequence order and reads what comes out. Nothing here looks inside the document, so it will
    /// carry any XML, ONVIF or otherwise; what says which it is, is the media type in the SDP.
    /// </para>
    /// </remarks>
    public class MetadataTrack : TrackBase
    {
        /// <summary>
        /// The media type the SDP announces this track as.
        /// </summary>
        /// <remarks>
        /// ONVIF's, by default, which is what a video management system looks for. Anything reading
        /// this track decides what to do with the documents by what this says they are.
        /// </remarks>
        public string MediaType { get; set; } = "vnd.onvif.metadata";

        public override string Codec => MediaType;

        /// <summary>
        /// Track ID. Used to identify the track in the SDP, and its place in the stream.
        /// </summary>
        /// <remarks>
        /// Two by default, after the one video and one audio track a stream usually starts with. A
        /// stream carrying more than that gives its tracks IDs of their own.
        /// </remarks>
        public override int ID { get; set; } = (int)TrackType.Metadata;

        /// <summary>
        /// What kind of media this track carries.
        /// </summary>
        public override TrackType Kind => TrackType.Metadata;

        /// <summary>
        /// Clock rate. 90 kHz, the same as video, so that a document can be put at the moment in the
        /// picture it describes.
        /// </summary>
        public int ClockRate { get; set; } = 90000;

        /// <summary>
        /// Payload type. Metadata has no static one, so it takes a dynamic number.
        /// </summary>
        public override int PayloadType { get; set; } = 107;

        /// <summary>
        /// Is the track ready? There is nothing to configure, so always.
        /// </summary>
        public override bool IsReady => true;

        /// <summary>
        /// Build the SDP for this track.
        /// </summary>
        /// <remarks>
        /// An application media section, which is what SDP has for a stream that is neither sound nor
        /// pictures. A client that does not know the media type passes over the section and sets up
        /// the rest of the stream as it would have anyway.
        /// </remarks>
        public override StringBuilder BuildSDP(StringBuilder sdp)
        {
            sdp.Append($"m=application 0 RTP/{RtpProfile} {PayloadType}\r\n");
            sdp.Append($"a=control:trackID={ID}\r\n");
            sdp.Append($"a=rtpmap:{PayloadType} {MediaType}/{ClockRate}\r\n");

            return sdp;
        }

        /// <summary>
        /// Sends one or more documents, encoded as UTF-8.
        /// </summary>
        /// <remarks>
        /// A convenience over the raw form, since what this track carries is text and encoding it by
        /// hand at every call site is a way of encoding it differently at one of them.
        /// </remarks>
        public void FeedInRawSamples(uint rtpTimestamp, params string[] documents)
        {
            if (documents == null)
                throw new ArgumentNullException(nameof(documents));

            var samples = new List<ReadOnlyMemory<byte>>(documents.Length);

            foreach (string document in documents)
            {
                if (!string.IsNullOrEmpty(document))
                {
                    samples.Add(new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(document)));
                }
            }

            FeedInRawSamples(rtpTimestamp, samples);
        }

        /// <summary>
        /// Creates RTP packets.
        /// </summary>
        /// <param name="samples">Documents, each cut into as many packets as it needs.</param>
        /// <param name="rtpTimestamp">RTP timestamp in the timescale of the track.</param>
        /// <param name="packets">Where to build them, and what holds them afterwards.</param>
        public override void CreateRtpPackets(List<ReadOnlyMemory<byte>> samples, uint rtpTimestamp, RtpPackets packets)
        {
            int payloadMTU = PayloadMTU();

            for (int s = 0; s < samples.Count; s++)
            {
                ReadOnlyMemory<byte> document = samples[s];
                bool isLastSample = s == samples.Count - 1;

                int at = 0;

                while (at < document.Length)
                {
                    int take = Math.Min(payloadMTU, document.Length - at);
                    bool endOfDocument = at + take >= document.Length;

                    int size = 12 + take;
                    Memory<byte> rtpPacket = packets.Rent(size);

                    // The marker is what says a document has ended, and so where a receiver should
                    // stop collecting and start reading.
                    bool rtpMarker = endOfDocument && isLastSample;

                    RTPPacketUtil.WriteHeader(rtpPacket.Span,
                        RTPPacketUtil.RTP_VERSION, false, false, 0, rtpMarker, PayloadType);
                    RTPPacketUtil.WriteTS(rtpPacket.Span, rtpTimestamp);

                    document.Slice(at, take).CopyTo(rtpPacket.Slice(12));

                    at += take;
                }
            }
        }
    }
}
