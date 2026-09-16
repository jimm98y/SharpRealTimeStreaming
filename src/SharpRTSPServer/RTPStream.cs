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

            return (byte[])_masterKeySalt.Clone();
        }

        /// <summary>
        /// When true will send out a RTCP packet to match Wall Clock Time to RTP Payload timestamps.
        /// </summary>
        public bool MustSendRtcpPacket { get; set; } = false;

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
    }
}
