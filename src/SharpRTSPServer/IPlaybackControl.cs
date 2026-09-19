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

namespace SharpRTSPServer
{
    /// <summary>
    /// A stream that can be played from somewhere other than wherever it has got to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server cannot do any of this itself. It does not know where the media comes from - a
    /// file, a camera, something being generated as it goes - and moving to a point in it means
    /// moving whatever is producing it. So the RTSP side is handled here and the moving is asked of
    /// whoever owns the media, through this.
    /// </para>
    /// <para>
    /// A stream that does not implement it is a live one: it plays from wherever it is, a client
    /// asking for anything else is told it cannot have it, and PAUSE stops sending without stopping
    /// the world.
    /// </para>
    /// </remarks>
    public interface IPlaybackControl
    {
        /// <summary>
        /// Whether this stream can be played from a given point.
        /// </summary>
        /// <remarks>
        /// False for anything live. A camera has no earlier to go back to and no later to skip to,
        /// and a client asking is asking for something that does not exist.
        /// </remarks>
        bool CanSeek { get; }

        /// <summary>
        /// How long the media runs, or null where it has no end.
        /// </summary>
        /// <remarks>
        /// Announced in the description, which is how a client knows there is anywhere to seek to and
        /// how far. Null for a live stream, which is described as running from now with no end.
        /// </remarks>
        TimeSpan? Duration { get; }

        /// <summary>
        /// How far into the media this stream has got.
        /// </summary>
        TimeSpan Position { get; }

        /// <summary>
        /// Moves to a point, before the media starts flowing again.
        /// </summary>
        /// <remarks>
        /// Called only where <see cref="CanSeek"/> is true, and with a point inside the duration. What
        /// a stream does about the gap between there and the nearest point it can actually start from
        /// is its own business - a video usually has to begin at a key frame - and
        /// <see cref="Position"/> afterwards says where it really went.
        /// </remarks>
        void SeekTo(TimeSpan position);

        /// <summary>
        /// The rate the media is playing at, where one is its own speed.
        /// </summary>
        /// <remarks>
        /// Two is twice as fast, and a negative rate is backwards. It is read after
        /// <see cref="TrySetScale"/> to find out what was actually done, since a stream that can only
        /// manage some rates should do the nearest one it can rather than refuse.
        /// </remarks>
        double Scale { get; }

        /// <summary>
        /// Asks for a rate, and says whether anything came of it.
        /// </summary>
        /// <returns>
        /// False where the stream can only play at its own speed, which has the client told so rather
        /// than left believing a request was honoured.
        /// </returns>
        bool TrySetScale(double scale);

        /// <summary>
        /// Stops the media being produced, so that it carries on from here rather than from wherever
        /// it would have reached.
        /// </summary>
        /// <remarks>
        /// The server stops sending on its own account whatever this does. What this is for is the
        /// clock: a paused file should wait, where a paused camera goes on seeing things.
        /// </remarks>
        void Pause();

        /// <summary>
        /// Starts it again, from wherever <see cref="Pause"/> left it.
        /// </summary>
        void Resume();
    }
}
