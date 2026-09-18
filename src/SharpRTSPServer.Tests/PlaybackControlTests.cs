using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using SharpRTSPServer;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// Playing from somewhere other than wherever the stream has got to.
    /// </summary>
    /// <remarks>
    /// PLAY used to answer every request with the same hardcoded range, whatever had been asked for,
    /// and PAUSE stopped the sending without remembering where it stopped - so playing again carried
    /// on from wherever the media had reached in the meantime. A client asking to seek was told it
    /// had been obeyed and was not.
    /// </remarks>
    [TestClass]
    public sealed class PlaybackControlTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        /// <summary>
        /// A stream that can be moved about, standing in for whatever owns the media.
        /// </summary>
        private sealed class FakePlayback : IPlaybackControl
        {
            public bool CanSeek { get; set; } = true;
            public TimeSpan? Duration { get; set; } = TimeSpan.FromMinutes(10);
            public TimeSpan Position { get; set; }
            public double Scale { get; private set; } = 1;

            public bool AllowsScale { get; set; }
            public bool IsPaused { get; private set; }
            public int Seeks { get; private set; }

            /// <summary>Where it can actually start from, as a video can only start at a key frame.</summary>
            public TimeSpan? SnapsTo { get; set; }

            public void SeekTo(TimeSpan position)
            {
                Seeks++;
                Position = SnapsTo ?? position;
            }

            public bool TrySetScale(double scale)
            {
                if (!AllowsScale)
                {
                    return false;
                }

                Scale = scale;
                return true;
            }

            public void Pause() => IsPaused = true;
            public void Resume() => IsPaused = false;
        }

        private sealed class Session : IDisposable
        {
            public RTSPServer Server;
            public RtspTestClient Client;
            public string BaseUri;
            public string SessionId;
            public FakePlayback Playback;

            public void Dispose()
            {
                Client?.Dispose();
                Server?.Dispose();
            }
        }

        private static Session SetUp(FakePlayback playback)
        {
            var session = new Session { Playback = playback };

            int port = TestPorts.FindFree();
            session.Server = new RTSPServer(port, "admin", "password");

            session.Server.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), null)
            {
                PlaybackControl = playback,
            });

            session.Server.StartListen();

            session.BaseUri = $"rtsp://127.0.0.1:{port}/stream1";
            session.Client = new RtspTestClient(port, "admin", "password");

            session.Client.Send("OPTIONS", session.BaseUri);
            session.Client.Send("DESCRIBE", session.BaseUri, "Accept: application/sdp");

            var setup = session.Client.Send("SETUP", session.BaseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");

            session.SessionId = setup.Session;
            return session;
        }

        private static double RangeStartOf(RtspTestClient.Response response)
        {
            string range = response.Match(@"Range:\s*([^\r\n]+)");
            Assert.IsNotNull(range, "the reply should say what is being played");

            var start = Regex.Match(range, @"npt=([0-9.]+)-");
            Assert.IsTrue(start.Success, "and say it in normal play time: " + range);

            return double.Parse(start.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------ seeking

        [TestMethod]
        public void PlayingFromAPointMovesTheMediaThere()
        {
            using var session = SetUp(new FakePlayback());

            var play = session.Client.Send("PLAY", session.BaseUri,
                "Session: " + session.SessionId, "Range: npt=125.5-");

            Assert.AreEqual(200, play.StatusCode);
            Assert.AreEqual(1, session.Playback.Seeks, "the media should have been moved, not merely reported on");
            Assert.AreEqual(125.5, session.Playback.Position.TotalSeconds, 0.001);
            Assert.AreEqual(125.5, RangeStartOf(play), 0.001, "and the reply should say where it went");
        }

        [TestMethod]
        public void TheReplySaysWhereItReallyWentRatherThanWhereItWasAsked()
        {
            // A video usually has to begin at a key frame, so where it actually starts is not
            // necessarily where the client asked. Reporting the request back would have the client
            // believe its timeline is a second or two from where it is.
            using var session = SetUp(new FakePlayback { SnapsTo = TimeSpan.FromSeconds(120) });

            var play = session.Client.Send("PLAY", session.BaseUri,
                "Session: " + session.SessionId, "Range: npt=125.5-");

            Assert.AreEqual(120, RangeStartOf(play), 0.001);
        }

        [TestMethod]
        public void HoursMinutesAndSecondsAreUnderstoodToo()
        {
            using var session = SetUp(new FakePlayback());

            session.Client.Send("PLAY", session.BaseUri,
                "Session: " + session.SessionId, "Range: npt=0:02:03.5-");

            Assert.AreEqual(123.5, session.Playback.Position.TotalSeconds, 0.001);
        }

        [TestMethod]
        public void AskingForSomewhereTheMediaDoesNotReachIsRefused()
        {
            using var session = SetUp(new FakePlayback { Duration = TimeSpan.FromSeconds(60) });

            var play = session.Client.Send("PLAY", session.BaseUri,
                "Session: " + session.SessionId, "Range: npt=600-");

            Assert.AreEqual(457, play.StatusCode, "there is nothing ten minutes into a one minute stream");
            Assert.AreEqual(0, session.Playback.Seeks);
        }

        [TestMethod]
        public void ARangeThisServerDoesNotMeasureInIsRefusedRatherThanGuessedAt()
        {
            using var session = SetUp(new FakePlayback());

            // a wall clock range, which means something only for media that has one
            var play = session.Client.Send("PLAY", session.BaseUri,
                "Session: " + session.SessionId, "Range: clock=19961108T142300Z-");

            Assert.AreEqual(456, play.StatusCode,
                "playing from somewhere else and reporting success is worse than saying no");
            Assert.AreEqual(0, session.Playback.Seeks);
        }

        [TestMethod]
        public void ALiveStreamSaysItCannotBePlayedFromAPoint()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");

            // no playback control at all, which is what a camera looks like
            server.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), null));
            server.StartListen();

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");
            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");

            var play = client.Send("PLAY", baseUri, "Session: " + setup.Session, "Range: npt=30-");

            Assert.AreEqual(456, play.StatusCode, "there is no thirty seconds ago on a live stream");
        }

        [TestMethod]
        [DataRow("Range: npt=0.000-")]
        [DataRow("range: npt=0.000-")]
        [DataRow("Range: npt=0-")]
        [DataRow("Range: npt=now-")]
        public void ALiveStreamPlaysTheRangeEveryClientActuallySends(string header)
        {
            // This is what our own client sends on every PLAY, and what VLC and ffmpeg send. It
            // means the beginning of whatever the server has, which on a live stream is now - not a
            // seek. Reading it as one and refusing turned away every client there is.
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");
            server.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), null));
            server.StartListen();

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");
            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");

            var play = client.Send("PLAY", baseUri, "Session: " + setup.Session, header);

            Assert.AreEqual(200, play.StatusCode, header + " should play");
        }

        [TestMethod]
        public void ALiveStreamActuallySendsMediaAfterThatPlay()
        {
            // Answering 200 is not enough: the point of PLAY is what follows it.
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");
            var video = new H264Track(Sps, Pps);
            server.AddStreamSource(new RTSPStreamSource("stream1", video, null));
            server.StartListen();

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");
            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");

            Assert.AreEqual(200, client.Send("PLAY", baseUri,
                "Session: " + setup.Session, "range: npt=0.000-").StatusCode);

            byte[] arrived = null;

            for (int i = 0; i < 12 && arrived == null; i++)
            {
                video.FeedInRawSamples((uint)((i + 1) * 3000), new List<ReadOnlyMemory<byte>>
                {
                    new ReadOnlyMemory<byte>(new byte[] { 0x65, 0x11, 0x22, 0x33 }),
                });

                var frame = client.ReadInterleaved();

                if (frame.Channel == 0)
                {
                    arrived = frame.Payload;
                }
            }

            Assert.IsNotNull(arrived, "media should follow a PLAY that was answered 200");
        }

        [TestMethod]
        public void ALiveStreamStillPlaysFromNow()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");
            server.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), null));
            server.StartListen();

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            using var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");
            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");

            var play = client.Send("PLAY", baseUri, "Session: " + setup.Session, "Range: npt=now-");

            Assert.AreEqual(200, play.StatusCode);
            Assert.Contains("npt=now-", play.Match(@"Range:\s*([^\r\n]+)"), "which is where a live stream is");
        }

        // ------------------------------------------------------------------ pausing

        [TestMethod]
        public void PausingStopsTheMediaWhereItIs()
        {
            using var session = SetUp(new FakePlayback());

            session.Client.Send("PLAY", session.BaseUri, "Session: " + session.SessionId, "Range: npt=40-");

            var pause = session.Client.Send("PAUSE", session.BaseUri, "Session: " + session.SessionId);

            Assert.AreEqual(200, pause.StatusCode);
            Assert.IsTrue(session.Playback.IsPaused, "the media should stop, not just the sending");
            Assert.AreEqual(40, RangeStartOf(pause), 0.001, "and the reply should say where it stopped");
        }

        [TestMethod]
        public void PlayingAgainCarriesOnFromWhereItWasPaused()
        {
            using var session = SetUp(new FakePlayback());

            session.Client.Send("PLAY", session.BaseUri, "Session: " + session.SessionId, "Range: npt=40-");
            session.Client.Send("PAUSE", session.BaseUri, "Session: " + session.SessionId);

            // no range, so carry on from where it was
            var again = session.Client.Send("PLAY", session.BaseUri, "Session: " + session.SessionId);

            Assert.AreEqual(200, again.StatusCode);
            Assert.IsFalse(session.Playback.IsPaused);
            Assert.AreEqual(40, RangeStartOf(again), 0.001, "it should carry on, not start again");
            Assert.AreEqual(1, session.Playback.Seeks, "carrying on is not a seek");
        }

        [TestMethod]
        public void OneClientPausingDoesNotStopTheStreamForTheOthers()
        {
            using var session = SetUp(new FakePlayback());

            session.Client.Send("PLAY", session.BaseUri, "Session: " + session.SessionId);

            using var other = new RtspTestClient(
                int.Parse(Regex.Match(session.BaseUri, @":(\d+)/").Groups[1].Value), "admin", "password");

            other.Send("OPTIONS", session.BaseUri);
            other.Send("DESCRIBE", session.BaseUri, "Accept: application/sdp");
            var otherSetup = other.Send("SETUP", session.BaseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            other.Send("PLAY", session.BaseUri, "Session: " + otherSetup.Session);

            session.Client.Send("PAUSE", session.BaseUri, "Session: " + session.SessionId);

            Assert.IsFalse(session.Playback.IsPaused,
                "the media is only really paused when nobody is left watching it");
        }

        // ------------------------------------------------------------------ speed

        [TestMethod]
        public void PlayingFasterIsAskedOfTheMediaAndReportedBack()
        {
            using var session = SetUp(new FakePlayback { AllowsScale = true });

            var play = session.Client.Send("PLAY", session.BaseUri,
                "Session: " + session.SessionId, "Scale: 2.0");

            Assert.AreEqual(200, play.StatusCode);
            Assert.AreEqual(2.0, session.Playback.Scale, 0.001);
            Assert.AreEqual("2", play.Match(@"Scale:\s*([^\r\n]+)")?.Trim());
        }

        [TestMethod]
        public void AStreamThatOnlyRunsAtItsOwnSpeedSaysSo()
        {
            using var session = SetUp(new FakePlayback { AllowsScale = false });

            var play = session.Client.Send("PLAY", session.BaseUri,
                "Session: " + session.SessionId, "Scale: 4.0");

            // RFC 2326 asks for the nearest rate that can be managed and for the reply to name it,
            // rather than a refusal - but the client must not be left believing it got four times.
            Assert.AreEqual(200, play.StatusCode);
            Assert.AreEqual("1", play.Match(@"Scale:\s*([^\r\n]+)")?.Trim());
            Assert.AreEqual(1.0, session.Playback.Scale, 0.001);
        }

        [TestMethod]
        public void ARateThatIsNotARateIsRefused()
        {
            using var session = SetUp(new FakePlayback { AllowsScale = true });

            foreach (string nonsense in new[] { "Scale: fast", "Scale: 0" })
            {
                var play = session.Client.Send("PLAY", session.BaseUri,
                    "Session: " + session.SessionId, nonsense);

                Assert.AreEqual(400, play.StatusCode, nonsense + " should be refused");
            }
        }

        // ------------------------------------------------------------------ the description

        [TestMethod]
        public void TheDescriptionSaysHowLongTheMediaRuns()
        {
            using var session = SetUp(new FakePlayback { Duration = TimeSpan.FromSeconds(90) });

            var describe = session.Client.Send("DESCRIBE", session.BaseUri, "Accept: application/sdp");

            // Without this a client has no reason to think there is anything to seek to, and will
            // not offer it.
            Assert.Contains("a=range:npt=0-90", describe.Body, describe.Body);
        }

        [TestMethod]
        public void ALiveDescriptionPromisesNoRange()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");
            server.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), null));
            server.StartListen();

            using var client = new RtspTestClient(port, "admin", "password");
            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            client.Send("OPTIONS", baseUri);
            var describe = client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            Assert.DoesNotContain("a=range:", describe.Body,
                "a live stream has no length, so promising one would be a lie");
        }
    }
}
