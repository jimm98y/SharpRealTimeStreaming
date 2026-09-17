using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SharpRTSPServer;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// Each connection drains its own queue, so a client that stops reading only slows itself.
    /// </summary>
    [TestClass]
    public sealed class OutboundQueueTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        /// <summary>Big enough that a few hundred fill the socket buffers at both ends.</summary>
        private static List<ReadOnlyMemory<byte>> BigNal()
        {
            var nal = new byte[1400];
            nal[0] = 0x65;
            return new List<ReadOnlyMemory<byte>> { new ReadOnlyMemory<byte>(nal) };
        }

        private static RtspTestClient Play(int port, string baseUri)
        {
            var client = new RtspTestClient(port, "admin", "password");
            client.Send("OPTIONS", baseUri);
            client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            var setup = client.Send("SETUP", baseUri + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1");
            Assert.AreEqual(200, setup.StatusCode);
            Assert.AreEqual(200, client.Send("PLAY", baseUri, "Session: " + setup.Session).StatusCode);
            return client;
        }

        private static ushort Seq(byte[] rtpPacket) => BinaryPrimitives.ReadUInt16BigEndian(rtpPacket.AsSpan(2));

        [TestMethod]
        public void AClientThatStopsReadingDoesNotDelayAnotherOnTheSameStream()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");
            var videoTrack = new H264Track(Sps, Pps);
            server.AddStreamSource(new RTSPStreamSource("stream1", videoTrack, null));
            server.StartListen();

            string baseUri = "rtsp://127.0.0.1:" + port + "/stream1";

            var stalled = Play(port, baseUri);      // and never reads again
            using var reader = Play(port, baseUri); // this one keeps up

            var stop = new CancellationTokenSource();
            var feeder = Task.Run(() =>
            {
                uint timestamp = 0;
                while (!stop.IsCancellationRequested)
                {
                    try { videoTrack.FeedInRawSamples(timestamp += 3000, BigNal()); }
                    catch (Exception) { return; }
                    Thread.Sleep(1);
                }
            });

            try
            {
                // long enough for the stalled client's buffers to fill and its queue to start dropping
                Thread.Sleep(1500);

                // the reader should still be getting media, promptly
                var stopwatch = Stopwatch.StartNew();
                for (int i = 0; i < 20; i++)
                {
                    if (reader.ReadInterleaved().Channel == 0)
                    {
                        stopwatch.Stop();
                        Assert.IsLessThan(5000, stopwatch.ElapsedMilliseconds,
                            "a client that keeps up should not wait on one that does not");
                        return;
                    }
                }

                Assert.Fail("the reader received no media while another client was stalled");
            }
            finally
            {
                stop.Cancel();
                stalled.Dispose();
                feeder.Wait(TimeSpan.FromSeconds(10));
            }
        }

        [TestMethod]
        public void FeedingAStalledClientDoesNotHoldUpTheProducer()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password");
            var videoTrack = new H264Track(Sps, Pps);
            server.AddStreamSource(new RTSPStreamSource("stream1", videoTrack, null));
            server.StartListen();

            string baseUri = "rtsp://127.0.0.1:" + port + "/stream1";
            var stalled = Play(port, baseUri);

            try
            {
                // fill whatever the socket will take, so the writer is stuck from here on
                for (int i = 0; i < 400; i++)
                {
                    videoTrack.FeedInRawSamples((uint)(i * 3000), BigNal());
                }

                // by now the connection cannot take anything more, and the producer should not care
                var stopwatch = Stopwatch.StartNew();
                for (int i = 0; i < 200; i++)
                {
                    videoTrack.FeedInRawSamples((uint)(400000 + i * 3000), BigNal());
                }
                stopwatch.Stop();

                Assert.IsLessThan(2000, stopwatch.ElapsedMilliseconds,
                    "producing media should not wait on a client that has stopped reading");
            }
            finally
            {
                stalled.Dispose();
            }
        }

        [TestMethod]
        public void DroppedFramesLeaveNoGapInWhatTheClientIsGiven()
        {
            int port = TestPorts.FindFree();
            using var server = new RTSPServer(port, "admin", "password") { MaxQueuedFramesPerConnection = 4 };
            var videoTrack = new H264Track(Sps, Pps);
            server.AddStreamSource(new RTSPStreamSource("stream1", videoTrack, null));
            server.StartListen();

            string baseUri = "rtsp://127.0.0.1:" + port + "/stream1";
            using var client = Play(port, baseUri);

            // Far more than a queue of four can hold, so plenty is dropped. The numbering is put on
            // at write time, so what does go out has to be numbered without gaps - a receiver counts
            // gaps as loss on the network.
            for (int i = 0; i < 300; i++)
            {
                videoTrack.FeedInRawSamples((uint)(i * 3000), BigNal());
            }

            int? previous = null;
            int seen = 0;

            // Most of what was fed was dropped, by design, so how much actually arrives is not
            // something to assert on - reading stops when there is no more rather than at a count.
            try
            {
                for (int i = 0; i < 40 && seen < 12; i++)
                {
                    var frame = client.ReadInterleaved();
                    if (frame.Channel != 0)
                        continue;

                    int sequence = Seq(frame.Payload);
                    if (previous != null)
                    {
                        Assert.AreEqual((ushort)(previous.Value + 1), (ushort)sequence,
                            "the numbering a client is given must not jump because a frame was dropped");
                    }

                    previous = sequence;
                    seen++;
                }
            }
            catch (System.IO.IOException)
            {
                // nothing more to read, which is the expected end of a stream that dropped a lot
            }

            Assert.IsGreaterThan(3, seen, "the test did not read enough media to be checking anything");
        }
    }
}
