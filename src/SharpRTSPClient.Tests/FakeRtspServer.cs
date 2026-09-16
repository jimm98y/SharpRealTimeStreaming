using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SharpRTSPClient.Tests
{
    /// <summary>
    /// A stand-in RTSP server that answers the client's handshake with canned responses, so the
    /// client's own dialog and SDP handling can be exercised without a real server or a camera.
    /// </summary>
    internal sealed class FakeRtspServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Thread _thread;
        private readonly string _sdp;
        private readonly CancellationTokenSource _stopping = new CancellationTokenSource();
        private readonly List<string> _requests = new List<string>();
        private readonly object _requestsLock = new object();

        public int Port { get; }

        /// <summary>The methods the client sent, in order.</summary>
        public IReadOnlyList<string> Requests
        {
            get { lock (_requestsLock) { return _requests.ToArray(); } }
        }

        public FakeRtspServer(string sdp)
        {
            _sdp = sdp;

            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            _thread = new Thread(Serve) { IsBackground = true, Name = "FakeRtspServer" };
            _thread.Start();
        }

        public string BaseUri => $"rtsp://127.0.0.1:{Port}/stream1";

        /// <summary>Challenge every request that arrives without an Authorization header.</summary>
        public bool RequireAuthentication { get; set; }

        /// <summary>The nonce currently being handed out. Change it to make the client's one stale.</summary>
        public string Nonce { get; set; } = "0000000000000001";

        /// <summary>Method after which the nonce is replaced, to expire it mid dialog.</summary>
        public string RotateNonceAfter { get; set; }

        /// <summary>Refuse every answer, and never call it stale - what a wrong password looks like.</summary>
        public bool AlwaysRefuse { get; set; }

        /// <summary>How many times a request was refused because its nonce had gone stale.</summary>
        public int StaleChallenges => _staleChallenges;

        private int _staleChallenges;

        private void Serve()
        {
            try
            {
                using (var client = _listener.AcceptTcpClient())
                using (var stream = client.GetStream())
                {
                    var buffer = new StringBuilder();
                    var chunk = new byte[4096];

                    while (!_stopping.IsCancellationRequested)
                    {
                        int read = stream.Read(chunk, 0, chunk.Length);
                        if (read == 0)
                            break;

                        buffer.Append(Encoding.ASCII.GetString(chunk, 0, read));

                        // requests here never carry a body, so the blank line ends each one
                        string pending = buffer.ToString();
                        int end;
                        while ((end = pending.IndexOf("\r\n\r\n", StringComparison.Ordinal)) >= 0)
                        {
                            string request = pending.Substring(0, end);
                            pending = pending.Substring(end + 4);
                            Respond(stream, request);
                        }

                        buffer.Clear();
                        buffer.Append(pending);
                    }
                }
            }
            catch (Exception)
            {
                // the client closing mid-dialog is a normal end to a test
            }
        }

        private void Respond(NetworkStream stream, string request)
        {
            string method = request.Split(' ')[0];
            string cseq = Match(request, @"CSeq:\s*(\d+)") ?? "1";
            string session = Match(request, @"Session:\s*([^\s;\r\n]+)");

            lock (_requestsLock)
            {
                _requests.Add(method);
            }

            if (RequireAuthentication)
            {
                string authorization = Match(request, @"Authorization:\s*(.+)");

                if (authorization == null)
                {
                    SendUnauthorized(stream, cseq, stale: false);
                    return;
                }

                if (AlwaysRefuse)
                {
                    SendUnauthorized(stream, cseq, stale: false);
                    return;
                }

                string presented = Match(authorization, "nonce=\"([^\"]+)\"");
                if (presented != Nonce)
                {
                    // the password was right, the nonce simply outlived the session
                    Interlocked.Increment(ref _staleChallenges);
                    SendUnauthorized(stream, cseq, stale: true);
                    return;
                }
            }

            switch (method)
            {
                case "OPTIONS":
                    Send(stream, cseq, "Public: OPTIONS, DESCRIBE, SETUP, PLAY, PAUSE, TEARDOWN, GET_PARAMETER");
                    break;

                case "DESCRIBE":
                    byte[] body = Encoding.UTF8.GetBytes(_sdp);
                    Send(stream, cseq,
                        new[] { "Content-Type: application/sdp", $"Content-Length: {body.Length}" },
                        body);
                    break;

                case "SETUP":
                    string transport = Match(request, @"Transport:\s*(.+)")?.Trim() ?? "RTP/AVP/TCP;unicast;interleaved=0-1";
                    Send(stream, cseq,
                        $"Transport: {transport}",
                        $"Session: {session ?? "FAKE0001"};timeout=60");
                    break;

                default:
                    Send(stream, cseq, session != null ? $"Session: {session}" : null);
                    break;
            }

            if (RotateNonceAfter != null && RotateNonceAfter == method)
            {
                Nonce = Guid.NewGuid().ToString("N");
            }
        }

        private void SendUnauthorized(NetworkStream stream, string cseq, bool stale)
        {
            string challenge = $"WWW-Authenticate: Digest realm=\"FakeRealm\", nonce=\"{Nonce}\""
                + (stale ? ", stale=\"true\"" : string.Empty);

            var response = new StringBuilder();
            response.Append("RTSP/1.0 401 Unauthorized\r\n");
            response.Append("CSeq: ").Append(cseq).Append("\r\n");
            response.Append(challenge).Append("\r\n");
            response.Append("\r\n");

            byte[] head = Encoding.ASCII.GetBytes(response.ToString());
            stream.Write(head, 0, head.Length);
            stream.Flush();
        }

        private static void Send(NetworkStream stream, string cseq, params string[] headers)
        {
            Send(stream, cseq, headers, null);
        }

        private static void Send(NetworkStream stream, string cseq, string[] headers, byte[] body)
        {
            var response = new StringBuilder();
            response.Append("RTSP/1.0 200 OK\r\n");
            response.Append("CSeq: ").Append(cseq).Append("\r\n");

            foreach (var header in headers)
            {
                if (!string.IsNullOrEmpty(header))
                {
                    response.Append(header).Append("\r\n");
                }
            }

            response.Append("\r\n");

            byte[] head = Encoding.ASCII.GetBytes(response.ToString());
            stream.Write(head, 0, head.Length);

            if (body != null)
            {
                stream.Write(body, 0, body.Length);
            }

            stream.Flush();
        }

        private static string Match(string text, string pattern)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        /// Blocks until the client has sent the given method, or the timeout expires.
        /// </summary>
        public bool WaitForRequest(string method, int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                lock (_requestsLock)
                {
                    if (_requests.Contains(method))
                        return true;
                }
                Thread.Sleep(20);
            }
            return false;
        }

        public void Dispose()
        {
            _stopping.Cancel();
            try { _listener.Stop(); } catch (SocketException) { }
            _thread.Join(2000);
            _stopping.Dispose();
        }
    }
}
