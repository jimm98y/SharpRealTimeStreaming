using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace SharpRTSPServer
{
    public class H264Track : IVideoTrack
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

        public void FragmentNal(List<Memory<byte>> rtp_packets, List<IMemoryOwner<byte>> memoryOwners, uint rtp_timestamp, byte[] raw_nal, bool last_nal, int packetMTU)
        {
            int data_remaining = raw_nal.Length;
            int nal_pointer = 0;
            int start_bit = 1;
            int end_bit = 0;

            // consume first byte of the raw_nal. It is used in the FU header
            byte first_byte = raw_nal[0];
            nal_pointer++;
            data_remaining--;

            //  For H264 (https://www.rfc-editor.org/rfc/rfc6184#page-29),
            while (data_remaining > 0)
            {
                int payload_size = Math.Min(packetMTU, data_remaining);
                if (data_remaining == payload_size) end_bit = 1;

                // 12 is header size. 2 bytes for H264 FU-A header
                var fuHeader = 2;
                var destSize = 12 + fuHeader + payload_size;
                var owner = MemoryPool<byte>.Shared.Rent(destSize);
                memoryOwners.Add(owner);
                var rtp_packet = owner.Memory.Slice(0, destSize);

                // RTP Packet Header
                // 0 - Version, P, X, CC, M, PT and Sequence Number
                //32 - Timestamp. H264/H265 uses a 90kHz clock
                //64 - SSRC
                //96 - CSRCs (optional)
                //nn - Extension ID and Length
                //nn - Extension header

                const bool rtpPadding = false;
                const bool rtpHasExtension = false;
                const int rtp_csrc_count = 0;

                RTPPacketUtil.WriteHeader(rtp_packet.Span, RTPPacketUtil.RTP_VERSION,
                    rtpPadding, rtpHasExtension, rtp_csrc_count, last_nal && end_bit == 1, PayloadType);

                // sequence number and SSRC are set just before send
                RTPPacketUtil.WriteTS(rtp_packet.Span, rtp_timestamp);

                // Now append the Fragmentation Header (with Start and End marker) and part of the raw_nal
                const byte f_bit = 0;
                byte nri = (byte)(first_byte >> 5 & 0x03); // Part of the 1st byte of the Raw NAL (NAL Reference ID)
                const byte type = 28; // FU-A Fragmentation

                rtp_packet.Span[12] = (byte)((f_bit << 7) + (nri << 5) + type);
                rtp_packet.Span[13] = (byte)((start_bit << 7) + (end_bit << 6) + (0 << 5) + (first_byte & 0x1F));

                raw_nal.AsSpan(nal_pointer, payload_size).CopyTo(rtp_packet.Slice(14).Span);

                nal_pointer += payload_size;
                data_remaining -= payload_size;

                rtp_packets.Add(rtp_packet);

                start_bit = 0;
            }
        }
    }
}
