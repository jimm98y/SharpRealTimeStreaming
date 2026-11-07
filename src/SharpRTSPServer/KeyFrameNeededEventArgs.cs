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
    /// Says that a client is waiting for a picture it can start on.
    /// </summary>
    /// <remarks>
    /// See <see cref="RTSPServer.KeyFrameNeeded"/> for when this is raised and what handling it is
    /// worth.
    /// </remarks>
    public class KeyFrameNeededEventArgs : EventArgs
    {
        public KeyFrameNeededEventArgs(string sessionId)
        {
            SessionId = sessionId;
        }

        /// <summary>
        /// The session that is waiting.
        /// </summary>
        /// <remarks>
        /// A stream is normally encoded once for everybody, so an encoder asked for a keyframe
        /// gives one to every client at once. This says which client was waiting, for a server that
        /// encodes per client or that wants to know how often this happens.
        /// </remarks>
        public string SessionId { get; }
    }
}
