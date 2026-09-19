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
using Microsoft.Extensions.Logging;
using SharpRTSPServer.Logging;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// The logger sits on the per-packet send path, so IsEnabled has to tell the truth - otherwise
    /// every call site formats a message that is then discarded.
    /// </summary>
    /// <remarks>
    /// These no longer need [DoNotParallelize]. The sinks used to be a static class, so a test that
    /// changed one changed it for everything else running at the same time; each of these now has a
    /// logger of its own.
    /// </remarks>
    [TestClass]
    public class LoggingTests
    {
        private static readonly byte[] Sps = { 0x67, 0x42, 0x00, 0x1E };
        private static readonly byte[] Pps = { 0x68, 0xCE, 0x3C, 0x80 };

        /// <summary>An <see cref="ILog"/> that remembers what it was given.</summary>
        private sealed class Recorder : ILog
        {
            public readonly List<string> Errors = new List<string>();
            public readonly List<string> Warnings = new List<string>();
            public readonly List<string> Infos = new List<string>();
            public readonly List<string> Debugs = new List<string>();
            public readonly List<string> Traces = new List<string>();

            public void LogError(string error) { Errors.Add(error); }
            public void LogWarning(string warning) { Warnings.Add(warning); }
            public void LogInfo(string info) { Infos.Add(info); }
            public void LogDebug(string debug) { Debugs.Add(debug); }
            public void LogTrace(string trace) { Traces.Add(trace); }

            public bool IsErrorEnabled { get; set; } = true;
            public bool IsWarningEnabled { get; set; } = true;
            public bool IsInfoEnabled { get; set; } = true;
            public bool IsDebugEnabled { get; set; } = true;
            public bool IsTraceEnabled { get; set; } = true;
        }

        [TestMethod]
        public void IsEnabledFollowsTheLoggersSwitches()
        {
            var log = new Recorder();
            var logger = new CustomLogger(() => log);

            log.IsDebugEnabled = false;
            log.IsInfoEnabled = true;

            Assert.IsFalse(logger.IsEnabled(LogLevel.Debug));
            Assert.IsTrue(logger.IsEnabled(LogLevel.Information));

            log.IsDebugEnabled = true;
            Assert.IsTrue(logger.IsEnabled(LogLevel.Debug));
        }

        [TestMethod]
        public void NoneIsNeverEnabled()
        {
            Assert.IsFalse(new CustomLogger(() => new Recorder()).IsEnabled(LogLevel.None));
        }

        [TestMethod]
        public void NoLoggerMeansNothingIsEnabled()
        {
            ILogger logger = new CustomLogger(() => null);

            Assert.IsFalse(logger.IsEnabled(LogLevel.Error), "a null logger has nowhere to put it");
            Assert.IsFalse(logger.IsEnabled(LogLevel.Debug));
        }

        [TestMethod]
        public void DisabledLevelDoesNotFormatTheMessage()
        {
            var log = new Recorder { IsDebugEnabled = false };
            ILogger logger = new CustomLogger(() => log);
            int formatterCalls = 0;

            // LogDebug only reaches the formatter if IsEnabled said yes
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.Log(LogLevel.Debug, default, "state", null, (s, e) => { formatterCalls++; return s; });
            }

            Assert.AreEqual(0, formatterCalls);
            Assert.AreEqual(0, log.Debugs.Count);
        }

        [TestMethod]
        public void EnabledLevelReachesTheLogger()
        {
            var log = new Recorder();
            ILogger logger = new CustomLogger(() => log);

            logger.Log(LogLevel.Debug, default, "hello", null, (s, e) => s);

            Assert.AreEqual(1, log.Debugs.Count);
            Assert.AreEqual("hello", log.Debugs[0]);
        }

        [TestMethod]
        public void EachLevelReachesItsOwnMethod()
        {
            var log = new Recorder();
            ILogger logger = new CustomLogger(() => log);

            logger.Log(LogLevel.Error, default, "e", null, (s, e) => s);
            logger.Log(LogLevel.Warning, default, "w", null, (s, e) => s);
            logger.Log(LogLevel.Information, default, "i", null, (s, e) => s);
            logger.Log(LogLevel.Trace, default, "t", null, (s, e) => s);

            Assert.AreEqual("e", log.Errors[0]);
            Assert.AreEqual("w", log.Warnings[0]);
            Assert.AreEqual("i", log.Infos[0]);
            Assert.AreEqual("t", log.Traces[0]);
        }

        [TestMethod]
        public void TwoServersReportToTheirOwnLoggers()
        {
            // The whole point of the change: this used to be one static class, so a second server
            // wrote wherever the first one had been pointed.
            var first = new Recorder();
            var second = new Recorder();

            using var one = new RTSPServer(TestPorts.FindFree(), new InMemoryUserRepository("admin", "password")) { Logger = first };
            using var two = new RTSPServer(TestPorts.FindFree(), new InMemoryUserRepository("admin", "password")) { Logger = second };

            one.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), null));

            one.StartListen();
            two.StartListen();

            Assert.AreSame(first, one.Logger, "each server keeps the logger it was given");
            Assert.AreSame(second, two.Logger);
            Assert.AreNotSame(one.Logger, two.Logger, "and they are not the same one");

            // The track was added to the first server, so it is the first logger it reports to.
            Assert.AreSame(first, ((TrackBase)one.GetStreamSources()[0].Tracks[0]).Logger);
            Assert.AreEqual(0, second.Debugs.Count, "the other logger should have nothing in it");
        }

        [TestMethod]
        public void ANullLoggerSaysNothingAndReportsEveryLevelOff()
        {
            using var server = new RTSPServer(TestPorts.FindFree(), new InMemoryUserRepository("admin", "password")) { Logger = NullLog.Instance };

            Assert.IsFalse(server.Logger.IsErrorEnabled);
            Assert.IsFalse(server.Logger.IsTraceEnabled);

            // and it does not throw when written to
            server.Logger.Error("nowhere");
            server.Logger.Trace("nowhere");
        }

        [TestMethod]
        public void TheDefaultLoggerKeepsTraceOff()
        {
            var log = new DefaultLog();

            Assert.IsFalse(log.IsTraceEnabled, "trace is the per-packet level and costs more than sending does");
            Assert.IsTrue(log.IsDebugEnabled);
            Assert.IsTrue(log.IsErrorEnabled);
        }

        [TestMethod]
        public void TheDefaultLoggerWritesWhereItIsTold()
        {
            var written = new List<string>();
            var log = new DefaultLog { Sink = written.Add };

            log.Error("an error");
            log.Trace("a trace line");

            Assert.AreEqual(1, written.Count, "trace is off, so only the error should have been written");
            Assert.AreEqual("an error", written[0]);
        }

        [TestMethod]
        public void TurningLoggingOffSilencesEveryLevel()
        {
            var written = new List<string>();
            var log = new DefaultLog { Sink = written.Add, IsLoggingEnabled = false };

            log.Error("an error");
            log.Warning("a warning");

            Assert.AreEqual(0, written.Count);
        }

        [TestMethod]
        public void TheExtensionsCopeWithNoLoggerAtAll()
        {
            ILog missing = null;

            // Every call site in the libraries goes through these, and a track that was never added
            // to a server has no logger.
            missing.Error("nowhere");
            missing.Warning("nowhere");
            missing.Info("nowhere");
            missing.Debug("nowhere");
            missing.Trace("nowhere");
        }

        [TestMethod]
        public void TheExceptionIsAppendedToTheMessage()
        {
            var log = new Recorder();

            log.Error("could not do it", new InvalidOperationException("because reasons"));

            Assert.Contains("could not do it", log.Errors[0]);
            Assert.Contains("because reasons", log.Errors[0]);
        }

        [TestMethod]
        public void ATrackReportsWhereItsServerDoes()
        {
            var log = new Recorder();
            using var server = new RTSPServer(TestPorts.FindFree(), new InMemoryUserRepository("admin", "password")) { Logger = log };

            var opus = new OpusTrack { ID = 1, SamplingRate = 16000, Channels = 1 }; // neither is what Opus allows
            server.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), opus));

            opus.BuildSDP(new System.Text.StringBuilder());

            Assert.IsGreaterThanOrEqualTo(2, log.Warnings.Count,
                "the track should have complained about the sampling rate and the channel count");
        }

        [TestMethod]
        public void ReassigningTheServersLoggerBringsItsTracksAlong()
        {
            var first = new Recorder();
            var second = new Recorder();

            using var server = new RTSPServer(TestPorts.FindFree(), new InMemoryUserRepository("admin", "password")) { Logger = first };

            var opus = new OpusTrack { ID = 1, SamplingRate = 16000, Channels = 1 };
            server.AddStreamSource(new RTSPStreamSource("stream1", new H264Track(Sps, Pps), opus));

            server.Logger = second;
            opus.BuildSDP(new System.Text.StringBuilder());

            Assert.AreEqual(0, first.Warnings.Count, "the track should no longer be writing to the old one");
            Assert.IsGreaterThanOrEqualTo(2, second.Warnings.Count);
        }
    }
}
