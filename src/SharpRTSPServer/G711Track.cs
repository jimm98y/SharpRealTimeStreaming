using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace SharpRTSPServer
{
    public class PCMUTrack : ITrack
    {
        public int ID { get; set; } = 1;
        public int SamplingRate { get; } = 8000;
        public int Channels { get; } = 1;

        public bool IsReady { get { return true; } }

        public int PayloadType => 0;

        public StringBuilder BuildSDP(StringBuilder sdp)
        {
            sdp.Append($"m=audio 0 RTP/AVP {PayloadType}\n"); // <---- Payload Type 0 means G711 ULAW
            sdp.Append($"a=control:trackID={ID}\n");
            sdp.Append($"a=rtpmap:{PayloadType} PCMU/{SamplingRate}\n");
            return sdp;
        }

        public (List<Memory<byte>>, List<IMemoryOwner<byte>>) PrepareRtpPackets(List<byte[]> samples, uint rtpTimestamp)
        {
            List<Memory<byte>> rtpPackets = new List<Memory<byte>>();
            List<IMemoryOwner<byte>> memoryOwners = new List<IMemoryOwner<byte>>();

            for (int i = 0; i < samples.Count; i++)
            {
                var audioPacket = samples[i];
                var size = 12 + audioPacket.Length;
                var owner = MemoryPool<byte>.Shared.Rent(size);
                memoryOwners.Add(owner);

                var rtpPacket = owner.Memory.Slice(0, size);

                const bool rtpPadding = false;
                const bool rtpHasExtension = false;
                int rtpCsrcCount = 0;
                const bool rtpMarker = true;

                RTPPacketUtil.WriteHeader(rtpPacket.Span,
                    RTPPacketUtil.RTP_VERSION, rtpPadding, rtpHasExtension, rtpCsrcCount, rtpMarker, PayloadType);

                RTPPacketUtil.WriteTS(rtpPacket.Span, rtpTimestamp);
                audioPacket.CopyTo(rtpPacket.Slice(12));
                rtpPackets.Add(rtpPacket);
            }

            return (rtpPackets, memoryOwners);
        }
    }

    public class PCMATrack : ITrack
    {
        public int ID { get; set; } = 1;
        public int SamplingRate { get; } = 8000;
        public int Channels { get; } = 1;

        public bool IsReady { get { return true; } }

        public int PayloadType => 8;

        public StringBuilder BuildSDP(StringBuilder sdp)
        {
            sdp.Append($"m=audio 0 RTP/AVP {PayloadType}\n"); // <---- Payload Type 8 means G711 ALAW
            sdp.Append($"a=control:trackID={ID}\n");
            sdp.Append($"a=rtpmap:{PayloadType} PCMA/{SamplingRate}\n");
            return sdp;
        }

        public (List<Memory<byte>>, List<IMemoryOwner<byte>>) PrepareRtpPackets(List<byte[]> samples, uint rtpTimestamp)
        {
            List<Memory<byte>> rtpPackets = new List<Memory<byte>>();
            List<IMemoryOwner<byte>> memoryOwners = new List<IMemoryOwner<byte>>();

            for (int i = 0; i < samples.Count; i++)
            {
                var audioPacket = samples[i];
                var size = 12 + audioPacket.Length;
                var owner = MemoryPool<byte>.Shared.Rent(size);
                memoryOwners.Add(owner);

                var rtpPacket = owner.Memory.Slice(0, size);

                const bool rtpPadding = false;
                const bool rtpHasExtension = false;
                int rtpCsrcCount = 0;
                const bool rtpMarker = true; 

                RTPPacketUtil.WriteHeader(rtpPacket.Span,
                    RTPPacketUtil.RTP_VERSION, rtpPadding, rtpHasExtension, rtpCsrcCount, rtpMarker, PayloadType);

                RTPPacketUtil.WriteTS(rtpPacket.Span, rtpTimestamp);
                audioPacket.CopyTo(rtpPacket.Slice(12));
                rtpPackets.Add(rtpPacket);
            }

            return (rtpPackets, memoryOwners);
        }
    }
}
