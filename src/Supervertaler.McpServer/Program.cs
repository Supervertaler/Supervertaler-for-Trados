using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
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

// --instance <selector> pins this server to one Studio, for a client whose config
// format has no env block. ChatGptMcpSetup writes `args`, so it can set this.
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] is "--instance" or "-i")
    {
        bridge.CommandLineSelector = args[i + 1];
        break;
    }
}

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
        var tools = defs.Select(d => new Tool
        {
            Name = d.Name,
            Description = d.Description,
            // #121: EVERY tool takes an optional `instance` so a chat can say
            // which Studio it means on every call - reads too. Reads were left out
            // at first, on the reasoning that a read "reports where it came from";
            // but a read with no instance follows the ONE selection shared by every
            // chat, so a chat that has declared its Studio still got the other
            // chat's project back, and models then fought over select_trados_instance
            // to fix it. With instance on every call the shared selection is never
            // needed and never touched.
            InputSchema = WithInstanceParam(d.InputSchema),
        }).ToList();

        // The instance tools are the exe's own – no bridge can answer "which
        // bridge?" – so they are appended rather than coming from the registry.
        tools.AddRange(LocalTools.Definitions().Select(d => new Tool
        {
            Name = d.Name,
            Description = d.Description,
            InputSchema = d.Schema,
        }));

        return new ListToolsResult { Tools = tools };
    })
    .WithCallToolHandler(async (ctx, ct) =>
    {
        var name = ctx.Params?.Name ?? "";
        var callArgs = ctx.Params?.Arguments ?? new Dictionary<string, JsonElement>();

        // Answered here, not forwarded: these decide which bridge everything else
        // goes to, so they must work even while the choice is ambiguous.
        if (LocalTools.Handles(name))
        {
            try { return TextResult(LocalTools.Invoke(name, callArgs, bridge)); }
            catch (Exception ex)
            {
                return TextResult(JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
            }
        }

        var defs = await GetToolsAsync(ct);
        var def = defs.FirstOrDefault(d => d.Name == name);
        if (def == null)
            return TextResult($"{{\"ok\":false,\"error\":\"unknown tool '{name}'\"}}", isError: true);

        var args = callArgs;
        try
        {
            // Which Studio are we talking to? With two open (e.g. Studio 2024 and
            // 2026 side by side) this is genuinely ambiguous, and the two halves
            // of that are not equally dangerous: a read from the wrong instance is
            // misleading, a write into the wrong instance destroys work. So writes
            // are refused until an instance is selected, and reads go through with
            // a warning naming the instance they came from. See issue #72.
            var selection = bridge.Resolve();

            // isError:false to match the bridge-unavailable path below: the whole
            // value of this refusal is the AI reading it and asking the user which
            // Studio they meant, so it goes back as ordinary tool output.
            // #121. The selection above is one setting shared by every chat on this
            // machine - the MCP protocol gives a server no idea which conversation
            // is calling - so two chats that each selected a different Studio are
            // both pointed at whichever was selected LAST, and a stale choice is
            // not caught by the ambiguity refusal, because a choice exists.
            //
            // So a call may carry its own `instance`. It is resolved against the
            // Studios actually RUNNING, not against the shared selection: exactly
            // one match is the target for this call and nothing else changes; no
            // match is refused by name; more than one falls through to the
            // ambiguity rules below. A chat that always passes its own Studio can
            // therefore never write into another chat's project, whatever the
            // shared selection says, and never needs select_trados_instance.
            //
            // Decided on the original args - `instance` never affects whether a
            // tool writes - then stripped, so the bridge never sees a parameter it
            // does not know.
            var isWrite = def.IsWrite(args);
            args = StripInstanceArg(args, out var wantedInstance);

            var target = selection.Chosen;
            var ambiguous = selection.IsAmbiguous;
            if (wantedInstance != null)
            {
                var matching = selection.Live.Where(i => BridgeClient.Matches(i, wantedInstance)).ToList();
                if (matching.Count == 1)
                {
                    target = matching[0];
                    ambiguous = false;
                }
                else if (matching.Count == 0)
                {
                    return TextResult(RefuseNoSuchInstance(def.Name, wantedInstance, selection), isError: false);
                }
                // >1: two Studios answer to the same name (e.g. one project open
                // twice). Ambiguous in the ordinary sense; handled below.
            }

            if (ambiguous && isWrite)
                return TextResult(RefuseAmbiguousWrite(def.Name, selection), isError: false);

            string result = def.Method == "POST"
                ? await bridge.PostAsync(target, def.Path, BuildBody(def, args), ct)
                : await bridge.GetAsync(target, def.Path + BuildQuery(def, args), ct);

            // Every write says where it went, and so does any call that named an
            // instance, so a chat's own reads confirm the Studio they came from
            // rather than leaving it to be inferred.
            if (isWrite || wantedInstance != null) result = LabelTarget(result, target);

            return ambiguous
                ? WarnedResult(AmbiguousReadWarning(selection), result)
                : TextResult(result);
        }
        catch (Exception ex) when (ex is BridgeUnavailableException or HttpRequestException or TaskCanceledException)
        {
            // Return the actionable message as tool output (the SDK hides thrown text).
            return TextResult(JsonSerializer.Serialize(new { ok = false, error = ex.Message }), isError: false);
        }
    });

// Advertise that our tool list can change at runtime, so clients honour the
// tools/list_changed notification the poller below sends.
builder.Services.Configure<McpServerOptions>(o =>
{
    o.Capabilities ??= new ServerCapabilities();
    o.Capabilities.Tools ??= new ToolsCapability();
    o.Capabilities.Tools.ListChanged = true;
});

var host = builder.Build();

// Background watcher: the client asks for the tool list once per connection, so
// if Trados's bridge wasn't up yet at connect time (or a plugin update changes
// the tools mid-session) the client would be stuck on a stale list. Poll the
// bridge and push tools/list_changed whenever the set differs from what we last
// advertised, so the client re-lists on its own – no restart, no reinstall.
_ = Task.Run(async () =>
{
    string lastSig;
    try { lastSig = ToolSignature(await GetToolsAsync(CancellationToken.None)); }
    catch { lastSig = ""; }

    while (true)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20)); } catch { break; }
        try
        {
            var sig = ToolSignature(await GetToolsAsync(CancellationToken.None, forceRefresh: true));
            if (sig != lastSig)
            {
                lastSig = sig;
                var server = host.Services.GetService<IMcpServer>();
                if (server != null)
                {
                    try { await server.SendNotificationAsync("notifications/tools/list_changed", CancellationToken.None); }
                    catch { /* client not connected yet – it'll get the fresh list on connect */ }
                }
            }
        }
        catch { /* keep polling */ }
    }
});

