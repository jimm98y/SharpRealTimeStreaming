using System;

namespace SharpRTSPClient
{
    /// <summary>
    /// Media that arrived on one track, with enough about the track to say which.
    /// </summary>
    /// <remarks>
    /// The video and audio events report the first track of each kind. This one reports every track,
    /// which is the only way a second track of a kind, or one that is neither sound nor pictures, can
    /// be told apart from the first.
    /// </remarks>
    public class TrackDataEventArgs : EventArgs
    {
        public TrackDataEventArgs(int trackIndex, TrackKind kind, string codec, SimpleDataEventArgs data)
        {
            TrackIndex = trackIndex;
            Kind = kind;
            Codec = codec;
            Data = data;
        }

        /// <summary>
        /// Where this track came in the description, which is what tells two of a kind apart.
        /// </summary>
        public int TrackIndex { get; }

        /// <summary>
        /// What sort of media the track carries.
        /// </summary>
        public TrackKind Kind { get; }

        /// <summary>
        /// The codec as the description named it.
        /// </summary>
        public string Codec { get; }

        /// <summary>
        /// The frame itself, and when it was sent.
        /// </summary>
        public SimpleDataEventArgs Data { get; }
    }

    /// <summary>
    /// A track the stream offers, reported as the description is read.
    /// </summary>
    public class NewTrackEventArgs : EventArgs
    {
        public NewTrackEventArgs(int trackIndex, TrackKind kind, int payloadType, string codec,
            IStreamConfigurationData configuration)
        {
            TrackIndex = trackIndex;
            Kind = kind;
            PayloadType = payloadType;
            Codec = codec;
            StreamConfigurationData = configuration;
        }

        public int TrackIndex { get; }

        public TrackKind Kind { get; }

        public int PayloadType { get; }

        public string Codec { get; }

        /// <summary>
        /// Whatever the description held for this track, or null where it held nothing this client
        /// knows how to read.
        /// </summary>
        public IStreamConfigurationData StreamConfigurationData { get; }
    }
}
