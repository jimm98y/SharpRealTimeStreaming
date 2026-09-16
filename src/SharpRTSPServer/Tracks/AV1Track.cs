using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace SharpRTSPServer
{
    /// <summary>
    /// AV1 video track.
    /// </summary>
    public class AV1Track : TrackBase
    {
        /// <summary>
        /// AV1 Video Codec name.
        /// </summary>
        public override string Codec => "AV1";

        /// <summary>
        /// Default video track clock rate.
        /// </summary>
        public const int DEFAULT_CLOCK = 90000;

        /// <summary>
        /// Track ID. Used to identify the track in the SDP.
        /// </summary>
        public override int ID { get; set; } = 0;

        /// <summary>
        /// Video clock rate. Default value is 90000.
        /// </summary>
        public int VideoClock { get; set; } = DEFAULT_CLOCK;

        /// <summary>
        /// Maximum size of the packet. If the resulting RTP packet exceeds this size, fragmentation will be used. Default value is 1400 and RTP over RTSP is constrained to 65535.
        /// </summary>
        public int PacketMTU { get; set; } = 1400;

        private int _payloadType = -1;

        /// <summary>
        /// Payload type. AV1 uses a dynamic payload type, which by default we calculate as 96 + track ID.
        /// </summary>
        public override int PayloadType
        {
            get
            {
                if (_payloadType < 0)
                {
                    return RTSPServer.DYNAMIC_PAYLOAD_TYPE + ID;
                }
                else
                {
                    return _payloadType;
                }
            }
            set
            {
                _payloadType = value;
            }
        }

        public override bool IsReady => _obus != null;

        private List<byte[]> _obus;

        /// <summary>
        /// Ctor.
        /// </summary>
        /// <param name="clock">AV1 clock. Default value is 90000.</param>
        public AV1Track(int clock = DEFAULT_CLOCK)
        {
        }

        public void SetOBUs(List<byte[]> obus)
        {
            this._obus = obus;
        }

        public override StringBuilder BuildSDP(StringBuilder sdp)
        {
            sdp.Append($"m=video 0 RTP/{RtpProfile} {PayloadType}\r\n"); // TODO: AVPF is AVP + Feedback https://aomediacodec.github.io/av1-rtp-spec/v1.0.0.html
            sdp.Append($"a=control:trackID={ID}\r\n");
            sdp.Append($"a=rtpmap:{PayloadType} {Codec}/{VideoClock}\r\n");

            return sdp;
        }

        /// <summary>OBU_TILE_LIST - not supported over RTP, dropped when transmitting.</summary>
        private const int OBU_TILE_LIST = 2;

        /// <summary>OBU_TEMPORAL_DELIMITER - dropped when transmitting, the RTP timestamp carries it.</summary>
        private const int OBU_TEMPORAL_DELIMITER = 4;

        /// <summary>
        /// Creates RTP packets.
        /// </summary>
        /// <param name="samples">An array of AV1 OBUs.</param>
        /// <param name="rtpTimestamp">RTP timestamp in the timescale of the track.</param>
        /// <returns>RTP packets.</returns>
        public override (List<Memory<byte>>, List<IMemoryOwner<byte>>) CreateRtpPackets(List<ReadOnlyMemory<byte>> samples, uint rtpTimestamp)
        {
            List<Memory<byte>> rtpPackets = new List<Memory<byte>>();
            List<IMemoryOwner<byte>> memoryOwners = new List<IMemoryOwner<byte>>();

            // The marker goes on the last OBU we actually send, which is not necessarily the last
            // sample - temporal delimiters and tile lists are dropped below.
            int lastEmittedSample = LastSampleToEmit(samples);

            for (int x = 0; x < samples.Count; x++)
            {
                if (samples[x].Length == 0)
                {
                    continue;
                }

                var owner = MemoryPool<byte>.Shared.Rent(samples[x].Length);
                memoryOwners.Add(owner);
                var rawObu = owner.Memory.Slice(0, samples[x].Length);
                samples[x].CopyTo(rawObu);

                bool lastObu = x == lastEmittedSample;

                int packetMTU = PacketMTU; // 65535; 
                packetMTU += -8 - 20 - 16; // -8 for UDP header, -20 for IP header, -16 normal RTP header len. ** LESS RTP EXTENSIONS !!!

                if (packetMTU <= 0)
                {
                    // a negative payload size would grow dataRemaining on every pass, so the loop
                    // below would never end
                    throw new InvalidOperationException(
                        $"{nameof(PacketMTU)} of {PacketMTU} is too small to carry any payload, it must leave room for the IP, UDP and RTP headers.");
                }

                int obuPointer = 0;
                int obuHeader = rawObu.Span[0];
                int obuHeaderLen = 1;
                int obuType = (obuHeader & 0x78) >> 3;

                // The temporal delimiter OBU, if present, SHOULD be removed when transmitting.
                // Tile list OBUs are not supported and SHOULD be removed when transmitted.
                if (obuType == OBU_TEMPORAL_DELIMITER || obuType == OBU_TILE_LIST)
                {
                    // skip obu
                    continue;
                }

                //Log.Trace($"OBU {obuType}, payload: {Utilities.ToHexString(rawObu.ToArray())}");

                if ((obuHeader & 0x04) == 0x04)
                {
                    obuHeaderLen += 1;
                }

                // The AV1 specification allows OBUs to have an optional size field called obu_size(also leb128 encoded), signaled by
                // the obu_has_size_field flag in the OBU header.To minimize overhead, the obu_has_size_field flag SHOULD be set to zero in all OBUs.
                if ((obuHeader & 0x02) == 0x02)
                {
                    if (rawObu.Length <= obuHeaderLen)
                    {
                        throw new ArgumentException($"OBU of {rawObu.Length} bytes declares a size field but is too short to hold one.", nameof(samples));
                    }

                    int len = ReadLeb128(rawObu.Span, obuHeaderLen, out _);

                    rawObu.Slice(obuHeaderLen + len).CopyTo(rawObu.Slice(obuHeaderLen));
                    rawObu = rawObu.Slice(0, rawObu.Length - len);

                    rawObu.Span[0] = (byte)(obuHeader & 0xFD);
                }               

                int dataRemaining = rawObu.Length;

                while (dataRemaining > 0)
                {
                    int payloadSize = Math.Min(packetMTU, dataRemaining);

                    var aggregationHeaderLen = 1;
                    var destSize = 12 + aggregationHeaderLen + payloadSize;
                    var lowner = MemoryPool<byte>.Shared.Rent(destSize);
                    memoryOwners.Add(lowner);
                    var rtpPacket = lowner.Memory.Slice(0, destSize);

                    // RTP Packet Header
                    // 0 - Version, P, X, CC, M, PT and Sequence Number
                    //32 - Timestamp. AV1 uses a 90kHz clock
                    //64 - SSRC
                    //96 - CSRCs (optional)
                    //nn - Extension ID and Length
                    //nn - Extension header
                    const bool rtpPadding = false;
                    const bool rtpHasExtension = false;
                    const int rtpCsrcCount = 0;

                    RTPPacketUtil.WriteHeader(rtpPacket.Span, RTPPacketUtil.RTP_VERSION,
                        rtpPadding, rtpHasExtension, rtpCsrcCount, lastObu && dataRemaining == payloadSize, PayloadType);

                    // sequence number and SSRC are set just before send
                    RTPPacketUtil.WriteTS(rtpPacket.Span, rtpTimestamp);

                    //  0 1 2 3 4 5 6 7
                    // +-+-+-+-+-+-+-+-+
                    // |Z|Y| W |N|-|-|-|
                    // +-+-+-+-+-+-+-+-+

                    // Specification says: MUST be set to 1 if the first OBU element is an OBU fragment that is a continuation of an OBU fragment from the previous packet, and MUST be set to 0 otherwise.
                    int zBit = obuPointer != 0 ? 1 : 0;

                    // Specification says: MUST be set to 1 if the last OBU element is an OBU fragment that will continue in the next packet, and MUST be set to 0 otherwise.
                    int yBit = dataRemaining != payloadSize ? 1 : 0; 

                    // Specification says: Two bit field that describes the number of OBU elements in the packet. This field MUST be set equal to 0 or equal to the number of OBU elements contained in the packet.
                    // If set to 0, each OBU element MUST be preceded by a length field.
                    int wCount = 1;

                    // Specification says: MUST be set to 1 if the packet is the first packet of a coded video sequence, and MUST be set to 0 otherwise.
                    int nBit = obuType == 1 ? 1 : 0; // OBU_SEQUENCE_HEADER
                    byte aggregationHeader = (byte)((zBit << 7) | (yBit << 6) | (wCount << 4) | (nBit << 3));

                    // aggregation header
                    rtpPacket.Span[12] = aggregationHeader;

                    // because w bit is set to 1, we don't need any size

                    // payload
                    rawObu.Slice(obuPointer, payloadSize).CopyTo(rtpPacket.Slice(13));

                    obuPointer += payloadSize;
                    dataRemaining -= payloadSize;

                    rtpPackets.Add(rtpPacket);
                }                
            }

            return (rtpPackets, memoryOwners);
        }

        /// <summary>
        /// Index of the last sample that will actually be sent, or -1 when none will be.
        /// </summary>
        /// <remarks>
        /// Temporal delimiter and tile list OBUs are dropped, so using the last sample outright
        /// would leave the frame with no marker bit when one of those comes last.
        /// </remarks>
        private static int LastSampleToEmit(List<ReadOnlyMemory<byte>> samples)
        {
            for (int x = samples.Count - 1; x >= 0; x--)
            {
                if (samples[x].Length == 0)
                {
                    continue;
                }

                int obuType = (samples[x].Span[0] & 0x78) >> 3;
                if (obuType != OBU_TEMPORAL_DELIMITER && obuType != OBU_TILE_LIST)
                {
                    return x;
                }
            }

            return -1;
        }

        public int ReadLeb128(Span<byte> source, int index, out int value)
        {
            int arrayIndex = index;
            int v = 0;
            int Leb128Bytes = 0;
            for (int i = 0; i < 8 && arrayIndex < source.Length; i++)
            {
                int leb128_byte = source[arrayIndex++];
                v = v | ((leb128_byte & 0x7f) << (i * 7));
                Leb128Bytes += 1;
                if ((leb128_byte & 0x80) == 0)
                {
                    break;
                }
            }
            value = v;
            return Leb128Bytes;
        }
    }
}
