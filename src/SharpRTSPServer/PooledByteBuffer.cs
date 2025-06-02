using System;
using System.Buffers;
using System.Collections.Generic;

namespace SharpRTSPServer
{
    /// <summary>
    /// A pooled buffer writer that implements <see cref="IBufferWriter{Byte}"/> using <see cref="ArrayPool{Byte}.Shared"/> for efficient writing of byte data and
    /// allows reading the written content through a <see cref="ReadOnlySequence{Byte}"/> using the <see cref="GetReadOnlySequence"/> method.
    /// </summary>
    public sealed class PooledByteBuffer : IByteBuffer
    {
        private const int DefaultBufferSize = 4096;

        private readonly List<byte[]> _buffers = new List<byte[]>();
        private readonly ArrayPool<byte> _pool;
        private int _currentIndex;
        private int _currentOffset;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="PooledByteBuffer"/> class with an optional initial buffer size and array pool.
        /// </summary>
        /// <param name="initialBufferSize">The initial size of the buffer to rent from the pool. Defaults to 4096 bytes.</param>
        /// <param name="pool">The array pool to use. If null, <see cref="ArrayPool{Byte}.Shared"/> is used.</param>
        public PooledByteBuffer(int initialBufferSize = DefaultBufferSize, ArrayPool<byte> pool = null)
        {
            _pool = pool ?? ArrayPool<byte>.Shared;
            AddNewBuffer(initialBufferSize);
        }

        /// <summary>
        /// Notifies the buffer writer that <paramref name="count"/> bytes were written.
        /// </summary>
        /// <param name="count">The number of bytes written.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if count is negative or exceeds the current buffer capacity.</exception>
        public void Advance(int count)
        {
            if (count < 0 || _currentOffset + count > _buffers[_currentIndex].Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            _currentOffset += count;
        }

        /// <summary>
        /// Returns a <see cref="Memory{Byte}"/> buffer to write to, ensuring at least <paramref name="sizeHint"/> bytes are available.
        /// </summary>
        /// <param name="sizeHint">The minimum number of bytes required. May be 0.</param>
        /// <returns>A writable memory buffer.</returns>
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffers[_currentIndex].AsMemory(_currentOffset);
        }

        /// <summary>
        /// Returns a <see cref="Span{Byte}"/> buffer to write to, ensuring at least <paramref name="sizeHint"/> bytes are available.
        /// </summary>
        /// <param name="sizeHint">The minimum number of bytes required. May be 0.</param>
        /// <returns>A writable span buffer.</returns>
        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffers[_currentIndex].AsSpan(_currentOffset);
        }

        /// <summary>
        /// Returns a <see cref="ReadOnlySequence{Byte}"/> representing the written data across all buffers.
        /// </summary>
        /// <returns>A read-only sequence of bytes.</returns>
        public ReadOnlySequence<byte> GetReadOnlySequence()
        {
            SequenceSegment first = null;
            SequenceSegment last = null;

            for (var i = 0; i < _buffers.Count; i++)
            {
                var buffer = _buffers[i];
                var length = (i == _currentIndex) ? _currentOffset : buffer.Length;

                if (length == 0)
                {
                    continue;
                }

                var segment = new SequenceSegment(buffer.AsMemory(0, length));

                if (first == null)
                {
                    first = segment;
                }

                if (last != null)
                {
                    last.SetNext(segment);
                }

                last = segment;
            }

            if (first == null || last == null)
            {
                return ReadOnlySequence<byte>.Empty;
            }

            return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
        }

        /// <summary>
        /// Releases all buffers back to the pool and clears internal state.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            foreach (var buffer in _buffers)
            {
                _pool.Return(buffer);
            }

            _buffers.Clear();
            _disposed = true;
        }

        private void EnsureCapacity(int sizeHint)
        {
            if (_currentOffset + sizeHint > _buffers[_currentIndex].Length)
            {
                var newSize = Math.Max(sizeHint, DefaultBufferSize);
                AddNewBuffer(newSize);
            }
        }

        private void AddNewBuffer(int size)
        {
            var buffer = _pool.Rent(size);
            _buffers.Add(buffer);
            _currentIndex = _buffers.Count - 1;
            _currentOffset = 0;
        }

        private class SequenceSegment : ReadOnlySequenceSegment<byte>
        {
            public SequenceSegment(ReadOnlyMemory<byte> memory)
            {
                Memory = memory;
            }

            public void SetNext(SequenceSegment next)
            {
                Next = next;
                next.RunningIndex = RunningIndex + Memory.Length;
            }
        }
    }
}
