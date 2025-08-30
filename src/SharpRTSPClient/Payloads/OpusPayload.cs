using System.Buffers;

namespace Rtsp.Rtp
{
    // This class handles the Opus Payload
    // It has methods to parse parameters in the SDP
    // It has methods to process the RTP Payload

    public class OpusPayload : RawPayload
    {
        public OpusPayload(MemoryPool<byte>? memoryPool = null)
            : base(memoryPool)
        {
        }
    }
}
