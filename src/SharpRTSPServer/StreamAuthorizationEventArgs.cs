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

using Rtsp.Messages;
using System;
using System.Net;

namespace SharpRTSPServer
{
    /// <summary>
    /// Asks whether a client that has authenticated may have this particular stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Authentication says who a client is; it says nothing about what it is entitled to. Without
    /// this, the two were the same thing - any client that knew the password reached every stream
    /// the server offered, which is fine for one camera and not for a server carrying the feeds of
    /// several tenants.
    /// </para>
    /// <para>
    /// A handler denies by calling <see cref="Deny"/>. Doing nothing allows, so a server that does
    /// not subscribe behaves exactly as it did before this existed.
    /// </para>
    /// </remarks>
    public class StreamAuthorizationEventArgs : EventArgs
    {
        internal StreamAuthorizationEventArgs(string streamId, RtspRequest request, IPEndPoint remoteEndPoint, string userName)
        {
            StreamID = streamId;
            Request = request;
            RemoteEndPoint = remoteEndPoint;
            UserName = userName;
        }

        /// <summary>
        /// The stream the request is for.
        /// </summary>
        public string StreamID { get; }

        /// <summary>
        /// The request itself, for a handler that wants to distinguish what is being asked - a
        /// DESCRIBE from a PLAY, say - or to read a header of its own.
        /// </summary>
        /// <remarks>
        /// Every field of it came from the client, including the Authorization header, so nothing
        /// here is evidence of anything except that the client sent it. <see cref="UserName"/> is
        /// the one thing that has been checked.
        /// </remarks>
        public RtspRequest Request { get; }

        /// <summary>
        /// Where the request came from.
        /// </summary>
        public IPEndPoint RemoteEndPoint { get; }

        /// <summary>
        /// The user the request authenticated as, or null on a server with no credentials set.
        /// </summary>
        /// <remarks>
        /// This one has been verified - the request carried a correct answer to the challenge for
        /// it - which is what makes it worth deciding on.
        /// </remarks>
        public string UserName { get; }

        /// <summary>
        /// Whether the client may have this stream. True unless a handler says otherwise.
        /// </summary>
        public bool IsAuthorized { get; private set; } = true;

        /// <summary>
        /// The status to answer a denied request with.
        /// </summary>
        public int DeniedStatusCode { get; private set; } = 401;

        /// <summary>
        /// Refuses this client this stream.
        /// </summary>
        /// <param name="statusCode">
        /// What to tell the client. 401 by default, which invites it to authenticate as somebody who
        /// may have the stream; 403 says the answer will not change however it authenticates, and
        /// 404 hides the existence of the stream from a client that has no business knowing about
        /// it. Anything else is sent as given.
        /// </param>
        /// <remarks>
        /// Denying twice is allowed and keeps the first refusal, so one handler cannot soften
        /// another's.
        /// </remarks>
        public void Deny(int statusCode = 401)
        {
            if (IsAuthorized)
            {
                IsAuthorized = false;
                DeniedStatusCode = statusCode;
            }
        }
    }
}
