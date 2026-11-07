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
    public class ProxyTrack : TrackBase, IDisposable
    {
        public override string Codec => "PROXY";

        public override int ID { get; set; }
        public override int PayloadType { get; set; }

        /// <summary>
        /// What kind of media this track forwards, as it was constructed with.
        /// </summary>
        public override TrackType Kind { get; }

        private bool _isReady = false;
        private bool _disposedValue;

        public override bool IsReady
        { 
            get 
            {
                return _isReady;
            } 
        }

        public Uri Uri { get; }

        /// <summary>
        /// Forward the RTP exactly as fed, keeping the SSRC and sequence numbers the source put in it
        /// instead of letting the server write its own. Off by default.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The server normally stamps every outgoing packet with the stream's SSRC and its own
        /// sequence counter, which is right when it is the origin of the media. For a track that
        /// forwards someone else's RTP it is not: renumbering closes the gaps a lossy source left, so
        /// a receiver can no longer see what was lost, and reordered packets come out carrying
        /// in-order sequence numbers. Turn this on to hand the packets over untouched.
        /// </para>
        /// <para>
        /// The source's SSRC is picked up from the first packet forwarded and published as
        /// <see cref="TrackBase.SSRC"/>, so the SETUP reply and the RTCP the server sends for this
        /// track agree with the RTP. Set it before the first client sets up, otherwise that client is
        /// told the placeholder SSRC this track was constructed with.
        /// </para>
        /// </remarks>
        public bool PreserveSourceHeaders { get; set; } = false;

        /// <summary>
        /// True once <see cref="PreserveSourceHeaders"/> has taken the SSRC off a forwarded packet.
        /// </summary>
        public bool HasLearnedSourceSsrc { get; private set; }

        public ProxyTrack(TrackType type)
        {
            this.ID = (int)type;
            this.Kind = type;
        }

        /// <summary>
        /// Takes the SSRC out of an RTP packet the source handed us, so the server announces and
        /// reports the same SSRC the packets carry.
        /// </summary>
        internal void LearnSourceSsrc(ReadOnlySpan<byte> rtpPacket)
        {
            if (!PreserveSourceHeaders || rtpPacket.Length < RTP_HEADER_LENGTH)
                return;

            uint ssrc = RTPPacketUtil.ReadSSRC(rtpPacket);

            if (HasLearnedSourceSsrc && SSRC == ssrc)
                return;

            SSRC = ssrc;
            HasLearnedSourceSsrc = true;
        }

        /// <summary>
        /// Smallest RTP packet that still has a full header, and so an SSRC to read.
        /// </summary>
        private const int RTP_HEADER_LENGTH = 12;

        /// <summary>
        /// Not something a forwarding track can do.
        /// </summary>
        /// <remarks>
        /// An SDP says what the media is - which codec, at what rate, with which parameters - and a
        /// track that forwards someone else's RTP knows none of that. It has the packets and nothing
        /// describing them. The description belongs to whatever produced the stream, so it is given
        /// to the stream source with RTSPStreamSource.OverrideSDP rather than made up here.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Always.</exception>
        public override StringBuilder BuildSDP(StringBuilder sdp)
        {
            throw new InvalidOperationException(
                $"A {nameof(ProxyTrack)} forwards RTP that something else described, so it cannot build an SDP. " +
                $"Pass the description the source gave you to {nameof(RTSPStreamSource)}.OverrideSDP.");
        }

        public void Start()
        {
            _isReady = true;
        }

        /// <summary>
        /// Passes already packetized RTP through unchanged. Every sample is forwarded - the caller may
        /// hand in more than one, and dropping the rest would silently lose media.
        /// </summary>
        /// <param name="samples">The samples to packetise.</param>
        /// <param name="rtpTimestamp">RTP timestamp in the timescale of the track.</param>
        /// <param name="packets">Where to build them, and what holds them afterwards.</param>
        public override void CreateRtpPackets(List<ReadOnlyMemory<byte>> samples, uint rtpTimestamp, RtpPackets packets)
        {
            if (samples == null)
                throw new ArgumentNullException(nameof(samples));


            foreach (var sample in samples)
            {
                if (sample.Length == 0)
                    continue;

                // the SSRC has to be known before the packet goes out, so the RTCP the server sends
                // alongside it names the same source
                LearnSourceSsrc(sample.Span);

                Memory<byte> rtpPacket = packets.Rent(sample.Length);
                sample.Span.CopyTo(rtpPacket.Span);
            }

        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if(disposing)
                {
                    _isReady = false;
                }

                _disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}