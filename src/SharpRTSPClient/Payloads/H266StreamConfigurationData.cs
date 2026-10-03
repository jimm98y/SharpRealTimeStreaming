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
    /// What the SDP says about an H266 stream.
    /// </summary>
    /// <remarks>
    /// The parameter sets are null where the SDP carries none, in which case they arrive in the
    /// stream itself. The rest hold the value RFC 9328 says to assume where the SDP gives none.
    /// <see href="https://datatracker.ietf.org/doc/html/rfc9328#section-7.1" />
    /// </remarks>
    public class H266StreamConfigurationData : IStreamConfigurationData
    {
        /// <summary>
        /// The fmtp's format parameters as the SDP wrote them, without the "a=fmtp:&lt;payload type&gt; "
        /// in front; null where the SDP has no fmtp for the stream.
        /// </summary>
        public string Fmtp { get; set; }

        public byte[] DCI { get; set; }
        public byte[] VPS { get; set; }
        public byte[] SPS { get; set; }
        public byte[] PPS { get; set; }
        public byte[] SEI { get; set; }

        /// <summary>
        /// profile-id, 0 to 127: 1 (Main 10) where the SDP gives none.
        /// </summary>
        public int ProfileId { get; set; } = 1;

        /// <summary>
        /// tier-flag, 0 or 1: 0 (Main) where the SDP gives none.
        /// </summary>
        public int TierFlag { get; set; }

        /// <summary>
        /// level-id, 0 to 255: 51 (level 3.1) where the SDP gives none.
        /// </summary>
        public int LevelId { get; set; } = 51;

        /// <summary>
        /// sprop-max-don-diff: 0 where the SDP gives none. Anything above 0 means each NAL unit in
        /// the payload carries a decoding order number (DONL/DOND) in front of it.
        /// </summary>
        public int MaxDonDiff { get; set; }

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

        /// <summary>
        /// Reads the profile, tier, level and sprop-max-don-diff out of the fmtp's format
        /// parameters, which may be null or empty. The parameter sets are left to the caller.
        /// </summary>
        /// <exception cref="FormatException">One of them is there but out of range.</exception>
        public static H266StreamConfigurationData Parse(string formatParameter)
        {
            var parameters = FormatParameters.Parse(formatParameter);
            return new H266StreamConfigurationData()
            {
                Fmtp = formatParameter,
                ProfileId = parameters.GetInt("profile-id", 1, 0, 127),
                TierFlag = parameters.GetInt("tier-flag", 0, 0, 1),
                LevelId = parameters.GetInt("level-id", 51, 0, 255),
                MaxDonDiff = parameters.GetInt("sprop-max-don-diff", 0, 0, 32767),
            };
        }

        public override string ToString()
        {
            return $"DCI: {Utilities.ToHexString(DCI)}\r\nVPS: {Utilities.ToHexString(VPS)}\r\nSPS: {Utilities.ToHexString(SPS)}\r\nPPS: {Utilities.ToHexString(PPS)}\r\nSEI: {Utilities.ToHexString(SEI)}\r\n" +
                $"ProfileId: {ProfileId}, TierFlag: {TierFlag}, LevelId: {LevelId}, MaxDonDiff: {MaxDonDiff}";
        }
    }
}
