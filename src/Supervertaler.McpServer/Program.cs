using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Supervertaler.McpServer;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// Supervertaler MCP Server – stdio MCP server that fronts the localhost HTTP
// bridge hosted by Supervertaler for Trados inside Trados Studio.
//
// The tool list is NOT hard-coded here. It is fetched from the bridge's
// /v1/tools registry (with a disk cache + a bundled fallback), so new tools
// ship in a plugin update with no extension reinstall. This exe is a generic
// forwarder: it advertises whatever the registry says, and forwards each call
// to the bridge path the registry maps it to.
//
// IMPORTANT: stdout belongs to the MCP protocol – all logging goes to stderr.

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

var bridge = new BridgeClient();

// Lazily loaded, refreshed on each tools/list so a plugin update (new tools)
// is picked up on the next Claude Desktop connection – no reinstall.
List<ToolDef> tools = new();
var loadLock = new SemaphoreSlim(1, 1);

async Task<List<ToolDef>> GetToolsAsync(CancellationToken ct, bool forceRefresh = false)
{
    await loadLock.WaitAsync(ct);
    try
    {
        if (forceRefresh || tools.Count == 0)
            tools = await ToolRegistry.LoadAsync(bridge, ct);
        return tools;
    }
    finally { loadLock.Release(); }
}

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithListToolsHandler(async (ctx, ct) =>
    {
        var defs = await GetToolsAsync(ct, forceRefresh: true);
        return new ListToolsResult
        {
            Tools = defs.Select(d => new Tool
            {
                Name = d.Name,
                Description = d.Description,
                InputSchema = d.InputSchema,
            }).ToList()
        };
    })
    .WithCallToolHandler(async (ctx, ct) =>
    {
        var name = ctx.Params?.Name ?? "";
        var defs = await GetToolsAsync(ct);
        var def = defs.FirstOrDefault(d => d.Name == name);
        if (def == null)
            return TextResult($"{{\"ok\":false,\"error\":\"unknown tool '{name}'\"}}", isError: true);

        var args = ctx.Params?.Arguments ?? new Dictionary<string, JsonElement>();
        try
        {
            string result = def.Method == "POST"
                ? await bridge.PostAsync(def.Path, BuildBody(def, args), ct)
                : await bridge.GetAsync(def.Path + BuildQuery(def, args), ct);
            return TextResult(result);
        }
        catch (Exception ex) when (ex is BridgeUnavailableException or HttpRequestException or TaskCanceledException)
        {
            // Return the actionable message as tool output (the SDK hides thrown text).
            return TextResult(JsonSerializer.Serialize(new { ok = false, error = ex.Message }), isError: false);
        }
    });

await builder.Build().RunAsync();
return;

// ── helpers ─────────────────────────────────────────────────────────────

static CallToolResult TextResult(string text, bool isError = false) => new()
{
    IsError = isError,
    Content = new List<ContentBlock> { new TextContentBlock { Text = text } }
};

static string BuildQuery(ToolDef def, IReadOnlyDictionary<string, JsonElement> args)
{
    var parts = new List<string>();
    foreach (var kv in def.FixedQuery)
        if (kv.Value != null) parts.Add(Enc(kv.Key, kv.Value.ToJsonString().Trim('"')));
    foreach (var kv in args)
    {
        var bridgeName = def.ParamMap.TryGetValue(kv.Key, out var mapped) ? mapped : kv.Key;
        var val = ScalarToString(kv.Value);
        if (val != null) parts.Add(Enc(bridgeName, val));
    }
    return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
}

static object BuildBody(ToolDef def, IReadOnlyDictionary<string, JsonElement> args)
{
    var body = new JsonObject();
    foreach (var kv in def.FixedBody)
        body[kv.Key] = kv.Value?.DeepClone();
    foreach (var kv in args)
    {
        var bridgeName = def.ParamMap.TryGetValue(kv.Key, out var mapped) ? mapped : kv.Key;
        body[bridgeName] = JsonNode.Parse(kv.Value.GetRawText());
    }
    return body;
}

static string Enc(string k, string v) => $"{Uri.EscapeDataString(k)}={Uri.EscapeDataString(v)}";

static string? ScalarToString(JsonElement e) => e.ValueKind switch
{
    JsonValueKind.String => e.GetString(),
    JsonValueKind.Number => e.GetRawText(),
    JsonValueKind.True => "true",
    JsonValueKind.False => "false",
    JsonValueKind.Null or JsonValueKind.Undefined => null,
    _ => e.GetRawText(),   // arrays/objects rarely used as query args
};
