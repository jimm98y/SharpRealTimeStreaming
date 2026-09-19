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

using SharpRTSPServer.Logging;
using System;
using System.Collections.Generic;
using System.Text;

namespace SharpRTSPServer
{
    /// <summary>
    /// Opus Track.
    /// </summary>
    public class OpusTrack : TrackBase
    {
        /// <summary>
        /// Opus Audio Codec name.
        /// </summary>
        public override string Codec => "opus";

        /// <summary>
        /// Track ID. Used to identify the track in the SDP.
        /// </summary>
        public override int ID { get; set; } = 1;

        /// <summary>
        /// What kind of media this track carries.
        /// </summary>
        public override TrackType Kind => TrackType.Audio;

        /// <summary>
        /// Sampling rate. Opus always requires 48000.
        /// </summary>
        public int SamplingRate { get; set; } = 48000;

        /// <summary>
        /// Number of channels. Opus always requires 2.
        /// </summary>
        public int Channels { get; set; } = 2;

        /// <summary>
        /// Is the track ready?
        /// </summary>
        public override bool IsReady { get { return true; } }

        private int _payloadType = -1;

        /// <summary>
        /// Payload type. Opus uses a dynamic payload type, which by default we calculate as 96 + track ID.
        /// </summary>
        public override int PayloadType
        {
            get
            {
                if (_payloadType < 0)
                {
                    return RTSPServer.DYNAMIC_PAYLOAD_TYPE + ID;
                }
                else
                {
                    return _payloadType;
                }
            }
            set
            {
                _payloadType = value;
            }
        }

        /// <summary>
        /// Ctor.
        /// </summary>
        public OpusTrack()
        {
        }

        /// <summary>
        /// Build the SDP for this track.
        /// </summary>
        /// <param name="sdp">SDP <see cref="StringBuilder"/>.</param>
        /// <returns><see cref="StringBuilder"/>.</returns>
        public override StringBuilder BuildSDP(StringBuilder sdp)
        {
            if (SamplingRate != 48000)
            {
                Logger.Warning($"Opus track set to use unsupported sampling rate {SamplingRate}. Opus requires 48000.");
            }

            if (Channels != 2)
            {
                Logger.Warning($"Opus track set to use unsupported channels {Channels}. Opus requires 2.");
            }

            sdp.Append($"m=audio 0 RTP/{RtpProfile} {PayloadType}\r\n"); // <---- Payload Type 0 means G711 ULAW, 96+ means dynamic payload type
            sdp.Append($"a=control:trackID={ID}\r\n");

            // The RTP clock rate in "a=rtpmap" MUST be 48000, and the number of channels MUST be 2.
            sdp.Append($"a=rtpmap:{PayloadType} {Codec}/{SamplingRate}/{Channels}\r\n");

            return sdp;
        }

        /// <summary>
        /// Creates RTP packets.
        /// </summary>
        /// <param name="samples">An array of Opus fragments. By default single fragment is expected.</param>
        /// <param name="rtpTimestamp">RTP timestamp in the timescale of the track.</param>
        /// <param name="packets">Where to build them, and what holds them afterwards.</param>
        public override void CreateRtpPackets(List<ReadOnlyMemory<byte>> samples, uint rtpTimestamp, RtpPackets packets)
        {
            for (int i = 0; i < samples.Count; i++)
            {
                var audioPacket = samples[i];

                // Put the whole Audio Packet into one RTP packet.
                // 12 is header size when there are no CSRCs or extensions
                var size = 12 + audioPacket.Length;
                Memory<byte> rtpPacket = packets.Rent(size);

                const bool rtpPadding = false;
                const bool rtpHasExtension = false;
                int rtpCsrcCount = 0;
                const bool rtpMarker = true; // always 1 as this is the last (and only) RTP packet for this audio timestamp

                RTPPacketUtil.WriteHeader(rtpPacket.Span,
                    RTPPacketUtil.RTP_VERSION, rtpPadding, rtpHasExtension, rtpCsrcCount, rtpMarker, PayloadType);

                // sequence number is set just before send
                RTPPacketUtil.WriteTS(rtpPacket.Span, rtpTimestamp);

                // Now append the audio packet
                audioPacket.CopyTo(rtpPacket.Slice(12));

            }
        }
    }
}
