using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace SharpRTSPServer
{
    public enum RtpProfiles
    {
        AVP,
        SAVP
    }

    public interface ITrack
    {
        RtpProfiles RtpProfile { get; set; }

        uint SSRC { get; set; }

        IRtpSender Sink { get; set; }

        string StreamID { get; set; }

        /// <summary>
        /// Codec name.
        /// </summary>
        string Codec { get; }

        /// <summary>
        /// Track ID. Used to identify the track in the SDP.
        /// </summary>
        int ID { get; set; }

        /// <summary>
        /// Payload type.
        /// </summary>
        int PayloadType { get; set; }

        /// <summary>
        /// Is the track ready?
        /// </summary>
        bool IsReady { get; }

        /// <summary>
        /// Build the SDP for this track.
        /// </summary>
        /// <param name="sdp">SDP <see cref="StringBuilder"/>.</param>
        /// <returns><see cref="StringBuilder"/>.</returns>
        StringBuilder BuildSDP(StringBuilder sdp);

        /// <summary>
        /// Creates RTP packets.
        /// </summary>
        /// <param name="samples">An array of samples.</param>
        /// <param name="rtpTimestamp">RTP timestamp in the timescale of the track.</param>
        /// <returns>RTP packets.</returns>
        (List<Memory<byte>>, List<IMemoryOwner<byte>>) CreateRtpPackets(List<byte[]> samples, uint rtpTimestamp);

        void FeedInRawSamples(uint rtpTimestamp, List<byte[]> samples);
    }
}
