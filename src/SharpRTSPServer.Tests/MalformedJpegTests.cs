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

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// The JPEG parser reads a buffer whose shape is whatever the producer handed over - which for
    /// anything forwarding MJPEG from a camera is not something it chose. Every one of these used to
    /// walk off the end of the span.
    /// </summary>
    [TestClass]
    public sealed class MalformedJpegTests
    {
        private static void Parse(byte[] image)
        {
            MJpegTrack.ParseJpeg(image, out _, out _, out _);
        }

        [TestMethod]
        public void AnEmptyImageIsRefused()
        {
            Assert.ThrowsExactly<ArgumentException>(() => Parse(Array.Empty<byte>()));
        }

        [TestMethod]
        public void AnImageOfOneByteIsRefused()
        {
            Assert.ThrowsExactly<ArgumentException>(() => Parse(new byte[] { 0xFF }));
        }

        [TestMethod]
        public void SomethingThatIsNotAJpegIsRefused()
        {
            Assert.ThrowsExactly<ArgumentException>(() => Parse(new byte[] { 0x00, 0x01, 0x02, 0x03 }));
        }

        [TestMethod]
        public void AnImageThatIsOnlyItsMarkerIsRefused()
        {
            Assert.ThrowsExactly<ArgumentException>(() => Parse(new byte[] { 0xFF, 0xD8 }));
        }

        [TestMethod]
        public void ASegmentTruncatedBeforeItsLengthIsRefused()
        {
            // SOI, then a marker with nowhere for the length to be
            Assert.ThrowsExactly<ArgumentException>(() => Parse(new byte[] { 0xFF, 0xD8, 0xFF, 0xDB }));
        }

        [TestMethod]
        public void ASegmentDeclaringALengthOfZeroIsRefused()
        {
            // A length counts itself, so zero would have the reader step backwards for ever.
            Assert.ThrowsExactly<ArgumentException>(
                () => Parse(new byte[] { 0xFF, 0xD8, 0xFF, 0xDB, 0x00, 0x00, 0x11, 0x22 }));
        }

        [TestMethod]
        public void ASegmentRunningPastTheEndIsRefused()
        {
            // says it carries 0x1000 bytes and carries two
            Assert.ThrowsExactly<ArgumentException>(
                () => Parse(new byte[] { 0xFF, 0xD8, 0xFF, 0xDB, 0x10, 0x00, 0x11, 0x22 }));
        }

        [TestMethod]
        public void ALargeSegmentIsReadAsUnsignedRatherThanNegative()
        {
            // 0x8004 is negative read as a short, which is how this used to be put back together.
            // The image is not long enough to hold it, so it is refused - the point is that it is
            // refused for that reason rather than slicing by a negative length.
            Assert.ThrowsExactly<ArgumentException>(
                () => Parse(new byte[] { 0xFF, 0xD8, 0xFF, 0xDB, 0x80, 0x04, 0x11, 0x22 }));
        }

        [TestMethod]
        public void ATruncatedStartOfFrameIsRefused()
        {
            // SOF0 with its header cut short
            Assert.ThrowsExactly<ArgumentException>(
                () => Parse(new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 0x08 }));
        }

        [TestMethod]
        public void AComponentListRunningPastTheEndIsRefused()
        {
            // SOF0 claiming 200 components, with room for none of them
            var image = new byte[]
            {
                0xFF, 0xD8,
                0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x10, 0x00, 0x10, 0xC8,
            };

            Assert.ThrowsExactly<ArgumentException>(() => Parse(image));
        }

        [TestMethod]
        public void AnImageWithNoStartOfFrameIsRefused()
        {
            // SOI, one well formed comment segment, then it ends
            var image = new byte[] { 0xFF, 0xD8, 0xFF, 0xFE, 0x00, 0x04, 0x11, 0x22 };

            Assert.ThrowsExactly<ArgumentException>(() => Parse(image));
        }

        [TestMethod]
        public void NoneOfTheseTakeTheProducerDownWithSomethingUnexpected()
        {
            // The contract is ArgumentException for a malformed image, and NotSupportedException for
            // one this packetiser cannot carry. An IndexOutOfRangeException is neither, and is what
            // every truncation used to produce.
            var random = new Random(20260919);

            for (int i = 0; i < 400; i++)
            {
                var image = new byte[random.Next(2, 64)];
                random.NextBytes(image);
                image[0] = 0xFF;
                image[1] = 0xD8;

                try
                {
                    Parse(image);
                }
                catch (ArgumentException)
                {
                }
                catch (NotSupportedException)
                {
                }
            }
        }
    }
}
