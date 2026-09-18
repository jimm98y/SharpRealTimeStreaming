using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace SharpRTSPServer
{
    /// <summary>
    /// G711 PCMU (U-LAW) track.
    /// </summary>
    public class PCMUTrack : TrackBase
    {
        /// <summary>
        /// PCMU Audio Codec name.
        /// </summary>
        public override string Codec => "PCMU";

        /// <summary>
        /// Track ID. Used to identify the track in the SDP.
        /// </summary>
        public override int ID { get; set; } = 1;

        /// <summary>
        /// What kind of media this track carries.
        /// </summary>
        public override TrackType Kind => TrackType.Audio;

        /// <summary>
        /// Sampling rate.
        /// </summary>
        public int SamplingRate { get; } = 8000;

        /// <summary>
        /// Number of channels. 1 for Mono.
        /// </summary>
        public int Channels { get; } = 1;

        /// <summary>
        /// Is the track ready?
        /// </summary>
        public override bool IsReady { get { return true; } }

        /// <summary>
        /// Payload type. PCMU uses static payload type 0.
        /// </summary>
        public override int PayloadType { get; set; } = 0;

        /// <summary>
        /// Build the SDP for this track.
        /// </summary>
        /// <param name="sdp">SDP <see cref="StringBuilder"/>.</param>
        /// <returns><see cref="StringBuilder"/>.</returns>
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
        /// <param name="samples">An array of PCMU fragments. By default single fragment is expected.</param>
        /// <param name="rtpTimestamp">RTP timestamp in the timescale of the track.</param>
        /// <returns>RTP packets.</returns>
        public override void CreateRtpPackets(List<ReadOnlyMemory<byte>> samples, uint rtpTimestamp, RtpPackets packets)
        {
            for (int i = 0; i < samples.Count; i++)
            {
                var audioPacket = samples[i];
                var size = 12 + audioPacket.Length;
                Memory<byte> rtpPacket = packets.Rent(size);

                const bool rtpPadding = false;
                const bool rtpHasExtension = false;
                int rtpCsrcCount = 0;
                const bool rtpMarker = true;

                RTPPacketUtil.WriteHeader(rtpPacket.Span,
                    RTPPacketUtil.RTP_VERSION, rtpPadding, rtpHasExtension, rtpCsrcCount, rtpMarker, PayloadType);

                RTPPacketUtil.WriteTS(rtpPacket.Span, rtpTimestamp);
                audioPacket.CopyTo(rtpPacket.Slice(12));
            }
        }
    }
}