await host.RunAsync();
return;

// ── helpers ─────────────────────────────────────────────────────────────

static CallToolResult TextResult(string text, bool isError = false) => new()
{
    IsError = isError,
    Content = new List<ContentBlock> { new TextContentBlock { Text = text } }
};

// Warning and payload as separate content blocks, so the tool's JSON stays
// parseable — prepending the warning into the string would corrupt it.
static CallToolResult WarnedResult(string warning, string text) => new()
{
    IsError = false,
    Content = new List<ContentBlock>
    {
        new TextContentBlock { Text = warning },
        new TextContentBlock { Text = text },
    }
};

// Deliberately on EVERY ambiguous read, not just the first: the failure this
// guards against is an agent acting confidently on the wrong project's data,
// and a warning issued forty turns ago has been compacted away. Kept to two
// lines because every byte returned is re-sent on every later turn — the exact
// cost session_report exists to measure.
static string AmbiguousReadWarning(BridgeSelection sel) =>
    $"⚠ Multiple Trados instances are live. This result is from {sel.Chosen.Label}. "
    + string.Join("; ", sel.Others.Select(o => o.Label)) + " also running.\n"
    + "Tell the user which instance this describes. Editing is refused until one is chosen — "
    + "ask which project they mean, then call select_trados_instance.";

