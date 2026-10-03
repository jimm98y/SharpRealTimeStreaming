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

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rtsp.Rtp;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;

namespace SharpRTSPClient
{
    /// <summary>
    /// Puts VP9 frames back together from their packets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The payload descriptor is taken off every packet and the frames between its B and E bits are
    /// joined up again. A picture is handed up when the marker bit ends it, as the frames it held:
    /// one entry per VP9 frame, which is a superframe whole where the sender sent it whole - each
    /// is something a VP9 decoder can be given as it stands.
    /// </para>
    /// <para>
    /// A frame that lost a packet is dropped rather than handed up with a hole in it, and nothing is
    /// handed up before the first key frame, since a decoder can do nothing with what comes before.
    /// After a loss the frames that follow may refer to the one that went; that is the decoder's to
    /// conceal, as it is for every other codec here.
    /// </para>
    /// <see href="https://datatracker.ietf.org/doc/html/rfc9628" />
    /// </remarks>
    internal sealed class VP9Payload : IPayloadProcessor
    {
        /// <summary>
        /// The largest a frame may grow before it is given up as broken.
        /// </summary>
        /// <remarks>
        /// Far beyond any real frame; there so that a sender which never sets the E bit cannot have
        /// this collect for ever.
        /// </remarks>
        private const int MOST_FRAME_BYTES = 16 * 1024 * 1024;

        private readonly ILogger _logger;
        private readonly MemoryPool<byte> _memoryPool;

        private readonly MemoryStream _frame = new MemoryStream();
        private readonly List<ReadOnlyMemory<byte>> _frames = new List<ReadOnlyMemory<byte>>();
        private readonly List<IMemoryOwner<byte>> _owners = new List<IMemoryOwner<byte>>();

        private bool _inFrame;
        private bool _seenKeyFrame;
        private bool _collecting;
        private uint _timestamp;
        private int _lastSequenceNumber = -1;

        public VP9Payload(ILogger<VP9Payload> logger = null, MemoryPool<byte> memoryPool = null)
        {
            _logger = logger as ILogger ?? NullLogger.Instance;
            _memoryPool = memoryPool ?? MemoryPool<byte>.Shared;
        }

        public RawMediaFrame ProcessPacket(RtpPacket packet)
        {
            // A packet missing in the middle of a frame leaves a hole no decoder can read past.
            int sequenceNumber = packet.SequenceNumber;
            if (_lastSequenceNumber >= 0 && sequenceNumber != ((_lastSequenceNumber + 1) & 0xFFFF))
            {
                DropFrame();
            }
            _lastSequenceNumber = sequenceNumber;

            // A new timestamp is a new picture: whatever was left of the last one is never finishing.
            if (_collecting && packet.Timestamp != _timestamp)
            {
                DropPicture();
            }

            if (!_collecting)
            {
                _timestamp = packet.Timestamp;
                _collecting = true;
            }

            ReadOnlySpan<byte> payload = packet.Payload;

            if (!TryReadDescriptor(payload, out Descriptor descriptor))
            {
                _logger.LogDebug("Dropping a VP9 packet whose payload descriptor is malformed");
                DropFrame();
                return Finish(packet);
            }

            if (descriptor.StartOfFrame)
            {
                if (_inFrame)
                {
                    // The E bit of the last frame went missing with the packet that carried it.
                    DropFrame();
                }

                // A decoder can start on a frame that is not predicted from another and belongs to
                // the base layer, and on nothing earlier.
                if (!descriptor.InterPicturePredicted && descriptor.SpatialId == 0)
                {
                    _seenKeyFrame = true;
                }

                _inFrame = _seenKeyFrame;
            }

            if (_inFrame)
            {
                byte[] data = payload.Slice(descriptor.Length).ToArray();
                _frame.Write(data, 0, data.Length);

                if (_frame.Length > MOST_FRAME_BYTES)
                {
                    DropFrame();
                }
                else if (descriptor.EndOfFrame)
                {
                    EndFrame();
                }
            }

            return Finish(packet);
        }

