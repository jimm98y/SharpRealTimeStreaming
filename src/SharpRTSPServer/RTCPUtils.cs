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
    public static class RTCPUtils
    {
        public const int RTCP_VERSION = 2;
        public const int RTCP_PACKET_TYPE_SENDER_REPORT = 200;
        public const int RTCP_PACKET_TYPE_BYE = 203;

        public static void WriteRTCPHeader(Span<byte> rtcp, int version, bool hasPadding, int count, int packetType, int length, uint ssrc)
        {
            rtcp[0] = (byte)((version << 6) + ((hasPadding ? 1 : 0) << 5) + count);
            rtcp[1] = (byte)packetType;
            BinaryPrimitives.WriteUInt16BigEndian(rtcp.Slice(2), (ushort)length);
            BinaryPrimitives.WriteUInt32BigEndian(rtcp.Slice(4), ssrc);
        }

        public static void WriteSenderReport(Span<byte> rtcpSenderReport, DateTime now, uint rtp_timestamp, uint rtpPacketCount, uint octetCount)
        {
            // Bytes 8, 9, 10, 11 and 12,13,14,15 are the Wall Clock
            // Bytes 16,17,18,19 are the RTP payload timestamp

            // NTP Most Significant Word is relative to 0h, 1 Jan 1900
            // This will wrap around in 2036
            DateTime ntp_start_time = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            TimeSpan tmpTime = now - ntp_start_time;
            double totalSeconds = tmpTime.TotalSeconds; // Seconds and fractions of a second

            uint ntp_msw_seconds = (uint)Math.Truncate(totalSeconds); // whole number of seconds
            uint ntp_lsw_fractions = (uint)(totalSeconds % 1 * uint.MaxValue); // fractional part, scaled between 0 and MaxInt

            // cross check...   double ntp = ntp_msw_seconds + (ntp_lsw_fractions / UInt32.MaxValue);
            BinaryPrimitives.WriteUInt32BigEndian(rtcpSenderReport.Slice(8), ntp_msw_seconds);
            BinaryPrimitives.WriteUInt32BigEndian(rtcpSenderReport.Slice(12), ntp_lsw_fractions);
            BinaryPrimitives.WriteUInt32BigEndian(rtcpSenderReport.Slice(16), rtp_timestamp);
            BinaryPrimitives.WriteUInt32BigEndian(rtcpSenderReport.Slice(20), rtpPacketCount);
            BinaryPrimitives.WriteUInt32BigEndian(rtcpSenderReport.Slice(24), octetCount);
        }
    }
}
