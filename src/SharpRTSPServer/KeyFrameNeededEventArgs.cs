using System;

namespace SharpRTSPServer
{
    /// <summary>
    /// Says that a client is waiting for a picture it can start on.
    /// </summary>
    /// <remarks>
    /// See <see cref="RTSPServer.KeyFrameNeeded"/> for when this is raised and what handling it is
    /// worth.
    /// </remarks>
    public class KeyFrameNeededEventArgs : EventArgs
    {
        public KeyFrameNeededEventArgs(string sessionId)
        {
            SessionId = sessionId;
        }

        /// <summary>
        /// The session that is waiting.
        /// </summary>
        /// <remarks>
        /// A stream is normally encoded once for everybody, so an encoder asked for a keyframe
        /// gives one to every client at once. This says which client was waiting, for a server that
        /// encodes per client or that wants to know how often this happens.
        /// </remarks>
        public string SessionId { get; }
    }
}
