using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Supervertaler.McpServer;

/// <summary>
/// MCP tools exposed to the AI app. Each tool forwards to an endpoint on the
/// Supervertaler for Trados bridge and returns the bridge's JSON verbatim –
/// the plugin is the single source of truth for shapes and field names.
/// Tool descriptions are written for the model: they say when to use the
/// tool and what comes back, in plain language.
/// </summary>
[McpServerToolType]
public static class TradosTools
{
    [McpServerTool(Name = "get_active_project"),
     Description("Get the Trados Studio project currently open in the editor: project name, source and " +
                 "target language, the document/file being edited, and progress statistics (segment counts " +
                 "per confirmation status, word counts). Call this first to orient yourself.")]
    public static Task<string> GetActiveProject(BridgeClient bridge, CancellationToken ct)
        => Safe(() => bridge.GetAsync("/v1/project", ct));

    [McpServerTool(Name = "get_segments"),
     Description("List segments of the document open in the Trados editor. Returns segment id, source text, " +
                 "target text, confirmation status (e.g. Unspecified/Draft/Translated), locked flag, and " +
                 "'number' – the segment number the user sees in Studio's grid. ALWAYS cite that number " +
                 "(plus the file name in merged documents, where numbers restart per file) when referring " +
                 "to segments in conversation – never invent numbers. " +
                 "Inline formatting appears as tags like <t1>…</t1> or <b>…</b> – preserve them in any " +
                 "translation you propose. Use the filters to fetch only what you need instead of the whole " +
                 "document. Results are paged via limit/offset; the response says whether it was truncated.")]
    public static Task<string> GetSegments(
        BridgeClient bridge,
        [Description("Only segments with this confirmation status: Unspecified, Draft, Translated, " +
                     "RejectedTranslation, ApprovedTranslation, RejectedSignOff or ApprovedSignOff. Omit for all.")]
        string? status = null,
        [Description("Only segments whose source or target contains this text (case-insensitive). Omit for all.")]
        string? contains = null,
        [Description("Maximum number of segments to return (default 200).")]
        int? limit = null,
        [Description("Number of matching segments to skip, for paging (default 0).")]
        int? offset = null,
        [Description("Only segments from this file of a merged multi-file document – a file id or " +
                     "(partial) file name from get_files. Omit for all files.")]
        string? file = null,
        CancellationToken ct = default)
    {
        var query = BuildQuery(
            ("status", status),
            ("contains", contains),
            ("limit", limit?.ToString()),
            ("offset", offset?.ToString()),
            ("file", file));
        return Safe(() => bridge.GetAsync("/v1/segments" + query, ct));
    }

    [McpServerTool(Name = "get_files"),
     Description("List the files of the document open in the Trados editor: file id, name, segment count, " +
                 "and which one is active. Normal documents have one file; merged documents have several. " +
                 "Use the names/ids with get_segments' 'file' filter to work per file.")]
    public static Task<string> GetFiles(BridgeClient bridge, CancellationToken ct = default)
        => Safe(() => bridge.GetAsync("/v1/files", ct));

    [McpServerTool(Name = "get_project_statistics"),
     Description("Get the project's analysis statistics (per language direction: perfect/exact/fuzzy/new/" +
                 "repetition words and segments) and per-file confirmation statistics (not started, draft, " +
                 "translated, approved, signed off) from the project file on disk. Use this for questions " +
                 "about word counts, progress, or how much work is left. Defaults to the project open in " +
                 "the editor.")]
    public static Task<string> GetProjectStatistics(
        BridgeClient bridge,
        [Description("Project name. Omit to use the project open in the Trados editor.")]
        string? projectName = null,
        CancellationToken ct = default)
        => Safe(() => bridge.GetAsync("/v1/statistics" + BuildQuery(("project", projectName)), ct));

