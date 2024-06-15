using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace SharpRTSPServer
{
    public class H264Track : ITrack
    {
        public const string VideoCodec = "H264";
        public const int DEFAULT_CLOCK = 90000;

        public int ID { get; set; } = 0;
        public int ProfileIdc { get; set; } = 77; // Main Profile
        public int ProfileIop { get; set; } = 0; // bit 7 (msb) is 0 so constrained_flag is false
        public int Level { get; set; } = 42; // Level 4.2
        public int VideoClock { get; set; } = DEFAULT_CLOCK;

        public byte[] SPS { get; set; }
        public byte[] PPS { get; set; }

        public bool IsReady { get { return SPS != null && PPS != null; } }

        public int PayloadType => RTSPServer.DYNAMIC_PAYLOAD_TYPE + ID;

        public H264Track(int profileIdc = 77, int profileIop = 0, int level = 42, int clock = DEFAULT_CLOCK)
        {
            this.ProfileIdc = profileIdc;
            this.ProfileIop = profileIop;
            this.Level = level;
            this.VideoClock = clock;
        }

        // Feed in Raw SPS/PPS data - no 32 bit headers, no 00 00 00 01 headers
        public void SetSPS_PPS(byte[] sps, byte[] pps)
        {
            this.SPS = sps;
            this.PPS = pps;
        }

        public StringBuilder BuildSDP(StringBuilder sdp)
        {
            // Make the profile-level-id
            // Eg a string of profile-level-id=42A01E is
            // a Profile eg Constrained Baseline, Baseline, Extended, Main, High. This defines which features in H264 are used
            // a Level eg 1,2,3 or 4. This defines a max resoution for the video. 2=up to SD, 3=upto 1080p. Decoders can then reserve sufficient RAM for frame buffers

            string profile_level_id_str = ProfileIdc.ToString("X2") + // convert to hex, padded to 2 characters
                                          ProfileIop.ToString("X2") +
                                          Level.ToString("X2");

            // Make the Base64 SPS and PPS
            // raw_sps has no 0x00 0x00 0x00 0x01 or 32 bit size header
            // raw_pps has no 0x00 0x00 0x00 0x01 or 32 bit size header
            string sps_str = Convert.ToBase64String(SPS);
            string pps_str = Convert.ToBase64String(PPS);

            sdp.Append($"m=video 0 RTP/AVP {PayloadType}\n");
            sdp.Append($"a=control:trackID={ID}\n");
            sdp.Append($"a=rtpmap:{PayloadType} {VideoCodec}/{VideoClock}\n");
            sdp.Append($"a=fmtp:{PayloadType} profile-level-id=").Append(profile_level_id_str)
                .Append("; sprop-parameter-sets=").Append(sps_str).Append(',').Append(pps_str).Append("\n");
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

                    //  For H264 (https://www.rfc-editor.org/rfc/rfc6184#page-29),
                    while (dataRemaining > 0)
                    {
                        int payloadSize = Math.Min(packetMTU, dataRemaining);
                        if (dataRemaining == payloadSize) endBit = 1;

                        // 12 is header size. 2 bytes for H264 FU-A header
                        var fuHeader = 2;
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
                        byte nri = (byte)(firstByte >> 5 & 0x03); // Part of the 1st byte of the Raw NAL (NAL Reference ID)
                        const byte type = 28; // FU-A Fragmentation

                        rtpPacket.Span[12] = (byte)((fBit << 7) + (nri << 5) + type);
                        rtpPacket.Span[13] = (byte)((startBit << 7) + (endBit << 6) + (0 << 5) + (firstByte & 0x1F));

                        rawNal.AsSpan(nalPointer, payloadSize).CopyTo(rtpPacket.Slice(14).Span);

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
