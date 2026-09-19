// SharpRTSPClient
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

namespace SharpRTSPClient
{
    /// <summary>
    /// What one track has actually received, as a receiver report describes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A receiver report says four things about how the media arrived: what fraction of it went
    /// missing since the last report, how much has gone missing altogether, how far through the
    /// sequence the receiver has got, and how unevenly the packets are turning up. A report without
    /// them is a report saying nothing, which is what this client used to send.
    /// </para>
    /// <para>
    /// Everything here is kept the way RFC 3550 Appendix A keeps it, because the definitions are not
    /// what one would guess: packets received counts duplicates and packets that arrived out of
    /// order, so the count of lost packets can go down, and the specification says to let it.
    /// </para>
    /// <see href="https://datatracker.ietf.org/doc/html/rfc3550#section-6.4.1" />
    /// </remarks>
    internal sealed class ReceptionStatistics
    {
        /// <summary>
        /// Half the sequence space. A step of less than this is the stream moving on; more, and it
        /// is a packet from behind.
        /// </summary>
        private const uint SEQUENCE_FORWARD = 0x8000;

        private const uint SEQUENCE_SPACE = 0x10000;

        /// <summary>
        /// How heavily a new measurement moves the jitter, as RFC 3550 sets it.
        /// </summary>
        private const double JITTER_GAIN = 1.0 / 16.0;

        private readonly object _gate = new object();

        private bool _started;
        private uint _baseSequence;
        private uint _highestSequence;
        private uint _cycles;
        private uint _received;
        private uint _expectedWhenLastReported;
        private uint _receivedWhenLastReported;
        private double _jitter;
        private int _lastTransit;
        private bool _hasTransit;

        private uint _lastSenderReportMiddle32;
        private long _lastSenderReportArrivedTicks;

        /// <summary>
        /// Notes a packet as it arrives.
        /// </summary>
        /// <param name="sequenceNumber">The packet's sequence number.</param>
        /// <param name="rtpTimestamp">The timestamp it carries.</param>
        /// <param name="clockRate">Ticks of this track's RTP clock per second, from the description.</param>
        public void RecordPacket(ushort sequenceNumber, uint rtpTimestamp, int clockRate)
        {
            lock (_gate)
            {
                if (!_started)
                {
                    _started = true;
                    _baseSequence = sequenceNumber;
                    _highestSequence = sequenceNumber;
                    _received = 1;
                    return;
                }

                _received++;

                // Where this packet sits relative to the furthest the stream has got. A small step
                // forward is the ordinary case; a large one read as a step back is the sequence
                // number wrapping, which happens every sixty five thousand packets.
                uint step = (uint)((sequenceNumber - _highestSequence) & 0xFFFF);

                if (step < SEQUENCE_FORWARD)
                {
                    if (sequenceNumber < _highestSequence)
                    {
                        _cycles += SEQUENCE_SPACE;
                    }

                    _highestSequence = sequenceNumber;
                }

                // Unevenness, measured as the difference between how far apart two packets were sent
                // and how far apart they arrived. Both in this track's own clock, since that is what
                // the report is expressed in.
                if (clockRate > 0)
                {
                    int arrival = unchecked((int)(long)(DateTime.UtcNow.Ticks / (TimeSpan.TicksPerSecond / (double)clockRate)));
                    int transit = arrival - unchecked((int)rtpTimestamp);

                    if (_hasTransit)
                    {
                        int drift = transit - _lastTransit;

                        if (drift < 0)
                        {
                            drift = -drift;
                        }

                        _jitter += (drift - _jitter) * JITTER_GAIN;
                    }

                    _lastTransit = transit;
                    _hasTransit = true;
                }
            }
        }

        /// <summary>
        /// Notes when a sender report arrived, and the part of its timestamp a report echoes back.
        /// </summary>
        /// <remarks>
        /// The sender works out the round trip from these: it knows when it sent the report, this
        /// says which report is being answered and how long the answer sat here before being sent.
        /// </remarks>
        public void RecordSenderReportArrival(uint middle32OfNtp)
        {
            lock (_gate)
            {
                _lastSenderReportMiddle32 = middle32OfNtp;
                _lastSenderReportArrivedTicks = DateTime.UtcNow.Ticks;
            }
        }

        /// <summary>
        /// What to put in a report block now, and notes that this is where the next one starts from.
        /// </summary>
        public ReceptionReport TakeReport()
        {
            lock (_gate)
            {
                if (!_started)
                {
                    return new ReceptionReport();
                }

                uint extendedHighest = _cycles + _highestSequence;
                uint expected = extendedHighest - _baseSequence + 1;

                // Signed, and deliberately: a receiver that has taken in duplicates has received more
                // than were sent, and the specification says to report that rather than clamp it.
                int lost = (int)expected - (int)_received;

                uint expectedSince = expected - _expectedWhenLastReported;
                uint receivedSince = _received - _receivedWhenLastReported;

                _expectedWhenLastReported = expected;
                _receivedWhenLastReported = _received;

                int lostSince = (int)expectedSince - (int)receivedSince;

                byte fraction = 0;

                if (expectedSince > 0 && lostSince > 0)
                {
                    fraction = (byte)Math.Min(255, (lostSince << 8) / (int)expectedSince);
                }

                uint delay = 0;

                if (_lastSenderReportArrivedTicks != 0)
                {
                    // in units of a sixty five thousandth of a second, which is what the field holds
                    double seconds = (DateTime.UtcNow.Ticks - _lastSenderReportArrivedTicks) / (double)TimeSpan.TicksPerSecond;
                    delay = (uint)Math.Min(uint.MaxValue, seconds * 65536.0);
                }

                return new ReceptionReport
                {
                    HasData = true,
                    FractionLost = fraction,
                    CumulativeLost = lost,
                    ExtendedHighestSequence = extendedHighest,
                    Jitter = (uint)_jitter,
                    LastSenderReport = _lastSenderReportMiddle32,
                    DelaySinceLastSenderReport = _lastSenderReportArrivedTicks == 0 ? 0 : delay,
                };
            }
        }

        /// <summary>
        /// Starts again, for a reconnect - a new stream numbers its packets from wherever it likes.
        /// </summary>
        public void Reset()
        {
            lock (_gate)
            {
                _started = false;
                _cycles = 0;
                _received = 0;
                _expectedWhenLastReported = 0;
                _receivedWhenLastReported = 0;
                _jitter = 0;
                _hasTransit = false;
                _lastSenderReportMiddle32 = 0;
                _lastSenderReportArrivedTicks = 0;
            }
        }
    }

    /// <summary>
    /// One report block's worth of what a track has received.
    /// </summary>
    internal struct ReceptionReport
    {
        /// <summary>Whether anything has arrived to report on.</summary>
        public bool HasData { get; set; }

        /// <summary>What share of the packets expected since the last report did not arrive, out of 256.</summary>
        public byte FractionLost { get; set; }

        /// <summary>How many have not arrived since the stream began. Negative where duplicates arrived.</summary>
        public int CumulativeLost { get; set; }

        /// <summary>How far through the sequence this receiver has got, counting wraps.</summary>
        public uint ExtendedHighestSequence { get; set; }

        /// <summary>How unevenly packets are arriving, in this track's own clock.</summary>
        public uint Jitter { get; set; }

        /// <summary>The middle of the timestamp of the last sender report, so the sender knows which.</summary>
        public uint LastSenderReport { get; set; }

        /// <summary>How long that report sat here before this answer went out.</summary>
        public uint DelaySinceLastSenderReport { get; set; }
    }
}
