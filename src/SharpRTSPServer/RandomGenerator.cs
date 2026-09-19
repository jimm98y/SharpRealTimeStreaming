// SharpRTSPServer
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
using System.Security.Cryptography;

namespace SharpRTSPServer
{
    /// <summary>
    /// Source of the random values the server must not let a client predict: digest nonces, session
    /// IDs and RTP SSRCs.
    /// </summary>
    /// <remarks>
    /// <see cref="RandomNumberGenerator"/> is safe to use from several threads, unlike
    /// <see cref="Random"/>, which tracks can otherwise race while being constructed.
    /// </remarks>
    internal static class RandomGenerator
    {
        private static readonly RandomNumberGenerator _rng = RandomNumberGenerator.Create();

        /// <summary>
        /// Returns a random 32 bit value, using the whole range.
        /// </summary>
        public static uint NextUInt32()
        {
            byte[] raw = new byte[sizeof(uint)];
            _rng.GetBytes(raw);
            return BitConverter.ToUInt32(raw, 0);
        }

        /// <summary>
        /// Returns <paramref name="byteCount"/> random bytes as an upper case hex string.
        /// </summary>
        public static string NextHexToken(int byteCount)
        {
            byte[] raw = new byte[byteCount];
            _rng.GetBytes(raw);
            return Utilities.ToHexString(raw);
        }
    }
}
