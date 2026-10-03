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
using System.Globalization;

namespace SharpRTSPClient
{
    /// <summary>
    /// What the SDP says about an H264 stream.
    /// </summary>
    /// <remarks>
    /// The parameter sets are null where the SDP carries none, in which case they arrive in the
    /// stream itself. The rest hold the value RFC 6184 says to assume where the SDP gives none.
    /// <see href="https://datatracker.ietf.org/doc/html/rfc6184#section-8.1" />
    /// </remarks>
    public class H264StreamConfigurationData : IStreamConfigurationData
    {
        /// <summary>
        /// The fmtp's format parameters as the SDP wrote them, without the "a=fmtp:&lt;payload type&gt; "
        /// in front; null where the SDP has no fmtp for the stream.
        /// </summary>
        public string Fmtp { get; set; }

        public byte[] SPS { get; set; }
        public byte[] PPS { get; set; }

        /// <summary>
        /// profile-level-id as written in the SDP, six hex digits; null where the SDP gives none.
        /// </summary>
        /// <remarks>
        /// Left null rather than set to the RFC's default of 42000A, because cameras that leave it
        /// out rarely mean Baseline level 1: the SPS says what the stream really is.
        /// </remarks>
        public string ProfileLevelId { get; set; }

        /// <summary>
        /// packetization-mode, 0 to 2: 0 (single NAL unit) where the SDP gives none.
        /// </summary>
        public int PacketizationMode { get; set; }

        public H264StreamConfigurationData()
        { }

        public H264StreamConfigurationData(byte[] sps, byte[] pps)
        {
            SPS = sps;
            PPS = pps;
        }

        /// <summary>
        /// Reads the profile-level-id and packetization-mode out of the fmtp's format parameters,
        /// which may be null or empty. The parameter sets are left to the caller.
        /// </summary>
        /// <exception cref="FormatException">One of them is there but is not a valid value.</exception>
        public static H264StreamConfigurationData Parse(string formatParameter)
        {
            var parameters = FormatParameters.Parse(formatParameter);

            string profileLevelId = parameters.GetString("profile-level-id");
            if (profileLevelId != null &&
                (profileLevelId.Length != 6 || !int.TryParse(profileLevelId, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _)))
            {
                throw new FormatException($"'{profileLevelId}' is not a valid profile-level-id.");
            }

            return new H264StreamConfigurationData()
            {
                Fmtp = formatParameter,
                ProfileLevelId = profileLevelId,
                PacketizationMode = parameters.GetInt("packetization-mode", 0, 0, 2),
            };
        }

        public override string ToString()
        {
            return $"SPS: {Utilities.ToHexString(SPS)}\r\nPPS: {Utilities.ToHexString(PPS)}\r\nProfileLevelId: {ProfileLevelId}, PacketizationMode: {PacketizationMode}";
        }
    }
}
