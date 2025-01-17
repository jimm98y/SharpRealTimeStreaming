using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace SharpRTSPServer
{
    /// <summary>
    /// H264 video track.
    /// </summary>
    public class H264Track : TrackBase
    {
        /// <summary>
        /// H264 Video Codec name.
        /// </summary>
        public override string Codec => "H264";

        /// <summary>
        /// Default video track clock rate.
        /// </summary>
        public const int DEFAULT_CLOCK = 90000;

        /// <summary>
        /// Track ID. Used to identify the track in the SDP.
        /// </summary>
        public override int ID { get; set; } = 0;

        /// <summary>
        /// H264 Profile IDC. Default value is 77 (Main Profile).
        /// </summary>
        public int ProfileIdc { get; set; } = 77;

        /// <summary>
        /// H264 Profile IOP. Default value is 0.
        /// </summary>
        public int ProfileIop { get; set; } = 0; // bit 7 (msb) is 0 so constrainedFlag is false

        /// <summary>
        /// H264 Level. Default value is 42, meaning 4.2.
        /// </summary>
        public int Level { get; set; } = 42; // Level 4.2

        /// <summary>
        /// Video clock rate. Default value is 90000.
        /// </summary>
        public int VideoClock { get; set; } = DEFAULT_CLOCK;

        /// <summary>
        /// Maximum size of the packet. If the resulting RTP packet exceeds this size, fragmentation will be used. Default value is 1400 and RTP over RTSP is constrained to 65535.
        /// </summary>
        public int PacketMTU { get; set; } = 1400;

        /// <summary>
        /// Sequence Parameter Set (SPS).
        /// </summary>
        public byte[] SPS { get; set; }

        /// <summary>
        /// Picture Parameter Set (PPS).
        /// </summary>
        public byte[] PPS { get; set; }

        /// <summary>
        /// Is the track ready?
        /// </summary>
        public override bool IsReady { get { return SPS != null && PPS != null; } }

        private int _payloadType = -1;

        /// <summary>
        /// Payload type. H264 uses a dynamic payload type, which by default we calculate as 96 + track ID.
        /// </summary>
        public override int PayloadType
        {
            get 
            {
                if (_payloadType < 0)
                {
                    return RTSPServer.DYNAMIC_PAYLOAD_TYPE + ID;
                }
                else
                {
                    return _payloadType;
                }
            }
            set 
            { 
                _payloadType = value;
            }
        }

        /// <summary>
        /// Ctor.
        /// </summary>
        /// <param name="profileIdc">H264 Profile IDC. Default value is 77.</param>
        /// <param name="profileIop">H264 Profile IOP. Default value is 0. See https://www.rfc-editor.org/rfc/rfc6184#page-41.</param>
        /// <param name="level">H264 Level. Default value is 42.</param>
        /// <param name="clock">H264 clock. Default value is 90000.</param>
        public H264Track(int profileIdc = 77, int profileIop = 0, int level = 42, int clock = DEFAULT_CLOCK)
        {
            this.ProfileIdc = profileIdc;
            this.ProfileIop = profileIop;
            this.Level = level;
            this.VideoClock = clock;
        }

        /// <summary>
        /// Ctor.
        /// </summary>
        /// <param name="sps">Sequence Parameter Set (SPS).</param>
        /// <param name="pps">Picture Parameter Set (PPS).</param>
        /// <param name="profileIdc">H264 Profile IDC. Default value is 77.</param>
        /// <param name="profileIop">H264 Profile IOP. Default value is 0. See https://www.rfc-editor.org/rfc/rfc6184#page-41.</param>
        /// <param name="level">H264 Level. Default value is 42.</param>
        /// <param name="clock">H264 clock. Default value is 90000.</param>
        public H264Track(byte[] sps, byte[] pps, int profileIdc = 77, int profileIop = 0, int level = 42, int clock = DEFAULT_CLOCK) : this(profileIdc, profileIop, level, clock)
        {
            SetParameterSets(sps, pps);
        }

        /// <summary>
        /// Feed in Raw SPS/PPS data - no 32 bit headers, no Annex-B (00 00 00 01).
        /// </summary>
        /// <param name="sps">Sequence Parameter Set (SPS).</param>
        /// <param name="pps">Picture Parameter Set (PPS).</param>
        public void SetParameterSets(byte[] sps, byte[] pps)
        {
            this.SPS = sps;
            this.PPS = pps;
        }

        /// <summary>
        /// Build the SDP for this track.
        /// </summary>
        /// <param name="sdp">SDP <see cref="StringBuilder"/>.</param>
        /// <returns><see cref="StringBuilder"/>.</returns>
        public override StringBuilder BuildSDP(StringBuilder sdp)
        {
            // Make the profile-level-id
            // Eg a string of profile-level-id=42A01E is
            // a Profile eg Constrained Baseline, Baseline, Extended, Main, High. This defines which features in H264 are used
            // a Level eg 1,2,3 or 4. This defines a max resoution for the video. 2=up to SD, 3=upto 1080p. Decoders can then reserve sufficient RAM for frame buffers
            string profileLevelIdStr = ProfileIdc.ToString("X2") + // convert to hex, padded to 2 characters
                                       ProfileIop.ToString("X2") +
                                       Level.ToString("X2");

            string spsStr = Convert.ToBase64String(SPS);
            string ppsStr = Convert.ToBase64String(PPS);

            sdp.Append($"m=video 0 RTP/AVP {PayloadType}\n");
            sdp.Append($"a=control:trackID={ID}\n");
            sdp.Append($"a=rtpmap:{PayloadType} {Codec}/{VideoClock}\n");
            sdp.Append($"a=fmtp:{PayloadType} profile-level-id={profileLevelIdStr}; sprop-parameter-sets={spsStr},{ppsStr}\n");
            return sdp;
        }

        /// <summary>
        /// Creates RTP packets.
        /// </summary>
        /// <param name="samples">An array of H264 NALUs.</param>
        /// <param name="rtpTimestamp">RTP timestamp in the timescale of the track.</param>
        /// <returns>RTP packets.</returns>
        public override List<IMemoryOwner<byte>> CreateRtpPackets(ReadOnlySequence<byte> samples, uint rtpTimestamp)
        {
            List<IMemoryOwner<byte>> memoryOwners = new List<IMemoryOwner<byte>>();
            var length = samples.Length;
            foreach (var rawNal in samples)
            {
                bool lastNal = (length -= rawNal.Length) == 0;

                // The H264 Payload could be sent as one large RTP packet (assuming the receiver can handle it)
                // or as a Fragmented Data, split over several RTP packets with the same Timestamp.
                bool fragmenting = false;

                int packetMTU = PacketMTU; // 65535; 
                packetMTU += -8 - 20 - 16; // -8 for UDP header, -20 for IP header, -16 normal RTP header len. ** LESS RTP EXTENSIONS !!!

                if (rawNal.Length > packetMTU)
                {
                    fragmenting = true;
                }

                // INDIGO VISION DOES NOT SUPPORT FRAGMENTATION. Send as one jumbo RTP packet and let OS split over MTUs.
                // NOTE TO SELF... perhaps this was because the SDP did not have the extra packetization flag
                //  fragmenting = false;
                if (fragmenting)
                {
                    int dataRemaining = rawNal.Length;
                    int nalPointer = 0;
                    int startBit = 1;
                    int endBit = 0;

                    // consume first byte of the raw_nal. It is used in the FU header
                    byte firstByte = rawNal.Span[0];
                    nalPointer++;
                    dataRemaining--;

                    while (dataRemaining > 0)
                    {
                        int payloadSize = Math.Min(packetMTU, dataRemaining);
                        
                        if (dataRemaining == payloadSize) 
                            endBit = 1;

                        // 12 is header size. 2 bytes for H264 FU-A header
                        var fuHeader = 2;
                        var destSize = 12 + fuHeader + payloadSize;
                        var owner = AdjustedSizeMemoryOwner.Rent(destSize);
                        memoryOwners.Add(owner);
                        var rtpPacket = owner.Memory.Slice(0, destSize);

                        // RTP Packet Header
                        // 0 - Version, P, X, CC, M, PT and Sequence Number
                        //32 - Timestamp. H264/H265 uses a 90kHz clock
                        //64 - SSRC
                        //96 - CSRCs (optional)
                        //nn - Extension ID and Length
                        //nn - Extension header
                        const bool rtpPadding = false;
                        const bool rtpHasExtension = false;
                        const int rtpCsrcCount = 0;

                        RTPPacketUtil.WriteHeader(rtpPacket.Span, RTPPacketUtil.RTP_VERSION,
                            rtpPadding, rtpHasExtension, rtpCsrcCount, lastNal && endBit == 1, PayloadType);

                        // sequence number and SSRC are set just before send
                        RTPPacketUtil.WriteTS(rtpPacket.Span, rtpTimestamp);

                        // Now append the Fragmentation Header (with Start and End marker) and part of the raw_nal
                        const byte fBit = 0;
                        byte nri = (byte)(firstByte >> 5 & 0x03); // Part of the 1st byte of the Raw NAL (NAL Reference ID)
                        const byte type = 28; // FU-A Fragmentation

                        // FU-A header
                        rtpPacket.Span[12] = (byte)((fBit << 7) + (nri << 5) + type);
                        rtpPacket.Span[13] = (byte)((startBit << 7) + (endBit << 6) + (0 << 5) + (firstByte & 0x1F));

                        rawNal.Span.Slice(nalPointer, payloadSize).CopyTo(rtpPacket.Slice(14).Span);

                        nalPointer += payloadSize;
                        dataRemaining -= payloadSize;

                        startBit = 0;
                    }
                }
                else
                {
                    // Put the whole NAL into one RTP packet.
                    // Note some receivers will have maximum buffers and be unable to handle large RTP packets.
                    // Also with RTP over RTSP there is a limit of 65535 bytes for the RTP packet.

                    // 12 is header size when there are no CSRCs or extensions
                    var owner = AdjustedSizeMemoryOwner.Rent(12 + rawNal.Length);
                    memoryOwners.Add(owner);
                    var rtpPacket = owner.Memory.Slice(0, 12 + rawNal.Length);

                    const bool rtpPadding = false;
                    const bool rtpHasExtension = false;
                    const int rtpCsrcCount = 0;

                    RTPPacketUtil.WriteHeader(rtpPacket.Span,
                        RTPPacketUtil.RTP_VERSION,
                        rtpPadding,
                        rtpHasExtension, rtpCsrcCount, lastNal, PayloadType);

                    // sequence number and SSRC are set just before send
                    RTPPacketUtil.WriteTS(rtpPacket.Span, rtpTimestamp);

                    // Now append the raw NAL
                    rawNal.CopyTo(rtpPacket.Slice(12));
                }
            }

            return memoryOwners;
        }
    }
}
