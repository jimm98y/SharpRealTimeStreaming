using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace SharpRTSPServer
{
    public enum RtpProfiles
    {
        AVP,
        // AVPF,
        SAVP,
        // SAVPF
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
        /// Track ID. Used to identify the track in the SDP, and its place in the stream it belongs to.
        /// </summary>
        int ID { get; set; }

        /// <summary>
        /// What kind of media this track carries.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="ID"/>, which says where the track sits in its stream. A stream
        /// can carry two audio tracks, and then one of them has an ID that no longer matches the
        /// kind - which is why the two are not the same thing.
        /// </remarks>
        TrackType Kind { get; }

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
        (List<Memory<byte>>, List<IMemoryOwner<byte>>) CreateRtpPackets(List<ReadOnlyMemory<byte>> samples, uint rtpTimestamp);

        void FeedInRawSamples(uint rtpTimestamp, List<ReadOnlyMemory<byte>> samples);

    }
}
