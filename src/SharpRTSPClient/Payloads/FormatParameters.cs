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
using System.Collections.Generic;
using System.Globalization;

namespace SharpRTSPClient
{
    /// <summary>
    /// The name=value pairs of an fmtp line, as the codec configurations read them.
    /// </summary>
    internal sealed class FormatParameters
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private FormatParameters()
        { }

        /// <summary>
        /// Splits the format parameters, which may be null or empty. A name given twice keeps the
        /// last value; a part without '=' is skipped.
        /// </summary>
        public static FormatParameters Parse(string formatParameter)
        {
            var parameters = new FormatParameters();

            if (string.IsNullOrWhiteSpace(formatParameter))
            {
                return parameters;
            }

            foreach (string parameter in formatParameter.Split(';'))
            {
                int separator = parameter.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                parameters._values[parameter.Substring(0, separator).Trim()] = parameter.Substring(separator + 1).Trim();
            }

            return parameters;
        }

        /// <summary>
        /// The parameter as it was written, or null where it is not there at all.
        /// </summary>
        public string GetString(string name)
        {
            return _values.TryGetValue(name, out string value) ? value : null;
        }

        /// <summary>
        /// The parameter as a whole number, or the given default where it is not there at all.
        /// </summary>
        /// <exception cref="FormatException">The parameter is there but is not a number from min to max.</exception>
        public int GetInt(string name, int defaultValue, int min, int max)
        {
            if (!_values.TryGetValue(name, out string value))
            {
                return defaultValue;
            }

            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int result) || result < min || result > max)
            {
                throw new FormatException($"'{value}' is not a valid {name}.");
            }

            return result;
        }

        /// <summary>
        /// A 0 or 1 parameter, false where it is not there at all.
        /// </summary>
        /// <exception cref="FormatException">The parameter is there but is neither 0 nor 1.</exception>
        public bool GetFlag(string name)
        {
            return GetInt(name, 0, 0, 1) == 1;
        }
    }
}
