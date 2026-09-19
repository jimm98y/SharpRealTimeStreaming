// SharpRTSPServer
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// A track has to say what kind of media it carries.
    /// </summary>
    /// <remarks>
    /// It used to default to <c>(TrackType)ID</c> - the track's place in its stream read as the
    /// sort of media it is - which is right only for a stream that is one video track followed by
    /// one audio track. The kind is acted on, so a silently wrong answer is worse than none.
    /// </remarks>
    [TestClass]
    public sealed class TrackKindTests
    {
        /// <summary>A track written the way someone outside this library would write one.</summary>
        private sealed class TrackThatDoesNotSay : TrackBase
        {
            public override string Codec => "TEST";
            public override int ID { get; set; }
            public override int PayloadType { get; set; } = 96;
            public override bool IsReady => true;

            public override StringBuilder BuildSDP(StringBuilder sdp) => sdp;

            public override void CreateRtpPackets(List<ReadOnlyMemory<byte>> samples, uint rtpTimestamp, RtpPackets packets)
            {
            }
        }

        [TestMethod]
        public void ATrackThatDoesNotSayItsKindSaysSoLoudly()
        {
            var track = new TrackThatDoesNotSay { ID = 0 };

            var thrown = Assert.ThrowsExactly<NotImplementedException>(() => _ = track.Kind);

            Assert.Contains(nameof(TrackThatDoesNotSay), thrown.Message,
                "the message should name the track that has to be fixed");
            Assert.Contains("Kind", thrown.Message);
        }

        [TestMethod]
        public void ItThrowsWhateverTheIdHappensToBe()
        {
            // ID 0 used to come out as Video and ID 1 as Audio, which is exactly the accident that
            // made this look as though it worked.
            foreach (int id in new[] { 0, 1, 2, 7 })
            {
                var track = new TrackThatDoesNotSay { ID = id };
                Assert.ThrowsExactly<NotImplementedException>(() => _ = track.Kind, "ID " + id);
            }
        }

        [TestMethod]
        public void EveryTrackThisLibraryShipsSaysItsKind()
        {
            // The guard that matters: adding a codec without overriding Kind should fail here rather
            // than at the first frame of a live stream.
            var tracks = new ITrack[]
            {
                new H264Track(new byte[] { 0x67 }, new byte[] { 0x68 }),
                new H265Track(new byte[] { 0x40 }, new byte[] { 0x42 }, new byte[] { 0x44 }),
                new H266Track(new byte[] { 0x00 }, new byte[] { 0x01 }, new byte[] { 0x02 }, new byte[] { 0x03 }),
                new AV1Track(),
                new MJpegTrack(),
                new MP4VTrack(),
                new AACTrack(new byte[] { 0x12, 0x10 }, 44100, 2),
                new OpusTrack(),
                new PCMATrack(),
                new PCMUTrack(),
                new AMRTrack(),
                new G726Track(),
                new MetadataTrack(),
                new ProxyTrack(TrackType.Video),
            };

            foreach (ITrack track in tracks)
            {
                TrackType kind = track.Kind; // throws if the track did not override it
                Assert.IsTrue(Enum.IsDefined(typeof(TrackType), kind), track.GetType().Name);
            }
        }

        [TestMethod]
        public void AKindIsNotReadFromTheTrackId()
        {
            // A second video track has ID 1, which used to make it report itself as audio.
            var second = new H264Track(new byte[] { 0x67 }, new byte[] { 0x68 }) { ID = 1 };

            Assert.AreEqual(TrackType.Video, second.Kind);
        }
    }
}
