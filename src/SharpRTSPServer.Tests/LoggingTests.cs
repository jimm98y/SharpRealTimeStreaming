using System;
using Microsoft.Extensions.Logging;
using SharpRTSPServer.Logging;

namespace SharpRTSPServer.Tests
{
    /// <summary>
    /// The logger sits on the per-packet send path, so IsEnabled has to tell the truth - otherwise
    /// every call site formats a message that is then discarded.
    /// </summary>
    [Collection("Logging")] // the Log sinks are process wide, so these must not run alongside each other
    public class LoggingTests : IDisposable
    {
        private readonly bool _debugEnabled = Log.DebugEnabled;
        private readonly bool _infoEnabled = Log.InfoEnabled;
        private readonly Action<string, Exception> _sinkDebug = Log.SinkDebug;

        [Fact]
        public void IsEnabledFollowsTheLogSwitches()
        {
            var logger = new CustomLogger();

            Log.DebugEnabled = false;
            Log.InfoEnabled = true;

            Assert.False(logger.IsEnabled(LogLevel.Debug));
            Assert.True(logger.IsEnabled(LogLevel.Information));

            Log.DebugEnabled = true;
            Assert.True(logger.IsEnabled(LogLevel.Debug));
        }

        [Fact]
        public void NoneIsNeverEnabled()
        {
            Assert.False(new CustomLogger().IsEnabled(LogLevel.None));
        }

        [Fact]
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

            Assert.Equal(0, formatterCalls);
            Assert.Equal(0, sinkCalls);
        }

        [Fact]
        public void EnabledLevelReachesTheSink()
        {
            ILogger logger = new CustomLogger();
            string received = null;

            Log.SinkDebug = (m, ex) => received = m;
            Log.DebugEnabled = true;

            logger.Log(LogLevel.Debug, default, "hello", null, (s, e) => s);

            Assert.Equal("hello", received);
        }

        public void Dispose()
        {
            Log.DebugEnabled = _debugEnabled;
            Log.InfoEnabled = _infoEnabled;
            Log.SinkDebug = _sinkDebug;
        }
    }
}
