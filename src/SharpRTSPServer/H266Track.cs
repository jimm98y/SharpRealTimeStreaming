using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace SharpRTSPServer
{
    /// <summary>
    /// H266 video track.
    /// </summary>
    public class H266Track : TrackBase
    {
        /// <summary>
        /// H266 Video Codec name.
        /// </summary>
        public override string Codec => "H266";

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
        /// Decoding Capability Information (DCI).
        /// </summary>
        public byte[] DCI { get; set; }

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
        /// Supplemental Enhancement Information (SEI).
        /// </summary>
        public byte[] SEI { get; set; }

        /// <summary>
        /// Is the track ready?
        /// </summary>
        public override bool IsReady { get { return SPS != null && PPS != null; } }

        private int _payloadType = -1;

        /// <summary>
        /// Payload type. H266 uses a dynamic payload type, which by default we calculate as 96 + track ID.
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
        /// <param name="clock">H266 clock. Default value is 90000.</param>
        public H266Track(int clock = DEFAULT_CLOCK)
        {
            this.VideoClock = clock;
        }

        /// <summary>
        /// Ctor.
        /// </summary>
        /// <param name="dci">Decoding Capability Information (DCI).</param>
        /// <param name="vps">Video Parameter Set (VPS).</param>
        /// <param name="sps">Sequence Parameter Set (SPS).</param>
        /// <param name="pps">Picture Parameter Set (PPS).</param>
        /// <param name="sei">Supplemental Enhancement Information (SEI).</param>
        /// <param name="clock">H266 clock. Default value is 90000.</param>
        public H266Track(byte[] dci, byte[] vps, byte[] sps, byte[] pps, byte[] sei = null, int clock = DEFAULT_CLOCK) : this(clock)
        {
            SetParameterSets(dci, vps, sps, pps, sei);
        }

        /// <summary>
        /// Feed in Raw DCI/VPS/SPS/PPS data - no 32 bit headers, no Annex-B (00 00 00 01).
        /// </summary>
        /// <param name="dci">Decoding Capability Information (DCI).</param>
        /// <param name="vps">Video Parameter Set (VPS).</param>
        /// <param name="sps">Sequence Parameter Set (SPS).</param>
        /// <param name="pps">Picture Parameter Set (PPS).</param>
        /// <param name="sei">Supplemental Enhancement Information (SEI).</param>
        public void SetParameterSets(byte[] dci, byte[] vps, byte[] sps, byte[] pps, byte[] sei)
        {
            this.DCI = dci;
            this.VPS = vps;
            this.SPS = sps;
            this.PPS = pps;
            this.SEI = sei;
        }

        public override StringBuilder BuildSDP(StringBuilder sdp)
        {
            string dci = "";
            string vps = "";
            string sps = "";
            string pps = "";
            string sei = "";

            if (DCI != null && DCI.Length > 0)
                dci = "; sprop-dci=" + Convert.ToBase64String(DCI);
            if (VPS != null && VPS.Length > 0)
                vps = "; sprop-vps=" + Convert.ToBase64String(VPS);
            if (SPS != null && SPS.Length > 0)
                sps = "; sprop-sps=" + Convert.ToBase64String(SPS);
            if (PPS != null && PPS.Length > 0)
                pps = "; sprop-pps=" + Convert.ToBase64String(PPS);
            if (SEI != null && SEI.Length > 0)
                sei = "; sprop-sei=" + Convert.ToBase64String(SEI);
            
            sdp.Append($"m=video 0 RTP/AVP {PayloadType}\n");
            sdp.Append($"a=control:trackID={ID}\n");
            sdp.Append($"a=rtpmap:{PayloadType} {Codec}/{VideoClock}\n");

            string additionalParams = dci + vps + sps + pps + sei;
            additionalParams = additionalParams.TrimStart(';');

            sdp.Append($"a=fmtp:{PayloadType}{additionalParams}\n");
            return sdp;
        }

        /// <summary>
        /// Creates RTP packets.
        /// </summary>
        /// <param name="samples">An array of H266 NALUs.</param>
        /// <param name="rtpTimestamp">RTP timestamp in the timescale of the track.</param>
        /// <returns>RTP packets.</returns>
        public override (List<Memory<byte>>, List<IMemoryOwner<byte>>) CreateRtpPackets(List<byte[]> samples, uint rtpTimestamp)
        {
            List<Memory<byte>> rtpPackets = new List<Memory<byte>>();
            List<IMemoryOwner<byte>> memoryOwners = new List<IMemoryOwner<byte>>();
            for (int x = 0; x < samples.Count; x++)
            {
                var rawNal = samples[x];
                bool lastNal = false;
                if (x == samples.Count - 1)
                {
                    lastNal = true; // last NAL in our nal_array
                }

                // The H266 Payload could be sent as one large RTP packet (assuming the receiver can handle it)
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
                    int pBit = 0;

                    // consume first byte of the raw_nal. It is used in the FU header
                    byte firstByte = rawNal[0];
                    nalPointer++;
                    dataRemaining--;

                    byte secondByte = rawNal[1];
                    nalPointer++;
                    dataRemaining--;

                    while (dataRemaining > 0)
                    {
                        int payloadSize = Math.Min(packetMTU, dataRemaining);

                        if (dataRemaining == payloadSize)
                            endBit = 1;

                        if (dataRemaining == payloadSize && lastNal)
                            pBit = 1;

                        // 12 is header size. 3 bytes for H266 FU-A header
                        var fuHeader = 3;
                        var destSize = 12 + fuHeader + payloadSize;
                        var owner = MemoryPool<byte>.Shared.Rent(destSize);
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

                        // For H266 we need https://www.rfc-editor.org/rfc/rfc7798#section-4.4.3

                        // Now append the Fragmentation Header (with Start and End marker) and part of the raw_nal
                        byte nalType = (byte)((secondByte & 0xF8) >> 1);
                        const byte type = 29; // FU Fragmentation

                        // PayloadHdr
                        rtpPacket.Span[12] = firstByte;
                        rtpPacket.Span[13] = (byte)(((type << 3) & 0xF8) | (secondByte & 0x07));

                        // FU header
                        rtpPacket.Span[14] = (byte)((startBit << 7) | (endBit << 6) | (pBit << 5) | nalType);

                        rawNal.AsSpan(nalPointer, payloadSize).CopyTo(rtpPacket.Slice(15).Span);

                        nalPointer += payloadSize;
                        dataRemaining -= payloadSize;

                        rtpPackets.Add(rtpPacket);

                        startBit = 0;
                    }
                }
                else
                {
                    // Put the whole NAL into one RTP packet.
                    // Note some receivers will have maximum buffers and be unable to handle large RTP packets.
                    // Also with RTP over RTSP there is a limit of 65535 bytes for the RTP packet.

                    // 12 is header size when there are no CSRCs or extensions
                    var owner = MemoryPool<byte>.Shared.Rent(12 + rawNal.Length);
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

                    rtpPackets.Add(rtpPacket);
                }
            }

            return (rtpPackets, memoryOwners);
        }
    }
}
