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
