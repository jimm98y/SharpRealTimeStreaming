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
