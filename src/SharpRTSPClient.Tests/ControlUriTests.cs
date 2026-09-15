using System;
using SharpRTSPClient;

namespace SharpRTSPClient.Tests
{
    /// <summary>
    /// The control URIs come out of the server's SDP and are used to build authenticated requests,
    /// so one naming a different host must not be followed.
    /// </summary>
    public class ControlUriTests
    {
        private static readonly Uri Connected = new Uri("rtsp://camera.local:8554/stream1");

        [Theory]
        [InlineData("rtsp://camera.local:8554/stream1")]
        [InlineData("rtsp://camera.local:8554/stream1/trackID=0")]
        [InlineData("rtsp://CAMERA.LOCAL:8554/stream1")]  // host comparison is case insensitive
        [InlineData("rtsp://camera.local:8554/")]
        public void SameHostSchemeAndPortIsAccepted(string candidate)
        {
            Assert.True(RTSPClient.IsSameOrigin(Connected, new Uri(candidate)));
        }

        [Theory]
        [InlineData("rtsp://evil.example.com:8554/stream1")] // different host
        [InlineData("rtsp://camera.local:9999/stream1")]     // different port
        [InlineData("rtsps://camera.local:8554/stream1")]    // different scheme
        [InlineData("http://camera.local:8554/stream1")]
        public void AnythingPointingElsewhereIsRejected(string candidate)
        {
            Assert.False(RTSPClient.IsSameOrigin(Connected, new Uri(candidate)));
        }

        [Fact]
        public void NullsAreNotConsideredSameOrigin()
        {
            Assert.False(RTSPClient.IsSameOrigin(Connected, null));
            Assert.False(RTSPClient.IsSameOrigin(null, Connected));
            Assert.False(RTSPClient.IsSameOrigin(null, null));
        }

        [Fact]
        public void DefaultPortIsTakenIntoAccount()
        {
            // rtsp defaults to 554, so an explicit 554 is still the same origin
            var implicitPort = new Uri("rtsp://camera.local/stream1");
            var explicitPort = new Uri("rtsp://camera.local:554/stream1");

            Assert.True(RTSPClient.IsSameOrigin(implicitPort, explicitPort));
        }
    }
}
