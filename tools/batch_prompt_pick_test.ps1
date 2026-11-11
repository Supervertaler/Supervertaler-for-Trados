# Batch Operations prompt pick is kept (18.20.206, user report: "opening Settings and
# clicking OK resets the selected prompt to the default").
#
# A prompt picked in the Batch list now becomes that mode's active prompt, globally and
# for the project (BatchPromptChoice.Remember, the same storage as the Prompt Manager's
# "Set as active"), and every rebuild of the list reads it back (BatchPromptChoice.Stored).
# Checked against the built plugin DLL, outside Studio, with the real BatchTranslateControl,
# PromptManagerPanel, TermLensSettings and ProjectSettings save/load:
#
#   A  picking raises PromptPicked once with the pick; a rebuild raises nothing
#   B  pick, then Settings OK (Prompt Manager round trip + project save): still shown, ticked
#   C  pick, then Settings Cancel: still shown
#   D  Translate -> Proofread -> Translate: each mode keeps its own pick
#   E  another document of the same project (rebuild from disk): still shown
#   F  reopening the project after another project changed the global choice: still shown
#   G  "(Built-in instructions only)" picked on purpose: kept, no default guessed in
#   H  failure: the picked prompt's file is deleted -> the list shows the default, says
#      nothing was picked, and the stored choice is NOT overwritten by that fallback
#      (so the prompt comes back if the file does)
#   J  the mouse wheel over the closed list changes nothing and saves nothing
#   I  before the fix: the old rebuild (global choice only, nothing stored on a pick)
#      shows the default after Settings - the reported bug, reproduced
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\batch_prompt_pick_test.ps1
#   ... -Dll <path to Supervertaler.Trados.dll>   (default: this repo's build)
# 32-bit builds (Studio 2024 / 2022) need the 32-bit PowerShell:
#   C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\batch_prompt_pick_test.ps1 -Dll <...\bin\Studio18\Debug\Supervertaler.Trados.dll>
#
# Everything is written under a data folder in %TEMP% (SupervertalerPaths.Set), deleted
# afterwards; the real settings and diagnostic log are checked unchanged at the end.
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
        }
    } catch { }
}
function Snap($paths) { $paths | ForEach-Object { if (Test-Path -LiteralPath $_) { $i = Get-Item -LiteralPath $_; "$_|$($i.LastWriteTimeUtc.Ticks)|$(if ($i.PSIsContainer) { (Get-ChildItem -LiteralPath $_ | Measure-Object).Count } else { $i.Length })" } else { "$_|absent" } } }
$before = Snap $real

