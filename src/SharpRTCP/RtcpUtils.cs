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
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace SharpRTCP
{
    /// <summary>
    /// RTCP (RFC 3550 6) read and written. Of each packet type, <c>Write…</c> writes it into a buffer and returns its length,
    /// <c>Create…</c> returns it in a new array, and <c>Get…Length</c> says how long it is; <c>TryRead…</c> reads one packet,
    /// <c>Read…</c> those of a compound packet. Of sender and receiver reports and their report blocks, source descriptions
    /// of a CNAME, Picture Loss Indications (RFC 4585 6.3.1) and goodbyes; and RTCP told from the RTP it is multiplexed with
    /// (RFC 5761).
    /// </summary>
    public static class RtcpUtils
    {
        /// <summary>The version of RTP and RTCP.</summary>
        public const int Version = 2;

        /// <summary>Version, padding, count, type, length - and the SSRC of who sends it.</summary>
        public const int HeaderLength = 8;

        /// <summary>One report block, as RFC 3550 section 6.4.1 lays it out.</summary>
        public const int ReportBlockLength = 24;

        /// <summary>The most report blocks a report has: its count is of 5 bits.</summary>
        public const int MaxReportBlocks = 31;

        /// <summary>The length of a Picture Loss Indication.</summary>
        public const int PictureLossIndicationLength = 12;

        /// <summary>The length of a goodbye of one source, of no reason given.</summary>
        public const int GoodbyeLength = 8;

        // a sender report's header, then its sender information: the NTP and RTP timestamps and its counts of packets and octets
        private const int SenderInformationEnd = 28;

        private const int SdesItemCname = 1;
        private const int PsfbFormatPictureLossIndication = 1;

        #region Packets

        /// <summary>
        /// The header of an RTCP packet: version 2, its count - of report blocks, of chunks, of sources, or a feedback's format
        /// - its type, its length in 32 bit words less one, and the SSRC of who sends it.
        /// </summary>
        public static void WriteHeader(Span<byte> packet, int count, int packetType, int length, uint ssrc, bool hasPadding = false)
        {
            packet[0] = (byte)((Version << 6) + ((hasPadding ? 1 : 0) << 5) + count);
            packet[1] = (byte)packetType;
            BinaryPrimitives.WriteUInt16BigEndian(packet.Slice(2), (ushort)length);
            BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(4), ssrc);
        }

        /// <summary>
        /// Whether a datagram of RTP's range of first bytes (RFC 7983: 128 to 191) is RTCP: of a second byte that is a packet
        /// type of RTCP, 192 to 223, which the payload types of RTP multiplexed with it leave free (RFC 5761 4).
        /// </summary>
        public static bool IsRtcp(ReadOnlySpan<byte> datagram)
            => datagram.Length >= HeaderLength && (datagram[0] & 0xC0) == 0x80 && datagram[1] >= 192 && datagram[1] <= 223;

        /// <summary>
        /// The packets of a compound packet (RFC 3550 6.1), each saying its own length: where each is, and how long. Those
        /// after one that is not well formed - of another version, or running past the end - are left out.
        /// </summary>
        public static List<(int Offset, int Length)> SplitCompoundPacket(ReadOnlySpan<byte> compound)
        {
            var packets = new List<(int, int)>();
            int at = 0;
            while (at + 4 <= compound.Length)
            {
                if ((compound[at] >> 6) != Version)
                {
                    break;
                }

                int length = (BinaryPrimitives.ReadUInt16BigEndian(compound.Slice(at + 2)) + 1) * 4;
                if (at + length > compound.Length)
                {
                    break;
                }

                packets.Add((at, length));
                at += length;
            }
            return packets;
        }

        /// <summary>Packets end to end, as a compound packet is.</summary>
        public static byte[] CreateCompoundPacket(params byte[][] packets)
        {
            int length = 0;
            foreach (byte[] packet in packets)
            {
                length += packet.Length;
            }

            byte[] compound = new byte[length];
            int at = 0;
            foreach (byte[] packet in packets)
            {
                Buffer.BlockCopy(packet, 0, compound, at, packet.Length);
                at += packet.Length;
            }
            return compound;
        }

        #endregion // Packets

        #region Report blocks

        /// <summary>A report block (RFC 3550 6.4.1), of <see cref="ReportBlockLength"/> bytes.</summary>
        public static ReportBlock ReadReportBlock(ReadOnlySpan<byte> block)
        {
            // twenty four bits, signed: a receiver that took in duplicates reports fewer than none
            int cumulative = (block[5] << 16) | (block[6] << 8) | block[7];
            if (cumulative >= 0x800000)
            {
                cumulative -= 0x1000000;
            }

            return new ReportBlock
            {
                HasData = true,
                Ssrc = BinaryPrimitives.ReadUInt32BigEndian(block),
                FractionLost = block[4],
                CumulativeLost = cumulative,
                ExtendedHighestSequence = BinaryPrimitives.ReadUInt32BigEndian(block.Slice(8)),
                Jitter = BinaryPrimitives.ReadUInt32BigEndian(block.Slice(12)),
                LastSenderReport = BinaryPrimitives.ReadUInt32BigEndian(block.Slice(16)),
                DelaySinceLastSenderReport = BinaryPrimitives.ReadUInt32BigEndian(block.Slice(20)),
            };
        }

        /// <summary>A report block (RFC 3550 6.4.1) about its source.</summary>
        /// <returns><see cref="ReportBlockLength"/>.</returns>
        public static int WriteReportBlock(Span<byte> block, ReportBlock report)
        {
            // twenty four bits, signed, so a receiver that took in duplicates reports fewer than none
            int lost = report.CumulativeLost;
            lost = lost > 0x7FFFFF ? 0x7FFFFF : lost < -0x800000 ? -0x800000 : lost;

            BinaryPrimitives.WriteUInt32BigEndian(block, report.Ssrc);
            block[4] = report.FractionLost;
            block[5] = (byte)((lost >> 16) & 0xFF);
            block[6] = (byte)((lost >> 8) & 0xFF);
            block[7] = (byte)(lost & 0xFF);
            BinaryPrimitives.WriteUInt32BigEndian(block.Slice(8), report.ExtendedHighestSequence);
            BinaryPrimitives.WriteUInt32BigEndian(block.Slice(12), report.Jitter);
            BinaryPrimitives.WriteUInt32BigEndian(block.Slice(16), report.LastSenderReport);
            BinaryPrimitives.WriteUInt32BigEndian(block.Slice(20), report.DelaySinceLastSenderReport);
            return ReportBlockLength;
        }

        // the report blocks of a report: as many as its count says, of those that fit in it
        private static void ReadReportBlocks(ReadOnlySpan<byte> packet, int at, List<ReportBlock> blocks)
        {
            int count = packet[0] & 0x1F;
            for (int i = 0; i < count && at + ReportBlockLength <= packet.Length; i++)
            {
                blocks.Add(ReadReportBlock(packet.Slice(at, ReportBlockLength)));
                at += ReportBlockLength;
            }
        }

        // the report blocks written after what comes before them: at most MaxReportBlocks of them
        private static int WriteReportBlocks(Span<byte> packet, int at, IReadOnlyList<ReportBlock> blocks, int count)
        {
            for (int i = 0; i < count; i++)
            {
                at += WriteReportBlock(packet.Slice(at, ReportBlockLength), blocks[i]);
            }
            return at;
        }

        private static int BlockCount(IReadOnlyList<ReportBlock> blocks) => Math.Min(blocks?.Count ?? 0, MaxReportBlocks);

        /// <summary>
        /// The report blocks of the receiver and sender reports of a compound packet, each with who reported it: what a
        /// sender reads of how its streams arrive, whichever of the two the receiver sent them in.
        /// </summary>
        public static List<(uint Reporter, ReportBlock Block)> ReadReportBlocks(ReadOnlySpan<byte> compound)
        {
            var blocks = new List<(uint, ReportBlock)>();
            foreach (var (offset, length) in SplitCompoundPacket(compound))
            {
                var packet = compound.Slice(offset, length);
                if (TryReadReceiverReport(packet, out ReceiverReport receiverReport))
                {
                    foreach (var block in receiverReport.Reports)
                    {
                        blocks.Add((receiverReport.Ssrc, block));
                    }
                }
                else if (TryReadSenderReport(packet, out SenderReport senderReport))
                {
                    foreach (var block in senderReport.Reports)
                    {
                        blocks.Add((senderReport.Ssrc, block));
                    }
                }
            }
            return blocks;
        }

        #endregion // Report blocks

        #region Sender reports

        /// <summary>The length of a sender report (RFC 3550 6.4.1) of a number of report blocks.</summary>
        public static int GetSenderReportLength(int blockCount) => SenderInformationEnd + ReportBlockLength * Math.Min(blockCount, MaxReportBlocks);

        /// <summary>A sender report (RFC 3550 6.4.1): its header, its sender information, and a block of each of its reports.</summary>
        /// <returns>Its length: <see cref="GetSenderReportLength"/> of the count of its blocks.</returns>
        public static int WriteSenderReport(Span<byte> packet, SenderReport report)
        {
            int count = BlockCount(report.Reports);
            int length = GetSenderReportLength(count);
            WriteHeader(packet, count, PacketType.SenderReport, length / 4 - 1, report.Ssrc);
            BinaryPrimitives.WriteUInt64BigEndian(packet.Slice(8), report.NtpTimestamp);
            BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(16), report.RtpTimestamp);
            BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(20), report.PacketCount);
            BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(24), report.OctetCount);
            return WriteReportBlocks(packet, SenderInformationEnd, report.Reports, count);
        }

        /// <summary>A sender report (RFC 3550 6.4.1), in a new array.</summary>
        public static byte[] CreateSenderReport(SenderReport report)
        {
            byte[] packet = new byte[GetSenderReportLength(BlockCount(report.Reports))];
            WriteSenderReport(packet, report);
            return packet;
        }

        /// <summary>A sender report, and its report blocks, of one RTCP packet; false of another type, or of one too short.</summary>
        public static bool TryReadSenderReport(ReadOnlySpan<byte> packet, out SenderReport report)
        {
            report = null;
            if (packet.Length < SenderInformationEnd || packet[1] != PacketType.SenderReport)
            {
                return false;
            }

            report = new SenderReport
            {
                Ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(4)),
                NtpTimestamp = BinaryPrimitives.ReadUInt64BigEndian(packet.Slice(8)),
                RtpTimestamp = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(16)),
                PacketCount = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(20)),
                OctetCount = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(24)),
            };
            ReadReportBlocks(packet, SenderInformationEnd, report.Reports);
            return true;
        }

        /// <summary>The sender reports of a compound packet.</summary>
        public static List<SenderReport> ReadSenderReports(ReadOnlySpan<byte> compound)
        {
            var reports = new List<SenderReport>();
            foreach (var (offset, length) in SplitCompoundPacket(compound))
            {
                if (TryReadSenderReport(compound.Slice(offset, length), out SenderReport report))
                {
                    reports.Add(report);
                }
            }
            return reports;
        }

        #endregion // Sender reports

        #region Receiver reports

        /// <summary>The length of a receiver report (RFC 3550 6.4.2) of a number of report blocks.</summary>
        public static int GetReceiverReportLength(int blockCount) => HeaderLength + ReportBlockLength * Math.Min(blockCount, MaxReportBlocks);

        /// <summary>
        /// A receiver report (RFC 3550 6.4.2): its header, and a block of each of its reports. Of none - nothing has arrived to
        /// report on - it still goes, because it is also what says the receiver is still there.
        /// </summary>
        /// <returns>Its length: <see cref="GetReceiverReportLength"/> of the count of its blocks.</returns>
        public static int WriteReceiverReport(Span<byte> packet, ReceiverReport report)
        {
            int count = BlockCount(report.Reports);
            WriteHeader(packet, count, PacketType.ReceiverReport, GetReceiverReportLength(count) / 4 - 1, report.Ssrc);
            return WriteReportBlocks(packet, HeaderLength, report.Reports, count);
        }

        /// <summary>A receiver report (RFC 3550 6.4.2), in a new array.</summary>
        public static byte[] CreateReceiverReport(ReceiverReport report)
        {
            byte[] packet = new byte[GetReceiverReportLength(BlockCount(report.Reports))];
            WriteReceiverReport(packet, report);
            return packet;
        }

        /// <summary>A receiver report, and its report blocks, of one RTCP packet; false of another type, or of one too short.</summary>
        public static bool TryReadReceiverReport(ReadOnlySpan<byte> packet, out ReceiverReport report)
        {
            report = null;
            if (packet.Length < HeaderLength || packet[1] != PacketType.ReceiverReport)
            {
                return false;
            }

            report = new ReceiverReport { Ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(4)) };
            ReadReportBlocks(packet, HeaderLength, report.Reports);
            return true;
        }

        /// <summary>The receiver reports of a compound packet.</summary>
        public static List<ReceiverReport> ReadReceiverReports(ReadOnlySpan<byte> compound)
        {
            var reports = new List<ReceiverReport>();
            foreach (var (offset, length) in SplitCompoundPacket(compound))
            {
                if (TryReadReceiverReport(compound.Slice(offset, length), out ReceiverReport report))
                {
                    reports.Add(report);
                }
            }
            return reports;
        }

        #endregion // Receiver reports

        #region Source descriptions

        /// <summary>The length of a source description of the CNAME of one source.</summary>
        public static int GetSourceDescriptionLength(string cname)
        {
            int nameLength = Math.Min(Encoding.UTF8.GetByteCount(cname ?? ""), 255);

            // its header, the item's type and length, the name, and the null that ends the items, padded to 32 bits
            return (HeaderLength + 2 + nameLength + 1 + 3) & ~3;
        }

        /// <summary>
        /// A source description of the CNAME of a source (RFC 3550 6.5.1), which every compound RTCP packet carries (RFC 3550
        /// 6.1): its chunk's items end with a null, and are padded to 32 bits.
        /// </summary>
        /// <returns>Its length: <see cref="GetSourceDescriptionLength"/>.</returns>
        public static int WriteSourceDescription(Span<byte> packet, uint ssrc, string cname)
        {
            byte[] name = Encoding.UTF8.GetBytes(cname ?? "");
            int nameLength = Math.Min(name.Length, 255);
            int length = GetSourceDescriptionLength(cname);

            packet.Slice(0, length).Clear();
            WriteHeader(packet, 1, PacketType.SourceDescription, length / 4 - 1, ssrc);
            packet[8] = SdesItemCname;
            packet[9] = (byte)nameLength;
            name.AsSpan(0, nameLength).CopyTo(packet.Slice(10));
            return length;
        }

        /// <summary>A source description of the CNAME of a source, in a new array.</summary>
        public static byte[] CreateSourceDescription(uint ssrc, string cname)
        {
            byte[] packet = new byte[GetSourceDescriptionLength(cname)];
            WriteSourceDescription(packet, ssrc, cname);
            return packet;
        }

        #endregion // Source descriptions

        #region Feedback and goodbye

        /// <summary>A Picture Loss Indication (RFC 4585 6.3.1): asks the sender of a source for a key frame.</summary>
        /// <returns><see cref="PictureLossIndicationLength"/>.</returns>
        public static int WritePictureLossIndication(Span<byte> packet, uint ssrc, uint mediaSsrc)
        {
            // its format in the field a report's count is in, and of no feedback control information
            WriteHeader(packet, PsfbFormatPictureLossIndication, PacketType.PayloadFeedback, PictureLossIndicationLength / 4 - 1, ssrc);
            BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(8), mediaSsrc);
            return PictureLossIndicationLength;
        }

        /// <summary>A Picture Loss Indication, in a new array.</summary>
        public static byte[] CreatePictureLossIndication(uint ssrc, uint mediaSsrc)
        {
            byte[] packet = new byte[PictureLossIndicationLength];
            WritePictureLossIndication(packet, ssrc, mediaSsrc);
            return packet;
        }

        /// <summary>A goodbye (RFC 3550 6.6) of one source: it is leaving.</summary>
        /// <returns><see cref="GoodbyeLength"/>.</returns>
        public static int WriteGoodbye(Span<byte> packet, uint ssrc)
        {
            WriteHeader(packet, 1, PacketType.Goodbye, GoodbyeLength / 4 - 1, ssrc);
            return GoodbyeLength;
        }

        /// <summary>A goodbye of one source, in a new array.</summary>
        public static byte[] CreateGoodbye(uint ssrc)
        {
            byte[] packet = new byte[GoodbyeLength];
            WriteGoodbye(packet, ssrc);
            return packet;
        }

        #endregion // Feedback and goodbye
    }
}
