using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace SharpRTSPServer.Tests
{
    internal static class TestPorts
    {
        /// <summary>
        /// Ports handed out already, so that two tests running side by side are never given the same
        /// one. The operating system will happily offer a port it has just been given back.
        /// </summary>
        private static readonly HashSet<int> Issued = new HashSet<int>();

        private static readonly object Gate = new object();

        /// <summary>
        /// Finds a free loopback port for a server under test to bind.
        /// </summary>
        /// <remarks>
        /// Asking the operating system for one and giving it straight back is not quite enough. The
        /// tests here run side by side and open a great many connections between them, so by the end
        /// of a run there are thousands of ports in TIME_WAIT and the ephemeral range is being cycled
        /// quickly - and the port offered can be taken by something else, or offered again to the
        /// next test, before the server that asked for it has bound it. So each one is remembered and
        /// checked, and another is asked for if it does not hold up.
        /// </remarks>
        public static int FindFree()
        {
            for (int attempt = 0; attempt < 50; attempt++)
            {
                int port = AskForOne();

                if (port == 0)
                {
                    continue;
                }

                lock (Gate)
                {
                    if (!Issued.Add(port))
                    {
                        // already given to another test in this run
                        continue;
                    }
                }

                if (CanBeBound(port))
                {
                    return port;
                }
            }

            throw new InvalidOperationException(
                "No free loopback port could be found after fifty attempts. There are probably " +
                "thousands of ports in TIME_WAIT from an earlier run; they clear on their own.");
        }

        private static int AskForOne()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);

            try
            {
                probe.Start();
                return ((IPEndPoint)probe.LocalEndpoint).Port;
            }
            catch (SocketException)
            {
                return 0;
            }
            finally
            {
                probe.Stop();
            }
        }

        /// <summary>
        /// Whether the port can actually be bound, rather than merely having been offered.
        /// </summary>
        private static bool CanBeBound(int port)
        {
            var check = new TcpListener(IPAddress.Loopback, port);

            try
            {
                check.Start();
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
            finally
            {
                check.Stop();
            }
        }
    }
}
