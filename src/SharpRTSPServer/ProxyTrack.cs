using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

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

        public ProxyTrack(TrackType type, string uri)
        {
            this.ID = (int)type;
            this.Uri = new Uri(uri, UriKind.Absolute);
            Connect(this.Uri);
        }

        public override StringBuilder BuildSDP(StringBuilder sdp)
        {
            throw new NotImplementedException();
        }

        public override (List<Memory<byte>>, List<IMemoryOwner<byte>>) CreateRtpPackets(List<byte[]> samples, uint rtpTimestamp)
        {
            throw new NotImplementedException();
        }

        public override void FeedInRawSamples(uint rtpTimestamp, List<byte[]> samples)
        {
            throw new InvalidOperationException("Proxy cannot feed in any samples!");
        }

        private async void Connect(Uri uri)
        {
            await Task.Run(() =>
            {
                try
                {
                    using (UdpClient udpClient = new UdpClient(uri.Port))
                    {
                        IPEndPoint remoteEndPoint = new IPEndPoint(IPAddress.Parse(uri.Host), 0);
                        _isReady = true;

                        while (_isReady)
                        {
                            try
                            {
                                byte[] rtp = udpClient.Receive(ref remoteEndPoint);
                                uint rtpTimestamp = RTPPacketUtil.ReadTS(rtp);
                                Sink.FeedInRawRTP(ID, rtpTimestamp, new List<Memory<byte>>() { rtp });
                            }
                            catch (Exception e)
                            {
                                Debug.WriteLine(e.ToString());
                            }
                        }
                    }
                }
                catch(Exception ee)
                {
                    Debug.WriteLine(ee.ToString());
                    _isReady = false;
                }
            });
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