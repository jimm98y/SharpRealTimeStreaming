using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SharpRTSPServer
{
    public abstract class TrackBase : ITrack
    {
        public IRtpSender Sink { get; set; } = null;

        public abstract string Codec { get; }

        public abstract int ID { get; set; }

        /// <summary>
        /// Payload type. AAC uses a dynamic payload type, which by default we calculate as 96 + track ID.
        /// </summary>
        public abstract int PayloadType { get; set; }

        public abstract bool IsReady { get; }

        public abstract StringBuilder BuildSDP(StringBuilder sdp);

        public abstract IByteBuffer CreateRtpPackets(ReadOnlySequence<byte> samples, uint rtpTimestamp);

        public virtual void FeedInRawSamples(uint rtpTimestamp, ReadOnlySequence<byte> samples)
        {
            if (Sink == null)
                throw new InvalidOperationException("Sink is null!!!");

            if (!Sink.CanAcceptNewSamples())
                return;

            if (ID != (int)TrackType.Video && ID != (int)TrackType.Audio)
                throw new ArgumentOutOfRangeException("ID must be 0 for video or 1 for audio");

            using (var rtpPackets = CreateRtpPackets(samples, rtpTimestamp))
            {
                Sink.FeedInRawRTP(ID, rtpTimestamp, rtpPackets.GetReadOnlySequence());
            }
        }
    }
}
