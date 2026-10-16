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

using System.Collections.Generic;

namespace SharpRTCP
{
    /// <summary>
    /// A receiver report (RFC 3550 6.4.2): of who receives, a report block about each source it receives - what of it
    /// arrived, how unevenly, and which of its sender reports it answers. Of no block, it says only that the receiver is
    /// there.
    /// </summary>
    public sealed class ReceiverReport
    {
        /// <summary>The SSRC of who sends the report: the receiver.</summary>
        public uint Ssrc { get; set; }

        /// <summary>A block about each source received, each of its <see cref="ReportBlock.Ssrc"/>; at most 31 are written.</summary>
        public List<ReportBlock> Reports { get; } = new List<ReportBlock>();

        public override string ToString() => $"RR {Ssrc:X8} of {Reports.Count} blocks";
    }
}
