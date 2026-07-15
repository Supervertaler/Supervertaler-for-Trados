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
        string? status,
        [Description("Only segments whose source or target contains this text (case-insensitive). Omit for all.")]
        string? contains,
        [Description("Maximum number of segments to return (default 200).")]
        int? limit,
        [Description("Number of matching segments to skip, for paging (default 0).")]
        int? offset,
        CancellationToken ct)
    {
        var query = BuildQuery(
            ("status", status),
            ("contains", contains),
            ("limit", limit?.ToString()),
            ("offset", offset?.ToString()));
        return Safe(() => bridge.GetAsync("/v1/segments" + query, ct));
    }

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
        int? limit,
        CancellationToken ct)
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
