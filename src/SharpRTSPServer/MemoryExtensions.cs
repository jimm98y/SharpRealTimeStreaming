﻿using System;
using System.Buffers;

namespace SharpRTSPServer
{
    internal static class MemoryExtensions
    {
        public static ReadOnlySpan<byte> GetFirstSpan(this ReadOnlySequence<byte> sequence)
#if NET8_0_OR_GREATER
            => sequence.FirstSpan;
#else
            => sequence.GetFirstSpan();
#endif
    }
}
