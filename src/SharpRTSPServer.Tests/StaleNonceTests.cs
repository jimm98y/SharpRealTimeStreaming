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

using System;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// A nonce that has expired is not a wrong password, and must not be answered as if it were.
    /// </summary>
    [TestClass]
    public sealed class StaleNonceTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        private const string UserName = "admin";
        private const string Password = "password";

        private static RTSPServer NewServer(int port, TimeSpan nonceLifetime)
        {
            var server = new RTSPServer(port, new InMemoryUserRepository(UserName, Password)) { NonceLifetime = nonceLifetime };
            server.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), null));
            server.StartListen();
            return server;
        }

        private static string Md5(string value)
        {
            using var md5 = MD5.Create();
            byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(value));
            var builder = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
            {
                builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }

        private static string BuildDigest(string realm, string nonce, string uri, string password)
        {
            string ha1 = Md5($"{UserName}:{realm}:{password}");
            string ha2 = Md5($"OPTIONS:{uri}");
            string response = Md5($"{ha1}:{nonce}:{ha2}");
            return $"Digest username=\"{UserName}\", realm=\"{realm}\", nonce=\"{nonce}\", uri=\"{uri}\", response=\"{response}\"";
        }

        private static string Field(string text, string name)
        {
            var match = System.Text.RegularExpressions.Regex.Match(text, name + "=\"([^\"]+)\"");
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        /// Keeps one connection open across several requests, which is the point: a stale nonce must
        /// not cost the client its session.
        /// </summary>
        private sealed class RawConnection : IDisposable
        {
            private readonly TcpClient _tcp;
            private readonly NetworkStream _stream;
            private int _cseq;

            public RawConnection(int port)
            {
                _tcp = new TcpClient("127.0.0.1", port) { ReceiveTimeout = 10_000, SendTimeout = 10_000 };
                _stream = _tcp.GetStream();
            }

            public string Send(string uri, string authorization)
            {
                _cseq++;
                var request = new StringBuilder();
                request.Append("OPTIONS ").Append(uri).Append(" RTSP/1.0\r\nCSeq: ")
                    .Append(_cseq.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                if (authorization != null)
                {
                    request.Append("Authorization: ").Append(authorization).Append("\r\n");
                }
                request.Append("\r\n");

                byte[] payload = Encoding.ASCII.GetBytes(request.ToString());
                _stream.Write(payload, 0, payload.Length);
                _stream.Flush();

                var raw = new StringBuilder();
                byte[] one = new byte[1];
                while (!raw.ToString().Contains("\r\n\r\n"))
                {
                    if (_stream.Read(one, 0, 1) == 0)
                        throw new System.IO.IOException("the server closed the connection");
                    raw.Append((char)one[0]);
                }
                return raw.ToString();
            }

            public void Dispose()
            {
                _stream?.Dispose();
                _tcp?.Dispose();
            }
        }

        private static int StatusOf(string response)
        {
            return int.Parse(response.Substring(9, 3), CultureInfo.InvariantCulture);
        }

        [TestMethod]
        public void AnExpiredNonceIsAnsweredStaleAndTheConnectionIsKept()
        {
            var lifetime = TimeSpan.FromSeconds(1);

            int port = TestPorts.FindFree();
            using var server = NewServer(port, lifetime);

            string uri = "rtsp://127.0.0.1:" + port + "/stream1";
            using var connection = new RawConnection(port);

            string challenge = connection.Send(uri, null);
            string authorization = BuildDigest(Field(challenge, "realm"), Field(challenge, "nonce"), uri, Password);
            Assert.AreEqual(200, StatusOf(connection.Send(uri, authorization)));

            // let the nonce rotate well out of its grace window
            Thread.Sleep(lifetime + lifetime + lifetime + TimeSpan.FromMilliseconds(500));

            string refused = connection.Send(uri, authorization);
            Assert.AreEqual(401, StatusOf(refused));
            Assert.Contains("stale", refused.ToLowerInvariant(),
                "the client has to be told the password was right and only the nonce was old");

            // and the session survives, so the client can simply answer the new challenge
            string retried = BuildDigest(Field(refused, "realm"), Field(refused, "nonce"), uri, Password);
            Assert.AreEqual(200, StatusOf(connection.Send(uri, retried)),
                "answering the new challenge on the same connection should work");
        }

        [TestMethod]
        public void AWrongPasswordIsNotReportedAsStale()
        {
            int port = TestPorts.FindFree();
            using var server = NewServer(port, TimeSpan.Zero);

            string uri = "rtsp://127.0.0.1:" + port + "/stream1";
            using var connection = new RawConnection(port);

            string challenge = connection.Send(uri, null);
            string wrong = BuildDigest(Field(challenge, "realm"), Field(challenge, "nonce"), uri, "not-the-password");

            string refused = connection.Send(uri, wrong);
            Assert.AreEqual(401, StatusOf(refused));
            Assert.DoesNotContain("stale", refused.ToLowerInvariant(),
                "a wrong password must not be dressed up as an expired nonce");
        }

        [TestMethod]
        public void RotationIsOffUnlessItIsAskedFor()
        {
            // It ends any session whose client treats a 401 as a wrong password, so the deployment
            // has to opt in rather than find out.
            Assert.AreEqual(TimeSpan.Zero, RTSPServer.DEFAULT_NONCE_LIFETIME);

            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, new InMemoryUserRepository(UserName, Password));
            server.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), null));
            server.StartListen();

            Assert.AreEqual(TimeSpan.Zero, server.NonceLifetime);

            string uri = "rtsp://127.0.0.1:" + port + "/stream1";
            using var connection = new RawConnection(port);

            string challenge = connection.Send(uri, null);
            string authorization = BuildDigest(Field(challenge, "realm"), Field(challenge, "nonce"), uri, Password);

            Assert.AreEqual(200, StatusOf(connection.Send(uri, authorization)));
            Thread.Sleep(1500);
            Assert.AreEqual(200, StatusOf(connection.Send(uri, authorization)),
                "with rotation off the nonce should still be good");
        }
    }
}
