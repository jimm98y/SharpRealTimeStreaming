using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace SharpRTSPServer
{
    public class ProxyTrack : TrackBase, IDisposable
    {
        public override string Codec => "PROXY";

        public override int ID { get; set; }
        public override int PayloadType { get; set; }

        private bool _isReady = false;
        private bool _disposedValue;

        public override bool IsReady
        { 
            get 
            {
                return _isReady;
            } 
        }

        public Uri Uri { get; }

        public ProxyTrack(TrackType type)
        {
            this.ID = (int)type;
        }

        public override StringBuilder BuildSDP(StringBuilder sdp)
        {
            throw new NotImplementedException();
        }

        public void Start()
        {
            _isReady = true;
        }

        /// <summary>
        /// Passes already packetized RTP through unchanged. Every sample is forwarded - the caller may
        /// hand in more than one, and dropping the rest would silently lose media.
        /// </summary>
        public override (List<Memory<byte>>, List<IMemoryOwner<byte>>) CreateRtpPackets(List<ReadOnlyMemory<byte>> samples, uint rtpTimestamp)
        {
            if (samples == null)
                throw new ArgumentNullException(nameof(samples));

            List<Memory<byte>> rtpPackets = new List<Memory<byte>>();
            List<IMemoryOwner<byte>> memoryOwners = new List<IMemoryOwner<byte>>();

            foreach (var sample in samples)
            {
                if (sample.Length == 0)
                    continue;

                var owner = MemoryPool<byte>.Shared.Rent(sample.Length);
                memoryOwners.Add(owner);
                var rtpPacket = owner.Memory.Slice(0, sample.Length);
                sample.Span.CopyTo(rtpPacket.Span);
                rtpPackets.Add(rtpPacket);
            }

            return (rtpPackets, memoryOwners);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if(disposing)
                {
                    _isReady = false;
                }

                _disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}