// SharpRTSPClient
// Copyright (C) 2026 Lukas Volf
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

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
