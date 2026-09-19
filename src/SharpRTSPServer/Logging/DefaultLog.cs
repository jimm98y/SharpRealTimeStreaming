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

namespace SharpRTSPServer
{
    /// <summary>
    /// The logger servers use when they are not given one: it writes to the debug output.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One of these per server, not one for the process, so switching trace on for a noisy stream
    /// does not switch it on for everything else in the program.
    /// </para>
    /// <para>
    /// Trace is off while the others are on, and deliberately. It is the level the per-packet lines
    /// are written at, and formatting those costs more than sending the media does - measured, it
    /// was the whole of what the server still allocated while streaming. Turn it on to follow packets,
    /// not to run a server.
    /// </para>
    /// </remarks>
    public sealed class DefaultLog : ILog
    {
        /// <summary>Whether anything is written at all.</summary>
        public bool IsLoggingEnabled { get; set; } = true;

        public bool IsErrorEnabled { get; set; } = true;
        public bool IsWarningEnabled { get; set; } = true;
        public bool IsInfoEnabled { get; set; } = true;
        public bool IsDebugEnabled { get; set; } = true;

        /// <summary>See the remarks on <see cref="DefaultLog"/> for why this one is off.</summary>
        public bool IsTraceEnabled { get; set; } = false;

        /// <summary>
        /// Where the lines go. Replaceable, so that a host which wants them somewhere other than the
        /// debug output does not have to implement <see cref="ILog"/> to say so.
        /// </summary>
        public Action<string> Sink { get; set; } = message => System.Diagnostics.Debug.WriteLine(message);

        public void LogError(string error) { Write(IsErrorEnabled, error); }

        public void LogWarning(string warning) { Write(IsWarningEnabled, warning); }

        public void LogInfo(string info) { Write(IsInfoEnabled, info); }

        public void LogDebug(string debug) { Write(IsDebugEnabled, debug); }

        public void LogTrace(string trace) { Write(IsTraceEnabled, trace); }

        private void Write(bool levelEnabled, string message)
        {
            if (!IsLoggingEnabled || !levelEnabled)
            {
                return;
            }

            Action<string> sink = Sink;

            if (sink != null)
            {
                sink(message);
            }
        }
    }
}
