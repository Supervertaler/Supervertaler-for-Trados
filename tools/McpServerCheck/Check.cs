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

        // ── 8: Supervertaler Workbench as the host ────────────────────────
        // Workbench writes the same handshakes, to workbench\runtime, without a
        // Studio version. Two running at once are told apart by project name.
        Ok(BridgeClient.ResolveRuntimeDir().EndsWith(Path.Combine("trados", "runtime")),
            "8 with no host set the runtime folder is still Trados's");
        Environment.SetEnvironmentVariable("SUPERVERTALER_HOST", "workbench");
        try
        {
            Ok(BridgeClient.ResolveRuntimeDir().EndsWith(Path.Combine("workbench", "runtime")),
                "8 host \"workbench\": handshakes are looked for in workbench\\runtime");
            Ok(!LocalTools.Definitions().Any(), "8 the two Trados-named instance tools are not offered");

            var wbDir = Path.Combine(Path.GetTempPath(), "sv-mcp-check-wb-" + Guid.NewGuid().ToString("N"));
            var wb = new BridgeClient();
            string? none = null;
            try { wb.Resolve(wbDir); } catch (BridgeUnavailableException ex) { none = ex.Message; }
            Ok(none != null && none.Contains("Workbench") && !none.Contains("Trados"),
                "8 no Workbench running: the message says so, and does not mention Trados");

            var wb1 = StartStandIn();
            var wb2 = StartStandIn();
            Handshake(wbDir, wb1.Id, null!, "Acme PROJ-001", 5101);
            Handshake(wbDir, wb2.Id, null!, "Acme PROJ-002", 5102);
            var both = wb.Resolve(wbDir);
            Ok(both.IsAmbiguous && both.Chosen.Label.StartsWith("Supervertaler Workbench (project"),
                "8 two Workbenches running: ambiguous, and each is called Supervertaler Workbench with its project");
            Ok(both.Live.Count(i => BridgeClient.Matches(i, "PROJ-002")) == 1,
                "8 part of a project name picks out one of them");

            Close(wb2);
            Ok(wb.Resolve(wbDir).Live.Count == 1, "8 one closed (handshake left behind): only the other is found");
            Close(wb1);
            try { Directory.Delete(wbDir, true); } catch { }
        }
        finally
        {
            Environment.SetEnvironmentVariable("SUPERVERTALER_HOST", null);
        }

        HostChecks(root);
        GuidanceChecks(root);
    }

    // ── 9: which application, when the client drops SUPERVERTALER_HOST ────
    // Field report 26 Nov 2026: ChatGPT desktop started the memoQ entry without
    // its environment, so the exe served Trados. The install folder now decides.
    static void HostChecks(string root)
    {
        const string memoqExe = @"D:\Supervertaler\memoq\mcp\SupervertalerMcpServer.exe";
        const string tradosExe = @"C:\Users\someone\Supervertaler\trados\mcp\SupervertalerMcpServer.exe";
        const string claudeExe = @"C:\Users\someone\AppData\Roaming\Claude\Claude Extensions\local.mcpb.supervertaler.supervertaler-memoq\server\SupervertalerMcpServer.exe";

        Ok(HostDetection.Decide(null, null, memoqExe) == ("memoq", "install folder"),
            "9 no variable, exe in <root>\\memoq\\mcp: memoQ, from the install folder");
        Ok(HostDetection.FromInstallFolder(memoqExe)?.Root == @"D:\Supervertaler",
            "9 and the data folder is the one it is installed in");
        Ok(HostDetection.Decide(null, null, tradosExe).Host == "trados"
           && HostDetection.Decide(null, null, @"X:\SV\workbench\mcp\SupervertalerMcpServer.exe").Host == "workbench",
            "9 the same for trados\\mcp and workbench\\mcp");
        Ok(HostDetection.Decide(null, "memoq", tradosExe) == ("memoq", "SUPERVERTALER_HOST"),
            "9 a variable that IS passed still wins over the folder");
        Ok(HostDetection.Decide("trados", "memoq", memoqExe) == ("trados", "--host argument"),
            "9 --host wins over both");
        Ok(HostDetection.Decide(null, "  MemoQ ", null).Host == "memoq",
            "9 the variable is read case- and space-insensitively, as before");
        Ok(HostDetection.Decide(null, "memoq-typo", memoqExe).Host == "memoq",
            "9 an unknown value falls through to the folder instead of serving a host that does not exist");
        Ok(HostDetection.Decide(null, null, claudeExe) == ("trados", "default"),
            "9 a Claude Desktop extension folder matches nothing: Trados by default, exactly as before");
        Ok(HostDetection.Decide(null, null, @"C:\mcp\SupervertalerMcpServer.exe").Host == "trados"
           && HostDetection.FromInstallFolder(@"C:\mcp\SupervertalerMcpServer.exe") == null,
            "9 an mcp folder at a drive root has no host folder above it: no match");
        Ok(HostDetection.FromInstallFolder("C:\\a|b\\memoq\\mcp\\x.exe\0") == null
           && HostDetection.FromInstallFolder("") == null && HostDetection.FromInstallFolder(null) == null,
            "9 an unparseable, empty or missing path is no match, and does not throw");
        Ok(HostDetection.Decide(null, null, Environment.ProcessPath).Host == "trados",
            "9 this check's own exe (a build folder) is not mistaken for an install");

        // The memoQ server with no cached tool list used to offer none at all.
        var starter = ToolRegistry.MemoQStarterTools();
        Ok(starter.Select(t => t.Name).SequenceEqual(new[] { "help", "get_project" })
           && starter.All(t => t.Access == ToolAccess.Read && t.Method == "GET")
           && starter[0].Path == "/v1/help" && starter[1].Path == "/v1/project",
            "9 memoQ with no cached tool list: help and get_project are offered, read-only, at the memoQ bridge's paths");

        Environment.SetEnvironmentVariable("SUPERVERTALER_HOST", "memoq");
        var handshake = Path.Combine(root, "memoq-bridge.json");
        Environment.SetEnvironmentVariable("SUPERVERTALER_BRIDGE_FILE", handshake);
        try
        {
            var mq = new BridgeClient();

            // memoQ closed: no handshake at all.
            string? closed = null;
            try { mq.Resolve(Path.Combine(root, "unused")); } catch (BridgeUnavailableException ex) { closed = ex.Message; }
            Ok(closed != null && closed.Contains("memoQ") && !closed.Contains("Trados"),
                "9 memoQ not running: the message is about memoQ and does not mention Trados");

            // memoQ "open" (a live process) but nothing answers on its port: the
            // case that used to say "inside Trados Studio".
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            var deadPort = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var standIn = StartStandIn();
            File.WriteAllText(handshake, JsonSerializer.Serialize(new
            {
                version = 1, port = deadPort, token = "t", pid = standIn.Id,
                startedAt = DateTime.UtcNow.ToString("o"), studioVersion = "memoQ",
            }));
            string? unreachable = null;
            try { mq.GetAsync("/v1/project").GetAwaiter().GetResult(); }
            catch (BridgeUnavailableException ex) { unreachable = ex.Message; }
            Ok(unreachable != null && unreachable.Contains("memoQ") && !unreachable.Contains("Trados"),
                "9 memoQ open but its port dead: the message says restart memoQ, not \"Trados Studio\"" +
                (unreachable == null ? " (nothing thrown)" : ""));
            Close(standIn);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SUPERVERTALER_HOST", null);
            Environment.SetEnvironmentVariable("SUPERVERTALER_BRIDGE_FILE", null);
        }
        Ok(BridgeClient.UnreachableMessage("x").Contains("Trados Studio"), "9 and with no host it is Trados's, as before");
    }

    /// <summary>
    /// The instructions the server sends at connect (ServerGuidance): the
    /// segment-boundary guidance, with the right way to merge for each host. The
    /// last check starts the real server exe and reads its MCP initialize answer.
    /// </summary>
    static void GuidanceChecks(string root)
    {
        var trados = ServerGuidance.For("trados");
        var memoq = ServerGuidance.For("memoq");
        var workbench = ServerGuidance.For("workbench");
        Ok(trados.Contains("merge_segments") && trados.Contains("split_segment"),
            "10 Trados guidance names merge_segments and split_segment");
        Ok(memoq.Contains("Ctrl+J") && !memoq.Contains("merge_segments"),
            "10 memoQ guidance asks the user to join rows with Ctrl+J and names no tool memoQ lacks");
        Ok(!workbench.Contains("merge_segments") && !workbench.Contains("Ctrl+J"),
            "10 Workbench guidance names neither");
        foreach (var (name, text) in new[] { ("trados", trados), ("memoq", memoq), ("workbench", workbench) })
            Ok(text.Contains("propose it and let the user decide") && text.Contains("paragraphs")
               && text.Contains("leave the other empty") && !text.Contains("Dutch") && !text.Contains("pension"),
                $"10 {name}: general (no language pair or subject), asks first, never across paragraphs, never an empty stand-in");
        Ok(ServerGuidance.For("something-else") == trados, "10 an unknown host gets the Trados text (the server's default host)");

        // The real exe, over stdio, the way a client meets it.
        var exe = Path.Combine(AppContext.BaseDirectory, "SupervertalerMcpServer.exe");
        if (!File.Exists(exe)) { Ok(false, "10 the server exe is next to this check: " + exe); return; }
        foreach (var host in new[] { "trados", "memoq" })
        {
            var psi = new ProcessStartInfo(exe, "--host " + host)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            // No real bridge or data folder: a handshake path that does not exist.
            psi.Environment["SUPERVERTALER_BRIDGE_FILE"] = Path.Combine(root, "no-such-bridge.json");
            using var p = Process.Start(psi)!;
            Children.Add(p);
            _ = p.StandardError.ReadToEndAsync();
            p.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"check\",\"version\":\"1\"}}}");
            p.StandardInput.Flush();
            var line = p.StandardOutput.ReadLineAsync();
            string? instructions = null;
            if (line.Wait(TimeSpan.FromSeconds(30)) && line.Result != null)
            {
                using var doc = JsonDocument.Parse(line.Result);
                if (doc.RootElement.TryGetProperty("result", out var result)
                    && result.TryGetProperty("instructions", out var ins))
                    instructions = ins.GetString();
            }
            Ok(instructions == ServerGuidance.For(host),
                $"10 the real server sends the {host} guidance as its MCP instructions at initialize");
            try { p.Kill(); } catch { }
        }
    }
}
