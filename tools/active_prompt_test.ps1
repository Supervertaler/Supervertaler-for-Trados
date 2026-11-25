# MCP get_active_prompt / set_active_prompt (20.210), offline, against the built plugin DLL.
#
# What it checks, outside Studio, with the plugin's own code:
#
#   A  what a stored choice means (Core/ActivePrompt.Describe): "" is the shipped default,
#      "*built-in*" no library prompt, a deleted prompt no library prompt; marker-tolerant
#   B  which prompts set_active_prompt accepts (ActivePrompt.Validate): unknown path refused
#      with near matches and no fallback; memoQ-only refused; QuickLauncher refused; the
#      wrong mode refused both ways; a translate prompt outside the Translate folder accepted
#      (the Prompt Manager's rule); "trados", "both" and no app accepted; 'kind' parsing
#   C  the stale-write risk the handoff names, reproduced: a write to the project's file
#      alone is undone by the save every project switch makes (SaveCurrentProjectSettings
#      -> ExtractProjectSettings), and the setter's write (BatchPromptChoice.Remember into
#      the global settings AND the project file) survives it
#   D  the bridge: GET /v1/active-prompt and POST /v1/set-active-prompt over real HTTP,
#      with fake pane handlers; the body reaches the handler; a body with no path is
#      refused before the handler; /v1/tools advertises both tools, with the access the
#      MCP server needs to refuse a write under two Studios, at routes the bridge answers
#
# What it cannot reach: the pane itself (UI thread, the Batch list, the Settings-window
# check, the read-back) - that is the live test in Studio, acceptance tests 1-9.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\active_prompt_test.ps1
#   ... -Dll <path to Supervertaler.Trados.dll>   (default: this repo's build)
# 32-bit builds (Studio 2024 / 2022) need the 32-bit PowerShell:
#   C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\active_prompt_test.ps1
#
# Everything is written under a data folder in %TEMP% (SupervertalerPaths.Set), deleted
# afterwards; the real settings, project files and diagnostic logs are checked unchanged.
param([string]$Dll)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Dll) {
    $want = if ([IntPtr]::Size -eq 8) { @('Studio19\Debug', 'Studio19\Release') } else { @('Studio18\Debug', 'Studio18\Release', 'Studio17\Debug', 'Studio17\Release') }
    foreach ($c in $want) {
        $p = Join-Path $root "src\Supervertaler.Trados\bin\$c\Supervertaler.Trados.dll"
        if (Test-Path $p) { $Dll = $p; break }
    }
}
if (-not $Dll -or -not (Test-Path $Dll)) { throw "Build the plugin first (no Supervertaler.Trados.dll found)" }
$Dll = (Resolve-Path -LiteralPath $Dll).Path
$studio = if ($Dll -match '\\Studio19\\') { "C:\Program Files\Trados\Trados Studio\Studio19" }
          elseif ($Dll -match '\\Studio17\\') { "C:\Program Files (x86)\Trados\Trados Studio\Studio17" }
          else { "C:\Program Files (x86)\Trados\Trados Studio\Studio18" }
Write-Host "Plugin: $Dll ($([IntPtr]::Size * 8)-bit process)"

# Real files that must not change.
$real = @()
$cfg = Join-Path $env:APPDATA "Supervertaler\config.json"
$real += $cfg
if (Test-Path -LiteralPath $cfg) {
    try {
        $dataDir = (Get-Content -LiteralPath $cfg -Raw | ConvertFrom-Json).user_data_path
        if ($dataDir) {
            $real += (Join-Path $dataDir "trados\settings\settings.json")
            $real += (Join-Path $dataDir "trados\diagnostic.log")
            $real += (Join-Path $dataDir "diagnostic.log")
            $real += (Join-Path $dataDir "trados\projects")
            $real += (Join-Path $dataDir "trados\runtime")
        }
    } catch { }
}
function Snap($paths) { $paths | ForEach-Object { if (Test-Path -LiteralPath $_) { $i = Get-Item -LiteralPath $_; "$_|$($i.LastWriteTimeUtc.Ticks)|$(if ($i.PSIsContainer) { (Get-ChildItem -LiteralPath $_ | Measure-Object).Count } else { $i.Length })" } else { "$_|absent" } } }
$before = Snap $real

