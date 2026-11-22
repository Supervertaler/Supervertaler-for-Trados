using System;
using System.Diagnostics;
using System.Threading;

namespace Supervertaler.Trados.SuperVault
{
    /// <summary>
    /// Runs EverTM Matches' lookups off Studio's UI thread, newest request first (docs/design/evertm-pane.md, section 2).
    ///
    /// <para>At most one lookup runs, and at most one waits. A request that arrives while one runs replaces the waiting
    /// one instead of queueing behind it, so holding the down arrow through 200 segments costs two lookups, not 200.
    /// A result is handed to <c>deliver</c> only while it is still the newest request; the caller checks again with
    /// <see cref="IsLatest"/> once it is back on the UI thread, because a newer request can arrive in between.</para>
    ///
    /// <para>No Studio types here, so the tests drive it without Studio. The lookup and the delivery are the caller's;
    /// neither may throw out of this class: a lookup that throws becomes a result with <see cref="Result{T}.Error"/>, and
    /// a delivery that throws is logged and dropped. Whatever happens, the worker is free again afterwards: a failure
    /// that left it marked busy would stop the pane for the rest of the Studio session.</para>
    ///
    /// <para>No lock is held while the lookup or the delivery runs.</para>
    /// </summary>
    public sealed class EverTmLookupWorker<TRequest, TResult>
    {
        public sealed class Result<T>
        {
            public long Id;
            public T Value;
            /// <summary>Why the lookup failed (its exception message); null when it ran.</summary>
            public string Error;
            public long Milliseconds;
        }

        readonly Func<TRequest, TResult> _lookup;
        readonly Action<Result<TResult>> _deliver;
        readonly Action<string> _log;
        readonly object _gate = new object();
        long _latest;                 // the newest request's number; under _gate for writes
        (long id, TRequest request)? _waiting;
        bool _running;
        bool _stopped;

        public EverTmLookupWorker(Func<TRequest, TResult> lookup, Action<Result<TResult>> deliver, Action<string> log = null)
        {
            _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
            _deliver = deliver ?? throw new ArgumentNullException(nameof(deliver));
            _log = log;
        }

        /// <summary>Asks for a lookup; returns its number. Never blocks on a running lookup.</summary>
        public long Submit(TRequest request)
        {
            lock (_gate)
            {
                if (_stopped) return 0;
                long id = ++_latest;
                if (_running) { _waiting = (id, request); return id; }
                _running = true;
                if (!ThreadPool.QueueUserWorkItem(_ => Run(id, request)))
                {
                    _running = false;
                    throw new InvalidOperationException("No thread for the EverTM lookup.");
                }
                return id;
            }
        }

        /// <summary>Makes every request so far out of date (the document closed, the pane was hidden): nothing more is
        /// delivered for them, and a waiting one is dropped.</summary>
        public void Invalidate()
        {
            lock (_gate) { _latest++; _waiting = null; }
        }

        /// <summary>For good: no more lookups or deliveries (the pane is gone). A running lookup finishes and is dropped.</summary>
        public void Stop()
        {
            lock (_gate) { _stopped = true; _latest++; _waiting = null; }
        }

        public bool IsLatest(long id)
        {
            lock (_gate) return !_stopped && id == _latest;
        }

        /// <summary>For tests: whether a lookup is running or waiting.</summary>
        public bool Busy { get { lock (_gate) return _running; } }

        void Run(long id, TRequest request)
        {
            try
            {
                while (true)
                {
                    var result = new Result<TResult> { Id = id };
                    if (IsLatest(id))
                    {
                        var sw = Stopwatch.StartNew();
                        try { result.Value = _lookup(request); }
                        catch (Exception ex)
                        {
                            result.Error = ex.Message;
                            Log("EverTM Matches: lookup failed: " + ex);
                        }
                        result.Milliseconds = sw.ElapsedMilliseconds;
                        if (IsLatest(id))
                        {
                            try { _deliver(result); }
                            catch (Exception ex) { Log("EverTM Matches: showing a result failed: " + ex); }
                        }
                    }
                    lock (_gate)
                    {
                        if (_waiting == null || _stopped) { _waiting = null; _running = false; return; }
                        (id, request) = _waiting.Value;
                        _waiting = null;
                    }
                }
            }
            catch (Exception ex)
            {
                // Only Log or IsLatest could get here; the worker must still come free.
                lock (_gate) { _running = false; _waiting = null; }
                Log("EverTM Matches: worker failed: " + ex);
            }
        }

        void Log(string message)
        {
            try { _log?.Invoke(message); } catch { }
        }
    }
}
