using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace SharpRTSPServer
{
    /// <summary>
    /// The rates G.726 runs at, in kilobits per second.
    /// </summary>
    /// <remarks>
    /// The rate is part of the codec's name in the SDP - a receiver cannot work it out from the
    /// packets, because the payload is a run of samples with nothing marking where one ends.
    /// </remarks>
    public enum G726BitRate
    {
        Rate16 = 16,
        Rate24 = 24,
        Rate32 = 32,
        Rate40 = 40,
    }

    /// <summary>
    /// G.726 ADPCM audio.
    /// </summary>
    /// <remarks>
    /// The payload is the ADPCM data and nothing else: no header of its own, no framing, no padding
    /// to a byte boundary between packets. What a receiver needs in order to read it - the rate, and
    /// so how many bits a sample takes - is in the SDP.
    /// <see href="https://datatracker.ietf.org/doc/html/rfc3551#section-4.5.4" />
    /// </remarks>
    public class G726Track : TrackBase
    {
        /// <summary>
        /// The rate this track is encoded at.
        /// </summary>
        public G726BitRate BitRate { get; }

        /// <summary>
        /// Codec name, which carries the rate with it.
        /// </summary>
        public override string Codec => $"G726-{(int)BitRate}";

        /// <summary>
        /// Track ID. Used to identify the track in the SDP.
        /// </summary>
        public override int ID { get; set; } = 1;

        /// <summary>
        /// What kind of media this track carries.
        /// </summary>
        public override TrackType Kind => TrackType.Audio;

        /// <summary>
        /// Sampling rate. G.726 is a narrowband codec and runs at 8 kHz whatever its bit rate.
        /// </summary>
        public int SamplingRate { get; } = 8000;

        /// <summary>
        /// Payload type. G.726 has no static one, so it takes a dynamic number.
        /// </summary>
        public override int PayloadType { get; set; } = 97;

        /// <summary>
        /// Is the track ready?
        /// </summary>
        public override bool IsReady => true;

        public G726Track(G726BitRate bitRate = G726BitRate.Rate32)
        {
            if (!Enum.IsDefined(typeof(G726BitRate), bitRate))
            {
                throw new ArgumentOutOfRangeException(nameof(bitRate), bitRate,
                    "G.726 runs at 16, 24, 32 or 40 kbit/s.");
            }

            BitRate = bitRate;
        }

        /// <summary>
        /// Build the SDP for this track.
        /// </summary>
        public override StringBuilder BuildSDP(StringBuilder sdp)
        {
            sdp.Append($"m=audio 0 RTP/{RtpProfile} {PayloadType}\r\n");
            sdp.Append($"a=control:trackID={ID}\r\n");
            sdp.Append($"a=rtpmap:{PayloadType} {Codec}/{SamplingRate}\r\n");

            return sdp;
        }

        /// <summary>
        /// Creates RTP packets.
        /// </summary>
        /// <param name="samples">Runs of ADPCM data, each of which becomes one packet.</param>
        /// <param name="rtpTimestamp">RTP timestamp in the timescale of the track.</param>
        public override (List<Memory<byte>>, List<IMemoryOwner<byte>>) CreateRtpPackets(
            List<ReadOnlyMemory<byte>> samples, uint rtpTimestamp)
        {
            var rtpPackets = new List<Memory<byte>>(samples.Count);
            var memoryOwners = new List<IMemoryOwner<byte>>(samples.Count);

            for (int i = 0; i < samples.Count; i++)
            {
                ReadOnlyMemory<byte> audio = samples[i];
                int size = 12 + audio.Length;

                IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(size);
                memoryOwners.Add(owner);

                Memory<byte> rtpPacket = owner.Memory.Slice(0, size);

                const bool rtpPadding = false;
                const bool rtpHasExtension = false;
                const int rtpCsrcCount = 0;

                // Audio has no frames to end, so every packet is the end of what it carries.
                const bool rtpMarker = true;

                RTPPacketUtil.WriteHeader(rtpPacket.Span,
                    RTPPacketUtil.RTP_VERSION, rtpPadding, rtpHasExtension, rtpCsrcCount, rtpMarker, PayloadType);

                RTPPacketUtil.WriteTS(rtpPacket.Span, rtpTimestamp);
                audio.CopyTo(rtpPacket.Slice(12));

                rtpPackets.Add(rtpPacket);
            }

            return (rtpPackets, memoryOwners);
        }
    }
}
