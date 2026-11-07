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
using SharpRTCP;

namespace SharpRTSPServer
{
    /// <summary>
    /// What the server offered for writing RTCP before SharpRTCP had it. Kept so code written
    /// against it still builds and runs; every member hands the work to <see cref="RTCPUtilities"/>.
    /// </summary>
    public static class RTCPUtils
    {
        [Obsolete("Use SharpRTCP.RTCPUtilities.Version.")]
        public const int RTCP_VERSION = RTCPUtilities.Version;

        [Obsolete("Use SharpRTCP.PacketType.SenderReport.")]
        public const int RTCP_PACKET_TYPE_SENDER_REPORT = PacketType.SenderReport;

        [Obsolete("Use SharpRTCP.PacketType.Goodbye.")]
        public const int RTCP_PACKET_TYPE_BYE = PacketType.Goodbye;

        [Obsolete("Use SharpRTCP.RTCPUtilities.WriteHeader.")]
        public static void WriteRTCPHeader(Span<byte> rtcp, int version, bool hasPadding, int count, int packetType, int length, uint ssrc)
        {
            RTCPUtilities.WriteHeader(rtcp, count, packetType, length, ssrc, hasPadding);

            // RTCPUtilities writes the only version there is; this was asked for another
            if (version != RTCPUtilities.Version)
            {
                rtcp[0] = (byte)((version << 6) + (rtcp[0] & 0x3F));
            }
        }

        /// <summary>
        /// The sender information of a sender report: bytes 8 to 27, the NTP and RTP time and the
        /// counters. The header before it is the caller's, written with <see cref="WriteRTCPHeader"/>.
        /// </summary>
        [Obsolete("Use SharpRTCP.RTCPUtilities.WriteSenderReport, which writes the header as well.")]
        public static void WriteSenderReport(Span<byte> rtcpSenderReport, DateTime now, uint rtp_timestamp, uint rtpPacketCount, uint octetCount)
        {
            var report = new SenderReport
            {
                NTPTimestamp = SenderReport.ToNTPTimestamp(now),
                RTPTimestamp = rtp_timestamp,
                PacketCount = rtpPacketCount,
                OctetCount = octetCount
            };

            // the whole packet is written, but only its sender information was ever this method's to write
            byte[] packet = RTCPUtilities.CreateSenderReport(report);
            packet.AsSpan(8, 20).CopyTo(rtcpSenderReport.Slice(8));
        }
    }
}
