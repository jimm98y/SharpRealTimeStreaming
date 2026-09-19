using System;
using System.Collections.Generic;
using SharpRTSPServer;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// Recognising a picture a decoder could start on, whatever shape the sample arrives in.
    /// </summary>
    /// <remarks>
    /// These tracks packetise a sample as one bare NAL, and that is what they document. But a sample
    /// read straight out of an mp4 has a four byte length in front of it, and one read out of an
    /// elementary stream has a start code - and a producer handing either of those over gets a
    /// stream that mostly plays, because a decoder is forgiving about a few bytes in front of a
    /// slice. What it does not get is a keyframe anyone can recognise, because the byte being read
    /// is the first byte of a length rather than the NAL header. That is silent, and it turns into
    /// a client that never gets a starting point.
    /// </remarks>
    [TestClass]
    public sealed class KeyFrameDetectionTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        private static bool IsKeyFrame(byte[] sample)
        {
            var track = new H264Track(Sps, Pps);
            var packets = RtpPackets.Take();

            try
            {
                track.CreateRtpPackets(
                    new List<ReadOnlyMemory<byte>> { new ReadOnlyMemory<byte>(sample) }, 3000, packets);

                return packets.IsKeyFrame;
            }
            finally
            {
                packets.Release();
            }
        }

        /// <summary>An IDR slice, as a bare NAL.</summary>
        private static byte[] BareIdr()
        {
            var nal = new byte[64];
            nal[0] = 0x65;
            return nal;
        }

        private static byte[] BarePredicted()
        {
            var nal = new byte[64];
            nal[0] = 0x41;
            return nal;
        }

        private static byte[] With(byte[] prefix, byte[] nal)
        {
            var sample = new byte[prefix.Length + nal.Length];
            Array.Copy(prefix, sample, prefix.Length);
            Array.Copy(nal, 0, sample, prefix.Length, nal.Length);
            return sample;
        }

        [TestMethod]
        public void ABareKeyFrameIsRecognised()
        {
            Assert.IsTrue(IsKeyFrame(BareIdr()));
            Assert.IsFalse(IsKeyFrame(BarePredicted()));
        }

        [TestMethod]
        public void AKeyFrameBehindAnMp4LengthIsRecognised()
        {
            // Four bytes of big endian length, which is how an mp4 stores every NAL.
            byte[] idr = BareIdr();
            byte[] length = { 0x00, 0x00, 0x00, (byte)idr.Length };

            Assert.IsTrue(IsKeyFrame(With(length, idr)),
                "a sample straight out of an mp4 was not recognised as a keyframe");

            byte[] predicted = BarePredicted();
            byte[] predictedLength = { 0x00, 0x00, 0x00, (byte)predicted.Length };

            Assert.IsFalse(IsKeyFrame(With(predictedLength, predicted)),
                "a predicted picture was mistaken for a keyframe");
        }

        [TestMethod]
        public void AKeyFrameBehindAStartCodeIsRecognised()
        {
            Assert.IsTrue(IsKeyFrame(With(new byte[] { 0x00, 0x00, 0x00, 0x01 }, BareIdr())),
                "a NAL with a four byte start code was not recognised");

            Assert.IsTrue(IsKeyFrame(With(new byte[] { 0x00, 0x00, 0x01 }, BareIdr())),
                "a NAL with a three byte start code was not recognised");

            Assert.IsFalse(IsKeyFrame(With(new byte[] { 0x00, 0x00, 0x00, 0x01 }, BarePredicted())),
                "a predicted picture was mistaken for a keyframe");
        }

        [TestMethod]
        public void AKeyFrameAfterItsParameterSetsIsRecognised()
        {
            // SPS, PPS and the IDR in one access unit, start codes between them, which is what a
            // reader that hands over whole access units produces.
            var sample = new List<byte>();

            foreach (byte[] nal in new[] { Sps, Pps, BareIdr() })
            {
                sample.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x01 });
                sample.AddRange(nal);
            }

            Assert.IsTrue(IsKeyFrame(sample.ToArray()),
                "an access unit of parameter sets and an IDR was not recognised");
        }
    }
}
