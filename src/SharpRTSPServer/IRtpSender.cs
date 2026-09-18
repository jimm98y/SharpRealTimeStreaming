using System;
using System.Buffers;
using System.Collections.Generic;

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