        private RawMediaFrame Finish(RtpPacket packet)
        {
            if (!packet.IsMarker)
            {
                return RawMediaFrame.Empty;
            }

            // The marker ends the picture whatever became of the E bit, so a frame still open here
            // is one that lost its end.
            DropFrame();

            var result = new RawMediaFrame(_frames.ToArray(), _owners.ToArray())
            {
                ClockTimestamp = DateTime.UtcNow,
                RtpTimestamp = _timestamp,
            };

            // handed over whole, and the owners with it - it disposes them
            _frames.Clear();
            _owners.Clear();
            _collecting = false;

            return result;
        }

        private void EndFrame()
        {
            int length = (int)_frame.Length;
            IMemoryOwner<byte> owner = _memoryPool.Rent(length);
            Memory<byte> frame = owner.Memory.Slice(0, length);
            _frame.GetBuffer().AsSpan(0, length).CopyTo(frame.Span);

            _owners.Add(owner);
            _frames.Add(frame);

            _frame.SetLength(0);
            _inFrame = false;
        }

        private void DropFrame()
        {
            _frame.SetLength(0);
            _inFrame = false;
        }

        private void DropPicture()
        {
            DropFrame();

            foreach (IMemoryOwner<byte> owner in _owners)
            {
                owner.Dispose();
            }

            _owners.Clear();
            _frames.Clear();
            _collecting = false;
        }

        internal readonly struct Descriptor
        {
            public int Length { get; }
            public bool InterPicturePredicted { get; }
            public bool StartOfFrame { get; }
            public bool EndOfFrame { get; }
            public int SpatialId { get; }

            public Descriptor(int length, bool interPicturePredicted, bool startOfFrame, bool endOfFrame, int spatialId)
            {
                Length = length;
                InterPicturePredicted = interPicturePredicted;
                StartOfFrame = startOfFrame;
                EndOfFrame = endOfFrame;
                SpatialId = spatialId;
            }
        }

        /// <summary>
        /// Reads the payload descriptor, in either mode, and says how long it is.
        /// </summary>
        /// <remarks>
        ///  0 1 2 3 4 5 6 7
        /// +-+-+-+-+-+-+-+-+
        /// |I|P|L|F|B|E|V|Z|
        /// +-+-+-+-+-+-+-+-+
        /// I:   |M| PICTURE ID  | and the extended PID if M
        /// L:   | TID |U| SID |D| and TL0PICIDX in the non-flexible mode
        /// P,F: | P_DIFF      |N| up to 3 times
        /// V:   | SS            |
        /// </remarks>
        internal static bool TryReadDescriptor(ReadOnlySpan<byte> payload, out Descriptor descriptor)
        {
            descriptor = default;

            if (payload.Length < 1)
                return false;

            int first = payload[0];
            bool i = (first & 0x80) != 0;
            bool p = (first & 0x40) != 0;
            bool l = (first & 0x20) != 0;
            bool f = (first & 0x10) != 0;
            bool b = (first & 0x08) != 0;
            bool e = (first & 0x04) != 0;
            bool v = (first & 0x02) != 0;

            int at = 1;
            int spatialId = 0;

            if (i)
            {
                if (at >= payload.Length)
                    return false;

                at += (payload[at] & 0x80) != 0 ? 2 : 1;
            }

            if (l)
            {
                if (at >= payload.Length)
                    return false;

                spatialId = (payload[at] >> 1) & 0x07;
                at += f ? 1 : 2;
            }

            if (f && p)
            {
                for (int diff = 0; ; diff++)
                {
                    if (at >= payload.Length || diff == 3)
                        return false;

                    bool more = (payload[at++] & 0x01) != 0;
                    if (!more)
                        break;
                }
            }

            if (v)
            {
                if (at >= payload.Length)
                    return false;

                int ss = payload[at++];
                int spatialLayers = (ss >> 5) + 1;
                bool y = (ss & 0x10) != 0;
                bool g = (ss & 0x08) != 0;

                if (y)
                {
                    at += 4 * spatialLayers;
                }

                if (g)
                {
                    if (at >= payload.Length)
                        return false;

                    int pictures = payload[at++];
                    for (int pg = 0; pg < pictures; pg++)
                    {
                        if (at >= payload.Length)
                            return false;

                        int references = (payload[at++] >> 2) & 0x03;
                        at += references;
                    }
                }
            }

            if (at > payload.Length)
                return false;

            descriptor = new Descriptor(at, p, b, e, spatialId);
            return true;
        }
    }
}