    [McpServerTool(Name = "find_inconsistencies"),
     Description("Find translation inconsistencies in the document open in the Trados editor: repeated " +
                 "source segments (compared tag-stripped) whose translations differ. Returns each " +
                 "inconsistent group with its source text and every occurrence (segment id, target, " +
                 "status, file). Pair with update_segments to align occurrences after the user chooses " +
                 "the preferred translation.")]
    public static Task<string> FindInconsistencies(
        BridgeClient bridge,
        [Description("Maximum number of inconsistent groups to return (default 50).")]
        int? limit = null,
        CancellationToken ct = default)
        => Safe(() => bridge.GetAsync("/v1/inconsistencies" + BuildQuery(("limit", limit?.ToString())), ct));

    [McpServerTool(Name = "get_active_segment"),
     Description("Get the segment the translator is working on right now, with its surrounding segments, " +
                 "TM matches and termbase hits for it, and project metadata. Use this when the user says " +
                 "'this segment' or asks about what they are currently editing.")]
    public static Task<string> GetActiveSegment(BridgeClient bridge, CancellationToken ct)
        => Safe(() => bridge.GetAsync("/v1/active-context", ct));

    [McpServerTool(Name = "search_tm"),
     Description("Search the user's *Supervertaler* translation memories (the ones bridged from Supervertaler " +
                 "Workbench) for matches to a text. For the native Trados TMs attached to the project – which " +
                 "is what most users work with – use search_studio_tm instead, or call both. Returns previous " +
                 "translations with scores; use before proposing a translation to ground it in the user's past work.")]
    public static Task<string> SearchTm(
        BridgeClient bridge,
        [Description("The text to find TM matches for (typically one segment or sentence).")]
        string text,
        [Description("Maximum number of matches to return (default 5).")]
        int? limit = null,
        CancellationToken ct = default)
        => Safe(() => bridge.GetAsync("/v1/tm-search" + BuildQuery(("q", text), ("limit", limit?.ToString())), ct));

    [McpServerTool(Name = "search_studio_tm"),
     Description("Concordance-search the native Trados translation memories attached to the open project – " +
                 "the .sdltm files and GroupShare server TMs the user actually translates against (the same " +
                 "TMs as Supervertaler's SuperSearch). Returns previous source/target pairs with match scores " +
                 "and the TM name. This is usually the right tool for \"how did I translate this before?\"; " +
                 "search_tm only covers the separate Supervertaler-bridged TMs.")]
    public static Task<string> SearchStudioTm(
        BridgeClient bridge,
        [Description("The text to search for (a word or phrase from the segment).")]
        string text,
        [Description("Which side to search: \"source\", \"target\", or \"both\" (default).")]
        string? searchIn = null,
        [Description("Maximum number of matches to return (default 10).")]
        int? limit = null,
        CancellationToken ct = default)
        => Safe(() => bridge.GetAsync("/v1/studio-tm-search" +
            BuildQuery(("q", text), ("in", searchIn), ("limit", limit?.ToString())), ct));

    [McpServerTool(Name = "lookup_term"),
     Description("Look up a term across ALL the user's termbases: Supervertaler termbases plus the " +
                 "Trados termbases attached to the open project (.ttb in Studio 2026, MultiTerm .sdltb " +
                 "in Studio 2024 – hits from those are marked [Trados project termbase]). Exact match " +
                 "first, substring fallback. The user's termbase is authoritative for terminology – " +
                 "always follow it over your own preference.")]
    public static Task<string> LookupTerm(
        BridgeClient bridge,
        [Description("The term to look up (source or target language).")]
        string term,
        CancellationToken ct)
        => Safe(() => bridge.GetAsync("/v1/term-lookup" + BuildQuery(("q", term)), ct));

