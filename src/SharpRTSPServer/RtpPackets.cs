using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace SharpRTSPServer
{
    /// <summary>
    /// The packets of one frame, and the memory holding them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A track asks for each packet by length, fills it in, and never sees a buffer or a pool. The
    /// whole lot is handed to the sink, which releases it once the last client has been sent the
    /// frame - so the memory outlives the call that made it, which is what lets a frame be sent
    /// without being copied.
    /// </para>
    /// <para>
    /// This replaced a pair of lists and an owner object per packet, all made fresh for every frame
    /// of every stream and thrown away milliseconds later. The lists are part of this and are reused
    /// with it, keeping whatever capacity they grew to, because the next frame of a stream is the
    /// same shape as the last.
    /// </para>
    /// </remarks>
    public sealed class RtpPackets : IReadOnlyList<Memory<byte>>
    {
        /// <summary>
        /// Where the buffers come from.
        /// </summary>
        /// <remarks>
        /// Not the shared pool. These are rented by whichever thread produced the frame and given
        /// back by whichever wrote it, and the shared pool keeps a small cache per thread - so a
        /// buffer handed back by a writer never reaches the producer that wants one, and the producer
        /// allocates instead. A pool without that cache costs a lock and gives back what it was given.
        /// </remarks>
        /// <remarks>
        /// Deeper than the default of fifty buffers a size, because the buffers of every frame still
        /// queued for a client are out on loan at once - a client that is a little behind, times the
        /// packets in a frame, is past fifty without trying. Past the depth the pool simply allocates,
        /// which is what it was quietly doing.
        /// </remarks>
        private static readonly ArrayPool<byte> Buffers = ArrayPool<byte>.Create(MOST_BYTES, MOST_BUFFERS_OF_A_SIZE);

        /// <summary>The largest packet worth pooling. RTP over a sane network is nowhere near this.</summary>
        private const int MOST_BYTES = 1024 * 1024;

        private const int MOST_BUFFERS_OF_A_SIZE = 1024;

        /// <remarks>
        /// A plain stack under a lock, not a concurrent bag. A bag keeps a list per thread and is at
        /// its best when the same thread puts things in and takes them out; here one thread always
        /// puts and another always takes, so every take was a steal from another thread's list -
        /// which locks anyway, and allocates on the way.
        /// </remarks>
        private static readonly Stack<RtpPackets> Spare = new Stack<RtpPackets>();

        private static readonly object SpareLock = new object();

        /// <summary>
        /// How many are kept for reuse. A burst must not leave a pool the size of the burst behind
        /// it; past this they are simply made and dropped, as they all were before.
        /// </summary>
        private const int MOST_SPARE = 64;

        private readonly List<byte[]> _rented = new List<byte[]>();

        /// <summary>
        /// Whether these have been given back, so that giving them back twice cannot happen.
        /// </summary>
        /// <remarks>
        /// The worst thing that could go wrong here. Returning a buffer to the pool twice has it
        /// lent to two callers at once, who then write over each other - media that is subtly wrong,
        /// in a way that looks like a fault in something else entirely.
        /// </remarks>
        private int _released;

        /// <summary>
        /// The packets themselves. Held as the concrete list so that whatever sends them can walk it
        /// by index without an enumerator.
        /// </summary>
        internal readonly List<Memory<byte>> Items = new List<Memory<byte>>();

        /// <summary>
        /// How many packets this frame came to.
        /// </summary>
        public int Count => Items.Count;

        /// <summary>
        /// One of them.
        /// </summary>
        public Memory<byte> this[int index] => Items[index];

        /// <summary>
        /// Somewhere to build the next packet of this frame, of exactly this length.
        /// </summary>
        /// <remarks>
        /// The memory belongs to the frame from here on. It stays valid until the frame has been
        /// sent, and a track must not hold on to it past the call it was asked for in.
        /// </remarks>
        public Memory<byte> Rent(int length)
        {
            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length), length, "A packet is not shorter than nothing.");
            }

            byte[] buffer = Buffers.Rent(length);
            _rented.Add(buffer);

            // Sliced to what was asked for: the pool hands back something at least that big, and the
            // extra at the end is nobody's business.
            var packet = new Memory<byte>(buffer, 0, length);
            Items.Add(packet);

            return packet;
        }

        /// <summary>
        /// Working memory for building a packet, which is not itself a packet.
        /// </summary>
        /// <remarks>
        /// Some formats have to take a copy of a sample and edit it - AV1 strips the size field out
        /// of an OBU and clears the bit that said it was there - and that copy is not something to
        /// send. It comes from the same pool and goes back with everything else, so a track cannot
        /// leak it by taking one of the ways out of a loop that does not pass the end.
        /// </remarks>
        public Memory<byte> RentScratch(int length)
        {
            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length), length, "Working memory is not shorter than nothing.");
            }

            byte[] buffer = Buffers.Rent(length);
            _rented.Add(buffer);

            return new Memory<byte>(buffer, 0, length);
        }

        public IEnumerator<Memory<byte>> GetEnumerator() => Items.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => Items.GetEnumerator();

        /// <summary>
        /// An empty one to fill in, reused if there is one going spare.
        /// </summary>
        public static RtpPackets Take()
        {
            lock (SpareLock)
            {
                if (Spare.Count > 0)
                {
                    RtpPackets spare = Spare.Pop();
                    spare._released = 0;
                    return spare;
                }
            }

            return new RtpPackets();
        }

        /// <summary>
        /// Gives the memory back, and this with it.
        /// </summary>
        /// <remarks>
        /// Called by whatever took the frame over, once the last client has been sent it. Calling it
        /// twice would hand the same buffers back twice and they would then be lent to two people at
        /// once, so whoever owns the frame owns this too.
        /// </remarks>
        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            for (int i = 0; i < _rented.Count; i++)
            {
                Buffers.Return(_rented[i]);
            }

            _rented.Clear();
            Items.Clear();

            lock (SpareLock)
            {
                if (Spare.Count < MOST_SPARE)
                {
                    Spare.Push(this);
                }
            }
        }
    }
}
