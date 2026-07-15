using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using Supervertaler.Trados.Models;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Append-only log file for the Supervertaler Bridge, written to
    /// <c>UserDataPath.TradosRuntimeDir\bridge.log</c>. Visible diagnostics so
    /// users can tell whether the bridge actually started, what port it bound
    /// to, and what went wrong if it didn't. Truncated on every plugin start
    /// so the log doesn't grow without bound.
    /// </summary>
    internal static class BridgeLog
    {
        private static readonly object _lock = new object();
        private static bool _truncatedThisSession;

        // Fallback path: %TEMP%\Supervertaler-bridge.log. Used as a *second*
        // write target whenever we log, plus a *first* write target if the
        // primary UserDataPath resolution throws or the directory can't be
        // created. %TEMP% is always writable, so this guarantees we always
        // get diagnostic output somewhere even if UserDataPath is broken.
        private static string FallbackPath
        {
            get
            {
                try { return Path.Combine(Path.GetTempPath(), "Supervertaler-bridge.log"); }
                catch { return null; }
            }
        }

        public static void Write(string message)
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\r\n";
            lock (_lock)
            {
                // First-write-of-session header, mirrored to both targets.
                string header = null;
                if (!_truncatedThisSession)
                {
                    header = $"--- Bridge session started at {DateTime.Now:O} " +
                             $"(PID {System.Diagnostics.Process.GetCurrentProcess().Id}) ---\r\n";
                    _truncatedThisSession = true;

                    // Try to log the resolved UserDataPath so we can see WHERE
                    // the plugin thinks the user data folder is.
                    try
                    {
                        header += $"UserDataPath.Root  = {UserDataPath.Root}\r\n";
                        header += $"TradosRuntimeDir   = {UserDataPath.TradosRuntimeDir}\r\n";
                        header += $"SupervertalerBridgeFile = {UserDataPath.SupervertalerBridgeFile}\r\n";
                    }
                    catch (Exception ex)
                    {
                        header += $"UserDataPath resolution THREW: {ex.GetType().Name}: {ex.Message}\r\n";
                    }
                }

                // Primary target: the user's Supervertaler data folder.
                try
                {
                    Directory.CreateDirectory(UserDataPath.TradosRuntimeDir);
                    var logPath = Path.Combine(UserDataPath.TradosRuntimeDir, "bridge.log");
                    if (header != null)
                        File.WriteAllText(logPath, header);
                    File.AppendAllText(logPath, line);
                }
                catch { /* primary write failed – fallback below will catch us */ }

                // Fallback target: %TEMP%\Supervertaler-bridge.log.
                try
                {
                    var fb = FallbackPath;
                    if (fb != null)
                    {
                        if (header != null)
                            File.WriteAllText(fb, header);
                        File.AppendAllText(fb, line);
                    }
                }
                catch { /* never let logging break the caller */ }
            }
        }
    }

    // ─── DataContract types for the bridge JSON wire format ──────────────────
    //
    // These mirror the in-Trados ChatContext shape the existing AI Assistant
    // already builds, but in a serialisation-friendly form. External clients
    // (notably Supervertaler Workbench's Sidekick Chat) consume these, so any
    // changes here are a wire-format change – bump the URL path version.

    [DataContract]
    public class BridgeContextSnapshot
    {
        [DataMember(Name = "available", Order = 0)]
        public bool Available { get; set; }

        [DataMember(Name = "project", Order = 1, EmitDefaultValue = false)]
        public BridgeProjectInfo Project { get; set; }

        [DataMember(Name = "activeSegment", Order = 2, EmitDefaultValue = false)]
        public BridgeSegmentInfo ActiveSegment { get; set; }

        [DataMember(Name = "surroundingSegments", Order = 3, EmitDefaultValue = false)]
        public List<BridgeSegmentInfo> SurroundingSegments { get; set; }

        [DataMember(Name = "tmMatches", Order = 4, EmitDefaultValue = false)]
        public List<BridgeTmMatch> TmMatches { get; set; }

        [DataMember(Name = "termbaseHits", Order = 5, EmitDefaultValue = false)]
        public List<BridgeTermbaseHit> TermbaseHits { get; set; }
    }

    [DataContract]
    public class BridgeProjectInfo
    {
        [DataMember(Name = "name", Order = 0, EmitDefaultValue = false)] public string Name { get; set; }
        [DataMember(Name = "fileName", Order = 1, EmitDefaultValue = false)] public string FileName { get; set; }
        [DataMember(Name = "sourceLang", Order = 2, EmitDefaultValue = false)] public string SourceLang { get; set; }
        [DataMember(Name = "targetLang", Order = 3, EmitDefaultValue = false)] public string TargetLang { get; set; }
    }

    [DataContract]
    public class BridgeSegmentInfo
    {
        [DataMember(Name = "source", Order = 0)] public string Source { get; set; }
        [DataMember(Name = "target", Order = 1, EmitDefaultValue = false)] public string Target { get; set; }
    }

    [DataContract]
    public class BridgeTmMatch
    {
        [DataMember(Name = "score", Order = 0)] public int Score { get; set; }
        [DataMember(Name = "source", Order = 1)] public string Source { get; set; }
        [DataMember(Name = "target", Order = 2)] public string Target { get; set; }
        [DataMember(Name = "tmName", Order = 3, EmitDefaultValue = false)] public string TmName { get; set; }
    }

    [DataContract]
    public class BridgeTermbaseHit
    {
        [DataMember(Name = "source", Order = 0)] public string Source { get; set; }
        [DataMember(Name = "target", Order = 1)] public string Target { get; set; }
        [DataMember(Name = "termbaseName", Order = 2, EmitDefaultValue = false)] public string TermbaseName { get; set; }
        [DataMember(Name = "definition", Order = 3, EmitDefaultValue = false)] public string Definition { get; set; }
        [DataMember(Name = "domain", Order = 4, EmitDefaultValue = false)] public string Domain { get; set; }
        [DataMember(Name = "notes", Order = 5, EmitDefaultValue = false)] public string Notes { get; set; }
        [DataMember(Name = "nonTranslatable", Order = 6, EmitDefaultValue = false)] public bool NonTranslatable { get; set; }
    }

    [DataContract]
    internal class BridgeHandshake
    {
        [DataMember(Name = "version", Order = 0)] public int Version { get; set; }
        [DataMember(Name = "port", Order = 1)] public int Port { get; set; }
        [DataMember(Name = "token", Order = 2)] public string Token { get; set; }
        [DataMember(Name = "pid", Order = 3)] public int Pid { get; set; }
        [DataMember(Name = "startedAt", Order = 4)] public string StartedAt { get; set; }
    }

    [DataContract]
    internal class BridgeInsertRequest
    {
        [DataMember(Name = "text", IsRequired = true)] public string Text { get; set; }
    }

    [DataContract]
    internal class BridgeResultResponse
    {
        [DataMember(Name = "ok", Order = 0)] public bool Ok { get; set; }
        [DataMember(Name = "error", Order = 1, EmitDefaultValue = false)] public string Error { get; set; }
    }

    // ─── MCP endpoint types (v1: /project, /segments, /tm-search, /term-lookup) ──

    [DataContract]
    public class BridgeProjectSnapshot
    {
        [DataMember(Name = "available", Order = 0)] public bool Available { get; set; }
        [DataMember(Name = "name", Order = 1, EmitDefaultValue = false)] public string Name { get; set; }
        [DataMember(Name = "fileName", Order = 2, EmitDefaultValue = false)] public string FileName { get; set; }
        [DataMember(Name = "sourceLang", Order = 3, EmitDefaultValue = false)] public string SourceLang { get; set; }
        [DataMember(Name = "targetLang", Order = 4, EmitDefaultValue = false)] public string TargetLang { get; set; }
        [DataMember(Name = "totalSegments", Order = 5)] public int TotalSegments { get; set; }
        [DataMember(Name = "lockedSegments", Order = 6)] public int LockedSegments { get; set; }
        [DataMember(Name = "statusCounts", Order = 7, EmitDefaultValue = false)]
        public List<BridgeStatusCount> StatusCounts { get; set; }
        [DataMember(Name = "note", Order = 8, EmitDefaultValue = false)] public string Note { get; set; }
    }

    [DataContract]
    public class BridgeStatusCount
    {
        [DataMember(Name = "status", Order = 0)] public string Status { get; set; }
        [DataMember(Name = "segments", Order = 1)] public int Segments { get; set; }
    }

    /// <summary>Parsed query-string filters for GET /v1/segments.</summary>
    public class BridgeSegmentsQuery
    {
        public string Status;
        public string Contains;
        /// <summary>File id or (partial) file name – restricts results to one file of a merged document.</summary>
        public string File;
        public int Limit = 200;
        public int Offset;
    }

    [DataContract]
    public class BridgeSegmentRecord
    {
        /// <summary>Stable Trados key: "&lt;paragraphUnitId&gt;:&lt;segmentId&gt;".</summary>
        [DataMember(Name = "id", Order = 0)] public string Id { get; set; }
        [DataMember(Name = "source", Order = 1)] public string Source { get; set; }
        [DataMember(Name = "target", Order = 2, EmitDefaultValue = false)] public string Target { get; set; }
        [DataMember(Name = "status", Order = 3)] public string Status { get; set; }
        [DataMember(Name = "isLocked", Order = 4, EmitDefaultValue = false)] public bool IsLocked { get; set; }
        /// <summary>Only set on merged multi-file documents where file attribution worked.</summary>
        [DataMember(Name = "fileName", Order = 5, EmitDefaultValue = false)] public string FileName { get; set; }
    }

    [DataContract]
    public class BridgeSegmentsResponse
    {
        [DataMember(Name = "available", Order = 0)] public bool Available { get; set; }
        [DataMember(Name = "totalMatching", Order = 1)] public int TotalMatching { get; set; }
        [DataMember(Name = "returned", Order = 2)] public int Returned { get; set; }
        [DataMember(Name = "truncated", Order = 3)] public bool Truncated { get; set; }
        [DataMember(Name = "segments", Order = 4, EmitDefaultValue = false)]
        public List<BridgeSegmentRecord> Segments { get; set; }
        [DataMember(Name = "note", Order = 5, EmitDefaultValue = false)] public string Note { get; set; }
    }

    [DataContract]
    public class BridgeTmSearchResponse
    {
        [DataMember(Name = "ok", Order = 0)] public bool Ok { get; set; }
        [DataMember(Name = "error", Order = 1, EmitDefaultValue = false)] public string Error { get; set; }
        [DataMember(Name = "matches", Order = 2, EmitDefaultValue = false)]
        public List<BridgeTmMatch> Matches { get; set; }
        [DataMember(Name = "note", Order = 3, EmitDefaultValue = false)] public string Note { get; set; }
    }

    /// <summary>Parsed query for GET /v1/studio-tm-search.</summary>
    public class BridgeStudioTmQuery
    {
        public string Query;
        /// <summary>"source", "target", or "both" (default).</summary>
        public string In = "both";
        public int Limit = 10;
    }

    [DataContract]
    public class BridgeTermLookupResponse
    {
        [DataMember(Name = "ok", Order = 0)] public bool Ok { get; set; }
        [DataMember(Name = "error", Order = 1, EmitDefaultValue = false)] public string Error { get; set; }
        [DataMember(Name = "hits", Order = 2, EmitDefaultValue = false)]
        public List<BridgeTermbaseHit> Hits { get; set; }
        [DataMember(Name = "note", Order = 3, EmitDefaultValue = false)] public string Note { get; set; }
    }

    [DataContract]
    public class BridgeFileInfo
    {
        [DataMember(Name = "id", Order = 0)] public string Id { get; set; }
        [DataMember(Name = "name", Order = 1)] public string Name { get; set; }
        [DataMember(Name = "segments", Order = 2)] public int Segments { get; set; }
        [DataMember(Name = "isActive", Order = 3, EmitDefaultValue = false)] public bool IsActive { get; set; }
    }

    [DataContract]
    public class BridgeFilesResponse
    {
        [DataMember(Name = "available", Order = 0)] public bool Available { get; set; }
        [DataMember(Name = "files", Order = 1, EmitDefaultValue = false)]
        public List<BridgeFileInfo> Files { get; set; }
        [DataMember(Name = "note", Order = 2, EmitDefaultValue = false)] public string Note { get; set; }
    }

    [DataContract]
    public class BridgeInconsistencyOccurrence
    {
        [DataMember(Name = "id", Order = 0)] public string Id { get; set; }
        [DataMember(Name = "target", Order = 1, EmitDefaultValue = false)] public string Target { get; set; }
        [DataMember(Name = "status", Order = 2)] public string Status { get; set; }
        [DataMember(Name = "fileName", Order = 3, EmitDefaultValue = false)] public string FileName { get; set; }
    }

    [DataContract]
    public class BridgeInconsistencyGroup
    {
        [DataMember(Name = "source", Order = 0)] public string Source { get; set; }
        [DataMember(Name = "occurrences", Order = 1)]
        public List<BridgeInconsistencyOccurrence> Occurrences { get; set; }
    }

    [DataContract]
    public class BridgeInconsistenciesResponse
    {
        [DataMember(Name = "available", Order = 0)] public bool Available { get; set; }
        [DataMember(Name = "groupsFound", Order = 1)] public int GroupsFound { get; set; }
        [DataMember(Name = "returned", Order = 2)] public int Returned { get; set; }
        [DataMember(Name = "truncated", Order = 3)] public bool Truncated { get; set; }
        [DataMember(Name = "groups", Order = 4, EmitDefaultValue = false)]
        public List<BridgeInconsistencyGroup> Groups { get; set; }
        [DataMember(Name = "note", Order = 5, EmitDefaultValue = false)] public string Note { get; set; }
    }

    // ─── MCP write endpoints (v1: /update-segments, /add-term) ──────────────

    [DataContract]
    public class BridgeSegmentUpdate
    {
        /// <summary>Segment key as returned by /v1/segments: "&lt;paragraphUnitId&gt;:&lt;segmentId&gt;".</summary>
        [DataMember(Name = "id", IsRequired = true)] public string Id { get; set; }
        /// <summary>New target text (may contain &lt;tN&gt;/&lt;b&gt; tag markers). Null = leave target unchanged (status-only update).</summary>
        [DataMember(Name = "target", EmitDefaultValue = false)] public string Target { get; set; }
        /// <summary>ConfirmationLevel name. Null with a target write defaults to Draft.</summary>
        [DataMember(Name = "status", EmitDefaultValue = false)] public string Status { get; set; }
    }

    [DataContract]
    public class BridgeUpdateSegmentsRequest
    {
        [DataMember(Name = "updates", IsRequired = true)]
        public List<BridgeSegmentUpdate> Updates { get; set; }
    }

    [DataContract]
    public class BridgeUpdateResultItem
    {
        [DataMember(Name = "id", Order = 0)] public string Id { get; set; }
        [DataMember(Name = "ok", Order = 1)] public bool Ok { get; set; }
        [DataMember(Name = "error", Order = 2, EmitDefaultValue = false)] public string Error { get; set; }
    }

    [DataContract]
    public class BridgeUpdateSegmentsResponse
    {
        [DataMember(Name = "ok", Order = 0)] public bool Ok { get; set; }
        [DataMember(Name = "error", Order = 1, EmitDefaultValue = false)] public string Error { get; set; }
        [DataMember(Name = "applied", Order = 2)] public int Applied { get; set; }
        [DataMember(Name = "failed", Order = 3)] public int Failed { get; set; }
        [DataMember(Name = "results", Order = 4, EmitDefaultValue = false)]
        public List<BridgeUpdateResultItem> Results { get; set; }
        [DataMember(Name = "note", Order = 5, EmitDefaultValue = false)] public string Note { get; set; }
    }

    [DataContract]
    public class BridgeAddTermRequest
    {
        [DataMember(Name = "source", IsRequired = true)] public string Source { get; set; }
        [DataMember(Name = "target", IsRequired = true)] public string Target { get; set; }
    }

    [DataContract]
    public class BridgeAddTermResponse
    {
        [DataMember(Name = "ok", Order = 0)] public bool Ok { get; set; }
        [DataMember(Name = "error", Order = 1, EmitDefaultValue = false)] public string Error { get; set; }
        [DataMember(Name = "addedTo", Order = 2, EmitDefaultValue = false)]
        public List<string> AddedTo { get; set; }
        [DataMember(Name = "note", Order = 3, EmitDefaultValue = false)] public string Note { get; set; }
    }

    /// <summary>
    /// Localhost-only HTTP bridge that exposes the active Trados project context
    /// to external Supervertaler clients (currently: Workbench's Sidekick Chat).
    ///
    /// Lifecycle:
    ///   * Started by AiAssistantViewPart on plugin init when the user has
    ///     Assistant access (paid or trial) AND AiSettings.SidekickBridgeEnabled.
    ///   * Binds to <c>http://127.0.0.1:&lt;random-port&gt;/</c> – never accepts
    ///     non-loopback connections.
    ///   * Generates a fresh per-session auth token on Start; clients must
    ///     present it as <c>Authorization: Bearer &lt;token&gt;</c>.
    ///   * Writes a handshake file at <c>UserDataPath.SupervertalerBridgeFile</c>
    ///     with port + token + PID + timestamp so clients can discover it.
    ///     Deleted on Stop. Stale files from hard kills are detected by the
    ///     client checking PID liveness.
    ///
    /// Endpoints:
    ///   * <c>GET /v1/active-context</c> – returns a BridgeContextSnapshot
    ///     describing the current Trados document state (active segment,
    ///     surrounding segments, TM matches, termbase hits, project metadata).
    ///   * <c>POST /v1/insert-translation</c> – inserts text into the active
    ///     target segment via the same path the in-Trados Apply-To-Target
    ///     button uses.
    ///
    /// Threading:
    ///   * Listener runs on a dedicated background thread; one request at a
    ///     time (Trados editor operations are not concurrency-safe).
    ///   * Both endpoint handlers marshal back to the UI thread via the
    ///     supplied delegates – callers MUST be safe to invoke from any
    ///     thread; the bridge itself does not synchronise with WinForms.
    /// </summary>
    public sealed class SupervertalerBridge : IDisposable
    {
        private const int HandshakeVersion = 1;

        private readonly Func<BridgeContextSnapshot> _getContext;
        private readonly Func<string, string> _insertText; // returns null on success, error message otherwise
        private readonly Func<BridgeProjectSnapshot> _getProject;
        private readonly Func<BridgeSegmentsQuery, BridgeSegmentsResponse> _getSegments;
        private readonly Func<string> _getDbPath; // resolves supervertaler.db for TM/termbase lookups
        private readonly Func<BridgeUpdateSegmentsRequest, BridgeUpdateSegmentsResponse> _updateSegments;
        private readonly Func<BridgeAddTermRequest, BridgeAddTermResponse> _addTerm;
        private readonly Func<BridgeFilesResponse> _getFiles;
        private readonly Func<int, BridgeInconsistenciesResponse> _findInconsistencies;
        private readonly Func<BridgeStudioTmQuery, BridgeTmSearchResponse> _searchStudioTm;

        /// <summary>Max segment updates per /v1/update-segments call – keeps a
        /// single request from freezing the editor thread for minutes on huge
        /// documents; callers page through larger jobs.</summary>
        public const int MaxUpdatesPerRequest = 200;

        private HttpListener _listener;
        private Thread _listenerThread;
        private CancellationTokenSource _cts;
        private string _token;
        private int _port;
        private bool _disposed;

        public SupervertalerBridge(
            Func<BridgeContextSnapshot> getContext,
            Func<string, string> insertText,
            Func<BridgeProjectSnapshot> getProject = null,
            Func<BridgeSegmentsQuery, BridgeSegmentsResponse> getSegments = null,
            Func<string> getDbPath = null,
            Func<BridgeUpdateSegmentsRequest, BridgeUpdateSegmentsResponse> updateSegments = null,
            Func<BridgeAddTermRequest, BridgeAddTermResponse> addTerm = null,
            Func<BridgeFilesResponse> getFiles = null,
            Func<int, BridgeInconsistenciesResponse> findInconsistencies = null,
            Func<BridgeStudioTmQuery, BridgeTmSearchResponse> searchStudioTm = null)
        {
            _getContext = getContext ?? throw new ArgumentNullException(nameof(getContext));
            _insertText = insertText ?? throw new ArgumentNullException(nameof(insertText));
            _getProject = getProject;
            _getSegments = getSegments;
            _getDbPath = getDbPath;
            _updateSegments = updateSegments;
            _addTerm = addTerm;
            _getFiles = getFiles;
            _findInconsistencies = findInconsistencies;
            _searchStudioTm = searchStudioTm;
        }

        public bool IsRunning => _listener != null && _listener.IsListening;
        public int Port => _port;

        /// <summary>
        /// Start the listener. Returns silently on failure (logged to Debug)
        /// rather than throwing – the bridge is a non-essential feature and
        /// must never break the rest of the plugin.
        /// </summary>
        public void Start()
        {
            if (IsRunning)
            {
                BridgeLog.Write("Start() called but bridge already running – no-op");
                return;
            }

            BridgeLog.Write("Start() entered");
            _token = Guid.NewGuid().ToString("N");

            // HttpListener doesn't accept "port 0 = OS-pick" so we try a
            // handful of random high ports until one is free.
            var rng = new Random();
            for (int attempt = 0; attempt < 16; attempt++)
            {
                int candidate = rng.Next(49152, 65535);
                try
                {
                    var listener = new HttpListener();
                    listener.Prefixes.Add($"http://127.0.0.1:{candidate}/");
                    listener.Start();
                    _listener = listener;
                    _port = candidate;
                    BridgeLog.Write($"HttpListener bound on port {candidate} (attempt {attempt + 1})");
                    break;
                }
                catch (HttpListenerException ex)
                {
                    BridgeLog.Write($"port {candidate} bind failed: HttpListenerException code={ex.ErrorCode} message=\"{ex.Message}\"");
                }
                catch (Exception ex)
                {
                    BridgeLog.Write($"port {candidate} bind failed: {ex.GetType().Name} message=\"{ex.Message}\"");
                }
            }

            if (_listener == null)
            {
                BridgeLog.Write("FAILED: no free port could be bound after 16 attempts. " +
                    "On Windows, HttpListener may need URL ACL registration for non-admin processes – see " +
                    "`netsh http show urlacl`. Bridge disabled this session.");
                return;
            }

            _cts = new CancellationTokenSource();
            _listenerThread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "SupervertalerBridge"
            };
            _listenerThread.Start();
            BridgeLog.Write("listener thread started");

            try
            {
                WriteHandshakeFile();
                BridgeLog.Write($"handshake file written at {UserDataPath.SupervertalerBridgeFile}");
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"FAILED to write handshake file: {ex.GetType().Name}: {ex.Message}");
                // Bridge is still usable, just not discoverable – not fatal.
            }

            BridgeLog.Write($"Start() complete. Bridge live on http://127.0.0.1:{_port}/ with token {_token.Substring(0, 8)}…");
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
            try { _listener?.Stop(); } catch { /* ignore */ }
            try { _listener?.Close(); } catch { /* ignore */ }
            _listener = null;

            try
            {
                if (File.Exists(UserDataPath.SupervertalerBridgeFile))
                    File.Delete(UserDataPath.SupervertalerBridgeFile);
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] failed to delete handshake file: {ex.Message}");
            }

            // Don't Join the thread – HttpListener.Stop unblocks GetContext
            // but the thread cleanup is best-effort. It's a background thread
            // and will die with the process anyway.
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _cts?.Dispose();
        }

        // ── Listener loop ────────────────────────────────────────────────

        private void ListenLoop()
        {
            while (_listener != null && _listener.IsListening && !_cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = _listener.GetContext();
                }
                catch (HttpListenerException)
                {
                    // Listener.Stop() unblocks with this exception – clean shutdown
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    BridgeLog.Write($"[SupervertalerBridge] GetContext failed: {ex.Message}");
                    return;
                }

                try
                {
                    HandleRequest(context);
                }
                catch (Exception ex)
                {
                    BridgeLog.Write($"[SupervertalerBridge] HandleRequest threw: {ex.Message}");
                    TryWriteError(context, 500, "internal error");
                }
                finally
                {
                    try { context.Response.Close(); } catch { /* ignore */ }
                }
            }
        }

        private void HandleRequest(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            // Defence in depth: HttpListener already binds to 127.0.0.1 so
            // remote requests can't reach us, but we double-check the
            // remote address for paranoia (and to fail loud if the binding
            // ever drifts).
            if (request.RemoteEndPoint == null
                || !IPAddress.IsLoopback(request.RemoteEndPoint.Address))
            {
                TryWriteError(context, 403, "loopback only");
                return;
            }

            // Bearer token auth
            var authHeader = request.Headers["Authorization"] ?? "";
            const string prefix = "Bearer ";
            if (!authHeader.StartsWith(prefix, StringComparison.Ordinal)
                || authHeader.Substring(prefix.Length) != _token)
            {
                TryWriteError(context, 401, "unauthorized");
                return;
            }

            var path = request.Url.AbsolutePath;
            var method = request.HttpMethod;

            if (method == "GET" && path == "/v1/active-context")
            {
                HandleGetActiveContext(context);
                return;
            }

            if (method == "POST" && path == "/v1/insert-translation")
            {
                HandleInsertTranslation(context);
                return;
            }

            if (method == "GET" && path == "/v1/project")
            {
                HandleGetProject(context);
                return;
            }

            if (method == "GET" && path == "/v1/segments")
            {
                HandleGetSegments(context);
                return;
            }

            if (method == "GET" && path == "/v1/tm-search")
            {
                HandleTmSearch(context);
                return;
            }

            if (method == "GET" && path == "/v1/term-lookup")
            {
                HandleTermLookup(context);
                return;
            }

            if (method == "POST" && path == "/v1/update-segments")
            {
                HandleUpdateSegments(context);
                return;
            }

            if (method == "POST" && path == "/v1/add-term")
            {
                HandleAddTerm(context);
                return;
            }

            if (method == "GET" && path == "/v1/files")
            {
                HandleGetFiles(context);
                return;
            }

            if (method == "GET" && path == "/v1/statistics")
            {
                HandleGetStatistics(context);
                return;
            }

            if (method == "GET" && path == "/v1/inconsistencies")
            {
                HandleGetInconsistencies(context);
                return;
            }

            if (method == "GET" && path == "/v1/studio-tm-search")
            {
                HandleStudioTmSearch(context);
                return;
            }

            TryWriteError(context, 404, "not found");
        }

        private void HandleGetActiveContext(HttpListenerContext context)
        {
            BridgeContextSnapshot snapshot;
            try
            {
                snapshot = _getContext() ?? new BridgeContextSnapshot { Available = false };
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] context provider threw: {ex.Message}");
                snapshot = new BridgeContextSnapshot { Available = false };
            }

            WriteJson(context, 200, snapshot);
        }

        private void HandleInsertTranslation(HttpListenerContext context)
        {
            BridgeInsertRequest req;
            try
            {
                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                {
                    var body = reader.ReadToEnd();
                    if (string.IsNullOrWhiteSpace(body))
                    {
                        WriteJson(context, 400, new BridgeResultResponse { Ok = false, Error = "empty body" });
                        return;
                    }
                    req = DeserializeJson<BridgeInsertRequest>(body);
                }
            }
            catch (Exception ex)
            {
                WriteJson(context, 400, new BridgeResultResponse { Ok = false, Error = "malformed body: " + ex.Message });
                return;
            }

            if (req == null || string.IsNullOrEmpty(req.Text))
            {
                WriteJson(context, 400, new BridgeResultResponse { Ok = false, Error = "missing 'text'" });
                return;
            }

            string err;
            try
            {
                err = _insertText(req.Text); // null on success
            }
            catch (Exception ex)
            {
                err = "insert failed: " + ex.Message;
            }

            if (err == null)
                WriteJson(context, 200, new BridgeResultResponse { Ok = true });
            else
                WriteJson(context, 409, new BridgeResultResponse { Ok = false, Error = err });
        }

        // ── MCP endpoints (v1) ───────────────────────────────────────────
        //
        // Consumed by the Supervertaler MCP Server (src/Supervertaler.McpServer),
        // which fronts this bridge for AI apps speaking the Model Context
        // Protocol. Same rules as the original endpoints: loopback + bearer
        // token, one request at a time, delegates marshal to the UI thread.

        private void HandleGetProject(HttpListenerContext context)
        {
            if (_getProject == null)
            {
                TryWriteError(context, 501, "project endpoint not wired");
                return;
            }

            BridgeProjectSnapshot snapshot;
            try
            {
                snapshot = _getProject() ?? new BridgeProjectSnapshot { Available = false };
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] project provider threw: {ex.Message}");
                snapshot = new BridgeProjectSnapshot { Available = false };
            }

            WriteJson(context, 200, snapshot);
        }

        private void HandleGetSegments(HttpListenerContext context)
        {
            if (_getSegments == null)
            {
                TryWriteError(context, 501, "segments endpoint not wired");
                return;
            }

            var qs = context.Request.QueryString;
            var query = new BridgeSegmentsQuery
            {
                Status = qs["status"],
                Contains = qs["contains"],
                File = qs["file"]
            };
            int limit, offset;
            if (int.TryParse(qs["limit"], out limit) && limit > 0)
                query.Limit = Math.Min(limit, 2000);
            if (int.TryParse(qs["offset"], out offset) && offset > 0)
                query.Offset = offset;

            BridgeSegmentsResponse response;
            try
            {
                response = _getSegments(query) ?? new BridgeSegmentsResponse { Available = false };
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] segments provider threw: {ex.Message}");
                response = new BridgeSegmentsResponse
                {
                    Available = false,
                    Note = "internal error reading segments: " + ex.Message
                };
            }

            WriteJson(context, 200, response);
        }

        private void HandleTmSearch(HttpListenerContext context)
        {
            var query = context.Request.QueryString["q"];
            if (string.IsNullOrWhiteSpace(query))
            {
                WriteJson(context, 400, new BridgeTmSearchResponse { Ok = false, Error = "missing 'q'" });
                return;
            }

            int limit;
            if (!int.TryParse(context.Request.QueryString["limit"], out limit) || limit <= 0)
                limit = 5;
            limit = Math.Min(limit, 50);

            var dbPath = ResolveDbPathSafe();
            if (dbPath == null)
            {
                WriteJson(context, 200, new BridgeTmSearchResponse
                {
                    Ok = false,
                    Error = "Supervertaler database (supervertaler.db) not found. Set the termbase/database " +
                            "path in the Supervertaler for Trados settings."
                });
                return;
            }

            var response = new BridgeTmSearchResponse { Ok = true, Matches = new List<BridgeTmMatch>() };
            try
            {
                using (var reader = new TmReader(dbPath))
                {
                    if (!reader.Open())
                    {
                        WriteJson(context, 200, new BridgeTmSearchResponse
                        {
                            Ok = false,
                            Error = "could not open Supervertaler database: " + (reader.LastError ?? "unknown error")
                        });
                        return;
                    }

                    var tms = reader.GetBridgedTms();
                    if (tms == null || tms.Count == 0)
                    {
                        response.Note = "No Supervertaler TMs are bridged to Trados. Enable 'Bridge to Trados' " +
                                        "on the relevant TMs in the Supervertaler Workbench to make them searchable.";
                        WriteJson(context, 200, response);
                        return;
                    }

                    // Exact hits first, then phrase-concordance hits; dedupe on
                    // (source, target) across both passes and all TMs.
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var tm in tms)
                    {
                        var hits = new List<BridgedTu>();
                        hits.AddRange(reader.SearchExact(tm.TmId, query, limit));
                        hits.AddRange(reader.SearchConcordance(tm.TmId, query, limit));

                        foreach (var tu in hits)
                        {
                            if (response.Matches.Count >= limit) break;
                            var key = (tu.SourceText ?? "") + "" + (tu.TargetText ?? "");
                            if (!seen.Add(key)) continue;
                            response.Matches.Add(new BridgeTmMatch
                            {
                                Score = tu.Score,
                                Source = tu.SourceText ?? "",
                                Target = tu.TargetText ?? "",
                                TmName = tm.Name
                            });
                        }
                        if (response.Matches.Count >= limit) break;
                    }

                    if (response.Matches.Count == 0)
                        response.Note = "No exact or phrase-concordance matches. Try a shorter, more " +
                                        "distinctive phrase from the segment.";
                }
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] tm-search threw: {ex.Message}");
                response = new BridgeTmSearchResponse { Ok = false, Error = "tm search failed: " + ex.Message };
            }

            WriteJson(context, 200, response);
        }

        private void HandleTermLookup(HttpListenerContext context)
        {
            var term = context.Request.QueryString["q"];
            if (string.IsNullOrWhiteSpace(term))
            {
                WriteJson(context, 400, new BridgeTermLookupResponse { Ok = false, Error = "missing 'q'" });
                return;
            }

            var dbPath = ResolveDbPathSafe();
            if (dbPath == null)
            {
                WriteJson(context, 200, new BridgeTermLookupResponse
                {
                    Ok = false,
                    Error = "Supervertaler database (supervertaler.db) not found. Set the termbase/database " +
                            "path in the Supervertaler for Trados settings."
                });
                return;
            }

            var response = new BridgeTermLookupResponse { Ok = true, Hits = new List<BridgeTermbaseHit>() };
            try
            {
                using (var reader = new TermbaseReader(dbPath))
                {
                    if (!reader.Open())
                    {
                        WriteJson(context, 200, new BridgeTermLookupResponse
                        {
                            Ok = false,
                            Error = "could not open Supervertaler database"
                        });
                        return;
                    }

                    // Exact/normalized match first; if nothing is stored under
                    // that exact form, fall back to substring so inflected or
                    // partial queries still surface the relevant entries.
                    var entries = reader.SearchTerm(term.Trim()) ?? new List<TermEntry>();
                    if (entries.Count == 0)
                    {
                        entries = reader.SearchTermSubstring(term.Trim()) ?? new List<TermEntry>();
                        if (entries.Count > 0)
                            response.Note = "No exact termbase entry for the query; these are substring " +
                                            "matches (query text appears inside the source or target term).";
                    }
                    foreach (var entry in entries)
                    {
                        response.Hits.Add(new BridgeTermbaseHit
                        {
                            Source = entry.SourceTerm ?? "",
                            Target = entry.TargetTerm ?? "",
                            TermbaseName = entry.TermbaseName,
                            Definition = entry.Definition,
                            Domain = entry.Domain,
                            Notes = entry.Notes,
                            NonTranslatable = entry.IsNonTranslatable
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] term-lookup threw: {ex.Message}");
                response = new BridgeTermLookupResponse { Ok = false, Error = "term lookup failed: " + ex.Message };
            }

            WriteJson(context, 200, response);
        }

        private void HandleUpdateSegments(HttpListenerContext context)
        {
            if (_updateSegments == null)
            {
                TryWriteError(context, 501, "update-segments endpoint not wired");
                return;
            }

            BridgeUpdateSegmentsRequest req;
            try
            {
                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                {
                    req = DeserializeJson<BridgeUpdateSegmentsRequest>(reader.ReadToEnd());
                }
            }
            catch (Exception ex)
            {
                WriteJson(context, 400, new BridgeUpdateSegmentsResponse
                {
                    Ok = false,
                    Error = "malformed body: " + ex.Message
                });
                return;
            }

            if (req?.Updates == null || req.Updates.Count == 0)
            {
                WriteJson(context, 400, new BridgeUpdateSegmentsResponse
                {
                    Ok = false,
                    Error = "missing 'updates' array"
                });
                return;
            }

            if (req.Updates.Count > MaxUpdatesPerRequest)
            {
                WriteJson(context, 400, new BridgeUpdateSegmentsResponse
                {
                    Ok = false,
                    Error = $"too many updates in one call ({req.Updates.Count}); the maximum is " +
                            $"{MaxUpdatesPerRequest}. Split the job into batches of at most " +
                            $"{MaxUpdatesPerRequest} and call this endpoint once per batch."
                });
                return;
            }

            BridgeUpdateSegmentsResponse response;
            try
            {
                response = _updateSegments(req) ?? new BridgeUpdateSegmentsResponse
                {
                    Ok = false,
                    Error = "internal error"
                };
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] update-segments threw: {ex.Message}");
                response = new BridgeUpdateSegmentsResponse
                {
                    Ok = false,
                    Error = "update failed: " + ex.Message
                };
            }

            WriteJson(context, 200, response);
        }

        private void HandleAddTerm(HttpListenerContext context)
        {
            if (_addTerm == null)
            {
                TryWriteError(context, 501, "add-term endpoint not wired");
                return;
            }

            BridgeAddTermRequest req;
            try
            {
                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                {
                    req = DeserializeJson<BridgeAddTermRequest>(reader.ReadToEnd());
                }
            }
            catch (Exception ex)
            {
                WriteJson(context, 400, new BridgeAddTermResponse
                {
                    Ok = false,
                    Error = "malformed body: " + ex.Message
                });
                return;
            }

            if (string.IsNullOrWhiteSpace(req?.Source) || string.IsNullOrWhiteSpace(req?.Target))
            {
                WriteJson(context, 400, new BridgeAddTermResponse
                {
                    Ok = false,
                    Error = "both 'source' and 'target' are required"
                });
                return;
            }

            BridgeAddTermResponse response;
            try
            {
                response = _addTerm(req) ?? new BridgeAddTermResponse
                {
                    Ok = false,
                    Error = "internal error"
                };
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] add-term threw: {ex.Message}");
                response = new BridgeAddTermResponse
                {
                    Ok = false,
                    Error = "add term failed: " + ex.Message
                };
            }

            WriteJson(context, 200, response);
        }

        private void HandleGetFiles(HttpListenerContext context)
        {
            if (_getFiles == null)
            {
                TryWriteError(context, 501, "files endpoint not wired");
                return;
            }

            BridgeFilesResponse response;
            try
            {
                response = _getFiles() ?? new BridgeFilesResponse { Available = false };
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] files provider threw: {ex.Message}");
                response = new BridgeFilesResponse { Available = false, Note = "error: " + ex.Message };
            }

            WriteJson(context, 200, response);
        }

        private void HandleGetStatistics(HttpListenerContext context)
        {
            // Statistics come from the .sdlproj / projects.xml on disk via
            // TradosTools – no editor state needed, so no UI-thread hop for
            // the numbers themselves. The project name defaults to the one
            // open in the editor (via the project delegate).
            var projectName = context.Request.QueryString["project"];
            if (string.IsNullOrWhiteSpace(projectName))
            {
                try { projectName = _getProject?.Invoke()?.Name; } catch { }
            }

            if (string.IsNullOrWhiteSpace(projectName))
            {
                TryWriteError(context, 400,
                    "no project name given and no project is open in the editor – pass ?project=<name>");
                return;
            }

            string stats, fileStatus;
            try
            {
                var input = SerializeProjectNameJson(projectName);
                stats = TradosTools.ExecuteTool("studio_get_project_statistics", input);
                fileStatus = TradosTools.ExecuteTool("studio_get_file_status", input);
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] statistics threw: {ex.Message}");
                TryWriteError(context, 500, "statistics failed: " + ex.Message);
                return;
            }

            // TradosTools returns ready-made JSON – embed it verbatim.
            WriteRawJson(context, 200,
                "{\"ok\":true,\"project\":" + JsonQuote(projectName) +
                ",\"analysisStatistics\":" + (stats ?? "null") +
                ",\"confirmationStatistics\":" + (fileStatus ?? "null") + "}");
        }

        private void HandleStudioTmSearch(HttpListenerContext context)
        {
            if (_searchStudioTm == null)
            {
                TryWriteError(context, 501, "studio-tm-search endpoint not wired");
                return;
            }

            var query = context.Request.QueryString["q"];
            if (string.IsNullOrWhiteSpace(query))
            {
                WriteJson(context, 400, new BridgeTmSearchResponse { Ok = false, Error = "missing 'q'" });
                return;
            }

            var q = new BridgeStudioTmQuery { Query = query };
            var inParam = context.Request.QueryString["in"];
            if (!string.IsNullOrEmpty(inParam)) q.In = inParam;
            int limit;
            if (int.TryParse(context.Request.QueryString["limit"], out limit) && limit > 0)
                q.Limit = Math.Min(limit, 50);

            BridgeTmSearchResponse response;
            try
            {
                response = _searchStudioTm(q) ?? new BridgeTmSearchResponse { Ok = false, Error = "internal error" };
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] studio-tm-search threw: {ex.Message}");
                response = new BridgeTmSearchResponse { Ok = false, Error = "studio TM search failed: " + ex.Message };
            }

            WriteJson(context, 200, response);
        }

        private void HandleGetInconsistencies(HttpListenerContext context)
        {
            if (_findInconsistencies == null)
            {
                TryWriteError(context, 501, "inconsistencies endpoint not wired");
                return;
            }

            int limit;
            if (!int.TryParse(context.Request.QueryString["limit"], out limit) || limit <= 0)
                limit = 50;
            limit = Math.Min(limit, 200);

            BridgeInconsistenciesResponse response;
            try
            {
                response = _findInconsistencies(limit) ?? new BridgeInconsistenciesResponse { Available = false };
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] inconsistencies threw: {ex.Message}");
                response = new BridgeInconsistenciesResponse
                {
                    Available = false,
                    Note = "error finding inconsistencies: " + ex.Message
                };
            }

            WriteJson(context, 200, response);
        }

        private static string SerializeProjectNameJson(string projectName)
            => "{\"project_name\":" + JsonQuote(projectName) + "}";

        private static string JsonQuote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.AppendFormat("\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        private static void WriteRawJson(HttpListenerContext context, int statusCode, string json)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                context.Response.StatusCode = statusCode;
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] WriteRawJson failed: {ex.Message}");
            }
        }

        private string ResolveDbPathSafe()
        {
            try
            {
                var path = _getDbPath?.Invoke();
                return !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;
            }
            catch
            {
                return null;
            }
        }

        // ── Handshake file ───────────────────────────────────────────────

        private void WriteHandshakeFile()
        {
            Directory.CreateDirectory(UserDataPath.TradosRuntimeDir);

            var handshake = new BridgeHandshake
            {
                Version = HandshakeVersion,
                Port = _port,
                Token = _token,
                Pid = Process.GetCurrentProcess().Id,
                StartedAt = DateTime.UtcNow.ToString("o")
            };

            var bytes = SerializeJson(handshake);
            File.WriteAllBytes(UserDataPath.SupervertalerBridgeFile, bytes);
        }

        // ── JSON helpers ─────────────────────────────────────────────────

        private static void WriteJson<T>(HttpListenerContext context, int statusCode, T payload)
        {
            try
            {
                var bytes = SerializeJson(payload);
                context.Response.StatusCode = statusCode;
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] WriteJson failed: {ex.Message}");
            }
        }

        private static void TryWriteError(HttpListenerContext context, int statusCode, string message)
        {
            try
            {
                WriteJson(context, statusCode, new BridgeResultResponse { Ok = false, Error = message });
            }
            catch { /* nothing more we can do */ }
        }

        private static byte[] SerializeJson<T>(T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var ms = new MemoryStream())
            {
                serializer.WriteObject(ms, value);
                return ms.ToArray();
            }
        }

        private static T DeserializeJson<T>(string json)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return (T)serializer.ReadObject(ms);
            }
        }
    }
}
