using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    public class BridgeResultResponse
    {
        [DataMember(Name = "ok", Order = 0)] public bool Ok { get; set; }
        [DataMember(Name = "error", Order = 1, EmitDefaultValue = false)] public string Error { get; set; }
        [DataMember(Name = "note", Order = 2, EmitDefaultValue = false)] public string Note { get; set; }
    }

    // ─── Prompt-library endpoint types (v1: /prompts, /prompt, /save-prompt) ──

    [DataContract]
    public class BridgePromptInfo
    {
        [DataMember(Name = "name", Order = 0)] public string Name { get; set; }
        [DataMember(Name = "description", Order = 1, EmitDefaultValue = false)] public string Description { get; set; }
        [DataMember(Name = "category", Order = 2, EmitDefaultValue = false)] public string Category { get; set; }
        [DataMember(Name = "relativePath", Order = 3)] public string RelativePath { get; set; }
        [DataMember(Name = "type", Order = 4, EmitDefaultValue = false)] public string Type { get; set; }
        [DataMember(Name = "isDefault", Order = 5)] public bool IsDefault { get; set; }
        [DataMember(Name = "isQuickLauncher", Order = 6)] public bool IsQuickLauncher { get; set; }
        [DataMember(Name = "isReadOnly", Order = 7)] public bool IsReadOnly { get; set; }
    }

    [DataContract]
    public class BridgePromptListResponse
    {
        [DataMember(Name = "ok", Order = 0)] public bool Ok { get; set; }
        [DataMember(Name = "count", Order = 1)] public int Count { get; set; }
        [DataMember(Name = "promptsFolder", Order = 2, EmitDefaultValue = false)] public string PromptsFolder { get; set; }
        [DataMember(Name = "prompts", Order = 3, EmitDefaultValue = false)] public List<BridgePromptInfo> Prompts { get; set; }
    }

    [DataContract]
    public class BridgePromptResponse
    {
        [DataMember(Name = "ok", Order = 0)] public bool Ok { get; set; }
        [DataMember(Name = "error", Order = 1, EmitDefaultValue = false)] public string Error { get; set; }
        [DataMember(Name = "name", Order = 2, EmitDefaultValue = false)] public string Name { get; set; }
        [DataMember(Name = "description", Order = 3, EmitDefaultValue = false)] public string Description { get; set; }
        [DataMember(Name = "category", Order = 4, EmitDefaultValue = false)] public string Category { get; set; }
        [DataMember(Name = "relativePath", Order = 5, EmitDefaultValue = false)] public string RelativePath { get; set; }
        [DataMember(Name = "type", Order = 6, EmitDefaultValue = false)] public string Type { get; set; }
        [DataMember(Name = "isDefault", Order = 7)] public bool IsDefault { get; set; }
        [DataMember(Name = "isReadOnly", Order = 8)] public bool IsReadOnly { get; set; }
        [DataMember(Name = "content", Order = 9, EmitDefaultValue = false)] public string Content { get; set; }
    }

    [DataContract]
    internal class BridgeSavePromptRequest
    {
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "content")] public string Content { get; set; }
        [DataMember(Name = "description")] public string Description { get; set; }
        [DataMember(Name = "category")] public string Category { get; set; }
        [DataMember(Name = "path")] public string Path { get; set; }
    }

    [DataContract]
    public class BridgeSavePromptResponse
    {
        [DataMember(Name = "ok", Order = 0)] public bool Ok { get; set; }
        [DataMember(Name = "error", Order = 1, EmitDefaultValue = false)] public string Error { get; set; }
        [DataMember(Name = "created", Order = 2)] public bool Created { get; set; }
        [DataMember(Name = "name", Order = 3, EmitDefaultValue = false)] public string Name { get; set; }
        [DataMember(Name = "relativePath", Order = 4, EmitDefaultValue = false)] public string RelativePath { get; set; }
        [DataMember(Name = "promptsFolder", Order = 5, EmitDefaultValue = false)] public string PromptsFolder { get; set; }
    }

    [DataContract]
    public class BridgeHelpResponse
    {
        [DataMember(Name = "ok", Order = 0)] public bool Ok { get; set; }
        [DataMember(Name = "help", Order = 1, EmitDefaultValue = false)] public string Help { get; set; }
    }

    [DataContract]
    public class BridgeTextPair
    {
        [DataMember(Name = "source", Order = 0)] public string Source { get; set; }
        [DataMember(Name = "target", Order = 1, EmitDefaultValue = false)] public string Target { get; set; }
    }

    [DataContract]
    public class BridgePromptContextResponse
    {
        [DataMember(Name = "ok", Order = 0)] public bool Ok { get; set; }
        [DataMember(Name = "error", Order = 1, EmitDefaultValue = false)] public string Error { get; set; }
        [DataMember(Name = "sourceLang", Order = 2, EmitDefaultValue = false)] public string SourceLang { get; set; }
        [DataMember(Name = "targetLang", Order = 3, EmitDefaultValue = false)] public string TargetLang { get; set; }
        [DataMember(Name = "segmentCount", Order = 4)] public int SegmentCount { get; set; }
        [DataMember(Name = "returnedSegments", Order = 5)] public int ReturnedSegments { get; set; }
        [DataMember(Name = "wordCount", Order = 6)] public int WordCount { get; set; }
        [DataMember(Name = "domain", Order = 7, EmitDefaultValue = false)] public string Domain { get; set; }
        [DataMember(Name = "truncated", Order = 8)] public bool Truncated { get; set; }
        [DataMember(Name = "sourceText", Order = 9, EmitDefaultValue = false)] public string SourceText { get; set; }
        [DataMember(Name = "terms", Order = 10, EmitDefaultValue = false)] public List<BridgeTextPair> Terms { get; set; }
        [DataMember(Name = "tmPairs", Order = 11, EmitDefaultValue = false)] public List<BridgeTextPair> TmPairs { get; set; }
        [DataMember(Name = "currentDefaultPrompt", Order = 12, EmitDefaultValue = false)] public string CurrentDefaultPrompt { get; set; }
        [DataMember(Name = "note", Order = 13, EmitDefaultValue = false)] public string Note { get; set; }
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

        // In-process only (no [DataMember] – never serialised to the wire): the
        // full path to the open project's .sdlproj, so /v1/statistics can read
        // the analysis report from the live project instead of resolving the
        // name through projects.xml.
        public string SdlprojPath { get; set; }
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
        /// <summary>The segment number shown in Studio's grid – restarts per file in merged documents.</summary>
        [DataMember(Name = "number", Order = 6, EmitDefaultValue = false)] public string Number { get; set; }
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

    /// <summary>Parsed query for GET /v1/qa-check.</summary>
    public class BridgeQaQuery
    {
        /// <summary>"numbers", "tags", or "terminology".</summary>
        public string Type;
        public int Limit = 50;
    }

    [DataContract]
    public class BridgeQaIssue
    {
        [DataMember(Name = "id", Order = 0)] public string Id { get; set; }
        [DataMember(Name = "status", Order = 1)] public string Status { get; set; }
        [DataMember(Name = "detail", Order = 2)] public string Detail { get; set; }
        [DataMember(Name = "source", Order = 3, EmitDefaultValue = false)] public string Source { get; set; }
        [DataMember(Name = "target", Order = 4, EmitDefaultValue = false)] public string Target { get; set; }
        [DataMember(Name = "fileName", Order = 5, EmitDefaultValue = false)] public string FileName { get; set; }
    }

    [DataContract]
    public class BridgeQaResponse
    {
        [DataMember(Name = "available", Order = 0)] public bool Available { get; set; }
        [DataMember(Name = "check", Order = 1, EmitDefaultValue = false)] public string Check { get; set; }
        [DataMember(Name = "segmentsChecked", Order = 2)] public int SegmentsChecked { get; set; }
        [DataMember(Name = "issuesFound", Order = 3)] public int IssuesFound { get; set; }
        [DataMember(Name = "returned", Order = 4)] public int Returned { get; set; }
        [DataMember(Name = "truncated", Order = 5)] public bool Truncated { get; set; }
        [DataMember(Name = "issues", Order = 6, EmitDefaultValue = false)]
        public List<BridgeQaIssue> Issues { get; set; }
        [DataMember(Name = "note", Order = 7, EmitDefaultValue = false)] public string Note { get; set; }
        /// <summary>Terminology check only: findings grouped per term, most-affected first.</summary>
        [DataMember(Name = "termGroups", Order = 8, EmitDefaultValue = false)]
        public List<BridgeQaTermGroup> TermGroups { get; set; }
        [DataMember(Name = "termsAffected", Order = 9, EmitDefaultValue = false)]
        public int TermsAffected { get; set; }
    }

    [DataContract]
    public class BridgeQaTermGroup
    {
        [DataMember(Name = "term", Order = 0)] public string Term { get; set; }
        [DataMember(Name = "termbase", Order = 1)] public string Termbase { get; set; }
        [DataMember(Name = "expected", Order = 2)] public List<string> Expected { get; set; }
        [DataMember(Name = "segmentsAffected", Order = 3)] public int SegmentsAffected { get; set; }
        [DataMember(Name = "sampleSegmentIds", Order = 4, EmitDefaultValue = false)]
        public List<string> SampleSegmentIds { get; set; }
        [DataMember(Name = "exampleTarget", Order = 5, EmitDefaultValue = false)]
        public string ExampleTarget { get; set; }
    }

    [DataContract]
    public class BridgeTmResource
    {
        [DataMember(Name = "name", Order = 0)] public string Name { get; set; }
        /// <summary>"studio-file", "studio-server", or "supervertaler".</summary>
        [DataMember(Name = "kind", Order = 1)] public string Kind { get; set; }
        [DataMember(Name = "languages", Order = 2, EmitDefaultValue = false)] public string Languages { get; set; }
        [DataMember(Name = "entries", Order = 3, EmitDefaultValue = false)] public int Entries { get; set; }
    }

    [DataContract]
    public class BridgeTermbaseResource
    {
        [DataMember(Name = "name", Order = 0)] public string Name { get; set; }
        [DataMember(Name = "languages", Order = 1, EmitDefaultValue = false)] public string Languages { get; set; }
        [DataMember(Name = "terms", Order = 2)] public int Terms { get; set; }
        [DataMember(Name = "isProjectTermbase", Order = 3, EmitDefaultValue = false)] public bool IsProjectTermbase { get; set; }
        [DataMember(Name = "readEnabled", Order = 4)] public bool ReadEnabled { get; set; }
        [DataMember(Name = "writeEnabled", Order = 5)] public bool WriteEnabled { get; set; }
        /// <summary>"supervertaler", "trados-ttb" (Studio 2026), or "multiterm" (.sdltb).</summary>
        [DataMember(Name = "kind", Order = 6, EmitDefaultValue = false)] public string Kind { get; set; }
    }

    [DataContract]
    public class BridgeResourcesResponse
    {
        [DataMember(Name = "available", Order = 0)] public bool Available { get; set; }
        [DataMember(Name = "tms", Order = 1, EmitDefaultValue = false)]
        public List<BridgeTmResource> Tms { get; set; }
        [DataMember(Name = "termbases", Order = 2, EmitDefaultValue = false)]
        public List<BridgeTermbaseResource> Termbases { get; set; }
        [DataMember(Name = "note", Order = 3, EmitDefaultValue = false)] public string Note { get; set; }
    }

    [DataContract]
    public class BridgeGoToRequest
    {
        /// <summary>Full id "puId:segId" – or leave null and use File+Number.</summary>
        [DataMember(Name = "id", EmitDefaultValue = false)] public string Id { get; set; }
        /// <summary>File id or (partial) name, for Number-based addressing in merged documents.</summary>
        [DataMember(Name = "file", EmitDefaultValue = false)] public string File { get; set; }
        /// <summary>The segment number as displayed in Studio's grid (per file).</summary>
        [DataMember(Name = "number", EmitDefaultValue = false)] public string Number { get; set; }
    }

    [DataContract]
    public class BridgeCommentInfo
    {
        [DataMember(Name = "index", Order = 0)] public int Index { get; set; }
        [DataMember(Name = "author", Order = 1, EmitDefaultValue = false)] public string Author { get; set; }
        [DataMember(Name = "date", Order = 2, EmitDefaultValue = false)] public string Date { get; set; }
        [DataMember(Name = "severity", Order = 3, EmitDefaultValue = false)] public string Severity { get; set; }
        [DataMember(Name = "text", Order = 4)] public string Text { get; set; }
    }

    [DataContract]
    public class BridgeCommentsResponse
    {
        [DataMember(Name = "ok", Order = 0)] public bool Ok { get; set; }
        [DataMember(Name = "error", Order = 1, EmitDefaultValue = false)] public string Error { get; set; }
        [DataMember(Name = "comments", Order = 2, EmitDefaultValue = false)]
        public List<BridgeCommentInfo> Comments { get; set; }
        [DataMember(Name = "note", Order = 3, EmitDefaultValue = false)] public string Note { get; set; }
    }

    [DataContract]
    public class BridgeAddCommentRequest
    {
        [DataMember(Name = "id", IsRequired = true)] public string Id { get; set; }
        [DataMember(Name = "text", IsRequired = true)] public string Text { get; set; }
        /// <summary>"Low" (informational, default), "Medium" (warning), or "High" (error).</summary>
        [DataMember(Name = "severity", EmitDefaultValue = false)] public string Severity { get; set; }
    }

    [DataContract]
    public class BridgeUpdateCommentRequest
    {
        [DataMember(Name = "id", IsRequired = true)] public string Id { get; set; }
        /// <summary>Comment index as returned by /v1/comments for this segment.</summary>
        [DataMember(Name = "commentIndex", IsRequired = true)] public int CommentIndex { get; set; }
        [DataMember(Name = "text", IsRequired = true)] public string Text { get; set; }
    }

    [DataContract]
    public class BridgeVerifyFinding
    {
        [DataMember(Name = "file", Order = 0, EmitDefaultValue = false)] public string File { get; set; }
        [DataMember(Name = "number", Order = 1, EmitDefaultValue = false)] public string Number { get; set; }
        /// <summary>Full segment id "puId:segId" – pass to go_to_segment / add_comment / update_segments.</summary>
        [DataMember(Name = "id", Order = 2, EmitDefaultValue = false)] public string Id { get; set; }
        [DataMember(Name = "severity", Order = 3, EmitDefaultValue = false)] public string Severity { get; set; }
        /// <summary>The QA rule/category, e.g. "QA Checker 3.0", "Tag Verifier".</summary>
        [DataMember(Name = "origin", Order = 4, EmitDefaultValue = false)] public string Origin { get; set; }
        [DataMember(Name = "message", Order = 5)] public string Message { get; set; }
    }

    [DataContract]
    public class BridgeRunTaskRequest
    {
        /// <summary>"pretranslate", "update-tm", or "export-target".</summary>
        [DataMember(Name = "task", IsRequired = true)] public string Task { get; set; }
    }

    [DataContract]
    public class BridgeRunTaskResponse
    {
        [DataMember(Name = "ok", Order = 0)] public bool Ok { get; set; }
        [DataMember(Name = "error", Order = 1, EmitDefaultValue = false)] public string Error { get; set; }
        [DataMember(Name = "task", Order = 2, EmitDefaultValue = false)] public string Task { get; set; }
        [DataMember(Name = "filesProcessed", Order = 3)] public int FilesProcessed { get; set; }
        [DataMember(Name = "messages", Order = 4, EmitDefaultValue = false)]
        public List<string> Messages { get; set; }
        [DataMember(Name = "note", Order = 5, EmitDefaultValue = false)] public string Note { get; set; }
        // Async: batch tasks now start in the background and return immediately.
        [DataMember(Name = "started", Order = 6, EmitDefaultValue = false)] public bool Started { get; set; }
        [DataMember(Name = "jobId", Order = 7, EmitDefaultValue = false)] public string JobId { get; set; }
    }

    [DataContract]
    public class BridgeTaskStatusResponse
    {
        [DataMember(Name = "ok", Order = 0)] public bool Ok { get; set; }
        [DataMember(Name = "found", Order = 1)] public bool Found { get; set; }
        [DataMember(Name = "error", Order = 2, EmitDefaultValue = false)] public string Error { get; set; }
        [DataMember(Name = "jobId", Order = 3, EmitDefaultValue = false)] public string JobId { get; set; }
        [DataMember(Name = "task", Order = 4, EmitDefaultValue = false)] public string Task { get; set; }
        /// <summary>"running" | "done" | "failed".</summary>
        [DataMember(Name = "status", Order = 5, EmitDefaultValue = false)] public string Status { get; set; }
        [DataMember(Name = "running", Order = 6)] public bool Running { get; set; }
        [DataMember(Name = "filesProcessed", Order = 7, EmitDefaultValue = false)] public int FilesProcessed { get; set; }
        [DataMember(Name = "elapsedSeconds", Order = 8)] public int ElapsedSeconds { get; set; }
        [DataMember(Name = "messages", Order = 9, EmitDefaultValue = false)] public List<string> Messages { get; set; }
        [DataMember(Name = "note", Order = 10, EmitDefaultValue = false)] public string Note { get; set; }
    }

    [DataContract]
    public class BridgeFindReplaceRequest
    {
        [DataMember(Name = "find", IsRequired = true)] public string Find { get; set; }
        [DataMember(Name = "replace")] public string Replace { get; set; }
        [DataMember(Name = "caseSensitive", EmitDefaultValue = false)] public bool CaseSensitive { get; set; }
        [DataMember(Name = "wholeWord", EmitDefaultValue = false)] public bool WholeWord { get; set; }
        [DataMember(Name = "regex", EmitDefaultValue = false)] public bool Regex { get; set; }
        /// <summary>When true, count and list what would change without writing anything.</summary>
        [DataMember(Name = "dryRun", EmitDefaultValue = false)] public bool DryRun { get; set; }
        /// <summary>Restrict to one file of a merged document (id or partial name).</summary>
        [DataMember(Name = "file", EmitDefaultValue = false)] public string File { get; set; }
        /// <summary>Restrict to segments with this confirmation status.</summary>
        [DataMember(Name = "status", EmitDefaultValue = false)] public string Status { get; set; }
    }

    [DataContract]
    public class BridgeFindReplaceChange
    {
        [DataMember(Name = "id", Order = 0)] public string Id { get; set; }
        [DataMember(Name = "number", Order = 1, EmitDefaultValue = false)] public string Number { get; set; }
        [DataMember(Name = "fileName", Order = 2, EmitDefaultValue = false)] public string FileName { get; set; }
        [DataMember(Name = "before", Order = 3)] public string Before { get; set; }
        [DataMember(Name = "after", Order = 4)] public string After { get; set; }
    }

    [DataContract]
    public class BridgeFindReplaceResponse
    {
        [DataMember(Name = "ok", Order = 0)] public bool Ok { get; set; }
        [DataMember(Name = "error", Order = 1, EmitDefaultValue = false)] public string Error { get; set; }
        [DataMember(Name = "dryRun", Order = 2)] public bool DryRun { get; set; }
        [DataMember(Name = "segmentsChanged", Order = 3)] public int SegmentsChanged { get; set; }
        [DataMember(Name = "returned", Order = 4)] public int Returned { get; set; }
        [DataMember(Name = "truncated", Order = 5)] public bool Truncated { get; set; }
        [DataMember(Name = "changes", Order = 6, EmitDefaultValue = false)]
        public List<BridgeFindReplaceChange> Changes { get; set; }
        /// <summary>Segments where the match straddles inline tags and was skipped for safety.</summary>
        [DataMember(Name = "skippedTagSpanning", Order = 7, EmitDefaultValue = false)]
        public List<string> SkippedTagSpanning { get; set; }
        [DataMember(Name = "skippedLocked", Order = 8)] public int SkippedLocked { get; set; }
        [DataMember(Name = "note", Order = 9, EmitDefaultValue = false)] public string Note { get; set; }
    }

    [DataContract]
    public class BridgeVerifyResponse
    {
        [DataMember(Name = "ok", Order = 0)] public bool Ok { get; set; }
        [DataMember(Name = "error", Order = 1, EmitDefaultValue = false)] public string Error { get; set; }
        [DataMember(Name = "findingsCount", Order = 2)] public int FindingsCount { get; set; }
        [DataMember(Name = "returned", Order = 3)] public int Returned { get; set; }
        [DataMember(Name = "truncated", Order = 4)] public bool Truncated { get; set; }
        [DataMember(Name = "findings", Order = 5, EmitDefaultValue = false)]
        public List<BridgeVerifyFinding> Findings { get; set; }
        [DataMember(Name = "note", Order = 6, EmitDefaultValue = false)] public string Note { get; set; }
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
        private readonly Func<BridgeQaQuery, BridgeQaResponse> _runQaCheck;
        private readonly Func<BridgeResourcesResponse> _listResources;
        private readonly Func<BridgeGoToRequest, BridgeResultResponse> _goToSegment;
        private readonly Func<string, BridgeCommentsResponse> _getComments;
        private readonly Func<BridgeAddCommentRequest, BridgeResultResponse> _addComment;
        private readonly Func<BridgeUpdateCommentRequest, BridgeResultResponse> _updateComment;
        private readonly Func<BridgeVerifyResponse> _runVerification;
        private readonly Func<BridgeFindReplaceRequest, BridgeFindReplaceResponse> _findReplace;
        private readonly Func<BridgeRunTaskRequest, BridgeRunTaskResponse> _runTask;
        private readonly Func<string, BridgeTaskStatusResponse> _getTaskStatus;
        private readonly Func<int, BridgePromptContextResponse> _getPromptContext;

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
            Func<BridgeStudioTmQuery, BridgeTmSearchResponse> searchStudioTm = null,
            Func<BridgeQaQuery, BridgeQaResponse> runQaCheck = null,
            Func<BridgeResourcesResponse> listResources = null,
            Func<BridgeGoToRequest, BridgeResultResponse> goToSegment = null,
            Func<string, BridgeCommentsResponse> getComments = null,
            Func<BridgeAddCommentRequest, BridgeResultResponse> addComment = null,
            Func<BridgeUpdateCommentRequest, BridgeResultResponse> updateComment = null,
            Func<BridgeVerifyResponse> runVerification = null,
            Func<BridgeFindReplaceRequest, BridgeFindReplaceResponse> findReplace = null,
            Func<BridgeRunTaskRequest, BridgeRunTaskResponse> runTask = null,
            Func<string, BridgeTaskStatusResponse> getTaskStatus = null,
            Func<int, BridgePromptContextResponse> getPromptContext = null)
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
            _runQaCheck = runQaCheck;
            _listResources = listResources;
            _goToSegment = goToSegment;
            _getComments = getComments;
            _addComment = addComment;
            _updateComment = updateComment;
            _runVerification = runVerification;
            _findReplace = findReplace;
            _runTask = runTask;
            _getTaskStatus = getTaskStatus;
            _getPromptContext = getPromptContext;
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

            if (method == "GET" && path == "/v1/tools")
            {
                HandleGetTools(context);
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

            if (method == "GET" && path == "/v1/qa-check")
            {
                HandleQaCheck(context);
                return;
            }

            if (method == "GET" && path == "/v1/resources")
            {
                HandleListResources(context);
                return;
            }

            if (method == "POST" && path == "/v1/go-to-segment")
            {
                HandleDelegatePost<BridgeGoToRequest>(context, _goToSegment, "go-to-segment");
                return;
            }

            if (method == "GET" && path == "/v1/comments")
            {
                HandleGetComments(context);
                return;
            }

            if (method == "POST" && path == "/v1/add-comment")
            {
                HandleDelegatePost<BridgeAddCommentRequest>(context, _addComment, "add-comment");
                return;
            }

            if (method == "POST" && path == "/v1/update-comment")
            {
                HandleDelegatePost<BridgeUpdateCommentRequest>(context, _updateComment, "update-comment");
                return;
            }

            if (method == "POST" && path == "/v1/verify")
            {
                HandleRunVerification(context);
                return;
            }

            if (method == "POST" && path == "/v1/find-replace")
            {
                HandleFindReplace(context);
                return;
            }

            if (method == "POST" && path == "/v1/run-task")
            {
                HandleRunTask(context);
                return;
            }
            if (method == "GET" && path == "/v1/task-status")
            {
                HandleGetTaskStatus(context);
                return;
            }
            if (method == "GET" && path == "/v1/prompt-context")
            {
                HandleGetPromptContext(context);
                return;
            }
            if (method == "GET" && path == "/v1/prompts")
            {
                HandleListPrompts(context);
                return;
            }
            if (method == "GET" && path == "/v1/prompt")
            {
                HandleGetPrompt(context);
                return;
            }
            if (method == "POST" && path == "/v1/save-prompt")
            {
                HandleSavePrompt(context);
                return;
            }
            if (method == "GET" && path == "/v1/help")
            {
                HandleGetHelp(context);
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

        private static string _cachedToolRegistry;

        /// <summary>GET /v1/tools – the MCP tool registry (embedded resource
        /// Resources/mcp-tools.json). The external server exe fetches this and
        /// registers tools dynamically, so adding a tool is a plugin-only
        /// change with no extension reinstall. Cached after first read.</summary>
        private void HandleGetTools(HttpListenerContext context)
        {
            try
            {
                if (_cachedToolRegistry == null)
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = asm.GetManifestResourceStream("McpTools.mcp-tools.json"))
                    {
                        if (stream == null)
                        {
                            TryWriteError(context, 500, "tool registry resource missing");
                            return;
                        }
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                            _cachedToolRegistry = reader.ReadToEnd();
                    }
                }
                WriteRawJson(context, 200, _cachedToolRegistry);
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] tools registry read failed: {ex.Message}");
                TryWriteError(context, 500, "tool registry error: " + ex.Message);
            }
        }

        private static string _cachedHelpCard;

        /// <summary>GET /v1/help – a curated capability card (embedded resource
        /// Resources/help-card.md) the AI shows when the user asks what they can
        /// do. Editable as a plugin-only change.</summary>
        private void HandleGetHelp(HttpListenerContext context)
        {
            try
            {
                if (_cachedHelpCard == null)
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = asm.GetManifestResourceStream("Help.help-card.md"))
                    {
                        if (stream == null)
                        {
                            WriteJson(context, 500, new BridgeHelpResponse { Ok = false });
                            return;
                        }
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                            _cachedHelpCard = reader.ReadToEnd();
                    }
                }
                WriteJson(context, 200, new BridgeHelpResponse { Ok = true, Help = _cachedHelpCard });
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] help card read failed: {ex.Message}");
                TryWriteError(context, 500, "help card error: " + ex.Message);
            }
        }

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

                    // Studio project termbases (.ttb for Studio 2026, MultiTerm
                    // .sdltb for 2024): TermLens merges them into its in-memory
                    // index (entries carry IsMultiTerm=true + TermbaseName), so
                    // query that index rather than re-reading the files. Only
                    // available once TermLens has loaded for the open document.
                    var q = term.Trim();
                    var studioExact = new List<TermEntry>();
                    var studioSub = new List<TermEntry>();
                    bool studioIndexLoaded = false;
                    try
                    {
                        var merged = TermLensEditorViewPart.GetCurrentTermbaseTerms();
                        studioIndexLoaded = merged != null && merged.Count > 0;
                        foreach (var e in merged ?? new List<TermEntry>())
                        {
                            // Non-MultiTerm entries come from supervertaler.db,
                            // which the DB search below already covers.
                            if (e == null || !e.IsMultiTerm) continue;
                            if (TermMatchesQuery(e, q, exact: true)) studioExact.Add(e);
                            else if (TermMatchesQuery(e, q, exact: false)) studioSub.Add(e);
                        }
                    }
                    catch (Exception ex)
                    {
                        BridgeLog.Write($"[SupervertalerBridge] studio-termbase lookup threw: {ex.Message}");
                    }

                    // Exact/normalized match first; if nothing is stored under
                    // that exact form ANYWHERE, fall back to substring so
                    // inflected or partial queries still surface entries.
                    var entries = reader.SearchTerm(q) ?? new List<TermEntry>();
                    var studioHits = studioExact;
                    if (entries.Count == 0 && studioExact.Count == 0)
                    {
                        entries = reader.SearchTermSubstring(q) ?? new List<TermEntry>();
                        studioHits = studioSub.Take(20).ToList();
                        if (entries.Count > 0 || studioHits.Count > 0)
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
                    foreach (var entry in studioHits)
                    {
                        var notes = entry.Notes;
                        if (entry.TargetSynonyms != null && entry.TargetSynonyms.Count > 0)
                        {
                            var syn = "Other translations: " + string.Join(", ", entry.TargetSynonyms);
                            notes = string.IsNullOrEmpty(notes) ? syn : notes + " | " + syn;
                        }
                        response.Hits.Add(new BridgeTermbaseHit
                        {
                            Source = entry.SourceTerm ?? "",
                            Target = entry.TargetTerm ?? "",
                            TermbaseName = (entry.TermbaseName ?? "Trados termbase") + " [Trados project termbase]",
                            Definition = entry.Definition,
                            Domain = entry.Domain,
                            Notes = notes,
                            NonTranslatable = entry.IsNonTranslatable
                        });
                    }

                    if (!studioIndexLoaded)
                        response.Note = ((response.Note ?? "") + " Note: the Trados project's own termbases " +
                            "were not searched – they load when a document is open in the editor with " +
                            "TermLens initialised.").Trim();
                }
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] term-lookup threw: {ex.Message}");
                response = new BridgeTermLookupResponse { Ok = false, Error = "term lookup failed: " + ex.Message };
            }

            WriteJson(context, 200, response);
        }

        /// <summary>Case-insensitive match of a query against a term entry's
        /// source term, target term, and target synonyms. Exact = equality,
        /// otherwise substring containment in either direction is enough
        /// (so "koelgas" finds "koelgassysteem" and vice versa).</summary>
        private static bool TermMatchesQuery(TermEntry e, string q, bool exact)
        {
            bool One(string t)
            {
                if (string.IsNullOrWhiteSpace(t)) return false;
                t = t.Trim();
                if (exact) return string.Equals(t, q, StringComparison.OrdinalIgnoreCase);
                return t.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                    || q.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            if (One(e.SourceTerm) || One(e.TargetTerm)) return true;
            if (e.TargetSynonyms != null)
                foreach (var s in e.TargetSynonyms)
                    if (One(s)) return true;
            return false;
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
            // Analysis + confirmation statistics are cached in the .sdlproj on
            // disk. Prefer reading them from the LIVE open project's .sdlproj
            // path (from the project snapshot) rather than resolving the name
            // through projects.xml – the name lookup misses recently-created
            // projects and projects registered under a different Studio version
            // (Studio 2024 vs 2026 keep separate projects.xml files).
            var projectName = context.Request.QueryString["project"];

            string liveName = null, livePath = null;
            try
            {
                var snap = _getProject?.Invoke();
                if (snap != null && snap.Available)
                {
                    liveName = snap.Name;
                    livePath = snap.SdlprojPath;
                }
            }
            catch { }

            if (string.IsNullOrWhiteSpace(projectName))
                projectName = liveName;

            if (string.IsNullOrWhiteSpace(projectName))
            {
                TryWriteError(context, 400,
                    "no project name given and no project is open in the editor – pass ?project=<name>");
                return;
            }

            // Use the live .sdlproj when the request targets the open project;
            // otherwise fall back to the projects.xml name lookup.
            bool useLive = !string.IsNullOrEmpty(livePath) &&
                string.Equals(projectName, liveName, StringComparison.OrdinalIgnoreCase);

            string stats, fileStatus;
            try
            {
                if (useLive)
                {
                    stats = TradosTools.GetProjectStatisticsByFile(livePath, projectName);
                    fileStatus = TradosTools.GetFileStatusByFile(livePath, projectName);
                }
                else
                {
                    var input = SerializeProjectNameJson(projectName);
                    stats = TradosTools.ExecuteTool("studio_get_project_statistics", input);
                    fileStatus = TradosTools.ExecuteTool("studio_get_file_status", input);
                }
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] statistics threw: {ex.Message}");
                TryWriteError(context, 500, "statistics failed: " + ex.Message);
                return;
            }

            // TradosTools returns ready-made JSON – embed it verbatim. 'source'
            // tells the caller which path produced the numbers.
            WriteRawJson(context, 200,
                "{\"ok\":true,\"project\":" + JsonQuote(projectName) +
                ",\"source\":" + JsonQuote(useLive ? "open-project" : "projects.xml") +
                ",\"analysisStatistics\":" + (stats ?? "null") +
                ",\"confirmationStatistics\":" + (fileStatus ?? "null") + "}");
        }

        // ── Prompt library (v1: /prompts, /prompt, /save-prompt) ─────────────
        //
        // The prompt library is a folder of .md files (UserDataPath.PromptLibraryDir)
        // shared with the Supervertaler Workbench. Pure disk operations – no editor
        // state, so no UI-thread hop. Lets an AI app browse the user's prompts, read
        // one, and save an improved version back ("Claude as your prompt engineer").

        private void HandleListPrompts(HttpListenerContext context)
        {
            try
            {
                var category = context.Request.QueryString["category"];
                var query = context.Request.QueryString["query"];

                var lib = new PromptLibrary();
                var items = new List<BridgePromptInfo>();
                foreach (var p in lib.GetAllPrompts())
                {
                    if (!string.IsNullOrEmpty(category) &&
                        (p.Category ?? "").IndexOf(category, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    if (!string.IsNullOrEmpty(query) &&
                        (p.Name ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 &&
                        (p.Description ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    items.Add(new BridgePromptInfo
                    {
                        Name = p.Name,
                        Description = p.Description,
                        Category = p.Category,
                        RelativePath = p.RelativePath,
                        Type = p.Type,
                        IsDefault = p.IsDefault,
                        IsQuickLauncher = p.IsQuickLauncher,
                        IsReadOnly = p.IsReadOnly
                    });
                }

                items.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));

                WriteJson(context, 200, new BridgePromptListResponse
                {
                    Ok = true,
                    Count = items.Count,
                    PromptsFolder = PromptLibrary.PromptsFolderPath,
                    Prompts = items
                });
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] list prompts failed: {ex.Message}");
                TryWriteError(context, 500, "list prompts failed: " + ex.Message);
            }
        }

        private void HandleGetPrompt(HttpListenerContext context)
        {
            try
            {
                var relPath = context.Request.QueryString["path"];
                var name = context.Request.QueryString["name"];
                if (string.IsNullOrWhiteSpace(relPath) && string.IsNullOrWhiteSpace(name))
                {
                    WriteJson(context, 400, new BridgePromptResponse { Ok = false, Error = "pass 'path' (relativePath from list_prompts) or 'name'" });
                    return;
                }

                var lib = new PromptLibrary();
                PromptTemplate p = null;
                if (!string.IsNullOrWhiteSpace(relPath))
                    p = lib.GetPromptByRelativePath(relPath);
                if (p == null && !string.IsNullOrWhiteSpace(name))
                    p = lib.GetAllPrompts().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

                if (p == null)
                {
                    WriteJson(context, 404, new BridgePromptResponse { Ok = false, Error = "prompt not found – call list_prompts and use a relativePath from it, or an exact name" });
                    return;
                }

                WriteJson(context, 200, new BridgePromptResponse
                {
                    Ok = true,
                    Name = p.Name,
                    Description = p.Description,
                    Category = p.Category,
                    RelativePath = p.RelativePath,
                    Type = p.Type,
                    IsDefault = p.IsDefault,
                    IsReadOnly = p.IsReadOnly,
                    Content = p.Content
                });
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] get prompt failed: {ex.Message}");
                TryWriteError(context, 500, "get prompt failed: " + ex.Message);
            }
        }

        private void HandleSavePrompt(HttpListenerContext context)
        {
            BridgeSavePromptRequest req;
            try
            {
                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                {
                    var body = reader.ReadToEnd();
                    if (string.IsNullOrWhiteSpace(body))
                    {
                        WriteJson(context, 400, new BridgeSavePromptResponse { Ok = false, Error = "empty body" });
                        return;
                    }
                    req = DeserializeJson<BridgeSavePromptRequest>(body);
                }
            }
            catch (Exception ex)
            {
                WriteJson(context, 400, new BridgeSavePromptResponse { Ok = false, Error = "malformed body: " + ex.Message });
                return;
            }

            if (req == null || string.IsNullOrEmpty(req.Content))
            {
                WriteJson(context, 400, new BridgeSavePromptResponse { Ok = false, Error = "missing 'content'" });
                return;
            }

            try
            {
                var lib = new PromptLibrary();
                PromptTemplate target;
                bool created;

                if (!string.IsNullOrWhiteSpace(req.Path))
                {
                    // Update an existing prompt identified by its relativePath.
                    target = lib.GetPromptByRelativePath(req.Path);
                    if (target == null)
                    {
                        WriteJson(context, 404, new BridgeSavePromptResponse { Ok = false, Error = "no prompt at that path – omit 'path' and pass a 'name' to create a new prompt" });
                        return;
                    }
                    if (target.IsDefault)
                    {
                        WriteJson(context, 409, new BridgeSavePromptResponse { Ok = false, Error = "that is a built-in default prompt and would be reset on restart – save your version under a new name instead (omit 'path', pass a 'name')" });
                        return;
                    }
                    if (target.IsReadOnly)
                    {
                        WriteJson(context, 409, new BridgeSavePromptResponse { Ok = false, Error = "that prompt is read-only" });
                        return;
                    }
                    target.Content = req.Content;
                    if (req.Description != null) target.Description = req.Description;
                    created = false;
                }
                else
                {
                    // Create a new prompt.
                    if (string.IsNullOrWhiteSpace(req.Name))
                    {
                        WriteJson(context, 400, new BridgeSavePromptResponse { Ok = false, Error = "missing 'name' (required to create a new prompt)" });
                        return;
                    }
                    if (!IsSafePromptName(req.Name) || !IsSafeCategory(req.Category))
                    {
                        WriteJson(context, 400, new BridgeSavePromptResponse { Ok = false, Error = "invalid 'name' or 'category' (no path separators, '..', or rooted paths)" });
                        return;
                    }
                    target = new PromptTemplate
                    {
                        Name = req.Name.Trim(),
                        Content = req.Content,
                        Description = req.Description,
                        Category = string.IsNullOrWhiteSpace(req.Category) ? "" : req.Category.Trim(),
                        IsDefault = false
                    };
                    created = true;
                }

                lib.SavePrompt(target);

                // Defence in depth: never let a write escape the prompt library folder.
                var root = System.IO.Path.GetFullPath(PromptLibrary.PromptsFolderPath)
                    .TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
                var written = string.IsNullOrEmpty(target.FilePath) ? root : System.IO.Path.GetFullPath(target.FilePath);
                if (!written.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    BridgeLog.Write($"[SupervertalerBridge] save prompt escaped library folder: {written}");
                    WriteJson(context, 400, new BridgeSavePromptResponse { Ok = false, Error = "refused: resolved path is outside the prompt library" });
                    return;
                }

                WriteJson(context, 200, new BridgeSavePromptResponse
                {
                    Ok = true,
                    Created = created,
                    Name = target.Name,
                    RelativePath = target.RelativePath,
                    PromptsFolder = PromptLibrary.PromptsFolderPath
                });
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] save prompt failed: {ex.Message}");
                WriteJson(context, 500, new BridgeSavePromptResponse { Ok = false, Error = "save prompt failed: " + ex.Message });
            }
        }

        private static bool IsSafePromptName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0) return false;
            var t = name.Trim();
            if (t == ".." || t == ".") return false;
            if (System.IO.Path.IsPathRooted(name)) return false;
            return true;
        }

        private static bool IsSafeCategory(string category)
        {
            if (string.IsNullOrWhiteSpace(category)) return true; // empty = library root
            if (System.IO.Path.IsPathRooted(category)) return false;
            foreach (var part in category.Split('/', '\\'))
            {
                var t = part.Trim();
                if (t == ".." || t == ".") return false;
            }
            return true;
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

        private void HandleQaCheck(HttpListenerContext context)
        {
            if (_runQaCheck == null)
            {
                TryWriteError(context, 501, "qa-check endpoint not wired");
                return;
            }

            var type = (context.Request.QueryString["type"] ?? "").ToLowerInvariant();
            if (type != "numbers" && type != "tags" && type != "terminology")
            {
                WriteJson(context, 400, new BridgeQaResponse
                {
                    Available = false,
                    Note = "missing or unknown 'type' – use numbers, tags, or terminology"
                });
                return;
            }

            var q = new BridgeQaQuery { Type = type };
            int limit;
            if (int.TryParse(context.Request.QueryString["limit"], out limit) && limit > 0)
                q.Limit = Math.Min(limit, 200);

            BridgeQaResponse response;
            try
            {
                response = _runQaCheck(q) ?? new BridgeQaResponse { Available = false };
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] qa-check threw: {ex.Message}");
                response = new BridgeQaResponse { Available = false, Note = "qa check failed: " + ex.Message };
            }

            WriteJson(context, 200, response);
        }

        private void HandleListResources(HttpListenerContext context)
        {
            if (_listResources == null)
            {
                TryWriteError(context, 501, "resources endpoint not wired");
                return;
            }

            BridgeResourcesResponse response;
            try
            {
                response = _listResources() ?? new BridgeResourcesResponse { Available = false };
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] resources threw: {ex.Message}");
                response = new BridgeResourcesResponse { Available = false, Note = "error: " + ex.Message };
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

        /// <summary>Shared plumbing for small POST endpoints: read body,
        /// deserialize to T, invoke the delegate, write its BridgeResultResponse.</summary>
        private void HandleDelegatePost<T>(HttpListenerContext context,
            Func<T, BridgeResultResponse> handler, string name) where T : class
        {
            if (handler == null)
            {
                TryWriteError(context, 501, name + " endpoint not wired");
                return;
            }

            T req;
            try
            {
                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                {
                    req = DeserializeJson<T>(reader.ReadToEnd());
                }
            }
            catch (Exception ex)
            {
                WriteJson(context, 400, new BridgeResultResponse { Ok = false, Error = "malformed body: " + ex.Message });
                return;
            }

            BridgeResultResponse response;
            try
            {
                response = handler(req) ?? new BridgeResultResponse { Ok = false, Error = "internal error" };
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] {name} threw: {ex.Message}");
                response = new BridgeResultResponse { Ok = false, Error = name + " failed: " + ex.Message };
            }

            WriteJson(context, 200, response);
        }

        private void HandleRunTask(HttpListenerContext context)
        {
            if (_runTask == null)
            {
                TryWriteError(context, 501, "run-task endpoint not wired");
                return;
            }

            BridgeRunTaskRequest req;
            try
            {
                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                {
                    req = DeserializeJson<BridgeRunTaskRequest>(reader.ReadToEnd());
                }
            }
            catch (Exception ex)
            {
                WriteJson(context, 400, new BridgeRunTaskResponse { Ok = false, Error = "malformed body: " + ex.Message });
                return;
            }

            if (req == null || string.IsNullOrWhiteSpace(req.Task))
            {
                WriteJson(context, 400, new BridgeRunTaskResponse { Ok = false, Error = "missing 'task'" });
                return;
            }

            BridgeRunTaskResponse response;
            try
            {
                response = _runTask(req) ?? new BridgeRunTaskResponse { Ok = false, Error = "internal error" };
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] run-task threw: {ex.Message}");
                response = new BridgeRunTaskResponse { Ok = false, Error = "task failed: " + ex.Message };
            }

            WriteJson(context, 200, response);
        }

        private void HandleGetTaskStatus(HttpListenerContext context)
        {
            if (_getTaskStatus == null)
            {
                TryWriteError(context, 501, "task-status endpoint not wired");
                return;
            }

            var id = context.Request.QueryString["id"];
            if (string.IsNullOrWhiteSpace(id))
            {
                WriteJson(context, 400, new BridgeTaskStatusResponse { Ok = false, Error = "missing 'id' (the jobId returned by a batch task)" });
                return;
            }

            BridgeTaskStatusResponse response;
            try
            {
                response = _getTaskStatus(id) ?? new BridgeTaskStatusResponse { Ok = false, Error = "internal error" };
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] task-status threw: {ex.Message}");
                response = new BridgeTaskStatusResponse { Ok = false, Error = "task-status failed: " + ex.Message };
            }

            WriteJson(context, 200, response);
        }

        private void HandleGetPromptContext(HttpListenerContext context)
        {
            if (_getPromptContext == null)
            {
                TryWriteError(context, 501, "prompt-context endpoint not wired");
                return;
            }

            // maxSegments override: absent -> -1 (use the AI Settings default);
            // 0 -> whole document; >0 -> cap.
            int maxSegments = -1;
            var q = context.Request.QueryString["maxSegments"];
            if (!string.IsNullOrWhiteSpace(q) && int.TryParse(q, out var m))
                maxSegments = m < 0 ? -1 : m;

            BridgePromptContextResponse response;
            try
            {
                response = _getPromptContext(maxSegments)
                    ?? new BridgePromptContextResponse { Ok = false, Error = "internal error" };
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] prompt-context threw: {ex.Message}");
                response = new BridgePromptContextResponse { Ok = false, Error = "prompt-context failed: " + ex.Message };
            }

            WriteJson(context, 200, response);
        }

        private void HandleFindReplace(HttpListenerContext context)
        {
            if (_findReplace == null)
            {
                TryWriteError(context, 501, "find-replace endpoint not wired");
                return;
            }

            BridgeFindReplaceRequest req;
            try
            {
                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                {
                    req = DeserializeJson<BridgeFindReplaceRequest>(reader.ReadToEnd());
                }
            }
            catch (Exception ex)
            {
                WriteJson(context, 400, new BridgeFindReplaceResponse { Ok = false, Error = "malformed body: " + ex.Message });
                return;
            }

            if (req == null || string.IsNullOrEmpty(req.Find))
            {
                WriteJson(context, 400, new BridgeFindReplaceResponse { Ok = false, Error = "missing 'find'" });
                return;
            }

            BridgeFindReplaceResponse response;
            try
            {
                response = _findReplace(req) ?? new BridgeFindReplaceResponse { Ok = false, Error = "internal error" };
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] find-replace threw: {ex.Message}");
                response = new BridgeFindReplaceResponse { Ok = false, Error = "find/replace failed: " + ex.Message };
            }

            WriteJson(context, 200, response);
        }

        private void HandleRunVerification(HttpListenerContext context)
        {
            if (_runVerification == null)
            {
                TryWriteError(context, 501, "verify endpoint not wired");
                return;
            }

            BridgeVerifyResponse response;
            try
            {
                response = _runVerification() ?? new BridgeVerifyResponse { Ok = false, Error = "internal error" };
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] verify threw: {ex.Message}");
                response = new BridgeVerifyResponse { Ok = false, Error = "verification failed: " + ex.Message };
            }

            WriteJson(context, 200, response);
        }

        private void HandleGetComments(HttpListenerContext context)
        {
            if (_getComments == null)
            {
                TryWriteError(context, 501, "comments endpoint not wired");
                return;
            }

            var id = context.Request.QueryString["id"];
            if (string.IsNullOrWhiteSpace(id))
            {
                WriteJson(context, 400, new BridgeCommentsResponse { Ok = false, Error = "missing 'id'" });
                return;
            }

            BridgeCommentsResponse response;
            try
            {
                response = _getComments(id) ?? new BridgeCommentsResponse { Ok = false, Error = "internal error" };
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] comments threw: {ex.Message}");
                response = new BridgeCommentsResponse { Ok = false, Error = "get comments failed: " + ex.Message };
            }

            WriteJson(context, 200, response);
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