    [McpServerTool(Name = "check_numbers"),
     Description("QA check: find translated segments where the numbers in source and target don't match " +
                 "(digits compared with thousand/decimal separators normalised, so 1.234,56 matches " +
                 "1,234.56). Returns each mismatch with the segment's numbers listed. Only translated " +
                 "segments are checked. Chain with update_segments after the user approves fixes.")]
    public static Task<string> CheckNumbers(
        BridgeClient bridge,
        [Description("Maximum issues to return (default 50).")]
        int? limit = null,
        CancellationToken ct = default)
        => Safe(() => bridge.GetAsync("/v1/qa-check" + BuildQuery(("type", "numbers"), ("limit", limit?.ToString())), ct));

    [McpServerTool(Name = "check_tags"),
     Description("QA check: find translated segments whose inline tag count differs between source and " +
                 "target (missing or extra formatting/placeholder tags). A difference is not always an " +
                 "error – formatting can legitimately differ – so present findings for review rather than " +
                 "auto-fixing.")]
    public static Task<string> CheckTags(
        BridgeClient bridge,
        [Description("Maximum issues to return (default 50).")]
        int? limit = null,
        CancellationToken ct = default)
        => Safe(() => bridge.GetAsync("/v1/qa-check" + BuildQuery(("type", "tags"), ("limit", limit?.ToString())), ct));

    [McpServerTool(Name = "check_terminology"),
     Description("QA check: find termbase terms that appear in source segments whose targets don't use the " +
                 "expected translation (or any synonym). Covers Supervertaler termbases AND the Trados project's " +
                 "own termbases (.ttb/MultiTerm). Results are GROUPED PER TERM, most-affected first: " +
                 "each group has the term, its termbase, the expected translations, how many segments are " +
                 "affected, and sample segment ids. A term affecting many segments usually means the project " +
                 "consistently uses a different translation than the termbase – help the user decide which " +
                 "is right (update_segments to align the project, or add_term/edit the termbase) rather than " +
                 "auto-fixing. Substring-based, so inflected target forms can be false positives.")]
    public static Task<string> CheckTerminology(
        BridgeClient bridge,
        [Description("Maximum term groups to return (default 50).")]
        int? limit = null,
        CancellationToken ct = default)
        => Safe(() => bridge.GetAsync("/v1/qa-check" + BuildQuery(("type", "terminology"), ("limit", limit?.ToString())), ct));

    [McpServerTool(Name = "list_resources"),
     Description("List the translation resources available: the Trados TMs attached to the open project " +
                 "(file-based and GroupShare server TMs), the Supervertaler bridged TMs, and ALL termbases " +
                 "- Supervertaler ones (kind: supervertaler, writable if flagged) and the Trados project's " +
                 "own (kind: trados-ttb or multiterm, always read-only). Useful for orientation before " +
                 "searching, and to answer 'what TMs/termbases am I using?'.")]
    public static Task<string> ListResources(BridgeClient bridge, CancellationToken ct = default)
        => Safe(() => bridge.GetAsync("/v1/resources", ct));

    [McpServerTool(Name = "go_to_segment"),
     Description("Move Trados Studio's editor to a specific segment, so the user sees the segment you are " +
                 "talking about. Address it by the full id from get_segments, OR by the segment number the " +
                 "user sees in Studio's grid (plus the file name in merged multi-file documents, since " +
                 "numbers restart per file). Use this after flagging or editing a segment.")]
    public static Task<string> GoToSegment(
        BridgeClient bridge,
        [Description("Full segment id (\"<paragraphUnitId>:<segmentId>\") from get_segments. Alternative to number.")]
        string? id = null,
        [Description("Segment number as displayed in Studio's grid. Alternative to id.")]
        string? number = null,
        [Description("File name or id (from get_files) – required with 'number' in merged documents.")]
        string? file = null,
        CancellationToken ct = default)
        => Safe(() => bridge.PostAsync("/v1/go-to-segment", new { id, number, file }, ct));

    [McpServerTool(Name = "get_comments"),
     Description("Read the Trados comments on a segment (author, date, severity, text), in a stable order " +
                 "whose index update_comment uses. Call before updating a comment.")]
    public static Task<string> GetComments(
        BridgeClient bridge,
        [Description("Segment id (\"<paragraphUnitId>:<segmentId>\") from get_segments.")]
        string id,
        CancellationToken ct = default)
        => Safe(() => bridge.GetAsync("/v1/comments" + BuildQuery(("id", id)), ct));

