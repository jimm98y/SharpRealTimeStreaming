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
using System.Globalization;

namespace SharpRTSPServer
{
    /// <summary>
    /// A stretch of a stream, as the Range header of RTSP writes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Normal play time: where something is measured from the beginning of the media rather than
    /// from any clock. "npt=10-" is from ten seconds in to the end, "npt=10-20" is those ten
    /// seconds, and "npt=now-" is live - from wherever the stream has got to.
    /// </para>
    /// <para>
    /// Written either as plain seconds or as hours, minutes and seconds, and a client may send
    /// either, so both are read. What is sent back is always seconds, which every client reads.
    /// </para>
    /// <see href="https://datatracker.ietf.org/doc/html/rfc2326#section-3.6" />
    /// </remarks>
    public struct NptRange
    {
        /// <summary>
        /// Where to start, or null for "now" - the live edge, whatever the stream has reached.
        /// </summary>
        public TimeSpan? Start { get; set; }

        /// <summary>
        /// Where to stop, or null to play to the end.
        /// </summary>
        public TimeSpan? End { get; set; }

        /// <summary>
        /// Whether the client asked for the live edge rather than a point in the media.
        /// </summary>
        public bool IsNow => !Start.HasValue;

        /// <summary>
        /// Reads a Range header, and says whether it was one this server understands.
        /// </summary>
        /// <remarks>
        /// Only normal play time. The other units RTSP allows - a wall clock time, or a frame number
        /// on tape - mean something only for media that has them, and answering a request in a unit
        /// that was not understood with a guess is worse than saying no.
        /// </remarks>
        public static bool TryParse(string header, out NptRange range)
        {
            range = default(NptRange);

            if (string.IsNullOrWhiteSpace(header))
            {
                return false;
            }

            // A header may offer several ranges in different units; the npt one is the one read.
            foreach (string part in header.Split(','))
            {
                string candidate = part.Trim();

                if (!candidate.StartsWith("npt=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return TryParseNpt(candidate.Substring("npt=".Length).Trim(), out range);
            }

            return false;
        }

        private static bool TryParseNpt(string value, out NptRange range)
        {
            range = default(NptRange);

            int dash = value.IndexOf('-');

            if (dash < 0)
            {
                // A single point with no dash is not a range. RTSP writes an open ended one as
                // "10-", so the dash is what says where the start ends.
                return false;
            }

            string start = value.Substring(0, dash).Trim();
            string end = value.Substring(dash + 1).Trim();

            if (start.Length == 0)
            {
                // "-20" is the last twenty seconds in some formats and nothing in RTSP
                return false;
            }

            if (string.Equals(start, "now", StringComparison.OrdinalIgnoreCase))
            {
                range.Start = null;
            }
            else if (TryParseTime(start, out TimeSpan startTime))
            {
                range.Start = startTime;
            }
            else
            {
                return false;
            }

            if (end.Length > 0)
            {
                if (!TryParseTime(end, out TimeSpan endTime))
                {
                    return false;
                }

                range.End = endTime;
            }

            // A range that ends before it starts is not a stretch of anything.
            if (range.Start.HasValue && range.End.HasValue && range.End.Value < range.Start.Value)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// The longest position this will read, in seconds. A TimeSpan cannot hold more, and the
        /// header is client supplied - so a value past this is refused rather than allowed to
        /// overflow, which threw out of a method whose whole contract is to say yes or no.
        /// </summary>
        private const double MOST_SECONDS = 365.0 * 24 * 3600 * 1000;

        /// <summary>
        /// Reads either of the two ways normal play time is written.
        /// </summary>
        private static bool TryParseTime(string value, out TimeSpan time)
        {
            time = TimeSpan.Zero;

            if (value.IndexOf(':') < 0)
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                    || seconds < 0
                    || double.IsNaN(seconds)
                    || double.IsInfinity(seconds)
                    || seconds > MOST_SECONDS)
                {
                    return false;
                }

                time = TimeSpan.FromSeconds(seconds);
                return true;
            }

            string[] fields = value.Split(':');

            if (fields.Length != 3)
            {
                return false;
            }

            if (!int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hours)
                || !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes)
                || !double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds2))
            {
                return false;
            }

            if (hours < 0 || minutes < 0 || minutes > 59 || seconds2 < 0 || seconds2 >= 60
                || hours > MOST_SECONDS / 3600)
            {
                return false;
            }

            time = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds2);
            return true;
        }

        /// <summary>
        /// Writes a range the way a reply reports what is actually being played.
        /// </summary>
        public static string Format(TimeSpan start, TimeSpan? end)
        {
            string from = start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

            return end.HasValue
                ? $"npt={from}-{end.Value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}"
                : $"npt={from}-";
        }

        /// <summary>
        /// Writes the live range, for a stream with no beginning to go back to.
        /// </summary>
        public static string FormatLive() => "npt=now-";
    }
}
