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

using System;
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

        /// <summary>
        /// Default value of <see cref="PacketMTU"/>, chosen to fit inside a 1500 byte Ethernet frame.
        /// </summary>
        public const int DEFAULT_PACKET_MTU = 1400;

        /// <summary>
        /// Maximum size of the packet. If the resulting RTP packet exceeds this size, fragmentation
        /// will be used. Default value is 1400 and RTP over RTSP is constrained to 65535.
        /// </summary>
        /// <remarks>
        /// Here rather than on each track, where four of them declared it identically and a fifth
        /// did not have it at all and fragmented at a fixed size - so setting it was honoured by
        /// some of the codecs and quietly ignored by the others.
        /// </remarks>
        public int PacketMTU { get; set; } = DEFAULT_PACKET_MTU;

        /// <summary>
        /// What is left of <see cref="PacketMTU"/> for payload once the headers below RTP are
        /// accounted for: 20 for IP, 8 for UDP, and 16 rather than the RTP header's 12 so that a
        /// packet carrying a small extension still fits.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// The MTU leaves no room for any payload, which would make fragmentation loop for ever.
        /// </exception>
        protected int PayloadMTU()
        {
            const int belowRtp = 20 + 8;
            const int rtpHeaderAllowance = 16;

            int payloadMTU = PacketMTU - belowRtp - rtpHeaderAllowance;

            if (payloadMTU <= 0)
            {
                throw new InvalidOperationException(
                    $"{nameof(PacketMTU)} of {PacketMTU} is too small to carry any payload, it must leave room for the IP, UDP and RTP headers.");
            }

            return payloadMTU;
        }

        public IRtpSender Sink { get; set; } = null;

        /// <summary>
        /// Where this track reports what it could not do.
        /// </summary>
        /// <remarks>
        /// Set by <see cref="RTSPServer.AddStreamSource"/> to the server's own logger, and kept in
        /// step when that is reassigned - so a track says what it has to say wherever the server it
        /// belongs to does. Null for a track that has not been added to one, which is why every use
        /// of it goes through the null-safe extensions.
        /// </remarks>
        public ILog Logger { get; set; }

        public string StreamID { get; set; } = null;

        public abstract string Codec { get; }

        public abstract int ID { get; set; }

        /// <summary>
        /// What kind of media this track carries. Every track has to say.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This used to fall back to <c>(TrackType)ID</c> - a track's place in its stream read as
        /// the sort of media it carries - which is right only for a stream that is one video track
        /// followed by one audio track. A stream with two video tracks had the second reporting
        /// itself as audio, and the kind is acted on: it decides which frames are thrown away first
        /// when a client falls behind, whether a frame is held back waiting for a keyframe, and
        /// which media section of an overridden SDP a track's control URL and keys belong to. A
        /// silently wrong answer there is worse than no answer.
        /// </para>
        /// <para>
        /// Every track in this library overrides it. One written elsewhere must too.
        /// </para>
        /// </remarks>
        /// <exception cref="NotImplementedException">
        /// The track does not say what kind of media it carries.
        /// </exception>
        public virtual TrackType Kind =>
            throw new NotImplementedException(
                $"{GetType().Name} does not say what kind of media it carries. Override {nameof(Kind)} " +
                $"and return the {nameof(TrackType)} this track is. It used to default to the track's ID " +
                "read as a kind, which is wrong for any stream that is not one video track followed by " +
                "one audio track.");

        /// <summary>
        /// Payload type. AAC uses a dynamic payload type, which by default we calculate as 96 + track ID.
        /// </summary>
        public abstract int PayloadType { get; set; }

        public abstract bool IsReady { get; }

        public abstract StringBuilder BuildSDP(StringBuilder sdp);

        public abstract void CreateRtpPackets(List<ReadOnlyMemory<byte>> samples, uint rtpTimestamp, RtpPackets packets);


        /// <summary>
        /// The samples with anything empty taken out, or the list itself when there is nothing to take.
        /// </summary>
        private static List<ReadOnlyMemory<byte>> WithoutEmptySamples(List<ReadOnlyMemory<byte>> samples)
        {
            if (samples == null)
            {
                return new List<ReadOnlyMemory<byte>>();
            }

            bool anyEmpty = false;
            foreach (ReadOnlyMemory<byte> sample in samples)
            {
                if (sample.Length == 0)
                {
                    anyEmpty = true;
                    break;
                }
            }

            if (!anyEmpty)
            {
                return samples;
            }

            var kept = new List<ReadOnlyMemory<byte>>(samples.Count);
            foreach (ReadOnlyMemory<byte> sample in samples)
            {
                if (sample.Length > 0)
                {
                    kept.Add(sample);
                }
            }

            return kept;
        }

        /// <summary>
        /// The RTP timestamp of the last sample handed to this track, or null if it has had none.
        /// </summary>
        /// <remarks>
        /// Where the track has got to, which is what the RTP-Info of a PLAY reports so that a client
        /// can put the media on a timeline before any RTCP has arrived. Noted as the sample arrives
        /// rather than as it is sent, because a source keeps running while nobody is watching and its
        /// clock keeps advancing - a client that arrives then still needs to be told where it is.
        /// </remarks>
        internal uint? LastRtpTimestamp { get; private set; }

        public virtual void FeedInRawSamples(uint rtpTimestamp, List<ReadOnlyMemory<byte>> samples)
        {
            var sink = Sink;

            // Before any of the reasons this sample might go no further. Every one of them is about
            // whether it is worth sending, and none of them changes where the source has got to.
            LastRtpTimestamp = rtpTimestamp;

            // No sink means the track is not attached to a server right now: it has not been added
            // yet, or its stream source was removed while a producer thread was still feeding. Both
            // are ordinary points in the lifecycle and neither is the producer's fault, so the
            // sample is dropped the same way one is when the sink cannot take it.
            if (sink == null)
                return;

            if (!sink.CanAcceptNewSamples(StreamID))
                return;

            if (ID < 0)
                throw new ArgumentOutOfRangeException(nameof(ID), ID, "A track's ID is its place in the stream, so it cannot be negative.");

            // A sample with nothing in it would go out as an RTP packet with no payload, for the
            // receiver to make sense of. Some tracks dropped these and some did not; now none of them
            // see one. It also puts the marker bit on the last sample that is actually sent, rather
            // than on an empty one that ends the list.
            samples = WithoutEmptySamples(samples);
            if (samples.Count == 0)
                return;

            RtpPackets packets = RtpPackets.Take();

            try
            {
                CreateRtpPackets(samples, rtpTimestamp, packets);
            }
            catch (Exception)
            {
                // Nothing has taken them over yet, so they are still this track's to give back.
                packets.Release();
                throw;
            }

            // Handed over, not lent. The sink releases them once the last client has been sent the
            // frame, which is why it no longer has to copy every packet before letting this return.
            sink.FeedInRawRTP(StreamID, ID, rtpTimestamp, packets);
        }
    }
}
