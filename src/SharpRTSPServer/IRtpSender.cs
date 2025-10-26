using System;
using System.Collections.Generic;

namespace SharpRTSPServer
{
    public interface IRtpSender
    {
        void FeedInRawRTP(string streamID, int streamType, uint rtpTimestamp, List<Memory<byte>> rtpPackets);
        bool CanAcceptNewSamples(string streamID);
    }
}
