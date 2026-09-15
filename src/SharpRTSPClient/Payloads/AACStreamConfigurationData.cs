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

        /// <summary>
        /// Sampling frequencies indexed by the samplingFrequencyIndex of an AudioSpecificConfig,
        /// as defined by ISO/IEC 14496-3. Index 13 and 14 are reserved, 15 means the frequency is
        /// written out explicitly instead of indexed.
        /// </summary>
        private static readonly int[] SamplingFrequencies =
        {
            96000, 88200, 64000, 48000, 44100, 32000,
            24000, 22050, 16000, 12000, 11025, 8000, 7350,
        };

        /// <summary>
        /// Translates a samplingFrequencyIndex into the frequency in Hz, or 0 when the index does
        /// not name one.
        /// </summary>
        public static int GetSamplingFrequency(int frequencyIndex)
        {
            return frequencyIndex >= 0 && frequencyIndex < SamplingFrequencies.Length
                ? SamplingFrequencies[frequencyIndex]
                : 0;
        }
    }
}
