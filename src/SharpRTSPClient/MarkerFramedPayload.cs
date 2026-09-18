using Rtsp.Rtp;
using System;
using System.Buffers;
using System.Collections.Generic;

namespace SharpRTSPClient
{
    /// <summary>
    /// Collects packets until the one that ends the thing they carry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For payloads that have no framing of their own - metadata documents are the case this was
    /// written for - where the only thing saying where one ends is the marker bit on its last packet.
    /// Handing each packet up as it arrives, which is what a raw reader does, gives whoever is
    /// listening a document in pieces and no way of knowing it is not whole.
    /// </para>
    /// <para>
    /// A run that never ends is abandoned rather than collected for ever: a lost final packet would
    /// otherwise leave the pieces of one document to be joined onto the next.
    /// </para>
    /// </remarks>
    internal sealed class MarkerFramedPayload : IPayloadProcessor
    {
        /// <summary>
        /// How many packets one thing may be spread over before the run is given up as broken.
        /// </summary>
        /// <remarks>
        /// Generous: a metadata document of a megabyte is already far past reasonable, and this only
        /// has to be small enough that a marker which never comes cannot be collected indefinitely.
        /// </remarks>
        private const int MOST_PACKETS = 1024;

        private readonly List<ReadOnlyMemory<byte>> _pieces = new List<ReadOnlyMemory<byte>>();
        private readonly List<IMemoryOwner<byte>> _owners = new List<IMemoryOwner<byte>>();

        private uint _timestamp;
        private bool _collecting;

        public RawMediaFrame ProcessPacket(RtpPacket packet)
        {
            // A new timestamp means whatever was being collected is never going to be finished, so
            // the pieces of it go rather than being joined to what follows.
            if (_collecting && packet.Timestamp != _timestamp)
            {
                Discard();
            }

            if (!_collecting)
            {
                _timestamp = packet.Timestamp;
                _collecting = true;
            }

            IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(packet.PayloadSize);
            Memory<byte> piece = owner.Memory.Slice(0, packet.PayloadSize);
            packet.Payload.CopyTo(piece.Span);

            _owners.Add(owner);
            _pieces.Add(piece);

            if (!packet.IsMarker)
            {
                if (_pieces.Count >= MOST_PACKETS)
                {
                    Discard();
                }

                return Nothing(packet.Timestamp);
            }

            var frame = new RawMediaFrame(_pieces.ToArray(), _owners.ToArray())
            {
                ClockTimestamp = DateTime.UtcNow,
                RtpTimestamp = _timestamp,
            };

            // handed over whole, and the owners with it - it disposes them
            _pieces.Clear();
            _owners.Clear();
            _collecting = false;

            return frame;
        }

        private static RawMediaFrame Nothing(uint timestamp) =>
            new RawMediaFrame(Array.Empty<ReadOnlyMemory<byte>>(), Array.Empty<IMemoryOwner<byte>>())
            {
                ClockTimestamp = DateTime.UtcNow,
                RtpTimestamp = timestamp,
            };

        private void Discard()
        {
            foreach (IMemoryOwner<byte> owner in _owners)
            {
                owner.Dispose();
            }

            _owners.Clear();
            _pieces.Clear();
            _collecting = false;
        }
    }
}
