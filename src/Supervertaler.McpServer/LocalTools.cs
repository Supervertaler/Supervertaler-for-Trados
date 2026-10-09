using System.Text.Json;
using System.Text.Json.Nodes;

namespace Supervertaler.McpServer;

/// <summary>
/// Tools the exe answers itself instead of forwarding to a bridge.
///
/// Everything else this server exposes comes from the plugin's /v1/tools
/// registry, which is what lets new tools ship in a plugin update with no
/// reinstall. These two cannot: they are about WHICH bridge to talk to, so a
/// bridge is the one thing that cannot answer them. They are advertised
/// unconditionally rather than only when a second Studio appears — a client
/// that ignores tools/list_changed would otherwise never learn the tool exists,
/// and would be stuck unable to resolve the very ambiguity it is being told
/// about. Two extra tools is the cheaper mistake.
///
/// See issue #72.
/// </summary>
public static class LocalTools
{
    public const string ListInstances = "list_trados_instances";
    public const string SelectInstance = "select_trados_instance";

    public static bool Handles(string name) => name is ListInstances or SelectInstance;

    public static IEnumerable<(string Name, string Description, JsonElement Schema)> Definitions()
    {
        // memoQ runs one instance with one handshake; the instance tools would
        // only ever answer "the one memoQ", and their wording is Trados's.
        if (BridgeClient.IsMemoQ) yield break;

        yield return (ListInstances,
            "List the Trados Studio instances currently running with the Supervertaler bridge, "
            + "with the Studio version and open project of each, and say which one your tool calls "
            + "go to. Use this when the user has more than one Studio open and you need to say which "
            + "project you are looking at, or before choosing one with select_trados_instance.",
            Schema("{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}"));

        yield return (SelectInstance,
            "Choose which running Trados Studio this conversation works with, when more than one is "
            + "open. Editing tools refuse to run while the choice is ambiguous, so call this after "
            + "asking the user which project they mean. The selector is \"2022\", \"2024\" or \"2026\" for a "
            + "Studio version, or part of a project name (case-insensitive). It matches on what the "
            + "Studio IS, so it keeps working if that Studio is restarted. Pass an empty string to "
            + "clear the choice. Lasts for this chat session.",
            Schema("{\"type\":\"object\",\"properties\":{\"instance\":{\"type\":\"string\","
                 + "\"description\":\"\\\"2022\\\", \\\"2024\\\", \\\"2026\\\", part of a project name, or \\\"\\\" to clear.\"}},"
                 + "\"required\":[\"instance\"],\"additionalProperties\":false}"));
    }

    /// <summary>Run a local tool. Returns the JSON text to hand back to the client.</summary>
    public static string Invoke(string name, IReadOnlyDictionary<string, JsonElement> args, BridgeClient bridge)
    {
        return name == SelectInstance ? Select(args, bridge) : List(bridge);
    }

    private static string List(BridgeClient bridge)
    {
        try
        {
            var sel = bridge.Resolve();
            return Json(new
            {
                ok = true,
                selector = sel.Selector,
                active = Describe(sel.Chosen),
                instances = sel.Live.Select(i => new
                {
                    studioVersion = i.StudioVersion,
                    project = i.ProjectName,
                    activeFile = i.ActiveFile,
                    pluginVersion = i.PluginVersion,
                    pid = i.Pid,
                    isActive = i.Pid == sel.Chosen.Pid,
                    matchesSelector = sel.Candidates.Any(c => c.Pid == i.Pid),
                }),
                note = sel.IsAmbiguous
                    ? "More than one instance is in scope, so editing tools are refused. "
                    + "Ask the user which project they mean, then call select_trados_instance."
                    : sel.SelectorResolved
                        ? "A selector is pinning this conversation to one of several running instances."
                        : null,
            });
        }
        catch (BridgeUnavailableException ex)
        {
            // Includes the "selector matches nothing" case, whose message already
            // lists what IS running — the useful answer to this question.
            return Json(new { ok = false, error = ex.Message });
        }
    }

    private static string Select(IReadOnlyDictionary<string, JsonElement> args, BridgeClient bridge)
    {
        var raw = args.TryGetValue("instance", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";
        var selector = raw.Trim();

        if (selector.Length == 0)
        {
            bridge.SessionSelector = null;
            return Json(new
            {
                ok = true,
                selector = (string?)null,
                message = "Cleared. Tool calls now go to whichever Studio started most recently, "
                        + "and editing is refused while more than one is running.",
            });
        }

        List<BridgeInstance> live;
        try { live = bridge.Resolve().Live.ToList(); }
        catch (BridgeUnavailableException ex) { return Json(new { ok = false, error = ex.Message }); }

        // Validate against what is actually running, so a typo is caught here
        // rather than surfacing as a puzzling failure on the next real call.
        var matches = live.Where(i => BridgeClient.Matches(i, selector)).ToList();

        if (matches.Count == 0)
            return Json(new
            {
                ok = false,
                error = $"No running Trados Studio matches \"{selector}\".",
                instances = live.Select(Describe),
                hint = "Use \"2022\", \"2024\", \"2026\", or part of one of the project names above.",
            });

        if (matches.Count > 1)
            return Json(new
            {
                ok = false,
                error = $"\"{selector}\" matches {matches.Count} running instances, so it does not "
                      + "identify one.",
                matched = matches.Select(Describe),
                hint = "Narrow it down – a longer part of the project name, or the Studio version.",
            });

        bridge.SessionSelector = selector;
        var chosen = matches[0];
        return Json(new
        {
            ok = true,
            selector,
            active = Describe(chosen),
            message = $"This conversation now works with {chosen.Label}. Editing tools are enabled "
                    + "again. The choice follows the project, not the process, so it survives that "
                    + "Studio being restarted.",
        });
    }

    private static object Describe(BridgeInstance i) => new
    {
        studioVersion = i.StudioVersion,
        project = i.ProjectName,
        pid = i.Pid,
        label = i.Label,
    };

    private static JsonElement Schema(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static string Json(object o) => JsonSerializer.Serialize(o,
        new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
}
