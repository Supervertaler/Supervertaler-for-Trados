using System.Diagnostics;
using System.Text.Json;
using Supervertaler.McpServer;

namespace McpServerCheck;

/// <summary>
/// The MCP server's choice of Trados Studio, against handshake files in a
/// temporary runtime folder. A "running Studio" is a child of this program that
/// only sleeps (the server asks nothing of a Studio to choose it: it reads the
/// handshake and checks that the process is alive); closing a Studio is ending
/// that child, with its handshake left behind as a crashed Studio leaves it.
///
/// What it cannot reach: a real Studio writing its handshake, and the MCP
/// protocol around the tools (Program.cs); the calls Program.cs makes are run
/// here directly.
/// </summary>
internal static class Check
{
    static int _pass, _fail;
    static readonly List<Process> Children = new();

    static void Ok(bool ok, string what)
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what);
    }

    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--stand-in")
        {
            // A stand-in Studio: alive until ended, or until the test is long over.
            Thread.Sleep(TimeSpan.FromMinutes(10));
            return 0;
        }

        // The server reads these from its environment; a test must not inherit them.
        foreach (var name in new[] { "SUPERVERTALER_BRIDGE_FILE", "SUPERVERTALER_HOST", "SUPERVERTALER_TRADOS_INSTANCE" })
            Environment.SetEnvironmentVariable(name, null);

        var root = Path.Combine(Path.GetTempPath(), "sv-mcp-server-check-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Run(root);
        }
        catch (Exception ex)
        {
            _fail++;
            Console.WriteLine("FAIL  the check itself threw: " + ex);
        }
        finally
        {
            // Also when a check throws: no stand-in is left running, no folder left behind.
            foreach (var c in Children)
            {
                try { if (!c.HasExited) c.Kill(); } catch { }
                try { c.Dispose(); } catch { }
            }
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(_pass + " passed, " + _fail + " failed");
        return _fail == 0 ? 0 : 1;
    }

    static Process StartStandIn()
    {
        var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--stand-in")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        Children.Add(p);
        return p;
    }

    /// <summary>Ends a stand-in and waits until Windows no longer lists it.</summary>
    static void Close(Process p)
    {
        int pid = p.Id;
        p.Kill();
        p.WaitForExit();
        p.Dispose();
        Children.Remove(p);
        for (int i = 0; i < 50; i++)
        {
            try { using var again = Process.GetProcessById(pid); }
            catch (ArgumentException) { return; }
            Thread.Sleep(100);
        }
        throw new InvalidOperationException("the stand-in process " + pid + " did not go away");
    }

    static void Handshake(string runtimeDir, int pid, string studio, string? project, int port)
    {
        var dir = Path.Combine(runtimeDir, "instances");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "bridge-" + pid + ".json"), JsonSerializer.Serialize(new
        {
            version = 1,
            port,
            token = "test-token-" + pid,
            pid,
            startedAt = DateTime.UtcNow.AddMinutes(-port % 7).ToString("o"),
            studioVersion = studio,
            pluginVersion = "0.0.0",
            projectName = project,
        }));
    }

    static JsonElement Call(BridgeClient bridge, string runtimeDir, string tool, string? instance = null)
    {
        var args = new Dictionary<string, JsonElement>();
        if (instance != null) args["instance"] = JsonSerializer.SerializeToElement(instance);
        return JsonDocument.Parse(LocalTools.Invoke(tool, args, bridge, runtimeDir)).RootElement.Clone();
    }

    static bool IsOk(JsonElement r) => r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
    static string Error(JsonElement r) => r.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
    static string ActiveStudio(JsonElement r) =>
        r.TryGetProperty("active", out var a) && a.TryGetProperty("studioVersion", out var v) ? v.GetString() ?? "" : "";

    const string Select = LocalTools.SelectInstance, List = LocalTools.ListInstances;

    static void Run(string root)
    {
        var dir = Path.Combine(root, "runtime");
        Directory.CreateDirectory(dir);
        Ok(!dir.StartsWith(@"D:\Supervertaler", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(dir, BridgeClient.ResolveRuntimeDir(), StringComparison.OrdinalIgnoreCase),
           "the handshakes are in the check's own folder, not the real runtime folder");

        // ── 0: nothing running ────────────────────────────────────────────
        var bridge = new BridgeClient();
        var r = Call(bridge, dir, Select, "2026");
        Ok(!IsOk(r) && Error(r).Length > 0 && bridge.SessionSelector == null, "0 no Studio running: choosing one is refused with the reason, nothing is stored");

        // ── 1: two Studios, choose one ────────────────────────────────────
        var s2024 = StartStandIn();
        var s2026 = StartStandIn();
        Handshake(dir, s2024.Id, "2024", "Acme PROJ-001", 5024);
        Handshake(dir, s2026.Id, "2026", "Acme PROJ-002", 5026);
        File.WriteAllText(Path.Combine(dir, "instances", "bridge-999.json"), "{ \"port\": 1, \"tok");   // half-written

        r = Call(bridge, dir, List);
        Ok(IsOk(r) && r.GetProperty("instances").GetArrayLength() == 2, "1 two Studios running (and a half-written handshake, passed over): both listed");
        r = Call(bridge, dir, Select, "2024");
        Ok(IsOk(r) && ActiveStudio(r) == "2024" && bridge.SessionSelector == "2024", "1 choosing \"2024\": chosen");
        Ok(bridge.Resolve(dir).Chosen.Pid == s2024.Id, "1 and calls now go to Studio 2024");

        // ── 2: the chosen Studio is closed (the fault) ────────────────────
        Close(s2024);
        BridgeUnavailableException? refused = null;
        try { bridge.Resolve(dir); } catch (BridgeUnavailableException ex) { refused = ex; }
        Ok(refused != null && refused.Message.Contains("\"2024\"") && refused.Message.Contains("Studio 2026"),
            "2 Studio 2024 closed, \"2024\" still selected: an ordinary call is refused and told what is running (never sent to the other Studio unasked)");
        r = Call(bridge, dir, List);
        Ok(!IsOk(r) && Error(r).Contains("Studio 2026"), "2 list_trados_instances says the same, naming Studio 2026");

        r = Call(bridge, dir, Select, "2022");
        Ok(!IsOk(r) && Error(r).Contains("\"2022\"") && r.GetProperty("instances").GetArrayLength() == 1 && bridge.SessionSelector == "2024",
            "2 choosing a Studio that is not running either is refused, lists what is, and leaves the selection as it was");

        r = Call(bridge, dir, Select, "2026");
        Ok(IsOk(r), "2 choosing \"2026\" while the stored selection is the closed \"2024\": accepted, without clearing first" + (IsOk(r) ? "" : " - " + Error(r)));
        Ok(ActiveStudio(r) == "2026" && bridge.SessionSelector == "2026", "2 the selection is now \"2026\"");
        Ok(bridge.Resolve(dir).Chosen.Pid == s2026.Id, "2 and calls go to Studio 2026");
        r = Call(bridge, dir, List);
        Ok(IsOk(r) && ActiveStudio(r) == "2026", "2 list_trados_instances works again");

        // ── 3: the stale selection came from the command line or by project name ──
        var fromConfig = new BridgeClient { CommandLineSelector = "2024" };
        r = Call(fromConfig, dir, Select, "2026");
        Ok(IsOk(r) && fromConfig.Resolve(dir).Chosen.Pid == s2026.Id, "3 a stale selection from --instance: choosing a running Studio works too");
        var byName = new BridgeClient { SessionSelector = "PROJ-001" };
        r = Call(byName, dir, Select, "proj-002");
        Ok(IsOk(r) && byName.SessionSelector == "proj-002", "3 a stale selection by project name, a new one by project name: works");
        var cleared = new BridgeClient { SessionSelector = "2024" };
        r = Call(cleared, dir, Select, "");
        Ok(IsOk(r) && cleared.SessionSelector == null && cleared.Resolve(dir).Chosen.Pid == s2026.Id, "3 clearing a stale selection still works, as before");

        // ── 4: a call that names its own Studio ────────────────────────────
        var stale = new BridgeClient { SessionSelector = "2024" };
        var sel = stale.Resolve(dir, "2026");
        Ok(sel.Chosen.Pid == s2026.Id && sel.Live.Count == 1 && stale.SessionSelector == "2024",
            "4 a call with instance \"2026\" while the stored selection is the closed \"2024\": resolved against what is running, the stored selection untouched");
        sel = stale.Resolve(dir, "2022");
        Ok(sel.Live.Count == 1 && !sel.Live.Any(i => BridgeClient.Matches(i, "2022")),
            "4 a call naming a Studio that is not running gets the running ones to refuse by name (no match among them)");
        refused = null;
        try { stale.Resolve(dir, null); } catch (BridgeUnavailableException ex) { refused = ex; }
        Ok(refused != null, "4 a call that names no Studio is still refused while the stored selection matches nothing");
        refused = null;
        try { stale.Resolve(dir, "   "); } catch (BridgeUnavailableException ex) { refused = ex; }
        Ok(refused != null, "4 and so is one whose instance is blank");

        // ── 5: a selector that fits two Studios ───────────────────────────
        var third = StartStandIn();
        Handshake(dir, third.Id, "2022", "Acme PROJ-003", 5022);
        bridge.SessionSelector = "2024";
        r = Call(bridge, dir, Select, "Acme");
        Ok(!IsOk(r) && Error(r).Contains("matches 2") && bridge.SessionSelector == "2024", "5 \"Acme\" fits two running Studios: refused, the selection left as it was");
        var live = new BridgeClient { SessionSelector = "2026" };
        sel = live.Resolve(dir, "2022");
        Ok(sel.Chosen.Pid == s2026.Id && sel.Candidates.Count == 1 && sel.Selector == "2026",
            "5 a stored selection that IS running is kept when a call names another Studio (the call's own match is made by the caller, as before)");

        // ── 6: every Studio closed ────────────────────────────────────────
        Close(third);
        Close(s2026);
        r = Call(bridge, dir, Select, "2026");
        Ok(!IsOk(r) && Error(r).Length > 0 && bridge.SessionSelector == "2024", "6 every Studio closed (handshakes left behind): refused with the reason, selection unchanged");

        // ── 7: scale ──────────────────────────────────────────────────────
        var again = StartStandIn();
        Handshake(dir, again.Id, "2026", "Acme PROJ-002", 5026);
        for (int i = 0; i < 2000; i++) Handshake(dir, 2_000_000_000 + i, "2024", "Acme left behind " + i, 6000 + i);
        var sw = Stopwatch.StartNew();
        r = Call(bridge, dir, Select, "2026");
        sw.Stop();
        Ok(IsOk(r) && bridge.Resolve(dir).Live.Count == 1 && sw.ElapsedMilliseconds < 10000,
            "7 2,000 handshakes left behind by closed Studios and one running: \"2026\" chosen in " + sw.ElapsedMilliseconds + " ms (limit 10,000)");
    }
}
