using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace SharpRTSPServer
{
    /// <summary>
    /// AMR narrowband and wideband speech.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sent octet aligned, which is the mode where everything starts on a byte boundary: a byte
    /// saying what rate the sender would like back, one byte describing each frame, then the frames
    /// themselves. The other mode saves a few bits per packet by running them together across byte
    /// boundaries, and costs anyone reading it a great deal of shifting to get them out again.
    /// </para>
    /// <para>
    /// Frames are taken in storage format - the one byte header and then the speech - which is what
    /// an encoder produces and what sits in a .amr file. The frame type in that header is what says
    /// how long the frame is, so a caller handing over several at once does not have to say where
    /// each ends.
    /// </para>
    /// <see href="https://datatracker.ietf.org/doc/html/rfc4867" />
    /// </remarks>
    public class AMRTrack : TrackBase
    {
        /// <summary>No particular rate is being asked of the other end.</summary>
        private const byte NO_CODEC_MODE_REQUEST = 0xF0;

        /// <summary>The last frame in a packet has its "another follows" bit clear.</summary>
        private const byte LAST_FRAME = 0x00;

        private const byte FOLLOWED_BY_ANOTHER = 0x80;

        /// <summary>Set to say the frame arrived intact, which is the only kind produced here.</summary>
        private const byte GOOD_FRAME = 0x04;

        /// <summary>
        /// Whether this is the wideband codec rather than the narrowband one.
        /// </summary>
        public bool IsWideband { get; }

        public override string Codec => IsWideband ? "AMR-WB" : "AMR";

        /// <summary>
        /// Track ID. Used to identify the track in the SDP.
        /// </summary>
        public override int ID { get; set; } = 1;

        /// <summary>
        /// What kind of media this track carries.
        /// </summary>
        public override TrackType Kind => TrackType.Audio;

        /// <summary>
        /// Sampling rate: 8 kHz narrowband, 16 kHz wideband.
        /// </summary>
        public int SamplingRate => IsWideband ? 16000 : 8000;

        /// <summary>
        /// Payload type. AMR has no static one, so it takes a dynamic number.
        /// </summary>
        public override int PayloadType { get; set; } = 98;

        /// <summary>
        /// Is the track ready?
        /// </summary>
        public override bool IsReady => true;

        public AMRTrack(bool wideband = false)
        {
            IsWideband = wideband;
        }

        /// <summary>
        /// Build the SDP for this track.
        /// </summary>
        /// <remarks>
        /// The octet-align parameter is not a preference: a receiver that misses it will try to read
        /// the payload bit packed and make nothing of it.
        /// </remarks>
        public override StringBuilder BuildSDP(StringBuilder sdp)
        {
            sdp.Append($"m=audio 0 RTP/{RtpProfile} {PayloadType}\r\n");
            sdp.Append($"a=control:trackID={ID}\r\n");
            sdp.Append($"a=rtpmap:{PayloadType} {Codec}/{SamplingRate}\r\n");
            sdp.Append($"a=fmtp:{PayloadType} octet-align=1\r\n");

            return sdp;
        }

        /// <summary>
        /// How long a frame of each type is, in bytes, not counting the header byte.
        /// </summary>
        /// <remarks>
        /// Indexed by frame type. The speech rates are the first eight; what follows are comfort
        /// noise and the no-data type, and the gaps between them are types that do not exist.
        /// <see href="https://datatracker.ietf.org/doc/html/rfc4867#section-3.6" />
        /// </remarks>
        private static readonly int[] NarrowbandFrameBytes =
            { 12, 13, 15, 17, 19, 20, 26, 31, 5, -1, -1, -1, -1, -1, -1, 0 };

        private static readonly int[] WidebandFrameBytes =
            { 17, 23, 32, 36, 40, 46, 50, 58, 60, 5, -1, -1, -1, -1, 0, 0 };

        /// <summary>
        /// The length of the frame whose storage header this is, or -1 if there is no such frame.
        /// </summary>
        public int FrameLength(byte storageHeader)
        {
            int frameType = (storageHeader >> 3) & 0x0F;

            return IsWideband ? WidebandFrameBytes[frameType] : NarrowbandFrameBytes[frameType];
        }

        /// <summary>
        /// Creates RTP packets.
        /// </summary>
        /// <param name="samples">
        /// Speech frames in storage format, a header byte and then the frame. Each sample becomes one
        /// packet; several frames in one sample are sent together, which is what a sender does to cut
        /// down the per packet overhead of a slow codec.
        /// </param>
        /// <param name="rtpTimestamp">RTP timestamp in the timescale of the track.</param>
        /// <param name="packets">Where to build them, and what holds them afterwards.</param>
        public override void CreateRtpPackets(List<ReadOnlyMemory<byte>> samples, uint rtpTimestamp, RtpPackets packets)
        {
            foreach (ReadOnlyMemory<byte> sample in samples)
            {
                List<(byte Header, ReadOnlyMemory<byte> Speech)> frames = SplitIntoFrames(sample);

                if (frames.Count == 0)
                {
                    continue;
                }

                // 12 for RTP, one for the mode request, one describing each frame, then the frames.
                int size = 12 + 1 + frames.Count;

                foreach (var frame in frames)
                {
                    size += frame.Speech.Length;
                }

                Memory<byte> rtpPacket = packets.Rent(size);

                RTPPacketUtil.WriteHeader(rtpPacket.Span,
                    RTPPacketUtil.RTP_VERSION, false, false, 0, true, PayloadType);
                RTPPacketUtil.WriteTS(rtpPacket.Span, rtpTimestamp);

                Span<byte> payload = rtpPacket.Span.Slice(12);
                payload[0] = NO_CODEC_MODE_REQUEST;

                // Every frame is described before any of them is written, because a receiver has to
                // know how many there are before it can find where the first one starts.
                for (int i = 0; i < frames.Count; i++)
                {
                    byte frameType = (byte)((frames[i].Header >> 3) & 0x0F);
                    byte follows = i + 1 < frames.Count ? FOLLOWED_BY_ANOTHER : LAST_FRAME;

                    payload[1 + i] = (byte)(follows | (frameType << 3) | GOOD_FRAME);
                }

                int at = 1 + frames.Count;

                foreach (var frame in frames)
                {
                    frame.Speech.Span.CopyTo(payload.Slice(at));
                    at += frame.Speech.Length;
                }

            }

        }

        /// <summary>
        /// Walks a run of storage format frames, using each header to find the next.
        /// </summary>
        private List<(byte Header, ReadOnlyMemory<byte> Speech)> SplitIntoFrames(ReadOnlyMemory<byte> sample)
        {
            var frames = new List<(byte, ReadOnlyMemory<byte>)>();
            int at = 0;

            while (at < sample.Length)
            {
                byte header = sample.Span[at];
                int length = FrameLength(header);

                if (length < 0)
                {
                    throw new ArgumentException(
                        $"AMR frame type {(header >> 3) & 0x0F} does not exist, so the sample cannot be read.",
                        nameof(sample));
                }

                if (at + 1 + length > sample.Length)
                {
                    throw new ArgumentException(
                        "An AMR frame runs past the end of the sample it is in.", nameof(sample));
                }

                frames.Add((header, sample.Slice(at + 1, length)));
                at += 1 + length;
            }

            return frames;
        }
    }
}
