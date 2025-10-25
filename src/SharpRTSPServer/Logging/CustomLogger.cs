using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;

namespace SharpRTSPServer.Logging
{
    public class CustomLogger : ILogger
    {
        class CustomLoggerScope<TState> : IDisposable
        {
            public CustomLoggerScope(TState state)
            {
                State = state;
            }
            public TState State { get; }
            public void Dispose()
            { }
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            return new CustomLoggerScope<TState>(state);
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            switch (logLevel)
            {
                case LogLevel.Trace:
                    {
                        if (Logging.Log.TraceEnabled)
                        {
                            Logging.Log.Trace(formatter.Invoke(state, exception));
                        }
                    }
                    break;

                case LogLevel.Debug:
                    {
                        if (Logging.Log.DebugEnabled)
                        {
                            Logging.Log.Debug(formatter.Invoke(state, exception));
                        }
                    }
                    break;

                case LogLevel.Information:
                    {
                        if (Logging.Log.InfoEnabled)
                        {
                            Logging.Log.Info(formatter.Invoke(state, exception));
                        }
                    }
                    break;

                case LogLevel.Warning:
                    {
                        if (Logging.Log.WarnEnabled)
                        {
                            Logging.Log.Warn(formatter.Invoke(state, exception));
                        }
                    }
                    break;

                case LogLevel.Error:
                case LogLevel.Critical:
                    {
                        if (Logging.Log.ErrorEnabled)
                        {
                            Logging.Log.Error(formatter.Invoke(state, exception));
                        }
                    }
                    break;

                default:
                    {
                        Debug.WriteLine($"Unknown trace level: {logLevel}");
                    }
                    break;
            }
        }
    }
}
