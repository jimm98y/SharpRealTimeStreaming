using System.Net;
using System.Net.Sockets;

namespace SharpRTSPServer.Tests
{
    internal static class TestPorts
    {
        /// <summary>
        /// Asks the OS for a free loopback port and gives it straight back, so the server under test
        /// can bind it. Good enough for tests, where nothing else is competing for ports.
        /// </summary>
        public static int FindFree()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }
}
