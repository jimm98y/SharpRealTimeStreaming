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
        public byte[] PrepareSrtpContext(string cryptoSuite, int mkiLen = 0)
        {
            if (string.IsNullOrEmpty(cryptoSuite))
                throw new ArgumentNullException("SRTP Crypto suite not selected!");

            // derive the master key + master salt to be sent in SDP crypto: attribute as per RFC 4568
            byte[] MKI = SrtpProtocol.GenerateMki(mkiLen);

            SrtpKeys keys = SrtpProtocol.CreateMasterKeys(cryptoSuite, MKI);
            Context = SrtpProtocol.CreateSrtpSessionContext(keys);

            return keys.MasterKeySalt;
        }

        /// <summary>
        /// When true will send out a RTCP packet to match Wall Clock Time to RTP Payload timestamps.
        /// </summary>
        public bool MustSendRtcpPacket { get; set; } = false;

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
