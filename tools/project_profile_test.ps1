# Client profiles, step 1 (20.209): a project's Supervertaler set-up kept in its Studio
# settings bundle (SupervertalerProjectSettingsGroup) as well as in the sidecar file, so
# that a project template made from the project carries it into every new project.
#
# Checked against the built plugin DLL, outside Studio, with the real ProjectProfile
# rules, the real ProjectSettings Load/Save, the real termbase database code and - when
# Studio's settings types can be made outside Studio - real Studio SettingsBundle objects:
#
#   A  bundle only: its values are the project's
#   B  sidecar only (empty bundle): the sidecar, exactly as before
#   C  both, disagreeing: the bundle wins value by value; where it has none, the sidecar
#   D  a memory bank that does not exist here: kept as stored, never rewritten
#   E  a prompt that does not exist here: kept as stored, never rewritten
#   F  a termbase the bundle names but this computer lacks: reported, off here, kept in the
#      stored value - also after a change to another termbase
#   G  a termbase renamed in the same database: still found (id + creation time), and the
#      bundle then gets the new name; the same id from another database is NOT taken
#   H  legacy MultiTerm values (bare synthetic ids, paths): read; unchanged sets not rewritten
#   I  empty and corrupt bundle values: no throw, nothing switched off, bad lines kept
#   J  template -> new project -> template, through real Studio SettingsBundle objects
#      (serialised to XML and read back, as .sdltpl/.sdlproj do), on another computer whose
#      termbases have other ids
#   K  ProjectSettings.Load/Save with the cache: Load gives the effective values; a save the
#      bundle cannot take (not open / not the UI thread) is marked, and the file then wins
#   L  the client is carried through Settings OK (ExtractProjectSettings)
#   M  the highest-numbered termbase, on and in no list, stays on when the bundle decides
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\project_profile_test.ps1
#   ... -Dll <path to Supervertaler.Trados.dll>   (default: this repo's build)
# 32-bit builds (Studio 2024 / 2022) need the 32-bit PowerShell:
#   C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\project_profile_test.ps1 -Dll <...\bin\Studio18\Release\Supervertaler.Trados.dll>
#
# Everything is written under a data folder in %TEMP% (SupervertalerPaths.Set), deleted
# afterwards; the real settings, diagnostic log and project files are checked unchanged.
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
            $real += (Join-Path $dataDir "resources\supervertaler.db")
        }
    } catch { }
}
function Snap($paths) { $paths | ForEach-Object { if (Test-Path -LiteralPath $_) { $i = Get-Item -LiteralPath $_; "$_|$($i.LastWriteTimeUtc.Ticks)|$(if ($i.PSIsContainer) { (Get-ChildItem -LiteralPath $_ | Measure-Object).Count } else { $i.Length })" } else { "$_|absent" } } }
$before = Snap $real