    [McpServerTool(Name = "add_comment"),
     Description("Add a Trados Studio comment to a segment (same as the editor's Add Comment command) – " +
                 "e.g. to flag a source issue for the client or leave a review note. Becomes part of the " +
                 "document's unsaved changes. Only add comments the user asked for or agreed to.")]
    public static Task<string> AddComment(
        BridgeClient bridge,
        [Description("Segment id (\"<paragraphUnitId>:<segmentId>\") from get_segments.")]
        string id,
        [Description("The comment text.")]
        string text,
        [Description("Severity: Low (informational, default), Medium (warning), or High (error).")]
        string? severity = null,
        CancellationToken ct = default)
        => Safe(() => bridge.PostAsync("/v1/add-comment", new { id, text, severity }, ct));

    [McpServerTool(Name = "update_comment"),
     Description("Replace the text of an existing Trados comment on a segment – e.g. when a fix you applied " +
                 "makes the old comment text outdated. Address the comment by the index from get_comments " +
                 "(call it first). Only update comments the user asked you to change.")]
    public static Task<string> UpdateComment(
        BridgeClient bridge,
        [Description("Segment id (\"<paragraphUnitId>:<segmentId>\") from get_segments.")]
        string id,
        [Description("The comment's index from get_comments.")]
        int commentIndex,
        [Description("The replacement comment text.")]
        string text,
        CancellationToken ct = default)
        => Safe(() => bridge.PostAsync("/v1/update-comment", new { id, commentIndex, text }, ct));

    [McpServerTool(Name = "find_and_replace"),
     Description("Find and replace text in the TARGET (translation) side of segments across the open Trados " +
                 "document. Options: caseSensitive, wholeWord, regex, and filters by file (merged documents) " +
                 "or confirmation status. STRONGLY RECOMMENDED workflow: first call with dryRun=true to " +
                 "preview which segments would change (returns before/after), show the user, then call again " +
                 "with dryRun=false to apply. Matches that straddle inline tags are skipped for safety and " +
                 "reported. Locked segments are skipped. Changes land in the open document; the user saves in " +
                 "Studio. Only replace when the user asked for it.")]
    public static Task<string> FindAndReplace(
        BridgeClient bridge,
        [Description("The text (or regex pattern) to find in target segments.")]
        string find,
        [Description("The replacement text (empty string deletes the found text).")]
        string replace,
        [Description("Preview only – report what would change without writing. Do this first for anything non-trivial.")]
        bool dryRun = false,
        [Description("Match case. Default false.")]
        bool caseSensitive = false,
        [Description("Match whole words only. Default false.")]
        bool wholeWord = false,
        [Description("Treat 'find' as a .NET regular expression (and 'replace' may use $1 etc.). Default false.")]
        bool regex = false,
        [Description("Restrict to one file of a merged document (id or partial name from get_files). Omit for all.")]
        string? file = null,
        [Description("Restrict to segments with this confirmation status (e.g. Draft, Translated). Omit for all.")]
        string? status = null,
        CancellationToken ct = default)
        => Safe(() => bridge.PostAsync("/v1/find-replace", new
        {
            find, replace, dryRun, caseSensitive, wholeWord, regex, file, status
        }, ct));

    [McpServerTool(Name = "run_verification"),
     Description("Run Trados Studio's OWN QA verification (Verify Files / F8 – QA Checker 3.0, tag and term " +
                 "verifiers: punctuation, brackets, repeated words, spelling, regex rules, length checks, etc.) " +
                 "and return the findings, each with file, segment number, severity, and message. This catches " +
                 "things the check_* tools don't (punctuation, spelling, regex) and complements them. IMPORTANT: " +
                 "it reads the LAST SAVED state of the files – if the user made edits (including ones you applied " +
                 "with update_segments), tell them to save in Studio first, then run again. Triage each finding " +
                 "against the source before proposing fixes; some are false positives.")]
    public static Task<string> RunVerification(BridgeClient bridge, CancellationToken ct = default)
        => Safe(() => bridge.PostAsync("/v1/verify", new { }, ct));

