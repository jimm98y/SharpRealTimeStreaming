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
    /// What the SDP says about a VP9 stream.
    /// </summary>
    /// <remarks>
    /// There are no parameter sets to carry, since each key frame's header holds what a decoder
    /// needs; what is left is the profile, which a receiver has to know before it picks a decoder -
    /// a WebRTC peer, for one, negotiates it.
    /// <see href="https://datatracker.ietf.org/doc/html/rfc9628#section-6" />
    /// </remarks>
    public class VP9StreamConfigurationData : IStreamConfigurationData
    {
        /// <summary>
        /// The fmtp's format parameters as the SDP wrote them, without the "a=fmtp:&lt;payload type&gt; "
        /// in front; null where the SDP has no fmtp for the stream.
        /// </summary>
        public string Fmtp { get; set; }

        /// <summary>
        /// The profile, 0 to 3. 0 where the SDP gives none, as RFC 9628 says it is to be taken.
        /// </summary>
        public int ProfileId { get; set; }

        public VP9StreamConfigurationData()
        { }

        public VP9StreamConfigurationData(int profileId)
        {
            ProfileId = profileId;
        }

        /// <summary>
        /// Reads the profile out of the fmtp's format parameters, which may be null or empty.
        /// </summary>
        /// <exception cref="FormatException">The profile-id is there but is not one of 0 to 3.</exception>
        public static VP9StreamConfigurationData Parse(string formatParameter)
        {
            var parameters = FormatParameters.Parse(formatParameter);
            return new VP9StreamConfigurationData(parameters.GetInt("profile-id", 0, 0, 3)) { Fmtp = formatParameter };
        }

        public override string ToString()
        {
            return $"Profile: {ProfileId}";
        }
    }
}
