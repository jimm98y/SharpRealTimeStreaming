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

using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;

namespace SharpRTSPServer.Logging
{
    /// <summary>
    /// Presents an <see cref="ILog"/> as the <see cref="ILogger"/> the server's own code writes to.
    /// </summary>
    /// <remarks>
    /// The server is written against <see cref="ILogger"/> throughout, because its messages are
    /// structured - named values rather than strings already glued together - and that is worth
    /// keeping for a host that has somewhere structured to put them. <see cref="ILog"/> is the
    /// simpler thing a host can implement without taking a dependency on anything, and this is what
    /// joins the two.
    /// <para>
    /// The logger is read through a delegate rather than held, so that assigning
    /// <see cref="RTSPServer.Logger"/> after the server is built takes effect. It used to read a
    /// static class, which is why every server in a process shared one.
    /// </para>
    /// </remarks>
    public class CustomLogger : ILogger
    {
        private readonly Func<ILog> _logger;

        /// <param name="logger">Where to look for the logger each time something is written.</param>
        public CustomLogger(Func<ILog> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

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

        /// <summary>
        /// Reports whether anything would actually be written at this level.
        /// </summary>
        /// <remarks>
        /// This gates the per-packet logging on the RTP send path. Answering "true" unconditionally
        /// made every call site build its message and box its arguments before the sink threw the
        /// result away.
        /// </remarks>
        public bool IsEnabled(LogLevel logLevel)
        {
            ILog logger = _logger();

            if (logger == null)
            {
                return false;
            }

            switch (logLevel)
            {
                case LogLevel.Trace:
                    return logger.IsTraceEnabled;
                case LogLevel.Debug:
                    return logger.IsDebugEnabled;
                case LogLevel.Information:
                    return logger.IsInfoEnabled;
                case LogLevel.Warning:
                    return logger.IsWarningEnabled;
                case LogLevel.Error:
                case LogLevel.Critical:
                    return logger.IsErrorEnabled;
                case LogLevel.None:
                    return false;
                default:
                    return true;
            }
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            ILog logger = _logger();

            if (logger == null || formatter == null)
            {
                return;
            }

            switch (logLevel)
            {
                case LogLevel.Trace:
                    logger.Trace(formatter.Invoke(state, exception), exception);
                    break;

                case LogLevel.Debug:
                    logger.Debug(formatter.Invoke(state, exception), exception);
                    break;

                case LogLevel.Information:
                    logger.Info(formatter.Invoke(state, exception), exception);
                    break;

                case LogLevel.Warning:
                    logger.Warning(formatter.Invoke(state, exception), exception);
                    break;

                case LogLevel.Error:
                case LogLevel.Critical:
                    logger.Error(formatter.Invoke(state, exception), exception);
                    break;

                case LogLevel.None:
                    break;

                default:
                    Debug.WriteLine($"Unknown trace level: {logLevel}");
                    break;
            }
        }
    }
}
