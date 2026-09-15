using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace SharpRTSPServer
{
    public abstract class TrackBase : ITrack
    {
        /// <summary>
        /// RTP profile.
        /// </summary>
        public RtpProfiles RtpProfile { get; set; } = RtpProfiles.AVP;

        /// <summary>
        /// SSRC for this track. Each track streamed by this server shall have a unique SSRC.
        /// </summary>
        /// <remarks>
        /// Drawn at random from the full 32 bit range, so two tracks colliding - which would break
        /// demultiplexing on the receiver - is not something that happens in practice.
        /// </remarks>
        public uint SSRC { get; set; } = RandomGenerator.NextUInt32();

        public IRtpSender Sink { get; set; } = null;

        public string StreamID { get; set; } = null;

        public abstract string Codec { get; }

        public abstract int ID { get; set; }

        /// <summary>
        /// Payload type. AAC uses a dynamic payload type, which by default we calculate as 96 + track ID.
        /// </summary>
        public abstract int PayloadType { get; set; }

        public abstract bool IsReady { get; }

        public abstract StringBuilder BuildSDP(StringBuilder sdp);

        public abstract (List<Memory<byte>>, List<IMemoryOwner<byte>>) CreateRtpPackets(List<ReadOnlyMemory<byte>> samples, uint rtpTimestamp);


        public virtual void FeedInRawSamples(uint rtpTimestamp, List<ReadOnlyMemory<byte>> samples)
        {
            var sink = Sink;

            // No sink means the track is not attached to a server right now: it has not been added
            // yet, or its stream source was removed while a producer thread was still feeding. Both
            // are ordinary points in the lifecycle and neither is the producer's fault, so the
            // sample is dropped the same way one is when the sink cannot take it.
            if (sink == null)
                return;

            if (!sink.CanAcceptNewSamples(StreamID))
                return;

            if (ID != (int)TrackType.Video && ID != (int)TrackType.Audio)
                throw new ArgumentOutOfRangeException("ID must be 0 for video or 1 for audio");

            (List<Memory<byte>> rtpPackets, List<IMemoryOwner<byte>> memoryOwners) = CreateRtpPackets(samples, rtpTimestamp);

            sink.FeedInRawRTP(StreamID, ID, rtpTimestamp, rtpPackets);

            foreach (var owner in memoryOwners)
            {
                owner.Dispose();
            }
        }
    }
}
