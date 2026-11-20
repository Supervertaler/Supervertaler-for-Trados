# Job profiles, step 2 (20.209): applying a Studio project template ("job profile") to an
# existing project, and Studio's customer list. Checked against the built plugin DLL,
# outside Studio, with the real JobProfile rules, the real template reader (Studio's own
# Sdl.Core.Settings deserialiser for the settings bundle) and the real template listing:
#
#   A  customer names: alphabetical by the user's language ignoring case, stable; the same
#      name ignoring case and spaces is the same customer
#   B  a customer name is refused when empty, on more than one line, or already in the list
#      (also in other capitals); a rename may change only the capitals of its own name
#   C  the question before removing a customer: how many projects use it (0, 1, many, unknown)
#   D  a template is read: Supervertaler's set-up and Studio's customer from its settings
#      bundle, its TM lists (all pairs and per pair), termbases, indexes, recognition options
#   E  a template that cannot be read (gone, folder gone, locked, not XML, not a template,
#      settings unreadable) gives a message naming the file, and nothing
#   F  TMs, keep and add: the project's lists stay as they are, in order; the template's TMs
#      a list lacks are added at the end; one in both is "already in the project"; a missing
#      file TM, a bad address and a second line are left out and said; applying twice adds nothing
#   G  termbases, keep and add: the project's stay; the template's it lacks are added; missing
#      and server termbases left out and said; only indexes for unmapped languages are added
#   H  the customer: none named, already set, set (list's spelling), ask to add, list unreadable
#   I  Supervertaler's values: the template's wherever it has one, the project's otherwise
#   J  the words: the preview table, the question before applying, and the summary, with the TM reopen hint exactly when TMs changed
#   K  the template list (Studio's registry plus folders), and scale: 400 templates listed,
#      500 customers sorted, a template with 300 TMs read and planned, all well under a second
#   L  failure paths outside Studio: applying with no project returns a failed step, never throws
#   M  TMs used in reverse
#   N  step 4, what a job profile holds: every part, TM switches, missing files, pair lists
#      (ignored ones hidden, empty ones shown), a file check that throws, and 1,000 TMs in well under a second
#   O  step 4, editing a template: termbase rows (this computer's, alphabetical; renamed ones found;
#      others and unreadable lines kept as they are), one Project termbase, the prompt and bank lists
#      (with a stored one this computer lacks), the edited values, new template names, and 2,000 termbases
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\job_profile_step2_test.ps1
#   ... -Dll <path to Supervertaler.Trados.dll>   (default: this repo's build)
# 32-bit builds (Studio 2024 / 2022) need the 32-bit PowerShell:
#   C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\job_profile_step2_test.ps1 -Dll <...\bin\Studio18\Release\Supervertaler.Trados.dll>
#
# Everything is written under a data folder in %TEMP% (SupervertalerPaths.Set), deleted
# afterwards; the real settings, diagnostic log and Studio's projects.xml files are checked
# unchanged.
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

# Real files that must not change: Supervertaler's settings and log, and every Studio's projects.xml.
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
$docs = [Environment]::GetFolderPath('MyDocuments')
Get-ChildItem -LiteralPath $docs -Directory -Filter 'Studio *' -ErrorAction SilentlyContinue | ForEach-Object {
    $real += (Join-Path $_.FullName "Projects\projects.xml")
}
function Snap($paths) { $paths | ForEach-Object { if (Test-Path -LiteralPath $_) { $i = Get-Item -LiteralPath $_; "$_|$($i.LastWriteTimeUtc.Ticks)|$(if ($i.PSIsContainer) { (Get-ChildItem -LiteralPath $_ | Measure-Object).Count } else { $i.Length })" } else { "$_|absent" } } }
$before = Snap $real

