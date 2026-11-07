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
using System.Collections.Generic;
using System.Threading;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// A server authenticates however many users its repository holds.
    /// </summary>
    /// <remarks>
    /// It used to take one user name and one password in its constructor, so every client that
    /// authenticated was the same client as far as anything downstream could tell - which left
    /// AuthorizeStream with nothing to decide on.
    /// </remarks>
    [TestClass]
    public sealed class UserRepositoryTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        private static RTSPServer NewServer(out int port, IUserRepository users)
        {
            port = TestPorts.FindFree();
            var server = new RTSPServer(port, users);
            server.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), null));
            server.StartListen();
            return server;
        }

        private static string UriOf(int port) => $"rtsp://127.0.0.1:{port}/stream1";

        [TestMethod]
        public void EitherOfTwoUsersCanAuthenticate()
        {
            var users = new InMemoryUserRepository()
                .Add("alice", "alice-password")
                .Add("bob", "bob-password");

            using var server = NewServer(out int port, users);

            using (var alice = new RtspTestClient(port, "alice", "alice-password"))
            {
                Assert.AreEqual(200, alice.Send("OPTIONS", UriOf(port)).StatusCode);
            }

            using (var bob = new RtspTestClient(port, "bob", "bob-password"))
            {
                Assert.AreEqual(200, bob.Send("OPTIONS", UriOf(port)).StatusCode);
            }
        }

        [TestMethod]
        public void OneUsersPasswordDoesNotWorkForAnother()
        {
            var users = new InMemoryUserRepository()
                .Add("alice", "alice-password")
                .Add("bob", "bob-password");

            using var server = NewServer(out int port, users);

            using var impostor = new RtspTestClient(port, "bob", "alice-password");
            Assert.AreEqual(401, impostor.Send("OPTIONS", UriOf(port)).StatusCode);
        }

        [TestMethod]
        public void AUserThatIsNotInTheRepositoryIsRefused()
        {
            using var server = NewServer(out int port, new InMemoryUserRepository("alice", "alice-password"));

            using var stranger = new RtspTestClient(port, "carol", "any-password");
            Assert.AreEqual(401, stranger.Send("OPTIONS", UriOf(port)).StatusCode);
        }

        [TestMethod]
        public void TheAuthorizationHandlerIsToldWhichUserItReallyIs()
        {
            var users = new InMemoryUserRepository()
                .Add("alice", "alice-password")
                .Add("bob", "bob-password");

            using var server = NewServer(out int port, users);

            var seen = new List<string>();
            server.AuthorizeStream += (s, e) => { lock (seen) { seen.Add(e.UserName); } };

            using (var alice = new RtspTestClient(port, "alice", "alice-password"))
            {
                Assert.AreEqual(200, alice.Send("DESCRIBE", UriOf(port), "Accept: application/sdp").StatusCode);
            }

            using (var bob = new RtspTestClient(port, "bob", "bob-password"))
            {
                Assert.AreEqual(200, bob.Send("DESCRIBE", UriOf(port), "Accept: application/sdp").StatusCode);
            }

            lock (seen)
            {
                CollectionAssert.AreEqual(new[] { "alice", "bob" }, seen,
                    "the handler should be told who each client actually is, which is the point of it");
            }
        }

        [TestMethod]
        public void AStreamCanBeGivenToOneUserAndRefusedToAnother()
        {
            // The thing a single user could not express: per-stream authorization that distinguishes.
            var users = new InMemoryUserRepository()
                .Add("alice", "alice-password")
                .Add("bob", "bob-password");

            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, users);
            server.AddStreamSource(new RTSPStreamSource("alices", new H264Track(Sps, Pps), null));
            server.AddStreamSource(new RTSPStreamSource("bobs", new H264Track(Sps, Pps), null));
            server.StartListen();

            server.AuthorizeStream += (s, e) =>
            {
                if (e.StreamID != e.UserName + "s")
                {
                    e.Deny(403);
                }
            };

            using (var alice = new RtspTestClient(port, "alice", "alice-password"))
            {
                Assert.AreEqual(200, alice.Send("DESCRIBE", $"rtsp://127.0.0.1:{port}/alices", "Accept: application/sdp").StatusCode);
                Assert.AreEqual(403, alice.Send("DESCRIBE", $"rtsp://127.0.0.1:{port}/bobs", "Accept: application/sdp").StatusCode);
            }

            using (var bob = new RtspTestClient(port, "bob", "bob-password"))
            {
                Assert.AreEqual(200, bob.Send("DESCRIBE", $"rtsp://127.0.0.1:{port}/bobs", "Accept: application/sdp").StatusCode);
                Assert.AreEqual(403, bob.Send("DESCRIBE", $"rtsp://127.0.0.1:{port}/alices", "Accept: application/sdp").StatusCode);
            }
        }

        [TestMethod]
        public void NoRepositoryMeansNoAuthentication()
        {
            using var server = NewServer(out int port, null);

            using var anyone = new RtspTestClient(port, null, null);
            Assert.AreEqual(200, anyone.Send("OPTIONS", UriOf(port)).StatusCode,
                "a server with no users does not challenge at all");
        }

        [TestMethod]
        public void AUserAddedWhileTheServerIsRunningCanAuthenticate()
        {
            var users = new InMemoryUserRepository("alice", "alice-password");
            using var server = NewServer(out int port, users);

            using (var earlyBob = new RtspTestClient(port, "bob", "bob-password"))
            {
                Assert.AreEqual(401, earlyBob.Send("OPTIONS", UriOf(port)).StatusCode);
            }

            users.Add("bob", "bob-password");

            using (var bob = new RtspTestClient(port, "bob", "bob-password"))
            {
                Assert.AreEqual(200, bob.Send("OPTIONS", UriOf(port)).StatusCode,
                    "the repository is asked per request, not read once at startup");
            }
        }

        [TestMethod]
        public void RemovingAUserStopsThemAuthenticatingAgain()
        {
            var users = new InMemoryUserRepository("alice", "alice-password");
            using var server = NewServer(out int port, users);

            using (var alice = new RtspTestClient(port, "alice", "alice-password"))
            {
                Assert.AreEqual(200, alice.Send("OPTIONS", UriOf(port)).StatusCode);
            }

            Assert.IsTrue(users.Remove("alice"));

            using (var gone = new RtspTestClient(port, "alice", "alice-password"))
            {
                Assert.AreEqual(401, gone.Send("OPTIONS", UriOf(port)).StatusCode);
            }
        }

        [TestMethod]
        public void ARepositoryThatThrowsIsNotAWayIn()
        {
            using var server = NewServer(out int port, new ThrowingRepository());

            using var client = new RtspTestClient(port, "alice", "alice-password");
            Assert.AreEqual(401, client.Send("OPTIONS", UriOf(port)).StatusCode);
        }

        private sealed class ThrowingRepository : IUserRepository
        {
            public UserInfo GetUser(string userName) => throw new InvalidOperationException("the database is down");
        }

        [TestMethod]
        public void UserNamesAreNotCaseSensitiveUnlessAskedToBe()
        {
            var relaxed = new InMemoryUserRepository();
            relaxed.Add("Alice", "alice-password");

            Assert.IsNotNull(relaxed.GetUser("alice"));
            Assert.IsNotNull(relaxed.GetUser("ALICE"));

            var exact = new InMemoryUserRepository(caseSensitive: true);
            exact.Add("Alice", "alice-password");

            Assert.IsNotNull(exact.GetUser("Alice"));
            Assert.IsNull(exact.GetUser("alice"));
        }

        [TestMethod]
        public void AUserWithoutANameOrAPasswordIsRefusedOutright()
        {
            var users = new InMemoryUserRepository();

            Assert.ThrowsExactly<ArgumentException>(() => users.Add("", "password"));
            Assert.ThrowsExactly<ArgumentException>(() => users.Add("alice", ""));
        }

        [TestMethod]
        public void BasicAuthenticationAlsoPicksTheRightUser()
        {
            var users = new InMemoryUserRepository()
                .Add("alice", "alice-password")
                .Add("bob", "bob-password");

            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, users)
            {
                AuthenticationScheme = RtspAuthenticationScheme.Basic,
            };
            server.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), null));
            server.StartListen();

            // The test client answers a Digest challenge, not a Basic one, so the header is built
            // here - which is what BasicAuthenticationTests does for the same reason.
            using (var bob = new RtspTestClient(port, "bob", "bob-password"))
            {
                Assert.AreEqual(200, bob.Send("OPTIONS", UriOf(port), BasicHeader("bob", "bob-password")).StatusCode);
            }

            using (var impostor = new RtspTestClient(port, "bob", "alice-password"))
            {
                Assert.AreEqual(401, impostor.Send("OPTIONS", UriOf(port), BasicHeader("bob", "alice-password")).StatusCode,
                    "one user's password must not work for another");
            }

            using (var stranger = new RtspTestClient(port, "carol", "any"))
            {
                Assert.AreEqual(401, stranger.Send("OPTIONS", UriOf(port), BasicHeader("carol", "any")).StatusCode);
            }
        }

        private static string BasicHeader(string user, string password) =>
            "Authorization: Basic " + Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{user}:{password}"));
    }
}
