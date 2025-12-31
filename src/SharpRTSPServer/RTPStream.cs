using Org.BouncyCastle.Security;
using Rtsp;
using SharpSRTP.SRTP;
using System;
using System.Linq;
using System.Numerics;

namespace SharpRTSPServer
{
    /// <summary>
    /// An RTPStream can be a Video Stream, Audio Stream or a Metadata Stream.
    /// </summary>
    public class RTPStream
    {
        private static SecureRandom _rand = new SecureRandom();
        public byte[] PrepareSrtpContext(int protectionProfile = ExtendedSrtpProtectionProfile.SRTP_AES128_CM_HMAC_SHA1_80, int mkiLen = 0)
        {
            var protectionProfileInfo = DtlsSrtpProtocol.DtlsProtectionProfiles[protectionProfile];
            int masterKeyLen = protectionProfileInfo.CipherKeyLength >> 3;
            int masterSaltLen = protectionProfileInfo.CipherSaltLength >> 3;

            // derive the master key + master salt to be sent in SDP  crypto: attribute as per RFC 4568
            byte[] masterKeySalt = new byte[masterKeyLen + masterSaltLen];
            _rand.NextBytes(masterKeySalt);

            byte[] MKI = new byte[mkiLen];
            if (mkiLen > 0)
            {
                int mkiValue = _rand.Next(0, int.MaxValue);
                BigInteger bi = new BigInteger(mkiValue);
                byte[] mkiValueBytes = bi.ToByteArray();
                Buffer.BlockCopy(mkiValueBytes, 0, MKI, 0, Math.Min(mkiValueBytes.Length, MKI.Length));
            }

            var encodeRtpContext = new SrtpContext(protectionProfile, MKI, masterKeySalt.Take(masterKeyLen).ToArray(), masterKeySalt.Skip(masterKeyLen).ToArray(), SrtpContextType.RTP);
            var encodeRtcpContext = new SrtpContext(protectionProfile, MKI, masterKeySalt.Take(masterKeyLen).ToArray(), masterKeySalt.Skip(masterKeyLen).ToArray(), SrtpContextType.RTCP);
            var decodeRtpContext = new SrtpContext(protectionProfile, MKI, masterKeySalt.Take(masterKeyLen).ToArray(), masterKeySalt.Skip(masterKeyLen).ToArray(), SrtpContextType.RTP);
            var decodeRtcpContext = new SrtpContext(protectionProfile, MKI, masterKeySalt.Take(masterKeyLen).ToArray(), masterKeySalt.Skip(masterKeyLen).ToArray(), SrtpContextType.RTCP);

            Context = new SrtpSessionContext(encodeRtpContext, decodeRtpContext, encodeRtcpContext, decodeRtcpContext);

            return masterKeySalt;
        }
        public SrtpSessionContext Context { get; set; } = null;

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
