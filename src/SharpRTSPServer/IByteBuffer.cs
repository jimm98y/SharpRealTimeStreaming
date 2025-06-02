using System;
using System.Buffers;

namespace SharpRTSPServer
{
    public interface IByteBuffer : IBufferWriter<byte>, IDisposable
    {
        ReadOnlySequence<byte> GetReadOnlySequence();
    }
}