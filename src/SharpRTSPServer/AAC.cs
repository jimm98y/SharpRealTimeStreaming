using System.Linq;
using System.Text;

namespace SharpRTSPServer
{
    public class AACTrack : ITrack
    {
        public int ID { get; set; } = 1;
        public int SamplingRate { get; set; } = 44100;
        public int Channels { get; set; } = 1;
        public string ConfigDescriptor { get; set; } = "1390"; // hex

        public bool IsReady { get { return !string.IsNullOrWhiteSpace(ConfigDescriptor); } }

        public int PayloadType => RTSPServer.DYNAMIC_PAYLOAD_TYPE + ID;

        public AACTrack(int samplingRate, int channels, byte[] configDescriptor)
        {
            this.SamplingRate = samplingRate;
            this.Channels = channels;
            this.ConfigDescriptor = Utilities.ToHexString(configDescriptor);
        }

        public StringBuilder BuildSDP(StringBuilder sdp)
        {
            sdp.Append($"m=audio 0 RTP/AVP {PayloadType}\n"); // <---- Payload Type 0 means G711 ULAW, 96+ means dynamic payload type
            sdp.Append($"a=control:trackID={ID}\n");
            sdp.Append($"a=rtpmap:{PayloadType} mpeg4-generic/{SamplingRate}/{Channels}\n");
            sdp.Append($"a=fmtp:{PayloadType} profile-level-id={GetAACProfileLevel(SamplingRate, Channels)}; " +
                $"config={ConfigDescriptor}; streamType=5; mode=AAC-hbr; objectType=64; sizeLength=13; indexLength=3; indexDeltaLength=3\n");
            return sdp;
        }

        private static int GetAACLevel(int samplingFrequency, int channelConfiguration)
        {
            if (samplingFrequency <= 24000)
            {
                if (channelConfiguration <= 2)
                    return 1; // AAC Profile, Level 1
            }
            else if (samplingFrequency <= 48000)
            {
                if (channelConfiguration <= 2)
                    return 2; // Level 2
                else if (channelConfiguration <= 5)
                    return 4; // Level 4
            }
            else if (samplingFrequency <= 96000)
            {
                if (channelConfiguration <= 5)
                    return 5; // Level 5
            }

            return 5;
        }

        private static int GetAACHELevel(int samplingFrequency, int channelConfiguration, bool sbr)
        {
            if (samplingFrequency <= 48000)
            {
                if (channelConfiguration <= 2)
                    return sbr ? 3 : 2; // Level 2/3
                else if (channelConfiguration <= 5)
                    return 4; // Level 4
            }
            else if (samplingFrequency <= 96000)
            {
                if (channelConfiguration <= 5)
                    return 5; // Level 5
            }

            return 5;
        }

        private static int GetAACHQLevel(int samplingFrequency, int channelConfiguration)
        {
            if (samplingFrequency <= 22050)
            {
                if (channelConfiguration <= 2)
                    return 1; // Level 1/5
            }
            else if (samplingFrequency <= 48000)
            {
                if (channelConfiguration <= 2)
                    return 2; // Level 2/6
                else if (channelConfiguration <= 5)
                    return 3; // Level 3/4/7/8
            }

            return 8;
        }

        private static int GetAACProfileLevel(int samplingFrequency, int channelConfiguration, int profile = 2, bool sbr = false)
        {
            switch (profile)
            {
                case 2: // AAC_LC
                    return GetAACLevel(samplingFrequency, channelConfiguration) - 1 + 0x28;

                case 5: // AAC_SBR
                    return GetAACHELevel(samplingFrequency, channelConfiguration, sbr) - 2 + 0x2C;

                case 29: // AAC_PS
                    return GetAACHELevel(samplingFrequency, channelConfiguration, sbr) - 2 + 0x30;

                case 8: // AAC_CELP
                    return GetAACHQLevel(samplingFrequency, channelConfiguration) - 1 + 0x0E;

                default:
                    return 1;
            }
        }

        public static byte[] AppendAUHeader(byte[] frame)
        {
            // append AU header (required for AAC)
            short frameLen = (short)(frame.Length << 3);
            byte[] header = new byte[4];
            header[0] = 0x00;
            header[1] = 0x10; // 16 bits size of the header
            header[2] = (byte)((frameLen >> 8) & 0xFF);
            header[3] = (byte)(frameLen & 0xFF);
            return header.Concat(frame).ToArray();
        }
    }
}
