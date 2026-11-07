// SharpRTSPClient
// Copyright (C) 2026 Lukas Volf
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

namespace SharpRTSPClient
{
    /// <summary>
    /// A track the description offers, put to <see cref="RTSPClient.AcceptTrack"/> before the client
    /// sets it up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything here is read out of the description. It is offered only for tracks this client
    /// could actually play - one whose codec it has no parser for is never offered, because there
    /// would be nothing to decide.
    /// </para>
    /// <para>
    /// This replaced a pair of settings: a MediaRequest of VIDEO_ONLY, AUDIO_ONLY or
    /// VIDEO_AND_AUDIO, and a ReceiveAllTracks flag. Between them they could say which kinds and
    /// how many of each, and nothing else - not "the H265 one rather than the H264 one", not "the
    /// English audio", not "the two lowest qualities". One question per track answers all of them.
    /// </para>
    /// </remarks>
    public sealed class TrackOffer
    {
        internal TrackOffer(int descriptionIndex, TrackKind kind, string codec, int payloadType,
            int acceptedSoFar, int acceptedOfThisKind)
        {
            DescriptionIndex = descriptionIndex;
            Kind = kind;
            Codec = codec;
            PayloadType = payloadType;
            AcceptedSoFar = acceptedSoFar;
            AcceptedOfThisKind = acceptedOfThisKind;
        }

        /// <summary>
        /// Where this track's media section came in the description, counting from zero.
        /// </summary>
        /// <remarks>
        /// The description's own order, which is not the order the tracks are offered in - they are
        /// offered a kind at a time, the video sections first.
        /// </remarks>
        public int DescriptionIndex { get; }

        /// <summary>
        /// What sort of media the track carries.
        /// </summary>
        public TrackKind Kind { get; }

        /// <summary>
        /// The codec, as the rtpmap of the description named it, upper case - "H264", "AAC", "PCMU".
        /// Empty where the description named none.
        /// </summary>
        public string Codec { get; }

        /// <summary>
        /// The RTP payload type the description gives this track.
        /// </summary>
        public int PayloadType { get; }

        /// <summary>
        /// How many tracks have been accepted before this one was offered.
        /// </summary>
        /// <remarks>
        /// The index this track will have if it is accepted, which is what
        /// <see cref="NewTrackEventArgs.TrackIndex"/> reports and what
        /// <see cref="RTSPClient.SendRTCP(int, byte[])"/> takes.
        /// </remarks>
        public int AcceptedSoFar { get; }

        /// <summary>
        /// How many tracks of this kind have been accepted before this one was offered.
        /// </summary>
        /// <remarks>
        /// Zero for the first video track, the first audio track and the first metadata track, which
        /// is what makes "the first of each kind" a condition rather than a setting - see
        /// <see cref="RTSPClient.FirstOfEachKind"/>.
        /// </remarks>
        public int AcceptedOfThisKind { get; }

        public override string ToString()
        {
            return $"{Kind} {Codec} (payload {PayloadType}, section {DescriptionIndex})";
        }
    }
}
