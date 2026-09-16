using System;
using System.Collections.Generic;
using System.Threading;
using SharpRTSPClient;

namespace SharpRTSPClient.Tests
{
    /// <summary>
    /// A 401 saying the nonce went stale means carry on, not give up.
    /// </summary>
    [TestClass]
    public sealed class StaleNonceTests
    {
        private const string Sdp =
            "v=0\r\no=- 0 0 IN IP4 0.0.0.0\r\ns=Test\r\nc=IN IP4 0.0.0.0\r\n" +
            "m=video 0 RTP/AVP 96\r\na=control:trackID=0\r\na=rtpmap:96 H264/90000\r\n" +
            "a=fmtp:96 packetization-mode=1; sprop-parameter-sets=Z0IAHpZUBaHogA==,aM48gA==\r\n";

        [TestMethod]
        public void AStaleChallengeMidHandshakeIsAnsweredRatherThanGivenUpOn()
        {
            using var server = new FakeRtspServer(Sdp)
            {
                RequireAuthentication = true,
                // expire the nonce the client just authenticated with, so the next request is stale
                RotateNonceAfter = "DESCRIBE"
            };

            var stops = new List<StoppedReason>();
            using var client = new RTSPClient();
            client.Stopped += (s, e) => { lock (stops) stops.Add(e.Reason); };

            client.Connect(server.BaseUri, RTPTransport.TCP, "admin", "password",
                MediaRequest.VIDEO_ONLY, false, null, false);

            // The client used to treat any 401 to an authorized request as a wrong password and stop
            // for good, so a nonce expiring under a live session ended it.
            Assert.IsTrue(server.WaitForRequest("PLAY", 8000), "the client never got as far as PLAY");
            Assert.IsGreaterThan(0, server.StaleChallenges, "the test did not actually expire a nonce");

            Thread.Sleep(250);
            lock (stops)
            {
                CollectionAssert.DoesNotContain(stops, StoppedReason.Unauthorized,
                    "a stale nonce is not a wrong password");
            }
        }

        [TestMethod]
        public void AWrongPasswordStillStopsTheClient()
        {
            using var server = new FakeRtspServer(Sdp)
            {
                RequireAuthentication = true,
                // never accept what the client answers, and never say it is merely stale
                AlwaysRefuse = true
            };

            var stops = new List<StoppedReason>();
            using var client = new RTSPClient();
            client.Stopped += (s, e) => { lock (stops) stops.Add(e.Reason); };

            client.Connect(server.BaseUri, RTPTransport.TCP, "admin", "password",
                MediaRequest.VIDEO_ONLY, false, null, false);

            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                lock (stops)
                {
                    if (stops.Contains(StoppedReason.Unauthorized))
                        return;
                }
                Thread.Sleep(50);
            }

            Assert.Fail("credentials that never work should still stop the client rather than loop");
        }
    }
}
