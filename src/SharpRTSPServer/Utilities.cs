using System;

namespace SharpRTSPServer
{
    internal static class Utilities
    {
        public static byte[] FromHexString(string hex)
        {
#if !NETCOREAPP
            byte[] raw = new byte[hex.Length / 2];
            for (int i = 0; i < raw.Length; i++)
            {
                raw[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return raw;
#else
            return Convert.FromHexString(hex);
#endif
        }

        public static string ToHexString(byte[] data)
        {
#if !NETCOREAPP
            string hexString = BitConverter.ToString(data);
            hexString = hexString.Replace("-", "");
            return hexString;
#else
            return Convert.ToHexString(data);
#endif
        }
    }
}
