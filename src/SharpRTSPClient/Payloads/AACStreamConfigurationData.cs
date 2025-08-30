namespace SharpRTSPClient
{
    public class AACStreamConfigurationData : IStreamConfigurationData
    {
        public AACStreamConfigurationData()
        { }

        public AACStreamConfigurationData(int objectType, int frequencyIndex, int samplingFrequency, int channelConfiguration)
        {
            ObjectType = objectType;
            FrequencyIndex = frequencyIndex;
            SamplingFrequency = samplingFrequency;
            ChannelConfiguration = channelConfiguration;
        }

        public int ObjectType { get; set; }
        public int FrequencyIndex { get; set; }
        public int SamplingFrequency { get; set; }
        public int ChannelConfiguration { get; set; }
    }
}
