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

namespace SharpRTSPServer.Logging
{
    /// <summary>
    /// Hands out <see cref="ILogger"/>s that write to one <see cref="ILog"/>.
    /// </summary>
    /// <remarks>
    /// What a server builds for itself when it is not given an <see cref="ILoggerFactory"/>. The
    /// logger is looked up per message rather than captured, so
    /// <see cref="RTSPServer.Logger"/> can be assigned after the server exists.
    /// </remarks>
    public class CustomLoggerFactory : ILoggerFactory
    {
        private readonly Func<ILog> _logger;

        /// <summary>
        /// A factory over a logger that may change, which is what a server uses for its own.
        /// </summary>
        public CustomLoggerFactory(Func<ILog> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// A factory over one fixed logger.
        /// </summary>
        public CustomLoggerFactory(ILog logger)
            : this(() => logger)
        {
        }

        /// <summary>
        /// A factory over a <see cref="DefaultLog"/> of its own.
        /// </summary>
        public CustomLoggerFactory()
            : this(new DefaultLog())
        {
        }

        public void AddProvider(ILoggerProvider provider)
        { }

        public ILogger CreateLogger(string categoryName)
        {
            return new CustomLogger(_logger);
        }

        public void Dispose()
        { }
    }
}
