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

namespace SharpRTSPServer
{
    /// <summary>
    /// Where the server says what it is doing and what it could not do.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The levels are asked before they are written, so that composing a message nobody will read
    /// costs nothing - which matters here, because the busiest of them is written once per packet.
    /// </para>
    /// <para>
    /// A logger belongs to the object doing the work rather than to the process, so two servers
    /// in one program can report to different places, or one of them to nowhere. That is the point
    /// of it: logging used to be a static class, so turning trace on for one turned it on for
    /// every one of them and they all wrote to the same sink.
    /// </para>
    /// </remarks>
    public interface ILog
    {
        void LogError(string error);
        void LogWarning(string warning);
        void LogInfo(string info);
        void LogDebug(string debug);
        void LogTrace(string trace);

        bool IsErrorEnabled { get; set; }
        bool IsWarningEnabled { get; set; }
        bool IsInfoEnabled { get; set; }
        bool IsDebugEnabled { get; set; }
        bool IsTraceEnabled { get; set; }
    }
}
