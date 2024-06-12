using System;
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
    }
}
