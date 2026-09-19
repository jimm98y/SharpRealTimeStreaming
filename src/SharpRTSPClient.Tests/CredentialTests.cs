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

using System;
using SharpRTSPClient;

namespace SharpRTSPClient.Tests
{
    [TestClass]
    public class CredentialTests
    {
        [TestMethod]
        public void UserInfoInTheUri_IsUsedAndStrippedFromTheUri()
        {
            var (uri, credentials) = RTSPClient.ExtractCredentials(
                new Uri("rtsp://bob:secret@camera.local:8554/stream1"), null, null);

            Assert.AreEqual("bob", credentials.UserName);
            Assert.AreEqual("secret", credentials.Password);
            Assert.AreEqual("rtsp://camera.local:8554/stream1", uri.ToString());
        }

        [TestMethod]
        public void UserNameWithoutAPassword_DoesNotThrow()
        {
            var (uri, credentials) = RTSPClient.ExtractCredentials(
                new Uri("rtsp://bob@camera.local:8554/stream1"), null, null);

            Assert.AreEqual("bob", credentials.UserName);
            Assert.AreEqual(string.Empty, credentials.Password);
            Assert.AreEqual("rtsp://camera.local:8554/stream1", uri.ToString());
        }

        [TestMethod]
        public void PasswordContainingAColon_IsKeptWhole()
        {
            var (_, credentials) = RTSPClient.ExtractCredentials(
                new Uri("rtsp://bob:pa:ss:word@camera.local:8554/stream1"), null, null);

            Assert.AreEqual("bob", credentials.UserName);
            Assert.AreEqual("pa:ss:word", credentials.Password);
        }

        [TestMethod]
        public void PercentEncodedUserInfo_IsDecoded()
        {
            // a password of "p@ss word/1" has to be escaped to survive the URI
            var (_, credentials) = RTSPClient.ExtractCredentials(
                new Uri("rtsp://bob%40work:p%40ss%20word%2F1@camera.local:8554/stream1"), null, null);

            Assert.AreEqual("bob@work", credentials.UserName);
            Assert.AreEqual("p@ss word/1", credentials.Password);
        }

        [TestMethod]
        public void WithoutUserInfo_TheSuppliedCredentialsAreUsedAndTheUriIsUntouched()
        {
            var original = new Uri("rtsp://camera.local:8554/stream1");

            var (uri, credentials) = RTSPClient.ExtractCredentials(original, "alice", "hunter2");

            Assert.AreEqual("alice", credentials.UserName);
            Assert.AreEqual("hunter2", credentials.Password);
            Assert.AreEqual(original, uri);
        }

        [TestMethod]
        public void UserInfoInTheUri_TakesPrecedenceOverTheSuppliedCredentials()
        {
            var (_, credentials) = RTSPClient.ExtractCredentials(
                new Uri("rtsp://bob:secret@camera.local:8554/stream1"), "alice", "hunter2");

            Assert.AreEqual("bob", credentials.UserName);
            Assert.AreEqual("secret", credentials.Password);
        }
    }
}
