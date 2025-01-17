using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace SharpRTSPServer
{
    /// <summary>
    /// H265 video track.
    /// </summary>
    public class H265Track : TrackBase
    {
        /// <summary>
        /// H265 Video Codec name.
        /// </summary>
        public override string Codec => "H265";

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

        /// <summary>
        /// Video Parameter Set (VPS).
        /// </summary>
        public byte[] VPS { get; set; }

        /// <summary>
        /// Sequence Parameter Set (SPS).
        /// </summary>
        public byte[] SPS { get; set; }
        
        /// <summary>
        /// Picture Parameter Set (PPS).
        /// </summary>
        public byte[] PPS { get; set; }

        /// <summary>
        /// Is the track ready?
        /// </summary>
        public override bool IsReady { get { return VPS != null && SPS != null && PPS != null; } }

        private int _payloadType = -1;

        /// <summary>
        /// Payload type. H264 uses a dynamic payload type, which by default we calculate as 96 + track ID.
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

        /// <summary>
        /// Ctor.
        /// </summary>
        /// <param name="clock">H265 clock. Default value is 90000.</param>
        public H265Track(int clock = DEFAULT_CLOCK)
        {
            this.VideoClock = clock;
        }

        /// <summary>
        /// Ctor.
        /// </summary>
        /// <param name="vps">Video Parameter Set (VPS).</param>
        /// <param name="sps">Sequence Parameter Set (SPS).</param>
        /// <param name="pps">Picture Parameter Set (PPS).</param>
        /// <param name="clock">H265 clock. Default value is 90000.</param>
        public H265Track(byte[] vps, byte[] sps, byte[] pps, int clock = DEFAULT_CLOCK) : this(clock)
        {
            SetParameterSets(vps, sps, pps);
        }

        /// <summary>
        /// Feed in Raw VPS/SPS/PPS data - no 32 bit headers, no Annex-B (00 00 00 01).
        /// </summary>
        /// <param name="vps">Video Parameter Set (VPS).</param>
        /// <param name="sps">Sequence Parameter Set (SPS).</param>
        /// <param name="pps">Picture Parameter Set (PPS).</param>
        public void SetParameterSets(byte[] vps, byte[] sps, byte[] pps)
        {
            this.VPS = vps;
            this.SPS = sps;
            this.PPS = pps;
        }

        public override StringBuilder BuildSDP(StringBuilder sdp)
        {
            string vps_str = Convert.ToBase64String(VPS);
            string sps_str = Convert.ToBase64String(SPS);
            string pps_str = Convert.ToBase64String(PPS);

            sdp.Append($"m=video 0 RTP/AVP {PayloadType}\n");
            sdp.Append($"a=control:trackID={ID}\n");
            sdp.Append($"a=rtpmap:{PayloadType} {Codec}/{VideoClock}\n");
            sdp.Append($"a=fmtp:{PayloadType} sprop-vps={vps_str}; sprop-sps={sps_str}; sprop-pps={pps_str}\n");
            return sdp;
        }

        /// <summary>
        /// Creates RTP packets.
        /// </summary>
        /// <param name="samples">An array of H265 NALUs.</param>
        /// <param name="rtpTimestamp">RTP timestamp in the timescale of the track.</param>
        /// <returns>RTP packets.</returns>
        public override List<IMemoryOwner<byte>> CreateRtpPackets(ReadOnlySequence<byte> samples, uint rtpTimestamp)
        {
            List<IMemoryOwner<byte>> memoryOwners = new List<IMemoryOwner<byte>>();
            var length = samples.Length;
            foreach (var rawNal in samples)
            {
                bool lastNal = (length -= rawNal.Length) == 0;

                // The H265 Payload could be sent as one large RTP packet (assuming the receiver can handle it)
                // or as a Fragmented Data, split over several RTP packets with the same Timestamp.
                bool fragmenting = false;

                int packetMTU = PacketMTU; // 65535; 
                packetMTU += -8 - 20 - 16; // -8 for UDP header, -20 for IP header, -16 normal RTP header len. ** LESS RTP EXTENSIONS !!!

                if (rawNal.Length > packetMTU)
                {
                    fragmenting = true;
                }

                if (fragmenting)
                {
                    int dataRemaining = rawNal.Length;
                    int nalPointer = 0;
                    int startBit = 1;
                    int endBit = 0;

                    // consume first byte of the raw_nal. It is used in the FU header
                    byte firstByte = rawNal.Span[0];
                    nalPointer++;
                    dataRemaining--;

                    byte secondByte = rawNal.Span[1];
                    nalPointer++;
                    dataRemaining--;

                    while (dataRemaining > 0)
                    {
                        int payloadSize = Math.Min(packetMTU, dataRemaining);
                        
                        if (dataRemaining == payloadSize) 
                            endBit = 1;

                        // 12 is header size. 3 bytes for H265 FU-A header
                        var fuHeader = 3;
                        var destSize = 12 + fuHeader + payloadSize;
                        var owner = AdjustedSizeMemoryOwner.Rent(destSize);
                        memoryOwners.Add(owner);
                        var rtpPacket = owner.Memory.Slice(0, destSize);

                        // RTP Packet Header
                        // 0 - Version, P, X, CC, M, PT and Sequence Number
                        //32 - Timestamp. H265 uses a 90kHz clock
                        //64 - SSRC
                        //96 - CSRCs (optional)
                        //nn - Extension ID and Length
                        //nn - Extension header
                        const bool rtpPadding = false;
                        const bool rtpHasExtension = false;
                        const int rtpCsrcCount = 0;

                        RTPPacketUtil.WriteHeader(rtpPacket.Span, RTPPacketUtil.RTP_VERSION,
                            rtpPadding, rtpHasExtension, rtpCsrcCount, lastNal && endBit == 1, PayloadType);

                        // sequence number and SSRC are set just before send
                        RTPPacketUtil.WriteTS(rtpPacket.Span, rtpTimestamp);

                        // For H265 we need https://www.rfc-editor.org/rfc/rfc7798#section-4.4.3

                        // Now append the Fragmentation Header (with Start and End marker) and part of the raw_nal
                        const byte fBit = 0;
                        byte nalType = (byte)((firstByte & 0x7E) >> 1);
                        const byte type = 49; // FU Fragmentation

                        // PayloadHdr
                        rtpPacket.Span[12] = (byte)((fBit << 7) | ((type << 1) & 0x7E) | (firstByte & 0x1));
                        rtpPacket.Span[13] = secondByte;

                        // FU header
                        rtpPacket.Span[14] = (byte)((startBit << 7) | (endBit << 6) | nalType);

                        rawNal.Span.Slice(nalPointer, payloadSize).CopyTo(rtpPacket.Slice(15).Span);

                        nalPointer += payloadSize;
                        dataRemaining -= payloadSize;

                        startBit = 0;
                    }
                }
                else
                {
                    // Put the whole NAL into one RTP packet.
                    // Note some receivers will have maximum buffers and be unable to handle large RTP packets.
                    // Also with RTP over RTSP there is a limit of 65535 bytes for the RTP packet.

                    // 12 is header size when there are no CSRCs or extensions
                    var owner = AdjustedSizeMemoryOwner.Rent(12 + rawNal.Length);
                    memoryOwners.Add(owner);
                    var rtpPacket = owner.Memory.Slice(0, 12 + rawNal.Length);

                    const bool rtpPadding = false;
                    const bool rtpHasExtension = false;
                    const int rtpCsrcCount = 0;

                    RTPPacketUtil.WriteHeader(rtpPacket.Span,
                        RTPPacketUtil.RTP_VERSION,
                        rtpPadding,
                        rtpHasExtension, rtpCsrcCount, lastNal, PayloadType);

                    // sequence number and SSRC are set just before send
                    RTPPacketUtil.WriteTS(rtpPacket.Span, rtpTimestamp);

                    // Now append the raw NAL
                    rawNal.CopyTo(rtpPacket.Slice(12));
                }
            }

            return memoryOwners;
        }
    }
}
