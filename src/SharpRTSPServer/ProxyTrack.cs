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
    public class ProxyTrack : TrackBase
    {
        public override string Codec => "PROXY";

        public override int ID { get; set; }
        public override int PayloadType { get; set; }

        private bool _isReady = false;
        public override bool IsReady
        { 
            get 
            {
                return _isReady;
            } 
        }

        public Uri Uri { get; }

        public ProxyTrack(int id, string uri)
        {
            this.ID = id;
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
                UdpClient udpClient = new UdpClient(uri.Port);
                IPEndPoint remoteEndPoint = new IPEndPoint(IPAddress.Parse(uri.Host), 0);
                _isReady = true;

                while (true)
                {
                    try
                    {
                        byte[] rtp = udpClient.Receive(ref remoteEndPoint);
                        uint rtpTimestamp = RTPPacketUtil.ReadRtpTimestamp(rtp);
                        Sink.FeedInRawRTP(ID, rtpTimestamp, new List<Memory<byte>>() { rtp });
                        //Debug.WriteLine($"This message was sent from {remoteEndPoint.Address} on their port number {remoteEndPoint.Port}");
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine(e.ToString());
                    }
                }
            });
        }
    }
}