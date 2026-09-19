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
    /// A set of users held in memory, for a server whose users are known when it starts.
    /// </summary>
    /// <remarks>
    /// Safe to use from several threads, and safe to change while the server is running - a user
    /// removed here cannot authenticate again, though it does not end the sessions they already
    /// have. Anything larger or longer lived than this belongs behind an
    /// <see cref="IUserRepository"/> of your own.
    /// </remarks>
    public sealed class InMemoryUserRepository : IUserRepository
    {
        private readonly Dictionary<string, UserInfo> _users;
        private readonly object _gate = new object();

        /// <param name="caseSensitive">
        /// Whether user names are matched exactly. False by default, since a person typing their
        /// name into a player does not expect the case to matter.
        /// </param>
        public InMemoryUserRepository(bool caseSensitive = false)
        {
            _users = new Dictionary<string, UserInfo>(
                caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A repository holding one user, which is the ordinary case.
        /// </summary>
        public InMemoryUserRepository(string userName, string password)
            : this()
        {
            Add(userName, password);
        }

        /// <summary>
        /// Adds a user, or replaces the password of one already there.
        /// </summary>
        public InMemoryUserRepository Add(string userName, string password)
        {
            if (string.IsNullOrEmpty(userName))
                throw new ArgumentException("A user has a name.", nameof(userName));

            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("A user has a password. For a server that does not authenticate, pass no repository at all.", nameof(password));

            lock (_gate)
            {
                _users[userName] = new UserInfo(userName, password);
            }

            return this;
        }

        /// <summary>
        /// Takes a user out. They cannot authenticate again; sessions they already have carry on
        /// until they end or time out.
        /// </summary>
        public bool Remove(string userName)
        {
            if (string.IsNullOrEmpty(userName))
            {
                return false;
            }

            lock (_gate)
            {
                return _users.Remove(userName);
            }
        }

        /// <summary>
        /// How many users this repository holds.
        /// </summary>
        public int Count
        {
            get { lock (_gate) { return _users.Count; } }
        }

        /// <inheritdoc/>
        public UserInfo GetUser(string userName)
        {
            if (string.IsNullOrEmpty(userName))
            {
                return null;
            }

            lock (_gate)
            {
                return _users.TryGetValue(userName, out UserInfo user) ? user : null;
            }
        }
    }
}
