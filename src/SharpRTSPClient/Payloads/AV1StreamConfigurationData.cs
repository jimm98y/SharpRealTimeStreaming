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

using System;

namespace SharpRTSPClient
{
    /// <summary>
    /// What the SDP says about an AV1 stream.
    /// </summary>
    /// <remarks>
    /// The sequence header travels in the stream, so there is nothing out of band a decoder needs;
    /// what is left is the profile, level and tier, which a receiver negotiates before it picks a
    /// decoder. Each holds the value the payload format says to assume where the SDP gives none.
    /// <see href="https://aomediacodec.github.io/av1-rtp-spec/#72-sdp-parameters" />
    /// </remarks>
    public class AV1StreamConfigurationData : IStreamConfigurationData
    {
        /// <summary>
        /// The fmtp's format parameters as the SDP wrote them, without the "a=fmtp:&lt;payload type&gt; "
        /// in front; null where the SDP has no fmtp for the stream.
        /// </summary>
        public string Fmtp { get; set; }

        /// <summary>
        /// The seq_profile, 0 to 2: 0 (Main) where the SDP gives none.
        /// </summary>
        public int Profile { get; set; }

        /// <summary>
        /// The seq_level_idx, 0 to 31: 5 (level 3.1) where the SDP gives none.
        /// </summary>
        public int LevelIdx { get; set; } = 5;

        /// <summary>
        /// The seq_tier, 0 or 1: 0 (Main) where the SDP gives none.
        /// </summary>
        public int Tier { get; set; }

        public AV1StreamConfigurationData()
        { }

        public AV1StreamConfigurationData(int profile, int levelIdx, int tier)
        {
            Profile = profile;
            LevelIdx = levelIdx;
            Tier = tier;
        }

        /// <summary>
        /// Reads the profile, level and tier out of the fmtp's format parameters, which may be null or empty.
        /// </summary>
        /// <exception cref="FormatException">One of them is there but out of range.</exception>
        public static AV1StreamConfigurationData Parse(string formatParameter)
        {
            var parameters = FormatParameters.Parse(formatParameter);
            return new AV1StreamConfigurationData(
                parameters.GetInt("profile", 0, 0, 2),
                parameters.GetInt("level-idx", 5, 0, 31),
                parameters.GetInt("tier", 0, 0, 1)) { Fmtp = formatParameter };
        }

        public override string ToString()
        {
            return $"Profile: {Profile}, LevelIdx: {LevelIdx}, Tier: {Tier}";
        }
    }
}