$temp = Join-Path $env:TEMP ("sv-profile-test-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $temp | Out-Null

[void][Reflection.Assembly]::LoadFrom($Dll)
$sdlSettings = Join-Path $studio "Sdl.Core.Settings.dll"
Add-Type -ReferencedAssemblies @('System.Runtime.Serialization', 'System.Xml', $Dll, $sdlSettings) -TypeDefinition @'
using System; using System.IO; using System.Linq; using System.Reflection; using System.Collections.Generic;
using Supervertaler.Core;
using Supervertaler.Trados.Core; using Supervertaler.Trados.Settings;

public static class ProjectProfileTest {
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
    static string Ids(IEnumerable<long> ids) { return string.Join(",", (ids ?? new List<long>()).OrderBy(i => i)); }

    // This computer's termbases.
    static List<TermbaseIdentity> Local() {
        return new List<TermbaseIdentity> {
            new TermbaseIdentity(1, "Acme Patents", "2026-01-05 09:00:00"),
            new TermbaseIdentity(2, "General", "2026-01-06 09:00:00"),
            new TermbaseIdentity(3, "PROJ-001 glossary", "2026-02-01 12:00:00"),
        };
    }
    static string Line(string flags, string name, long id, string created) { return flags + "\t" + name + "\t" + id + "\t" + created; }

    static ProjectSettings Sidecar() {
        return new ProjectSettings {
            ProjectPath = @"C:\Projects\PROJ-001\PROJ-001.sdlproj", ProjectName = "PROJ-001",
            MemoryBankName = "Sidecar bank", ActivePromptPath = "Translate/Sidecar.md", ActiveProofreadPromptPath = "Proofread/Sidecar.md",
            Client = "Sidecar client",
            DisabledTermbaseIds = new List<long> { 1, 2 }, DisabledAiTermbaseIds = new List<long> { 1, 2, 3 },
            WriteTermbaseIds = new List<long> { 3 }, ProjectTermbaseId = 3, AiTermbaseIdsInitialized = true,
            DisabledMultiTermIds = new List<long>(), EnabledAiMultiTermIds = new List<long>(),
        };
    }

    static void Guard(Action a) {
        try { a(); } catch (Exception ex) { Fail++; Console.WriteLine("  FAIL threw: " + (ex.InnerException ?? ex)); }
    }

    static List<string> _reports = new List<string>();
    static void Overlay(ProjectSettings ps, ProjectProfileValues g, List<TermbaseIdentity> local) {
        _reports.Clear();
        ProjectProfile.Overlay(ps, g, local, m => _reports.Add(m));
    }

    public static void Run(string temp) {
        SupervertalerPaths.Set(temp);
        Check(string.Equals(SupervertalerPaths.Root, temp, StringComparison.OrdinalIgnoreCase), "data folder is the temp folder");

        Console.WriteLine("A  bundle only");
        {
            var ps = new ProjectSettings { DisabledTermbaseIds = new List<long>(), DisabledAiTermbaseIds = new List<long>(), WriteTermbaseIds = new List<long>(), ProjectTermbaseId = -1 };
            var g = new ProjectProfileValues {
                MemoryBankName = "Acme bank", TranslatePromptPath = "Translate/Acme.md", ProofreadPromptPath = "Proofread/Acme.md",
                ClientName = "Acme", ProfileName = "Acme",
                Termbases = Line("RA", "Acme Patents", 1, "2026-01-05 09:00:00") + "\n" + Line("RWP", "PROJ-001 glossary", 3, "2026-02-01 12:00:00"),
            };
            Overlay(ps, g, Local());
            Check(ps.MemoryBankName == "Acme bank" && ps.ActivePromptPath == "Translate/Acme.md" && ps.ActiveProofreadPromptPath == "Proofread/Acme.md" && ps.Client == "Acme",
                "bank, both prompts and client come from the bundle");
            Check(Ids(ps.DisabledTermbaseIds) == "2", "read: General off, the two listed on (" + Ids(ps.DisabledTermbaseIds) + ")");
            Check(Ids(ps.DisabledAiTermbaseIds) == "2,3", "AI: only Acme Patents sent (" + Ids(ps.DisabledAiTermbaseIds) + ")");
            Check(Ids(ps.WriteTermbaseIds) == "3" && ps.ProjectTermbaseId == 3, "write and project termbase: PROJ-001 glossary");
            Check(ps.AiTermbaseIdsInitialized && _reports.Count == 0, "nothing reported, AI choice counts as made");
            var back = ProjectProfile.FromSidecar(ps, g, Local(), null);
            Check(back.SameAs(g), "saving these unchanged writes nothing to the bundle (profile name kept although the sidecar has none)");
        }

        Console.WriteLine("B  sidecar only");
        {
            var ps = Sidecar();
            Overlay(ps, new ProjectProfileValues(), Local());
            var s = Sidecar();
            Check(ps.MemoryBankName == s.MemoryBankName && ps.ActivePromptPath == s.ActivePromptPath && ps.ActiveProofreadPromptPath == s.ActiveProofreadPromptPath && ps.Client == s.Client,
                "an empty bundle changes no text value");
            Check(Ids(ps.DisabledTermbaseIds) == Ids(s.DisabledTermbaseIds) && Ids(ps.DisabledAiTermbaseIds) == Ids(s.DisabledAiTermbaseIds)
                && Ids(ps.WriteTermbaseIds) == Ids(s.WriteTermbaseIds) && ps.ProjectTermbaseId == s.ProjectTermbaseId && ps.KnownTermbaseCeiling == 0,
                "nor any termbase choice");
            var g = ProjectProfile.FromSidecar(ps, new ProjectProfileValues(), Local(), null);
            Check(g.MemoryBankName == "Sidecar bank" && g.ClientName == "Sidecar client", "the next save gives the bundle the sidecar's values");
            Check(g.Termbases == Line("RWP", "PROJ-001 glossary", 3, "2026-02-01 12:00:00"),
                "termbases by name, only those with a choice on (" + g.Termbases.Replace("\t", "|") + ")");
        }

        Console.WriteLine("C  both, disagreeing");
        {
            var ps = Sidecar();
            var g = new ProjectProfileValues { MemoryBankName = "Group bank", TranslatePromptPath = "Translate/Group.md", Termbases = Line("R", "General", 2, "") };
            Overlay(ps, g, Local());
            Check(ps.MemoryBankName == "Group bank" && ps.ActivePromptPath == "Translate/Group.md", "bank and translate prompt: the bundle's");
            Check(ps.ActiveProofreadPromptPath == "Proofread/Sidecar.md" && ps.Client == "Sidecar client", "proofread prompt and client, which the bundle lacks: the sidecar's");
            Check(Ids(ps.DisabledTermbaseIds) == "1,3" && ps.WriteTermbaseIds.Count == 0 && ps.ProjectTermbaseId == -1,
                "termbases: the bundle's choices replace the sidecar's as a whole");
        }

        Console.WriteLine("D  memory bank that does not exist here");
        {
            var ps = Sidecar();
            var g = new ProjectProfileValues { MemoryBankName = "Bank from another translator" };
            Overlay(ps, g, Local());
            Check(!UserDataPath.ListMemoryBanks().Contains("Bank from another translator"), "(there is no such bank here)");
            Check(ps.MemoryBankName == "Bank from another translator", "the project names it, as stored");
            var back = ProjectProfile.FromSidecar(ps, g, Local(), null);
            Check(back.MemoryBankName == "Bank from another translator", "a save keeps it: never overwritten because it is missing here");
        }

        Console.WriteLine("E  prompt that does not exist here");
        {
            var ps = Sidecar();
            var g = new ProjectProfileValues { TranslatePromptPath = "Translate/Gone.md", ProofreadPromptPath = "Proofread/Gone.md" };
            Overlay(ps, g, Local());
            Check(ps.GetBatchPrompt(false) == "Translate/Gone.md" && ps.GetBatchPrompt(true) == "Proofread/Gone.md",
                "both prompts as stored (the Batch list shows the mode's default for a missing file: batch_prompt_pick_test H)");
            var back = ProjectProfile.FromSidecar(ps, g, Local(), null);
            Check(back.TranslatePromptPath == "Translate/Gone.md" && back.ProofreadPromptPath == "Proofread/Gone.md", "a save keeps them");
        }

        Console.WriteLine("F  termbase the bundle names but this computer lacks");
        {
            var other = Line("RWA", "Other translator's TB", 44, "2025-03-03 08:00:00");
            var g = new ProjectProfileValues { Termbases = Line("RA", "Acme Patents", 1, "2026-01-05 09:00:00") + "\n" + other };
            var ps = Sidecar();
            Overlay(ps, g, Local());
            Check(_reports.Count == 1 && _reports[0].Contains("Other translator's TB"), "reported (" + (_reports.Count > 0 ? _reports[0] : "nothing") + ")");
            Check(Ids(ps.DisabledTermbaseIds) == "2,3" && ps.WriteTermbaseIds.Count == 0, "off here; the rest as the bundle says");
            var same = ProjectProfile.FromSidecar(ps, g, Local(), null);
            Check(same.Termbases == g.Termbases, "a save without a change leaves the stored value exactly as it was");
            ps.WriteTermbaseIds.Add(2); ps.DisabledTermbaseIds.Remove(2);
            var changed = ProjectProfile.FromSidecar(ps, g, Local(), null);
            Check(changed.Termbases.Contains(other), "after a change to another termbase the missing one is still stored, line for line");
            Check(changed.Termbases.Contains(Line("RW", "General", 2, "2026-01-06 09:00:00")), "and the change is there");
            Check(changed.Termbases.Split('\n').Length == 3, "three lines, nothing doubled");
        }

        Console.WriteLine("G  renamed termbase");
        {
            var g = new ProjectProfileValues { Termbases = Line("RW", "Acme Patents (old name)", 1, "2026-01-05 09:00:00") };
            var ps = Sidecar();
            Overlay(ps, g, Local());
            Check(_reports.Count == 0 && Ids(ps.WriteTermbaseIds) == "1" && !ps.DisabledTermbaseIds.Contains(1), "found by id + creation time, its choices applied");
            var back = ProjectProfile.FromSidecar(ps, g, Local(), null);
            Check(back.Termbases == Line("RW", "Acme Patents", 1, "2026-01-05 09:00:00"), "the next save stores its new name");
            var elsewhere = new ProjectProfileValues { Termbases = Line("RW", "Acme Patents (old name)", 1, "2024-07-07 07:07:07") };
            ps = Sidecar();
            Overlay(ps, elsewhere, Local());
            Check(_reports.Count == 1 && ps.WriteTermbaseIds.Count == 0, "the same id from another database (other creation time) is not taken for it");
            var folded = new ProjectProfileValues { Termbases = Line("R", "acme patents", 9, "") };
            ps = Sidecar();
            Overlay(ps, folded, Local());
            Check(!ps.DisabledTermbaseIds.Contains(1), "a name differing only in case still finds it");
        }

        Console.WriteLine("H  legacy MultiTerm values");
        {
            var path = @"C:\Termbases\Acme.sdltb";
            long pathId = MultiTermProjectDetector.MakeStableSyntheticId(path);
            var g = new ProjectProfileValues { AiEnabledMultiTerm = "-12345\n" + path, DisabledMultiTerm = "-777" };
            var ps = Sidecar();
            Overlay(ps, g, Local());
            Check(Ids(ps.EnabledAiMultiTermIds) == Ids(new[] { -12345L, pathId }), "bare ids (up to 4.20.65) and paths both read");
            Check(Ids(ps.DisabledMultiTermIds) == "-777", "MultiTerm read switch-offs read");
            var back = ProjectProfile.FromSidecar(ps, g, Local(), ProjectProfile.MultiTermPaths(new[] { path }, g));
            Check(back.AiEnabledMultiTerm == g.AiEnabledMultiTerm && back.DisabledMultiTerm == g.DisabledMultiTerm, "the same sets are not rewritten");
            ps.EnabledAiMultiTermIds.Remove(-12345);
            var path2 = @"C:\Termbases\General.sdltb";
            ps.DisabledMultiTermIds.Add(MultiTermProjectDetector.MakeStableSyntheticId(path2));
            back = ProjectProfile.FromSidecar(ps, g, Local(), ProjectProfile.MultiTermPaths(new[] { path, path2 }, g));
            Check(back.AiEnabledMultiTerm == path, "a changed set is stored by path (" + back.AiEnabledMultiTerm + ")");
            Check(back.DisabledMultiTerm == "-777\n" + path2, "an id with no known path stays an id, beside the path (" + back.DisabledMultiTerm.Replace("\n", " | ") + ")");
            var viaBundleHelper = ProjectProfile.ParseMultiTermIds("  \n" + path + "\r\n" + path + "\n");
            Check(viaBundleHelper.Count == 1 && viaBundleHelper[0] == pathId, "blank lines and a doubled path give one id");
        }

        Console.WriteLine("I  empty and corrupt bundle values");
        {
            var ps = Sidecar();
            var g = new ProjectProfileValues { MemoryBankName = "   ", TranslatePromptPath = null, ClientName = "\r\n", Termbases = "this is not a line\nXYZ\tName\t1\n\t\t\n" };
            bool threw = false;
            try { Overlay(ps, g, Local()); } catch (Exception ex) { threw = true; Console.WriteLine("    " + ex); }
            Check(!threw, "no throw");
            Check(ps.MemoryBankName == "Sidecar bank" && ps.ActivePromptPath == "Translate/Sidecar.md" && ps.Client == "Sidecar client", "blank values count as none: the sidecar's");
            Check(Ids(ps.DisabledTermbaseIds) == "1,2" && Ids(ps.WriteTermbaseIds) == "3", "unreadable termbase choices switch nothing off: the sidecar's stand");
            Check(_reports.Any(r => r.Contains("cannot be read")), "and that is reported");
            var back = ProjectProfile.FromSidecar(ps, g, Local(), null);
            Check(back.Termbases.Contains("this is not a line") && back.Termbases.Contains("XYZ\tName\t1"), "a save keeps the unreadable lines beside its own");
            try { ProjectProfile.Overlay(ps, null, null, null); ProjectProfile.Overlay(null, g, null, null); ProjectProfile.FromSidecar(null, null, null, null); threw = false; }
            catch { threw = true; }
            Check(!threw, "null bundle, null settings, null termbase list: no throw");
            Check(ProjectBundleSettings.ReadValues(null) == null && !ProjectBundleSettings.WriteValues(null, g), "no bundle: nothing read, nothing written");
            var noDb = Sidecar();
            Overlay(noDb, new ProjectProfileValues { Termbases = Line("R", "General", 2, "") }, new List<TermbaseIdentity>());
            Check(Ids(noDb.DisabledTermbaseIds) == "1,2" && Ids(noDb.WriteTermbaseIds) == "3", "no termbase database here: choices left alone, never read as all off");
            var keptNoDb = ProjectProfile.FromSidecar(noDb, new ProjectProfileValues { Termbases = "R\tGeneral\t2\t" }, new List<TermbaseIdentity>(), null);
            Check(keptNoDb.Termbases == "R\tGeneral\t2\t", "and a save then leaves the stored choices alone");
        }

        Console.WriteLine("J  template -> new project -> template, through Studio settings bundles");
        Guard(RunBundleRoundTrip);

        Console.WriteLine("K  ProjectSettings Load/Save with the cache (outside Studio: no UI thread)");
        Guard(() => RunLoadSave(temp));

        Console.WriteLine("L  client carried through Settings OK");
        {
            var path = Path.Combine(temp, "client", "PROJ-001.sdlproj");
            ProjectSettings.Save(path, new ProjectSettings { ProjectPath = path, ProjectName = "PROJ-001", Client = "Acme", MemoryBankName = "Acme bank" });
            var global = new TermLensSettings(); global.AiSettings = new AiSettings();
            var extracted = global.ExtractProjectSettings(path, "PROJ-001");
            Check(extracted.Client == "Acme" && extracted.MemoryBankName == "Acme bank", "ExtractProjectSettings keeps the client (it used to blank it)");
        }

        Console.WriteLine("M  highest-numbered termbase on, in no list");
        {
            var g = new ProjectProfileValues { Termbases = Line("RA", "PROJ-001 glossary", 3, "2026-02-01 12:00:00") };
            var ps = Sidecar();
            Overlay(ps, g, Local());
            var global = new TermLensSettings(); global.AiSettings = new AiSettings();
            global.ApplyProjectOverlay(ps, new List<long> { 1, 2, 3 });
            Check(!global.DisabledTermbaseIds.Contains(3) && !global.AiSettings.DisabledAiTermbaseIds.Contains(3),
                "the bundle's choice decides: it stays on, for reading and for the AI");
            ps.KnownTermbaseCeiling = 0;
            var old = new TermLensSettings(); old.AiSettings = new AiSettings();
            old.ApplyProjectOverlay(ps, new List<long> { 1, 2, 3 });
            Check(old.DisabledTermbaseIds.Contains(3), "(from the sidecar alone it is switched off: the #103 ceiling, unchanged for sidecar-only projects)");
        }
    }

    static void RunBundleRoundTrip() {
        Sdl.Core.Settings.ISettingsBundle template;
        try { template = Sdl.Core.Settings.SettingsUtil.CreateSettingsBundle(null); }
        catch (Exception ex) { Console.WriteLine("  SKIP Studio settings bundles cannot be made outside Studio here: " + ex.Message); return; }

        var v = new ProjectProfileValues {
            MemoryBankName = "Acme bank", TranslatePromptPath = "Translate/Acme patents.md", ProofreadPromptPath = "Proofread/Acme.md",
            ClientName = "Acme", ProfileName = "Acme",
            Termbases = Line("RA", "Acme Patents", 1, "2026-01-05 09:00:00") + "\n" + Line("RWP", "PROJ-001 glossary", 3, "2026-02-01 12:00:00"),
            AiEnabledMultiTerm = @"C:\Termbases\Acme.sdltb",
        };
        Check(ProjectBundleSettings.WriteValues(template, v), "values written into a template's bundle");
        Check(!ProjectBundleSettings.WriteValues(template, v), "writing the same again reports no change (no needless project save)");
        Check(ProjectBundleSettings.ReadValues(template).SameAs(v), "read back from the same bundle");

        // The .sdltpl and the .sdlproj hold the bundle as XML; Studio copies the whole bundle.
        var xml = ToXml(template);
        Check(xml.Contains("SupervertalerProjectSettingsGroup") && xml.Contains("Acme bank") && xml.Contains("TranslatePromptPath"),
            "the group and its values are in the bundle's XML");
        var project = FromXml(xml);
        Check(ProjectBundleSettings.ReadValues(project).SameAs(v), "a new project's bundle read from that XML holds the same values");
        var assigned = Sdl.Core.Settings.SettingsUtil.CreateSettingsBundle(null);
        assigned.Assign(template);
        Check(ProjectBundleSettings.ReadValues(assigned).SameAs(v), "and so does a bundle assigned from the template's");

        // On another computer: the same termbases by name, with other ids.
        var there = new List<TermbaseIdentity> {
            new TermbaseIdentity(7, "PROJ-001 glossary", "2026-03-01 10:00:00"),
            new TermbaseIdentity(8, "Acme Patents", "2026-03-01 10:00:01"),
            new TermbaseIdentity(9, "Somebody else's", "2026-03-01 10:00:02"),
        };
        var ps = new ProjectSettings { DisabledTermbaseIds = new List<long>(), DisabledAiTermbaseIds = new List<long>(), WriteTermbaseIds = new List<long>(), ProjectTermbaseId = -1 };
        Overlay(ps, ProjectBundleSettings.ReadValues(project), there);
        Check(Ids(ps.DisabledTermbaseIds) == "9" && Ids(ps.DisabledAiTermbaseIds) == "7,9" && Ids(ps.WriteTermbaseIds) == "7" && ps.ProjectTermbaseId == 7,
            "the new project on another computer: termbases found by name, under that computer's ids");
        Check(ps.MemoryBankName == "Acme bank" && ps.ActivePromptPath == "Translate/Acme patents.md" && ps.Client == "Acme", "bank, prompt and client arrive");
        var unchanged = ProjectProfile.FromSidecar(ps, ProjectBundleSettings.ReadValues(project), there, null);
        Check(unchanged.SameAs(ProjectBundleSettings.ReadValues(project)), "opening it there writes nothing back (names match; the other computer's ids are not churned in)");

        // A change there, then "Create project template" from that project: the new template carries it.
        ps.ProjectTermbaseId = 8;
        var changed = ProjectProfile.FromSidecar(ps, ProjectBundleSettings.ReadValues(project), there, null);
        Check(ProjectBundleSettings.WriteValues(project, changed), "a change is written into the project's bundle");
        var template2 = FromXml(ToXml(project));
        var tv = ProjectBundleSettings.ReadValues(template2);
        Check(tv.Termbases.Contains(Line("RPA", "Acme Patents", 8, "2026-03-01 10:00:01")) && tv.ProfileName == "Acme",
            "a template made from that project carries the change and the profile name");

        // Other groups in the bundle are untouched.
        Check(!ProjectBundleSettings.ReadValues(Sdl.Core.Settings.SettingsUtil.CreateSettingsBundle(null)).SameAs(v)
              && ProjectBundleSettings.ReadValues(Sdl.Core.Settings.SettingsUtil.CreateSettingsBundle(null)).MemoryBankName == "",
            "a bundle without the group reads as all empty");
    }

    static string ToXml(Sdl.Core.Settings.ISettingsBundle b) {
        var sw = new StringWriter();
        using (var w = System.Xml.XmlWriter.Create(sw)) Sdl.Core.Settings.SettingsUtil.SerializeSettingsBundle(w, b);
        return sw.ToString();
    }
    static Sdl.Core.Settings.ISettingsBundle FromXml(string xml) {
        using (var r = System.Xml.XmlReader.Create(new StringReader(xml))) {
            r.MoveToContent();
            return Sdl.Core.Settings.SettingsUtil.DeserializeSettingsBundle(r, null);
        }
    }

    static void RunLoadSave(string temp) {
        var store = typeof(ProjectProfileStore);
        var prime = store.GetMethod("Prime", BindingFlags.NonPublic | BindingFlags.Static);

        // A real termbase database in the temp folder.
        var db = Path.Combine(temp, "resources", "supervertaler.db");
        Directory.CreateDirectory(Path.GetDirectoryName(db));
        TermbaseReader.CreateDatabase(db);
        long acme = TermbaseReader.CreateTermbase(db, "Acme Patents", "en", "nl");
        long general = TermbaseReader.CreateTermbase(db, "General", "en", "nl");
        List<TermbaseIdentity> catalog;
        using (var r = new TermbaseReader(db)) { r.Open(); catalog = r.GetTermbaseIdentities(); }
        Check(catalog.Count == 2 && catalog.All(t => t.Created.Length > 0), "the database lists its termbases with name, id and creation time (" + string.Join("; ", catalog.Select(t => t.Id + " " + t.Name + " " + t.Created)) + ")");

        var path = Path.Combine(temp, "Projects", "PROJ-001", "PROJ-001.sdlproj");
        var group = new ProjectProfileValues {
            MemoryBankName = "Acme bank", TranslatePromptPath = "Translate/Acme.md", ClientName = "Acme",
            Termbases = ProjectProfile.FormatTermbase(catalog.First(t => t.Id == acme), true, true, false, true),
        };
        prime.Invoke(null, new object[] { path, "11111111-2222-3333-4444-555555555555", group, catalog, new List<string>() });

        // The project as Studio would leave it after opening: the sidecar mirrors the bundle.
        var ps = new ProjectSettings { ProjectPath = path, ProjectName = "PROJ-001", TermbasePath = db,
            DisabledTermbaseIds = new List<long> { general }, DisabledAiTermbaseIds = new List<long> { general },
            WriteTermbaseIds = new List<long> { acme }, ProjectTermbaseId = -1, AiTermbaseIdsInitialized = true,
            MemoryBankName = "Acme bank", ActivePromptPath = "Translate/Acme.md", Client = "Acme" };
        ProjectSettings.Save(path, ps);
        Check(!ps.ProfileSyncPending, "a save that matches the bundle needs no bundle write, so nothing is pending");

        var loaded = ProjectSettings.Load(path);
        Check(loaded.MemoryBankName == "Acme bank" && Ids(loaded.WriteTermbaseIds) == acme.ToString(), "Load gives the effective values");

        // A change that cannot reach the bundle now (outside Studio there is no UI thread).
        loaded.MemoryBankName = "Changed bank";
        ProjectSettings.Save(path, loaded);
        Check(loaded.ProfileSyncPending, "the bundle cannot be written: the save marks the file");
        var after = ProjectSettings.Load(path);
        Check(after.ProfileSyncPending && after.MemoryBankName == "Changed bank", "and the next Load gives the change, not the bundle's older bank");

        // The bundle changed meanwhile (a template's values arrive): the bundle wins once the file is in step.
        after.MemoryBankName = "Acme bank";
        ProjectSettings.Save(path, after);
        Check(!after.ProfileSyncPending, "back in step with the bundle: no longer marked");
        prime.Invoke(null, new object[] { path, "11111111-2222-3333-4444-555555555555",
            new ProjectProfileValues { MemoryBankName = "Bundle bank", Termbases = group.Termbases }, catalog, new List<string>() });
        Check(ProjectSettings.Load(path).MemoryBankName == "Bundle bank", "then the bundle's value wins over the file's");

        // A project nobody has read the bundle of (not open in Studio).
        var other = Path.Combine(temp, "Projects", "PROJ-002", "PROJ-002.sdlproj");
        var ops = new ProjectSettings { ProjectPath = other, ProjectName = "PROJ-002", MemoryBankName = "Acme bank" };
        ProjectSettings.Save(other, ops);
        Check(ops.ProfileSyncPending && ProjectSettings.Load(other).MemoryBankName == "Acme bank", "a save for a project not open is marked, and read back as saved");

        // A termbase created and switched on after the project was read.
        long fresh = TermbaseReader.CreateTermbase(db, "PROJ-001 new", "en", "nl");
        var withNew = ProjectSettings.Load(path);
        withNew.DisabledTermbaseIds.Remove(fresh);
        withNew.WriteTermbaseIds.Add(fresh);
        withNew.MemoryBankName = "Bundle bank";
        // Compare what would be written with the database as it is now:
        List<TermbaseIdentity> now;
        using (var r = new TermbaseReader(db)) { r.Open(); now = r.GetTermbaseIdentities(); }
        var desired = ProjectProfile.FromSidecar(withNew, new ProjectProfileValues { MemoryBankName = "Bundle bank", Termbases = group.Termbases }, now, null);
        Check(desired.Termbases.Contains("PROJ-001 new"), "a termbase created since the project was read is named in what a save writes");

        // Renamed in the database, same id and creation time.
        TermbaseReader.RenameTermbase(db, acme, "Acme Patents 2026");
        using (var r = new TermbaseReader(db)) { r.Open(); now = r.GetTermbaseIdentities(); }
        var rps = new ProjectSettings { DisabledTermbaseIds = new List<long>(), DisabledAiTermbaseIds = new List<long>(), WriteTermbaseIds = new List<long>(), ProjectTermbaseId = -1 };
        ProjectProfile.Overlay(rps, group, now, null);
        Check(Ids(rps.WriteTermbaseIds) == acme.ToString(), "a termbase renamed in the real database is still found");

        // The settings files went where they should: the temp data folder.
        Check(Directory.GetFiles(UserDataPath.ProjectsDir, "*.json").Length == 2, "two sidecar files, in the temp data folder");
    }
}
'@

[ProjectProfileTest]::Install([string[]]@((Split-Path $Dll), $studio))
try {
    [ProjectProfileTest]::Run($temp)
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
$after = Snap $real
$same = (Compare-Object $before $after) -eq $null
if ($same) { [ProjectProfileTest]::Pass++ ; Write-Host "  ok   real settings, diagnostic log, termbase database and project files unchanged" }
else { [ProjectProfileTest]::Fail++ ; Write-Host "  FAIL real files changed:"; Compare-Object $before $after | Out-Host }
Write-Host ""
Write-Host ("{0} passed, {1} failed" -f [ProjectProfileTest]::Pass, [ProjectProfileTest]::Fail)
if ([ProjectProfileTest]::Fail -gt 0) { exit 1 }
