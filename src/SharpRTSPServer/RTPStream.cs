using Rtsp;
using SharpSRTP.SRTP;
using System;

namespace SharpRTSPServer
{
    /// <summary>
    /// An RTPStream can be a Video Stream, Audio Stream or a Metadata Stream.
    /// </summary>
    public class RTPStream
    {
        public SrtpSessionContext Context { get; set; } = null;

        private byte[] _masterKeySalt;

        private byte[] _mki;

        private string _cryptoSuite;

        /// <summary>
        /// Derives this stream's SRTP keys, or hands back the ones already derived.
        /// </summary>
        /// <remarks>
        /// Once per stream, not once per call. Clients do ask for the SDP more than once, and a
        /// second derivation would replace the context of a stream that is already playing - leaving
        /// the receiver holding keys that no longer decrypt anything, with nothing to say so.
        /// </remarks>
        public byte[] PrepareSrtpContext(string cryptoSuite, int mkiLen = 0)
        {
            if (string.IsNullOrEmpty(cryptoSuite))
                throw new ArgumentNullException("SRTP Crypto suite not selected!");

            if (Context != null && _masterKeySalt != null)
            {
                return (byte[])_masterKeySalt.Clone();
            }

            // derive the master key + master salt to be sent in SDP crypto: attribute as per RFC 4568
            byte[] MKI = SrtpProtocol.GenerateMki(mkiLen);

            SrtpKeys keys = SrtpProtocol.CreateMasterKeys(cryptoSuite, MKI);
            Context = SrtpProtocol.CreateSrtpSessionContext(keys);

            _masterKeySalt = keys.MasterKeySalt.ToArray();
            _mki = MKI;
            _cryptoSuite = cryptoSuite;

            return (byte[])_masterKeySalt.Clone();
        }

        /// <summary>
        /// A context of its own, under the keys this stream already holds.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The same keys, and nothing else shared. An SRTP context carries the state of one sender -
        /// where it has got to in the packet numbering, and the roll over that goes with it - and the
        /// keystream protecting a packet is worked out from that state. Two senders using one context
        /// advance it past each other, and each protects its packets with a keystream the receiver
        /// cannot reproduce.
        /// </para>
        /// <para>
        /// So a stream whose key belongs to the whole stream still gets a context to itself. Which is
        /// the same reasoning as the SSRCs: shared key, separate everything else.
        /// </para>
        /// </remarks>
        internal SrtpSessionContext CreateSeparateContext()
        {
            if (_masterKeySalt == null || _cryptoSuite == null)
            {
                return null;
            }

            SrtpKeys keys = SrtpProtocol.CreateMasterKeys(_cryptoSuite, _mki, _masterKeySalt);

            return SrtpProtocol.CreateSrtpSessionContext(keys);
        }

        /// <summary>
        /// Forgets the keys, so that the next request for them derives fresh ones.
        /// </summary>
        /// <remarks>
        /// Only safe where nobody holds the old ones: a receiver still decrypting under them would
        /// be left with keys that no longer read anything, and nothing to say so.
        /// </remarks>
        internal void ResetSrtpContext()
        {
            Context = null;
            _masterKeySalt = null;
            _mki = null;
            _cryptoSuite = null;
        }

        /// <summary>
        /// When true the next packet sent on this stream is preceded by a sender report, whatever the
        /// interval says. Set when the stream starts playing, so a client is told the mapping between
        /// wall clock and RTP timestamps straight away rather than at the end of the first interval.
        /// </summary>
        public bool MustSendRtcpPacket { get; set; } = false;

        /// <summary>
        /// When the last sender report went out on this stream.
        /// </summary>
        /// <remarks>
        /// There used to be no such thing, because the flag above was set once and never cleared:
        /// every single packet was preceded by a sender report. RFC 3550 puts RTCP at a few per cent
        /// of what the session sends, not one for one with it.
        /// </remarks>
        public DateTime LastSenderReportUtc { get; set; } = DateTime.MinValue;

        /// <summary>
        /// True when the track this stream carries asked for SAVP, so everything sent on it has to be
        /// protected. Set by SETUP.
        /// </summary>
        /// <remarks>
        /// Kept apart from <see cref="Context"/> being present: the two differing is exactly the case
        /// worth catching, because sending unprotected RTP for a track that asked for SAVP would
        /// quietly undo the encryption the server was configured for.
        /// </remarks>
        public bool RequiresSrtp { get; set; }

        /// <summary>
        /// Whether this stream has already complained about having no SRTP keys, so that a stream
        /// dropping every frame says so once rather than at frame rate.
        /// </summary>
        internal bool ReportedMissingSrtpKeys { get; set; }

        /// <summary>
        /// SSRC carried by the RTP this stream sends.
        /// </summary>
        /// <remarks>
        /// One per stream, not one per connection: the video and audio of a session are separate RTP
        /// streams and each announces its own SSRC in its SETUP reply, so sending both under a single
        /// SSRC would contradict what the client was told.
        /// </remarks>
        public uint SSRC { get; set; }

        /// <summary>
        /// Sequence number.
        /// </summary>
        public ushort SequenceNumber { get; set; } = 1;

        /// <summary>
        /// Pair of UDP sockets (data and control) used when sending via UDP.
        /// </summary>
        public IRtpTransport RtpChannel { get; set; }

        /// <summary>
        /// Whether this client takes this track from a multicast group rather than from a stream of
        /// its own.
        /// </summary>
        /// <remarks>
        /// Such a client has no transport here: the media goes to the group once, however many are
        /// listening, so the fan out passes over it. This says the difference between that and a
        /// track which was never set up.
        /// </remarks>
        public bool IsMulticast { get; set; }

        // <summary>
        // Time since last RTCP message received - used to spot dead UDP clients.
        // </summary>
        //public DateTime TimeSinceLastRtcpKeepalive { get; set; } = DateTime.UtcNow; 

        /// <summary>
        /// Used in the RTCP Sender Report to state how many RTP packets have been transmitted (for packet loss)
        /// </summary>
        public uint RtpPacketCount { get; set; } = 0;

        /// <summary>
        /// Number of bytes of video that have been transmitted (for average bandwidth monitoring)
        /// </summary>
        public uint OctetCount { get; set; } = 0;

        /// <summary>
        /// The middle of the timestamp of the last sender report sent on this stream, and when it
        /// went out.
        /// </summary>
        /// <remarks>
        /// A receiver echoes the first of these back and says how long it held the report. With what
        /// is kept here, that is enough to work out the round trip - which a sender has no other way
        /// of measuring, since nothing it sends is answered.
        /// </remarks>
        public uint LastSenderReportMiddle32 { get; set; }

        public DateTime LastSenderReportSentUtc { get; set; } = DateTime.MinValue;

        /// <summary>
        /// The last thing a receiver said about how this stream is reaching it.
        /// </summary>
        public double LastReportedFractionLost { get; set; }

        public int LastReportedCumulativeLost { get; set; }

        public uint LastReportedJitter { get; set; }

        public TimeSpan? LastReportedRoundTrip { get; set; }

        public DateTime LastReceptionReportUtc { get; set; } = DateTime.MinValue;
    }
}
