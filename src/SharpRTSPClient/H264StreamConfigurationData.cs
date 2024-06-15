using System.Collections.Generic;

namespace SharpRTSPClient
{
    public class H264StreamConfigurationData : IVideoStreamConfigurationData
    {
        public byte[] SPS { get; set; }
        public byte[] PPS { get; set; }

        public H264StreamConfigurationData()
        { }

        public H264StreamConfigurationData(byte[] sps, byte[] pps)
        {
            SPS = sps;
            PPS = pps;
        }

        public override string ToString()
        {
            return $"SPS: {Utilities.ToHexString(SPS)}\r\nPPS: {Utilities.ToHexString(PPS)}";
        }

        public IEnumerable<byte[]> GetNALUs()
        {
            return new byte[][] { SPS, PPS };
        }
    }
}
