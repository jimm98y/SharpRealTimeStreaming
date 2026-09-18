using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace SharpRTSPServer.Tests
{
    internal static class TestPorts
    {
        /// <summary>
        /// Where RTSP ports for tests are taken from.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Above everything the tests reserve for themselves. RTP ranges, multicast ranges and the
        /// client ports named in SETUP requests all sit in fixed bands between forty and fifty eight
        /// thousand, because a test asserting which port was allocated has to know where to look.
        /// </para>
        /// <para>
        /// Asking the operating system for a free port does not respect any of that: it hands out
        /// whatever is unbound, which includes the middle of a band another test is about to use, and
        /// that test then finds its port taken by a server doing nothing wrong. It also hands them
        /// out in runs, so filtering its answers means rejecting fifty in a row and giving up.
        /// </para>
        /// </remarks>
        private const int FirstPort = 60000;

        private const int LastPort = 65000;

        /// <summary>
        /// Ports handed out already, so that two tests running side by side never get the same one.
        /// </summary>
        private static readonly HashSet<int> Issued = new HashSet<int>();

        private static readonly object Gate = new object();

        private static readonly Random Start = new Random();

        private static int _next = -1;

        /// <summary>
        /// Finds a free loopback port for a server under test to bind.
        /// </summary>
        /// <remarks>
        /// Walks the band rather than picking at random, so a run cannot keep landing on ports it has
        /// already used, and checks each one can actually be bound - a port left in TIME_WAIT by an
        /// earlier run is unbindable for a few minutes and there are thousands of them after a full
        /// suite.
        /// </remarks>
        public static int FindFree()
        {
            for (int attempt = 0; attempt < LastPort - FirstPort; attempt++)
            {
                int port;

                lock (Gate)
                {
                    if (_next < 0)
                    {
                        _next = Start.Next(FirstPort, LastPort);
                    }

                    port = _next++;

                    if (_next >= LastPort)
                    {
                        _next = FirstPort;
                    }

                    if (!Issued.Add(port))
                    {
                        continue;
                    }
                }

                if (CanBeBound(port))
                {
                    return port;
                }
            }

            throw new InvalidOperationException(
                $"No free loopback port between {FirstPort} and {LastPort}. There are probably " +
                "thousands of ports in TIME_WAIT from an earlier run; they clear on their own.");
        }

        /// <summary>
        /// Whether the port can actually be bound, rather than merely being unclaimed here.
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