static string RefuseAmbiguousWrite(string toolName, BridgeSelection sel)
{
    var instances = sel.Live
        .Select(i => new
        {
            studioVersion = i.StudioVersion,
            project = i.ProjectName,
            activeFile = i.ActiveFile,
            pid = i.Pid,
        })
        .ToList();

    return JsonSerializer.Serialize(new
    {
        ok = false,
        error = $"Refusing to run '{toolName}': {sel.Candidates.Count} Trados Studio instances are running "
              + "and nothing says which one to write to. Writing to the wrong one would edit the wrong "
              + "project's document. Ask the user which project they mean, then call "
              + "select_trados_instance with \"2024\", \"2026\", or part of the project name — and run "
              + "this tool again. Closing the other Studio works too.",
        instances,
        note = "Read-only tools still work and report which instance answered.",
    });
}

// ── #121: per-call instance guard and target labelling ───────────────────────

/// <summary>
/// Adds an optional string property `instance` to a tool's input schema, so
/// clients advertise it and models pass it. Returns the schema unchanged if it
/// is not an object, already has the property, or cannot be parsed - a tool
/// with a slightly poorer schema beats a server that fails to list its tools.
/// </summary>
static JsonElement WithInstanceParam(JsonElement schema)
{
    try
    {
        if (JsonNode.Parse(schema.GetRawText()) is not JsonObject node) return schema;
        if (node["properties"] is not JsonObject props)
        {
            props = new JsonObject();
            node["properties"] = props;
        }
        if (props.ContainsKey("instance")) return schema;
        props["instance"] = new JsonObject
        {
            ["type"] = "string",
            ["description"] =
                "Which Trados Studio this call is for: \"2024\", \"2026\", or part of the project name. "
                + "It is checked against the Studio the call would actually go to, and the call is refused "
                + "if they differ. The choice made with select_trados_instance is shared by every chat on "
                + "this machine, so a chat that always passes its own Studio here can never write into "
                + "another chat's project. Optional: omit to trust the current selection.",
        };
        return JsonSerializer.SerializeToElement(node);
    }
    catch
    {
        return schema;
    }
}

/// <summary>
/// Takes `instance` out of the call arguments - the bridge does not know it -
/// and reports the value it carried, trimmed, or null when absent or blank.
/// </summary>
static IReadOnlyDictionary<string, JsonElement> StripInstanceArg(
    IReadOnlyDictionary<string, JsonElement> args, out string? wanted)
{
    wanted = null;
    if (!args.TryGetValue("instance", out var v)) return args;
    if (v.ValueKind == JsonValueKind.String)
    {
        var raw = v.GetString();
        if (!string.IsNullOrWhiteSpace(raw)) wanted = raw.Trim();
    }
    var copy = new Dictionary<string, JsonElement>(args.Count);
    foreach (var kv in args)
        if (kv.Key != "instance") copy[kv.Key] = kv.Value;
    return copy;
}

static string RefuseNoSuchInstance(string toolName, string wanted, BridgeSelection sel)
{
    return JsonSerializer.Serialize(new
    {
        ok = false,
        error = $"Refusing to run '{toolName}': this call says it is for \"{wanted}\", but no running Trados "
              + "Studio matches that. Nothing was written. Check which Studios are open (list_trados_instances) "
              + "and that the one meant is running with its project open; \"2024\", \"2026\" or part of the "
              + "project name all work as the instance.",
        running = sel.Live
            .Select(i => new { studioVersion = i.StudioVersion, project = i.ProjectName, activeFile = i.ActiveFile })
            .ToList(),
    });
}

/// <summary>
/// Adds <c>target: {studioVersion, project, activeFile}</c> to a write's JSON
/// result. A result that is not a JSON object is returned as it was.
/// </summary>
static string LabelTarget(string json, BridgeInstance inst)
{
    try
    {
        if (JsonNode.Parse(json) is not JsonObject obj) return json;
        obj["target"] = new JsonObject
        {
            ["studioVersion"] = inst.StudioVersion,
            ["project"] = inst.ProjectName,
            ["activeFile"] = inst.ActiveFile,
        };
        return obj.ToJsonString();
    }
    catch
    {
        return json;
    }
}

// Stable fingerprint of the advertised tool set (names + descriptions), so the
// watcher only fires tools/list_changed on a real change.
static string ToolSignature(List<ToolDef> defs) =>
    string.Join("|", defs
        .Select(d => d.Name + "::" + d.Description)
        .OrderBy(s => s, StringComparer.Ordinal));

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
