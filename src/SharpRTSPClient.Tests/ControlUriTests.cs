using System;
using SharpRTSPClient;

namespace SharpRTSPClient.Tests
{
    /// <summary>
    /// The control URIs come out of the server's SDP and are used to build authenticated requests,
    /// so one naming a different host must not be followed.
    /// </summary>
    [TestClass]
    public class ControlUriTests
    {
        private static readonly Uri Connected = new Uri("rtsp://camera.local:8554/stream1");

        [TestMethod]
        [DataRow("rtsp://camera.local:8554/stream1")]
        [DataRow("rtsp://camera.local:8554/stream1/trackID=0")]
        [DataRow("rtsp://CAMERA.LOCAL:8554/stream1")]  // host comparison is case insensitive
        [DataRow("rtsp://camera.local:8554/")]
        public void SameHostSchemeAndPortIsAccepted(string candidate)
        {
            Assert.IsTrue(RTSPClient.IsSameOrigin(Connected, new Uri(candidate)));
        }

        [TestMethod]
        [DataRow("rtsp://evil.example.com:8554/stream1")] // different host
        [DataRow("rtsp://camera.local:9999/stream1")]     // different port
        [DataRow("rtsps://camera.local:8554/stream1")]    // different scheme
        [DataRow("http://camera.local:8554/stream1")]
        public void AnythingPointingElsewhereIsRejected(string candidate)
        {
            Assert.IsFalse(RTSPClient.IsSameOrigin(Connected, new Uri(candidate)));
        }

        [TestMethod]
        public void NullsAreNotConsideredSameOrigin()
        {
            Assert.IsFalse(RTSPClient.IsSameOrigin(Connected, null));
            Assert.IsFalse(RTSPClient.IsSameOrigin(null, Connected));
            Assert.IsFalse(RTSPClient.IsSameOrigin(null, null));
        }

        [TestMethod]
        public void DefaultPortIsTakenIntoAccount()
        {
            // rtsp defaults to 554, so an explicit 554 is still the same origin
            var implicitPort = new Uri("rtsp://camera.local/stream1");
            var explicitPort = new Uri("rtsp://camera.local:554/stream1");

            Assert.IsTrue(RTSPClient.IsSameOrigin(implicitPort, explicitPort));
        }
    }
}
