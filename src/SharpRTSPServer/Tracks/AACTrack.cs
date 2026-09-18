using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace SharpRTSPServer
{
    /// <summary>
    /// AAC track.
    /// </summary>
    public class AACTrack : TrackBase
    {
        /// <summary>
        /// AAC Audio Codec name.
        /// </summary>
        public override string Codec => "mpeg4-generic";

        /// <summary>
        /// Track ID. Used to identify the track in the SDP.
        /// </summary>
        public override int ID { get; set; } = 1;

        /// <summary>
        /// What kind of media this track carries.
        /// </summary>
        public override TrackType Kind => TrackType.Audio;

        /// <summary>
        /// Sampling rate.
        /// </summary>
        public int SamplingRate { get; set; } = 44100;

        /// <summary>
        /// Number of channels. 1 for Mono, 2 for Stereo, ...
        /// </summary>
        public int Channels { get; set; } = 1;

        /// <summary>
        /// AAC configuration descriptor.
        /// </summary>
        public byte[] ConfigDescriptor { get; set; }

        /// <summary>
        /// Is the track ready?
        /// </summary>
        public override bool IsReady { get { return ConfigDescriptor != null && ConfigDescriptor.Length > 0; } }

        private int _payloadType = -1;

        /// <summary>
        /// Payload type. AAC uses a dynamic payload type, which by default we calculate as 96 + track ID.
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
        /// <param name="samplingRate">Audio sampling rate.</param>
        /// <param name="channels">Number of audio channels.</param>
        public AACTrack(int samplingRate, int channels)
        {
            this.SamplingRate = samplingRate;
            this.Channels = channels;
        }

        /// <summary>
        /// Ctor.
        /// </summary>
        /// <param name="configDescriptor">AAC configuration descriptor.</param>
        /// <param name="samplingRate">Audio sampling rate.</param>
        /// <param name="channels">Number of audio channels.</param>
        public AACTrack(byte[] configDescriptor, int samplingRate, int channels) : this(samplingRate, channels)
        {
            SetConfigDescriptor(configDescriptor);
        }

        /// <summary>
        /// Set the AAC configuration Descriptor.
        /// </summary>
        /// <param name="configDescriptor">AAC Configuration Descriptor.</param>
        public void SetConfigDescriptor(byte[] configDescriptor)
        {
            this.ConfigDescriptor = configDescriptor;
        }

        /// <summary>
        /// Build the SDP for this track.
        /// </summary>
        /// <param name="sdp">SDP <see cref="StringBuilder"/>.</param>
        /// <returns><see cref="StringBuilder"/>.</returns>
        public override StringBuilder BuildSDP(StringBuilder sdp)
        {
            sdp.Append($"m=audio 0 RTP/{RtpProfile} {PayloadType}\r\n"); // <---- Payload Type 0 means G711 ULAW, 96+ means dynamic payload type
            sdp.Append($"a=control:trackID={ID}\r\n");
            sdp.Append($"a=rtpmap:{PayloadType} {Codec}/{SamplingRate}/{Channels}\r\n");
            sdp.Append($"a=fmtp:{PayloadType} profile-level-id={GetAACProfileLevel(SamplingRate, Channels)}; " +
                $"config={Utilities.ToHexString(ConfigDescriptor)}; streamType=5; mode=AAC-hbr; objectType=64; sizeLength=13; indexLength=3; indexDeltaLength=3\r\n");

            return sdp;
        }

        /// <summary>
        /// Creates RTP packets.
        /// </summary>
        /// <param name="samples">An array of AAC fragments. By default single fragment is expected.</param>
        /// <param name="rtpTimestamp">RTP timestamp in the timescale of the track.</param>
        /// <returns>RTP packets.</returns>
        public override (List<Memory<byte>>, List<IMemoryOwner<byte>>) CreateRtpPackets(List<ReadOnlyMemory<byte>> samples, uint rtpTimestamp)
        {
            List<Memory<byte>> rtpPackets = new List<Memory<byte>>();
            List<IMemoryOwner<byte>> memoryOwners = new List<IMemoryOwner<byte>>();

            for (int i = 0; i < samples.Count; i++)
            {
                ReadOnlyMemory<byte> frame = samples[i];
                ThrowIfTooLongForAUHeader(frame.Length);

                // Put the whole Audio Packet into one RTP packet.
                // 12 is header size when there are no CSRCs or extensions, 4 for the AU header section
                var size = RTP_HEADER_LENGTH + AU_HEADER_LENGTH + frame.Length;
                var owner = MemoryPool<byte>.Shared.Rent(size);
                memoryOwners.Add(owner);

                var rtpPacket = owner.Memory.Slice(0, size);

                const bool rtpPadding = false;
                const bool rtpHasExtension = false;
                int rtpCsrcCount = 0;
                const bool rtpMarker = true; // always 1 as this is the last (and only) RTP packet for this audio timestamp

                RTPPacketUtil.WriteHeader(rtpPacket.Span,
                    RTPPacketUtil.RTP_VERSION, rtpPadding, rtpHasExtension, rtpCsrcCount, rtpMarker, PayloadType);

                // sequence number is set just before send
                RTPPacketUtil.WriteTS(rtpPacket.Span, rtpTimestamp);

                // The AU header and the frame go straight into the packet. Building them in an array
                // of their own first meant an allocation and a second copy for every audio frame,
                // which at forty odd frames a second per stream is steady garbage for nothing.
                WriteAUHeader(rtpPacket.Span.Slice(RTP_HEADER_LENGTH), frame.Length);
                frame.Span.CopyTo(rtpPacket.Span.Slice(RTP_HEADER_LENGTH + AU_HEADER_LENGTH));

                rtpPackets.Add(rtpPacket);
            }

            return (rtpPackets, memoryOwners);
        }

        /// <summary>
        /// Largest AAC frame that fits the AU-size field, which is sizeLength = 13 bits wide.
        /// </summary>
        internal const int MAX_FRAME_LENGTH = (1 << 13) - 1;

        /// <summary>
        /// The RTP header, with no CSRCs or extensions.
        /// </summary>
        private const int RTP_HEADER_LENGTH = 12;

        /// <summary>
        /// The AU header section for a single access unit: two bytes saying the section is sixteen
        /// bits long, then those sixteen bits - a thirteen bit size and a three bit index.
        /// </summary>
        private const int AU_HEADER_LENGTH = 4;

        private static void ThrowIfTooLongForAUHeader(int frameLength)
        {
            if (frameLength > MAX_FRAME_LENGTH)
            {
                // silently truncating the AU-size here would produce a corrupt, undecodable stream
                throw new ArgumentOutOfRangeException(nameof(frameLength), frameLength,
                    $"An AAC frame must not be longer than {MAX_FRAME_LENGTH} bytes to fit the 13 bit AU-size field.");
            }
        }

        private static void WriteAUHeader(Span<byte> destination, int frameLength)
        {
            destination[0] = 0x00;
            destination[1] = 0x10; // 16 bits size of the header

            // the size sits in the top thirteen bits, the AU index in the bottom three
            int auSize = frameLength << 3;
            destination[2] = (byte)((auSize >> 8) & 0xFF);
            destination[3] = (byte)(auSize & 0xFF);
        }

        private static int GetAACLevel(int samplingFrequency, int channelConfiguration)
        {
            if (samplingFrequency <= 24000)
            {
                if (channelConfiguration <= 2)
                    return 1; // AAC Profile, Level 1
            }
            else if (samplingFrequency <= 48000)
            {
                if (channelConfiguration <= 2)
                    return 2; // Level 2
                else if (channelConfiguration <= 5)
                    return 4; // Level 4
            }
            else if (samplingFrequency <= 96000)
            {
                if (channelConfiguration <= 5)
                    return 5; // Level 5
            }

            return 5;
        }

        private static int GetAACHELevel(int samplingFrequency, int channelConfiguration, bool sbr)
        {
            if (samplingFrequency <= 48000)
            {
                if (channelConfiguration <= 2)
                    return sbr ? 3 : 2; // Level 2/3
                else if (channelConfiguration <= 5)
                    return 4; // Level 4
            }
            else if (samplingFrequency <= 96000)
            {
                if (channelConfiguration <= 5)
                    return 5; // Level 5
            }

            return 5;
        }

        private static int GetAACHQLevel(int samplingFrequency, int channelConfiguration)
        {
            if (samplingFrequency <= 22050)
            {
                if (channelConfiguration <= 2)
                    return 1; // Level 1/5
            }
            else if (samplingFrequency <= 48000)
            {
                if (channelConfiguration <= 2)
                    return 2; // Level 2/6
                else if (channelConfiguration <= 5)
                    return 3; // Level 3/4/7/8
            }

            return 8;
        }

        private static int GetAACProfileLevel(int samplingFrequency, int channelConfiguration, int profile = 2, bool sbr = false)
        {
            switch (profile)
            {
                case 2: // AAC_LC
                    return GetAACLevel(samplingFrequency, channelConfiguration) - 1 + 0x28;

                case 5: // AAC_SBR
                    return GetAACHELevel(samplingFrequency, channelConfiguration, sbr) - 2 + 0x2C;

                case 29: // AAC_PS
                    return GetAACHELevel(samplingFrequency, channelConfiguration, sbr) - 2 + 0x30;

                case 8: // AAC_CELP
                    return GetAACHQLevel(samplingFrequency, channelConfiguration) - 1 + 0x0E;

                default:
                    return 1;
            }
        }
    }
}
