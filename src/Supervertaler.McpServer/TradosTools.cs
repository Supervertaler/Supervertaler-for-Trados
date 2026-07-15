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
                 "target text, confirmation status (e.g. Unspecified/Draft/Translated) and locked flag. " +
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
     Description("Search the user's Supervertaler translation memories for matches to a source-language " +
                 "text. Returns previous translations with similarity scores. Use this before proposing a " +
                 "translation so your suggestion is grounded in how the user actually translated similar " +
                 "text before.")]
    public static Task<string> SearchTm(
        BridgeClient bridge,
        [Description("The source-language text to find TM matches for (typically one segment or sentence).")]
        string text,
        [Description("Maximum number of matches to return (default 5).")]
        int? limit = null,
        CancellationToken ct = default)
        => Safe(() => bridge.GetAsync("/v1/tm-search" + BuildQuery(("q", text), ("limit", limit?.ToString())), ct));

    [McpServerTool(Name = "lookup_term"),
     Description("Look up a term in the user's termbases (Supervertaler termbases and attached MultiTerm " +
                 "termbases). Returns source term, target term, and flags such as non-translatable or " +
                 "project-specific. The user's termbase is authoritative for terminology – always follow " +
                 "it over your own preference.")]
    public static Task<string> LookupTerm(
        BridgeClient bridge,
        [Description("The term to look up (source or target language).")]
        string term,
        CancellationToken ct)
        => Safe(() => bridge.GetAsync("/v1/term-lookup" + BuildQuery(("q", term)), ct));

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
     Description("Add a source/target term pair to the user's configured Write termbases (same as " +
                 "Supervertaler's quick-add). Reports which termbases the term was added to, or an error " +
                 "if it already exists. Only use when the user asked for a term to be added, or explicitly " +
                 "agreed to your suggestion to add one.")]
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
