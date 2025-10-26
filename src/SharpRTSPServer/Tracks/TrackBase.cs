﻿using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace SharpRTSPServer
{
    public abstract class TrackBase : ITrack
    {
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

        public abstract (List<Memory<byte>>, List<IMemoryOwner<byte>>) CreateRtpPackets(List<byte[]> samples, uint rtpTimestamp);

        public virtual void FeedInRawSamples(uint rtpTimestamp, List<byte[]> samples)
        {
            if (Sink == null)
                throw new InvalidOperationException("Sink is null!!!");

            if (!Sink.CanAcceptNewSamples(StreamID))
                return;

            if (ID != (int)TrackType.Video && ID != (int)TrackType.Audio)
                throw new ArgumentOutOfRangeException("ID must be 0 for video or 1 for audio");

            (List<Memory<byte>> rtpPackets, List<IMemoryOwner<byte>> memoryOwners) = CreateRtpPackets(samples, rtpTimestamp);

            Sink.FeedInRawRTP(StreamID, ID, rtpTimestamp, rtpPackets);

            foreach (var owner in memoryOwners)
            {
                owner.Dispose();
            }
        }
    }
}
