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
