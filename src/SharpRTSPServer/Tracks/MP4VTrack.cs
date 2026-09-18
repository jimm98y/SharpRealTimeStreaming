using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace SharpRTSPServer
{
    /// <summary>
    /// MPEG-4 Part 2 video, the elementary stream carried as it stands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no payload header at all: the bitstream is cut at whatever the packets will hold and
    /// sent. What tells a receiver where a frame ends is the marker bit on the last packet of it,
    /// and what tells it how to start decoding is the configuration in the SDP - the headers that
    /// open the stream, which a receiver joining later would otherwise never see.
    /// </para>
    /// <para>
    /// Cuts are made on a whole number of bytes and preferably at the start of a header, which the
    /// specification asks for where it can be had. It is not something a receiver depends on: it is
    /// there so that losing one packet costs the part of the frame it held rather than the sense of
    /// everything after it.
    /// </para>
    /// <see href="https://datatracker.ietf.org/doc/html/rfc6416#section-6" />
    /// </remarks>
    public class MP4VTrack : TrackBase
    {
        public override string Codec => "MP4V-ES";

        /// <summary>
        /// Track ID. Used to identify the track in the SDP.
        /// </summary>
        public override int ID { get; set; } = 0;

        /// <summary>
        /// What kind of media this track carries.
        /// </summary>
        public override TrackType Kind => TrackType.Video;

        /// <summary>
        /// Clock rate. 90 kHz, as video is.
        /// </summary>
        public int ClockRate { get; } = 90000;

        /// <summary>
        /// Payload type. MPEG-4 video has no static one, so it takes a dynamic number.
        /// </summary>
        public override int PayloadType { get; set; } = 96;

        /// <summary>
        /// The profile and level a receiver needs to be able to decode, as the SDP reports it.
        /// </summary>
        /// <remarks>
        /// 1 is Simple Profile Level 1. It is announced rather than worked out, because it says what
        /// a decoder must be capable of and only the encoder knows.
        /// </remarks>
        public int ProfileLevelId { get; set; } = 1;

        /// <summary>
        /// The headers that open the stream, which the SDP carries so that a receiver can decode
        /// without having been there at the start.
        /// </summary>
        /// <remarks>
        /// The visual object sequence, visual object and video object layer headers, exactly as they
        /// appear in the bitstream. Without them a decoder has nothing to say how large the picture
        /// is or how it is coded, and the first frames are wasted.
        /// </remarks>
        public byte[] ConfigurationHeaders { get; set; }

        /// <summary>
        /// Is the track ready? Not until the headers a receiver needs are known.
        /// </summary>
        public override bool IsReady => ConfigurationHeaders != null && ConfigurationHeaders.Length > 0;

        public MP4VTrack(byte[] configurationHeaders = null)
        {
            ConfigurationHeaders = configurationHeaders;
        }

        /// <summary>
        /// Build the SDP for this track.
        /// </summary>
        public override StringBuilder BuildSDP(StringBuilder sdp)
        {
            if (!IsReady)
            {
                throw new InvalidOperationException(
                    $"{nameof(MP4VTrack)} has no configuration headers, so a receiver would have nothing to decode with. " +
                    $"Set {nameof(ConfigurationHeaders)} to the visual object sequence, visual object and video object layer headers.");
            }

            sdp.Append($"m=video 0 RTP/{RtpProfile} {PayloadType}\r\n");
            sdp.Append($"a=control:trackID={ID}\r\n");
            sdp.Append($"a=rtpmap:{PayloadType} {Codec}/{ClockRate}\r\n");
            sdp.Append($"a=fmtp:{PayloadType} profile-level-id={ProfileLevelId};config={AsHex(ConfigurationHeaders)}\r\n");

            return sdp;
        }

        private static string AsHex(byte[] bytes)
        {
            var hex = new StringBuilder(bytes.Length * 2);

            foreach (byte b in bytes)
            {
                hex.Append(b.ToString("X2"));
            }

            return hex.ToString();
        }

        /// <summary>
        /// Creates RTP packets.
        /// </summary>
        /// <param name="samples">Video frames, each cut into as many packets as it needs.</param>
        /// <param name="rtpTimestamp">RTP timestamp in the timescale of the track.</param>
        /// <param name="packets">Where to build them, and what holds them afterwards.</param>
        public override void CreateRtpPackets(List<ReadOnlyMemory<byte>> samples, uint rtpTimestamp, RtpPackets packets)
        {
            int payloadMTU = PayloadMTU();

            for (int s = 0; s < samples.Count; s++)
            {
                ReadOnlyMemory<byte> frame = samples[s];
                bool isLastSample = s == samples.Count - 1;

                int at = 0;

                while (at < frame.Length)
                {
                    int remaining = frame.Length - at;
                    int take = remaining <= payloadMTU ? remaining : CutAt(frame.Span, at, payloadMTU);

                    bool endOfFrame = at + take >= frame.Length;

                    int size = 12 + take;
                    Memory<byte> rtpPacket = packets.Rent(size);

                    // The marker says this is the last packet of a frame, which is the only thing
                    // telling a receiver where one ends.
                    bool rtpMarker = endOfFrame && isLastSample;

                    RTPPacketUtil.WriteHeader(rtpPacket.Span,
                        RTPPacketUtil.RTP_VERSION, false, false, 0, rtpMarker, PayloadType);
                    RTPPacketUtil.WriteTS(rtpPacket.Span, rtpTimestamp);

                    frame.Slice(at, take).CopyTo(rtpPacket.Slice(12));

                    at += take;
                }
            }

        }

        /// <summary>
        /// How much of the frame to put in the next packet.
        /// </summary>
        /// <remarks>
        /// As much as will fit, but ending just before a start code if one falls in the last part of
        /// what would be sent - so that a packet begins at a header rather than in the middle of one.
        /// Only worth doing if it leaves the packet reasonably full; cutting a packet in half to
        /// tidy a boundary costs more than the boundary is worth.
        /// </remarks>
        private static int CutAt(ReadOnlySpan<byte> frame, int from, int payloadMTU)
        {
            int limit = from + payloadMTU;
            int leastWorthTaking = from + (payloadMTU / 2);

            // Backwards from the end, so the cut lands as late as it can while still being at a
            // header. The first three bytes of a start code are 00 00 01.
            for (int at = limit - 1; at > leastWorthTaking; at--)
            {
                if (frame[at] == 0x01 && frame[at - 1] == 0x00 && frame[at - 2] == 0x00)
                {
                    return at - 2 - from;
                }
            }

            return payloadMTU;
        }
    }
}
