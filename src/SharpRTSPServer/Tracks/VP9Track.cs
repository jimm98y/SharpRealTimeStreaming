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
    /// VP9 video track.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sent in the non-flexible mode with a single spatial and temporal layer, which is what a VP9
    /// file holds. Every packet carries a 15 bit picture ID, and the first packet of a key frame
    /// carries the scalability structure with the picture size, so a receiver joining there knows
    /// what it is about to decode without waiting for the decoder to tell it.
    /// </para>
    /// <para>
    /// A sample is sent as it is given, superframe and index included. A file packs a hidden
    /// frame - one that only fills a reference buffer - together with the frame shown after it,
    /// and the payload format lets the two share a timestamp. Splitting them into separate frames
    /// would be within the specification too, but receivers built after FFmpeg's start a new
    /// frame on every B bit and throw away what they had, which is the hidden frame and with it
    /// every picture that refers to it. A VP9 decoder takes a superframe as Annex B describes it.
    /// </para>
    /// <see href="https://datatracker.ietf.org/doc/html/rfc9628" />
    /// </remarks>
    public class VP9Track : TrackBase
    {
        /// <summary>
        /// VP9 Video Codec name.
        /// </summary>
        public override string Codec => "VP9";

        /// <summary>
        /// Default video track clock rate.
        /// </summary>
        public const int DEFAULT_CLOCK = 90000;

        /// <summary>
        /// Track ID. Used to identify the track in the SDP.
        /// </summary>
        public override int ID { get; set; } = 0;

        /// <summary>
        /// What kind of media this track carries.
        /// </summary>
        public override TrackType Kind => TrackType.Video;

        /// <summary>
        /// Video clock rate. Default value is 90000.
        /// </summary>
        public int VideoClock { get; set; } = DEFAULT_CLOCK;

        private int _payloadType = -1;

        /// <summary>
        /// Payload type. VP9 uses a dynamic payload type, which by default we calculate as 96 + track ID.
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
        /// The VP9 profile the SDP announces, 0 to 3.
        /// </summary>
        /// <remarks>
        /// Null announces the profile of the last key frame sent, and nothing at all before there
        /// has been one - which a receiver reads as profile 0, by far the most common.
        /// </remarks>
        public int? ProfileId { get; set; }

        /// <summary>
        /// Always ready: VP9 has no configuration out of band, everything a decoder needs is in
        /// the header of a key frame.
        /// </summary>
        public override bool IsReady => true;

        private int _seenProfile = -1;

        // 15 bits, from a random start as RTP sequence numbers are, so that a receiver does not
        // take a restarted sender for the stream it was following.
        private int _pictureId = (int)(RandomGenerator.NextUInt32() & 0x7FFF);

        /// <summary>
        /// Ctor.
        /// </summary>
        /// <param name="clock">VP9 clock. Default value is 90000.</param>
        public VP9Track(int clock = DEFAULT_CLOCK)
        {
            VideoClock = clock;
        }

        public override StringBuilder BuildSDP(StringBuilder sdp)
        {
            sdp.Append($"m=video 0 RTP/{RtpProfile} {PayloadType}\r\n");
            sdp.Append($"a=control:trackID={ID}\r\n");
            sdp.Append($"a=rtpmap:{PayloadType} {Codec}/{VideoClock}\r\n");

            int profile = ProfileId ?? _seenProfile;
            if (profile >= 0)
            {
                sdp.Append($"a=fmtp:{PayloadType} profile-id={profile}\r\n");
            }

            return sdp;
        }

        private const int DESCRIPTOR_I = 0x80;
        private const int DESCRIPTOR_P = 0x40;
        private const int DESCRIPTOR_B = 0x08;
        private const int DESCRIPTOR_E = 0x04;
        private const int DESCRIPTOR_V = 0x02;

        /// <summary>
        /// Creates RTP packets.
        /// </summary>
        /// <param name="samples">VP9 frames of one picture, each a frame or a superframe with its index.</param>
        /// <param name="rtpTimestamp">RTP timestamp in the timescale of the track.</param>
        /// <param name="packets">Where to build them, and what holds them afterwards.</param>
        public override void CreateRtpPackets(List<ReadOnlyMemory<byte>> samples, uint rtpTimestamp, RtpPackets packets)
        {
            // One picture per call, and every frame of a picture has the same picture ID.
            int pictureId = _pictureId;
            _pictureId = (_pictureId + 1) & 0x7FFF;

            // Set below if a frame of the picture is one a decoder can start on.
            packets.IsKeyFrame = false;

            for (int s = 0; s < samples.Count; s++)
            {
                ReadOnlyMemory<byte> frame = samples[s];
                bool isLastSample = s == samples.Count - 1;

                bool keyFrame = FrameHeader.TryRead(frame.Span, out FrameHeader header) && header.IsKeyFrame;
                if (keyFrame)
                {
                    packets.IsKeyFrame = true;
                    _seenProfile = header.Profile;
                }

                int at = 0;

                while (at < frame.Length)
                {
                    bool startOfFrame = at == 0;

                    // The scalability structure goes once, on the first packet of a key frame:
                    // N_S = 0 for the one spatial layer, Y for its size, no picture group.
                    bool withScalability = startOfFrame && keyFrame;
                    int descriptorLen = 3 + (withScalability ? 5 : 0);

                    int take = Math.Min(PayloadMTU() - descriptorLen, frame.Length - at);
                    if (take <= 0)
                    {
                        throw new InvalidOperationException(
                            $"{nameof(PacketMTU)} of {PacketMTU} leaves no room for VP9 payload after its {descriptorLen} byte descriptor.");
                    }

                    bool endOfFrame = at + take >= frame.Length;

                    Memory<byte> rtpPacket = packets.Rent(12 + descriptorLen + take);
                    Span<byte> span = rtpPacket.Span;

                    // The marker is the end of the picture; with no spatial layers that is the end
                    // of its last frame.
                    RTPPacketUtil.WriteHeader(span, RTPPacketUtil.RTP_VERSION,
                        false, false, 0, endOfFrame && isLastSample, PayloadType);

                    // sequence number and SSRC are set just before send
                    RTPPacketUtil.WriteTS(span, rtpTimestamp);

                    //  0 1 2 3 4 5 6 7
                    // +-+-+-+-+-+-+-+-+
                    // |I|P|L|F|B|E|V|Z|
                    // +-+-+-+-+-+-+-+-+
                    // |M| PICTURE ID  |
                    // +-+-+-+-+-+-+-+-+
                    // | EXTENDED PID  |
                    // +-+-+-+-+-+-+-+-+
                    int descriptor = DESCRIPTOR_I;
                    if (!keyFrame) descriptor |= DESCRIPTOR_P;
                    if (startOfFrame) descriptor |= DESCRIPTOR_B;
                    if (endOfFrame) descriptor |= DESCRIPTOR_E;
                    if (withScalability) descriptor |= DESCRIPTOR_V;

                    span[12] = (byte)descriptor;
                    span[13] = (byte)(0x80 | (pictureId >> 8));
                    span[14] = (byte)(pictureId & 0xFF);

                    if (withScalability)
                    {
                        // | N_S |Y|G|-|-|-|
                        span[15] = 0x10;
                        span[16] = (byte)(header.Width >> 8);
                        span[17] = (byte)header.Width;
                        span[18] = (byte)(header.Height >> 8);
                        span[19] = (byte)header.Height;
                    }

                    frame.Slice(at, take).CopyTo(rtpPacket.Slice(12 + descriptorLen));

                    at += take;
                }
            }
        }

        /// <summary>
        /// What the start of a VP9 frame header says, as far as this track needs it.
        /// </summary>
        /// <remarks>
        /// The first frame of a superframe begins at its first byte, so this reads a superframe as
        /// well as a frame: a superframe that opens with a key frame is one a decoder can start on.
        /// VP9 Bitstream &amp; Decoding Process Specification v0.6, 6.2 uncompressed_header.
        /// </remarks>
        internal readonly struct FrameHeader
        {
            public int Profile { get; }
            public bool IsKeyFrame { get; }
            public int Width { get; }
            public int Height { get; }

            private FrameHeader(int profile, bool isKeyFrame, int width, int height)
            {
                Profile = profile;
                IsKeyFrame = isKeyFrame;
                Width = width;
                Height = height;
            }

            private const int KEY_FRAME = 0;
            private const int CS_RGB = 7;

            public static bool TryRead(ReadOnlySpan<byte> frame, out FrameHeader header)
            {
                header = default;
                var bits = new BitReader(frame);

                if (!bits.TryRead(2, out int frameMarker) || frameMarker != 2)
                    return false;

                if (!bits.TryRead(1, out int profileLow) || !bits.TryRead(1, out int profileHigh))
                    return false;

                int profile = (profileHigh << 1) | profileLow;
                if (profile == 3 && !bits.TryRead(1, out _))
                    return false;

                if (!bits.TryRead(1, out int showExistingFrame))
                    return false;

                // Shows a frame decoded earlier: nothing new is coded in it, and it is never a key frame.
                if (showExistingFrame == 1)
                {
                    header = new FrameHeader(profile, false, 0, 0);
                    return true;
                }

                // frame_type, show_frame, error_resilient_mode
                if (!bits.TryRead(1, out int frameType) || !bits.TryRead(2, out _))
                    return false;

                if (frameType != KEY_FRAME)
                {
                    header = new FrameHeader(profile, false, 0, 0);
                    return true;
                }

                if (!bits.TryRead(24, out int syncCode) || syncCode != 0x498342)
                    return false;

                // color_config
                if (profile >= 2 && !bits.TryRead(1, out _))
                    return false;

                if (!bits.TryRead(3, out int colorSpace))
                    return false;

                if (colorSpace != CS_RGB)
                {
                    // color_range, and subsampling_x, subsampling_y and reserved_zero for 1 and 3
                    if (!bits.TryRead(profile == 1 || profile == 3 ? 4 : 1, out _))
                        return false;
                }
                else if ((profile == 1 || profile == 3) && !bits.TryRead(1, out _))
                {
                    return false;
                }

                // frame_size
                if (!bits.TryRead(16, out int widthMinus1) || !bits.TryRead(16, out int heightMinus1))
                    return false;

                header = new FrameHeader(profile, true, widthMinus1 + 1, heightMinus1 + 1);
                return true;
            }
        }

        private ref struct BitReader
        {
            private readonly ReadOnlySpan<byte> _data;
            private int _bit;

            public BitReader(ReadOnlySpan<byte> data)
            {
                _data = data;
                _bit = 0;
            }

            public bool TryRead(int count, out int value)
            {
                value = 0;
                if (_bit + count > _data.Length * 8)
                    return false;

                for (int i = 0; i < count; i++, _bit++)
                {
                    value = (value << 1) | ((_data[_bit >> 3] >> (7 - (_bit & 7))) & 1);
                }

                return true;
            }
        }
    }
}
