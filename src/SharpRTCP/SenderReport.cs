// SharpRTCP
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

namespace SharpRTCP
{
    /// <summary>
    /// A sender report (RFC 3550 6.4.1): the sender's wall clock - NTP - at an RTP timestamp of its stream, which is what
    /// puts the streams of one sender on one clock, and what it has sent so far.
    /// </summary>
    public sealed class SenderReport
    {
        // NTP Most Significant Word is relative to 0h, 1 Jan 1900. This will wrap around in 2036
        private static readonly DateTime NtpEpoch = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>The sender's SSRC.</summary>
        public uint Ssrc { get; set; }

        /// <summary>The NTP timestamp as it was sent: 32 bits of seconds since 1900, then 32 of their fraction.</summary>
        public ulong NtpTimestamp { get; set; }

        public uint RtpTimestamp { get; set; }

        /// <summary>How many RTP packets the sender has sent: 0 of one that has sent nothing yet, whose pairing is of no stream.</summary>
        public uint PacketCount { get; set; }

        public uint OctetCount { get; set; }

        /// <summary>
        /// Of a sender that receives too, a block about each source it receives (RFC 3550 6.4.1), as a receiver report has
        /// them: see <see cref="ReceiverReport"/>.
        /// </summary>
        public List<ReportBlock> Reports { get; } = new List<ReportBlock>();

        /// <summary>
        /// The NTP timestamp as a time. The fraction matters: lip sync is a matter of tens of milliseconds, and the seconds
        /// word alone is a second wide. Divided by 2^32 as a double.
        /// </summary>
        public DateTime NtpTime => NtpEpoch.AddSeconds((NtpTimestamp >> 32) + (NtpTimestamp & 0xFFFFFFFF) / 4294967296.0);

        /// <summary>
        /// The middle 32 bits of the NTP timestamp: what a report block echoes back as its last sender report, which the
        /// sender matches to the report it sent and works out the round trip of.
        /// </summary>
        public uint CompactNtp => (uint)(NtpTimestamp >> 16);

        /// <summary>A time as an NTP timestamp: of 32 bits of seconds since 1900, then 32 of their fraction - of 2^32ths.</summary>
        public static ulong ToNtpTimestamp(DateTime time)
        {
            double seconds = (time.ToUniversalTime() - NtpEpoch).TotalSeconds;
            ulong whole = (ulong)Math.Floor(seconds);
            ulong fraction = (ulong)((seconds - whole) * 4294967296.0);
            return (whole << 32) | Math.Min(fraction, 0xFFFFFFFFUL);
        }

        public override string ToString() => $"SR {Ssrc:X8} NTP {NtpTime:O} RTP {RtpTimestamp} of {Reports.Count} blocks";
    }
}
