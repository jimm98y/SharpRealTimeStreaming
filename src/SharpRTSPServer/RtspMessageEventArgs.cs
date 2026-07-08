using Rtsp.Messages;
using System;

namespace SharpRTSPServer
{
    public class RtspMessageEventArgs : EventArgs
    {
        public RtspMessage Message { get; private set; }
        public RTSPConnection Connection { get; private set; }
        public RtspMessageEventArgs(RtspMessage message, RTSPConnection connection = null)
        {
            Message = message;
            Connection = connection;
        }
    }
}
