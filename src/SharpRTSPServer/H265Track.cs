using System;
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
    }
}
