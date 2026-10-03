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
    /// <summary>
    /// The configuration of a stream whose fmtp is there but could not be read.
    /// </summary>
    /// <remarks>
    /// Reported in place of the codec's own configuration, which would otherwise claim the defaults
    /// of the payload format for parameters the SDP did give - just not in a form this could read.
    /// Nothing is parsed out of it, the parameter sets included; they still arrive in the stream.
    /// </remarks>
    public class UnparsedStreamConfigurationData : IStreamConfigurationData
    {
        /// <summary>
        /// The fmtp's format parameters as the SDP wrote them, without the "a=fmtp:&lt;payload type&gt; "
        /// in front.
        /// </summary>
        public string Fmtp { get; set; }

        public UnparsedStreamConfigurationData()
        { }

        public UnparsedStreamConfigurationData(string fmtp)
        {
            Fmtp = fmtp;
        }

        public override string ToString()
        {
            return $"Unparsed fmtp: {Fmtp}";
        }
    }
}
