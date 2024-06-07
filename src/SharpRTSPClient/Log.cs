using Microsoft.Extensions.Logging;
using System;

namespace SharpRTSPClient
{
    public static class Log
    {
        public static bool WarnEnabled { get; set; } = true;
        public static void Warn(string message, Exception ex = null)
        {
            SinkWarn(message, ex);
        }

        public static bool ErrorEnabled { get; set; } = true;
        public static void Error(string message, Exception ex = null)
        {
            SinkError(message, ex);
        }

        public static bool TraceEnabled { get; set; } = true;
        public static void Trace(string message, Exception ex = null)
        {
            SinkTrace(message, ex);
        }

        public static bool DebugEnabled { get; set; } = true;
        public static void Debug(string message, Exception ex = null)
        {
            SinkDebug(message, ex);
        }

        public static bool InfoEnabled { get; set; } = true;
        public static void Info(string message, Exception ex = null)
        {
            SinkInfo(message, ex);
        }

        public static Action<string, Exception> SinkWarn = new Action<string, Exception>((m, ex) => { System.Diagnostics.Debug.WriteLine(m); });
        public static Action<string, Exception> SinkError = new Action<string, Exception>((m, ex) => { System.Diagnostics.Debug.WriteLine(m); });
        public static Action<string, Exception> SinkTrace = new Action<string, Exception>((m, ex) => { System.Diagnostics.Debug.WriteLine(m); });
        public static Action<string, Exception> SinkDebug = new Action<string, Exception>((m, ex) => { System.Diagnostics.Debug.WriteLine(m); });
        public static Action<string, Exception> SinkInfo = new Action<string, Exception>((m, ex) => { System.Diagnostics.Debug.WriteLine(m); });
    }

    public class DebugLogger : ILogger
    {
        class DebugLoggerScope<TState> : IDisposable
        {
            public DebugLoggerScope(DebugLogger logger, TState state)
            {
                _logger = logger;
                State = state;
            }
            private DebugLogger _logger;
            public TState State { get; }
            public void Dispose()
            { }
        }
        public IDisposable BeginScope<TState>(TState state)
        {
            return new DebugLoggerScope<TState>(this, state);
        }
        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (SharpRTSPClient.Log.DebugEnabled) SharpRTSPClient.Log.Debug(formatter.Invoke(state, exception));
        }
    }
}