$temp = Join-Path $env:TEMP ("sv-jobprofile-test-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $temp | Out-Null

[void][Reflection.Assembly]::LoadFrom($Dll)
$sdlSettings = Join-Path $studio "Sdl.Core.Settings.dll"
Add-Type -ReferencedAssemblies @('System.Runtime.Serialization', 'System.Xml', 'System.Xml.Linq', $Dll, $sdlSettings) -TypeDefinition @'
using System; using System.IO; using System.Linq; using System.Reflection; using System.Collections.Generic;
using System.Diagnostics; using System.Globalization; using System.Text; using System.Threading; using System.Xml.Linq;
using Supervertaler.Core;
using Supervertaler.Trados.Core; using Supervertaler.Trados.Settings;

public static class JobProfileStep2Test {
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
    static void Guard(Action a) {
        try { a(); } catch (Exception ex) { Fail++; Console.WriteLine("  FAIL threw: " + (ex.InnerException ?? ex)); }
    }
    static KeyValuePair<Guid, string> C(string name) { return new KeyValuePair<Guid, string>(Guid.NewGuid(), name); }

    static string _temp;

    public static void Run(string temp) {
        _temp = temp;
        SupervertalerPaths.Set(temp);
        Check(string.Equals(SupervertalerPaths.Root, temp, StringComparison.OrdinalIgnoreCase), "data folder is the temp folder");

        Console.WriteLine("A  customer names in order");
        Guard(Names);
        Console.WriteLine("B  customer names refused");
        Guard(Refused);
        Console.WriteLine("C  removing a customer");
        Guard(Removing);
        Console.WriteLine("D  reading a template");
        Guard(Reading);
        Console.WriteLine("E  templates that cannot be read");
        Guard(Unreadable);
        Console.WriteLine("F  TMs");
        Guard(Tms);
        Console.WriteLine("G  termbases");
        Guard(Termbases);
        Console.WriteLine("H  the customer");
        Guard(Customer);
        Console.WriteLine("I  Supervertaler's values");
        Guard(Merge);
        Console.WriteLine("J  the words");
        Guard(Words);
        Console.WriteLine("K  the template list and scale");
        Guard(ListAndScale);
        Console.WriteLine("L  failure paths outside Studio");
        Guard(OutsideStudio);
        Console.WriteLine("M  TMs used in reverse");
        Guard(Reversed);
        Console.WriteLine("N  what a project template holds (step 4)");
        Guard(Contents);
        Console.WriteLine("O  editing a project template (step 4)");
        Guard(Editing);
    }

    // --- M: TMs Studio uses in reverse (Michael, 20 Nov: an en-GB -> nl-NL TM in a nl-NL -> en-GB project) ---
    static void Reversed() {
        var one = new List<string> { "en-GB" };
        Check(TmFieldStamping.IsReversed("en-GB", "nl-NL", "nl-NL", one), "a TM the other way round is reversed");
        Check(TmFieldStamping.IsReversed("EN-gb", "nl-nl", "nl-NL", one), "languages compare ignoring case");
        Check(!TmFieldStamping.IsReversed("nl-NL", "en-GB", "nl-NL", one), "a TM the same way round is not");
        Check(!TmFieldStamping.IsReversed("en-US", "nl-NL", "nl-NL", one), "another variant is not called reversed (only a clear swap is)");
        Check(!TmFieldStamping.IsReversed("en-GB", "nl-NL", "nl-NL", new List<string> { "en-GB", "de-DE" }),
            "the list for all pairs: reversed only when it is reversed for every target the project has");
        Check(!TmFieldStamping.IsReversed(null, "nl-NL", "nl-NL", one) && !TmFieldStamping.IsReversed("en-GB", "nl-NL", "", one)
              && !TmFieldStamping.IsReversed("en-GB", "nl-NL", "nl-NL", new List<string>()) && !TmFieldStamping.IsReversed("en-GB", "nl-NL", "nl-NL", null),
            "anything unknown is not reversed");

        // The Apply table: an added TM that runs the other way is marked, and said once.
        var t = Read(FullTemplate());
        var f = Facts();
        f.Source = "nl-NL"; f.Targets = new List<string> { "en-GB" };
        f.TmsPerTarget.Clear();
        PreparedProfile p;
        Check(JobProfile.Prepare(FullTemplate(), f, File.Exists, JobProfile.ReadBundleWithStudio, out p) == null, "prepared for a nl-NL -> en-GB project");
        var acmePath = p.Tms.AllPairs.Add.First(e => e.Main.DisplayName.StartsWith("Acme (")).Main.FilePath;
        JobProfile.FindReversed(p, f, path => string.Equals(path, acmePath, StringComparison.OrdinalIgnoreCase)
            ? (ValueTuple<string, string>?)new ValueTuple<string, string>("en-GB", "nl-NL") : null);
        Check(p.Reversed.Count == 1 && p.Reversed.ContainsKey(acmePath), "the TM the other way round is found; TMs whose languages are unknown are not");
        List<string> notes;
        var rows = JobProfile.Rows(p, f, out notes);
        var acme = rows.First(r => r.Group == JobProfile.GroupTms && r.Item.StartsWith("Acme ("));
        Check(acme.Action.EndsWith("; used in reverse") && acme.Changes, "table: the added TM says it is used in reverse");
        Check(notes.Count(n => n.Contains("the other way round") && n.Contains("adds nothing to it")) == 1, "and a note says what that means, once");
        Check(rows.Where(r => r.Group == JobProfile.GroupTms && r != acme).All(r => !r.Action.Contains("reverse")), "no other TM is marked");

        // A failure reading a TM's languages marks nothing and stops nothing.
        PreparedProfile p2;
        JobProfile.Prepare(FullTemplate(), f, File.Exists, JobProfile.ReadBundleWithStudio, out p2);
        JobProfile.FindReversed(p2, f, path => { if (path != null) throw new IOException("locked"); return null; });
        Check(p2.Reversed.Count == 0, "a TM that cannot be read is not marked, and the rest goes on");
        JobProfile.FindReversed(null, f, path => (ValueTuple<string, string>?)null);
        JobProfile.FindReversed(p2, null, path => (ValueTuple<string, string>?)null);
        Check(true, "no plan or no project: nothing happens, no throw");

    }

    // ─── A ──────────────────────────────────────────────────────────
    static void Names() {
        var saved = Thread.CurrentThread.CurrentCulture;
        try {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("en-GB");
            var input = new[] { "beta", "Acme", "Zeta", "acme b.v.", "\u00C5ngstr\u00F6m Ltd", "alpha" };
            var sorted = JobProfile.SortByName(input, s => s);
            Check(string.Join("|", sorted) == "Acme|acme b.v.|alpha|\u00C5ngstr\u00F6m Ltd|beta|Zeta", "by language, ignoring case: " + string.Join(" | ", sorted));
            var same = JobProfile.SortByName(new[] { "acme", "Acme" }, s => s);
            var again = JobProfile.SortByName(new[] { "Acme", "acme" }, s => s);
            Check(string.Join("|", same) == string.Join("|", again), "two names that differ only in case come out in one fixed order");
            var stable = JobProfile.SortByName(new[] { "x1", "x2", "x3" }.Select(s => new { Name = "same", Tag = s }), x => x.Name);
            Check(string.Join("", stable.Select(x => x.Tag)) == "x1x2x3", "equal names keep their order");
            Thread.CurrentThread.CurrentCulture = new CultureInfo("sv-SE");
            var swedish = JobProfile.SortByName(new[] { "\u00C5ngstr\u00F6m", "Zeta" }, s => s);
            Check(swedish[0] == "Zeta", "the user's language decides (Swedish puts \u00C5 after Z)");
        } finally { Thread.CurrentThread.CurrentCulture = saved; }
        Check(JobProfile.SameCustomerName(" Acme ", "ACME") && !JobProfile.SameCustomerName("Acme", "Acme Ltd"), "the same customer: ignoring case and spaces, nothing more");
        Check(JobProfile.SameCustomerName(null, "") , "no name equals an empty name");
    }

    // ─── B ──────────────────────────────────────────────────────────
    static void Refused() {
        var list = new List<KeyValuePair<Guid, string>> { C("Acme"), C("PROJ-001 client") };
        Check(JobProfile.CheckCustomerName("  ", list) != null, "an empty name is refused");
        Check(JobProfile.CheckCustomerName("Two\nlines", list) != null, "a name on two lines is refused");
        var dup = JobProfile.CheckCustomerName("Acme", list);
        Check(dup != null && dup.Contains("\"Acme\""), "a name already in the list is refused, naming it: " + dup);
        var dupCase = JobProfile.CheckCustomerName(" acme ", list);
        Check(dupCase != null && dupCase.Contains("capitals"), "also in other capitals, saying why: " + dupCase);
        Check(JobProfile.CheckCustomerName("Acme Ltd", list) == null, "a new name is accepted");
        Check(JobProfile.CheckCustomerName("ACME", list, list[0].Key) == null, "a rename may change only the capitals of its own name");
        Check(JobProfile.CheckCustomerName("Acme", list, list[1].Key) != null, "a rename to another customer's name is refused");
    }

    // ─── C ──────────────────────────────────────────────────────────
    static void Removing() {
        var none = JobProfile.RemoveQuestion("Acme", 0);
        var one = JobProfile.RemoveQuestion("Acme", 1);
        var many = JobProfile.RemoveQuestion("Acme", 12);
        var unknown = JobProfile.RemoveQuestion("Acme", -1);
        Check(none.Contains("No project"), "no project uses it: said so");
        Check(one.Contains("1 project in") && one.Contains("keep it"), "one project: said, and that it keeps its customer");
        Check(many.Contains("12 projects") && many.Contains("Studio will no longer offer it"), "many projects: the number, and what removing means");
        Check(unknown.Contains("could not check"), "when projects cannot be counted: said so, not a guess");
    }

    // ─── fixtures ───────────────────────────────────────────────────
    static string FileTm(string name) {
        var p = Path.Combine(_temp, "TMs", name + ".sdltm");
        Directory.CreateDirectory(Path.GetDirectoryName(p));
        if (!File.Exists(p)) File.WriteAllText(p, "fixture");
        return p;
    }
    static string TmUri(string path) { return "sdltm.file:///" + Uri.EscapeUriString(path.Replace('\\', '/')); }
    static string Termbase(string name) {
        var p = Path.Combine(_temp, "Termbases", name + ".sdltb");
        Directory.CreateDirectory(Path.GetDirectoryName(p));
        if (!File.Exists(p)) File.WriteAllText(p, "fixture");
        return p;
    }

    static string Entry(string uri, bool update, bool lookup, int penalty, string state) {
        return "<CascadeEntryItem PerformConcordanceSearch=\"true\" Penalty=\"" + penalty + "\" PerformUpdate=\"" + (update ? "true" : "false") + "\" PerformNormalSearch=\"" + (lookup ? "true" : "false") + "\">"
             + "<MainTranslationProviderItem Uri=\"" + System.Security.SecurityElement.Escape(uri) + "\"" + (state == null ? "" : " State=\"" + state + "\"") + " Enabled=\"true\" /></CascadeEntryItem>";
    }
    static string TermbaseXml(string name, string path, bool local) {
        var settings = "<?xml version=\"1.0\" encoding=\"utf-16\"?><TermbaseSettings xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns=\"http://schemas.datacontract.org/2004/07/Sdl.ProjectApi.TermbaseApi\"><Path>"
            + System.Security.SecurityElement.Escape(path) + "</Path><IsOpen>false</IsOpen><Filter>-1</Filter><Local>" + (local ? "true" : "false") + "</Local></TermbaseSettings>";
        return "<Termbases><Name>" + name + "</Name><SettingsXml>" + System.Security.SecurityElement.Escape(settings) + "</SettingsXml><Enabled>true</Enabled></Termbases>";
    }

    /// The project settings bundle as Studio writes it, with Supervertaler's group and the customer.
    static string BundleXml(bool withSupervertaler, string customer) {
        var b = Sdl.Core.Settings.SettingsUtil.CreateSettingsBundle(null);
        if (withSupervertaler)
            ProjectBundleSettings.WriteValues(b, new ProjectProfileValues { MemoryBankName = "Acme bank", TranslatePromptPath = "Translate/Acme.md", ClientName = "Acme", ProfileName = "Acme patents" });
        if (customer != null) {
            var g = b.GetSettingsGroup("GeneralProjectInfoSettings");
            g.GetSetting<string>("CustomerId", "").Value = customer;
        }
        var sb = new StringBuilder();
        var ws = new System.Xml.XmlWriterSettings { OmitXmlDeclaration = true };
        using (var w = System.Xml.XmlWriter.Create(sb, ws)) Sdl.Core.Settings.SettingsUtil.SerializeSettingsBundle(w, b);
        return sb.ToString();
    }

    static string Template(string name, string allPairs, string perPair, string termbases, string bundle) {
        var g = Guid.NewGuid().ToString();
        var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><ProjectTemplate Guid=\"" + Guid.NewGuid() + "\" SettingsBundleGuid=\"" + g + "\" Version=\"4.0.0.0\">"
            + "<LanguageDirections>" + (perPair ?? "") + "</LanguageDirections>"
            + (termbases ?? "")
            + "<SettingsBundles><SettingsBundle Guid=\"" + Guid.NewGuid() + "\"><SettingsBundle /></SettingsBundle>"
            + (bundle == null ? "" : "<SettingsBundle Guid=\"" + g + "\">" + bundle + "</SettingsBundle>") + "</SettingsBundles>"
            + "<CascadeItem OverrideParent=\"false\" StopSearchingWhenResultsFound=\"false\">" + (allPairs ?? "") + "</CascadeItem>"
            + "<GeneralProjectTemplateInfo Description=\"\" /></ProjectTemplate>";
        var p = Path.Combine(_temp, "Templates", name + ".sdltpl");
        Directory.CreateDirectory(Path.GetDirectoryName(p));
        File.WriteAllText(p, xml, Encoding.UTF8);
        return p;
    }

    static string FullTemplate() {
        var acme = FileTm("Acme (en-GB-nl-NL)");
        var general = FileTm("General");
        var gone = Path.Combine(_temp, "TMs", "Gone.sdltm");
        var all = Entry(TmUri(acme), true, true, 0, null)
                + Entry(TmUri(gone), false, true, 0, null)
                + Entry("sdltm.https://tms.example.com/acme/Acme%20server?orgPath=/", false, true, 1, "")
                + Entry("googletranslationprovider:///", false, true, 0, "key-state")
                + Entry(TmUri(acme), false, true, 0, null)              // the same TM twice
                + Entry("not a uri", false, true, 0, null)
                + Entry(TmUri(general), true, false, -3, null);         // update without lookup, negative penalty
        var perPair = "<LanguageDirection Guid=\"" + Guid.NewGuid() + "\" SettingsBundleGuid=\"" + Guid.NewGuid() + "\" TargetLanguageCode=\"nl-NL\" SourceLanguageCode=\"en-GB\">"
                    + "<CascadeItem OverrideParent=\"true\" StopSearchingWhenResultsFound=\"true\">" + Entry(TmUri(general), false, true, 2, null) + Entry(TmUri(FileTm("Acme NL")), true, true, 0, null) + "</CascadeItem></LanguageDirection>"
                    + "<LanguageDirection Guid=\"" + Guid.NewGuid() + "\" SettingsBundleGuid=\"" + Guid.NewGuid() + "\" TargetLanguageCode=\"fr-FR\" SourceLanguageCode=\"de-DE\">"
                    + "<CascadeItem OverrideParent=\"true\" StopSearchingWhenResultsFound=\"false\">" + Entry(TmUri(acme), false, true, 0, null) + "</CascadeItem></LanguageDirection>";
        var tbs = "<TermbaseConfiguration>"
                + TermbaseXml("Acme terms", Termbase("Acme terms"), true)
                + TermbaseXml("Gone terms", Path.Combine(_temp, "Termbases", "Gone.sdltb"), true)
                + TermbaseXml("Server terms", "https://tms.example.com\\%\\Server terms", false)
                + "<LanguageIndexMappings><Language>en-GB</Language><Index>English</Index></LanguageIndexMappings>"
                + "<LanguageIndexMappings><Language>nl-NL</Language><Index>Dutch</Index></LanguageIndexMappings>"
                + "<RecognitionOptions><ShowWithNoAvailableTranslation>true</ShowWithNoAvailableTranslation><MinimumMatchValue>80</MinimumMatchValue><SearchDepth>150</SearchDepth><SearchOrder>Parallel</SearchOrder></RecognitionOptions>"
                + "</TermbaseConfiguration>";
        return Template("Acme patents EN-NL", all, perPair, tbs, BundleXml(true, "Acme"));
    }

    static ProjectFacts Facts() {
        return new ProjectFacts {
            Name = "PROJ-001", Path = @"C:\Projects\PROJ-001\PROJ-001.sdlproj", Source = "en-GB",
            Targets = new List<string> { "nl-NL", "de-DE", "fr-FR" },
            TermbaseIndexes = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("de-DE", "German"), new KeyValuePair<string, string>("nl-NL", "Nederlands") },
            CustomerName = "", CustomerNames = new List<string> { "acme", "Other client" },
            TmsAllPairs = new TmListFacts { Providers = new List<TemplateProvider> {
                new TemplateProvider { Uri = TmUri(FileTm("Own TM")) },
                new TemplateProvider { Uri = TmUri(FileTm("General").Replace("TMs", "tms")) } } },
            TmsPerTarget = new Dictionary<string, TmListFacts>(StringComparer.OrdinalIgnoreCase) {
                { "nl-NL", new TmListFacts() },
                { "de-DE", new TmListFacts { Override = true, Providers = new List<TemplateProvider> { new TemplateProvider { Uri = TmUri(FileTm("Own DE")) } } } },
                { "fr-FR", new TmListFacts() } },
            Termbases = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("Own terms", JobProfile.TermbaseKey(true, Termbase("Own terms"))) },
        };
    }

    // N: what a job profile holds, with no project to compare it with
    static PreviewRow One(List<PreviewRow> rows, string group, string itemStart) {
        return rows.FirstOrDefault(r => r.Group == group && r.Item.StartsWith(itemStart, StringComparison.Ordinal));
    }
    static void Contents() {
        List<string> notes;
        var rows = JobProfile.ContentRows(Read(FullTemplate()), File.Exists, out notes);
        var sv = rows.Where(r => r.Group == JobProfile.GroupSupervertaler).ToList();
        Check(sv.Any(r => r.Item == "Acme bank" && r.Sub == "Memory bank") && sv.Any(r => r.Item == "Translate: Acme" && r.Sub == "Prompts")
              && sv.Any(r => r.Item == "Acme" && r.Sub == "Client"), "Supervertaler's project settings, each under its part");
        var tms = rows.Where(r => r.Group == JobProfile.GroupTms).ToList();
        Check(tms.Count == 10, "every TM line: 7 for all language pairs, 2 for en-GB -> nl-NL, 1 for de-DE -> fr-FR (" + tms.Count + ")");
        Check(tms[0].Item == "Acme (en-GB-nl-NL)" && tms[0].Action == "Lookup, concordance, update", "a TM with its switches: " + tms[0].Action);
        var gone = One(tms, JobProfile.GroupTms, "Gone");
        Check(gone != null && gone.Action.StartsWith("File not there (") && gone.Action.Contains(Path.Combine(_temp, "TMs")), "a TM whose file is not there says so, with its folder: " + (gone == null ? "" : gone.Action));
        Check(tms[6].Item == "General" && tms[6].Action == "Concordance, update; penalty -3", "no lookup, and a penalty: " + tms[6].Action);
        Check(tms.Any(r => r.Item.Contains("(server TM)") && r.Action.EndsWith("penalty 1")) && tms.Any(r => r.Item.Contains("(provider)")),
            "server TMs and other providers (MT) are listed too");
        Check(tms.Count(r => r.Item.EndsWith("[en-GB \u2192 nl-NL only]")) == 2 && tms.Count(r => r.Item.EndsWith("[de-DE \u2192 fr-FR only]")) == 1,
            "a pair's own list is marked with its pair: " + string.Join(" | ", tms.Select(r => r.Item)));
        Check(notes.Count(n => n.Contains("[... only]")) == 1, "and one note says what that means");
        Check(One(rows, JobProfile.GroupTermbases, "Acme terms").Action == "" && One(rows, JobProfile.GroupTermbases, "Gone terms").Action.StartsWith("File not there (")
              && One(rows, JobProfile.GroupTermbases, "Server terms").Action == "Server termbase", "termbases: found, not there, server");
        Check(One(rows, JobProfile.GroupRecognition, "Termbase index for nl-NL").Action == "Dutch" && One(rows, JobProfile.GroupRecognition, "Minimum match value").Action == "80%"
              && One(rows, JobProfile.GroupRecognition, "Search order").Action == "Parallel" && One(rows, JobProfile.GroupRecognition, "Show terms").Action == "Yes",
            "term recognition: indexes and options");
        Check(One(rows, JobProfile.GroupCustomer, "Acme") != null, "the customer");
        Check(!notes.Any(n => n.Contains("no Supervertaler project settings")) && rows.All(r => !r.Changes), "no 'none yet' note, and nothing marked as a change");
        var order = rows.Select(r => r.Group).Distinct().ToList();
        Check(string.Join("|", order) == string.Join("|", new[] { JobProfile.GroupSupervertaler, JobProfile.GroupTms, JobProfile.GroupTermbases, JobProfile.GroupRecognition, JobProfile.GroupCustomer }),
            "parts in order: Supervertaler, TMs, termbases, term recognition, customer");

        List<string> n2;
        var bare = JobProfile.ContentRows(Read(Template("Plain holds", null, null, null, BundleXml(false, null))), File.Exists, out n2);
        Check(One(bare, JobProfile.GroupSupervertaler, "(none in this template)") != null && n2.Any(n => n.Contains("Create Project Template")),
            "a template without Supervertaler's settings: said, with how to give it some");
        Check(One(bare, JobProfile.GroupTms, "(none)") != null && One(bare, JobProfile.GroupTermbases, "(none)") != null && One(bare, JobProfile.GroupCustomer, "(none)") != null
              && !bare.Any(r => r.Group == JobProfile.GroupRecognition), "no TMs, termbases or customer: (none); no recognition options: no rows");

        var ignored = "<LanguageDirection Guid=\"" + Guid.NewGuid() + "\" SettingsBundleGuid=\"" + Guid.NewGuid() + "\" TargetLanguageCode=\"nl-NL\" SourceLanguageCode=\"en-GB\">"
                    + "<CascadeItem OverrideParent=\"false\">" + Entry(TmUri(FileTm("Unused")), true, true, 0, null) + "</CascadeItem></LanguageDirection>"
                    + "<LanguageDirection Guid=\"" + Guid.NewGuid() + "\" SettingsBundleGuid=\"" + Guid.NewGuid() + "\" TargetLanguageCode=\"de-DE\" SourceLanguageCode=\"en-GB\">"
                    + "<CascadeItem OverrideParent=\"true\"></CascadeItem></LanguageDirection>";
        List<string> n3;
        var pairs = JobProfile.ContentRows(Read(Template("Pairs holds", Entry(TmUri(FileTm("Main")), true, true, 0, null), ignored, null, null)), File.Exists, out n3);
        var pairTms = pairs.Where(r => r.Group == JobProfile.GroupTms).ToList();
        Check(!pairTms.Any(r => r.Item.StartsWith("Unused")), "a pair's list Studio ignores (OverrideParent off) is not shown");
        Check(pairTms.Count == 2 && pairTms[1].Item == "(no TMs)   [en-GB \u2192 de-DE only]" && pairTms[1].Action.Contains("empty"), "a pair's own empty list is shown as empty: " + string.Join(" | ", pairTms.Select(r => r.Item + " = " + r.Action)));

        List<string> n4;
        var thrown = JobProfile.ContentRows(Read(FullTemplate()), p => { throw new IOException("offline"); }, out n4);
        Check(thrown.All(r => !r.Action.Contains("not there")), "a file check that throws (offline drive) counts as there, no throw");
        List<string> n5;
        Check(JobProfile.ContentRows(null, File.Exists, out n5).Count == 0 && n5.Count == 0, "no template: nothing, no throw");

        // Scale: a template with 1,000 TM lines and 200 termbases, every file looked for.
        var sb = new StringBuilder();
        for (int i = 0; i < 1000; i++) sb.Append(Entry(TmUri(Path.Combine(_temp, "Many", "TM " + i + ".sdltm")), i % 2 == 0, true, 0, null));
        var tbs = new StringBuilder("<TermbaseConfiguration>");
        for (int i = 0; i < 200; i++) tbs.Append(TermbaseXml("Terms " + i, Path.Combine(_temp, "Many", "Terms " + i + ".sdltb"), true));
        tbs.Append("</TermbaseConfiguration>");
        var big = Read(Template("Big holds", sb.ToString(), null, tbs.ToString(), BundleXml(true, "Acme")));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        List<string> n6;
        var bigRows = JobProfile.ContentRows(big, File.Exists, out n6);
        watch.Stop();
        Check(bigRows.Count(r => r.Group == JobProfile.GroupTms) == 1000 && bigRows.Count(r => r.Group == JobProfile.GroupTermbases) == 200 && watch.ElapsedMilliseconds < 1000,
            "1,000 TMs and 200 termbases listed in " + watch.ElapsedMilliseconds + " ms");
    }

    // O: editing a project template
    static void Editing() {
        var local = new List<TermbaseIdentity> { new TermbaseIdentity(3, "Zeta terms", "2026-01-03"), new TermbaseIdentity(1, "Acme terms", "2026-01-01"), new TermbaseIdentity(2, "Beta terms", "2026-01-02") };
        var stored = "RWPA\tAcme terms\t1\t2026-01-01\nR\tOld name\t3\t2026-01-03\nRA\tElsewhere terms\t99\t2020-01-01\nnot a line";
        var rows = ProjectTemplateEdit.TermbaseRows(stored, local);
        Check(string.Join("|", rows.Select(r => r.Name)) == "Acme terms|Beta terms|Zeta terms|Elsewhere terms|not a line", "rows: this computer's alphabetical, then the others: " + string.Join("|", rows.Select(r => r.Name)));
        Check(rows[0].Read && rows[0].Write && rows[0].Project && rows[0].Ai && !rows[1].Any && rows[2].Read && !rows[2].Write, "ticks from the template; a renamed termbase is found by id and creation time");
        Check(rows[3].Missing && rows[3].Read && rows[3].Ai && rows[4].Missing && !rows[0].Missing, "a termbase this computer lacks, and an unreadable line, are kept as missing rows");
        var text = ProjectTemplateEdit.TermbaseText(rows);
        var lines = text.Split('\n');
        Check(lines.Length == 4 && lines[0] == "RWPA\tAcme terms\t1\t2026-01-01" && lines[1] == "R\tZeta terms\t3\t2026-01-03"
              && lines[2] == "RA\tElsewhere terms\t99\t2020-01-01" && lines[3] == "not a line", "saved: ticked ones in the stored form (renamed one under its name now), the others unchanged");
        rows[1].Project = true;
        Check(ProjectTemplateEdit.TermbaseText(rows).Split('\n').Count(l => l.StartsWith("RWPA") || l.Split('\t')[0].Contains("P")) == 1, "only one Project termbase: the first ticked keeps it");
        var none = ProjectTemplateEdit.TermbaseRows("", local);
        Check(none.Count == 3 && none.All(r => !r.Any) && ProjectTemplateEdit.TermbaseText(none) == "", "nothing ticked: empty, so a project keeps its own choices");
        Check(ProjectTemplateEdit.TermbaseRows(null, null).Count == 0 && ProjectTemplateEdit.TermbaseText(null) == "", "no list and no termbases: nothing, no throw");

        var prompts = new List<KeyValuePair<string, string>> {
            new KeyValuePair<string, string>("Translate/Acme.md", "Translate"), new KeyValuePair<string, string>("Proofread/Check.md", "Proofread"),
            new KeyValuePair<string, string>("Translate/acme.md", "Translate"), new KeyValuePair<string, string>("", "Translate") };
        Func<string, string, bool> belongs = (c, f) => c == f;
        var tr = ProjectTemplateEdit.PromptItems(prompts, false, belongs, "Translate/Old.md");
        Check(string.Join("|", tr.Select(i => i.Label)) == ProjectTemplateEdit.NotSet + "|(System Prompt only)|Translate/Acme|Translate/Old (not on this computer)",
            "translate list: not set, System Prompt only, the mode's prompts once each, and the stored one this computer lacks: " + string.Join("|", tr.Select(i => i.Label)));
        Check(tr[1].Value == "*built-in*" && tr[0].Value == "" && tr[3].Value == "Translate/Old.md", "each entry stores what the project would store");
        var pr = ProjectTemplateEdit.PromptItems(prompts, true, belongs, "*built-in*");
        Check(string.Join("|", pr.Select(i => i.Label)) == ProjectTemplateEdit.NotSet + "|(Built-in checks only)|Proofread/Check", "proofread list; a stored built-in-only adds nothing");
        var banks = ProjectTemplateEdit.BankItems(new[] { "b", "A", "", "A" }, "Gone");
        Check(string.Join("|", banks.Select(i => i.Label)) == ProjectTemplateEdit.NotSet + "|A|b|Gone (not on this computer)", "bank list: alphabetical, once each, and the stored one this computer lacks");

        var before = new ProjectProfileValues { MemoryBankName = "Old", ProfileName = "Acme patents", DisabledMultiTerm = "C:\\mt.sdltb", AiEnabledMultiTerm = "x" };
        var ed = ProjectTemplateEdit.Edited(before, " Acme bank ", "Translate/Acme.md", "", " Acme ", rows);
        Check(ed.MemoryBankName == "Acme bank" && ed.TranslatePromptPath == "Translate/Acme.md" && ed.ProofreadPromptPath == "" && ed.ClientName == "Acme",
            "edited values, trimmed");
        Check(ed.ProfileName == "" && ed.DisabledMultiTerm == before.DisabledMultiTerm && ed.AiEnabledMultiTerm == "x" && ed.Termbases.Length > 0,
            "MultiTerm choices kept as they were; the profile name cleared (a template is known by its file name)");

        var tpl = Path.Combine(_temp, "Templates", "New one.sdltpl");
        Check(ProjectTemplateEdit.CheckNewTemplatePath(tpl, File.Exists) == null, "a new name is accepted");
        Check(ProjectTemplateEdit.CheckNewTemplatePath("", File.Exists) != null && ProjectTemplateEdit.CheckNewTemplatePath(Path.Combine(_temp, "x.txt"), File.Exists) != null,
            "no name, or not .sdltpl: refused");
        var taken = ProjectTemplateEdit.CheckNewTemplatePath(FullTemplate(), File.Exists);
        Check(taken != null && taken.Contains("Update from project"), "an existing file is never replaced, and the message says what to use instead");
        Check(ProjectTemplateEdit.CheckNewTemplatePath(tpl, x => { throw new IOException("offline"); }) == null, "a file check that throws: no throw");

        // Scale: 2,000 termbases on this computer, 2,000 lines in the template.
        var many = new List<TermbaseIdentity>();
        var sb = new StringBuilder();
        for (int i = 0; i < 2000; i++) { many.Add(new TermbaseIdentity(i + 1, "Terms " + i, "c" + i)); if (i % 2 == 0) sb.Append("RW\tTerms " + i + "\t" + (i + 1) + "\tc" + i + "\n"); }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var manyRows = ProjectTemplateEdit.TermbaseRows(sb.ToString(), many);
        var manyText = ProjectTemplateEdit.TermbaseText(manyRows);
        watch.Stop();
        Check(manyRows.Count == 2000 && manyText.Split('\n').Length == 1000 && watch.ElapsedMilliseconds < 2000,
            "2,000 termbases read and written back in " + watch.ElapsedMilliseconds + " ms");
    }

    static TemplateProfile Read(string path) {
        TemplateProfile t;
        var error = JobProfile.ReadTemplate(path, out t, JobProfile.ReadBundleWithStudio);
        if (error != null) throw new Exception(error);
        return t;
    }

    // ─── D ──────────────────────────────────────────────────────────
    static void Reading() {
        var t = Read(FullTemplate());
        Check(t.Supervertaler != null && t.Supervertaler.MemoryBankName == "Acme bank" && t.Supervertaler.TranslatePromptPath == "Translate/Acme.md",
            "Supervertaler's set-up is read from the template's settings bundle (Studio's own deserialiser)");
        Check(t.CustomerName == "Acme", "Studio's customer is read by name (GeneralProjectInfoSettings.CustomerId)");
        Check(t.ProfileName == "Acme patents EN-NL" && t.Supervertaler.ProfileName == "Acme patents", "a template is known by its file name, not by a profile name copied into its Supervertaler group");
        Check(t.AllPairs.Entries.Count == 7 && t.PerPair.Count == 2, "the list for all pairs (7 lines) and two pairs' own lists are read");
        var nl = t.PerPair.First(p => p.Target == "nl-NL");
        Check(nl.OverrideParent && nl.StopWhenFound && nl.Source == "en-GB" && nl.Entries.Count == 2, "a pair's own list keeps its switches");
        var first = t.AllPairs.Entries[0];
        Check(first.Main.IsFileTm && first.Update && first.Lookup && first.Main.FilePath.EndsWith("Acme (en-GB-nl-NL).sdltm"), "a file TM, its path from the sdltm.file address, update and lookup");
        Check(t.AllPairs.Entries[2].Main.IsServerTm && t.AllPairs.Entries[2].Main.State == "", "a server TM is recognised, its State kept as stored");
        Check(!t.AllPairs.Entries[3].Main.IsFileTm && !t.AllPairs.Entries[3].Main.IsServerTm && t.AllPairs.Entries[3].Main.State == "key-state", "another provider (MT) is kept with its State");
        Check(t.Termbases.Count == 3 && t.Termbases[0].Local && t.Termbases[0].Path.EndsWith("Acme terms.sdltb") && !t.Termbases[2].Local,
            "termbases: name, path and local/server from their settings");
        Check(t.TermbaseIndexes.Count == 2 && t.Recognition != null && t.Recognition.MinimumMatchValue == 80 && t.Recognition.SearchOrder == "Parallel",
            "language indexes and recognition options are read");

        var bare = Read(Template("Plain", null, null, null, BundleXml(false, null)));
        Check(bare.Supervertaler == null && bare.CustomerName == "" && !bare.HasAnyTm && bare.Termbases.Count == 0 && bare.Recognition == null,
            "a template with none of it: no Supervertaler set-up, no customer, no TMs, no termbases");
        Check(bare.ProfileName == "Plain", "then the profile is named after the file");
        var noBundle = Read(Template("No bundle", null, null, null, null));
        Check(noBundle.Supervertaler == null && noBundle.CustomerName == "", "a template whose settings bundle is missing reads as having none");
    }

    // ─── E ──────────────────────────────────────────────────────────
    static void Unreadable() {
        TemplateProfile t;
        var gone = Path.Combine(_temp, "Templates", "Moved away.sdltpl");
        var e1 = JobProfile.ReadTemplate(gone, out t, JobProfile.ReadBundleWithStudio);
        Check(e1 != null && t == null && e1.Contains(gone) && e1.Contains("not there any more") && e1.Contains("Nothing was changed"), "a template that is gone: named, why, nothing changed");
        var noFolder = Path.Combine(_temp, "Offline NAS", "Acme.sdltpl");
        var e2 = JobProfile.ReadTemplate(noFolder, out t, JobProfile.ReadBundleWithStudio);
        Check(e2 != null && e2.Contains(noFolder) && e2.Contains("not there"), "a folder that is not there (a NAS that is offline): named and said");
        var notXml = Path.Combine(_temp, "Templates", "Broken.sdltpl");
        File.WriteAllText(notXml, "<ProjectTemplate><unclosed>");
        var e3 = JobProfile.ReadTemplate(notXml, out t, JobProfile.ReadBundleWithStudio);
        Check(e3 != null && e3.Contains("not a readable Studio project template"), "a damaged file: said");
        var other = Path.Combine(_temp, "Templates", "Project.sdltpl");
        File.WriteAllText(other, "<Project Guid=\"x\" />");
        var e4 = JobProfile.ReadTemplate(other, out t, JobProfile.ReadBundleWithStudio);
        Check(e4 != null && e4.Contains("is not a Studio project template"), "an XML file that is not a template: said");
        var locked = FullTemplate();
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            var e5 = JobProfile.ReadTemplate(locked, out t, JobProfile.ReadBundleWithStudio);
            Check(e5 != null && e5.Contains("could not be read") && t == null, "a file another program holds: said, nothing read");
        }
        var e6 = JobProfile.ReadTemplate(FullTemplate(), out t, x => { throw new InvalidOperationException("bad settings"); });
        Check(e6 != null && e6.Contains("settings") && e6.Contains("bad settings") && t == null, "settings that cannot be read: the whole template is refused (nothing half-applied)");
        PreparedProfile p;
        var e7 = JobProfile.Prepare(gone, Facts(), File.Exists, JobProfile.ReadBundleWithStudio, out p);
        Check(e7 != null && p == null, "and nothing is planned from it");
        Check(JobProfile.ReadTemplate("", out t) != null, "no template chosen: said");
    }

    // ─── F ──────────────────────────────────────────────────────────
    static void Tms() {
        var t = Read(FullTemplate());
        var plan = JobProfile.PlanTms(t, Facts(), File.Exists);
        Check(!plan.LeaveAlone, "there is something to add");
        Check(string.Join(",", plan.AllPairs.Kept) == "Own TM,General", "all pairs: the project's own TMs are kept, in their order (" + string.Join(", ", plan.AllPairs.Kept) + ")");
        var added = plan.AllPairs.Add.Select(e => e.Main.DisplayName).ToList();
        Check(added.Count == 3 && added[0] == "Acme (en-GB-nl-NL)" && added.Contains("Acme server (server TM)") && added.Contains("googletranslationprovider (provider)"),
            "all pairs: the template's TMs the project lacks are added, in the template's order (" + string.Join(", ", added) + ")");
        Check(plan.AllPairs.AlreadyIn.SequenceEqual(new[] { "General" }), "a TM in both (path in other capitals) is not added again: \"already in the project\"");
        Check(plan.Notes.Any(n => n.Contains("Gone.sdltm") && n.Contains("not there")), "a missing file TM is left out and said");
        Check(plan.Notes.Any(n => n.Contains("cannot be read")), "a provider whose address cannot be read is left out and said");
        Check(plan.Notes.Any(n => n.Contains("twice")), "the same TM twice in the template: added once, said");
        Check(plan.Notes.Any(n => n.Contains("server") && n.Contains("sign in")), "a server TM is added and said (Studio asks for the sign-in)");
        Check(plan.HasServerTms, "the plan knows it adds server TMs (so a refusal can be retried without them)");
        var acme = plan.AllPairs.Add[0];
        Check(acme.Update && acme.Lookup, "an added TM keeps the template's switches");

        var nl = plan.PerTarget.First(s => s.Target == "nl-NL");
        Check(nl.MakeOwnList && nl.Add.Count == 1 && nl.Add[0].Main.DisplayName == "Acme NL" && nl.AlreadyIn.Contains("General"),
            "nl-NL had no list of its own, the template's pair has one: it gets one (the project's list for all pairs plus Acme NL); General is there already");
        Check(plan.Notes.Any(n => n.Contains("nl-NL gets a TM list of its own")), "and that is said");
        var de = plan.PerTarget.First(s => s.Target == "de-DE");
        Check(!de.MakeOwnList && de.Kept.SequenceEqual(new[] { "Own DE" }) && de.Add.Count == 4,
            "de-DE has its own list: kept as it is, and the template's TMs for all pairs it lacks are added to it");
        Check(!plan.PerTarget.Any(s => s.Target == "fr-FR"), "fr-FR uses the list for all pairs, which has the template pair's TM: nothing of its own");
        Check(plan.Notes.Any(n => n.Contains("fr-FR") && n.Contains("de-DE")), "the template's pair with another source is said when used");

        var f2 = Facts();
        foreach (var e in plan.AllPairs.Add) f2.TmsAllPairs.Providers.Add(e.Main);
        f2.TmsAllPairs.Providers.Add(new TemplateProvider { Uri = TmUri(FileTm("Acme NL")) });
        f2.TmsPerTarget["de-DE"].Providers.AddRange(plan.PerTarget.First(s => s.Target == "de-DE").Add.Select(e => e.Main));
        var again = JobProfile.PlanTms(t, f2, File.Exists);
        Check(again.LeaveAlone && again.Notes.Last().Contains("already has all"), "applying the same profile twice adds nothing the second time");

        var none = JobProfile.PlanTms(Read(Template("No TMs", null, null, null, null)), Facts(), File.Exists);
        Check(none.LeaveAlone && none.Notes[0].Contains("left as they are"), "a template with no TMs leaves the project's TMs alone");
        var empty = Facts(); empty.TmsAllPairs = new TmListFacts();
        var fresh = JobProfile.PlanTms(t, empty, File.Exists);
        Check(fresh.AllPairs.Kept.Count == 0 && fresh.AllPairs.Add.Count == 4, "a project with no TMs gets all the template's");
    }

    // ─── G ──────────────────────────────────────────────────────────
    static void Termbases() {
        var t = Read(FullTemplate());
        var plan = JobProfile.PlanTermbases(t, Facts(), File.Exists);
        Check(!plan.LeaveAlone && plan.Kept.SequenceEqual(new[] { "Own terms" }) && plan.Add.Count == 1 && plan.Add[0].Name == "Acme terms",
            "the project's termbase is kept; the template's that is there and the project lacks is added");
        Check(plan.Notes.Any(n => n.Contains("Gone.sdltb") && n.Contains("not there")), "a missing termbase is left out and said");
        Check(plan.Notes.Any(n => n.Contains("server termbase") && n.Contains("Server terms")), "a server termbase is not added, and that is said");
        var idx = string.Join(",", plan.AddIndexes.Select(kv => kv.Key + "=" + kv.Value));
        Check(idx == "en-GB=English", "indexes: only the template's for languages the project does not map (" + idx + ")");

        var f = Facts();
        f.Termbases.Add(new KeyValuePair<string, string>("Acme terms", JobProfile.TermbaseKey(true, Termbase("Acme terms").ToUpperInvariant())));
        var again = JobProfile.PlanTermbases(t, f, File.Exists);
        Check(again.LeaveAlone && again.AlreadyIn.Contains("Acme terms") && again.Notes.Last().Contains("already has"),
            "a termbase the project has (path in other capitals): not added again, nothing to do");

        var none = JobProfile.PlanTermbases(Read(Template("No termbases", null, null, null, null)), Facts(), File.Exists);
        Check(none.LeaveAlone, "a template with no termbases leaves the project's alone");
        var allGone = JobProfile.PlanTermbases(Read(Template("All gone", null, null,
            "<TermbaseConfiguration>" + TermbaseXml("Gone", Path.Combine(_temp, "nothing.sdltb"), true) + "</TermbaseConfiguration>", null)), Facts(), File.Exists);
        Check(allGone.LeaveAlone && allGone.Notes.Any(n => n.StartsWith("None of")), "none of them there: the project's are left alone, and that is said");
    }

    // ─── H ──────────────────────────────────────────────────────────
    static void Customer() {
        var f = Facts();
        Check(JobProfile.DecideCustomer("", f).Choice == CustomerChoice.LeaveAlone, "the template names no customer: the project's is left alone");
        var change = JobProfile.DecideCustomer("ACME", f);
        Check(change.Choice == CustomerChoice.Change && change.Name == "acme", "a name in the list in other capitals: set, as the list spells it");
        f.CustomerName = "acme";
        Check(JobProfile.DecideCustomer("Acme", f).Choice == CustomerChoice.AlreadySet, "already the project's customer: nothing to do");
        var ask = JobProfile.DecideCustomer(" New client ", f);
        Check(ask.Choice == CustomerChoice.AskToAdd && ask.Name == "New client", "not in the list: asked first, never added unasked");
        f.CustomerNames = null;
        Check(JobProfile.DecideCustomer("Acme", f).Choice == CustomerChoice.CannotRead, "Studio's list cannot be read: the customer is left alone");
    }

    // ─── I ──────────────────────────────────────────────────────────
    static void Merge() {
        var current = new ProjectProfileValues { MemoryBankName = "Own bank", ProofreadPromptPath = "Proofread/Own.md", ClientName = "Own client", Termbases = "R\tOwn\t1\t", ProfileName = "Old profile" };
        var template = new ProjectProfileValues { MemoryBankName = "Acme bank", TranslatePromptPath = "Translate/Acme.md", ClientName = " " };
        var m = JobProfile.MergeForApply(current, template, "Acme patents");
        Check(m.MemoryBankName == "Acme bank" && m.TranslatePromptPath == "Translate/Acme.md", "the template's values win where it has one");
        Check(m.ProofreadPromptPath == "Proofread/Own.md" && m.ClientName == "Own client" && m.Termbases == "R\tOwn\t1\t", "the project keeps its own where the template has none (blank counts as none)");
        Check(m.ProfileName == "Acme patents", "the project records the job profile's name");
        Check(JobProfile.MergeForApply(null, null, "X").ProfileName == "X", "nothing on either side: no throw");
    }

    // ─── J ──────────────────────────────────────────────────────────
    static void Words() {
        PreparedProfile p;
        var f = Facts();
        Check(JobProfile.Prepare(FullTemplate(), f, File.Exists, JobProfile.ReadBundleWithStudio, out p) == null, "prepared");
        List<string> notes;
        var rows = JobProfile.Rows(p, f, out notes);
        Func<string, string, PreviewRow> R = (g, item) => rows.FirstOrDefault(r => r.Group == g && r.Item == item);
        var bank = R(JobProfile.GroupSupervertaler, "Acme bank");
        Check(bank != null && bank.Sub == "Memory bank" && bank.Action == "Set" && bank.Changes && notes.Any(n => n.Contains("stay as the project has them")),
            "table: Supervertaler's settings, one row each, and that the rest stays");
        var items = JobProfile.SetupItems(new ProjectProfileValues { MemoryBankName = "acme-bank", TranslatePromptPath = "Translate/Acme patents.md", ClientName = "Acme",
            Termbases = "RW\tZeta terms\t13\t2025-11-29" + "\n" + "RWPA\tAcme terms\t70\t2026-07-22" });
        Check(items.Contains("Memory bank: acme-bank") && items.Contains("Translate prompt: Acme patents") && items.Contains("Client: Acme"),
            "set-up items: memory bank, prompt by its name, client");
        Check(items.IndexOf("Supervertaler termbase: Acme terms (read, write, project termbase, AI)") >= 0
              && items.IndexOf("Supervertaler termbase: Zeta terms (read, write)") > items.IndexOf("Supervertaler termbase: Acme terms (read, write, project termbase, AI)")
              && items.Contains("Other Supervertaler termbases: off for this project"),
            "set-up items: each termbase by name with its ticks, alphabetical, and that the others go off");
        Check(JobProfile.SetupItems(new ProjectProfileValues { MemoryBankName = "x" }).Count == 1, "set-up items: only what the template has");
        Check(rows.Any(r => r.Group == JobProfile.GroupSupervertaler && r.Sub == "Prompts" && r.Item.StartsWith("Translate: ")), "table: the set-up items are its Supervertaler rows, each under its part");
        var parts = JobProfile.SetupRows(new ProjectProfileValues { MemoryBankName = "acme-bank", ClientName = "Acme",
            Termbases = "RW\tZeta terms\t13\t2025-11-29" + "\n" + "RWPA\tAcme terms\t70\t2026-07-22" + "\n" + "R\tBeta terms\t14\t2026-01-01" });
        var tbs = parts.Where(kv => kv.Key == "Termbases").Select(kv => kv.Value).ToList();
        Check(tbs.Count == 4 && tbs[0] == "Acme terms (read, write, project termbase, AI)" && tbs[1] == "Beta terms (read)" && tbs[3] == "All others: off for this project",
            "table: termbases are names under one Termbases heading, alphabetical, the others last (" + string.Join(" | ", tbs) + ")");
        Check(string.Join(",", parts.Select(kv => kv.Key).Distinct()) == "Memory bank,Client,Termbases", "table: the parts in a fixed order");
        var own = R(JobProfile.GroupTms, "Own TM");
        var general = R(JobProfile.GroupTms, "General");
        var acme = rows.FirstOrDefault(r => r.Group == JobProfile.GroupTms && r.Item.StartsWith("Acme (en-GB-nl-NL)"));
        Check(own != null && own.Action == "Kept (only in the project)" && !own.Changes, "table: the project's own TM is kept, and does not count as a change");
        Check(general != null && general.Action == "Kept (in both project and template)" && rows.Count(r => r.Item == "General") == 1,
            "table: a TM both have is one row, kept, not listed twice");
        Check(acme != null && acme.Action == "Added (lookup, update)" && acme.Changes, "table: the template's TM is added, with its switches");
        Check(rows.Any(r => r.Group == JobProfile.GroupTms && r.Action.StartsWith("New list of its own") && r.Item.Contains("en-GB \u2192 nl-NL")),
            "table: a pair's new list of its own");
        Check(R(JobProfile.GroupTermbases, "Own terms") != null && R(JobProfile.GroupTermbases, "Own terms").Action == "Kept (only in the project)" && R(JobProfile.GroupTermbases, "Acme terms") != null && R(JobProfile.GroupTermbases, "Acme terms").Action == "Added",
            "table: termbases kept and added");

        // Two TMs with the same name, one an old path whose file is gone, one switched off (Michael, 20 Nov: two
        // "BEIJER (en-GB->nl-NL)" rows, the first pointing at an old Google Drive folder).
        var f3 = Facts();
        var oldPath = Path.Combine(Path.Combine(_temp, "old drive"), "Own TM.sdltm");
        f3.TmsAllPairs.Providers.Insert(0, new TemplateProvider { Uri = TmUri(oldPath), Enabled = false });
        PreparedProfile p3;
        JobProfile.Prepare(FullTemplate(), f3, File.Exists, JobProfile.ReadBundleWithStudio, out p3);
        List<string> notes3;
        var rows3 = JobProfile.Rows(p3, f3, out notes3);
        var same = rows3.Where(r => r.Group == JobProfile.GroupTms && r.Item.StartsWith("Own TM")).ToList();
        Check(same.Count == 2 && same.All(r => r.Item.Contains("(in ")) && same[0].Item != same[1].Item,
            "two TMs with the same name: each shows its folder (" + string.Join(" | ", same.Select(r => r.Item)) + ")");
        Check(same[0].Action == "Kept, but its file is not there; switched off" && !same[0].Changes && same[1].Action == "Kept (only in the project)",
            "the one whose file is gone says so; the other is kept as usual");
        Check(notes3.Count(n => n.Contains("its file is not there") && n.Contains("Project Settings")) == 1, "a note says what to do about it, once");
        var off = Facts();
        off.TmsAllPairs.Providers[0].Enabled = false;
        PreparedProfile p4;
        JobProfile.Prepare(FullTemplate(), off, File.Exists, JobProfile.ReadBundleWithStudio, out p4);
        List<string> notes4;
        var offRow = JobProfile.Rows(p4, off, out notes4).First(r => r.Group == JobProfile.GroupTms && r.Item == "Own TM");
        Check(offRow.Action == "Kept (only in the project); switched off", "a TM switched off in the project says so");
        Check(JobProfile.PlanTms(Read(FullTemplate()), f3, path => { if (path == oldPath) throw new IOException("locked"); return File.Exists(path); }).AllPairs.KeptDetail.All(k => !k.Missing),
            "a file check that fails does not call a TM missing, and nothing throws");
        Check(R(JobProfile.GroupCustomer, "acme") != null && R(JobProfile.GroupCustomer, "acme").Changes, "table: the customer is set");
        var q = JobProfile.ConfirmQuestion("Acme patents", "PROJ-001", rows);
        Check(q != null && q.StartsWith("Apply the project template \"Acme patents\" to \"PROJ-001\"?") && q.Contains("This will set Supervertaler's project settings from the template, add 5 TMs, give a language pair a TM list of its own") && q.Contains("add 1 termbase")
              && q.Contains(" and set the customer") && q.Contains("Nothing is removed"), "question: short, counts what changes, says nothing is removed");
        Check(JobProfile.ConfirmQuestion("X", "Y", rows.Where(r => !r.Changes).ToList()) == null, "question: none when nothing would change");
        f.FromElsewhere = true;
        List<string> pkgNotes;
        var pkg = JobProfile.Rows(p, f, out pkgNotes);
        Check(pkgNotes.Any(n => n.Contains("own record only")) && pkg.Where(r => r.Group != JobProfile.GroupSupervertaler).All(r => r.Action.Contains("package or GroupShare") && !r.Changes),
            "a project from a package or GroupShare: Supervertaler's own record only; Studio's parts left alone");

        var done = JobProfile.Summary("Acme patents", "PROJ-001", new List<ApplyStep> {
            new ApplyStep(JobProfile.StepSupervertaler, StepOutcome.Done, "written into the project"),
            new ApplyStep(JobProfile.StepTms, StepOutcome.Done, "added Acme"),
            new ApplyStep(JobProfile.StepCustomer, StepOutcome.Unchanged, "the template names none"),
        });
        Check(done.StartsWith("Project template \"Acme patents\" applied to \"PROJ-001\".") && done.Contains("Changed:") && done.Contains("No change: Studio customer.") && !done.Contains("Not done:"),
            "summary: what changed and what was left, nothing failed");
        Check(!done.Contains("close it and open it again") && done.Contains("Nothing was removed"), "summary: says nothing was removed, and no reopen hint (the open document searches an added TM at once)");
        Check(done.Contains("\u2022 Studio TMs: added Acme\r\n"), "summary: one short line per change");
        var partial = JobProfile.Summary("Acme patents", "PROJ-001", new List<ApplyStep> {
            new ApplyStep(JobProfile.StepTms, StepOutcome.Failed, "Studio did not accept the template's TMs (x); the project's TMs are as they were"),
            new ApplyStep(JobProfile.StepTermbases, StepOutcome.Done, "Acme terms"),
        });
        Check(partial.Contains("in part") && partial.Contains("Not done:") && partial.Contains("as they were") ,
            "summary: a failure is shown, not hidden");
    }

    // ─── K ──────────────────────────────────────────────────────────
    static void ListAndScale() {
        var studio = Path.Combine(_temp, "Documents", "Studio 2026");
        var projects = Path.Combine(studio, "Projects");
        var folder = Path.Combine(studio, "Project Templates");
        var elsewhere = Path.Combine(_temp, "Client templates");
        Directory.CreateDirectory(projects); Directory.CreateDirectory(folder); Directory.CreateDirectory(elsewhere);
        var items = new StringBuilder();
        for (int i = 0; i < 200; i++) {
            var tp = Path.Combine(elsewhere, "Client " + i.ToString("000") + ".sdltpl");
            File.WriteAllText(tp, "<ProjectTemplate />");
            items.Append("<ProjectTemplateListItem ProjectTemplateFilePath=\"" + System.Security.SecurityElement.Escape(tp) + "\"><ProjectTemplateInfo Description=\"\" /></ProjectTemplateListItem>");
        }
        items.Append("<ProjectTemplateListItem ProjectTemplateFilePath=\"..\\Project Templates\\Registered.sdltpl\" />");
        File.WriteAllText(Path.Combine(projects, "projects.xml"), "<ProjectServer><ProjectTemplates>" + items + "</ProjectTemplates></ProjectServer>");
        File.WriteAllText(Path.Combine(folder, "Registered.sdltpl"), "<ProjectTemplate />");
        for (int i = 0; i < 200; i++) File.WriteAllText(Path.Combine(folder, "Folder only " + i.ToString("000") + ".sdltpl"), "<ProjectTemplate />");

        var find = typeof(TradosTools).GetMethod("FindProjectTemplates", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(IList<string>) }, null);
        var sw = Stopwatch.StartNew();
        var list = ((System.Collections.IEnumerable)find.Invoke(null, new object[] { new List<string> { studio } })).Cast<object>().ToList();
        long ms = sw.ElapsedMilliseconds;
        Func<object, string> pathOf = o => (string)o.GetType().GetProperty("Path").GetValue(o);
        Func<object, int> registered = o => ((List<string>)o.GetType().GetProperty("RegisteredIn").GetValue(o)).Count;
        Check(list.Count == 401, "Studio's registered templates (also outside its folder) and the folder's, each once: " + list.Count);
        Check(registered(list.First(o => pathOf(o).EndsWith("Registered.sdltpl"))) == 1, "a registered template in the folder is one entry, marked registered");
        Check(ms < 1000, "401 templates listed in " + ms + " ms");

        var names = Enumerable.Range(0, 500).Select(i => "Customer " + ((i * 7919) % 500).ToString("000")).ToList();
        sw.Restart();
        var sorted = JobProfile.SortByName(names, s => s);
        Check(sorted.First() == "Customer 000" && sorted.Last() == "Customer 499" && sw.ElapsedMilliseconds < 500, "500 customers sorted in " + sw.ElapsedMilliseconds + " ms");

        var big = new StringBuilder();
        for (int i = 0; i < 300; i++) big.Append(Entry(TmUri(FileTm("Big " + i.ToString("000"))), i % 2 == 0, true, 0, null));
        var bigPath = Template("Big", big.ToString(), null, null, BundleXml(true, "Acme"));
        sw.Restart();
        PreparedProfile prepared;
        var error = JobProfile.Prepare(bigPath, Facts(), File.Exists, JobProfile.ReadBundleWithStudio, out prepared);
        Check(error == null && prepared.Tms.AllPairs.Add.Count == 300 && sw.ElapsedMilliseconds < 1000, "a template with 300 TMs read and planned in " + sw.ElapsedMilliseconds + " ms");
    }

    // ─── L ──────────────────────────────────────────────────────────
    static void OutsideStudio() {
        var apply = typeof(ProjectProfileStore).GetMethod("ApplyProfile", BindingFlags.NonPublic | BindingFlags.Static);
        var step = (ApplyStep)apply.Invoke(null, new object[] { null, null, new ProjectProfileValues { MemoryBankName = "Acme bank" }, "Acme" });
        Check(step != null && step.Outcome == StepOutcome.Failed, "applying with no project: a failed step, no throw");
        Check(!Directory.Exists(UserDataPath.ProjectsDir) || Directory.GetFiles(UserDataPath.ProjectsDir).Length == 0, "and no settings file was written");
    }
}
'@

[JobProfileStep2Test]::Install([string[]]@((Split-Path $Dll), $studio))
try {
    [JobProfileStep2Test]::Run($temp)
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
$after = Snap $real
$same = (Compare-Object $before $after) -eq $null
if ($same) { [JobProfileStep2Test]::Pass++ ; Write-Host "  ok   real settings, diagnostic log and Studio's projects.xml files unchanged" }
else { [JobProfileStep2Test]::Fail++ ; Write-Host "  FAIL real files changed:"; Compare-Object $before $after | Out-Host }
Write-Host ""
Write-Host ("{0} passed, {1} failed" -f [JobProfileStep2Test]::Pass, [JobProfileStep2Test]::Fail)
if ([JobProfileStep2Test]::Fail -gt 0) { exit 1 }
