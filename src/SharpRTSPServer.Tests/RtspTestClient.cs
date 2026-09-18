using System;
using System.Globalization;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// A deliberately minimal RTSP client that speaks to the server over a raw socket.
    /// Used to drive the protocol directly, including cases a well behaved client would never produce.
    /// </summary>
    internal sealed class RtspTestClient : IDisposable
    {
        private readonly TcpClient _tcp;
        private readonly Stream _stream;

        /// <summary>
        /// Reads whatever has arrived into a buffer of the caller's, and throws it away.
        /// </summary>
        /// <remarks>
        /// For tests that care what the server allocates while streaming. <see cref="ReadInterleaved"/>
        /// makes an array for every frame it returns, which is fine when a test wants the frame and
        /// swamps the thing being measured when it does not.
        /// </remarks>
        /// <returns>False once the other end has gone.</returns>
        internal bool DrainInto(byte[] buffer) => _stream.Read(buffer, 0, buffer.Length) > 0;
        private readonly string _userName;
        private readonly string _password;

        private int _cseq;
        private string _realm;
        private string _nonce;

        /// <param name="useTls">Wrap the connection in TLS, for talking to an RTSPS server.</param>
        public RtspTestClient(int port, string userName, string password, bool useTls = false)
            : this("127.0.0.1", port, userName, password, useTls)
        {
        }

        /// <param name="host">Which address to reach the server on, for the tests that care.</param>
        /// <param name="useTls">Wrap the connection in TLS, for talking to an RTSPS server.</param>
        public RtspTestClient(string host, int port, string userName, string password, bool useTls = false)
        {
            _userName = userName;
            _password = password;
            _tcp = new TcpClient(host, port) { ReceiveTimeout = 10_000, SendTimeout = 10_000 };

            if (useTls)
            {
                // the server's certificate is self-signed, so only the handshake itself is of interest
                var ssl = new SslStream(_tcp.GetStream(), false, (sender, cert, chain, errors) => true);
                ssl.AuthenticateAsClient("localhost");
                _stream = ssl;
            }
            else
            {
                _stream = _tcp.GetStream();
            }
        }

        public sealed class Response
        {
            public int StatusCode { get; set; }
            public string Headers { get; set; }
            public string Body { get; set; }
            public string Session => Match(@"Session:\s*([^\s;\r\n]+)");
            public string Match(string pattern)
            {
                var m = Regex.Match(Headers ?? string.Empty, pattern, RegexOptions.IgnoreCase);
                return m.Success ? m.Groups[1].Value : null;
            }
        }

        public Response Send(string method, string uri, params string[] extraHeaders)
        {
            var response = SendOnce(method, uri, extraHeaders);

            // the first request is expected to be challenged - learn the realm/nonce and try again
            if (response.StatusCode == 401 && _nonce == null && _userName != null)
            {
                _realm = response.Match("realm=\"([^\"]+)\"");
                _nonce = response.Match("nonce=\"([^\"]+)\"");
                if (_realm != null && _nonce != null)
                {
                    return SendOnce(method, uri, extraHeaders);
                }
            }

            return response;
        }

        private Response SendOnce(string method, string uri, string[] extraHeaders)
        {
            _cseq++;

            var request = new StringBuilder();
            request.Append(method).Append(' ').Append(uri).Append(" RTSP/1.0\r\n");
            request.Append("CSeq: ").Append(_cseq.ToString(CultureInfo.InvariantCulture)).Append("\r\n");

            if (_nonce != null)
            {
                request.Append("Authorization: ").Append(BuildDigest(method, uri)).Append("\r\n");
            }

            foreach (var header in extraHeaders)
            {
                request.Append(header).Append("\r\n");
            }

            request.Append("\r\n");

            byte[] payload = Encoding.ASCII.GetBytes(request.ToString());
            _stream.Write(payload, 0, payload.Length);
            _stream.Flush();

            return ReadResponse();
        }

        private string BuildDigest(string method, string uri)
        {
            string ha1 = Md5($"{_userName}:{_realm}:{_password}");
            string ha2 = Md5($"{method}:{uri}");
            string response = Md5($"{ha1}:{_nonce}:{ha2}");

            return $"Digest username=\"{_userName}\", realm=\"{_realm}\", nonce=\"{_nonce}\", uri=\"{uri}\", response=\"{response}\"";
        }

        private static string Md5(string value)
        {
            using (var md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(value));
                var builder = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                {
                    builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                }
                return builder.ToString();
            }
        }

        private Response ReadResponse()
        {
            var raw = new MemoryStream();
            byte[] one = new byte[1];
            string headers = null;

            // read byte by byte so we stop exactly at the end of the headers
            while (true)
            {
                int read = _stream.Read(one, 0, 1);
                if (read == 0)
                {
                    throw new IOException("The server closed the connection before sending a full response.");
                }

                raw.WriteByte(one[0]);
                string text = Encoding.ASCII.GetString(raw.ToArray());
                int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end >= 0)
                {
                    headers = text.Substring(0, end);
                    break;
                }
            }

            // read any body, both to keep the next response starting at a clean boundary and because
            // the SDP a DESCRIBE returns is worth looking at
            string bodyText = null;
            var contentLength = Regex.Match(headers, @"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase);
            if (contentLength.Success)
            {
                int remaining = int.Parse(contentLength.Groups[1].Value, CultureInfo.InvariantCulture);
                byte[] body = new byte[remaining];
                int offset = 0;
                while (offset < remaining)
                {
                    int read = _stream.Read(body, offset, remaining - offset);
                    if (read == 0) break;
                    offset += read;
                }
                bodyText = Encoding.UTF8.GetString(body, 0, offset);
            }

            var statusLine = Regex.Match(headers, @"^RTSP/1\.0\s+(\d+)");
            return new Response
            {
                StatusCode = statusLine.Success ? int.Parse(statusLine.Groups[1].Value, CultureInfo.InvariantCulture) : 0,
                Headers = headers,
                Body = bodyText,
            };
        }

        /// <summary>
        /// Reads one RTP or RTCP frame that the server interleaved into the RTSP connection.
        /// </summary>
        /// <remarks>
        /// The framing is '$', a one byte channel, a two byte length, then that many bytes. Reading
        /// them lets a test see the packets exactly as they go on the wire, without a UDP transport.
        /// </remarks>
        public (int Channel, byte[] Payload) ReadInterleaved()
        {
            byte[] marker = new byte[1];
            do
            {
                ReadExactly(marker, 1);
            }
            while (marker[0] != (byte)'$');

            byte[] header = new byte[3];
            ReadExactly(header, 3);

            byte[] payload = new byte[header[1] << 8 | header[2]];
            ReadExactly(payload, payload.Length);

            return (header[0], payload);
        }

        private void ReadExactly(byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = _stream.Read(buffer, offset, count - offset);
                if (read == 0)
                {
                    throw new IOException("The server closed the connection mid-frame.");
                }
                offset += read;
            }
        }

        public void Dispose()
        {
            _stream?.Dispose();
            _tcp?.Dispose();
        }
    }
}
