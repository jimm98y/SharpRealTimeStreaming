// SharpRTSPServer
// Copyright (C) 2026 Lukas Volf
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using Microsoft.Extensions.Logging;
using Rtsp;
using Rtsp.Messages;
using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SharpRTSPServer
{
    /// <summary>
    /// A pair of UDP sockets carrying one session's RTP and RTCP, in either address family.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The transport library provides one of these, and it opens its sockets in the IPv4 family with
    /// no way to ask for anything else - so a client reached over IPv6 could hold an RTSP
    /// conversation and then had nowhere for its media to go. This is the same thing with the family
    /// chosen by the caller, which is all that was in the way.
    /// </para>
    /// <para>
    /// It is an <see cref="IRtpTransport"/> like any other, so nothing else in the server has to know
    /// which of the two it is holding.
    /// </para>
    /// </remarks>
    public sealed class RtpUdpTransport : IRtpTransport
    {
        /// <summary>As much as arrives in one datagram, which cannot exceed this.</summary>
        private const int RECEIVE_BUFFER = 65536;

        private readonly UdpClient _dataSocket;
        private readonly UdpClient _controlSocket;
        private readonly CancellationTokenSource _stopping = new CancellationTokenSource();
        private readonly ILogger _logger;

        private Task _dataReader;
        private Task _controlReader;
        private IPEndPoint _dataEndPoint;
        private IPEndPoint _controlEndPoint;
        private bool _disposed;

        public int DataPort { get; private set; }

        public int ControlPort { get; private set; }

        /// <summary>The family the sockets were opened in, and so the only one they can send to.</summary>
        public AddressFamily AddressFamily { get; }

        /// <summary>
        /// How far media sent to a multicast group is allowed to travel, in routed hops.
        /// </summary>
        /// <remarks>
        /// One keeps it on the local link, which is where a group nobody has arranged routing for
        /// belongs. Set it before the destination, since that is when it is applied.
        /// </remarks>
        public int MulticastTimeToLive { get; set; } = 1;

        public event EventHandler<RtspDataEventArgs> DataReceived;

        public event EventHandler<RtspDataEventArgs> ControlReceived;

        /// <summary>
        /// Opens a consecutive pair of ports within the given range, RTP on the first of them.
        /// </summary>
        /// <remarks>
        /// The scan starts where it is told to and moves up in twos, so that RTP lands on the port
        /// named in the reply to SETUP and RTCP on the one after it, which is what a client expects.
        /// </remarks>
        public RtpUdpTransport(int startPort, int endPort, AddressFamily addressFamily, ILogger logger = null)
        {
            if (addressFamily != AddressFamily.InterNetwork && addressFamily != AddressFamily.InterNetworkV6)
            {
                throw new ArgumentOutOfRangeException(nameof(addressFamily), addressFamily,
                    "RTP goes over IPv4 or IPv6.");
            }

            AddressFamily = addressFamily;
            _logger = logger;

            IPAddress any = addressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;

            for (int port = startPort; port + 1 < endPort; port += 2)
            {
                UdpClient data = null;
                UdpClient control = null;

                try
                {
                    data = new UdpClient(new IPEndPoint(any, port));
                    control = new UdpClient(new IPEndPoint(any, port + 1));
                }
                catch (SocketException)
                {
                    // One of the pair is taken. Both go back, because half a pair is no use, and the
                    // scan moves on rather than giving up - the range is shared with every other
                    // session on this server.
                    data?.Close();
                    control?.Close();
                    continue;
                }

                _dataSocket = data;
                _controlSocket = control;
                DataPort = port;
                ControlPort = port + 1;

                _dataSocket.Client.ReceiveBufferSize = 100 * 1024;

                // The default of 8192 is smaller than a fragmented frame, and a send that does not
                // fit in the buffer waits for the one before it to drain.
                _dataSocket.Client.SendBufferSize = 65535;

                return;
            }

            throw new InvalidOperationException(
                $"No free pair of UDP ports between {startPort} and {endPort}.");
        }

        /// <summary>
        /// Where to send this session's RTP.
        /// </summary>
        public void SetDataDestination(string hostname, int port)
        {
            _dataEndPoint = Resolve(hostname, port);
            ApplyMulticast(_dataSocket, _dataEndPoint);
        }

        /// <summary>
        /// Where to send this session's RTCP.
        /// </summary>
        public void SetControlDestination(string hostname, int port)
        {
            _controlEndPoint = Resolve(hostname, port);
            ApplyMulticast(_controlSocket, _controlEndPoint);
        }

        /// <summary>
        /// Whether packets arriving from somewhere other than where this session's media is sent are
        /// discarded. On by default.
        /// </summary>
        /// <remarks>
        /// <para>
        /// These ports are open to anything that can reach them, and what arrives on them is taken
        /// as the client's: it refreshes the session's keepalive and its reception reports are
        /// believed. The range is a few hundred well known ports, so without this an off-path
        /// attacker who can guess one can hold a session open past the idle sweep for as long as it
        /// likes, and feed whatever loss and jitter figures it chooses to anything acting on them.
        /// </para>
        /// <para>
        /// The address is matched, not the port: RFC 3550 lets a receiver send its reports from a
        /// port of its own choosing, and many do. It is not a defence against an attacker who can
        /// spoof the client's source address - nothing short of SRTP is - but it does mean one has
        /// to.
        /// </para>
        /// </remarks>
        public bool FilterBySourceAddress { get; set; } = true;

        /// <summary>
        /// Whether a datagram from this address is one this transport should act on.
        /// </summary>
        private bool IsExpectedSource(EndPoint from, IPEndPoint destination)
        {
            if (!FilterBySourceAddress || destination == null)
            {
                return true;
            }

            // A group is sent to, never received from, so nothing arriving is the group's.
            if (IsMulticast(destination.Address))
            {
                return true;
            }

            if (!(from is IPEndPoint source))
            {
                return true;
            }

            return SameHost(source.Address, destination.Address);
        }

        /// <summary>
        /// Whether two addresses name the same machine, allowing for the two ways an IPv4 one can be
        /// written when a dual mode socket is in play.
        /// </summary>
        internal static bool SameHost(IPAddress left, IPAddress right)
        {
            if (left == null || right == null)
            {
                return false;
            }

            if (left.IsIPv4MappedToIPv6)
            {
                left = left.MapToIPv4();
            }

            if (right.IsIPv4MappedToIPv6)
            {
                right = right.MapToIPv4();
            }

            return left.Equals(right);
        }

        /// <summary>
        /// Turns a destination into one of these sockets can actually send to.
        /// </summary>
        /// <remarks>
        /// A name can hold addresses of both families and a socket can only send to its own, so the
        /// one that matches is the one to take rather than whichever came back first.
        /// </remarks>
        private IPEndPoint Resolve(string hostname, int port)
        {
            if (string.IsNullOrEmpty(hostname))
            {
                throw new ArgumentException("There is no address to send to.", nameof(hostname));
            }

            if (IPAddress.TryParse(hostname, out IPAddress literal))
            {
                return new IPEndPoint(Match(literal, hostname), port);
            }

            IPAddress[] resolved = Dns.GetHostAddresses(hostname);

            foreach (IPAddress address in resolved)
            {
                if (address.AddressFamily == AddressFamily)
                {
                    return new IPEndPoint(address, port);
                }
            }

            foreach (IPAddress address in resolved)
            {
                if (CanBeReached(address))
                {
                    return new IPEndPoint(Match(address, hostname), port);
                }
            }

            throw new ArgumentException(
                $"No {AddressFamily} address for '{hostname}'.", nameof(hostname));
        }

        /// <summary>
        /// The same address in the shape these sockets can send to, or a complaint if there is none.
        /// </summary>
        private IPAddress Match(IPAddress address, string hostname)
        {
            if (address.AddressFamily == AddressFamily)
            {
                return address;
            }

            // An IPv4 address is reachable from an IPv6 socket in its mapped form, which is how a
            // client that connected over IPv4 to a socket listening on both is reported.
            if (AddressFamily == AddressFamily.InterNetworkV6 &&
                address.AddressFamily == AddressFamily.InterNetwork)
            {
                return address.MapToIPv6();
            }

            if (AddressFamily == AddressFamily.InterNetwork && address.IsIPv4MappedToIPv6)
            {
                return address.MapToIPv4();
            }

            throw new ArgumentException(
                $"'{hostname}' cannot be reached from a {AddressFamily} socket.", nameof(hostname));
        }

        private bool CanBeReached(IPAddress address)
        {
            if (address.AddressFamily == AddressFamily)
            {
                return true;
            }

            return AddressFamily == AddressFamily.InterNetworkV6
                ? address.AddressFamily == AddressFamily.InterNetwork
                : address.IsIPv4MappedToIPv6;
        }

        /// <summary>
        /// Whether an address names a group rather than one machine.
        /// </summary>
        public static bool IsMulticast(IPAddress address)
        {
            if (address == null)
            {
                return false;
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return address.IsIPv6Multicast;
            }

            // 224.0.0.0 to 239.255.255.255
            byte first = address.GetAddressBytes()[0];
            return first >= 224 && first <= 239;
        }

        /// <summary>
        /// Sets the hop limit on a socket that is about to send to a group.
        /// </summary>
        /// <remarks>
        /// The default is one hop on most systems, but it is not worth relying on when the whole
        /// point of the setting is to decide how far the media travels.
        /// </remarks>
        private void ApplyMulticast(UdpClient socket, IPEndPoint destination)
        {
            if (!IsMulticast(destination.Address))
            {
                return;
            }

            try
            {
                if (AddressFamily == AddressFamily.InterNetworkV6)
                {
                    socket.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive,
                        MulticastTimeToLive);
                }
                else
                {
                    socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive,
                        MulticastTimeToLive);
                }
            }
            catch (SocketException ex)
            {
                _logger?.LogWarning(ex, "Could not set the multicast hop limit to {ttl}", MulticastTimeToLive);
            }
        }

        public void Start()
        {
            if (_dataReader != null)
            {
                throw new InvalidOperationException("This transport has already been started.");
            }

            // The destination is read per datagram rather than captured, because SETUP sets it after
            // the transport is built and a repeated SETUP may move it.
            _dataReader = Read(_dataSocket, DataPort, () => _dataEndPoint, args => DataReceived?.Invoke(this, args));
            _controlReader = Read(_controlSocket, ControlPort, () => _controlEndPoint, args => ControlReceived?.Invoke(this, args));
        }

        /// <summary>
        /// Hands on whatever arrives, until the socket is closed.
        /// </summary>
        private Task Read(UdpClient socket, int port, Func<IPEndPoint> destination, Action<RtspDataEventArgs> handler)
        {
            // A thread of its own rather than a pooled one: it spends its life waiting on a socket.
            return Task.Factory.StartNew(() =>
            {
                byte[] buffer = new byte[RECEIVE_BUFFER];
                EndPoint from = new IPEndPoint(
                    AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);

                while (!_stopping.IsCancellationRequested)
                {
                    int size;

                    try
                    {
                        size = socket.Client.ReceiveFrom(buffer, ref from);
                    }
                    catch (Exception ex) when (ex is SocketException || ex is ObjectDisposedException)
                    {
                        // the ordinary way this ends: the socket was closed underneath the read
                        return;
                    }

                    if (size <= 0)
                    {
                        continue;
                    }

                    // Before anything is made of it. What arrives here is taken as the client's, and
                    // this port is reachable by anything that can route to it.
                    if (!IsExpectedSource(from, destination()))
                    {
                        _logger?.LogDebug("Discarding a datagram on UDP port {port} from {from}, which is not where this session's media goes",
                            port, from);
                        continue;
                    }

                    // Copied out of the buffer this loop reuses, and owned by whoever is handed it.
                    IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(size);

                    try
                    {
                        buffer.AsSpan(0, size).CopyTo(owner.Memory.Span);

                        handler(new RtspDataEventArgs(new RtspData(owner, size) { Channel = port }));
                    }
                    catch (Exception ex)
                    {
                        // Nothing that goes wrong downstream should stop this loop, and nothing that
                        // escapes it would be caught anywhere - this is a thread of our own.
                        owner.Dispose();
                        _logger?.LogDebug(ex, "Error handling a packet on UDP port {port}", port);
                    }
                }
            }, TaskCreationOptions.LongRunning);
        }

        public void Stop()
        {
            _stopping.Cancel();

            // Closing is what ends the reads, which are sitting in a receive that nothing else will
            // wake.
            Close(_dataSocket);
            Close(_controlSocket);
        }

        private void Close(UdpClient socket)
        {
            try
            {
                socket?.Close();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Error closing a UDP socket");
            }
        }

        public void WriteToDataPort(ReadOnlySpan<byte> data)
        {
            Send(_dataSocket, _dataEndPoint, data);
        }

        public void WriteToControlPort(ReadOnlySpan<byte> data)
        {
            Send(_controlSocket, _controlEndPoint, data);
        }

        private static void Send(UdpClient socket, IPEndPoint destination, ReadOnlySpan<byte> data)
        {
            if (destination == null)
            {
                throw new InvalidOperationException("Nothing has said where to send this yet.");
            }

#if NET8_0_OR_GREATER
            socket.Client.SendTo(data, SocketFlags.None, destination);
#else
            // no span overload before .NET 8, and the array has to be real for the older targets
            byte[] copy = data.ToArray();
            socket.Client.SendTo(copy, 0, copy.Length, SocketFlags.None, destination);
#endif
        }

        public Task WriteToDataPortAsync(ReadOnlyMemory<byte> data)
        {
            WriteToDataPort(data.Span);
            return Task.CompletedTask;
        }

        public Task WriteToControlPortAsync(ReadOnlyMemory<byte> data)
        {
            WriteToControlPort(data.Span);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            Stop();

            _dataSocket?.Dispose();
            _controlSocket?.Dispose();
            _stopping.Dispose();
        }
    }
}
