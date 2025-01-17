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
            sdp.Append($"m=audio 0 RTP/AVP {PayloadType}\n");
            sdp.Append($"a=control:trackID={ID}\n");
            sdp.Append($"a=rtpmap:{PayloadType} {Codec}/{SamplingRate}\n");
            return sdp;
        }

        /// <summary>
        /// Creates RTP packets.
        /// </summary>
        /// <param name="samples">An array of PCMU fragments. By default single fragment is expected.</param>
        /// <param name="rtpTimestamp">RTP timestamp in the timescale of the track.</param>
        /// <returns>RTP packets.</returns>
        public override List<IMemoryOwner<byte>> CreateRtpPackets(ReadOnlySequence<byte> samples, uint rtpTimestamp)
        {
            List<Memory<byte>> rtpPackets = new List<Memory<byte>>();
            List<IMemoryOwner<byte>> memoryOwners = new List<IMemoryOwner<byte>>();

            foreach (var audioPacket in samples)
            {
                var size = 12 + audioPacket.Length;
                var owner = AdjustedSizeMemoryOwner.Rent(size);
                memoryOwners.Add(owner);

                var rtpPacket = owner.Memory.Slice(0, size);

                const bool rtpPadding = false;
                const bool rtpHasExtension = false;
                int rtpCsrcCount = 0;
                const bool rtpMarker = true;

                RTPPacketUtil.WriteHeader(rtpPacket.Span,
                    RTPPacketUtil.RTP_VERSION, rtpPadding, rtpHasExtension, rtpCsrcCount, rtpMarker, PayloadType);

                RTPPacketUtil.WriteTS(rtpPacket.Span, rtpTimestamp);
                audioPacket.CopyTo(rtpPacket.Slice(12));
                rtpPackets.Add(rtpPacket);
            }

            return memoryOwners;
        }
    }

    /// <summary>
    /// G711 PCMA (A-LAW) track.
    /// </summary>
    public class PCMATrack : TrackBase
    {
        /// <summary>
        /// PCMA Audio Codec name.
        /// </summary>
        public override string Codec => "PCMA";

        /// <summary>
        /// Track ID. Used to identify the track in the SDP.
        /// </summary>
        public override int ID { get; set; } = 1;

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
        /// Payload type. PCMA uses static payload type 8.
        /// </summary>
        public override int PayloadType { get; set; } = 8;

        /// <summary>
        /// Build the SDP for this track.
        /// </summary>
        /// <param name="sdp">SDP <see cref="StringBuilder"/>.</param>
        /// <returns><see cref="StringBuilder"/>.</returns>
        public override StringBuilder BuildSDP(StringBuilder sdp)
        {
            sdp.Append($"m=audio 0 RTP/AVP {PayloadType}\n");
            sdp.Append($"a=control:trackID={ID}\n");
            sdp.Append($"a=rtpmap:{PayloadType} {Codec}/{SamplingRate}\n");
            return sdp;
        }

        /// <summary>
        /// Creates RTP packets.
        /// </summary>
        /// <param name="samples">An array of PCMA fragments. By default single fragment is expected.</param>
        /// <param name="rtpTimestamp">RTP timestamp in the timescale of the track.</param>
        /// <returns>RTP packets.</returns>
        public override List<IMemoryOwner<byte>> CreateRtpPackets(ReadOnlySequence<byte> samples, uint rtpTimestamp)
        {
            List<IMemoryOwner<byte>> memoryOwners = new List<IMemoryOwner<byte>>();

            foreach (var audioPacket in samples)
            {
                var size = 12 + audioPacket.Length;
                var owner = AdjustedSizeMemoryOwner.Rent(size);
                memoryOwners.Add(owner);

                var rtpPacket = owner.Memory;

                const bool rtpPadding = false;
                const bool rtpHasExtension = false;
                int rtpCsrcCount = 0;
                const bool rtpMarker = true; 

                RTPPacketUtil.WriteHeader(rtpPacket.Span,
                    RTPPacketUtil.RTP_VERSION, rtpPadding, rtpHasExtension, rtpCsrcCount, rtpMarker, PayloadType);

                RTPPacketUtil.WriteTS(rtpPacket.Span, rtpTimestamp);
                audioPacket.CopyTo(rtpPacket.Slice(12));
            }

            return memoryOwners;
        }
    }
}
