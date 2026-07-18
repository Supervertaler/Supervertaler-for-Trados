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
    IReadOnlyDictionary<string, JsonNode?> FixedBody);

/// <summary>
/// Loads the tool registry the exe exposes. Source of truth is the plugin's
/// bridge (/v1/tools); we cache the last good copy to disk so tools are still
/// advertised when Trados is closed or on a fresh launch, and fall back to a
/// copy bundled in the exe on a first-ever run with no cache. This is what
/// lets new tools ship in a plugin update with no extension reinstall.
/// </summary>
public static class ToolRegistry
{
    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Supervertaler", "mcp-tools-cache.json");

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
                ToNodeMap(o["fixedBody"])));
        }
        return result;
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
