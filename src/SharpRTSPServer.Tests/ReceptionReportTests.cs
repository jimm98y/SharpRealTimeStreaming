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
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// What a client says about how the media is reaching it, and what the server makes of it.
    /// </summary>
    /// <remarks>
    /// Anything arriving on the control port used to count as a sign that the client was still
    /// there, and nothing more. A receiver report is the only account there is of how a stream is
    /// actually arriving: every packet this server sent, it sent successfully, so a stream arriving
    /// in pieces looks from here exactly like one that is not.
    /// </remarks>
    [TestClass]
    [DoNotParallelize] // sends real RTCP to real ports
    public sealed class ReceptionReportTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        private sealed class Session : IDisposable
        {
            public RTSPServer Server;
            public H264Track Video;
            public RtspTestClient Client;
            public UdpClient Rtp;
            public UdpClient Rtcp;
            public int ServerRtcpPort;
            public uint ServerSsrc;

            public void Dispose()
            {
                Client?.Dispose();
                Rtp?.Dispose();
                Rtcp?.Dispose();
                Server?.Dispose();
            }
        }

        /// <summary>
        /// A playing session over UDP, with sockets standing in for the client's own.
        /// </summary>
        private static Session Play(int firstClientPort)
        {
            var session = new Session();

            int port = TestPorts.FindFree();
            session.Server = new RTSPServer(port, "admin", "password");
            session.Server.SetRtpPortRange(58000, 58200);

            session.Video = new H264Track(Sps, Pps);
            session.Server.AddStreamSource(new RTSPStreamSource("stream1", session.Video, null));
            session.Server.StartListen();

            session.Rtp = new UdpClient(new IPEndPoint(IPAddress.Loopback, firstClientPort));
            session.Rtcp = new UdpClient(new IPEndPoint(IPAddress.Loopback, firstClientPort + 1));

            string baseUri = $"rtsp://127.0.0.1:{port}/stream1";

            session.Client = new RtspTestClient(port, "admin", "password");
            session.Client.Send("OPTIONS", baseUri);
            session.Client.Send("DESCRIBE", baseUri, "Accept: application/sdp");

            var setup = session.Client.Send("SETUP", baseUri + "/trackID=0",
                $"Transport: RTP/AVP;unicast;client_port={firstClientPort}-{firstClientPort + 1}");

            Assert.AreEqual(200, setup.StatusCode);

            string transport = setup.Match(@"Transport:\s*([^\r\n]+)");
            session.ServerRtcpPort = int.Parse(Regex.Match(transport, @"server_port=(\d+)-(\d+)").Groups[2].Value);
            session.ServerSsrc = uint.Parse(Regex.Match(transport, @"ssrc=([0-9A-Fa-f]+)").Groups[1].Value,
                System.Globalization.NumberStyles.HexNumber);

            Assert.AreEqual(200, session.Client.Send("PLAY", baseUri, "Session: " + setup.Session).StatusCode);

            return session;
        }

        /// <summary>
        /// A receiver report about one source, as RFC 3550 section 6.4.2 lays it out.
        /// </summary>
        private static byte[] ReceiverReport(uint reporter, uint about, byte fractionLost,
            int cumulativeLost, uint highest, uint jitter, uint lastSenderReport, uint delaySince)
        {
            byte[] rtcp = new byte[32];

            rtcp[0] = (2 << 6) | 1; // version 2, one report block
            rtcp[1] = 201;          // receiver report
            rtcp[2] = 0;
            rtcp[3] = 7;            // length in words, minus one

            Write(rtcp, 4, reporter);
            Write(rtcp, 8, about);

            rtcp[12] = fractionLost;
            rtcp[13] = (byte)((cumulativeLost >> 16) & 0xFF);
            rtcp[14] = (byte)((cumulativeLost >> 8) & 0xFF);
            rtcp[15] = (byte)(cumulativeLost & 0xFF);

            Write(rtcp, 16, highest);
            Write(rtcp, 20, jitter);
            Write(rtcp, 24, lastSenderReport);
            Write(rtcp, 28, delaySince);

            return rtcp;
        }

        private static void Write(byte[] target, int at, uint value)
        {
            target[at] = (byte)((value >> 24) & 0xFF);
            target[at + 1] = (byte)((value >> 16) & 0xFF);
            target[at + 2] = (byte)((value >> 8) & 0xFF);
            target[at + 3] = (byte)(value & 0xFF);
        }

        private static ReceptionReportEventArgs SendAndWait(Session session, byte[] rtcp)
        {
            ReceptionReportEventArgs seen = null;
            var arrived = new ManualResetEventSlim(false);

            session.Server.ReceptionReportReceived += (s, e) => { seen = e; arrived.Set(); };

            session.Rtcp.Send(rtcp, rtcp.Length, new IPEndPoint(IPAddress.Loopback, session.ServerRtcpPort));

            arrived.Wait(5000);
            return seen;
        }

        [TestMethod]
        public void WhatTheClientReportsReachesTheServer()
        {
            using var session = Play(41900);

            var seen = SendAndWait(session, ReceiverReport(
                reporter: 0xDEADBEEF, about: session.ServerSsrc,
                fractionLost: 64, cumulativeLost: 120, highest: 5000, jitter: 77,
                lastSenderReport: 0, delaySince: 0));

            Assert.IsNotNull(seen, "the report should have been read, not merely counted as a sign of life");

            Assert.AreEqual(0xDEADBEEFu, seen.ReporterSsrc);
            Assert.AreEqual(0, seen.TrackId);
            Assert.AreEqual(64 / 256.0, seen.FractionLost, 0.001, "a quarter of the interval went missing");
            Assert.AreEqual(120, seen.CumulativeLost);
            Assert.AreEqual(5000u, seen.ExtendedHighestSequence);
            Assert.AreEqual(77u, seen.Jitter);
        }

        [TestMethod]
        public void TheServerRemembersTheLastThingItWasTold()
        {
            using var session = Play(41910);

            SendAndWait(session, ReceiverReport(
                reporter: 1, about: session.ServerSsrc,
                fractionLost: 32, cumulativeLost: 7, highest: 900, jitter: 12,
                lastSenderReport: 0, delaySince: 0));

            // kept on the stream, so anything asking how a session is doing can look rather than
            // having to have been listening at the time
            var connections = session.Server.GetStreamSources()[0].ConnectionList;

            foreach (var connection in connections)
            {
                var stream = connection.Streams[0];

                Assert.AreEqual(7, stream.LastReportedCumulativeLost);
                Assert.AreEqual(12u, stream.LastReportedJitter);
                Assert.AreNotEqual(DateTime.MinValue, stream.LastReceptionReportUtc);
            }
        }

        [TestMethod]
        public void AReportAboutSomethingElseIsPassedOver()
        {
            using var session = Play(41920);

            // A compound packet may carry blocks about several sources, and a client may report on
            // sources this server knows nothing about.
            var seen = SendAndWait(session, ReceiverReport(
                reporter: 1, about: session.ServerSsrc ^ 0xFFFF,
                fractionLost: 255, cumulativeLost: 9999, highest: 1, jitter: 1,
                lastSenderReport: 0, delaySince: 0));

            Assert.IsNull(seen, "a report about a source this connection is not sending says nothing about it");
        }

        [TestMethod]
        public void TheRoundTripIsNotGuessedAtWithoutAReportToMeasureFrom()
        {
            using var session = Play(41930);

            var seen = SendAndWait(session, ReceiverReport(
                reporter: 1, about: session.ServerSsrc,
                fractionLost: 0, cumulativeLost: 0, highest: 10, jitter: 0,
                lastSenderReport: 0xAAAAAAAA, // answering a report this server never sent
                delaySince: 65536));

            Assert.IsNotNull(seen);
            Assert.IsNull(seen.RoundTripTime,
                "measuring from a report we did not send would time the gap between reports, not the trip");
        }

        [TestMethod]
        public void TheRoundTripIsWorkedOutFromAReportTheServerSent()
        {
            using var session = Play(41940);

            // make the server send one, so there is something to answer
            session.Video.FeedInRawSamples(3000, new List<ReadOnlyMemory<byte>>
            {
                new ReadOnlyMemory<byte>(new byte[] { 0x65, 0x11, 0x22, 0x33 }),
            });

            Thread.Sleep(300);

            uint echoed = 0;

            foreach (var connection in session.Server.GetStreamSources()[0].ConnectionList)
            {
                echoed = connection.Streams[0].LastSenderReportMiddle32;
            }

            Assert.AreNotEqual(0u, echoed, "the server should have sent a report to be answered");

            var seen = SendAndWait(session, ReceiverReport(
                reporter: 1, about: session.ServerSsrc,
                fractionLost: 0, cumulativeLost: 0, highest: 10, jitter: 0,
                lastSenderReport: echoed,
                delaySince: 0)); // answered immediately

            Assert.IsNotNull(seen);
            Assert.IsNotNull(seen.RoundTripTime, "a report answering ours can be timed");

            Assert.IsGreaterThanOrEqualTo(TimeSpan.Zero, seen.RoundTripTime.Value);
            Assert.IsLessThan(TimeSpan.FromSeconds(5), seen.RoundTripTime.Value,
                "a plausible round trip over loopback: " + seen.RoundTripTime);
        }

        [TestMethod]
        public void RubbishOnTheControlPortDoesNotUpsetAnything()
        {
            using var session = Play(41950);

            // the control port takes whatever the network gives it
            foreach (byte[] rubbish in new[]
            {
                new byte[] { 0x00 },
                new byte[] { 0xFF, 0xFF, 0xFF, 0xFF },
                new byte[] { (2 << 6) | 31, 201, 0xFF, 0xFF },       // claims far more than it holds
                new byte[] { (2 << 6) | 1, 201, 0x00, 0x00 },        // claims a length of nothing
            })
            {
                session.Rtcp.Send(rubbish, rubbish.Length,
                    new IPEndPoint(IPAddress.Loopback, session.ServerRtcpPort));
            }

            Thread.Sleep(300);

            // and the session is still there and still working
            var seen = SendAndWait(session, ReceiverReport(
                reporter: 1, about: session.ServerSsrc,
                fractionLost: 10, cumulativeLost: 1, highest: 2, jitter: 3,
                lastSenderReport: 0, delaySince: 0));

            Assert.IsNotNull(seen, "a real report should still be read after the rubbish");
        }
    }
}
