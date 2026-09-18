using System;

namespace SharpRTSPServer.Logging
{
    public static class Log
    {
        public static bool WarnEnabled { get; set; } = true;
        public static void Warn(string message, Exception ex = null)
        {
            SinkWarn(message, ex);
        }

        public static bool ErrorEnabled { get; set; } = true;
        public static void Error(string message, Exception ex = null)
        {
            SinkError(message, ex);
        }

        /// <summary>
        /// Whether to log a line for every packet. Off, unlike the rest.
        /// </summary>
        /// <remarks>
        /// Trace is the only level the send path writes at, and it writes once per frame per client
        /// watching. Left on, that was the whole of what a streaming server still allocated - seven
        /// hundred bytes a frame a client, formatted and thrown away - against sixty-four for
        /// actually sending the frame. Turn it on to follow packets, not to run a server.
        /// </remarks>
        public static bool TraceEnabled { get; set; } = false;
        public static void Trace(string message, Exception ex = null)
        {
            SinkTrace(message, ex);
        }

        public static bool DebugEnabled { get; set; } = true;
        public static void Debug(string message, Exception ex = null)
        {
            SinkDebug(message, ex);
        }

        public static bool InfoEnabled { get; set; } = true;
        public static void Info(string message, Exception ex = null)
        {
            SinkInfo(message, ex);
        }

        public static Action<string, Exception> SinkWarn = new Action<string, Exception>((m, ex) => { System.Diagnostics.Debug.WriteLine(m); });
        public static Action<string, Exception> SinkError = new Action<string, Exception>((m, ex) => { System.Diagnostics.Debug.WriteLine(m); });
        public static Action<string, Exception> SinkTrace = new Action<string, Exception>((m, ex) => { System.Diagnostics.Debug.WriteLine(m); });
        public static Action<string, Exception> SinkDebug = new Action<string, Exception>((m, ex) => { System.Diagnostics.Debug.WriteLine(m); });
        public static Action<string, Exception> SinkInfo = new Action<string, Exception>((m, ex) => { System.Diagnostics.Debug.WriteLine(m); });
    }    
}
