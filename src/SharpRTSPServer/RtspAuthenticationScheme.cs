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
    /// The scheme clients are challenged with in the WWW-Authenticate header.
    /// </summary>
    public enum RtspAuthenticationScheme
    {
        /// <summary>
        /// Digest access authentication. The client proves it knows the password without sending it,
        /// so the password is not exposed even on a plaintext connection. This is the default.
        /// </summary>
        Digest = 0,

        /// <summary>
        /// Basic access authentication. The user name and password are sent base64 encoded, which is
        /// reversible by anyone who can read the traffic, so this is only safe over RTSPS.
        /// </summary>
        /// <remarks>
        /// Off by default. Turn it on only for clients and hardware decoders that cannot do Digest,
        /// and prefer to pair it with a TLS certificate so the credentials are not sent in the clear.
        /// </remarks>
        Basic = 1,
    }
}
