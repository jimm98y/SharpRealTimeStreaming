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

namespace SharpRTSPServer
{
    /// <summary>
    /// One user the server will authenticate.
    /// </summary>
    public class UserInfo
    {
        public string UserName { get; set; }

        /// <summary>
        /// The password, in the clear.
        /// </summary>
        /// <remarks>
        /// Digest access authentication proves a client knows this without either end sending it,
        /// but the server still has to hold it to check the proof: the answer is built from
        /// MD5(user:realm:password) and there is no way to check one without being able to compute
        /// it. Storing the hash instead - which is what a user database ought to keep - needs the
        /// digest to be verified here rather than by the transport library, which cannot be given
        /// anything but a password.
        /// </remarks>
        public string Password { get; set; }

        public UserInfo()
        { }

        public UserInfo(string userName, string password)
        {
            UserName = userName;
            Password = password;
        }

        public override string ToString() => UserName;
    }

    /// <summary>
    /// Where the server looks a user up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server used to be given one user name and one password in its constructor, so every
    /// client that authenticated was the same client as far as anything downstream could tell -
    /// which made <see cref="RTSPServer.AuthorizeStream"/> unable to say anything useful, since
    /// the user it reported was the only user there was.
    /// </para>
    /// <para>
    /// Asked once per request that carries an Authorization header, on the connection's own receive
    /// thread. An implementation that goes to a database holds up that one client and no other, but
    /// it is on the path of every request, so it is worth being quick or cached.
    /// </para>
    /// <para>
    /// There is no asynchronous pair, deliberately: the RTSP request path is synchronous, so the
    /// server would have to block on the result and the method would promise something it could not
    /// deliver.
    /// </para>
    /// </remarks>
    public interface IUserRepository
    {
        /// <summary>
        /// The user with this name, or null if there is no such user.
        /// </summary>
        /// <param name="userName">
        /// The user name the client offered, which is unverified - it is whatever was in the
        /// Authorization header. Returning a user for it is not a decision that the client is that
        /// user; the server checks the password against what comes back.
        /// </param>
        UserInfo GetUser(string userName);
    }
}
