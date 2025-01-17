using System;
using System.Buffers;

namespace SharpRTSPServer
{
    internal class AdjustedSizeMemoryOwner : IMemoryOwner<byte>
    {
        private readonly IMemoryOwner<byte> _wrapped;

        private AdjustedSizeMemoryOwner(IMemoryOwner<byte> wrapped, int size)
        {
            _wrapped = wrapped;
            Memory = _wrapped.Memory.Slice(0, size);
        }

        public Memory<byte> Memory { get; }

        public void Dispose() => _wrapped.Dispose();

        public static IMemoryOwner<byte> Rent(int size)
        {
            var memoryOwner = MemoryPool<byte>.Shared.Rent(size);

            return memoryOwner.Memory.Length == size
                ? memoryOwner
                : new AdjustedSizeMemoryOwner(memoryOwner, size);
        }
    }
}
