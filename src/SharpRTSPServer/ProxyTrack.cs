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

        public override IByteBuffer CreateRtpPackets(ReadOnlySequence<byte> samples, uint rtpTimestamp)
        {
            int length = (int)samples.Length;
            var byteBuffer = new PooledByteBuffer(length);
            samples.CopyTo(byteBuffer.GetSpan(length));
            byteBuffer.Advance(length);
            return byteBuffer;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
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