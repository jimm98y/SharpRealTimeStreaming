using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace SharpRTSPServer
{
    public class H265Track : IVideoTrack
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

            byte second_byte = raw_nal[1];
            nal_pointer++;
            data_remaining--;

            // For H265 we need https://www.rfc-editor.org/rfc/rfc7798#section-4.4.3
            while (data_remaining > 0)
            {
                int payload_size = Math.Min(packetMTU, data_remaining);
                if (data_remaining == payload_size) end_bit = 1;

                // 12 is header size. 3 bytes for H265 FU-A header
                var fuHeader = 3;
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
                byte nalType = (byte)((first_byte & 0x7E) >> 1);
                const byte type = 49; // FU Fragmentation

                // PayloadHdr
                rtp_packet.Span[12] = (byte)((f_bit << 7) | ((type << 1) & 0x7E) | (first_byte & 0x1));
                rtp_packet.Span[13] = second_byte;

                // FU header
                rtp_packet.Span[14] = (byte)((start_bit << 7) | (end_bit << 6) | nalType);

                raw_nal.AsSpan(nal_pointer, payload_size).CopyTo(rtp_packet.Slice(15).Span);

                nal_pointer += payload_size;
                data_remaining -= payload_size;

                rtp_packets.Add(rtp_packet);

                start_bit = 0;
            }
        }
    }
}
