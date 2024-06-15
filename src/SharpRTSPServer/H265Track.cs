using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace SharpRTSPServer
{
    public class H265Track : ITrack
    {
        public const string VideoCodec = "H265";
        public const int DEFAULT_CLOCK = 90000;

        public int ID { get; set; } = 0;
        public int VideoClock { get; set; } = DEFAULT_CLOCK;

        public byte[] VPS { get; set; }
        public byte[] SPS { get; set; }
        public byte[] PPS { get; set; }

        public bool IsReady { get { return VPS != null && SPS != null && PPS != null; } }

        public int PayloadType => RTSPServer.DYNAMIC_PAYLOAD_TYPE + ID;

        public H265Track(int clock = DEFAULT_CLOCK)
        {
            this.VideoClock = clock;
        }

        // Feed in Raw VPS/SPS/PPS data - no 32 bit headers, no 00 00 00 01 headers
        public void SetVPS_SPS_PPS(byte[] vps, byte[] sps, byte[] pps)
        {
            this.VPS = vps;
            this.SPS = sps;
            this.PPS = pps;
        }

        public StringBuilder BuildSDP(StringBuilder sdp)
        {
            string vps_str = Convert.ToBase64String(VPS);
            string sps_str = Convert.ToBase64String(SPS);
            string pps_str = Convert.ToBase64String(PPS);

            sdp.Append($"m=video 0 RTP/AVP {PayloadType}\n");
            sdp.Append($"a=control:trackID={ID}\n");
            sdp.Append($"a=rtpmap:{PayloadType} {VideoCodec}/{VideoClock}\n");
            sdp.Append($"a=fmtp:{PayloadType} sprop-vps=").Append(vps_str).Append("; sprop-sps=").Append(sps_str).Append("; sprop-pps=").Append(pps_str).Append("\n");
            return sdp;
        }

        public (List<Memory<byte>>, List<IMemoryOwner<byte>>) PrepareRtpPackets(List<byte[]> nalArray, uint rtpTimestamp)
        {
            List<Memory<byte>> rtpPackets = new List<Memory<byte>>();
            List<IMemoryOwner<byte>> memoryOwners = new List<IMemoryOwner<byte>>();
            for (int x = 0; x < nalArray.Count; x++)
            {
                var rawNal = nalArray[x];
                bool lastNal = false;
                if (x == nalArray.Count - 1)
                {
                    lastNal = true; // last NAL in our nal_array
                }

                // The H264/H265 Payload could be sent as one large RTP packet (assuming the receiver can handle it)
                // or as a Fragmented Data, split over several RTP packets with the same Timestamp.
                bool fragmenting = false;

                int packetMTU = 1400; // 65535; 
                packetMTU += -8 - 20 - 16; // -8 for UDP header, -20 for IP header, -16 normal RTP header len. ** LESS RTP EXTENSIONS !!!

                if (rawNal.Length > packetMTU)
                {
                    fragmenting = true;
                }

                // INDIGO VISION DOES NOT SUPPORT FRAGMENTATION. Send as one jumbo RTP packet and let OS split over MTUs.
                // NOTE TO SELF... perhaps this was because the SDP did not have the extra packetization flag
                //  fragmenting = false;
                if (fragmenting)
                {
                    int dataRemaining = rawNal.Length;
                    int nalPointer = 0;
                    int startBit = 1;
                    int endBit = 0;

                    // consume first byte of the raw_nal. It is used in the FU header
                    byte firstByte = rawNal[0];
                    nalPointer++;
                    dataRemaining--;

                    byte secondByte = rawNal[1];
                    nalPointer++;
                    dataRemaining--;

                    // For H265 we need https://www.rfc-editor.org/rfc/rfc7798#section-4.4.3
                    while (dataRemaining > 0)
                    {
                        int payloadSize = Math.Min(packetMTU, dataRemaining);
                        if (dataRemaining == payloadSize) endBit = 1;

                        // 12 is header size. 3 bytes for H265 FU-A header
                        var fuHeader = 3;
                        var destSize = 12 + fuHeader + payloadSize;
                        var owner = MemoryPool<byte>.Shared.Rent(destSize);
                        memoryOwners.Add(owner);
                        var rtpPacket = owner.Memory.Slice(0, destSize);

                        // RTP Packet Header
                        // 0 - Version, P, X, CC, M, PT and Sequence Number
                        //32 - Timestamp. H264/H265 uses a 90kHz clock
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

                        // Now append the Fragmentation Header (with Start and End marker) and part of the raw_nal
                        const byte fBit = 0;
                        byte nalType = (byte)((firstByte & 0x7E) >> 1);
                        const byte type = 49; // FU Fragmentation

                        // PayloadHdr
                        rtpPacket.Span[12] = (byte)((fBit << 7) | ((type << 1) & 0x7E) | (firstByte & 0x1));
                        rtpPacket.Span[13] = secondByte;

                        // FU header
                        rtpPacket.Span[14] = (byte)((startBit << 7) | (endBit << 6) | nalType);

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

                    // RTP Packet Header
                    // 0 - Version, P, X, CC, M, PT and Sequence Number
                    //32 - Timestamp. H264 uses a 90kHz clock
                    //64 - SSRC
                    //96 - CSRCs (optional)
                    //nn - Extension ID and Length
                    //nn - Extension header

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
