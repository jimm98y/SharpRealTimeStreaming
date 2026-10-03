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
    /// What the SDP says about an Opus stream.
    /// </summary>
    /// <remarks>
    /// The rtpmap of Opus always says two channels, whatever is sent, so whether the stream is
    /// stereo is only in the fmtp. Each holds the value RFC 7587 says to assume where the SDP gives none.
    /// <see href="https://datatracker.ietf.org/doc/html/rfc7587#section-6.1" />
    /// </remarks>
    public class OpusStreamConfigurationData : IStreamConfigurationData
    {
        /// <summary>
        /// The fmtp's format parameters as the SDP wrote them, without the "a=fmtp:&lt;payload type&gt; "
        /// in front; null where the SDP has no fmtp for the stream.
        /// </summary>
        public string Fmtp { get; set; }

        /// <summary>
        /// sprop-stereo: whether the sender is likely to send stereo.
        /// </summary>
        public bool SpropStereo { get; set; }

        /// <summary>
        /// stereo: whether the sender of the SDP prefers to receive stereo.
        /// </summary>
        public bool Stereo { get; set; }

        /// <summary>
        /// useinbandfec: whether the sender of the SDP can make use of in-band FEC.
        /// </summary>
        public bool UseInbandFec { get; set; }

        /// <summary>
        /// usedtx: whether the sender of the SDP prefers DTX.
        /// </summary>
        public bool UseDtx { get; set; }

        /// <summary>
        /// sprop-maxcapturerate: the most the sender captures at, in Hz; 48000 where the SDP gives none.
        /// </summary>
        public int SpropMaxCaptureRate { get; set; } = 48000;

        public OpusStreamConfigurationData()
        { }

        /// <summary>
        /// Reads the fmtp's format parameters, which may be null or empty.
        /// </summary>
        /// <exception cref="FormatException">One of them is there but is not a valid value.</exception>
        public static OpusStreamConfigurationData Parse(string formatParameter)
        {
            var parameters = FormatParameters.Parse(formatParameter);
            return new OpusStreamConfigurationData()
            {
                Fmtp = formatParameter,
                SpropStereo = parameters.GetFlag("sprop-stereo"),
                Stereo = parameters.GetFlag("stereo"),
                UseInbandFec = parameters.GetFlag("useinbandfec"),
                UseDtx = parameters.GetFlag("usedtx"),
                SpropMaxCaptureRate = parameters.GetInt("sprop-maxcapturerate", 48000, 8000, 48000),
            };
        }

        public override string ToString()
        {
            return $"SpropStereo: {SpropStereo}, Stereo: {Stereo}, UseInbandFec: {UseInbandFec}, UseDtx: {UseDtx}, SpropMaxCaptureRate: {SpropMaxCaptureRate}";
        }
    }
}
