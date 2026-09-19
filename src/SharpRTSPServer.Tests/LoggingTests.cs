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
using Microsoft.Extensions.Logging;
using SharpRTSPServer.Logging;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// The logger sits on the per-packet send path, so IsEnabled has to tell the truth - otherwise
    /// every call site formats a message that is then discarded.
    /// </summary>
    [TestClass]
    [DoNotParallelize] // the Log sinks are process wide, so this must not run alongside anything that logs
    public class LoggingTests
    {
        private readonly bool _debugEnabled = Log.DebugEnabled;
        private readonly bool _infoEnabled = Log.InfoEnabled;
        private readonly Action<string, Exception> _sinkDebug = Log.SinkDebug;

        [TestMethod]
        public void IsEnabledFollowsTheLogSwitches()
        {
            var logger = new CustomLogger();

            Log.DebugEnabled = false;
            Log.InfoEnabled = true;

            Assert.IsFalse(logger.IsEnabled(LogLevel.Debug));
            Assert.IsTrue(logger.IsEnabled(LogLevel.Information));

            Log.DebugEnabled = true;
            Assert.IsTrue(logger.IsEnabled(LogLevel.Debug));
        }

        [TestMethod]
        public void NoneIsNeverEnabled()
        {
            Assert.IsFalse(new CustomLogger().IsEnabled(LogLevel.None));
        }

        [TestMethod]
        public void DisabledLevelDoesNotFormatTheMessage()
        {
            ILogger logger = new CustomLogger();
            int formatterCalls = 0;
            int sinkCalls = 0;

            Log.SinkDebug = (m, ex) => sinkCalls++;
            Log.DebugEnabled = false;

            // LogDebug only reaches the formatter if IsEnabled said yes
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.Log(LogLevel.Debug, default, "state", null, (s, e) => { formatterCalls++; return s; });
            }

            Assert.AreEqual(0, formatterCalls);
            Assert.AreEqual(0, sinkCalls);
        }

        [TestMethod]
        public void EnabledLevelReachesTheSink()
        {
            ILogger logger = new CustomLogger();
            string received = null;

            Log.SinkDebug = (m, ex) => received = m;
            Log.DebugEnabled = true;

            logger.Log(LogLevel.Debug, default, "hello", null, (s, e) => s);

            Assert.AreEqual("hello", received);
        }

        [TestCleanup]
        public void Cleanup()
        {
            Log.DebugEnabled = _debugEnabled;
            Log.InfoEnabled = _infoEnabled;
            Log.SinkDebug = _sinkDebug;
        }
    }
}
