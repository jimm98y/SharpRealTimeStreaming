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

        public override List<IMemoryOwner<byte>> CreateRtpPackets(ReadOnlySequence<byte> samples, uint rtpTimestamp)
        {
            List<IMemoryOwner<byte>> memoryOwners = new List<IMemoryOwner<byte>>(1);
            var owner = AdjustedSizeMemoryOwner.Rent(samples.First.Length);
            memoryOwners.Add(owner);
            var rtpPacket = owner.Memory;
            samples.First.CopyTo(rtpPacket);
            return memoryOwners;
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