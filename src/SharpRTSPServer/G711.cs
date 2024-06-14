using System.Text;

namespace SharpRTSPServer
{
    public class PCMU : ITrack
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
    }

    public class PCMA : ITrack
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
    }
}
