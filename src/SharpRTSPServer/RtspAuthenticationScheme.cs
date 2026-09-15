namespace SharpRTSPServer
{
    /// <summary>
    /// The scheme clients are challenged with in the WWW-Authenticate header.
    /// </summary>
    public enum RtspAuthenticationScheme
    {
        /// <summary>
        /// Digest access authentication. The client proves it knows the password without sending it,
        /// so the password is not exposed even on a plaintext connection. This is the default.
        /// </summary>
        Digest = 0,

        /// <summary>
        /// Basic access authentication. The user name and password are sent base64 encoded, which is
        /// reversible by anyone who can read the traffic, so this is only safe over RTSPS.
        /// </summary>
        /// <remarks>
        /// Off by default. Turn it on only for clients and hardware decoders that cannot do Digest,
        /// and prefer to pair it with a TLS certificate so the credentials are not sent in the clear.
        /// </remarks>
        Basic = 1,
    }
}
