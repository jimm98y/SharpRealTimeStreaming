using System;

namespace SharpRTSPServer
{
    /// <summary>
    /// What a client said about how the media is reaching it.
    /// </summary>
    /// <remarks>
    /// Every one of these is a client's own account of what it received, so it is worth what the
    /// client is worth: a receiver that lies, or that is simply wrong about its own clock, reports
    /// nonsense and there is no way to tell from here. What it is good for is noticing that a stream
    /// has gone bad, which is not something a sender can see on its own - the packets it sent all
    /// left successfully.
    /// </remarks>
    public class ReceptionReportEventArgs : EventArgs
    {
        public ReceptionReportEventArgs(string sessionId, int trackId, uint reporterSsrc,
            double fractionLost, int cumulativeLost, uint extendedHighestSequence, uint jitter,
            TimeSpan? roundTripTime)
        {
            SessionId = sessionId;
            TrackId = trackId;
            ReporterSsrc = reporterSsrc;
            FractionLost = fractionLost;
            CumulativeLost = cumulativeLost;
            ExtendedHighestSequence = extendedHighestSequence;
            Jitter = jitter;
            RoundTripTime = roundTripTime;
        }

        /// <summary>
        /// The session the report came from.
        /// </summary>
        public string SessionId { get; }

        /// <summary>
        /// Which track of that session it is about.
        /// </summary>
        public int TrackId { get; }

        /// <summary>
        /// The SSRC the reporting client goes by.
        /// </summary>
        public uint ReporterSsrc { get; }

        /// <summary>
        /// What share of the packets sent since the client's last report did not reach it, from zero
        /// to one.
        /// </summary>
        public double FractionLost { get; }

        /// <summary>
        /// How many have not reached it since the stream began. Negative where it received
        /// duplicates, which the specification asks for rather than hiding.
        /// </summary>
        public int CumulativeLost { get; }

        /// <summary>
        /// How far through the sequence the client has got, counting wraps.
        /// </summary>
        public uint ExtendedHighestSequence { get; }

        /// <summary>
        /// How unevenly packets are arriving there, in the track's own clock.
        /// </summary>
        public uint Jitter { get; }

        /// <summary>
        /// How long the round trip to that client took, where the report answered one of ours.
        /// </summary>
        /// <remarks>
        /// Null until the client has answered a sender report, since it is worked out from when that
        /// report went out and how long the client held it.
        /// </remarks>
        public TimeSpan? RoundTripTime { get; }
    }
}
