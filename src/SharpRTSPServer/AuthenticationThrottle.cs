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
using System.Collections.Generic;

namespace SharpRTSPServer
{
    /// <summary>
    /// Slows down a client that keeps getting the password wrong.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A wrong password costs the client its connection, which already makes guessing slower than
    /// it would be on a connection it could keep. It does not make it slow: opening a TCP connection
    /// is cheap, and up to <see cref="RTSPServer.MaxConnections"/> of them can be in flight at once,
    /// so an attacker who can reconnect gets as many attempts as it has patience for.
    /// </para>
    /// <para>
    /// So failures are counted per address. Past <see cref="Failures"/> in a row, the answer to a
    /// request from that address is delayed by <see cref="Delay"/> before it goes out - which costs
    /// an honest client that mistyped its password almost nothing, since it stops as soon as one
    /// attempt succeeds, and costs anyone working through a dictionary the whole delay per guess.
    /// </para>
    /// <para>
    /// Per address rather than per user name, because the user name in a failed attempt is whatever
    /// the attacker chose to send. It is not a defence against a distributed attempt, and it is not
    /// meant to be; it is what turns thousands of guesses a second into a handful.
    /// </para>
    /// <para>
    /// Safe to use from several threads.
    /// </para>
    /// </remarks>
    internal sealed class AuthenticationThrottle
    {
        /// <summary>
        /// How many failures in a row an address may have before its requests are delayed.
        /// </summary>
        /// <remarks>
        /// Generous on purpose. A client that is configured with the wrong password, or one whose
        /// nonce went stale in a way it could not answer, will fail a few times in the ordinary
        /// course of things.
        /// </remarks>
        public const int DEFAULT_FAILURES_BEFORE_DELAY = 5;

        /// <summary>
        /// How long a request from an address past the threshold is held before it is answered.
        /// </summary>
        public static readonly TimeSpan DEFAULT_DELAY = TimeSpan.FromSeconds(1);

        /// <summary>
        /// How long an address is remembered after its last failure. One that stops failing is
        /// forgotten, so a client that got its password wrong this morning starts clean.
        /// </summary>
        public static readonly TimeSpan DEFAULT_MEMORY = TimeSpan.FromMinutes(10);

        /// <summary>
        /// The most addresses kept at once, so that attempts from many addresses cannot themselves
        /// become the way to use up the server's memory.
        /// </summary>
        private const int MOST_ADDRESSES = 4096;

        private sealed class Record
        {
            public int Failures;
            public DateTime LastFailureUtc;
        }

        private readonly Dictionary<string, Record> _byAddress = new Dictionary<string, Record>(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new object();

        public int Failures { get; set; } = DEFAULT_FAILURES_BEFORE_DELAY;

        public TimeSpan Delay { get; set; } = DEFAULT_DELAY;

        public TimeSpan Memory { get; set; } = DEFAULT_MEMORY;

        /// <summary>
        /// How long a request from this address should be held before it is answered.
        /// </summary>
        public TimeSpan DelayFor(string address)
        {
            if (address == null || Failures <= 0 || Delay <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            lock (_gate)
            {
                if (!_byAddress.TryGetValue(address, out Record record))
                {
                    return TimeSpan.Zero;
                }

                if (DateTime.UtcNow - record.LastFailureUtc > Memory)
                {
                    // stopped failing long enough ago to have been forgotten
                    _byAddress.Remove(address);
                    return TimeSpan.Zero;
                }

                return record.Failures >= Failures ? Delay : TimeSpan.Zero;
            }
        }

        /// <summary>
        /// Notes that a request from this address failed to authenticate.
        /// </summary>
        public void Failed(string address)
        {
            if (address == null)
            {
                return;
            }

            lock (_gate)
            {
                if (!_byAddress.TryGetValue(address, out Record record))
                {
                    if (_byAddress.Count >= MOST_ADDRESSES)
                    {
                        ForgetStale();
                    }

                    if (_byAddress.Count >= MOST_ADDRESSES)
                    {
                        // Still full of addresses that are actively failing. Not counting this one
                        // is the safe way to be full: the addresses already in here are the ones
                        // being delayed, and the alternative is to grow without limit.
                        return;
                    }

                    record = new Record();
                    _byAddress[address] = record;
                }

                if (record.Failures < int.MaxValue)
                {
                    record.Failures++;
                }

                record.LastFailureUtc = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Notes that a request from this address authenticated, so it starts clean again.
        /// </summary>
        public void Succeeded(string address)
        {
            if (address == null)
            {
                return;
            }

            lock (_gate)
            {
                _byAddress.Remove(address);
            }
        }

        /// <summary>
        /// Drops addresses that have not failed for longer than they are remembered.
        /// </summary>
        /// <remarks>Called under <see cref="_gate"/>.</remarks>
        private void ForgetStale()
        {
            DateTime forgetBefore = DateTime.UtcNow - Memory;
            List<string> stale = null;

            foreach (KeyValuePair<string, Record> entry in _byAddress)
            {
                if (entry.Value.LastFailureUtc < forgetBefore)
                {
                    (stale = stale ?? new List<string>()).Add(entry.Key);
                }
            }

            if (stale == null)
            {
                return;
            }

            foreach (string address in stale)
            {
                _byAddress.Remove(address);
            }
        }

        /// <summary>
        /// The part of an end point the count is kept against: the address, not the port, since a
        /// client reconnecting arrives on a different port every time.
        /// </summary>
        public static string KeyFor(System.Net.IPEndPoint endPoint)
        {
            if (endPoint?.Address == null)
            {
                return null;
            }

            return RTSPServer.MediaDestination(endPoint.Address);
        }
    }
}
