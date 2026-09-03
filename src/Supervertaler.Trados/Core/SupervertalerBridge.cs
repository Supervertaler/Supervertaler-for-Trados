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
        /// <summary>True when the hit comes from a termbase whose Read tick is
        /// OFF in the Supervertaler Termbases settings – the lookup searches the
        /// whole database, so inactive termbases still answer, but flagged.</summary>
        [DataMember(Name = "inactive", Order = 7, EmitDefaultValue = false)] public bool Inactive { get; set; }
        /// <summary>Which stored column the query text matched: "source",
        /// "target" or "both". source/target above are returned exactly as
        /// stored (the termbase's own direction) – this says where the match
        /// was, so orientation can be judged rather than guessed.</summary>
        [DataMember(Name = "matchedField", Order = 8, EmitDefaultValue = false)] public string MatchedField { get; set; }
        [DataMember(Name = "sourceLang", Order = 9, EmitDefaultValue = false)] public string SourceLang { get; set; }
        [DataMember(Name = "targetLang", Order = 10, EmitDefaultValue = false)] public string TargetLang { get; set; }
        /// <summary>True when this hit's termbase carries the Trados "Project"
        /// flag – a job-specific decision, as opposed to a shared background
        /// termbase. Mirrors TermLens's pink-vs-blue chip distinction.</summary>
        [DataMember(Name = "isProjectTermbase", Order = 11, EmitDefaultValue = false)] public bool IsProjectTermbase { get; set; }
        /// <summary>True when this entry's own stored languages contradict its
        /// termbase's declared direction. Either the TEXT is stored the wrong
        /// way round – in which case the row is indexed under the wrong
        /// language, matches no source segment, and check_terminology can never
        /// report it however badly the document violates it – or only the tags
        /// are wrong and the entry works fine. Which one it is can only be
        /// settled by looking at the two terms, so this flag says "inspect
        /// this", not "this is dead". The hit otherwise looks perfectly
        /// sensible (its text and its lang tags agree with each other), which
        /// is why the flag exists: without it there is nothing to notice.</summary>
        [DataMember(Name = "directionMismatch", Order = 12, EmitDefaultValue = false)] public bool DirectionMismatch { get; set; }
    }

    [DataContract]
    internal class BridgeHandshake
    {
        [DataMember(Name = "version", Order = 0)] public int Version { get; set; }
        [DataMember(Name = "port", Order = 1)] public int Port { get; set; }
        [DataMember(Name = "token", Order = 2)] public string Token { get; set; }
        [DataMember(Name = "pid", Order = 3)] public int Pid { get; set; }
        [DataMember(Name = "startedAt", Order = 4)] public string StartedAt { get; set; }

        // ── Instance identity (issue #72) ────────────────────────────────
        //
        // Added on version 1 deliberately: the MCP exe does not validate
        // `version` at all, and Workbench's Sidekick Chat reads this same file
        // from another repo under a convention of strict version equality
        // (cf. WorkbenchBridgeClient). Additive fields keep every existing
        // reader working; a bump would not.
        //
        // All EmitDefaultValue = false, so a plain bridge.json written by this
        // build is byte-identical to the old one when no project is open.

        /// <summary>"2024" or "2026" – derived from the plugin build major
        /// (18.x targets Studio 2024, 19.x targets Studio 2026).</summary>
        [DataMember(Name = "studioVersion", Order = 5, EmitDefaultValue = false)] public string StudioVersion { get; set; }

        /// <summary>Full plugin assembly version, so a client can report a mismatch.</summary>
        [DataMember(Name = "pluginVersion", Order = 6, EmitDefaultValue = false)] public string PluginVersion { get; set; }

        /// <summary>Active project name, refreshed on project switch. Null when none is open.</summary>
        [DataMember(Name = "projectName", Order = 7, EmitDefaultValue = false)] public string ProjectName { get; set; }

        /// <summary>Active document name, best-effort. Null when none is open.</summary>
        [DataMember(Name = "activeFile", Order = 8, EmitDefaultValue = false)] public string ActiveFile { get; set; }

        /// <summary>
        /// Process name that wrote this file ("SDLTradosStudio"). Readers pair it
        /// with <c>pid</c> for liveness: Windows reuses PIDs, and a recycled PID
        /// would otherwise make a dead instance look live – which for a write
        /// gate means routing an edit at a process that is not Studio at all.
        /// Stored rather than hard-coded so it stays right if the exe is ever
        /// renamed between Studio generations.
        /// </summary>
        [DataMember(Name = "processName", Order = 9, EmitDefaultValue = false)] public string ProcessName { get; set; }
    }

    /// <summary>
    /// The bits of instance identity the bridge cannot work out for itself:
    /// they come from the editor and must be read on the UI thread.
    /// </summary>
    public class BridgeInstanceInfo
    {
        public string ProjectName { get; set; }
        public string ActiveFile { get; set; }
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

    [DataContract]
    public class BridgeEditTermRequest
    {
        [DataMember(Name = "source")] public string Source { get; set; }
        [DataMember(Name = "target")] public string Target { get; set; }
        /// <summary>Optional termbase name to restrict the edit to.</summary>
        [DataMember(Name = "termbase")] public string Termbase { get; set; }
        // update_term only:
        [DataMember(Name = "newSource")] public string NewSource { get; set; }
        [DataMember(Name = "newTarget")] public string NewTarget { get; set; }
        [DataMember(Name = "newNotes")] public string NewNotes { get; set; }
        [DataMember(Name = "newDefinition")] public string NewDefinition { get; set; }
        [DataMember(Name = "newDomain")] public string NewDomain { get; set; }
    }

    [DataContract]
    public class BridgeEditTermResponse
    {
        [DataMember(Name = "ok", Order = 0)] public bool Ok { get; set; }
        [DataMember(Name = "error", Order = 1, EmitDefaultValue = false)] public string Error { get; set; }
        [DataMember(Name = "changed", Order = 2)] public int Changed { get; set; }
        [DataMember(Name = "details", Order = 3, EmitDefaultValue = false)] public List<string> Details { get; set; }
        [DataMember(Name = "note", Order = 4, EmitDefaultValue = false)] public string Note { get; set; }
    }

    // ─── Termbase import endpoint types (v1: /import-termbase) ───────────────

    /// <summary>
    /// Copy a Trados project termbase (.sdltb / .ttb) into a Supervertaler termbase.
    /// The same operation as Settings → Termbases → "Import .sdltb/.ttb…", which was
    /// UI-only: over the bridge the alternative was one <c>add_term</c> call per term.
    /// </summary>
    [DataContract]
    public class BridgeImportTermbaseRequest
    {
        /// <summary>Project termbase to read, by name or full path. Omit when the
        /// project has exactly one.</summary>
        [DataMember(Name = "termbase", EmitDefaultValue = false)] public string Termbase { get; set; }

        /// <summary>Destination Supervertaler termbase name. Created if absent.</summary>
        [DataMember(Name = "into", IsRequired = true)] public string Into { get; set; }

        /// <summary>Language to take as source; defaults to the project's source language.</summary>
        [DataMember(Name = "sourceLang", EmitDefaultValue = false)] public string SourceLang { get; set; }

        /// <summary>Language to take as target; defaults to the project's target language.</summary>
        [DataMember(Name = "targetLang", EmitDefaultValue = false)] public string TargetLang { get; set; }

        /// <summary>
        /// Field mapping overrides as <c>"MultiTermField=target"</c> strings, e.g.
        /// <c>"Subject=domain"</c>. Targets: definition, domain, notes, context,
        /// partofspeech, url, client, project, forbiddenflag, appendtonotes, ignore.
        /// Fields not listed keep their automatic suggestion.
        ///
        /// A list of strings rather than a JSON object because the bridge's
        /// DataContractJsonSerializer is constructed without
        /// UseSimpleDictionaryFormat, so a Dictionary would have to be sent in the
        /// verbose [{"Key":…,"Value":…}] form. Changing that serializer would touch
        /// every endpoint on the bridge for the sake of one optional argument.
        /// </summary>
        [DataMember(Name = "fieldMap", EmitDefaultValue = false)] public List<string> FieldMap { get; set; }

        /// <summary>Report what would be imported without writing anything.</summary>
        [DataMember(Name = "dryRun", EmitDefaultValue = false)] public bool DryRun { get; set; }
    }

    [DataContract]
    public class BridgeImportTermbaseResponse
    {
        [DataMember(Name = "ok", Order = 0)] public bool Ok { get; set; }
        [DataMember(Name = "error", Order = 1, EmitDefaultValue = false)] public string Error { get; set; }
        [DataMember(Name = "dryRun", Order = 2)] public bool DryRun { get; set; }
        [DataMember(Name = "from", Order = 3, EmitDefaultValue = false)] public string From { get; set; }
        [DataMember(Name = "format", Order = 4, EmitDefaultValue = false)] public string Format { get; set; }
        [DataMember(Name = "into", Order = 5, EmitDefaultValue = false)] public string Into { get; set; }
        [DataMember(Name = "createdDestination", Order = 6)] public bool CreatedDestination { get; set; }
        [DataMember(Name = "sourceLang", Order = 7, EmitDefaultValue = false)] public string SourceLang { get; set; }
        [DataMember(Name = "targetLang", Order = 8, EmitDefaultValue = false)] public string TargetLang { get; set; }
        [DataMember(Name = "conceptsTotal", Order = 9)] public int ConceptsTotal { get; set; }
        [DataMember(Name = "rowsBuilt", Order = 10)] public int RowsBuilt { get; set; }
        [DataMember(Name = "added", Order = 11)] public int Added { get; set; }
        [DataMember(Name = "duplicates", Order = 12)] public int Duplicates { get; set; }
        [DataMember(Name = "synonymsAdded", Order = 13)] public int SynonymsAdded { get; set; }
        /// <summary>The mapping actually used, as "Field = target" strings.</summary>
        [DataMember(Name = "fieldMap", Order = 14, EmitDefaultValue = false)] public List<string> FieldMap { get; set; }
        [DataMember(Name = "availableLanguages", Order = 15, EmitDefaultValue = false)] public List<string> AvailableLanguages { get; set; }
        [DataMember(Name = "warnings", Order = 16, EmitDefaultValue = false)] public List<string> Warnings { get; set; }
        [DataMember(Name = "note", Order = 17, EmitDefaultValue = false)] public string Note { get; set; }

        /// <summary>A few parsed pairs, as "source \u2192 target". Dry runs of a
        /// delimited file only: a reversed pair is obvious here and invisible
        /// in a row count.</summary>
        [DataMember(Name = "samples", Order = 18, EmitDefaultValue = false)]
        public List<string> Samples { get; set; }
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
        /// <summary>Misconfigurations that silently degrade quality and are
        /// invisible from the MCP side otherwise – currently "no termbase is
        /// read-enabled". Surfaced here because get_active_project is the
        /// orientation call every session starts with.</summary>
        [DataMember(Name = "warnings", Order = 8, EmitDefaultValue = false)]
        public List<string> Warnings { get; set; }
        [DataMember(Name = "note", Order = 9, EmitDefaultValue = false)] public string Note { get; set; }

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
        /// <summary>Grid-number range (inclusive; 0 = unset). Numbers restart per
        /// file in merged documents, so combine with File to disambiguate.</summary>
        public int FromNumber;
        public int ToNumber;
        /// <summary>TM match-percentage range (inclusive; -1 = unset). Segments
        /// without a TM/MT origin count as 0, so MatchMax=0 finds no-match
        /// segments and MatchMin=75/MatchMax=94 finds fuzzies to review.</summary>
        public int MatchMin = -1;
        public int MatchMax = -1;
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
        /// <summary>TM match percentage from the segment's translation origin
        /// (100 = exact/CM, 85 = fuzzy…). Null when the segment has no
        /// TM/MT origin (e.g. typed from scratch or still untranslated).</summary>
        [DataMember(Name = "match", Order = 7, EmitDefaultValue = false)] public int? Match { get; set; }
        /// <summary>Translation origin type: tm, mt, interactive, auto-propagated, source…</summary>
        [DataMember(Name = "origin", Order = 8, EmitDefaultValue = false)] public string Origin { get; set; }
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
        [DataMember(Name = "offset", Order = 4)] public int Offset { get; set; }
        [DataMember(Name = "groups", Order = 5, EmitDefaultValue = false)]
        public List<BridgeInconsistencyGroup> Groups { get; set; }
        [DataMember(Name = "note", Order = 6, EmitDefaultValue = false)] public string Note { get; set; }
    }

    /// <summary>Parsed query for GET /v1/qa-check.</summary>
    public class BridgeQaQuery
    {
        /// <summary>"numbers", "tags", "terminology", or "nbsp".</summary>
        public string Type;
        public int Limit = 50;
        /// <summary>Terminology check only: restrict to these termbases, by
        /// name or numeric id (comma-separated in the query string).
        /// Null/empty = all read-enabled termbases.</summary>
        public List<string> Termbases;
    }

    /// <summary>Body of POST /v1/mark-reviewed (MCP mark_reviewed).</summary>
    [DataContract]
    public class BridgeMarkReviewedRequest
    {
        [DataMember(Name = "ids")] public List<string> Ids { get; set; }
        [DataMember(Name = "note")] public string Note { get; set; }
    }

    [DataContract]
    public class BridgeMarkReviewedResponse
    {
        [DataMember(Name = "ok", Order = 0)] public bool Ok { get; set; }
        [DataMember(Name = "marked", Order = 1)] public int Marked { get; set; }
        [DataMember(Name = "unknownIds", Order = 2, EmitDefaultValue = false)] public List<string> UnknownIds { get; set; }
        [DataMember(Name = "reviewedThisSession", Order = 3)] public int ReviewedThisSession { get; set; }
        [DataMember(Name = "note", Order = 4, EmitDefaultValue = false)] public string Note { get; set; }
    }

    /// <summary>One match band of GET /v1/coverage (MCP get_coverage).</summary>
    [DataContract]
    public class BridgeCoverageBand
    {
        [DataMember(Name = "band", Order = 0)] public string Band { get; set; }
        [DataMember(Name = "total", Order = 1)] public int Total { get; set; }
        [DataMember(Name = "written", Order = 2)] public int Written { get; set; }
        [DataMember(Name = "reviewed", Order = 3)] public int Reviewed { get; set; }
        [DataMember(Name = "uncovered", Order = 4)] public int Uncovered { get; set; }
        [DataMember(Name = "uncoveredIds", Order = 5, EmitDefaultValue = false)] public List<string> UncoveredIds { get; set; }
        [DataMember(Name = "uncoveredIdsTruncated", Order = 6, EmitDefaultValue = false)] public bool UncoveredIdsTruncated { get; set; }
    }

    [DataContract]
    public class BridgeCoverageResponse
    {
        [DataMember(Name = "available", Order = 0)] public bool Available { get; set; }
        [DataMember(Name = "totalSegments", Order = 1)] public int TotalSegments { get; set; }
        [DataMember(Name = "lockedExcluded", Order = 2)] public int LockedExcluded { get; set; }
        [DataMember(Name = "writtenThisSession", Order = 3)] public int WrittenThisSession { get; set; }
        [DataMember(Name = "reviewedThisSession", Order = 4)] public int ReviewedThisSession { get; set; }
        [DataMember(Name = "uncoveredTotal", Order = 5)] public int UncoveredTotal { get; set; }
        [DataMember(Name = "bands", Order = 6)] public List<BridgeCoverageBand> Bands { get; set; }
        [DataMember(Name = "note", Order = 7, EmitDefaultValue = false)] public string Note { get; set; }
    }

    public class BridgeTrackedChangesQuery
    {
        /// <summary>Write the full harvest (all changes, not just the returned
        /// page) as a Markdown file into the active SuperMemory bank's reference/ folder.</summary>
        public bool Save;
        public int Limit = 200;
    }

    [DataContract]
    public class BridgeTrackedChangeRecord
    {
        [DataMember(Name = "id", Order = 0)] public string Id { get; set; }
        [DataMember(Name = "fileName", Order = 1, EmitDefaultValue = false)] public string FileName { get; set; }
        [DataMember(Name = "source", Order = 2)] public string Source { get; set; }
        [DataMember(Name = "before", Order = 3)] public string Before { get; set; }
        [DataMember(Name = "after", Order = 4)] public string After { get; set; }
        [DataMember(Name = "authors", Order = 5, EmitDefaultValue = false)] public List<string> Authors { get; set; }
        [DataMember(Name = "lastDate", Order = 6, EmitDefaultValue = false)] public string LastDate { get; set; }
        [DataMember(Name = "status", Order = 7, EmitDefaultValue = false)] public string Status { get; set; }
    }

    [DataContract]
    public class BridgeTrackedChangesResponse
    {
        [DataMember(Name = "available", Order = 0)] public bool Available { get; set; }
        [DataMember(Name = "segmentsScanned", Order = 1)] public int SegmentsScanned { get; set; }
        [DataMember(Name = "segmentsWithChanges", Order = 2)] public int SegmentsWithChanges { get; set; }
        [DataMember(Name = "changes", Order = 3, EmitDefaultValue = false)] public List<BridgeTrackedChangeRecord> Changes { get; set; }
        [DataMember(Name = "truncated", Order = 4, EmitDefaultValue = false)] public bool Truncated { get; set; }
        [DataMember(Name = "savedTo", Order = 5, EmitDefaultValue = false)] public string SavedTo { get; set; }
        [DataMember(Name = "savedToBank", Order = 6, EmitDefaultValue = false)] public string SavedToBank { get; set; }
        [DataMember(Name = "note", Order = 8, EmitDefaultValue = false)] public string Note { get; set; }
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
        /// <summary>Terminology check only: entries whose stored direction
        /// contradicts their termbase's. Absent when there are none. These are
        /// not findings about the document – they are entries whose orientation
        /// wants checking, because any of them that is genuinely reversed was
        /// invisible to this check.</summary>
        [DataMember(Name = "directionMismatches", Order = 10, EmitDefaultValue = false)]
        public List<BridgeQaDirectionMismatch> DirectionMismatches { get; set; }
    }

    /// <summary>One termbase's tally of entries whose stored direction
    /// contradicts the termbase's declared one – some reversed and therefore
    /// unmatchable, some merely mislabelled and working fine.</summary>
    [DataContract]
    public class BridgeQaDirectionMismatch
    {
        [DataMember(Name = "termbase", Order = 0)] public string Termbase { get; set; }
        [DataMember(Name = "declaredDirection", Order = 1, EmitDefaultValue = false)]
        public string DeclaredDirection { get; set; }
        [DataMember(Name = "entries", Order = 2)] public int Entries { get; set; }
        /// <summary>A few of them, as stored: "source_term → target_term".</summary>
        [DataMember(Name = "samples", Order = 3, EmitDefaultValue = false)]
        public List<string> Samples { get; set; }
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

    /// <summary>Parsed query for GET /v1/compare-tm.</summary>
    public class BridgeTmCompareQuery
    {
        /// <summary>TM name or partial name; empty = every TM on the project.</summary>
        public string Tm;
        /// <summary>Only compare segments with this confirmation status. Empty = translated ones.</summary>
        public string Status;
        public int Limit = 100;
    }

    [DataContract]
    public class BridgeTmDeviation
    {
        [DataMember(Name = "id", Order = 0)] public string Id { get; set; }
        [DataMember(Name = "number", Order = 1, EmitDefaultValue = false)] public string Number { get; set; }
        [DataMember(Name = "fileName", Order = 2, EmitDefaultValue = false)] public string FileName { get; set; }
        [DataMember(Name = "source", Order = 3)] public string Source { get; set; }
        [DataMember(Name = "documentTarget", Order = 4)] public string DocumentTarget { get; set; }
        /// <summary>What the TM holds for this exact source. More than one when the TM disagrees with itself.</summary>
        [DataMember(Name = "tmTargets", Order = 5)] public List<string> TmTargets { get; set; }
    }

    [DataContract]
    public class BridgeTmCompareResponse
    {
        [DataMember(Name = "available", Order = 0)] public bool Available { get; set; }
        [DataMember(Name = "error", Order = 1, EmitDefaultValue = false)] public string Error { get; set; }
        [DataMember(Name = "tmsCompared", Order = 2, EmitDefaultValue = false)] public List<string> TmsCompared { get; set; }
        [DataMember(Name = "tmUnitsRead", Order = 3)] public int TmUnitsRead { get; set; }
        [DataMember(Name = "segmentsChecked", Order = 4)] public int SegmentsChecked { get; set; }
        /// <summary>Segments whose source was found in the TM verbatim.</summary>
        [DataMember(Name = "exactSourceHits", Order = 5)] public int ExactSourceHits { get; set; }
        [DataMember(Name = "deviations", Order = 6)] public int Deviations { get; set; }
        [DataMember(Name = "returned", Order = 7)] public int Returned { get; set; }
        [DataMember(Name = "truncated", Order = 8)] public bool Truncated { get; set; }
        /// <summary>True when a TM was too large to read fully within the time budget.</summary>
        [DataMember(Name = "tmPartiallyRead", Order = 9)] public bool TmPartiallyRead { get; set; }
        [DataMember(Name = "items", Order = 10, EmitDefaultValue = false)] public List<BridgeTmDeviation> Items { get; set; }
        [DataMember(Name = "note", Order = 11, EmitDefaultValue = false)] public string Note { get; set; }
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

    // ── Session payload report (cost instrumentation) ─────────────────

    [DataContract]
    public class BridgeSessionReportEntry
    {
        /// <summary>Bridge path, plus "?type=x" where several tools share one
        /// endpoint. Not the MCP tool name – one endpoint can back several.</summary>
        [DataMember(Name = "endpoint", Order = 0)] public string Endpoint { get; set; }
        [DataMember(Name = "calls", Order = 1)] public int Calls { get; set; }
        [DataMember(Name = "responseBytes", Order = 2)] public long ResponseBytes { get; set; }
        [DataMember(Name = "avgResponseBytes", Order = 3)] public long AvgResponseBytes { get; set; }
        /// <summary>Largest single response – an average hides the one call that flooded the chat.</summary>
        [DataMember(Name = "maxResponseBytes", Order = 4)] public long MaxResponseBytes { get; set; }
        [DataMember(Name = "requestBytes", Order = 5, EmitDefaultValue = false)] public long RequestBytes { get; set; }
        /// <summary>Rough proxy only: (request + response) / 4.</summary>
        [DataMember(Name = "estTokens", Order = 6)] public long EstTokens { get; set; }
    }

    [DataContract]
    public class BridgeSessionReportResponse
    {
        [DataMember(Name = "available", Order = 0)] public bool Available { get; set; }
        /// <summary>ISO-8601 UTC – bridge start, or the last reset.</summary>
        [DataMember(Name = "since", Order = 1)] public string Since { get; set; }
        [DataMember(Name = "totalCalls", Order = 2)] public int TotalCalls { get; set; }
        [DataMember(Name = "totalResponseBytes", Order = 3)] public long TotalResponseBytes { get; set; }
        [DataMember(Name = "totalRequestBytes", Order = 4)] public long TotalRequestBytes { get; set; }
        [DataMember(Name = "estTotalTokens", Order = 5)] public long EstTotalTokens { get; set; }
        /// <summary>True when this call zeroed the counters after reporting.</summary>
        [DataMember(Name = "wasReset", Order = 6, EmitDefaultValue = false)] public bool WasReset { get; set; }
        [DataMember(Name = "endpoints", Order = 7, EmitDefaultValue = false)]
        public List<BridgeSessionReportEntry> Endpoints { get; set; }
        [DataMember(Name = "note", Order = 8, EmitDefaultValue = false)] public string Note { get; set; }
    }

    // ── SuperMemory (memory banks) ────────────────────────────────────
    // The bank has always fed the plugin's own prompt building; these types
    // carry it over the bridge as well, so an external MCP client sees the
    // same knowledge the in-Trados chat does. See issues #51 and #22.

    [DataContract]
    public class BridgeSuperMemoryQuery
    {
        /// <summary>Free text to bias retrieval towards, e.g. the term being
        /// asked about. Optional – without it the bank is loaded on project,
        /// domain and language pair alone, exactly as a translation would.</summary>
        [DataMember(Name = "query", EmitDefaultValue = false)] public string Query { get; set; }
        /// <summary>Overrides the domain auto-detected from the open document.</summary>
        [DataMember(Name = "domain", EmitDefaultValue = false)] public string Domain { get; set; }
        /// <summary>Names the client explicitly when the project name does not
        /// give it away. Matched loosely against 01_CLIENTS article names.</summary>
        [DataMember(Name = "client", EmitDefaultValue = false)] public string Client { get; set; }
        /// <summary>Reads a specific bank instead of the active one. Unknown
        /// names are an error rather than a silent fall back to the active bank:
        /// the response carries a bank name either way, so falling back looks
        /// exactly like success and injects another project's terminology.</summary>
        [DataMember(Name = "bank", EmitDefaultValue = false)] public string Bank { get; set; }
        /// <summary>Defaults to the same 24k budget the in-Trados chat uses.</summary>
        [DataMember(Name = "tokenBudget", EmitDefaultValue = false)] public int TokenBudget { get; set; }
    }

    [DataContract]
    public class BridgeSuperMemoryContextResponse
    {
        [DataMember(Name = "available", Order = 0)] public bool Available { get; set; }
        [DataMember(Name = "bank", Order = 1, EmitDefaultValue = false)] public string Bank { get; set; }
        [DataMember(Name = "client", Order = 2, EmitDefaultValue = false)] public string Client { get; set; }
        [DataMember(Name = "domain", Order = 3, EmitDefaultValue = false)] public string Domain { get; set; }
        /// <summary>How the client profile was resolved: "manual", "project-name" or "none".</summary>
        [DataMember(Name = "detectionMethod", Order = 4, EmitDefaultValue = false)] public string DetectionMethod { get; set; }
        /// <summary>The formatted knowledge-base block, identical to what gets
        /// injected into the plugin's own system prompt.</summary>
        [DataMember(Name = "context", Order = 5, EmitDefaultValue = false)] public string Context { get; set; }
        /// <summary>Bank-relative paths of every article that fed the block, so
        /// the AI can cite them and the translator can open them.</summary>
        [DataMember(Name = "sources", Order = 6, EmitDefaultValue = false)] public List<string> Sources { get; set; }
        /// <summary>Articles the bank holds that did NOT fit the token budget,
        /// as bank-relative paths, least-important first.
        ///
        /// <para>Without this a trimmed answer is indistinguishable from a
        /// complete one: the caller sees content, has no way to know a third
        /// article existed, and translates against rules it was never shown.
        /// Present only when something was actually dropped, so its absence
        /// means "you have all of it".</para></summary>
        [DataMember(Name = "trimmed", Order = 7, EmitDefaultValue = false)] public List<string> Trimmed { get; set; }
        [DataMember(Name = "note", Order = 8, EmitDefaultValue = false)] public string Note { get; set; }
    }

    [DataContract]
    public class BridgeSuperMemorySearchQuery
    {
        [DataMember(Name = "query")] public string Query { get; set; }
        [DataMember(Name = "limit", EmitDefaultValue = false)] public int Limit { get; set; }
    }

    [DataContract]
    public class BridgeSuperMemorySearchHit
    {
        /// <summary>Which bank the hit came from – a search spans the active
        /// bank and "_shared", and the caller must be able to tell them apart.</summary>
        [DataMember(Name = "bank", Order = 0, EmitDefaultValue = false)] public string Bank { get; set; }
        [DataMember(Name = "path", Order = 1)] public string Path { get; set; }
        [DataMember(Name = "folder", Order = 2, EmitDefaultValue = false)] public string Folder { get; set; }
        [DataMember(Name = "title", Order = 3, EmitDefaultValue = false)] public string Title { get; set; }
        [DataMember(Name = "score", Order = 4)] public int Score { get; set; }
        [DataMember(Name = "snippet", Order = 5, EmitDefaultValue = false)] public string Snippet { get; set; }
    }

    [DataContract]
    public class BridgeSuperMemorySearchResponse
    {
        [DataMember(Name = "available", Order = 0)] public bool Available { get; set; }
        [DataMember(Name = "bank", Order = 1, EmitDefaultValue = false)] public string Bank { get; set; }
        /// <summary>Every bank the query actually ran against. Without this a
        /// zero-hit answer is indistinguishable from "you never wrote that
        /// down", which is the wrong conclusion to hand an AI.</summary>
        [DataMember(Name = "banksSearched", Order = 2, EmitDefaultValue = false)]
        public List<string> BanksSearched { get; set; }
        [DataMember(Name = "hits", Order = 3, EmitDefaultValue = false)]
        public List<BridgeSuperMemorySearchHit> Hits { get; set; }
        [DataMember(Name = "note", Order = 4, EmitDefaultValue = false)] public string Note { get; set; }
    }

    [DataContract]
    public class BridgeSuperMemoryBank
    {
        [DataMember(Name = "name", Order = 0)] public string Name { get; set; }
        /// <summary>"bank" for an ordinary bank, "shared" for the _shared
        /// overlay. The overlay appears in this list because it is on disk like
        /// any other, but it is not a sibling: it is loaded on top of whichever
        /// bank is active, so reading its active:false as "unused" is wrong.</summary>
        [DataMember(Name = "role", Order = 1)] public string Role { get; set; }
        [DataMember(Name = "active", Order = 2)] public bool Active { get; set; }
        /// <summary>True for the shared overlay: its content reaches the model
        /// on every call regardless of which bank is active.</summary>
        [DataMember(Name = "alwaysLoaded", Order = 3, EmitDefaultValue = false)]
        public bool AlwaysLoaded { get; set; }
        [DataMember(Name = "articles", Order = 4)] public int Articles { get; set; }
    }

    [DataContract]
    public class BridgeSuperMemoryBanksResponse
    {
        [DataMember(Name = "available", Order = 0)] public bool Available { get; set; }
        [DataMember(Name = "root", Order = 1, EmitDefaultValue = false)] public string Root { get; set; }
        [DataMember(Name = "activeBank", Order = 2, EmitDefaultValue = false)] public string ActiveBank { get; set; }
        [DataMember(Name = "banks", Order = 3, EmitDefaultValue = false)]
        public List<BridgeSuperMemoryBank> Banks { get; set; }
        [DataMember(Name = "note", Order = 4, EmitDefaultValue = false)] public string Note { get; set; }
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
        /// <summary>New comment text. Omit to change only 'severity'.</summary>
        [DataMember(Name = "text", EmitDefaultValue = false)] public string Text { get; set; }
        /// <summary>"Low", "Medium", or "High". Omit to leave the severity unchanged.</summary>
        [DataMember(Name = "severity", EmitDefaultValue = false)] public string Severity { get; set; }
    }

    [DataContract]
    public class BridgeDeleteCommentRequest
    {
        [DataMember(Name = "id", IsRequired = true)] public string Id { get; set; }
        /// <summary>Comment index as returned by /v1/comments for this segment.
        /// Omit (or -1) together with all=true to remove every comment.</summary>
        [DataMember(Name = "commentIndex")] public int CommentIndex { get; set; } = -1;
        /// <summary>Remove every comment on the segment instead of one.</summary>
        [DataMember(Name = "all")] public bool All { get; set; }
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
        /// <summary>What to do with each changed segment's confirmation status.
        /// "preserve" (default) restores the status the segment had before the
        /// replacement – editing content through ProcessSegmentPair otherwise
        /// silently demotes it to Draft, which turns a consistency sweep over a
        /// finished file into an unfinished one. Any ConfirmationLevel name
        /// ("Draft", "Translated", …) forces that status instead.</summary>
        [DataMember(Name = "setStatus", EmitDefaultValue = false)] public string SetStatus { get; set; }
        /// <summary>Opt in to HTML-entity decoding of 'find' and 'replace' -
        /// the only reliable way to carry a non-breaking space, since sending
        /// the character itself only works sometimes. See
        /// <see cref="EntityEscapes"/>.</summary>
        [DataMember(Name = "decodeEntities", EmitDefaultValue = false)] public bool DecodeEntities { get; set; }
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
        /// <summary>Echo of the effective setStatus mode ("preserve" or a ConfirmationLevel name).</summary>
        [DataMember(Name = "statusMode", Order = 9, EmitDefaultValue = false)] public string StatusMode { get; set; }
        /// <summary>Segments whose pre-existing confirmation status was restored after the write.</summary>
        [DataMember(Name = "statusRestored", Order = 10)] public int StatusRestored { get; set; }
        [DataMember(Name = "note", Order = 11, EmitDefaultValue = false)] public string Note { get; set; }
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
        /// <summary>True when the open document has bridge-applied edits that
        /// have not been saved, so these findings describe an older state of the
        /// file. A top-level flag rather than prose in 'note', because the
        /// findings otherwise look perfectly current.</summary>
        [DataMember(Name = "stale", Order = 6)] public bool Stale { get; set; }
        [DataMember(Name = "note", Order = 7, EmitDefaultValue = false)] public string Note { get; set; }
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
        /// <summary>Opt in to HTML-entity decoding of every 'target'. See
        /// <see cref="EntityEscapes"/> for why this exists.</summary>
        [DataMember(Name = "decodeEntities", EmitDefaultValue = false)] public bool DecodeEntities { get; set; }
    }

    /// <summary>
    /// Decodes a small set of HTML entities in text arriving over the bridge,
    /// strictly opt-in per request.
    ///
    /// Why: a caller cannot reliably transmit an invisible character. Measured
    /// on a real client, a non-breaking space written into a tool argument
    /// sometimes arrives here intact and sometimes as an ordinary space - and
    /// the JSON escape \u00a0 is no safer, because the client's own parser
    /// turns it into the character before we ever see it. The write reports
    /// success either way, so the caller cannot tell which happened;
    /// intermittent is worse than broken, because it survives testing.
    /// Entities dodge the problem completely: they are plain ASCII on the wire,
    /// so no transport, tokeniser or normaliser can touch them, and the
    /// substitution happens here where it can be trusted.
    ///
    /// Opt-in because a document may legitimately contain the text "&amp;nbsp;" –
    /// an HTML manual, for one – and silently rewriting it would be its own
    /// silent corruption bug.
    /// </summary>
    public static class EntityEscapes
    {
        private static readonly System.Text.RegularExpressions.Regex Pattern =
            new System.Text.RegularExpressions.Regex(
            @"&(?:(?<name>nbsp|amp)|#(?<dec>[0-9]{1,7})|#[xX](?<hex>[0-9a-fA-F]{1,6}));",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        public static string Decode(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('&') < 0) return text;
            return Pattern.Replace(text, m =>
            {
                try
                {
                    if (m.Groups["name"].Success)
                        return m.Groups["name"].Value.Equals("amp", StringComparison.OrdinalIgnoreCase)
                            ? "&" : "\u00a0";

                    int cp;
                    if (m.Groups["dec"].Success)
                        cp = int.Parse(m.Groups["dec"].Value, System.Globalization.CultureInfo.InvariantCulture);
                    else
                        cp = int.Parse(m.Groups["hex"].Value, System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture);

                    // Reject anything that isn't a legal scalar value rather
                    // than throwing – an unparseable entity stays as written.
                    if (cp <= 0 || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)) return m.Value;
                    return char.ConvertFromUtf32(cp);
                }
                catch
                {
                    return m.Value;
                }
            });
        }
    }

    [DataContract]
    public class BridgeUpdateResultItem
    {
        [DataMember(Name = "id", Order = 0)] public string Id { get; set; }
        [DataMember(Name = "ok", Order = 1)] public bool Ok { get; set; }
        [DataMember(Name = "error", Order = 2, EmitDefaultValue = false)] public string Error { get; set; }

        /// <summary>Set when the write succeeded but the target's inline tags do
        /// not carry the same underlying tag ids as the source. Studio's own Tag
        /// Verifier will report this later as "Duplicated tag with id 'N'" /
        /// "Missing tag with id 'N'"; surfacing it here lets the caller see it at
        /// write time instead of at verification time. Not an error — the text
        /// was written — but the segment needs attention.</summary>
        [DataMember(Name = "warning", Order = 3, EmitDefaultValue = false)] public string Warning { get; set; }
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
        // NOT IsRequired: with an 'entries' array these are absent, and
        // DataContractJsonSerializer refuses the whole body when a required
        // member is missing - so the batch form could never be parsed and the
        // branch handling it was unreachable. The handler validates instead,
        // and says which of the two forms is missing.
        [DataMember(Name = "source")] public string Source { get; set; }
        [DataMember(Name = "target")] public string Target { get; set; }
        /// <summary>Optional: restrict the write to these termbases (names or
        /// numeric ids). Empty/null = all write-enabled termbases.</summary>
        [DataMember(Name = "termbases", EmitDefaultValue = false)] public List<string> Termbases { get; set; }
        /// <summary>Optional: the language the <c>source</c> text is in. When
        /// supplied, orientation is decided from it rather than assumed from
        /// the project direction.</summary>
        [DataMember(Name = "sourceLang", EmitDefaultValue = false)] public string SourceLang { get; set; }
        [DataMember(Name = "targetLang", EmitDefaultValue = false)] public string TargetLang { get; set; }
        [DataMember(Name = "definition", EmitDefaultValue = false)] public string Definition { get; set; }
        [DataMember(Name = "domain", EmitDefaultValue = false)] public string Domain { get; set; }
        [DataMember(Name = "notes", EmitDefaultValue = false)] public string Notes { get; set; }
        /// <summary>Optional: "project" (Project-flagged Write termbases only),
        /// "background" (Write termbases without the Project flag), or "both"
        /// (default – current behaviour). Ignored when 'termbases' is given –
        /// an explicit list always wins.</summary>
        [DataMember(Name = "scope", EmitDefaultValue = false)] public string Scope { get; set; }

        /// <summary>
        /// Many pairs in one call. Mutually exclusive with the single-pair
        /// form: passing both is an error rather than a merge, because a
        /// caller that did so almost certainly meant one of them.
        ///
        /// <para><c>termbases</c> and <c>scope</c> stay at the CALL level and
        /// apply to the whole batch. Per-entry termbase targeting is a
        /// different feature and is not wanted here.</para>
        /// </summary>
        [DataMember(Name = "entries", EmitDefaultValue = false)]
        public List<BridgeAddTermEntry> Entries { get; set; }
    }

    /// <summary>Exactly what one termbase stored, echoed so the caller can
    /// verify orientation instead of trusting a bare success.</summary>
    [DataContract]
    public class BridgeStoredTerm
    {
        [DataMember(Name = "source", Order = 0)] public string Source { get; set; }
        [DataMember(Name = "target", Order = 1)] public string Target { get; set; }
        [DataMember(Name = "sourceLang", Order = 2, EmitDefaultValue = false)] public string SourceLang { get; set; }
        [DataMember(Name = "targetLang", Order = 3, EmitDefaultValue = false)] public string TargetLang { get; set; }
        [DataMember(Name = "definition", Order = 4, EmitDefaultValue = false)] public string Definition { get; set; }
        [DataMember(Name = "domain", Order = 5, EmitDefaultValue = false)] public string Domain { get; set; }
        [DataMember(Name = "notes", Order = 6, EmitDefaultValue = false)] public string Notes { get; set; }
        /// <summary>True when the caller's pair was reoriented to fit this
        /// termbase's declared direction.</summary>
        [DataMember(Name = "reoriented", Order = 7, EmitDefaultValue = false)] public bool Reoriented { get; set; }
    }

    /// <summary>Duplicate-only: the existing entry an add_term call matched,
    /// so the caller can see what it hit instead of taking "duplicate" on
    /// faith.</summary>
    [DataContract]
    public class BridgeExistingTerm
    {
        [DataMember(Name = "id", Order = 0)] public long Id { get; set; }
        [DataMember(Name = "source", Order = 1)] public string Source { get; set; }
        [DataMember(Name = "target", Order = 2)] public string Target { get; set; }
    }

    /// <summary>One pair in a batch <c>add_term</c> call.</summary>
    [DataContract]
    public class BridgeAddTermEntry
    {
        [DataMember(Name = "source")] public string Source { get; set; }
        [DataMember(Name = "target")] public string Target { get; set; }

        /// <summary>Overrides the call-level value for this entry, so one batch
        /// can mix directions if it has to.</summary>
        [DataMember(Name = "sourceLang", EmitDefaultValue = false)] public string SourceLang { get; set; }
        [DataMember(Name = "targetLang", EmitDefaultValue = false)] public string TargetLang { get; set; }
        [DataMember(Name = "definition", EmitDefaultValue = false)] public string Definition { get; set; }
        [DataMember(Name = "domain", EmitDefaultValue = false)] public string Domain { get; set; }
        [DataMember(Name = "notes", EmitDefaultValue = false)] public string Notes { get; set; }
    }

    /// <summary>What happened to one entry of a batch, in input order.</summary>
    [DataContract]
    public class BridgeAddTermBatchItem
    {
        [DataMember(Name = "index", Order = 0)] public int Index { get; set; }
        [DataMember(Name = "source", Order = 1)] public string Source { get; set; }
        [DataMember(Name = "target", Order = 2)] public string Target { get; set; }
        [DataMember(Name = "ok", Order = 3)] public bool Ok { get; set; }
        [DataMember(Name = "error", Order = 4, EmitDefaultValue = false)] public string Error { get; set; }
        [DataMember(Name = "addedTo", Order = 5, EmitDefaultValue = false)] public List<string> AddedTo { get; set; }
        [DataMember(Name = "results", Order = 6, EmitDefaultValue = false)] public List<BridgeAddTermResult> Results { get; set; }
        [DataMember(Name = "note", Order = 7, EmitDefaultValue = false)] public string Note { get; set; }
    }

    [DataContract]
    public class BridgeAddTermBatchSummary
    {
        [DataMember(Name = "added", Order = 0)] public int Added { get; set; }
        [DataMember(Name = "duplicates", Order = 1)] public int Duplicates { get; set; }
        [DataMember(Name = "failed", Order = 2)] public int Failed { get; set; }
    }

    [DataContract]
    public class BridgeAddTermBatchResponse
    {
        [DataMember(Name = "ok", Order = 0)] public bool Ok { get; set; }
        [DataMember(Name = "error", Order = 1, EmitDefaultValue = false)] public string Error { get; set; }
        [DataMember(Name = "summary", Order = 2, EmitDefaultValue = false)] public BridgeAddTermBatchSummary Summary { get; set; }
        [DataMember(Name = "results", Order = 3, EmitDefaultValue = false)] public List<BridgeAddTermBatchItem> Results { get; set; }
    }

    [DataContract]
    public class BridgeAddTermResult
    {
        [DataMember(Name = "termbase", Order = 0)] public string Termbase { get; set; }
        /// <summary>"added" | "duplicate" | "error".</summary>
        [DataMember(Name = "status", Order = 1)] public string Status { get; set; }
        [DataMember(Name = "detail", Order = 2, EmitDefaultValue = false)] public string Detail { get; set; }
        [DataMember(Name = "stored", Order = 3, EmitDefaultValue = false)] public BridgeStoredTerm Stored { get; set; }
        /// <summary>"project" or "background", resolved from the termbase's
        /// Project flag. Null when the termbase couldn't be resolved (e.g. an
        /// unknown name passed via 'termbases').</summary>
        [DataMember(Name = "role", Order = 4, EmitDefaultValue = false)] public string Role { get; set; }
        [DataMember(Name = "existing", Order = 5, EmitDefaultValue = false)] public BridgeExistingTerm Existing { get; set; }
    }

    [DataContract]
    public class BridgeAddTermResponse
    {
        [DataMember(Name = "ok", Order = 0)] public bool Ok { get; set; }
        [DataMember(Name = "error", Order = 1, EmitDefaultValue = false)] public string Error { get; set; }
        [DataMember(Name = "addedTo", Order = 2, EmitDefaultValue = false)]
        public List<string> AddedTo { get; set; }
        [DataMember(Name = "results", Order = 3, EmitDefaultValue = false)]
        public List<BridgeAddTermResult> Results { get; set; }
        [DataMember(Name = "note", Order = 4, EmitDefaultValue = false)] public string Note { get; set; }
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
    ///   * The listener runs on a dedicated background thread that ONLY accepts:
    ///     each request is handed to a ThreadPool worker so the accept loop never
    ///     blocks. Handling requests inline used to serialise the whole bridge,
    ///     so one slow or client-abandoned call stalled every later one.
    ///   * Handlers that touch the Trados editor marshal to the UI thread via the
    ///     supplied delegates and therefore still serialise THERE, which is
    ///     required – editor operations are not concurrency-safe. Handlers that
    ///     do not need the editor (term lookup, help, tool registry,
    ///     session_report) now genuinely run in parallel.
    ///   * Consequence for anything added here: handler code may run on several
    ///     threads at once, so bridge-level shared state must be synchronised.
    ///     The existing state already is – see BridgePayloadLedger's lock and the
    ///     coverage sets' _bridgeCoverageLock.
    /// </summary>
    public sealed class SupervertalerBridge : IDisposable
    {
        private const int HandshakeVersion = 1;

        private readonly Func<BridgeContextSnapshot> _getContext;
        private readonly Func<BridgeInstanceInfo> _getInstanceInfo; // project/document identity for the handshake (UI thread)
        private readonly Func<string, string> _insertText; // returns null on success, error message otherwise
        private readonly Func<BridgeProjectSnapshot> _getProject;
        private readonly Func<BridgeSegmentsQuery, BridgeSegmentsResponse> _getSegments;
        private readonly Func<string> _getDbPath; // resolves supervertaler.db for TM/termbase lookups
        private readonly Func<BridgeUpdateSegmentsRequest, BridgeUpdateSegmentsResponse> _updateSegments;
        private readonly Func<BridgeAddTermRequest, BridgeAddTermResponse> _addTerm;
        private readonly Func<BridgeImportTermbaseRequest, BridgeImportTermbaseResponse> _importTermbase;
        private readonly Func<BridgeFilesResponse> _getFiles;
        private readonly Func<int, int, BridgeInconsistenciesResponse> _findInconsistencies;
        private readonly Func<BridgeStudioTmQuery, BridgeTmSearchResponse> _searchStudioTm;
        private readonly Func<BridgeQaQuery, BridgeQaResponse> _runQaCheck;
        private readonly Func<BridgeTmCompareQuery, BridgeTmCompareResponse> _compareTm;
        private readonly Func<BridgeResourcesResponse> _listResources;
        private readonly Func<BridgeGoToRequest, BridgeResultResponse> _goToSegment;
        private readonly Func<string, BridgeCommentsResponse> _getComments;
        private readonly Func<BridgeAddCommentRequest, BridgeResultResponse> _addComment;
        private readonly Func<BridgeUpdateCommentRequest, BridgeResultResponse> _updateComment;
        private readonly Func<BridgeDeleteCommentRequest, BridgeResultResponse> _deleteComment;
        private readonly Func<BridgeVerifyResponse> _runVerification;
        private readonly Func<BridgeFindReplaceRequest, BridgeFindReplaceResponse> _findReplace;
        private readonly Func<BridgeRunTaskRequest, BridgeRunTaskResponse> _runTask;
        private readonly Func<string, BridgeTaskStatusResponse> _getTaskStatus;
        private readonly Func<int, BridgePromptContextResponse> _getPromptContext;
        private readonly Func<BridgeEditTermRequest, BridgeEditTermResponse> _updateTerm;
        private readonly Func<BridgeEditTermRequest, BridgeEditTermResponse> _deleteTerm;
        private readonly Func<BridgeResultResponse> _saveDocument;
        private readonly Func<BridgeSuperMemoryQuery, BridgeSuperMemoryContextResponse> _getSuperMemoryContext;
        private readonly Func<BridgeSuperMemorySearchQuery, BridgeSuperMemorySearchResponse> _searchSuperMemory;
        private readonly Func<BridgeSuperMemoryBanksResponse> _listSuperMemoryBanks;
        private readonly Func<BridgeMarkReviewedRequest, BridgeMarkReviewedResponse> _markReviewed;
        private readonly Func<BridgeCoverageResponse> _getCoverage;
        private readonly Func<BridgeTrackedChangesQuery, BridgeTrackedChangesResponse> _getTrackedChanges;

        /// <summary>Max segment updates per /v1/update-segments call – keeps a
        /// single request from freezing the editor thread for minutes on huge
        /// documents; callers page through larger jobs.
        ///
        /// Kept below what the MCP server exe's HTTP client will wait for
        /// (BridgeClient.Http.Timeout). Field feedback: batches of ~45+ took
        /// longer than the exe's old 30 s timeout, and because the write had
        /// already been applied by then, the caller lost the confirmation
        /// without losing the edit – indistinguishable from a failure. The exe
        /// timeout has since been raised, but the cap stays conservative so an
        /// older exe still gets an answer.</summary>
        public const int MaxUpdatesPerRequest = 40;

        // ── MCP exe version handshake ────────────────────────────────────────
        //
        // The MCP server exe reports its protocol level in an
        // X-Supervertaler-Mcp-Exe-Version header (from level 2 on); exes older
        // than the handshake send no header and count as level 1. When a plugin
        // feature someday genuinely needs a newer exe, bump RequiredExeVersion:
        // outdated installs then get an upgrade note via the help tool, the
        // get_active_project note, and the Connect dialog - the AI relays it to
        // the user in chat, so no extra UI is needed.

        /// <summary>Minimum exe protocol level this plugin needs.
        ///
        /// Raised to 2 on 2026-08-08. Level 1 means a PRE-HANDSHAKE exe (no
        /// version header at all), i.e. built before 20.110 on 2026-07-21 -
        /// which also predates the 20.148 fix raising the exe's own HTTP timeout
        /// from 30 seconds to 5 minutes. Those installs report long operations as
        /// failures that in fact succeeded, and an agent that retries on timeout
        /// then double-writes, so they are worth one nag.
        ///
        /// KNOWN GAP: an exe from the 20.110-20.147 window reports level 2 and so
        /// is NOT flagged, yet still carries the 30-second timeout. Closing that
        /// would mean bumping ExeProtocolVersion to 3 in the exe and requiring 3
        /// here, which also nags everyone whose exe is merely not the newest.
        /// Deliberately not done - the timeout is the only known symptom and the
        /// tool list is fetched from this plugin at runtime, so an older exe is
        /// otherwise fully functional.</summary>
        public const int RequiredExeVersion = 2;

        /// <summary>Exe protocol level last seen on a bridge request this
        /// session. 0 = no MCP request yet; 1 = pre-handshake exe.</summary>
        public static int LastSeenExeVersion { get; private set; }

        /// <summary>True when an exe has connected and it is older than what
        /// this plugin requires.</summary>
        public static bool ExeOutdated =>
            LastSeenExeVersion > 0 && LastSeenExeVersion < RequiredExeVersion;

        /// <summary>Note the AI is asked to relay to the user when their
        /// extension exe is outdated.</summary>
        public const string ExeUpgradeNote =
            "NOTE FOR THE ASSISTANT - relay this to the user once, then carry on with " +
            "their request: their Supervertaler MCP extension is an old build whose own " +
            "request timeout is 30 seconds, so long operations (saving or updating a big " +
            "document) can be REPORTED AS FAILURES even though they actually succeeded. " +
            "Never blindly retry a write after a timeout on this build - check whether it " +
            "landed first. To fix it: in Trados open Supervertaler Settings > AI Settings > " +
            "Connect AI assistant..., download the latest extension (also on the latest " +
            "GitHub release of Supervertaler for Trados), reinstall it in the AI app and " +
            "restart the app. Everything else keeps working meanwhile - the tool list is " +
            "read from the plugin at runtime, so this old build still has the current tools.";

        /// <summary>When this bridge started listening; fixed for the session.</summary>
        private string _startedAtUtc;

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
            Func<int, int, BridgeInconsistenciesResponse> findInconsistencies = null,
            Func<BridgeStudioTmQuery, BridgeTmSearchResponse> searchStudioTm = null,
            Func<BridgeQaQuery, BridgeQaResponse> runQaCheck = null,
            Func<BridgeTmCompareQuery, BridgeTmCompareResponse> compareTm = null,
            Func<BridgeResourcesResponse> listResources = null,
            Func<BridgeGoToRequest, BridgeResultResponse> goToSegment = null,
            Func<string, BridgeCommentsResponse> getComments = null,
            Func<BridgeAddCommentRequest, BridgeResultResponse> addComment = null,
            Func<BridgeUpdateCommentRequest, BridgeResultResponse> updateComment = null,
            Func<BridgeDeleteCommentRequest, BridgeResultResponse> deleteComment = null,
            Func<BridgeVerifyResponse> runVerification = null,
            Func<BridgeFindReplaceRequest, BridgeFindReplaceResponse> findReplace = null,
            Func<BridgeRunTaskRequest, BridgeRunTaskResponse> runTask = null,
            Func<string, BridgeTaskStatusResponse> getTaskStatus = null,
            Func<int, BridgePromptContextResponse> getPromptContext = null,
            Func<BridgeEditTermRequest, BridgeEditTermResponse> updateTerm = null,
            Func<BridgeEditTermRequest, BridgeEditTermResponse> deleteTerm = null,
            Func<BridgeResultResponse> saveDocument = null,
            Func<BridgeSuperMemoryQuery, BridgeSuperMemoryContextResponse> getSuperMemoryContext = null,
            Func<BridgeSuperMemorySearchQuery, BridgeSuperMemorySearchResponse> searchSuperMemory = null,
            Func<BridgeSuperMemoryBanksResponse> listSuperMemoryBanks = null,
            Func<BridgeMarkReviewedRequest, BridgeMarkReviewedResponse> markReviewed = null,
            Func<BridgeCoverageResponse> getCoverage = null,
            Func<BridgeTrackedChangesQuery, BridgeTrackedChangesResponse> getTrackedChanges = null,
            Func<BridgeImportTermbaseRequest, BridgeImportTermbaseResponse> importTermbase = null,
            Func<BridgeInstanceInfo> getInstanceInfo = null)
        {
            _getInstanceInfo = getInstanceInfo;
            _getContext = getContext ?? throw new ArgumentNullException(nameof(getContext));
            _insertText = insertText ?? throw new ArgumentNullException(nameof(insertText));
            _getProject = getProject;
            _getSegments = getSegments;
            _getDbPath = getDbPath;
            _updateSegments = updateSegments;
            _addTerm = addTerm;
            _importTermbase = importTermbase;
            _getFiles = getFiles;
            _findInconsistencies = findInconsistencies;
            _searchStudioTm = searchStudioTm;
            _runQaCheck = runQaCheck;
            _compareTm = compareTm;
            _listResources = listResources;
            _goToSegment = goToSegment;
            _getComments = getComments;
            _addComment = addComment;
            _updateComment = updateComment;
            _deleteComment = deleteComment;
            _runVerification = runVerification;
            _findReplace = findReplace;
            _runTask = runTask;
            _getTaskStatus = getTaskStatus;
            _getPromptContext = getPromptContext;
            _updateTerm = updateTerm;
            _deleteTerm = deleteTerm;
            _saveDocument = saveDocument;
            _getSuperMemoryContext = getSuperMemoryContext;
            _searchSuperMemory = searchSuperMemory;
            _listSuperMemoryBanks = listSuperMemoryBanks;
            _markReviewed = markReviewed;
            _getCoverage = getCoverage;
            _getTrackedChanges = getTrackedChanges;
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
            _startedAtUtc = DateTime.UtcNow.ToString("o");

            // Before advertising ourselves, clear out instances that died without
            // running Stop() – otherwise we look like the second of two live
            // Studios and clients start refusing writes for no reason.
            SweepStaleInstanceFiles();

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
                WriteHandshakeFile(includeShared: true);
                BridgeLog.Write($"handshake file written at {UserDataPath.SupervertalerBridgeFile}");
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"FAILED to write handshake file: {ex.GetType().Name}: {ex.Message}");
                // Bridge is still usable, just not discoverable – not fatal.
            }

            HookProcessShutdown();

            BridgeLog.Write($"Start() complete. Bridge live on http://127.0.0.1:{_port}/ with token {_token.Substring(0, 8)}…");
        }

        // ── Shutdown hooks ───────────────────────────────────────────────
        //
        // AiAssistantViewPart.Dispose() calls Stop(), but Trados does not reliably
        // dispose its view parts on the way out: observed in the field, a normal
        // Studio close left both handshake files behind and wrote no shutdown line
        // to the log. Nothing breaks — readers reject a handshake whose process is
        // gone — but the shared bridge.json is then never handed over to a Studio
        // that is still running, so an older client sees a dead handshake instead
        // of a live one.
        //
        // NOT RELIED UPON. Measured in Trados on 2026-08-25: closing Studio normally
        // fires neither this nor the view part's Dispose, so the handshake outlives
        // the process either way. The recovery that actually works is on the other
        // side — SweepStaleInstanceFiles and ClaimSharedHandshakeIfAbandoned, run by
        // whichever Studio is still alive. This hook stays because it is correct
        // when it does fire (Dispose, and hosts that shut the CLR down properly),
        // and costs nothing when it does not. Do not build anything on it.
        //
        // Stop() is idempotent, so being called from Dispose and again from the hook
        // is harmless.
        //
        // DO NOT ALSO HOOK System.Windows.Forms.Application.ApplicationExit.
        // It fires whenever *a* message loop ends, not when Studio quits, and
        // Studio runs a short-lived loop while it starts up. Observed 2026-08-25:
        // the event arrived 258 ms after Start() completed, so the bridge tore its
        // own handshake down and closed its listener seconds into the session —
        // live on a port, discoverable by nobody, for as long as Studio stayed
        // open. Nothing in the log said anything was wrong; the only symptom was
        // an empty runtime folder.

        private bool _shutdownHooked;

        private void HookProcessShutdown()
        {
            if (_shutdownHooked) return;
            _shutdownHooked = true;
            try
            {
                AppDomain.CurrentDomain.ProcessExit += OnProcessShutdown;
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"could not hook process shutdown: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void UnhookProcessShutdown()
        {
            if (!_shutdownHooked) return;
            _shutdownHooked = false;
            try { AppDomain.CurrentDomain.ProcessExit -= OnProcessShutdown; } catch { }
        }

        private void OnProcessShutdown(object sender, EventArgs e)
        {
            try
            {
                BridgeLog.Write("process shutdown – releasing handshake");

                // Only the handshake, NOT the listener: at genuine process exit the
                // OS reclaims the socket anyway, and keeping the teardown out of
                // this path means a hook that ever fires when it should not costs a
                // rewritable file rather than a dead bridge. Learned the hard way —
                // see the ApplicationExit note above.
                ReleaseHandshakeFiles();
            }
            catch { /* never throw out of a shutdown handler */ }
        }

        public void Stop()
        {
            UnhookProcessShutdown();
            try { _cts?.Cancel(); } catch { /* ignore */ }
            try { _listener?.Stop(); } catch { /* ignore */ }
            try { _listener?.Close(); } catch { /* ignore */ }
            _listener = null;

            ReleaseHandshakeFiles();
        }

        /// <summary>
        /// Withdraw this process's handshake so no client is pointed at a bridge
        /// that is going away, and hand the shared file to a Studio still running.
        /// Safe to call more than once.
        /// </summary>
        private void ReleaseHandshakeFiles()
        {
            var myPid = 0;
            try { myPid = Process.GetCurrentProcess().Id; } catch { }

            try
            {
                // Only delete the shared handshake if it is still OURS. Deleting it
                // unconditionally is what left a second Studio listening but
                // undiscoverable: its bridge was fine, its handshake was gone.
                if (File.Exists(UserDataPath.SupervertalerBridgeFile))
                {
                    var shared = ReadHandshakeFile(UserDataPath.SupervertalerBridgeFile);
                    if (shared == null || shared.Pid == myPid)
                        File.Delete(UserDataPath.SupervertalerBridgeFile);
                    else
                        BridgeLog.Write($"shared handshake belongs to PID {shared.Pid} – left in place");
                }
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] failed to delete handshake file: {ex.Message}");
            }

            try
            {
                if (myPid > 0)
                {
                    var mine = UserDataPath.SupervertalerBridgeInstanceFile(myPid);
                    if (File.Exists(mine)) File.Delete(mine);
                }
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] failed to delete instance handshake: {ex.Message}");
            }

            PromoteSurvivingInstanceToSharedHandshake(myPid);

            // Note on the listener thread: HttpListener.Stop unblocks GetContext,
            // but the thread cleanup is best-effort. It's a background thread and
            // will die with the process anyway, so Stop() never joins it.
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

                // Hand the request to a worker and go straight back to
                // GetContext(). Previously HandleRequest ran INLINE here, so the
                // listener accepted exactly one request at a time: a single slow
                // call blocked every later one, and because a client-side timeout
                // does not cancel the work already running on this side, an
                // abandoned request kept the queue stalled and each retry made it
                // worse. Measured before this change: /v1/project answered in
                // 0.4 s idle but took 84 s when issued behind two abandoned calls.
                //
                // Handlers that touch Trados marshal to the UI thread themselves
                // and still serialize there – that is inherent, the SDK is
                // UI-thread-bound. What this fixes is everything that does NOT
                // need the UI thread (term lookup, help, the tool registry,
                // session_report) no longer waiting behind something that does.
                // The shared bridge state is already lock-protected
                // (BridgePayloadLedger, the coverage sets), so concurrent
                // handlers are safe.
                ThreadPool.QueueUserWorkItem(state =>
                {
                    var ctx = (HttpListenerContext)state;
                    try
                    {
                        HandleRequest(ctx);
                    }
                    catch (Exception ex)
                    {
                        BridgeLog.Write($"[SupervertalerBridge] HandleRequest threw: {ex.Message}");
                        TryWriteError(ctx, 500, "internal error");
                    }
                    finally
                    {
                        try { ctx.Response.Close(); } catch { /* ignore */ }
                    }
                }, context);
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

            // Version handshake: the MCP server exe reports its protocol level on
            // every request (from exe protocol v2 on). Absence of the header means
            // a pre-handshake exe -> level 1. Lets the plugin detect an outdated
            // extension and tell the AI to tell the user to update it.
            var exeVerHeader = request.Headers["X-Supervertaler-Mcp-Exe-Version"];
            int seenVer = 1;
            if (!string.IsNullOrEmpty(exeVerHeader) && int.TryParse(exeVerHeader, out var v) && v > 1)
                seenVer = v;
            if (seenVer != LastSeenExeVersion)
            {
                LastSeenExeVersion = seenVer;
                BridgeLog.Write($"[SupervertalerBridge] MCP exe protocol level: {seenVer}" +
                    (ExeOutdated ? $" (OUTDATED - plugin requires {RequiredExeVersion})" : ""));
            }

            var path = request.Url.AbsolutePath;
            var method = request.HttpMethod;

            // Cost instrumentation: tally what this call carries in. The
            // response side is tallied in WriteJson/WriteRawJson.
            RecordRequestPayload(context);

            if (method == "GET" && path == SessionReportPath)
            {
                HandleSessionReport(context);
                return;
            }

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

            if (method == "POST" && path == "/v1/import-termbase")
            {
                HandleImportTermbase(context);
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

            if (method == "GET" && path == "/v1/coverage")
            {
                HandleCoverage(context);
                return;
            }

            if (method == "GET" && path == "/v1/tracked-changes")
            {
                HandleTrackedChanges(context);
                return;
            }

            if (method == "POST" && path == "/v1/mark-reviewed")
            {
                HandleMarkReviewed(context);
                return;
            }

            if (method == "GET" && path == "/v1/compare-tm")
            {
                HandleCompareTm(context);
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

            if (method == "POST" && path == "/v1/delete-comment")
            {
                HandleDelegatePost<BridgeDeleteCommentRequest>(context, _deleteComment, "delete-comment");
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
            if (method == "GET" && path == "/v1/projects")
            {
                HandleDiskTool(context, "studio_list_projects",
                    "{\"status_filter\":" + JsonQuote(QueryUtf8(context.Request)["status"] ?? "") + "}");
                return;
            }
            if (method == "GET" && path == "/v1/project-info")
            {
                HandleDiskTool(context, "studio_get_project",
                    "{\"project_name\":" + JsonQuote(QueryUtf8(context.Request)["name"] ?? "") + "}");
                return;
            }
            if (method == "GET" && path == "/v1/tms")
            {
                HandleDiskTool(context, "studio_list_tms", "{}");
                return;
            }
            if (method == "GET" && path == "/v1/project-templates")
            {
                HandleDiskTool(context, "studio_list_project_templates", "{}");
                return;
            }
            if (method == "POST" && path == "/v1/update-term")
            {
                HandleEditTerm(context, _updateTerm, "update-term");
                return;
            }
            if (method == "POST" && path == "/v1/delete-term")
            {
                HandleEditTerm(context, _deleteTerm, "delete-term");
                return;
            }
            if (method == "POST" && path == "/v1/save-document")
            {
                if (_saveDocument == null) { TryWriteError(context, 501, "save-document endpoint not wired"); return; }
                BridgeResultResponse saveResp;
                try { saveResp = _saveDocument() ?? new BridgeResultResponse { Ok = false, Error = "internal error" }; }
                catch (Exception ex)
                {
                    BridgeLog.Write($"[SupervertalerBridge] save-document threw: {ex.Message}");
                    saveResp = new BridgeResultResponse { Ok = false, Error = "save failed: " + ex.Message };
                }
                WriteJson(context, 200, saveResp);
                return;
            }

            if (method == "GET" && path == "/v1/supermemory-context")
            {
                HandleSuperMemoryContext(context);
                return;
            }
            if (method == "GET" && path == "/v1/supermemory-search")
            {
                HandleSuperMemorySearch(context);
                return;
            }
            if (method == "GET" && path == "/v1/supermemory-banks")
            {
                HandleSuperMemoryBanks(context);
                return;
            }

            TryWriteError(context, 404, "not found");
        }

        private void HandleSuperMemoryContext(HttpListenerContext context)
        {
            if (_getSuperMemoryContext == null)
            {
                TryWriteError(context, 501, "supermemory-context endpoint not wired");
                return;
            }

            var q = new BridgeSuperMemoryQuery
            {
                Query = QueryUtf8(context.Request)["q"],
                Domain = QueryUtf8(context.Request)["domain"],
                Client = QueryUtf8(context.Request)["client"],
                Bank = QueryUtf8(context.Request)["bank"]
            };
            int budget;
            if (int.TryParse(QueryUtf8(context.Request)["tokenBudget"], out budget) && budget > 0)
                q.TokenBudget = Math.Min(budget, 100000);

            BridgeSuperMemoryContextResponse response;
            try
            {
                response = _getSuperMemoryContext(q)
                    ?? new BridgeSuperMemoryContextResponse { Available = false };
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] supermemory-context threw: {ex.Message}");
                response = new BridgeSuperMemoryContextResponse { Available = false, Note = "error: " + ex.Message };
            }

            WriteJson(context, 200, response);
        }

        private void HandleSuperMemorySearch(HttpListenerContext context)
        {
            if (_searchSuperMemory == null)
            {
                TryWriteError(context, 501, "supermemory-search endpoint not wired");
                return;
            }

            var query = QueryUtf8(context.Request)["q"];
            if (string.IsNullOrWhiteSpace(query))
            {
                WriteJson(context, 400, new BridgeSuperMemorySearchResponse
                {
                    Available = false,
                    Note = "missing 'q'"
                });
                return;
            }

            var q = new BridgeSuperMemorySearchQuery { Query = query };
            int limit;
            if (int.TryParse(QueryUtf8(context.Request)["limit"], out limit) && limit > 0)
                q.Limit = Math.Min(limit, 50);

            BridgeSuperMemorySearchResponse response;
            try
            {
                response = _searchSuperMemory(q)
                    ?? new BridgeSuperMemorySearchResponse { Available = false };
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] supermemory-search threw: {ex.Message}");
                response = new BridgeSuperMemorySearchResponse { Available = false, Note = "error: " + ex.Message };
            }

            WriteJson(context, 200, response);
        }

        private void HandleSuperMemoryBanks(HttpListenerContext context)
        {
            if (_listSuperMemoryBanks == null)
            {
                TryWriteError(context, 501, "supermemory-banks endpoint not wired");
                return;
            }

            BridgeSuperMemoryBanksResponse response;
            try
            {
                response = _listSuperMemoryBanks() ?? new BridgeSuperMemoryBanksResponse { Available = false };
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] supermemory-banks threw: {ex.Message}");
                response = new BridgeSuperMemoryBanksResponse { Available = false, Note = "error: " + ex.Message };
            }

            WriteJson(context, 200, response);
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
                var help = _cachedHelpCard;
                if (ExeOutdated)
                    help = ExeUpgradeNote + "\n\n---\n\n" + help;
                WriteJson(context, 200, new BridgeHelpResponse { Ok = true, Help = help });
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] help card read failed: {ex.Message}");
                TryWriteError(context, 500, "help card error: " + ex.Message);
            }
        }

        /// <summary>Shared handler for POST /v1/update-term and /v1/delete-term.</summary>
        private void HandleEditTerm(HttpListenerContext context,
            Func<BridgeEditTermRequest, BridgeEditTermResponse> handler, string name)
        {
            if (handler == null)
            {
                TryWriteError(context, 501, name + " endpoint not wired");
                return;
            }

            BridgeEditTermRequest req;
            try
            {
                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                    req = DeserializeJson<BridgeEditTermRequest>(reader.ReadToEnd());
            }
            catch (Exception ex)
            {
                WriteJson(context, 400, new BridgeEditTermResponse { Ok = false, Error = "malformed body: " + ex.Message });
                return;
            }

            if (req == null || string.IsNullOrWhiteSpace(req.Source) || string.IsNullOrWhiteSpace(req.Target))
            {
                WriteJson(context, 400, new BridgeEditTermResponse
                {
                    Ok = false,
                    Error = "missing 'source'/'target' – identify the entry by its exact current source and target terms"
                });
                return;
            }

            BridgeEditTermResponse response;
            try
            {
                response = handler(req) ?? new BridgeEditTermResponse { Ok = false, Error = "internal error" };
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] {name} threw: {ex.Message}");
                response = new BridgeEditTermResponse { Ok = false, Error = name + " failed: " + ex.Message };
            }

            WriteJson(context, 200, response);
        }

        /// <summary>
        /// Shared handler for the pure disk-read TradosTools tools (list projects /
        /// project details / TMs / templates). No editor state, no UI-thread hop:
        /// ExecuteTool reads the projects.xml registries and files on disk and
        /// returns ready-made JSON, which is passed through verbatim (errors come
        /// back as {"error":"..."} inside the payload).
        /// </summary>
        private void HandleDiskTool(HttpListenerContext context, string toolName, string inputJson)
        {
            try
            {
                var result = TradosTools.ExecuteTool(toolName, inputJson);
                WriteRawJson(context, 200, result ?? "{\"error\":\"no result\"}");
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] {toolName} threw: {ex.Message}");
                TryWriteError(context, 500, toolName + " failed: " + ex.Message);
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

            // Outdated-extension nudge on the highest-traffic informational tool,
            // so the AI relays it to the user in chat.
            if (ExeOutdated)
                snapshot.Note = string.IsNullOrEmpty(snapshot.Note)
                    ? ExeUpgradeNote
                    : snapshot.Note + " | " + ExeUpgradeNote;

            WriteJson(context, 200, snapshot);
        }

        private void HandleGetSegments(HttpListenerContext context)
        {
            if (_getSegments == null)
            {
                TryWriteError(context, 501, "segments endpoint not wired");
                return;
            }

            var qs = QueryUtf8(context.Request);
            var query = new BridgeSegmentsQuery
            {
                Status = qs["status"],
                Contains = qs["contains"],
                File = qs["file"]
            };
            int limit, offset, fromNum, toNum, matchMin, matchMax;
            if (int.TryParse(qs["limit"], out limit) && limit > 0)
                query.Limit = Math.Min(limit, 2000);
            if (int.TryParse(qs["offset"], out offset) && offset > 0)
                query.Offset = offset;
            if (int.TryParse(qs["fromNumber"], out fromNum) && fromNum > 0)
                query.FromNumber = fromNum;
            if (int.TryParse(qs["toNumber"], out toNum) && toNum > 0)
                query.ToNumber = toNum;
            if (int.TryParse(qs["matchMin"], out matchMin) && matchMin >= 0)
                query.MatchMin = Math.Min(matchMin, 100);
            if (int.TryParse(qs["matchMax"], out matchMax) && matchMax >= 0)
                query.MatchMax = Math.Min(matchMax, 100);

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
            var query = QueryUtf8(context.Request)["q"];
            if (string.IsNullOrWhiteSpace(query))
            {
                WriteJson(context, 400, new BridgeTmSearchResponse { Ok = false, Error = "missing 'q'" });
                return;
            }

            int limit;
            if (!int.TryParse(QueryUtf8(context.Request)["limit"], out limit) || limit <= 0)
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
            var term = QueryUtf8(context.Request)["q"];
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

                    // The DB search covers EVERY Supervertaler termbase, including
                    // ones whose Read tick is off. Flag hits from inactive termbases,
                    // and drop them entirely when the caller asked activeOnly=true.
                    bool activeOnly = string.Equals(
                        QueryUtf8(context.Request)["activeOnly"], "true", StringComparison.OrdinalIgnoreCase);
                    HashSet<long> disabledTbs;
                    // The settings' Project tick is the authoritative project flag
                    // (drives TermLens's pink chips) – the DB column is often stale.
                    long projectTbId = -1;
                    try
                    {
                        var tlSettings = SettingsService.Current;
                        disabledTbs = new HashSet<long>(
                            tlSettings?.DisabledTermbaseIds ?? new List<long>());
                        projectTbId = tlSettings?.ProjectTermbaseId ?? -1;
                    }
                    catch { disabledTbs = new HashSet<long>(); }

                    // Declared direction per termbase, to spot entries stored the
                    // wrong way round. The hit's source/target and its lang tags
                    // agree with each other even when the row is reversed, so the
                    // only way to see the fault is to compare against what the
                    // termbase itself declares.
                    Dictionary<long, (string src, string tgt)> tbDirections;
                    try { tbDirections = reader.GetTermbaseDirections(); }
                    catch { tbDirections = new Dictionary<long, (string src, string tgt)>(); }

                    int excludedInactive = 0;
                    int reversedHits = 0;
                    foreach (var entry in entries)
                    {
                        var inactive = disabledTbs.Contains(entry.TermbaseId);
                        if (inactive && activeOnly) { excludedInactive++; continue; }

                        bool reversed = false;
                        (string src, string tgt) declared;
                        if (tbDirections.TryGetValue(entry.TermbaseId, out declared))
                            reversed = LanguageUtils.EntryDirectionContradictsTermbase(
                                entry.SourceTerm, entry.TargetTerm,
                                entry.SourceLang, entry.TargetLang, declared.src, declared.tgt);
                        if (reversed) reversedHits++;

                        response.Hits.Add(new BridgeTermbaseHit
                        {
                            Source = entry.SourceTerm ?? "",
                            Target = entry.TargetTerm ?? "",
                            TermbaseName = entry.TermbaseName,
                            Definition = entry.Definition,
                            Domain = entry.Domain,
                            Notes = entry.Notes,
                            NonTranslatable = entry.IsNonTranslatable,
                            Inactive = inactive,
                            MatchedField = ComputeMatchedField(q, entry.SourceTerm, entry.TargetTerm),
                            SourceLang = entry.SourceLang,
                            TargetLang = entry.TargetLang,
                            IsProjectTermbase = entry.TermbaseId == projectTbId,
                            DirectionMismatch = reversed
                        });
                    }
                    if (excludedInactive > 0)
                        response.Note = ((response.Note ?? "") +
                            $" {excludedInactive} hit(s) from inactive (Read-unticked) termbases were excluded (activeOnly).").Trim();
                    if (reversedHits > 0)
                        response.Note = ((response.Note ?? "") +
                            $" {reversedHits} hit(s) carry directionMismatch: the entry's own stored languages " +
                            "contradict its termbase's declared direction. Read the two terms and decide which " +
                            "case it is. If the TEXT is the wrong way round (the source column holding the " +
                            "termbase's target language), the entry is indexed under the wrong language – it " +
                            "matches no source segment, check_terminology can never report it however badly the " +
                            "document violates it, and a term that looks present and locked is enforcing nothing; " +
                            "repair it by re-adding the pair the right way round for that termbase. If only the " +
                            "language labels are wrong and the terms themselves are correctly oriented, the entry " +
                            "works exactly as it should and only the labels need correcting. Do not assume the " +
                            "first case: both occur, and on a real termbase the harmless one was a real share of " +
                            "the flags.").Trim();
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
                            NonTranslatable = entry.IsNonTranslatable,
                            MatchedField = ComputeMatchedField(q, entry.SourceTerm, entry.TargetTerm),
                            SourceLang = entry.SourceLang,
                            TargetLang = entry.TargetLang,
                            IsProjectTermbase = true
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

        /// <summary>Which stored column the query text matched: "source",
        /// "target" or "both". Tries exact (trailing-punctuation-tolerant)
        /// equality first, then substring containment, mirroring the two
        /// search stages – so the answer reflects however the hit was found.</summary>
        private static string ComputeMatchedField(string q, string source, string target)
        {
            bool srcHit = FieldMatches(q, source);
            bool tgtHit = FieldMatches(q, target);
            if (srcHit && tgtHit) return "both";
            if (tgtHit) return "target";
            return "source";
        }

        private static bool FieldMatches(string q, string field)
        {
            if (string.IsNullOrEmpty(field)) return false;
            var f = field.Trim();
            var query = (q ?? "").Trim();
            if (string.Equals(f, query, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(f.TrimEnd('.', '!', '?', ',', ';', ':'), query,
                    StringComparison.OrdinalIgnoreCase)) return true;
            return f.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || query.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0;
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

        private void HandleCoverage(HttpListenerContext context)
        {
            if (_getCoverage == null)
            {
                TryWriteError(context, 501, "coverage endpoint not wired");
                return;
            }

            BridgeCoverageResponse response;
            try
            {
                response = _getCoverage() ?? new BridgeCoverageResponse { Available = false };
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] coverage threw: {ex.Message}");
                response = new BridgeCoverageResponse { Available = false, Note = "coverage failed: " + ex.Message };
            }
            WriteJson(context, 200, response);
        }

        private void HandleTrackedChanges(HttpListenerContext context)
        {
            if (_getTrackedChanges == null)
            {
                TryWriteError(context, 501, "tracked-changes endpoint not wired");
                return;
            }

            var qs = QueryUtf8(context.Request);
            var query = new BridgeTrackedChangesQuery();
            bool save;
            if (bool.TryParse(qs["save"], out save))
                query.Save = save;
            int limit;
            if (int.TryParse(qs["limit"], out limit) && limit > 0)
                query.Limit = Math.Min(limit, 500);

            BridgeTrackedChangesResponse response;
            try
            {
                response = _getTrackedChanges(query) ?? new BridgeTrackedChangesResponse { Available = false };
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] tracked-changes threw: {ex.Message}");
                response = new BridgeTrackedChangesResponse
                {
                    Available = false,
                    Note = "tracked-changes failed: " + ex.Message
                };
            }
            WriteJson(context, 200, response);
        }

        private void HandleMarkReviewed(HttpListenerContext context)
        {
            if (_markReviewed == null)
            {
                TryWriteError(context, 501, "mark-reviewed endpoint not wired");
                return;
            }

            BridgeMarkReviewedRequest req;
            try
            {
                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                {
                    req = DeserializeJson<BridgeMarkReviewedRequest>(reader.ReadToEnd());
                }
            }
            catch (Exception ex)
            {
                WriteJson(context, 400, new BridgeMarkReviewedResponse
                {
                    Ok = false,
                    Note = "malformed body: " + ex.Message
                });
                return;
            }

            if (req?.Ids == null || req.Ids.Count == 0)
            {
                WriteJson(context, 400, new BridgeMarkReviewedResponse
                {
                    Ok = false,
                    Note = "missing 'ids' array"
                });
                return;
            }

            BridgeMarkReviewedResponse response;
            try
            {
                response = _markReviewed(req) ?? new BridgeMarkReviewedResponse { Ok = false };
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] mark-reviewed threw: {ex.Message}");
                response = new BridgeMarkReviewedResponse { Ok = false, Note = "mark-reviewed failed: " + ex.Message };
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

            if (req?.Entries != null && req.Entries.Count > 0)
            {
                HandleAddTermBatch(context, req);
                return;
            }

            if (string.IsNullOrWhiteSpace(req?.Source) || string.IsNullOrWhiteSpace(req?.Target))
            {
                WriteJson(context, 400, new BridgeAddTermResponse
                {
                    Ok = false,
                    Error = "both 'source' and 'target' are required, or pass 'entries'"
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

        /// <summary>
        /// Many term pairs in one call, each decided independently.
        ///
        /// <para><b>A duplicate or a failure on one entry must not abort the
        /// batch.</b> That is the requirement this exists to satisfy: a 40-term
        /// batch that dies on entry 12 because entry 12 already existed is worse
        /// than the single-pair interface, not better. Every entry gets its own
        /// result, in input order, so a caller can tell exactly which landed.</para>
        ///
        /// <para>Each entry goes through the SAME delegate a single call uses,
        /// rather than a bulk write path. Scope resolution, orientation and
        /// duplicate detection then cannot drift between the two interfaces,
        /// which matters more here than saving database round trips: the cost
        /// this addresses is repeated parameter boilerplate, not SQLite.</para>
        /// </summary>
        private void HandleAddTermBatch(HttpListenerContext context, BridgeAddTermRequest req)
        {
            const int MaxEntries = 40;   // matches update_segments

            // Both forms at once is a caller error, not something to merge:
            // whoever did it meant one of them, and guessing which is worse than
            // saying so.
            if (!string.IsNullOrWhiteSpace(req.Source) || !string.IsNullOrWhiteSpace(req.Target))
            {
                WriteJson(context, 400, new BridgeAddTermBatchResponse
                {
                    Ok = false,
                    Error = "pass either 'entries' or a single 'source'/'target' pair, not both"
                });
                return;
            }

            if (req.Entries.Count > MaxEntries)
            {
                WriteJson(context, 400, new BridgeAddTermBatchResponse
                {
                    Ok = false,
                    Error = "too many entries: " + req.Entries.Count + ". The maximum is "
                          + MaxEntries + " per call - split the list rather than sending more, "
                          + "since a truncated batch would look like a complete one."
                });
                return;
            }

            var items = new List<BridgeAddTermBatchItem>();
            var summary = new BridgeAddTermBatchSummary();

            for (int i = 0; i < req.Entries.Count; i++)
            {
                var entry = req.Entries[i] ?? new BridgeAddTermEntry();
                var item = new BridgeAddTermBatchItem
                {
                    Index = i,
                    Source = entry.Source,
                    Target = entry.Target,
                };

                if (string.IsNullOrWhiteSpace(entry.Source) || string.IsNullOrWhiteSpace(entry.Target))
                {
                    item.Ok = false;
                    item.Error = "both 'source' and 'target' are required";
                    summary.Failed++;
                    items.Add(item);
                    continue;
                }

                // Call-level termbases and scope; per-entry languages win where
                // given, so one batch can mix directions.
                var single = new BridgeAddTermRequest
                {
                    Source = entry.Source,
                    Target = entry.Target,
                    Termbases = req.Termbases,
                    Scope = req.Scope,
                    SourceLang = string.IsNullOrWhiteSpace(entry.SourceLang) ? req.SourceLang : entry.SourceLang,
                    TargetLang = string.IsNullOrWhiteSpace(entry.TargetLang) ? req.TargetLang : entry.TargetLang,
                    Definition = entry.Definition,
                    Domain = entry.Domain,
                    Notes = entry.Notes,
                };

                BridgeAddTermResponse one;
                try
                {
                    one = _addTerm(single) ?? new BridgeAddTermResponse
                    {
                        Ok = false,
                        Error = "internal error"
                    };
                }
                catch (Exception ex)
                {
                    // One entry throwing is one entry's problem.
                    BridgeLog.Write($"[SupervertalerBridge] add-term entry {i} threw: {ex.Message}");
                    one = new BridgeAddTermResponse { Ok = false, Error = ex.Message };
                }

                item.Ok = one.Ok;
                item.Error = one.Error;
                item.AddedTo = one.AddedTo;
                item.Results = one.Results;   // per-termbase echo, incl. orientation and role
                item.Note = one.Note;

                // Classify from the per-termbase statuses, NOT from one.Ok.
                // A single add_term returns ok:false for a pure duplicate, so
                // testing !Ok first counted every duplicate as a failure - a
                // re-run of an already-imported batch reported "5 failed" when
                // nothing had gone wrong. The summary is the line a caller reads
                // first, so it has to mean what it says.
                var perTb = one.Results;
                if (perTb != null && perTb.Count > 0)
                {
                    if (perTb.TrueForAll(r =>
                            string.Equals(r.Status, "duplicate", StringComparison.OrdinalIgnoreCase)))
                    {
                        // Only a duplicate when EVERY termbase said so. Already
                        // in one but newly added to another is an add, or a batch
                        // spanning termbases would under-report what it wrote.
                        summary.Duplicates++;
                    }
                    else if (perTb.Exists(r =>
                            string.Equals(r.Status, "added", StringComparison.OrdinalIgnoreCase)))
                    {
                        summary.Added++;
                    }
                    else
                    {
                        summary.Failed++;
                    }
                }
                else if (one.Ok)
                {
                    summary.Added++;
                }
                else
                {
                    summary.Failed++;
                }

                items.Add(item);
            }

            WriteJson(context, 200, new BridgeAddTermBatchResponse
            {
                Ok = true,        // the CALL succeeded; per-entry outcomes are in results
                Summary = summary,
                Results = items,
            });
        }

        private void HandleImportTermbase(HttpListenerContext context)
        {
            if (_importTermbase == null)
            {
                TryWriteError(context, 501, "import-termbase endpoint not wired");
                return;
            }

            BridgeImportTermbaseRequest req;
            try
            {
                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                {
                    req = DeserializeJson<BridgeImportTermbaseRequest>(reader.ReadToEnd());
                }
            }
            catch (Exception ex)
            {
                WriteJson(context, 400, new BridgeImportTermbaseResponse
                {
                    Ok = false,
                    Error = "malformed body: " + ex.Message
                });
                return;
            }

            if (string.IsNullOrWhiteSpace(req?.Into))
            {
                WriteJson(context, 400, new BridgeImportTermbaseResponse
                {
                    Ok = false,
                    Error = "'into' is required – name the Supervertaler termbase to import into"
                });
                return;
            }

            BridgeImportTermbaseResponse response;
            try
            {
                response = _importTermbase(req) ?? new BridgeImportTermbaseResponse
                {
                    Ok = false,
                    Error = "internal error"
                };
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] import-termbase threw: {ex.Message}");
                response = new BridgeImportTermbaseResponse
                {
                    Ok = false,
                    Error = "import failed: " + ex.Message
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
            var projectName = QueryUtf8(context.Request)["project"];

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
                var category = QueryUtf8(context.Request)["category"];
                var query = QueryUtf8(context.Request)["query"];

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
                var relPath = QueryUtf8(context.Request)["path"];
                var name = QueryUtf8(context.Request)["name"];
                if (string.IsNullOrWhiteSpace(relPath) && string.IsNullOrWhiteSpace(name))
                {
                    WriteJson(context, 400, new BridgePromptResponse { Ok = false, Error = "pass 'path' (relativePath from list_prompts) or 'name'" });
                    return;
                }

                var lib = new PromptLibrary();
                PromptTemplate p = null;
                if (!string.IsNullOrWhiteSpace(relPath))
                    p = PromptPaths.Find(lib, relPath);   // marker-tolerant (#100)
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
                    // Marker-tolerant (#100): a client holding a path from before a
                    // rename must still update the same file, not get a 404.
                    target = PromptPaths.Find(lib, req.Path);
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

            var query = QueryUtf8(context.Request)["q"];
            if (string.IsNullOrWhiteSpace(query))
            {
                WriteJson(context, 400, new BridgeTmSearchResponse { Ok = false, Error = "missing 'q'" });
                return;
            }

            var q = new BridgeStudioTmQuery { Query = query };
            var inParam = QueryUtf8(context.Request)["in"];
            if (!string.IsNullOrEmpty(inParam)) q.In = inParam;
            int limit;
            if (int.TryParse(QueryUtf8(context.Request)["limit"], out limit) && limit > 0)
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

        private void HandleCompareTm(HttpListenerContext context)
        {
            if (_compareTm == null)
            {
                TryWriteError(context, 501, "compare-tm endpoint not wired");
                return;
            }

            var q = new BridgeTmCompareQuery
            {
                Tm = QueryUtf8(context.Request)["tm"],
                Status = QueryUtf8(context.Request)["status"]
            };
            int limit;
            if (int.TryParse(QueryUtf8(context.Request)["limit"], out limit) && limit > 0)
                q.Limit = Math.Min(limit, 500);

            BridgeTmCompareResponse response;
            try
            {
                response = _compareTm(q) ?? new BridgeTmCompareResponse { Available = false };
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] compare-tm threw: {ex.Message}");
                response = new BridgeTmCompareResponse
                {
                    Available = false,
                    Error = "TM comparison failed: " + ex.Message
                };
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

            var type = (QueryUtf8(context.Request)["type"] ?? "").ToLowerInvariant();
            if (type != "numbers" && type != "tags" && type != "terminology" && type != "nbsp")
            {
                WriteJson(context, 400, new BridgeQaResponse
                {
                    Available = false,
                    Note = "missing or unknown 'type' – use numbers, tags, terminology, or nbsp"
                });
                return;
            }

            var q = new BridgeQaQuery { Type = type };
            int limit;
            if (int.TryParse(QueryUtf8(context.Request)["limit"], out limit) && limit > 0)
                q.Limit = Math.Min(limit, 200);

            // Terminology only: termbase names or ids. The MCP exe passes an
            // array argument to a GET tool as its RAW JSON text (Program.cs
            // ScalarToString falls through to GetRawText for arrays), so the
            // value here may be either a plain comma list (hand-written call)
            // or ["A","B"] - accept both. A name containing a comma cannot be
            // expressed either way; use the numeric id from list_resources.
            var termbases = QueryUtf8(context.Request)["termbases"];
            if (!string.IsNullOrWhiteSpace(termbases))
            {
                var cleaned = termbases.Trim();
                if (cleaned.StartsWith("[")) cleaned = cleaned.Trim('[', ']');
                q.Termbases = cleaned.Split(',')
                    .Select(x => x.Trim().Trim('"', '\'').Trim())
                    .Where(x => x.Length > 0)
                    .ToList();
            }

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
            if (!int.TryParse(QueryUtf8(context.Request)["limit"], out limit) || limit <= 0)
                limit = 50;
            // Cap raised from 200 and paired with 'offset': at 200-with-no-offset
            // the groups past the cap were unreachable at any limit.
            limit = Math.Min(limit, 500);
            int offset;
            if (!int.TryParse(QueryUtf8(context.Request)["offset"], out offset) || offset < 0)
                offset = 0;

            BridgeInconsistenciesResponse response;
            try
            {
                response = _findInconsistencies(limit, offset) ?? new BridgeInconsistenciesResponse { Available = false };
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

        /// <summary>
        /// Parses the request query with explicit UTF-8 decoding. .NET Framework's
        /// HttpListenerRequest.QueryString does not reliably UTF-8-decode percent-
        /// escaped non-ASCII values, so a "contains"/"q" search for a word like
        /// "orientatie" (with a diaeresis) or a Greek letter silently matched nothing.
        /// RawUrl preserves the on-the-wire escapes; Uri.UnescapeDataString reverses
        /// the MCP client's Uri.EscapeDataString (UTF-8). ASCII values are unchanged,
        /// and the collection is case-insensitive like HttpListenerRequest.QueryString.
        /// </summary>
        private static System.Collections.Specialized.NameValueCollection QueryUtf8(HttpListenerRequest request)
        {
            var result = new System.Collections.Specialized.NameValueCollection();
            var raw = request?.RawUrl ?? "";
            int qi = raw.IndexOf('?');
            if (qi < 0) return result;
            foreach (var pair in raw.Substring(qi + 1).Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                string k = eq >= 0 ? pair.Substring(0, eq) : pair;
                string v = eq >= 0 ? pair.Substring(eq + 1) : "";
                try { k = Uri.UnescapeDataString(k); } catch { }
                try { v = Uri.UnescapeDataString(v); } catch { }
                result.Add(k, v);
            }
            return result;
        }

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
                RecordResponsePayload(context, bytes.Length);
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

            var id = QueryUtf8(context.Request)["id"];
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
            var q = QueryUtf8(context.Request)["maxSegments"];
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

            var id = QueryUtf8(context.Request)["id"];
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

        /// <param name="includeShared">
        /// Whether to (re)claim the shared <c>bridge.json</c>. True at startup only.
        /// A refresh must NOT touch it: rewriting it on every document change would
        /// make the two Studios trade the shared handshake back and forth as the user
        /// switches documents, so an older client would hop between instances
        /// mid-conversation. Claiming it once at start keeps the old, predictable
        /// last-to-start-wins behaviour for those readers.
        /// </param>
        private void WriteHandshakeFile(bool includeShared)
        {
            Directory.CreateDirectory(UserDataPath.TradosRuntimeDir);

            var handshake = BuildHandshake();
            var bytes = SerializeJson(handshake);

            // Shared file: kept for older MCP exes and Workbench's Sidekick
            // Chat. Last writer still wins here – that is what the per-instance
            // file below exists to fix, not something we can change without
            // breaking those readers.
            if (includeShared)
                File.WriteAllBytes(UserDataPath.SupervertalerBridgeFile, bytes);

            try
            {
                Directory.CreateDirectory(UserDataPath.TradosInstancesDir);
                File.WriteAllBytes(UserDataPath.SupervertalerBridgeInstanceFile(handshake.Pid), bytes);
            }
            catch (Exception ex)
            {
                // Non-fatal: a new client falls back to bridge.json, an old one
                // never looked here anyway.
                BridgeLog.Write($"failed to write instance handshake: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private BridgeHandshake BuildHandshake()
        {
            var proc = Process.GetCurrentProcess();
            var asmVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;

            string projectName = null, activeFile = null;
            try
            {
                // Reads the editor, so it must be whatever thread the caller is
                // on – Start() and RefreshInstanceFile() are both called from the
                // UI thread. Never let a null document break the handshake.
                var info = _getInstanceInfo?.Invoke();
                projectName = info?.ProjectName;
                activeFile = info?.ActiveFile;
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"instance info unavailable: {ex.GetType().Name}: {ex.Message}");
            }

            return new BridgeHandshake
            {
                Version = HandshakeVersion,
                Port = _port,
                Token = _token,
                Pid = proc.Id,
                // The moment the bridge came up, NOT the moment this file is being
                // written. RefreshInstanceFile rewrites the handshake on every
                // document change, so stamping "now" here would restamp it — and
                // clients break the two-instance tie on newest startedAt, so the
                // Studio you last clicked in would silently take over from the one
                // that started last. Observed before it was fixed: a handshake
                // 18 seconds "newer" than its own bridge.
                StartedAt = _startedAtUtc ?? DateTime.UtcNow.ToString("o"),
                // 18.x targets Studio 2024, 19.x targets Studio 2026 – the same
                // mapping the update check uses to keep the two generations apart.
                StudioVersion = asmVersion == null ? null
                    : asmVersion.Major == 18 ? "2024"
                    : asmVersion.Major == 19 ? "2026"
                    : null,
                PluginVersion = asmVersion?.ToString(),
                ProjectName = projectName,
                ActiveFile = activeFile,
                ProcessName = SafeProcessName(proc)
            };
        }

        private static string SafeProcessName(Process p)
        {
            try { return p.ProcessName; } catch { return null; }
        }

        /// <summary>
        /// Rewrite this instance's handshake so <c>projectName</c> / <c>activeFile</c>
        /// track the editor. Called on ActiveDocumentChanged: the names are what a
        /// client shows the user when it has to say which Studio it is talking to,
        /// and a stale name there is worse than none.
        /// </summary>
        public void RefreshInstanceFile()
        {
            if (!IsRunning) return;
            try
            {
                WriteHandshakeFile(includeShared: false);

                // Tidy up after sessions that ended while we were running, and take
                // over the shared handshake if its owner is gone. Both are here
                // rather than only in Start() because a long-lived Studio has to
                // cope with others coming and going around it.
                SweepStaleInstanceFiles();
                ClaimSharedHandshakeIfAbandoned();
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"RefreshInstanceFile failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Take over <c>bridge.json</c> when whoever wrote it has gone.
        ///
        /// The handover cannot be done by the Studio that is closing: Trados runs
        /// neither our Dispose nor AppDomain.ProcessExit on a normal close (both
        /// verified in the field on 2026-08-25 — the handshake simply outlived the
        /// process, with nothing in the log). So the SURVIVOR claims the file
        /// instead. Only ever claims from a dead owner, so two live Studios never
        /// fight over it and the last-to-start rule still holds while both are up.
        /// </summary>
        private void ClaimSharedHandshakeIfAbandoned()
        {
            try
            {
                var myPid = Process.GetCurrentProcess().Id;
                var shared = ReadHandshakeFile(UserDataPath.SupervertalerBridgeFile);
                if (shared != null && (shared.Pid == myPid || IsInstanceAlive(shared)))
                    return;

                File.WriteAllBytes(UserDataPath.SupervertalerBridgeFile, SerializeJson(BuildHandshake()));
                BridgeLog.Write("claimed the shared handshake (previous owner is gone)");
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"could not claim shared handshake: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Drop instance handshakes whose process is gone. Studio does not always
        /// get to run <see cref="Stop"/> – a crash or a kill leaves the file behind,
        /// and a stale entry makes a client believe two instances are live and
        /// refuse writes that were never ambiguous.
        /// </summary>
        private static void SweepStaleInstanceFiles()
        {
            try
            {
                if (!Directory.Exists(UserDataPath.TradosInstancesDir)) return;

                foreach (var file in Directory.GetFiles(UserDataPath.TradosInstancesDir, "bridge-*.json"))
                {
                    try
                    {
                        var hs = ReadHandshakeFile(file);
                        if (hs != null && IsInstanceAlive(hs)) continue;
                        File.Delete(file);
                        BridgeLog.Write($"swept stale instance handshake {Path.GetFileName(file)}");
                    }
                    catch { /* next file */ }
                }
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"instance sweep failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// After we shut down, republish a surviving instance into the shared
        /// <c>bridge.json</c>. Clients that predate per-instance discovery only
        /// read that file, so without this, closing the Studio that happened to
        /// write it last leaves the other one running but invisible to them —
        /// the orphaning bug, just displaced onto older clients.
        /// Newest survivor wins, matching how new clients break the same tie.
        /// </summary>
        private static void PromoteSurvivingInstanceToSharedHandshake(int excludePid)
        {
            try
            {
                if (File.Exists(UserDataPath.SupervertalerBridgeFile)) return;   // still owned by someone else
                if (!Directory.Exists(UserDataPath.TradosInstancesDir)) return;

                BridgeHandshake best = null;
                string bestStartedAt = null;

                foreach (var file in Directory.GetFiles(UserDataPath.TradosInstancesDir, "bridge-*.json"))
                {
                    var hs = ReadHandshakeFile(file);
                    if (hs == null || hs.Pid == excludePid || !IsInstanceAlive(hs)) continue;
                    if (best == null || string.CompareOrdinal(hs.StartedAt, bestStartedAt) > 0)
                    {
                        best = hs;
                        bestStartedAt = hs.StartedAt;
                    }
                }

                if (best == null) return;

                File.WriteAllBytes(UserDataPath.SupervertalerBridgeFile, SerializeJson(best));
                BridgeLog.Write($"promoted instance PID {best.Pid} into the shared handshake");
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"handshake promotion failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>One other Studio the user has open, for the Connect dialog.</summary>
        public class LiveInstanceInfo
        {
            public string StudioVersion { get; set; }
            public string ProjectName { get; set; }
            public int Pid { get; set; }

            /// <summary>e.g. "Studio 2026 – Acme (PROJ-001)".</summary>
            public string Describe()
            {
                var studio = string.IsNullOrEmpty(StudioVersion) ? "Trados Studio" : "Studio " + StudioVersion;
                return string.IsNullOrEmpty(ProjectName) ? studio + " – no project open" : studio + " – " + ProjectName;
            }
        }

        /// <summary>
        /// Every live bridge EXCEPT this process's own. Used by the Connect dialog to
        /// warn that a second Studio is running, because that is what decides whether
        /// the AI will accept an edit — and it is invisible from inside one Studio.
        /// </summary>
        public static List<LiveInstanceInfo> ListOtherLiveInstances()
        {
            var result = new List<LiveInstanceInfo>();
            try
            {
                var myPid = Process.GetCurrentProcess().Id;
                if (!Directory.Exists(UserDataPath.TradosInstancesDir)) return result;

                foreach (var file in Directory.GetFiles(UserDataPath.TradosInstancesDir, "bridge-*.json"))
                {
                    var hs = ReadHandshakeFile(file);
                    if (hs == null || hs.Pid == myPid || !IsInstanceAlive(hs)) continue;
                    result.Add(new LiveInstanceInfo
                    {
                        StudioVersion = hs.StudioVersion,
                        ProjectName = hs.ProjectName,
                        Pid = hs.Pid
                    });
                }
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"ListOtherLiveInstances failed: {ex.GetType().Name}: {ex.Message}");
            }
            return result;
        }

        private static BridgeHandshake ReadHandshakeFile(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var serializer = new DataContractJsonSerializer(typeof(BridgeHandshake));
                    return serializer.ReadObject(fs) as BridgeHandshake;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// PID liveness, plus a process-name match where the handshake records one.
        /// Windows reuses PIDs, and a recycled PID would otherwise resurrect a dead
        /// instance – which on the client side means a write gate pointed at a
        /// process that is not Studio.
        /// </summary>
        private static bool IsInstanceAlive(BridgeHandshake hs)
        {
            if (hs == null || hs.Pid <= 0) return false;

            Process proc;
            try
            {
                // Throws ArgumentException when no such process exists – that,
                // not HasExited, is how a dead PID is detected here.
                proc = Process.GetProcessById(hs.Pid);
            }
            catch
            {
                return false;
            }

            using (proc)
            {
                // DO NOT call proc.HasExited. It throws Win32Exception "Access is
                // denied" when a 32-bit Studio 2024 asks about a 64-bit Studio
                // 2026 — precisely the pairing this whole feature exists for.
                // Measured 2026-08-25: Studio 2024 swept Studio 2026's live
                // handshake at startup, so the second Studio became invisible to
                // the Connect dialog and to any client that had not already read
                // it. ProcessName and StartTime both work across that boundary;
                // only HasExited does not.
                var name = SafeProcessName(proc);

                if (string.IsNullOrEmpty(hs.ProcessName)) return true;

                // Name unreadable: we cannot prove it is dead, and the cost of
                // guessing wrong that way is deleting a live instance's
                // handshake. Guessing "alive" only risks a spurious refusal.
                if (string.IsNullOrEmpty(name)) return true;

                if (!string.Equals(name, hs.ProcessName, StringComparison.OrdinalIgnoreCase))
                    return false;

                return StartedBeforeHandshake(proc, hs.StartedAt);
            }
        }

        /// <summary>
        /// A live instance always started BEFORE it wrote its handshake — Studio
        /// launches, then the plugin initialises. So a process that started after
        /// the handshake was written cannot be the one that wrote it: Windows has
        /// reused the PID for another Studio. Without this, the name check alone
        /// passes (both are "SDLTradosStudio") and a dead session comes back as a
        /// phantom second instance, refusing writes that were never ambiguous.
        /// </summary>
        private static bool StartedBeforeHandshake(Process p, string startedAtUtc)
        {
            if (string.IsNullOrEmpty(startedAtUtc)) return true;
            try
            {
                if (!DateTime.TryParse(startedAtUtc, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var handshakeUtc))
                    return true;

                // Slack for clock adjustments between process start and handshake
                // write. Erring towards "alive" here costs a spurious refusal;
                // erring the other way disconnects a Studio that is working fine.
                return p.StartTime.ToUniversalTime() <= handshakeUtc.ToUniversalTime().AddSeconds(30);
            }
            catch
            {
                // StartTime is denied for some processes – fall back to trusting
                // the PID and name, which is what we did before this check.
                return true;
            }
        }

        // ── Cost instrumentation ─────────────────────────────────────────
        //
        // Every byte a tool returns lands in the AI's transcript, and a chat
        // client re-sends its whole transcript on every turn – so a single fat
        // result is billed again on every subsequent turn for the rest of the
        // conversation. We cannot see the AI's token counts, but we can measure
        // exactly what we emit, which is the half we control. session_report
        // turns "get_segments is probably the expensive one" into a number.

        private const string SessionReportPath = "/v1/session-report";

        /// <summary>Ledger key for a request: the bridge path, plus the "type"
        /// query value where one endpoint backs several tools (/v1/qa-check
        /// serves check_numbers, check_tags, check_nbsp and
        /// check_terminology).</summary>
        private static string LedgerKey(HttpListenerContext context)
        {
            try
            {
                var p = context.Request.Url.AbsolutePath;
                var type = QueryUtf8(context.Request)["type"];
                return string.IsNullOrEmpty(type) ? p : p + "?type=" + type;
            }
            catch { return "(unknown)"; }
        }

        /// <summary>Instrumentation must never break a request – all failures swallowed.</summary>
        private static void RecordRequestPayload(HttpListenerContext context)
        {
            try
            {
                var key = LedgerKey(context);
                // Polling the report must not pollute the report.
                if (key == SessionReportPath) return;
                var len = context.Request.ContentLength64;
                if (len > 0) BridgePayloadLedger.RecordRequest(key, len);
            }
            catch { }
        }

        private static void RecordResponsePayload(HttpListenerContext context, int byteCount)
        {
            try
            {
                var key = LedgerKey(context);
                if (key == SessionReportPath) return;
                BridgePayloadLedger.RecordResponse(key, byteCount);
            }
            catch { }
        }

        /// <summary>GET /v1/session-report – what the bridge has emitted this
        /// session, per endpoint, heaviest first. <c>reset=true</c> reports and
        /// then zeroes, so one conversation can be measured in isolation.</summary>
        private void HandleSessionReport(HttpListenerContext context)
        {
            bool reset = string.Equals(
                QueryUtf8(context.Request)["reset"], "true", StringComparison.OrdinalIgnoreCase);

            var snapshot = BridgePayloadLedger.Snapshot();

            var resp = new BridgeSessionReportResponse
            {
                Available = true,
                Since = BridgePayloadLedger.SinceUtc.ToString("o"),
                WasReset = reset,
                Endpoints = new List<BridgeSessionReportEntry>()
            };

            foreach (var e in snapshot)
            {
                resp.TotalCalls += e.Calls;
                resp.TotalResponseBytes += e.ResponseBytes;
                resp.TotalRequestBytes += e.RequestBytes;
                resp.Endpoints.Add(new BridgeSessionReportEntry
                {
                    Endpoint = e.Key,
                    Calls = e.Calls,
                    ResponseBytes = e.ResponseBytes,
                    AvgResponseBytes = e.Calls > 0
                        ? (long)Math.Round((double)e.ResponseBytes / e.Calls)
                        : 0,
                    MaxResponseBytes = e.MaxResponseBytes,
                    RequestBytes = e.RequestBytes,
                    EstTokens = (e.ResponseBytes + e.RequestBytes) / 4
                });
            }
            resp.EstTotalTokens = (resp.TotalResponseBytes + resp.TotalRequestBytes) / 4;

            resp.Note =
                "Bytes this bridge has moved since 'since' (Trados start, or the last reset) - " +
                "NOT the AI's token usage, which the plugin cannot see. estTokens is a rough " +
                "bytes/4 proxy. Remember each byte is billed again on every later turn of the " +
                "same conversation, so a big early result costs far more than its size suggests. " +
                "This endpoint excludes itself. /v1/tools is the one-off tool registry fetched at " +
                "startup, not a per-turn cost.";

            // Report first, then zero: the caller sees the window it asked about
            // and the next window starts clean.
            if (reset) BridgePayloadLedger.Reset();

            WriteJson(context, 200, resp);
        }

        // ── JSON helpers ─────────────────────────────────────────────────

        private static void WriteJson<T>(HttpListenerContext context, int statusCode, T payload)
        {
            try
            {
                var bytes = SerializeJson(payload);
                RecordResponsePayload(context, bytes.Length);
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
