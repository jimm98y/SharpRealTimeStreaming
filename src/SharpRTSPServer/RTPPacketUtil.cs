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

        public static ushort ReadSequenceNumber(ReadOnlySpan<byte> rtpPacket)
        {
            return BinaryPrimitives.ReadUInt16BigEndian(rtpPacket.Slice(2));
        }

        public static uint ReadSSRC(ReadOnlySpan<byte> rtpPacket)
        {
            return BinaryPrimitives.ReadUInt32BigEndian(rtpPacket.Slice(8));
        }

        public static void WriteSSRC(Span<byte> rtp_packet, uint ssrc)
        {
            BinaryPrimitives.WriteUInt32BigEndian(rtp_packet.Slice(8), ssrc);
        }

        public static uint ReadTS(byte[] data)
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
