namespace SharpRTSPClient
{
    public class H265StreamConfigurationData : IStreamConfigurationData
    {
        public byte[] VPS { get; set; }
        public byte[] SPS { get; set; }
        public byte[] PPS { get; set; }

        public H265StreamConfigurationData()
        { }

        public H265StreamConfigurationData(byte[] vps, byte[] sps, byte[] pps)
        {
            VPS = vps;
            SPS = sps;
            PPS = pps;
        }

        public override string ToString()
        {
            return $"VPS: {Utilities.ToHexString(VPS)}\r\nSPS: {Utilities.ToHexString(SPS)}\r\nPPS: {Utilities.ToHexString(PPS)}";
        }
    }
}