$temp = Join-Path $env:TEMP ("sv-batch-prompt-test-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $temp | Out-Null

Add-Type -AssemblyName System.Windows.Forms
[void][Reflection.Assembly]::LoadFrom($Dll)
Add-Type -ReferencedAssemblies @('System.Windows.Forms', 'System.Drawing', 'System.Runtime.Serialization', $Dll) -TypeDefinition @"
using System; using System.IO; using System.Reflection; using System.Collections.Generic; using System.Windows.Forms;
using Supervertaler.Core; using Supervertaler.Core.Models;
using Supervertaler.Trados.Controls; using Supervertaler.Trados.Settings;

public static class BatchPromptPickTest {
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

    // BatchPromptChoice is internal: reached by reflection, the code under test unchanged.
    static Type Choice;
    static void Remember(AiSettings ai, ProjectSettings ps, bool proofread, string path) {
        Choice.GetMethod("Remember").Invoke(null, new object[] { ai, ps, proofread, path });
    }
    static void Stored(AiSettings ai, ProjectSettings ps, bool proofread, out string selected, out string active) {
        var args = new object[] { ai, ps, proofread, null, null };
        Choice.GetMethod("Stored").Invoke(null, args);
        selected = (string)args[3]; active = (string)args[4];
    }
    static string BuiltInOnly { get { return (string)Choice.GetField("BuiltInOnly").GetValue(null); } }
    static string DefaultPath(List<PromptTemplate> prompts, bool proofread) {
        return (string)Choice.GetMethod("DefaultPath").Invoke(null, new object[] { prompts, proofread });
    }

    static PromptTemplate P(string name, string rel, string cat, bool isDefault) {
        return new PromptTemplate { Name = name, RelativePath = rel, Category = cat, IsDefault = isDefault, Content = "x" };
    }

    static BatchTranslateControl _batch;
    static ComboBox _combo;
    static int _picks; static string _lastPick;
    const string ProjectPath = @"C:\Projects\Client job\Client job.sdlproj";
    const string ProjectName = "Client job";

    // What PopulateBatchPromptDropdown does: read the stored state (from disk for the
    // project), then fill the list.
    static void Rebuild(TermLensSettings global, List<PromptTemplate> prompts, bool proofread) {
        string selected, active;
        Stored(global.AiSettings, ProjectSettings.Load(ProjectPath), proofread, out selected, out active);
        _batch.SetPrompts(prompts, selected, proofread ? "Proofread" : "Translate", ProjectName, active,
            DefaultPath(prompts, proofread));
    }
    // What OnBatchPromptPicked does with a pick.
    static void Store(TermLensSettings global, bool proofread, string path) {
        Remember(global.AiSettings, null, proofread, path);
        var ps = ProjectSettings.Load(ProjectPath) ?? new ProjectSettings { ProjectPath = ProjectPath, ProjectName = ProjectName };
        Remember(null, ps, proofread, path);
        ProjectSettings.Save(ProjectPath, ps);
    }
    static void Pick(string text) {
        for (int i = 0; i < _combo.Items.Count; i++)
            if (((string)_combo.Items[i]).StartsWith(text)) { _combo.SelectedIndex = i; return; }
        throw new Exception("no item " + text);
    }
    static string Shown { get { return (string)_combo.SelectedItem; } }

    public static void Run(string temp) {
        SupervertalerPaths.Set(temp);
        Check(string.Equals(SupervertalerPaths.Root, temp, StringComparison.OrdinalIgnoreCase), "data folder is the temp folder");
        Choice = typeof(BatchTranslateControl).Assembly.GetType("Supervertaler.Trados.Core.BatchPromptChoice");

        var prompts = new List<PromptTemplate> {
            P("Default Translation Prompt", "Translate/Default Translation Prompt.md", "Translate", true),
            P("Annual reports NL-EN", "Translate/Annual reports NL-EN.md", "Translate", false),
            P("Patents EN-NL", "Translate/Patents EN-NL.md", "Translate", false),
            P("Default Proofreading Prompt", "Proofread/Default Proofreading Prompt.md", "Proofread", true),
            P("100% match review", "Proofread/100% match review.md", "Proofread", false),
        };

        var host = new Form();
        _batch = new BatchTranslateControl();
        host.Controls.Add(_batch);
        _combo = (ComboBox)typeof(BatchTranslateControl).GetField("_cmbPrompt", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(_batch);
        _batch.PromptPicked += (s, path) => { _picks++; _lastPick = path; };

        var global = new TermLensSettings();
        if (global.AiSettings == null) global.AiSettings = new AiSettings();

        Console.WriteLine("A  pick vs rebuild");
        Rebuild(global, prompts, false);
        Check(_picks == 0, "a rebuild raises no pick");
        Check(Shown.StartsWith("Default Translation Prompt"), "nothing chosen yet: the shipped default is shown (" + Shown + ")");
        Pick("Annual reports");
        Check(_picks == 1 && _lastPick == "Translate/Annual reports NL-EN.md", "a pick raises PromptPicked once, with its path");
        Store(global, false, _lastPick);
        _picks = 0;
        Rebuild(global, prompts, false);
        Check(_picks == 0, "the rebuild after the pick raises nothing");
        Check(Shown == "Annual reports NL-EN  \u2714", "after the pick the list shows it, ticked as active (" + Shown + ")");

        Console.WriteLine("B  Settings OK");
        {
            var ps = ProjectSettings.Load(ProjectPath);
            var panel = new PromptManagerPanel();
            panel.PopulateFromSettings(global.AiSettings, new PromptLibrary(), ps.ActivePromptPath, ps.ActiveProofreadPromptPath);
            panel.ApplyToSettings(global.AiSettings);
            ProjectSettings.Save(ProjectPath, global.ExtractProjectSettings(ProjectPath, ProjectName));
            panel.Dispose();
        }
        Rebuild(global, prompts, false);
        Check(Shown == "Annual reports NL-EN  \u2714", "after Settings OK: still the pick (" + Shown + ")");
        Check(_picks == 0, "Settings OK raised no pick");

        Console.WriteLine("C  Settings Cancel");
        Rebuild(global, prompts, false);   // Cancel writes nothing; the list is rebuilt anyway
        Check(Shown == "Annual reports NL-EN  \u2714", "after Settings Cancel: still the pick");

        Console.WriteLine("D  Translate -> Proofread -> Translate");
        Rebuild(global, prompts, true);
        Check(Shown.StartsWith("Default Proofreading Prompt"), "Proofread shows its own default (" + Shown + ")");
        Pick("100% match review");
        Store(global, true, _lastPick);
        Rebuild(global, prompts, true);
        Check(Shown == "100% match review  \u2714", "Proofread keeps its pick");
        Rebuild(global, prompts, false);
        Check(Shown == "Annual reports NL-EN  \u2714", "back in Translate: the Translate pick, untouched by Proofread's");
        Rebuild(global, prompts, true);
        Check(Shown == "100% match review  \u2714", "and Proofread's is still there");

        Console.WriteLine("E  another document of the same project");
        Rebuild(global, prompts, false);
        Check(Shown == "Annual reports NL-EN  \u2714", "rebuilt from disk for another document: still the pick");

        Console.WriteLine("F  reopening the project");
        // Another project was used in between and set its own global choice; then a
        // fresh start reads this project's file again.
        var restarted = new TermLensSettings(); restarted.AiSettings = new AiSettings();
        restarted.AiSettings.SelectedPromptPath = "Translate/Patents EN-NL.md";
        restarted.ApplyProjectOverlay(ProjectSettings.Load(ProjectPath));
        Check(restarted.AiSettings.SelectedPromptPath == "Translate/Annual reports NL-EN.md", "the project's pick is applied to the global choice on opening");
        Rebuild(restarted, prompts, false);
        Check(Shown == "Annual reports NL-EN  \u2714", "reopened project: still the pick");
        global = restarted;

        Console.WriteLine("G  built-in instructions only");
        Pick("(Built-in");
        Check(_lastPick == BuiltInOnly, "the first item is stored as 'built-in only', not as nothing");
        Store(global, false, _lastPick);
        Rebuild(global, prompts, false);
        Check(_combo.SelectedIndex == 0, "after a rebuild: still built-in only, no default guessed in (" + Shown + ")");
        Pick("Annual reports"); Store(global, false, _lastPick);

        Console.WriteLine("H  failure: the picked prompt's file was deleted");
        var gone = prompts.FindAll(p => p.Name != "Annual reports NL-EN");
        _picks = 0;
        Rebuild(global, gone, false);
        Check(Shown.StartsWith("Default Translation Prompt"), "the list falls back to the default (" + Shown + ")");
        Check(_batch.GetSelectedPromptPath() == "Translate/Default Translation Prompt.md", "and a run would use what is shown");
        Check(_picks == 0, "the fallback is not taken for a pick");
        Check(ProjectSettings.Load(ProjectPath).ActivePromptPath == "Translate/Annual reports NL-EN.md"
              && global.AiSettings.SelectedPromptPath == "Translate/Annual reports NL-EN.md", "the stored choice is not overwritten by the fallback");
        Rebuild(global, prompts, false);
        Check(Shown == "Annual reports NL-EN  \u2714", "file back: the pick is back");
        _batch.SetPrompts(null, "Translate/Annual reports NL-EN.md", "Translate", ProjectName, null, null);
        Check(_combo.Items.Count == 1 && _picks == 0, "no prompt library at all: just the built-in item, no throw, no pick");

        Console.WriteLine("I  the bug as it was");
        {
            // Old rebuild: the global choice only (here never set by a pick) and no
            // project entry - what Settings OK led to before the fix.
            var old = new TermLensSettings(); old.AiSettings = new AiSettings();
            _batch.SetPrompts(prompts, old.AiSettings.GetBatchPrompt(false), "Translate", ProjectName, null, DefaultPath(prompts, false));
            Check(Shown.StartsWith("Default Translation Prompt"), "without a stored pick the rebuild shows the default - the reported reset");
        }

        Console.WriteLine("J  mouse wheel over the closed prompt list");
        Rebuild(global, prompts, false);
        host.Show(); Application.DoEvents();
        var shownBefore = Shown;
        var storedBefore = ProjectSettings.Load(ProjectPath).ActivePromptPath;
        _picks = 0;
        var pt = _combo.PointToScreen(new System.Drawing.Point(_combo.Width / 2, _combo.Height / 2));
        foreach (var delta in new[] { -120, -120, 120, -120 })
            SendMessage(_combo.Handle, 0x020A, (IntPtr)(delta << 16), (IntPtr)((pt.Y << 16) | (pt.X & 0xFFFF)));
        Application.DoEvents();
        Check(Shown == shownBefore, "the wheel leaves the shown prompt as it was (" + Shown + ")");
        Check(_picks == 0, "no pick raised, so nothing is saved");
        Check(ProjectSettings.Load(ProjectPath).ActivePromptPath == storedBefore, "the stored active prompt is unchanged");

        host.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
}
"@

[BatchPromptPickTest]::Install([string[]]@((Split-Path $Dll), $studio))
try {
    [BatchPromptPickTest]::Run($temp)
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
$after = Snap $real
$same = (Compare-Object $before $after) -eq $null
if ($same) { [BatchPromptPickTest]::Pass++ ; Write-Host "  ok   real settings, diagnostic log and project files unchanged" }
else { [BatchPromptPickTest]::Fail++ ; Write-Host "  FAIL real files changed:"; Compare-Object $before $after | Out-Host }
Write-Host ""
Write-Host ("{0} passed, {1} failed" -f [BatchPromptPickTest]::Pass, [BatchPromptPickTest]::Fail)
if ([BatchPromptPickTest]::Fail -gt 0) { exit 1 }
