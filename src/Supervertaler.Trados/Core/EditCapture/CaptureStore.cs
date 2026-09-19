using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace Supervertaler.Trados.Core.EditCapture
{
    /// <summary>
    /// The write side of edit capture: a bounded in-memory queue drained to
    /// SQLite by one background thread.
    ///
    /// <para><b>The editor thread must never wait.</b> <see cref="Enqueue"/> takes
    /// the lock only long enough to push onto a queue and never touches the
    /// database. The writer takes the same lock only long enough to swap the
    /// queue out, then does every insert with the lock released — so the editor
    /// can never block behind disk I/O.</para>
    ///
    /// <para><b>Dropping data is acceptable; freezing Studio is not.</b> The queue
    /// is capped. When it is full the oldest event is discarded and counted, so a
    /// slow disk costs us the tail of a burst rather than the editor's
    /// responsiveness or the process's memory.</para>
    ///
    /// <para>One writer, WAL, no cross-process coordination. A second Studio
    /// instance simply fails to write and says so in the log; a clever lock here
    /// would be a buggy one.</para>
    /// </summary>
    internal sealed class CaptureStore : IDisposable
    {
        private const int MaxQueue = 10000;
        private const int BatchSize = 100;
        private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

        private readonly object _gate = new object();
        private Queue<CaptureEvent> _queue = new Queue<CaptureEvent>();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private readonly string _dbPath;

        private Thread _writer;
        private volatile bool _stopping;
        private long _dropped;
        private long _written;
        private bool _schemaFailed;

        public CaptureStore(string dbPath) { _dbPath = dbPath; }

        public long Dropped { get { return Interlocked.Read(ref _dropped); } }
        public long Written { get { return Interlocked.Read(ref _written); } }

        /// <summary>
        /// Starts the writer. Returns false when the database could not be opened
        /// or the schema could not be created — capture then stays off rather
        /// than failing once per segment for the rest of the session.
        /// </summary>
        public bool Start()
        {
            try
            {
                if (!EnsureSchema()) return false;

                _writer = new Thread(WriterLoop)
                {
                    IsBackground = true,
                    Name = "Supervertaler edit capture",
                    Priority = ThreadPriority.BelowNormal
                };
                _writer.Start();
                return true;
            }
            catch (Exception ex)
            {
                Log("could not start: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Hands an event to the writer. Called from the editor thread and from
        /// event callbacks: it must never throw and never block.
        /// </summary>
        public void Enqueue(CaptureEvent e)
        {
            if (e == null || _stopping || _schemaFailed) return;
            try
            {
                lock (_gate)
                {
                    if (_queue.Count >= MaxQueue)
                    {
                        _queue.Dequeue();                 // oldest goes
                        Interlocked.Increment(ref _dropped);
                    }
                    _queue.Enqueue(e);
                }
                _signal.Set();
            }
            catch
            {
                // Capture failing must be invisible to the translator. No dialog,
                // no retry, no rethrow into the UI thread.
            }
        }

        private void WriterLoop()
        {
            SqliteConnection conn = null;
            try
            {
                conn = Open();
                while (true)
                {
                    var stopping = _stopping;
                    if (!stopping) _signal.WaitOne(FlushInterval);

                    var batch = Drain();
                    if (batch.Count > 0) WriteBatch(conn, batch);

                    // Exit only once the queue is empty, so Stop() flushes the tail.
                    if (stopping && batch.Count == 0) break;
                }
            }
            catch (Exception ex)
            {
                Log("writer stopped: " + ex.Message);
            }
            finally
            {
                // Cleanup runs whether or not the loop above threw. A connection
                // closed only on the success path is the leak this guards.
                try { if (conn != null) conn.Dispose(); } catch { }
                if (Dropped > 0) Log("dropped " + Dropped + " event(s) under load this session");
            }
        }

        private List<CaptureEvent> Drain()
        {
            // Swap the queue out under the lock and insert with it released, so
            // the editor never waits behind disk I/O.
            Queue<CaptureEvent> taken;
            lock (_gate)
            {
                if (_queue.Count == 0) return new List<CaptureEvent>();
                if (_queue.Count <= BatchSize)
                {
                    taken = _queue;
                    _queue = new Queue<CaptureEvent>();
                }
                else
                {
                    taken = new Queue<CaptureEvent>();
                    for (var i = 0; i < BatchSize; i++) taken.Enqueue(_queue.Dequeue());
                }
            }
            return new List<CaptureEvent>(taken);
        }

        private SqliteConnection Open()
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private
            }.ToString();
            var conn = new SqliteConnection(cs);
            conn.Open();
            using (var pragma = new SqliteCommand("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;", conn))
                pragma.ExecuteNonQuery();
            return conn;
        }

        private bool EnsureSchema()
        {
            try
            {
                using (var conn = Open())
                using (var cmd = new SqliteCommand(SchemaSql, conn))
                {
                    cmd.ExecuteNonQuery();
                }
                return true;
            }
            catch (Exception ex)
            {
                _schemaFailed = true;
                Log("schema could not be created at " + _dbPath + ": " + ex.Message);
                return false;
            }
        }

        internal const string SchemaSql = @"
CREATE TABLE IF NOT EXISTS events (
    id            INTEGER PRIMARY KEY,
    event         TEXT NOT NULL,
    ts            TEXT NOT NULL,
    file_id       TEXT NOT NULL,
    unit_id       TEXT NOT NULL,
    seg_id        TEXT NOT NULL,
    source        TEXT,
    target        TEXT,
    origin        TEXT,
    match_percent INTEGER,
    conf_level    TEXT,
    comment       TEXT,
    comment_meta  TEXT,
    project       TEXT,
    client        TEXT,
    case_ref      TEXT,
    src_lang      TEXT,
    tgt_lang      TEXT
);
CREATE INDEX IF NOT EXISTS idx_seg ON events(file_id, unit_id, seg_id);
CREATE INDEX IF NOT EXISTS idx_ts  ON events(ts);";

        private void WriteBatch(SqliteConnection conn, List<CaptureEvent> batch)
        {
            try
            {
                using (var tx = conn.BeginTransaction())
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = @"
INSERT INTO events (event, ts, file_id, unit_id, seg_id, source, target, origin,
                    match_percent, conf_level, comment, comment_meta,
                    project, client, case_ref, src_lang, tgt_lang)
VALUES (@event, @ts, @file, @unit, @seg, @source, @target, @origin,
        @match, @conf, @comment, @meta, @project, @client, @case, @src, @tgt);";
                        var p = cmd.Parameters;
                        p.Add("@event", SqliteType.Text); p.Add("@ts", SqliteType.Text);
                        p.Add("@file", SqliteType.Text); p.Add("@unit", SqliteType.Text);
                        p.Add("@seg", SqliteType.Text); p.Add("@source", SqliteType.Text);
                        p.Add("@target", SqliteType.Text); p.Add("@origin", SqliteType.Text);
                        p.Add("@match", SqliteType.Integer); p.Add("@conf", SqliteType.Text);
                        p.Add("@comment", SqliteType.Text); p.Add("@meta", SqliteType.Text);
                        p.Add("@project", SqliteType.Text); p.Add("@client", SqliteType.Text);
                        p.Add("@case", SqliteType.Text); p.Add("@src", SqliteType.Text);
                        p.Add("@tgt", SqliteType.Text);

                        foreach (var e in batch)
                        {
                            p["@event"].Value = Val(e.Event);
                            p["@ts"].Value = e.TimestampUtc.ToString("o");
                            // These three are NOT NULL. Val() yields DBNull for an
                            // empty string and DBNull is not null, so "Val(x) ?? \"\""
                            // never fell back and every batch was rejected - the
                            // whole of the second live run was lost this way.
                            p["@file"].Value = e.FileId ?? "";
                            p["@unit"].Value = e.UnitId ?? "";
                            p["@seg"].Value = e.SegId ?? "";
                            p["@source"].Value = Val(e.Source);
                            p["@target"].Value = Val(e.Target);
                            p["@origin"].Value = Val(e.Origin);
                            p["@match"].Value = e.MatchPercent.HasValue ? (object)e.MatchPercent.Value : DBNull.Value;
                            p["@conf"].Value = Val(e.ConfLevel);
                            p["@comment"].Value = Val(e.Comment);
                            p["@meta"].Value = Val(e.CommentMeta);
                            p["@project"].Value = Val(e.Project);
                            p["@client"].Value = Val(e.Client);
                            p["@case"].Value = Val(e.CaseRef);
                            p["@src"].Value = Val(e.SrcLang);
                            p["@tgt"].Value = Val(e.TgtLang);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    tx.Commit();
                }
                Interlocked.Add(ref _written, batch.Count);
            }
            catch (Exception ex)
            {
                // A failed batch is lost, deliberately. Retrying inline would
                // stall the queue behind whatever is wrong with the disk.
                Log("batch of " + batch.Count + " lost: " + ex.Message);
            }
        }

        private static object Val(string s)
        {
            return string.IsNullOrEmpty(s) ? (object)DBNull.Value : s;
        }

        /// <summary>
        /// Stops the writer and flushes what is queued. Safe to call twice, and
        /// safe to call when Start() failed or was never called.
        /// </summary>
        public void Dispose()
        {
            try
            {
                _stopping = true;
                _signal.Set();
                var w = _writer;
                if (w != null && w.IsAlive && !w.Join(TimeSpan.FromSeconds(5)))
                    Log("writer did not finish within 5s; the tail was not flushed");
            }
            catch (Exception ex)
            {
                Log("stop: " + ex.Message);
            }
            finally
            {
                try { _signal.Dispose(); } catch { }
                _writer = null;
            }
        }

        private static void Log(string message)
        {
            try { DiagnosticLog.Log("EditCapture", message); } catch { }
        }
    }
}
