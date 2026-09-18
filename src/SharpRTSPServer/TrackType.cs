namespace SharpRTSPServer
{
    /// <summary>
    /// What kind of media a track carries.
    /// </summary>
    /// <remarks>
    /// A kind, not a position. A stream may carry several tracks of the same kind - two languages,
    /// two qualities - so which track a request is about is its index in the stream, and this only
    /// says what sort of thing is in it.
    /// </remarks>
    public enum TrackType : int
    {
        Video = 0,
        Audio = 1,

        /// <summary>
        /// Data about the media rather than the media itself - what a camera saw, and where.
        /// </summary>
        Metadata = 2,
    }
}
