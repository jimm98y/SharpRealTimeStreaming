using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace SharpRTSPServer
{
    /// <summary>
    /// The threads that write media to clients, shared by all of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A connection that has media waiting asks to be run; a thread here picks it up, writes some of
    /// what it has, and either puts it back at the end of the queue or leaves it until it has more.
    /// Only one thread ever holds a given connection at a time, so what goes out on it still goes
    /// out in order.
    /// </para>
    /// <para>
    /// The point of the arrangement is that the number of threads follows the number of connections
    /// being written to at this instant, not the number that exist. A thread each is fine for tens
    /// of clients and ruinous for thousands: a megabyte of stack apiece and a scheduler that spends
    /// its time moving between them.
    /// </para>
    /// <para>
    /// Above one thread per processor it grows only when every thread has been busy for long enough
    /// that it is not simply working - a write to a client that is reading takes microseconds, so a
    /// thread still in one after this long is in a write that is not coming back. That is what a
    /// client which has stopped reading does, and it is the only thing worth spending a thread on.
    /// Growing on a merely non-empty queue instead meant a burst of frames spawned threads for work
    /// that the existing ones were about to pick up anyway.
    /// </para>
    /// </remarks>
    internal sealed class RtpWriterPool : IDisposable
    {
        /// <summary>
        /// The most threads that will ever be created. Reached only if this many clients are stuck
        /// in a write at once.
        /// </summary>
        public const int DEFAULT_MAX_THREADS = 64;

        /// <summary>
        /// How long every thread must have been busy before another is added. Long enough that no
        /// healthy write is still running, short enough not to be felt as a stall.
        /// </summary>
        private static readonly TimeSpan STUCK_AFTER = TimeSpan.FromMilliseconds(50);

        private readonly Queue<OutboundQueue> _runnable = new Queue<OutboundQueue>();
        private readonly object _gate = new object();
        private readonly object _watchGate = new object();
        private readonly ILogger _logger;
        private readonly int _maxThreads;
        private readonly int _threadsPerProcessor;

        /// <summary>
        /// When each thread started the work it is on, or zero if it is waiting for some. Indexed by
        /// the order the threads were made.
        /// </summary>
        private readonly List<long> _busySince = new List<long>();

        private int _busy;
        private bool _watching;
        private bool _stopping;

        public RtpWriterPool(ILogger logger, int maxThreads)
        {
            _logger = logger;
            _maxThreads = maxThreads < 1 ? 1 : maxThreads;

            // Enough to use the machine - protecting RTP for a room full of clients is real work, and
            // it is done on these threads.
            int perProcessor = Environment.ProcessorCount < 2 ? 2 : Environment.ProcessorCount;
            _threadsPerProcessor = perProcessor > _maxThreads ? _maxThreads : perProcessor;
        }

        /// <summary>
        /// Asks for a connection to be written to. A connection already waiting its turn or being
        /// written to does not ask again, which is what keeps one thread on it at a time.
        /// </summary>
        public void Schedule(OutboundQueue queue)
        {
            lock (_gate)
            {
                if (_stopping)
                {
                    return;
                }

                _runnable.Enqueue(queue);
                StartWatchdog();

                if (WantsAnotherThread())
                {
                    StartThread();
                }

                Monitor.Pulse(_gate);
            }
        }

        /// <summary>
        /// Watches for every thread being stuck at once, and adds one when it is.
        /// </summary>
        /// <remarks>
        /// Growing only when a connection asks to be written to is not enough. Connections that are
        /// already waiting their turn do not ask again - that is what keeps one thread on each - so
        /// once the threads are all in writes that will not return, the connections behind them can
        /// sit there with nobody left to notice. Something has to look that is not itself stuck.
        /// </remarks>
        private void Watch()
        {
            while (true)
            {
                // A gate of its own. Waiting on the one the writers use would mean a pulse meant to
                // wake a writer could wake this instead, and leave the connection it was for sitting
                // in the queue until the next one came along.
                lock (_watchGate)
                {
                    Monitor.Wait(_watchGate, STUCK_AFTER);
                }

                lock (_gate)
                {
                    if (_stopping)
                    {
                        return;
                    }

                    // Nothing being written and nothing waiting to be, so there is nothing that
                    // could be stuck. It stands down rather than waking twenty times a second for
                    // the life of a server that may have no clients at all; the next connection with
                    // something to send starts it again.
                    if (_runnable.Count == 0 && _busy == 0)
                    {
                        _watching = false;
                        return;
                    }

                    if (WantsAnotherThread())
                    {
                        StartThread();
                    }
                }
            }
        }

        /// <summary>
        /// Called under <see cref="_gate"/>.
        /// </summary>
        private void StartWatchdog()
        {
            if (_watching)
            {
                return;
            }

            _watching = true;

            var watchdog = new Thread(Watch)
            {
                IsBackground = true,
                Name = "RTSP send watchdog",
            };

            watchdog.Start();
        }

        /// <summary>
        /// Called under <see cref="_gate"/>.
        /// </summary>
        private bool WantsAnotherThread()
        {
            if (_busySince.Count >= _maxThreads)
            {
                return false;
            }

            // Threads that are waiting for work, which are about to take what is queued.
            int free = _busySince.Count - _busy;

            if (_runnable.Count <= free)
            {
                return false;
            }

            // Up to one per processor, at once - this is work, and there are processors idle.
            if (_busySince.Count < _threadsPerProcessor)
            {
                return true;
            }

            // Past that, only for threads that are not coming back. If any is merely working, it
            // will be free shortly and another thread would be one more than the machine can run.
            long stuckBefore = Stopwatch.GetTimestamp() - (long)(STUCK_AFTER.TotalSeconds * Stopwatch.Frequency);

            foreach (long since in _busySince)
            {
                if (since == 0 || since > stuckBefore)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Called under <see cref="_gate"/>.
        /// </summary>
        private void StartThread()
        {
            int index = _busySince.Count;
            _busySince.Add(0);

            // Threads of their own rather than pooled ones: they spend their lives blocked in
            // writes, which is what the thread pool must not be used for - it would grow a thread
            // per stalled client anyway, and every other pooled thing in the process would queue up
            // behind them.
            var thread = new Thread(() => Work(index))
            {
                IsBackground = true,
                Name = "RTSP send " + (index + 1),
            };

            thread.Start();
        }

        private void Work(int index)
        {
            while (true)
            {
                OutboundQueue queue;

                lock (_gate)
                {
                    while (_runnable.Count == 0 && !_stopping)
                    {
                        Monitor.Wait(_gate);
                    }

                    if (_stopping)
                    {
                        return;
                    }

                    queue = _runnable.Dequeue();
                    _busy++;
                    _busySince[index] = Stopwatch.GetTimestamp();
                }

                try
                {
                    queue.WriteSome();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error writing queued media");
                }
                finally
                {
                    lock (_gate)
                    {
                        _busy--;
                        _busySince[index] = 0;
                    }
                }
            }
        }

        /// <summary>
        /// How many threads exist. Worth watching: it settles at a handful whatever the audience, and
        /// climbs only when clients stop reading.
        /// </summary>
        public int ThreadCount
        {
            get { lock (_gate) { return _busySince.Count; } }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_stopping)
                {
                    return;
                }

                _stopping = true;
                _runnable.Clear();
                Monitor.PulseAll(_gate);
            }

            lock (_watchGate)
            {
                Monitor.PulseAll(_watchGate);
            }
        }
    }
}