$temp = Join-Path $env:TEMP ("sv-active-prompt-test-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $temp | Out-Null

Add-Type -AssemblyName System.Windows.Forms
[void][Reflection.Assembly]::LoadFrom($Dll)
Add-Type -ReferencedAssemblies @('System.Windows.Forms', 'System.Drawing', 'System.Runtime.Serialization', $Dll) -TypeDefinition @"
using System; using System.IO; using System.Net; using System.Text; using System.Reflection;
using System.Collections.Generic; using System.Linq;
using Supervertaler.Core; using Supervertaler.Core.Models;
using Supervertaler.Trados.Core; using Supervertaler.Trados.Settings;

public static class ActivePromptTest {
    static string[] _dirs;
    public static void Install(string[] dirs) { _dirs = dirs; AppDomain.CurrentDomain.AssemblyResolve += Resolve; }
    static Assembly Resolve(object sender, ResolveEventArgs e) {
        var n = new AssemblyName(e.Name).Name;
        foreach (var d in _dirs) { var p = Path.Combine(d, n + ".dll"); if (File.Exists(p)) return Assembly.LoadFrom(p); }
        return null;
    }

    public static int Pass, Fail;
    static void Check(bool ok, string what) {
        if (ok) { Pass++; Console.WriteLine("  ok   " + what); } else { Fail++; Console.WriteLine("  FAIL " + what); }
    }

    // ActivePrompt and BatchPromptChoice are internal: reached by reflection, unchanged.
    static Type AP, Choice;
    static BridgeActivePromptSlot Describe(string stored, bool proofread, List<PromptTemplate> prompts) {
        return (BridgeActivePromptSlot)AP.GetMethod("Describe").Invoke(null, new object[] { stored, "project", proofread, prompts });
    }
    static PromptTemplate Validate(string path, bool proofread, List<PromptTemplate> prompts, out string error, out List<string> near) {
        var args = new object[] { path, proofread, prompts, null, null };
        var p = (PromptTemplate)AP.GetMethod("Validate").Invoke(null, args);
        error = (string)args[3]; near = (List<string>)args[4];
        return p;
    }
    static bool Kind(string kind, out bool proofread) {
        var args = new object[] { kind, false };
        var ok = (bool)AP.GetMethod("TryParseKind").Invoke(null, args);
        proofread = (bool)args[1];
        return ok;
    }
    static void Remember(AiSettings ai, ProjectSettings ps, bool proofread, string path) {
        Choice.GetMethod("Remember").Invoke(null, new object[] { ai, ps, proofread, path });
    }
    static string BuiltInOnly { get { return (string)Choice.GetField("BuiltInOnly").GetValue(null); } }

    static PromptTemplate P(string name, string rel, string cat, bool isDefault, string app = "both", bool ql = false) {
        return new PromptTemplate { Name = name, RelativePath = rel, Category = cat, IsDefault = isDefault,
                                    Content = "x", App = app, IsQuickLauncher = ql };
    }

    static List<PromptTemplate> Library() {
        return new List<PromptTemplate> {
            P("Default Translation Prompt", @"Translate\Default Translation Prompt.md", "Translate", true),
            P("Acme patents v2", @"Translate\Acme patents v2 [Trados].md", "Translate", false, "trados"),
            P("Acme patents", @"Translate\Acme patents.md", "Translate", false),
            P("Annual reports NL-EN", @"Translate\Annual reports NL-EN.md", "Translate", false),
            P("Loose prompt", @"Loose prompt.md", "", false),
            P("memoQ only", @"Translate\memoQ only [memoQ].md", "Translate", false, "memoq"),
            P("No app key", @"Translate\No app key.md", "Translate", false, null),
            P("Default Proofreading Prompt", @"Proofread\Default Proofreading Prompt.md", "Proofread", true),
            P("PROJ-001 review", @"Proofread\PROJ-001 review.md", "Proofread", false),
            P("Explain", @"QuickLauncher\Explain.md", "QuickLauncher", false, "both", true),
        };
    }

    public static void Rules() {
        AP = typeof(SupervertalerBridge).Assembly.GetType("Supervertaler.Trados.Core.ActivePrompt", true);
        Choice = typeof(SupervertalerBridge).Assembly.GetType("Supervertaler.Trados.Core.BatchPromptChoice", true);
        var lib = Library();

        Console.WriteLine("A  what a stored choice means");
        var s = Describe("", false, lib);
        Check(s.Path == "" && s.EffectivePath == @"Translate\Default Translation Prompt.md" && s.Exists && s.Note != null,
              "A1 empty translate choice = the Default Translation Prompt runs, said in a note");
        s = Describe("", true, lib);
        Check(s.EffectivePath == @"Proofread\Default Proofreading Prompt.md", "A2 empty proofread choice = the Default Proofreading Prompt");
        s = Describe(BuiltInOnly, false, lib);
        Check(s.EffectivePath == null && s.Name == "(System Prompt only)" && s.Note != null, "A3 *built-in* = no library prompt (" + s.Name + ")");
        s = Describe(BuiltInOnly, true, lib);
        Check(s.EffectivePath == null && s.Name == "(Built-in checks only)", "A4 *built-in* in Proofread = built-in checks (" + s.Name + ")");
        s = Describe(@"Translate\Gone.md", false, lib);
        Check(!s.Exists && s.EffectivePath == null && s.Note != null, "A5 a deleted prompt: exists=false, no library prompt runs");
        s = Describe(@"Translate\Acme patents v2.md", false, lib);
        Check(s.Exists && s.EffectivePath == @"Translate\Acme patents v2 [Trados].md" && s.Name == "Acme patents v2",
              "A6 a path stored before the [Trados] marker still resolves");
        var noDefault = lib.Where(p => !p.IsDefault).ToList();
        s = Describe("", false, noDefault);
        Check(s.EffectivePath == null && s.Note != null, "A7 empty choice and no shipped default: no library prompt, said");

        Console.WriteLine("B  which prompts may become active");
        string err; List<string> near; bool pr;
        var p0 = Validate(@"Translate\Acme patent v2.md", false, lib, out err, out near);
        Check(p0 == null && err != null && near != null && near.Contains(@"Translate\Acme patents v2 [Trados].md"),
              "B1 unknown path refused, no fallback, near matches list the close one: " + string.Join(", ", near ?? new List<string>()));
        Check(near == null || !near.Any(n => n.StartsWith("Proofread") || n.StartsWith("QuickLauncher") || n.Contains("memoQ")),
              "B2 near matches are of the asked mode and runnable here");
        p0 = Validate(@"Translate\Zzqx.md", false, lib, out err, out near);
        Check(p0 == null && near.Count == 0 && err.Contains("list_prompts"), "B3 nothing close: no near matches, pointed at list_prompts");
        p0 = Validate(@"Translate\memoQ only.md", false, lib, out err, out near);
        Check(p0 == null && err.Contains("memoQ"), "B4 a memoQ-only prompt refused");
        p0 = Validate(@"QuickLauncher\Explain.md", false, lib, out err, out near);
        Check(p0 == null && err.Contains("QuickLauncher"), "B5 a QuickLauncher prompt refused");
        p0 = Validate(@"Proofread\PROJ-001 review.md", false, lib, out err, out near);
        Check(p0 == null && err.Contains("proofread"), "B6 a proofreading prompt refused as kind translate");
        p0 = Validate(@"Translate\Acme patents.md", true, lib, out err, out near);
        Check(p0 == null && err.Contains("translate"), "B7 a translation prompt refused as kind proofread");
        p0 = Validate(@"Proofread\PROJ-001 review.md", true, lib, out err, out near);
        Check(p0 != null && p0.Name == "PROJ-001 review", "B8 a proofreading prompt accepted as kind proofread");
        p0 = Validate("Loose prompt.md", false, lib, out err, out near);
        Check(p0 != null, "B9 a prompt outside the Translate folder accepted as translate (the Prompt Manager allows it)");
        p0 = Validate(@"Translate\No app key.md", false, lib, out err, out near);
        Check(p0 != null, "B10 a prompt with no app key accepted");
        p0 = Validate("Translate/Acme patents v2 [Trados].md", false, lib, out err, out near);
        Check(p0 != null && p0.RelativePath == @"Translate\Acme patents v2 [Trados].md", "B11 forward slashes and the app marker accepted (app: trados)");
        p0 = Validate("translate/acme patents v2.md", false, lib, out err, out near);
        Check(p0 != null, "B12 case and missing marker tolerated, as the panel tolerates them");
        p0 = Validate("  ", false, lib, out err, out near);
        Check(p0 == null && err.Contains("path"), "B13 an empty path refused");
        Check(Kind(null, out pr) && !pr, "B14 kind omitted = translate");
        Check(Kind("Proofread", out pr) && pr, "B15 kind 'Proofread' = proofread");
        Check(!Kind("review", out pr), "B16 any other kind refused, not guessed");
    }

    const string ProjectPath = @"C:\Projects\PROJ-001\PROJ-001.sdlproj";
    const string ProjectName = "PROJ-001";

    public static void StaleWrite() {
        Console.WriteLine("C  the stale-write risk");
        const string Old = @"Translate\Acme patents.md";
        const string New = @"Translate\Acme patents v2 [Trados].md";

        // Studio's state with the project open: the global settings mirror its choice.
        var global = new TermLensSettings { AiSettings = new AiSettings() };
        Remember(global.AiSettings, null, false, Old);
        var ps = new ProjectSettings { ProjectPath = ProjectPath, ProjectName = ProjectName };
        Remember(null, ps, false, Old);
        ProjectSettings.Save(ProjectPath, ps);
        Check(ProjectSettings.Load(ProjectPath).ActivePromptPath == Old, "C1 project starts on the old prompt");

        // A file-only write, then the save every project switch makes.
        ps = ProjectSettings.Load(ProjectPath);
        ps.ActivePromptPath = New;
        ProjectSettings.Save(ProjectPath, ps);
        ProjectSettings.Save(ProjectPath, global.ExtractProjectSettings(ProjectPath, ProjectName));
        Check(ProjectSettings.Load(ProjectPath).ActivePromptPath == Old,
              "C2 reproduced: a write to the file alone is undone at the next project switch");

        // The setter's write: global and project, as a pick in the Batch list.
        Remember(global.AiSettings, null, false, New);
        ps = ProjectSettings.Load(ProjectPath);
        Remember(null, ps, false, New);
        ProjectSettings.Save(ProjectPath, ps);
        ProjectSettings.Save(ProjectPath, global.ExtractProjectSettings(ProjectPath, ProjectName));
        var after = ProjectSettings.Load(ProjectPath);
        Check(after.ActivePromptPath == New, "C3 the setter's write survives the project-switch save");
        Check(after.ActiveProofreadPromptPath == "", "C4 the other mode is untouched");
    }

    // ── D: the bridge over HTTP, with fake pane handlers ────────────────────────
    static string Send(string method, string url, string token, string body) {
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.Method = method; req.Timeout = 10000;
        req.Headers["Authorization"] = "Bearer " + token;
        req.Headers["X-Supervertaler-Mcp-Exe-Version"] = "3";
        if (body != null) {
            var bytes = Encoding.UTF8.GetBytes(body);
            req.ContentType = "application/json";
            using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
        } else if (method == "POST") req.ContentLength = 0;
        HttpWebResponse resp;
        try { resp = (HttpWebResponse)req.GetResponse(); }
        catch (WebException ex) { resp = ex.Response as HttpWebResponse; if (resp == null) return "0|" + ex.Message; }
        using (resp) using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            return ((int)resp.StatusCode) + "|" + r.ReadToEnd();
    }

    public static void Bridge() {
        Console.WriteLine("D  the bridge routes");
        BridgeSetActivePromptRequest seen = null; int setCalls = 0;
        var bridge = new SupervertalerBridge(
            getContext: () => new BridgeContextSnapshot(),
            insertText: t => null,
            getActivePrompt: () => new BridgeActivePromptResponse {
                Ok = true, Available = true,
                Project = new BridgePromptProject { Name = ProjectName, StudioVersion = "2024" },
                Translate = new BridgeActivePromptSlot { Path = "", EffectivePath = @"Translate\Default Translation Prompt.md", Name = "Default Translation Prompt" },
                Proofread = new BridgeActivePromptSlot { Path = "" } },
            setActivePrompt: r => { setCalls++; seen = r; return new BridgeSetActivePromptResponse { Ok = true, Kind = r.Kind }; });
        bridge.Start();
        try {
            Check(bridge.IsRunning, "D0 bridge started on port " + bridge.Port);
            var token = (string)typeof(SupervertalerBridge).GetField("_token", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(bridge);
            var url = "http://127.0.0.1:" + bridge.Port;

            var r1 = Send("GET", url + "/v1/active-prompt", token, null);
            Check(r1.StartsWith("200|") && r1.Contains("\"translate\":{\"path\":\"\"") && r1.Contains("\"effectivePath\":\"Translate\\\\Default Translation Prompt.md\"")
                  && r1.Contains("\"studioVersion\":\"2024\""), "D1 GET /v1/active-prompt: " + Short(r1));

            var r2 = Send("POST", url + "/v1/set-active-prompt", token, "{\"path\":\"Proofread\\\\PROJ-001 review.md\",\"kind\":\"proofread\"}");
            Check(r2.StartsWith("200|") && setCalls == 1 && seen != null && seen.Path == @"Proofread\PROJ-001 review.md" && seen.Kind == "proofread",
                  "D2 POST /v1/set-active-prompt hands path and kind to the pane: " + Short(r2));

            var r3 = Send("POST", url + "/v1/set-active-prompt", token, "{\"kind\":\"translate\"}");
            Check(r3.StartsWith("400|") && setCalls == 1 && r3.Contains("path"), "D3 no path: refused before the pane is asked: " + Short(r3));
            var r4 = Send("POST", url + "/v1/set-active-prompt", token, null);
            Check(r4.StartsWith("400|") && setCalls == 1, "D4 empty body refused: " + Short(r4));
            var r5 = Send("POST", url + "/v1/set-active-prompt", token, "{not json");
            Check(r5.StartsWith("400|") && setCalls == 1, "D5 malformed body refused: " + Short(r5));
            var r6 = Send("GET", url + "/v1/active-prompt", "wrong-token", null);
            Check(r6.StartsWith("401|"), "D6 wrong token refused");

            var tools = Send("GET", url + "/v1/tools", token, null);
            Check(tools.Contains("\"name\":\"get_active_prompt\"") || tools.Contains("\"name\": \"get_active_prompt\""), "D7 /v1/tools advertises get_active_prompt");
            Check(tools.Contains("\"name\":\"set_active_prompt\"") || tools.Contains("\"name\": \"set_active_prompt\""), "D8 /v1/tools advertises set_active_prompt");
        } finally { bridge.Dispose(); }
    }

    static string Short(string s) { s = s.Replace("\r", "").Replace("\n", " "); return s.Length > 160 ? s.Substring(0, 160) + "..." : s; }

    public static void Run(string temp) {
        SupervertalerPaths.Set(temp);
        Check(string.Equals(SupervertalerPaths.Root, temp, StringComparison.OrdinalIgnoreCase), "data folder is the temp folder");
        Rules();
        StaleWrite();
        Bridge();
    }
}
"@

$exit = 1
try {
    [ActivePromptTest]::Install(@((Split-Path $Dll), $studio))
    [ActivePromptTest]::Run($temp)

    # The registry the MCP server serves: both tools, the access the instance rules need,
    # and routes the bridge answers.
    Write-Host "E  the tool registry"
    $reg = Get-Content -LiteralPath (Join-Path $root "src\Supervertaler.Trados\Resources\mcp-tools.json") -Raw -Encoding UTF8 | ConvertFrom-Json
    $g = $reg.tools | Where-Object name -eq 'get_active_prompt'
    $s = $reg.tools | Where-Object name -eq 'set_active_prompt'
    $ok = $g -and $g.access -eq 'read' -and $g.method -eq 'GET' -and $g.path -eq '/v1/active-prompt'
    Write-Host ("  {0} E1 get_active_prompt: read, GET /v1/active-prompt" -f $(if ($ok) { 'ok  ' } else { 'FAIL' })); if ($ok) { [ActivePromptTest]::Pass++ } else { [ActivePromptTest]::Fail++ }
    $ok = $s -and $s.access -eq 'write' -and $s.method -eq 'POST' -and $s.path -eq '/v1/set-active-prompt' -and ($s.inputSchema.required -contains 'path')
    Write-Host ("  {0} E2 set_active_prompt: write (refused under two Studios), POST, path required" -f $(if ($ok) { 'ok  ' } else { 'FAIL' })); if ($ok) { [ActivePromptTest]::Pass++ } else { [ActivePromptTest]::Fail++ }
    $sp = $reg.tools | Where-Object name -eq 'save_prompt'
    $ok = $sp.description -match 'does NOT make the prompt active' -and $sp.description -match 'set_active_prompt'
    Write-Host ("  {0} E3 save_prompt says saving does not activate, and points at set_active_prompt" -f $(if ($ok) { 'ok  ' } else { 'FAIL' })); if ($ok) { [ActivePromptTest]::Pass++ } else { [ActivePromptTest]::Fail++ }
    $exit = if ([ActivePromptTest]::Fail -eq 0) { 0 } else { 1 }
}
catch {
    Write-Host "ERROR: $($_.Exception.ToString())"
    $exit = 1
}
finally {
    try { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

$after = Snap $real
$same = (Compare-Object $before $after | Measure-Object).Count -eq 0
Write-Host ("  {0} real settings, project files and logs unchanged" -f $(if ($same) { 'ok  ' } else { 'FAIL' }))
if (-not $same) { Compare-Object $before $after | ForEach-Object { Write-Host "     $($_.InputObject) $($_.SideIndicator)" }; $exit = 1 }

Write-Host ""
Write-Host "$([ActivePromptTest]::Pass) passed, $([ActivePromptTest]::Fail) failed"
exit $exit
