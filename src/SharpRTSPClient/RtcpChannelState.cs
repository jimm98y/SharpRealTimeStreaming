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
        }
    }
}
