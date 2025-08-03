using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rtsp.Onvif;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Rtsp.Rtp
{
    // This class handles the AV1 Payload
    // It has methods to parse parameters in the SDP
    // It has methods to process the RTP Payload

    public class AV1Payload : IPayloadProcessor
    {
        private readonly ILogger _logger;

        // AV1 structure.

        private readonly List<ReadOnlyMemory<byte>> obus = [];
        private readonly List<IMemoryOwner<byte>> owners = [];

        private readonly MemoryPool<byte> _memoryPool;

        private DateTime _timestamp;

        // Constructor
        public AV1Payload(ILogger<AV1Payload>? logger, MemoryPool<byte>? memoryPool = null)
        {
            _logger = logger as ILogger ?? NullLogger.Instance;
            _memoryPool = memoryPool ?? MemoryPool<byte>.Shared;
        }

        public RawMediaFrame ProcessPacket(RtpPacket packet)
        {
            if (packet.Extension.Length > 0)
            {
                _timestamp = RtpPacketOnvifUtils.ProcessRTPTimestampExtension(packet.Extension, headerPosition: out _);
            }

            ProcessRTPFrame(packet.Payload);

            if (!packet.IsMarker)
            {
                // we don't have a frame yet. Keep accumulating RTP packets
                return RawMediaFrame.Empty;
            }

            // End Marker is set return the list of OBUs
            // clone list of nalUnits and owners
            var result = new RawMediaFrame([.. obus], [.. owners])
            {
                RtpTimestamp = packet.Timestamp,
                ClockTimestamp = _timestamp,
            };
            obus.Clear();
            owners.Clear();
            return result;
        }

        private void ProcessRTPFrame(ReadOnlySpan<byte> payload)
        {
            throw new NotImplementedException();
        }

        private Span<byte> PrepareNewObu(int sizeWitoutHeader)
        {
            var owner = _memoryPool.Rent(sizeWitoutHeader + 4);
            owners.Add(owner);
            var memory = owner.Memory[..(sizeWitoutHeader + 4)];
            obus.Add(memory);
            return memory.Span;
        }
    }
}