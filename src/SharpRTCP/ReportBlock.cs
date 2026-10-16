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

namespace SharpRTCP
{
    /// <summary>
    /// One report block of a receiver or sender report (RFC 3550 6.4.1): what a receiver has received of one source.
    /// </summary>
    public struct ReportBlock
    {
        /// <summary>Whether anything has arrived to report on.</summary>
        public bool HasData { get; set; }

        /// <summary>
        /// The source the block is about: of a block read, as it says; of one taken of <see cref="ReceptionStatistics"/>,
        /// none, which is set before it is written - the statistics are kept before the source is known.
        /// </summary>
        public uint Ssrc { get; set; }

        /// <summary>What share of the packets expected since the last report did not arrive, out of 256.</summary>
        public byte FractionLost { get; set; }

        /// <summary>How many have not arrived since the stream began. Negative where duplicates arrived.</summary>
        public int CumulativeLost { get; set; }

        /// <summary>How far through the sequence this receiver has got, counting wraps.</summary>
        public uint ExtendedHighestSequence { get; set; }

        /// <summary>How unevenly packets are arriving, in this track's own clock.</summary>
        public uint Jitter { get; set; }

        /// <summary>The middle of the timestamp of the last sender report, so the sender knows which.</summary>
        public uint LastSenderReport { get; set; }

        /// <summary>How long that report sat here before this answer went out, in units of 1/65536 s.</summary>
        public uint DelaySinceLastSenderReport { get; set; }
    }
}