    [McpServerTool(Name = "insert_into_active_segment"),
     Description("Insert text into the target side of the segment the translator is currently editing in " +
                 "Trados Studio (same as Supervertaler's Apply-to-target button). Replaces the current " +
                 "target content of that one segment. Only use when the user asked you to apply or insert " +
                 "a translation.")]
    public static Task<string> InsertIntoActiveSegment(
        BridgeClient bridge,
        [Description("The target-language text to insert. Preserve inline tags from the source where appropriate.")]
        string text,
        CancellationToken ct)
        => Safe(() => bridge.PostAsync("/v1/insert-translation", new { text }, ct));

    [McpServerTool(Name = "update_segments"),
     Description("Write translations and/or set confirmation statuses for segments in the document open " +
                 "in Trados Studio, addressed by the ids returned by get_segments. Target text may reuse " +
                 "the inline tag markers from the source (<t1>…</t1>, <b>…</b>) – preserve them. A target " +
                 "write without an explicit status is set to Draft automatically so the user can review " +
                 "your work in Studio. Locked segments are refused. Maximum 200 updates per call – page " +
                 "larger jobs. Only use when the user asked you to change segments, and afterwards tell " +
                 "them exactly what you changed. Changes land in the open document; the user still needs " +
                 "to save it in Studio.")]
    public static Task<string> UpdateSegments(
        BridgeClient bridge,
        [Description("Segments to update. Each item: id (required, from get_segments), target (optional " +
                     "new target text), status (optional: Unspecified, Draft, Translated, " +
                     "RejectedTranslation, ApprovedTranslation, RejectedSignOff, ApprovedSignOff). " +
                     "Provide target, status, or both per item.")]
        IList<SegmentUpdate> updates,
        CancellationToken ct = default)
        => Safe(() => bridge.PostAsync("/v1/update-segments", new
        {
            updates = updates.Select(u => new { id = u.Id, target = u.Target, status = u.Status })
        }, ct));

    [McpServerTool(Name = "add_term"),
     Description("Add a source/target term pair to the user's configured Supervertaler Write termbases " +
                 "(same as Supervertaler's quick-add). Reports which termbases received the term, or an " +
                 "error if it already exists. NOTE: Trados's own project termbases (.ttb / MultiTerm) are " +
                 "READ-ONLY for you – if the user wants a term added there, tell them to add it in Trados " +
                 "itself. Only use when the user asked for a term to be added, or explicitly agreed to " +
                 "your suggestion to add one.")]
    public static Task<string> AddTerm(
        BridgeClient bridge,
        [Description("The source-language term.")]
        string source,
        [Description("The target-language term.")]
        string target,
        CancellationToken ct = default)
        => Safe(() => bridge.PostAsync("/v1/add-term", new { source, target }, ct));

    private static string BuildQuery(params (string Key, string? Value)[] parts)
    {
        var kept = parts.Where(p => !string.IsNullOrEmpty(p.Value))
                        .Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}")
                        .ToList();
        return kept.Count == 0 ? "" : "?" + string.Join("&", kept);
    }

    /// <summary>
    /// The MCP SDK replaces thrown exceptions with a generic "an error occurred"
    /// message, which hides the actionable guidance in BridgeUnavailableException
    /// ("start Trados Studio", "enable the bridge", …). So errors are returned
    /// as tool *output* the model can read and relay to the user.
    /// </summary>
    private static async Task<string> Safe(Func<Task<string>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (ex is BridgeUnavailableException or HttpRequestException or TaskCanceledException)
        {
            return JsonSerializer.Serialize(new { ok = false, error = ex.Message });
        }
    }
}
