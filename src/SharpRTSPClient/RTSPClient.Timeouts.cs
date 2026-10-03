// SharpRTSPClient
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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace SharpRTSPClient
{
    /// <summary>
    /// Connection, response and media timeouts, and noticing the RTSP connection has gone.
    /// </summary>
    /// <remarks>
    /// Every timeout is off by default, which is how the client always behaved: a server that never
    /// answered left it waiting for good. When one expires the session is torn down and
    /// <see cref="Stopped"/> says which it was, so the caller can decide whether to reconnect.
    /// </remarks>
    public partial class RTSPClient
    {
        /// <summary>
        /// How long <see cref="Connect(Uri, RTPTransport, System.Net.NetworkCredential, bool, System.Net.Security.RemoteCertificateValidationCallback, bool)"/>
        /// waits for the TCP connection, and for rtsps the TLS handshake as well.
        /// <see cref="Timeout.InfiniteTimeSpan"/> (the default) leaves it to the operating system.
        /// </summary>
        /// <remarks>
        /// Applies to rtsp:// and rtsps://. RTSP tunnelled over http(s) connects inside SharpRTSP and
        /// is not covered, nor are the reconnects SharpRTSP makes on its own when auto reconnect is
        /// on. On expiry <see cref="Stopped"/> reports <see cref="StoppedReason.ConnectTimeout"/>.
        /// </remarks>
        public TimeSpan ConnectTimeout { get; set; } = Timeout.InfiniteTimeSpan;

        /// <summary>
        /// How long any request - OPTIONS, DESCRIBE, SETUP, PLAY, PAUSE, keepalives - may go
        /// unanswered. <see cref="Timeout.InfiniteTimeSpan"/> (the default) waits for ever.
        /// </summary>
        /// <remarks>
        /// Also used as the socket's send timeout, so a write to a server that stopped reading
        /// cannot block for ever either - of the connection made by <see cref="Connect(Uri, RTPTransport, System.Net.NetworkCredential, bool, System.Net.Security.RemoteCertificateValidationCallback, bool)"/>
        /// with a <see cref="ConnectTimeout"/>; not of one SharpRTSP makes itself, or remakes on its
        /// own when auto reconnect is on. On expiry <see cref="Stopped"/> reports
        /// <see cref="StoppedReason.ResponseTimeout"/>.
        /// </remarks>
        public TimeSpan ResponseTimeout { get; set; } = Timeout.InfiniteTimeSpan;

        /// <summary>
        /// How long the client may go without an RTP packet while playing.
        /// <see cref="Timeout.InfiniteTimeSpan"/> (the default) waits for ever.
        /// </summary>
        /// <remarks>
        /// Counted from the reply to PLAY and suspended by <see cref="Pause"/>, so a paused
        /// playback session does not time out. On expiry <see cref="Stopped"/> reports
        /// <see cref="StoppedReason.ReceiveTimeout"/>.
        /// </remarks>
        public TimeSpan ReceiveTimeout { get; set; } = Timeout.InfiniteTimeSpan;

        private static readonly TimeSpan WatchdogPeriod = TimeSpan.FromMilliseconds(250);

        /// <summary>Requests still waiting for their reply, and when each was sent.</summary>
        /// <remarks>
        /// By reference: SharpRTSP hands back the very instance that was sent as the reply's
        /// OriginalRequest, and a message's own equality is no business of ours.
        /// </remarks>
        private readonly ConcurrentDictionary<RtspRequest, long> _pendingRequests =
            new ConcurrentDictionary<RtspRequest, long>(ReferenceComparer.Instance);

        private long _lastMediaTimestamp;
        private volatile bool _mediaExpected;
        private Timer _watchdog;

        /// <summary>
        /// The session the watchdog watches: a number of its own each time it starts, and a new one
        /// as it stops, so a check from a session that has ended does nothing.
        /// </summary>
        private int _watchdogSession;

        /// <summary>
        /// Taken by a teardown and by the watchdog's check, which may tear down from its timer
        /// thread while <see cref="Stop"/> or the listener does on theirs: one at a time, the
        /// second finding the tracks' transports already released.
        /// </summary>
        private readonly object _teardownLock = new object();

        private static bool IsSet(TimeSpan timeout) => timeout > TimeSpan.Zero;

        private static TimeSpan Elapsed(long since, long now) =>
            TimeSpan.FromSeconds((double)(now - since) / Stopwatch.Frequency);

        /// <summary>
        /// The RTSP connection, made within <see cref="ConnectTimeout"/> when one is set.
        /// </summary>
        /// <param name="connection">
        /// The TCP connection made here, so its handshake timeout can be lifted once the listener
        /// is up; null when SharpRTSP made the connection itself.
        /// </param>
        /// <exception cref="TimeoutException">No connection within <see cref="ConnectTimeout"/>.</exception>
        private IRtspTransport CreateRtspTransport(out TcpClient connection)
        {
            connection = null;

            bool overTcp = _uri.Scheme == "rtsp" || _uri.Scheme == "rtsps";
            if (!IsSet(ConnectTimeout) || !overTcp)
            {
                return RtspUtils.CreateRtspTransportFromUrl(_uri, _credentials, _userCertificateSelectionCallback);
            }

            TcpClient tcp = ConnectWithin(_uri.Host, _uri.Port, ConnectTimeout);

            // For rtsps the TLS handshake runs synchronously when the listener first asks for the
            // stream, and a synchronous read honours this. Lifted again once the listener is up.
            tcp.ReceiveTimeout = (int)ConnectTimeout.TotalMilliseconds;

            connection = tcp;
            return _uri.Scheme == "rtsps"
                ? new RtspTcpTlsTransport(tcp, _userCertificateSelectionCallback)
                : new RtspTcpTransport(tcp);
        }

        /// <summary>
        /// A TCP connection to a host, made within a time: its name resolved, then each of its
        /// addresses tried in turn - IPv4 and IPv6 alike - with what is left of the time, as
        /// <c>new TcpClient(host, port)</c> tries them. A <see cref="TcpClient"/> made without an
        /// address family is IPv4 only on .NET Framework, which the netstandard2.0 and net481
        /// builds run on, so a camera with only an IPv6 address would not be reached there.
        /// </summary>
        /// <exception cref="TimeoutException">No connection within <paramref name="timeout"/>.</exception>
        /// <exception cref="SocketException">The name did not resolve, or every address refused.</exception>
        private static TcpClient ConnectWithin(string host, int port, TimeSpan timeout)
        {
            long deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
            TimeSpan Remaining() => Elapsed(Stopwatch.GetTimestamp(), deadline);

            Task<IPAddress[]> resolving = Dns.GetHostAddressesAsync(host);
            if (!WaitFor(resolving, Remaining()))
                throw new TimeoutException($"Could not resolve {host} within {timeout}.");
            IPAddress[] addresses = resolving.GetAwaiter().GetResult();

            Exception refused = null;
            foreach (IPAddress address in addresses)
            {
                TimeSpan remaining = Remaining();
                if (remaining <= TimeSpan.Zero)
                    break;

                var tcp = new TcpClient(address.AddressFamily);
                try
                {
                    Task connecting = tcp.ConnectAsync(address, port);
                    if (!WaitFor(connecting, remaining))
                    {
                        tcp.Close();
                        break;
                    }

                    connecting.GetAwaiter().GetResult();
                    return tcp;
                }
                catch (SocketException ex)
                {
                    // this address refused, or is unreachable: the next may not be
                    tcp.Close();
                    refused = ex;
                }
            }

            if (Remaining() <= TimeSpan.Zero || refused == null)
                throw new TimeoutException($"No connection to {host}:{port} within {timeout}.");
            throw refused;
        }

        /// <summary>
        /// Whether a task completed - successfully or not - within a time. One that did not is left
        /// to finish on its own, its exception observed so it is not reported as unobserved later.
        /// </summary>
        private static bool WaitFor(Task task, TimeSpan timeout)
        {
            try
            {
                if (task.Wait(timeout))
                    return true;
            }
            catch (AggregateException)
            {
                // completed, faulted: the caller gets the exception from the task itself
                return true;
            }

            task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return false;
        }

        /// <summary>
        /// Whether an exception is a socket read or write that ran out of time.
        /// </summary>
        private static bool IsSocketTimeout(Exception ex)
        {
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                if (e is SocketException se && se.SocketErrorCode == SocketError.TimedOut)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Takes back the timeouts that only the connect was meant to have.
        /// </summary>
        private void AfterConnected(TcpClient connection)
        {
            if (connection == null)
                return;

            connection.ReceiveTimeout = 0;

            if (IsSet(ResponseTimeout))
            {
                connection.SendTimeout = (int)ResponseTimeout.TotalMilliseconds;
            }
        }

        /// <summary>
        /// Sends a request, noting when it went so the watchdog can tell if it is never answered.
        /// </summary>
        private void SendRequest(RtspListener listener, RtspRequest request)
        {
            if (listener == null)
                return;

            if (IsSet(ResponseTimeout))
            {
                _pendingRequests[request] = Stopwatch.GetTimestamp();
            }

            // not sent - the connection is closed, and the listener does not reconnect - so not
            // waiting for a reply either: the closed connection is what the watchdog reports
            if (!listener.SendMessage(request))
            {
                _pendingRequests.TryRemove(request, out _);
            }
        }

        /// <summary>
        /// A reply arrived: whatever it answers is no longer waiting.
        /// </summary>
        private void ResponseReceived(RtspResponse response)
        {
            if (response.OriginalRequest != null)
            {
                _pendingRequests.TryRemove(response.OriginalRequest, out _);
            }

            if (!response.IsOk)
                return;

            if (response.OriginalRequest is RtspRequestPlay)
            {
                // the media clock starts at the reply, not at the first packet, so a PLAY that is
                // answered but never followed by media still times out
                Interlocked.Exchange(ref _lastMediaTimestamp, Stopwatch.GetTimestamp());
                _mediaExpected = true;
            }
            else if (response.OriginalRequest is RtspRequestPause)
            {
                _mediaExpected = false;
            }
        }

        private void MediaReceived()
        {
            Interlocked.Exchange(ref _lastMediaTimestamp, Stopwatch.GetTimestamp());
        }

        private void StartWatchdog()
        {
            lock (_teardownLock)
            {
                _pendingRequests.Clear();
                _mediaExpected = false;
                int session = ++_watchdogSession;
                Interlocked.Exchange(ref _watchdog, new Timer(Watchdog, session, WatchdogPeriod, WatchdogPeriod))?.Dispose();
            }
        }

        /// <summary>
        /// Stops the watchdog, and retires its session: a check of it already running finds the
        /// session gone and does nothing. First thing in a teardown, so closing the connection on
        /// purpose is not mistaken for losing it.
        /// </summary>
        private void StopWatchdog()
        {
            lock (_teardownLock)
            {
                _watchdogSession++;
                Interlocked.Exchange(ref _watchdog, null)?.Dispose();
                _pendingRequests.Clear();
                _mediaExpected = false;
            }
        }

        private void Watchdog(object state)
        {
            // a timer thread: anything escaping here would take the process down
            try
            {
                // Checked and torn down under the lock a teardown takes, and only while the session
                // it watches is the current one: one torn down meanwhile - by Stop(), or by the
                // listener on an error, on threads of their own - is neither torn down again nor
                // reported, and a check still running from before is not either.
                StoppedReason? reason;
                lock (_teardownLock)
                {
                    if (_watchdogSession != (int)state)
                        return;

                    reason = CheckTimeouts();
                    if (reason == null)
                        return;

                    TeardownClient();
                }

                Stopped?.Invoke(this, new StoppedEventArgs(reason.Value));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in the RTSP watchdog");
            }
        }

        private StoppedReason? CheckTimeouts()
        {
            IRtspTransport socket = _rtspSocket;
            if (socket == null)
                return null;

            // With auto reconnect the listener reconnects on the next send, so a closed socket is
            // not the end of it.
            if (!socket.Connected && !_autoReconnect)
            {
                _logger.LogWarning("The RTSP connection was closed");
                return StoppedReason.ConnectionLost;
            }

            long now = Stopwatch.GetTimestamp();

            TimeSpan responseTimeout = ResponseTimeout;
            if (IsSet(responseTimeout))
            {
                foreach (KeyValuePair<RtspRequest, long> pending in _pendingRequests)
                {
                    if (Elapsed(pending.Value, now) > responseTimeout)
                    {
                        _logger.LogWarning("No reply to {method} within {timeout}", pending.Key.RequestTyped, responseTimeout);
                        return StoppedReason.ResponseTimeout;
                    }
                }
            }

            TimeSpan receiveTimeout = ReceiveTimeout;
            if (IsSet(receiveTimeout) && _mediaExpected
                && Elapsed(Interlocked.Read(ref _lastMediaTimestamp), now) > receiveTimeout)
            {
                _logger.LogWarning("No media received within {timeout}", receiveTimeout);
                return StoppedReason.ReceiveTimeout;
            }

            return null;
        }

        private sealed class ReferenceComparer : IEqualityComparer<RtspRequest>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();

            public bool Equals(RtspRequest x, RtspRequest y) => ReferenceEquals(x, y);

            public int GetHashCode(RtspRequest obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
