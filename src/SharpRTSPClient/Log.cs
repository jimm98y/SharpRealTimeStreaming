using System;

namespace SharpRTSPClient
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
        /// Trace is what the payload parsers write at, once per RTP packet - "Single NAL",
        /// "Fragmentation Unit", and so on. Left on, formatting and writing those lines cost around
        /// five hundred bytes a packet, measured, which was half of everything the client allocated
        /// receiving a stream. Turn it on to follow packets, not to run a client.
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
