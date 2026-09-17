using System;
using System.Threading;

namespace SharpRTSPClient
{
    /// <summary>
    /// RTCP state kept for one media channel (video or audio).
    /// </summary>
    /// <remarks>
    /// Updated from the RTP receive thread and read from the RTCP receive thread, so the SSRC is
    /// held as a <see cref="long"/> and accessed through <see cref="Interlocked"/>.
    /// </remarks>
    internal sealed class RtcpChannelState
    {
        private const long Unknown = -1;

        private long _remoteSsrc = Unknown;

        /// <summary>
        /// SSRC of the server we are actually receiving media from, or null until the first RTP packet
        /// has arrived. RTCP that does not belong to this SSRC is not ours to act on.
        /// </summary>
        public uint? RemoteSsrc
        {
            get
            {
                long remoteSsrc = Interlocked.Read(ref _remoteSsrc);
                return remoteSsrc == Unknown ? (uint?)null : (uint)remoteSsrc;
            }
        }

        /// <summary>
        /// Records the SSRC of the source we are streaming from. Only the first one is kept - a later
        /// packet claiming a different SSRC must not be able to take the channel over.
        /// </summary>
        /// <param name="ssrc">SSRC taken from a received RTP packet.</param>
        public void LearnRemoteSsrc(uint ssrc)
        {
            Interlocked.CompareExchange(ref _remoteSsrc, ssrc, Unknown);
        }

        private long _lastReceiverReportTicks;

        private readonly object _syncGate = new object();
        private bool _hasSync;
        private DateTime _syncSenderTime;
        private uint _syncRtpTimestamp;

        /// <summary>
        /// Ticks of this stream's RTP clock per second, from the SDP. 90000 for video, the sampling
        /// rate for audio. Without it a sender report cannot be turned into a span of time.
        /// </summary>
        public int ClockRate { get; set; }

        /// <summary>
        /// Whether a sender report has arrived, so frames on this stream can be placed on the
        /// sender's clock.
        /// </summary>
        public bool HasSenderSync
        {
            get { lock (_syncGate) { return _hasSync; } }
        }

        /// <summary>
        /// Records the pairing a sender report carries: a wall clock time, and the RTP timestamp of
        /// that same instant on this stream.
        /// </summary>
        /// <remarks>
        /// This pairing is the only thing that makes two streams comparable. Each has its own clock,
        /// running at its own rate from a starting point the sender picked at random, so their RTP
        /// timestamps say nothing about each other. Both reports come from one sender and one clock,
        /// which is what makes the streams line up - the sender's clock does not have to agree with
        /// ours, or with anything else, for that to hold.
        /// </remarks>
        public void RecordSenderReport(DateTime senderTimeUtc, uint rtpTimestamp)
        {
            lock (_syncGate)
            {
                _hasSync = true;
                _syncSenderTime = senderTimeUtc;
                _syncRtpTimestamp = rtpTimestamp;
            }
        }

        /// <summary>
        /// Places one packet's RTP timestamp on the sender's clock, using the most recent report.
        /// </summary>
        /// <returns>False until a report has arrived, or if the SDP gave no clock rate.</returns>
        public bool TryMapToSenderClock(uint rtpTimestamp, out DateTime senderTimeUtc)
        {
            int clockRate = ClockRate;

            lock (_syncGate)
            {
                if (!_hasSync || clockRate <= 0)
                {
                    senderTimeUtc = default(DateTime);
                    return false;
                }

                // Subtracted as a signed 32 bit difference, so it reads correctly either side of the
                // point where a 32 bit RTP clock wraps - about every thirteen hours at 90 kHz.
                int elapsed = unchecked((int)(rtpTimestamp - _syncRtpTimestamp));

                senderTimeUtc = _syncSenderTime.AddSeconds(elapsed / (double)clockRate);
                return true;
            }
        }


        /// <summary>
        /// Whether a receiver report is due on this channel, and records that one is being sent.
        /// </summary>
        /// <remarks>
        /// A report used to go back for every sender report that arrived, so the client answered at
        /// whatever rate the far end chose to report at. RFC 3550 has RTCP at a few per cent of what
        /// a session carries, from both ends.
        /// </remarks>
        /// <param name="interval">How long to leave between reports. Zero or less sends one each time.</param>
        public bool ClaimReceiverReportSlot(TimeSpan interval)
        {
            if (interval <= TimeSpan.Zero)
            {
                return true;
            }

            long now = DateTime.UtcNow.Ticks;
            long last = Interlocked.Read(ref _lastReceiverReportTicks);

            if (last != 0 && now - last < interval.Ticks)
            {
                return false;
            }

            // whoever gets the slot sends the report, so two arriving at once do not both answer
            return Interlocked.CompareExchange(ref _lastReceiverReportTicks, now, last) == last;
        }

        /// <summary>
        /// Forgets the learned SSRC, so the channel can be used again after a reconnect.
        /// </summary>
        public void Reset()
        {
            Interlocked.Exchange(ref _remoteSsrc, Unknown);
            Interlocked.Exchange(ref _lastReceiverReportTicks, 0);

            lock (_syncGate)
            {
                // a reconnect is a new stream, whose clock starts wherever the sender decides
                _hasSync = false;
            }
        }
    }
}
