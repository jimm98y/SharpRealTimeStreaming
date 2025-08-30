namespace SharpRTSPClient
{
    public class H266StreamConfigurationData : IStreamConfigurationData
    {
        public byte[] DCI { get; set; }
        public byte[] VPS { get; set; }
        public byte[] SPS { get; set; }
        public byte[] PPS { get; set; }
        public byte[] SEI { get; set; }

        public H266StreamConfigurationData()
        { }

        public H266StreamConfigurationData(byte[] dci, byte[] vps, byte[] sps, byte[] pps, byte[] sei)
        {
            DCI = dci;
            VPS = vps;
            SPS = sps;
            PPS = pps;
            SEI = sei;
        }

        public override string ToString()
        {
            return $"DCI: {Utilities.ToHexString(DCI)}\r\nVPS: {Utilities.ToHexString(VPS)}\r\nSPS: {Utilities.ToHexString(SPS)}\r\nPPS: {Utilities.ToHexString(PPS)}\r\nSEI: {Utilities.ToHexString(SEI)}";
        }
    }
}
