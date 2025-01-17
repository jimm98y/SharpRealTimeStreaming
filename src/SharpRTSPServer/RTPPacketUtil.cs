using System;
using System.Buffers.Binary;

namespace SharpRTSPServer
{
    public static class RTPPacketUtil
    {
        public const int RTP_VERSION = 2;

        public static void WriteHeader(
            Span<byte> rtpPacket,
            int rtpVersion,
            bool rtpPadding,
            bool rtpExtension,
            int rtpCsrcCount,
            bool rtpMarker,
            int rtpPayloadType)
        {
            rtpPacket[0] = (byte)((rtpVersion << 6) | ((rtpPadding ? 1 : 0) << 5) | ((rtpExtension ? 1 : 0) << 4) | rtpCsrcCount);
            rtpPacket[1] = (byte)(((rtpMarker ? 1 : 0) << 7) | (rtpPayloadType & 0x7F));
        }

        public static void WriteSequenceNumber(Span<byte> rtpPacket, ushort sequenceId)
        {
            BinaryPrimitives.WriteUInt16BigEndian(rtpPacket.Slice(2), sequenceId);
        }

        public static void WriteSSRC(Span<byte> rtp_packet, uint ssrc)
        {
            BinaryPrimitives.WriteUInt32BigEndian(rtp_packet.Slice(8), ssrc);
        }

        public static uint ReadTS(ReadOnlySpan<byte> data)
        {
            uint rtpTimestamp = ((uint)data[4] << 24) + (uint)(data[5] << 16) + (uint)(data[6] << 8) + data[7];
            return rtpTimestamp;
        }

        public static void WriteTS(Span<byte> rtp_packet, uint ts)
        {
            BinaryPrimitives.WriteUInt32BigEndian(rtp_packet.Slice(4), ts);
        }
    }
}
