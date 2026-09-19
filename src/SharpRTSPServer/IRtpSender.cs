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
    public interface IRtpSender
    {
        /// <summary>
        /// Hands over one frame's RTP packets, and whatever is holding them.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The sink takes the packets over. It may still be writing them long after this returns -
        /// a frame goes to each client in that client's own time - so the caller must not touch
        /// them again, and must not release them: the sink releases <paramref name="packets"/> once
        /// the last client has finished with the frame, on every path including the ones where it
        /// decides not to send anything and the ones where something throws.
        /// </para>
        /// <para>
        /// This is what lets a frame be sent without being copied. The buffers used to be released
        /// as soon as this call returned, so the sink had to copy every packet into a buffer of its
        /// own before letting the call go - a copy and a pooled buffer per packet, for a frame it
        /// was about to send unchanged.
        /// </para>
        /// </remarks>
        /// <param name="streamID">Which stream the frame belongs to.</param>
        /// <param name="streamType">Which track of it, by the track's ID.</param>
        /// <param name="rtpTimestamp">RTP timestamp in the timescale of the track.</param>
        /// <param name="packets">The packets, already built, and the memory holding them.</param>
        void FeedInRawRTP(string streamID, int streamType, uint rtpTimestamp, RtpPackets packets);

        bool CanAcceptNewSamples(string streamID);
    }
}
