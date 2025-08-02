using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
            throw new NotImplementedException();
        }
    }
}