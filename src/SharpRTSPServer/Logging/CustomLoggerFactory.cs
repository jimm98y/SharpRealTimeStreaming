using Microsoft.Extensions.Logging;

namespace SharpRTSPServer.Logging
{
    public class CustomLoggerFactory : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        { }

        public ILogger CreateLogger(string categoryName)
        {
            return new CustomLogger();
        }

        public void Dispose()
        { }
    }
}
