using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using static System.Net.Mime.MediaTypeNames;

namespace SharpRTSPServer
{
    /// <summary>
    /// Represents a segment of a <see cref="ReadOnlySequenceSegment{testc}"/> of <see langword="byte"/> backed by a <see cref="IMemoryOwner{testc}"/> of <see langword="byte"/>.
    /// </summary>
    public sealed class BufferSegment : ReadOnlySequenceSegment<byte>, IDisposable
    {
        public static readonly BufferSegment Empty = new BufferSegment();
        private readonly IMemoryOwner<byte> _owner;

        private BufferSegment()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="BufferSegment"/> class.
        /// </summary>
        /// <param name="owner">The memory owner that provides the memory for the segment.</param>
        /// <param name="size">The size of the memory slice to use for the segment.</param>
        public BufferSegment(IMemoryOwner<byte> owner, int size)
        {
            Memory = owner.Memory.Slice(0, size);
            _owner = owner;
        }

        /// <summary>
        /// Appends a new segment to the current segment.
        /// </summary>
        /// <param name="owner">The memory owner that provides the memory for the new segment.</param>
        /// <param name="size">The size of the memory slice to use for the new segment.</param>
        /// <returns>The newly created <see cref="BufferSegment"/>.</returns>
        public BufferSegment Append(IMemoryOwner<byte> owner, int size)
        {
            var segment = new BufferSegment(owner, size);
            segment.RunningIndex = RunningIndex + Memory.Length;
            Next = segment;
            return segment;
        }

        /// <summary>
        /// Releases the resources used by the <see cref="BufferSegment"/>.
        /// </summary>
        public void Dispose()
        {
            _owner?.Dispose();
        }
    }
}
