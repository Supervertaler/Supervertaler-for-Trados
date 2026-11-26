using System.Text.Json;
using System.Text.Json.Nodes;

namespace Supervertaler.McpServer;

/// <summary>One tool as defined by the bridge's /v1/tools registry: the MCP
/// surface (name/description/schema) plus how to forward a call to the bridge.</summary>
public sealed record ToolDef(
    string Name,
    string Description,
    JsonElement InputSchema,
    string Method,                 // "GET" or "POST"
    string Path,                   // bridge path, e.g. /v1/segments
    IReadOnlyDictionary<string, string> ParamMap,   // mcp arg name -> bridge param name
    IReadOnlyDictionary<string, JsonNode?> FixedQuery,
    IReadOnlyDictionary<string, JsonNode?> FixedBody,
    ToolAccess Access,
    IReadOnlyList<string> WriteWhen)
{
    /// <summary>
    /// Whether THIS call writes, given its arguments.
    ///
    /// Deliberately not derived from <see cref="Method"/>: GET/POST nearly
    /// matches read/write across the registry but not quite —
    /// <c>get_tracked_changes(save=true)</c> is a GET that persists revision
    /// pairs into a memory bank, and serving that from the wrong Studio writes
    /// one project's revisions into another project's bank.
    /// </summary>
    public bool IsWrite(IReadOnlyDictionary<string, JsonElement> args) => Access switch
    {
        ToolAccess.Read => false,
        // An empty writeWhen makes the condition unanswerable, so it falls back
        // to the safe reading rather than silently becoming a read.
        ToolAccess.Conditional => WriteWhen.Count == 0 || WriteWhen.Any(p => IsTruthy(args, p)),
        _ => true,
    };

    private static bool IsTruthy(IReadOnlyDictionary<string, JsonElement> args, string name)
    {
        if (!args.TryGetValue(name, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.String => bool.TryParse(v.GetString(), out var b) && b,
            JsonValueKind.Number => v.TryGetDouble(out var d) && d != 0,
            _ => true,
        };
    }
}

/// <summary>Whether a tool changes anything, which decides what happens when
/// more than one Studio instance is live (issue #72).</summary>
public enum ToolAccess
{
    /// <summary>Changes nothing. Served under ambiguity, with a warning.</summary>
    Read,
    /// <summary>Changes something. Refused under ambiguity.</summary>
    Write,
    /// <summary>A write only when one of <see cref="ToolDef.WriteWhen"/> is set truthy.</summary>
    Conditional
}

/// <summary>
/// Loads the tool registry the exe exposes. Source of truth is the plugin's
/// bridge (/v1/tools); we cache the last good copy to disk so tools are still
/// advertised when Trados is closed or on a fresh launch, and fall back to a
/// copy bundled in the exe on a first-ever run with no cache. This is what
/// lets new tools ship in a plugin update with no extension reinstall.
/// </summary>
public static class ToolRegistry
{
    // Keyed by the SUPERVERTALER_BRIDGE_FILE override when one is set: the same
    // exe can be configured twice in Claude Desktop, once per host (Trados, and
    // the memoQ plugin's bridge), and those bridges serve different registries.
    // With one shared cache file, whichever host was closed would advertise the
    // other host's tools. The default (no override) keeps the historical path.
    private static readonly string CachePath = BuildCachePath();

    private static string BuildCachePath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Supervertaler");

        // One cache per (host, pinned handshake): the memoQ and Trados bridges
        // serve different registries, and two server entries sharing one file
        // would advertise each other's tools whenever one CAT tool is closed.
        var overridePath = Environment.GetEnvironmentVariable("SUPERVERTALER_BRIDGE_FILE");
        var key = BridgeClient.Host + "|" + (overridePath ?? "");
        if (key == "trados|")
            return Path.Combine(dir, "mcp-tools-cache.json");

        uint hash = 2166136261;
        foreach (var ch in key.ToLowerInvariant())
            hash = (hash ^ ch) * 16777619;

        return Path.Combine(dir, $"mcp-tools-cache-{hash:x8}.json");
    }

    /// <summary>Fetch from the bridge if reachable (and refresh the cache);
    /// otherwise use the disk cache; otherwise the bundled fallback. Never
    /// throws – returns an empty list only if all three fail.</summary>
    public static async Task<List<ToolDef>> LoadAsync(BridgeClient bridge, CancellationToken ct = default)
    {
        // 1. Live from the bridge.
        try
        {
            var json = await bridge.GetAsync("/v1/tools", ct);
            var tools = Parse(json);
            if (tools.Count > 0)
            {
                TrySaveCache(json);
                return tools;
            }
        }
        catch { /* bridge down or Trados closed – fall through */ }

        // 2. Disk cache (last good copy).
        try
        {
            if (File.Exists(CachePath))
            {
                var tools = Parse(File.ReadAllText(CachePath));
                if (tools.Count > 0) return tools;
            }
        }
        catch { }

        // 3. Bundled fallback (embedded at build time).
        // The bundled fallback is the Trados registry. Advertising it for a
        // memoQ host would offer fifty tools of which a third do not exist
        // there. But offering NONE left the chat with no memoQ tools at all,
        // and nothing to tell the user why: a client that ignores
        // tools/list_changed stays that way for the whole session. So memoQ
        // gets the two tools that are always there and need no capture -
        // help and get_project - which, with memoQ closed, return the message
        // saying how to start it, and with memoQ open, work. The poller below
        // replaces them with the full list as soon as the bridge answers.
        if (BridgeClient.IsMemoQ) return MemoQStarterTools();
        // Workbench: shorter list still, no field report yet; left as it was.
        if (BridgeClient.IsWorkbench) return new List<ToolDef>();

        try
        {
            var asm = typeof(ToolRegistry).Assembly;
            var resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("mcp-tools.json", StringComparison.OrdinalIgnoreCase));
            if (resName != null)
            {
                using var s = asm.GetManifestResourceStream(resName)!;
                using var r = new StreamReader(s);
                return Parse(r.ReadToEnd());
            }
        }
        catch { }

        return new List<ToolDef>();
    }

    /// <summary>
    /// The memoQ registry's help and get_project, as the memoQ bridge serves them
    /// (MemoQBridge's /v1/tools). Copied rather than embedded: the memoQ registry
    /// lives in the other repository, and these two are its oldest and most
    /// stable entries. A drift in their wording is harmless - the live list
    /// replaces these the moment memoQ answers.
    /// </summary>
    internal static List<ToolDef> MemoQStarterTools()
    {
        var noArgs = JsonSerializer.Deserialize<JsonElement>("{\"type\":\"object\",\"properties\":{}}");
        var none = new Dictionary<string, string>();
        var noNodes = new Dictionary<string, JsonNode?>();
        return new List<ToolDef>
        {
            new("help",
                "Show the user a curated menu of what they can ask this memoQ assistant to do. ALWAYS call this " +
                "when the user asks what they can do or asks for help, then present the returned card verbatim. " +
                "If memoQ is not reachable, the answer says how to connect it.",
                noArgs, "GET", "/v1/help", none, noNodes, noNodes, ToolAccess.Read, Array.Empty<string>()),
            new("get_project",
                "What the plugin knows about the open memoQ project: language pair, documents, segment counts and " +
                "staged translations. Call this first for any question about the user's memoQ project. If memoQ " +
                "is not reachable, the answer says what to do; once it is, the full set of memoQ tools appears.",
                noArgs, "GET", "/v1/project", none, noNodes, noNodes, ToolAccess.Read, Array.Empty<string>()),
        };
    }

    private static void TrySaveCache(string json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, json);
        }
        catch { }
    }

    private static List<ToolDef> Parse(string json)
    {
        var result = new List<ToolDef>();
        var root = JsonNode.Parse(json);
        var arr = root?["tools"]?.AsArray();
        if (arr == null) return result;

        foreach (var node in arr)
        {
            if (node is not JsonObject o) continue;
            var name = o["name"]?.GetValue<string>();
            if (string.IsNullOrEmpty(name)) continue;

            var schema = o["inputSchema"] is JsonNode sn
                ? JsonSerializer.Deserialize<JsonElement>(sn.ToJsonString())
                : JsonSerializer.Deserialize<JsonElement>("{\"type\":\"object\",\"properties\":{}}");

            result.Add(new ToolDef(
                name!,
                o["description"]?.GetValue<string>() ?? "",
                schema,
                (o["method"]?.GetValue<string>() ?? "GET").ToUpperInvariant(),
                o["path"]?.GetValue<string>() ?? "",
                ToStringMap(o["paramMap"]),
                ToNodeMap(o["fixedQuery"]),
                ToNodeMap(o["fixedBody"]),
                ParseAccess(o["access"]),
                ToStringList(o["writeWhen"])));
        }
        return result;
    }

    /// <summary>
    /// Fail closed. A registry with no "access" fields is a real possibility —
    /// the disk cache and the bundled fallback can both predate the annotation —
    /// and the safe reading of "unknown" is "write". The cost is an over-strict
    /// refusal while two Studios are live, until the cache refreshes from a live
    /// bridge; the alternative is an unannotated update_segments waved through
    /// as a read into whichever project happened to start last.
    /// </summary>
    private static ToolAccess ParseAccess(JsonNode? n)
    {
        var s = n?.GetValue<string>();
        return s?.ToLowerInvariant() switch
        {
            "read" => ToolAccess.Read,
            "conditional" => ToolAccess.Conditional,
            _ => ToolAccess.Write,
        };
    }

    private static IReadOnlyList<string> ToStringList(JsonNode? n)
    {
        var list = new List<string>();
        if (n is JsonArray a)
            foreach (var item in a)
                if (item != null) list.Add(item.GetValue<string>());
        return list;
    }

    private static IReadOnlyDictionary<string, string> ToStringMap(JsonNode? n)
    {
        var d = new Dictionary<string, string>();
        if (n is JsonObject o)
            foreach (var kv in o)
                if (kv.Value != null) d[kv.Key] = kv.Value.GetValue<string>();
        return d;
    }

    private static IReadOnlyDictionary<string, JsonNode?> ToNodeMap(JsonNode? n)
    {
        var d = new Dictionary<string, JsonNode?>();
        if (n is JsonObject o)
            foreach (var kv in o)
                d[kv.Key] = kv.Value?.DeepClone();
        return d;
    }
}
