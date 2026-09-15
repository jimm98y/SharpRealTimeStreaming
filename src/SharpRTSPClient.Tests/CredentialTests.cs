using System;
using SharpRTSPClient;

namespace SharpRTSPClient.Tests
{
    public class CredentialTests
    {
        [Fact]
        public void UserInfoInTheUri_IsUsedAndStrippedFromTheUri()
        {
            var (uri, credentials) = RTSPClient.ExtractCredentials(
                new Uri("rtsp://bob:secret@camera.local:8554/stream1"), null, null);

            Assert.Equal("bob", credentials.UserName);
            Assert.Equal("secret", credentials.Password);
            Assert.Equal("rtsp://camera.local:8554/stream1", uri.ToString());
        }

        [Fact]
        public void UserNameWithoutAPassword_DoesNotThrow()
        {
            var (uri, credentials) = RTSPClient.ExtractCredentials(
                new Uri("rtsp://bob@camera.local:8554/stream1"), null, null);

            Assert.Equal("bob", credentials.UserName);
            Assert.Equal(string.Empty, credentials.Password);
            Assert.Equal("rtsp://camera.local:8554/stream1", uri.ToString());
        }

        [Fact]
        public void PasswordContainingAColon_IsKeptWhole()
        {
            var (_, credentials) = RTSPClient.ExtractCredentials(
                new Uri("rtsp://bob:pa:ss:word@camera.local:8554/stream1"), null, null);

            Assert.Equal("bob", credentials.UserName);
            Assert.Equal("pa:ss:word", credentials.Password);
        }

        [Fact]
        public void PercentEncodedUserInfo_IsDecoded()
        {
            // a password of "p@ss word/1" has to be escaped to survive the URI
            var (_, credentials) = RTSPClient.ExtractCredentials(
                new Uri("rtsp://bob%40work:p%40ss%20word%2F1@camera.local:8554/stream1"), null, null);

            Assert.Equal("bob@work", credentials.UserName);
            Assert.Equal("p@ss word/1", credentials.Password);
        }

        [Fact]
        public void WithoutUserInfo_TheSuppliedCredentialsAreUsedAndTheUriIsUntouched()
        {
            var original = new Uri("rtsp://camera.local:8554/stream1");

            var (uri, credentials) = RTSPClient.ExtractCredentials(original, "alice", "hunter2");

            Assert.Equal("alice", credentials.UserName);
            Assert.Equal("hunter2", credentials.Password);
            Assert.Equal(original, uri);
        }

        [Fact]
        public void UserInfoInTheUri_TakesPrecedenceOverTheSuppliedCredentials()
        {
            var (_, credentials) = RTSPClient.ExtractCredentials(
                new Uri("rtsp://bob:secret@camera.local:8554/stream1"), "alice", "hunter2");

            Assert.Equal("bob", credentials.UserName);
            Assert.Equal("secret", credentials.Password);
        }
    }
}
