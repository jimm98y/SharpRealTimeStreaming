using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace SharpRTSPServer
{
    /// <see href="https://datatracker.ietf.org/doc/html/rfc2435" />
    public class MJpegTrack : TrackBase
    {
        /// <inheritdoc/>
        public override bool IsReady => true;

        /// <inheritdoc/>
        public override int PayloadType { get; set; } = 26; // RTP_PT_JPEG - https://datatracker.ietf.org/doc/html/rfc2435 - Appendix C

        /// <inheritdoc/>
        public override string Codec => "JPEG";

        /// <inheritdoc/>
        public override int ID { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        /// <inheritdoc/>
        public override StringBuilder BuildSDP(StringBuilder sdp)
        {
            return sdp
                .Append("m=video 0 RTP/AVP ").Append(PayloadType).AppendLine()
                .Append("a=control:trackID=").Append(ID).AppendLine()
                ;
        }

        /// <inheritdoc/>
        public override (List<Memory<byte>>, List<IMemoryOwner<byte>>) CreateRtpPackets(List<byte[]> samples, uint rtpTimestamp)
        {
            if (samples.Count != 1)
            {
                throw new InvalidOperationException("Only 1 sample is supported.");
            }

            var rtpPackets = new List<Memory<byte>>();
            var memoryOwners = new List<IMemoryOwner<byte>>();

            for (int i = 0; i < samples.Count; i++)
            {
                // https://en.wikipedia.org/wiki/JPEG_File_Interchange_Format
                const ushort SoiMarker = 0xFFD8; // SOI - Start of image header
                                                 //const ushort App0Header = 0xFFE0; // Application Segment 0 header
                                                 //const ushort App15Header = 0xFFEF; // Application Segment 15 header
                const ushort Sof0Marker = 0xFFC0; // SOF0 - Start of Frame marker
                const ushort DqtHeader = 0xFFDB; // Define Quantization Table header
                                                 //const ushort SosMarker = 0xFFDA; // SOS - Start of Scan marker
                const ushort EoiMarker = 0xFFD9; // EOI - End of Image marker

                var jpegImage = samples[i].AsSpan();

                var header = BinaryPrimitives.ReadUInt16BigEndian(jpegImage);
                if (header != SoiMarker)
                {
                    throw new InvalidOperationException($"JPEG image must start with SOI marker {SoiMarker.ToString("X4")} and {header.ToString("X4")}");
                }

                header = BinaryPrimitives.ReadUInt16BigEndian(jpegImage.Slice(jpegImage.Length - 2));
                if (header != EoiMarker)
                {
                    throw new InvalidOperationException($"JPEG image must start with SOI marker {EoiMarker.ToString("X4")} and {header.ToString("X4")}");
                }

                //var reader = jpegImage[2..^2];
                var reader = jpegImage.Slice(2); // keep EOI

                byte type = 1; // https://datatracker.ietf.org/doc/html/rfc2435#section-3.1.3
                byte q = 255; // https://datatracker.ietf.org/doc/html/rfc2435#section-3.1.4, https://datatracker.ietf.org/doc/html/rfc2435#section-4.2

                int nbQuantizationTables = 0;
                var firstQuantizationtable = ReadOnlySpan<byte>.Empty;
                var secondQuantizationtable = ReadOnlySpan<byte>.Empty;

                while (true)
                {
                    header = BinaryPrimitives.ReadUInt16BigEndian(reader);

                    if (header == Sof0Marker)
                    {
                        break;
                    }

                    reader = reader.Slice(2);

                    var size = BinaryPrimitives.ReadUInt16BigEndian(reader) - 2; reader = reader.Slice(2);

                    switch (header)
                    {
                        case DqtHeader:
                            nbQuantizationTables++;
                            if (nbQuantizationTables == 1)
                            {
                                firstQuantizationtable = reader.Slice(0, size);
                            }
                            else if (nbQuantizationTables == 2)
                            {
                                secondQuantizationtable = reader.Slice(0, size);
                            }
                            else
                            {
                                throw new InvalidOperationException("Error: More than 2 quantization tables in JPEG image");
                            }
                            break;
                        default:
                            break;
                    }

                    reader = reader.Slice(size);
                }

                // Build a list of 1 or more RTP packets
                // The last packet will have the M bit set to '1'

                var endOfFrame = false;
                var firstFrame = true;

                // -8 for UDP header, -20 for IP header, -16 normal RTP header len. ** LESS RTP EXTENSIONS !!!
                var packetMTU = 1400; // 65535; 

                var dataPointer = 0;

                while (reader.Length > 0)
                {
                    bool shouldSendQuantizationTables = firstFrame && q > 127;

                    firstFrame = false;

                    int payloadSize = Math.Min(packetMTU, reader.Length);

                    endOfFrame = payloadSize == reader.Length;

                    // 12 is header size. then jpeg header, then payload
                    var destSize = 12 + 8 + payloadSize;
                    var owner = MemoryPool<byte>.Shared.Rent(destSize);
                    memoryOwners.Add(owner);
                    var rtpPacket = owner.Memory.Slice(0, destSize);

                    // RTP Packet Header
                    // 0 - Version, P, X, CC, M, PT and Sequence Number
                    //32 - Timestamp. H264 uses a 90kHz clock
                    //64 - SSRC
                    //96 - CSRCs (optional)
                    //nn - Extension ID and Length
                    //nn - Extension header

                    var rtpPacketSpan = rtpPacket.Span;

                    rtpPacketSpan.Slice(3, 9).Clear();

                    RTPPacketUtil.WriteHeader(
                        rtpPacket: rtpPacketSpan,
                        rtpVersion: RTPPacketUtil.RTP_VERSION,
                        rtpPadding: false,
                        rtpExtension: false,
                        rtpCsrcCount: 0,
                        rtpMarker: endOfFrame,
                        rtpPayloadType: PayloadType);

                    // sequence number and SSRC are set just before send
                    RTPPacketUtil.WriteTS(rtpPacketSpan, rtpTimestamp);

                    rtpPacketSpan = rtpPacketSpan.Slice(12);

                    // For JPEG we need https://www.rfc-editor.org/rfc/rfc2435

                    BinaryPrimitives.WriteInt32BigEndian(rtpPacketSpan, dataPointer & 0x00FFFFFF);
                    rtpPacketSpan = rtpPacketSpan.Slice(4);

                    // Write JPEG Header - https://datatracker.ietf.org/doc/html/rfc2435#section-3.1
                    rtpPacketSpan[0] = type;
                    rtpPacketSpan[1] = q;
                    rtpPacketSpan[2] = (byte)(Width / 8);
                    rtpPacketSpan[3] = (byte)(Height / 8);
                    rtpPacketSpan = rtpPacketSpan.Slice(4);

                    // write quantization tables
                    if (shouldSendQuantizationTables)
                    {
                        // Write Restart Marker header - https://datatracker.ietf.org/doc/html/rfc2435#section-3.1.7
                        // Not present for type 1

                        // Write Quantization Table header https://datatracker.ietf.org/doc/html/rfc2435#section-3.1.8

                        if (nbQuantizationTables == 1)
                        {
                            // MBZ
                            rtpPacketSpan[0] = (byte)(firstQuantizationtable[0] & 0xf);

                            // Precision
                            rtpPacketSpan[1] = (byte)(firstQuantizationtable[0] >> 4);

                            // Length
                            var qtSize = firstQuantizationtable.Length - 1;
                            BinaryPrimitives.WriteInt16BigEndian(rtpPacketSpan.Slice(2), (short)(qtSize));

                            // Quantization Table Data
                            firstQuantizationtable.Slice(1).CopyTo(rtpPacketSpan.Slice(4));
                            qtSize += 4;
                            rtpPacketSpan = rtpPacketSpan.Slice(qtSize);
                            payloadSize -= qtSize;
                        }
                        else // nbQuantizationTables == 2
                        {
                            // MBZ
                            rtpPacketSpan[0] = 0;

                            // Precision
                            rtpPacketSpan[1] = (byte)(firstQuantizationtable[0] >> 4);

                            // Length
                            var qtSize = firstQuantizationtable.Length + secondQuantizationtable.Length - 2;
                            BinaryPrimitives.WriteInt16BigEndian(rtpPacketSpan.Slice(2), (short)(qtSize));

                            // Quantization Table Data
                            firstQuantizationtable.Slice(1).CopyTo(rtpPacketSpan.Slice(4));
                            secondQuantizationtable.Slice(1).CopyTo(rtpPacketSpan.Slice(3 + firstQuantizationtable.Length));
                            qtSize += 4;
                            rtpPacketSpan = rtpPacketSpan.Slice(qtSize);
                            payloadSize -= qtSize;
                        }
                    }

                    // Write JPEG Payload

                    reader.Slice(0, rtpPacketSpan.Length).CopyTo(rtpPacketSpan);
                    reader = reader.Slice(rtpPacketSpan.Length);
                    dataPointer += rtpPacketSpan.Length;

                    rtpPackets.Add(rtpPacket);
                }
            }

            return (rtpPackets, memoryOwners);
        }
    }
}
