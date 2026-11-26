# MCP merge_segments / split_segment, offline. Runs the BUILT plugin's own code:
#
#   A  the checks (Core/SegmentStructurePlanner): which merges and split points
#      are refused, and why - every condition under which Studio's own command
#      would stop to ask (a confirmed segment, a group tag, a shared tag id), plus
#      different paragraphs, locks, tracked changes, tag pairs, ambiguous text.
#   B  the bridge handlers (BridgeMergeSegments / BridgeSplitSegment) on REAL
#      segments from Studio's own factory, in a stand-in document: each refusal
#      comes back as the MCP client sees it, and NOTHING in the document changes
#      (no edit, no status write, no navigation).
#   C  Studio's editor parts the tools drive (Core/StudioSegmentCommands.FindParts),
#      found by the plugin's own lookup code in THAT Studio's DLLs; the view field
#      on the integration API's document; the two command ids in Studio's editor.
#      Without a running Studio the action service cannot be had, and the handlers
#      must then refuse - the "missing or renamed part" path - not throw.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\mcp_merge_split_test.ps1
#
# Runs every build found under src\Supervertaler.Trados\bin\Studio1x\Debug against
# its Studio (Studio 2026 also against the 2026 beta). 32-bit builds run in the
# 32-bit PowerShell. No Studio is started; nothing outside a temp folder is written.
#
# What it cannot reach, and the live test covers: Studio's editor actually
# selecting the rows / placing the cursor, Studio's Merge and Split running, the
# ids Studio gives the new segments, and Undo.
param(
    [string]$Dll,
    [string]$StudioDir,
    [string]$Root = (Join-Path $env:TEMP "sv-mcp-merge-split-test"),
    [switch]$Child
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$Root = [IO.Path]::GetFullPath($Root)
if ($Root -like 'D:\Supervertaler*' -or $Root.Length -lt 10) { throw "Refusing to use $Root as the test root." }

if (-not $Child) {
    $runs = @()
    foreach ($v in 19, 18, 17) {
        $d = Join-Path $repo "src\Supervertaler.Trados\bin\Studio$v\Debug\Supervertaler.Trados.dll"
        if (-not (Test-Path -LiteralPath $d)) { continue }
        $dirs = if ($v -eq 19) { @('C:\Program Files\Trados\Trados Studio\Studio19', 'C:\Program Files\Trados\Trados Studio\Studio19Beta') }
                else { @("C:\Program Files (x86)\Trados\Trados Studio\Studio$v") }
        foreach ($s in $dirs) { if (Test-Path -LiteralPath (Join-Path $s 'Sdl.TranslationStudio.Editor.dll')) { $runs += ,@($d, $s) } }
    }
    if ($runs.Count -eq 0) { Write-Host "RESULT: FAILED (no Debug build with an installed Studio)"; exit 1 }

    $protected = @('D:\Supervertaler\settings\api-keys.json', (Join-Path $env:APPDATA 'Supervertaler\config.json'),
                   'D:\Supervertaler\resources\supervertaler.db')
    function Snapshot { $protected | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { $i = Get-Item -LiteralPath $_; "$($i.FullName)|$($i.Length)|$($i.LastWriteTimeUtc.Ticks)" } }
    $before = @(Snapshot)
    $failed = $false
    $n = 0
    foreach ($r in $runs) {
        $n++
        $ps = if ($r[1] -like '*(x86)*') { Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe' }
              else { Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe' }
        Write-Host ""
        Write-Host "== $($r[0])"
        Write-Host "   against $($r[1])"
        $psi = New-Object System.Diagnostics.ProcessStartInfo $ps
        $psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Child -Dll `"$($r[0])`" -StudioDir `"$($r[1])`" -Root `"$Root\$n`""
        $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.CreateNoWindow = $true
        $p = [Diagnostics.Process]::Start($psi)
        $out = $p.StandardOutput.ReadToEndAsync(); $err = $p.StandardError.ReadToEndAsync()
        $p.WaitForExit()
        foreach ($line in (($out.Result + $err.Result) -split "`r?`n")) { if ($line.Trim()) { Write-Host "  $line" } }
        if ($p.ExitCode -ne 0) { $failed = $true }
    }
    $after = @(Snapshot)
    if ((Compare-Object $before $after)) { Write-Host "FAIL  a real Supervertaler file changed during the test"; $failed = $true }
    else { Write-Host "PASS  real files unchanged ($($before.Count) checked)" }
    try { Remove-Item -LiteralPath $Root -Recurse -Force -ErrorAction SilentlyContinue } catch { }
    if ($failed) { Write-Host "RESULT: FAILED"; exit 1 }
    Write-Host "RESULT: all passed"
    exit 0
}

# ── Child ────────────────────────────────────────────────────────────────
if (Test-Path -LiteralPath $Root) { Remove-Item -LiteralPath $Root -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $Root 'data') | Out-Null
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
public static class MergeSplitResolver
{
    private static readonly HashSet<string> Tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public static void Register(string[] dirs)
    {
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object s, ResolveEventArgs e)
        {
            var n = new AssemblyName(e.Name).Name;
            lock (Tried) { if (!Tried.Add(n)) return null; }
            foreach (var d in dirs)
            {
                var c = Path.Combine(d, n + ".dll");
                if (File.Exists(c)) { try { return Assembly.LoadFrom(c); } catch { return null; } }
            }
            return null;
        };
    }
}
'@
[MergeSplitResolver]::Register(@((Split-Path $Dll), $StudioDir))
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Web.Extensions
$plugin = [Reflection.Assembly]::LoadFrom($Dll)
$plugin.GetType("Supervertaler.Core.SupervertalerPaths").GetMethod("Set").Invoke($null, @([string](Join-Path $Root 'data')))

# The stand-in document: IStudioDocument from this Studio's own interface, every
# member throwing except the ones these handlers may read. A call to anything
# else (SetActiveSegmentPair, ProcessSegmentPair, ...) throws, and so fails the
# case that reached it: these cases must change nothing.
$api = [Reflection.Assembly]::LoadFrom((Join-Path $StudioDir 'Sdl.TranslationStudioAutomation.IntegrationApi.dll'))
$iface = $api.GetType('Sdl.TranslationStudioAutomation.IntegrationApi.IStudioDocument', $true)
$refs = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
function CsType([Type]$t) {
    if ($t -eq [void]) { return 'void' }
    if ($t.IsByRef) { return (CsType $t.GetElementType()) }
    if ($t.IsArray) { return (CsType $t.GetElementType()) + '[' + (',' * ($t.GetArrayRank() - 1)) + ']' }
    [void]$refs.Add($t.Assembly.Location)
    if ($t.IsGenericType) {
        $def = $t.GetGenericTypeDefinition().FullName
        $name = $def.Substring(0, $def.IndexOf('`')).Replace('+', '.')
        $targs = @($t.GetGenericArguments() | ForEach-Object { CsType $_ })
        return 'global::' + $name + '<' + ($targs -join ', ') + '>'
    }
    return 'global::' + $t.FullName.Replace('+', '.')
}
$special = @('GetParentParagraphUnit')
$specialProps = @('SegmentPairs')
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('public partial class FakeDocument : ' + (CsType $iface) + ' {')
foreach ($i in @($iface) + $iface.GetInterfaces()) {
    $in = CsType $i
    foreach ($p in $i.GetProperties()) {
        $ix = @($p.GetIndexParameters())
        $head = if ($ix.Count -gt 0) { $k = 0; $ps = @($ix | ForEach-Object { (CsType $_.ParameterType) + ' i' + ($k++) }); "$in.this[" + ($ps -join ', ') + "]" } else { "$in." + $p.Name }
        $body = ''
        if ($p.CanRead) { $body += if ($specialProps -contains $p.Name -and $ix.Count -eq 0) { ' get { return Fake' + $p.Name + '; }' } else { ' get { Touched++; throw new global::System.NotImplementedException("' + $p.Name + '"); }' } }
        if ($p.CanWrite) { $body += ' set { Touched++; throw new global::System.NotImplementedException(); }' }
        [void]$sb.AppendLine('  ' + (CsType $p.PropertyType) + ' ' + $head + ' {' + $body + ' }')
    }
    foreach ($e in $i.GetEvents()) { [void]$sb.AppendLine('  event ' + (CsType $e.EventHandlerType) + " $in." + $e.Name + ' { add { } remove { } }') }
    foreach ($m in $i.GetMethods()) {
        if ($m.IsSpecialName) { continue }
        if ($m.IsGenericMethodDefinition) { throw "Generic interface method $($m.Name): extend the generator." }
        $k = 0; $names = @(); $ps = @()
        foreach ($pa in $m.GetParameters()) {
            $mod = if ($pa.ParameterType.IsByRef) { if ($pa.IsOut) { 'out ' } else { 'ref ' } } else { '' }
            $nm = 'p' + ($k++); $names += ($mod + $nm); $ps += ($mod + (CsType $pa.ParameterType) + ' ' + $nm)
        }
        $ret = CsType $m.ReturnType
        $call = 'Fake' + $m.Name + '(' + ($names -join ', ') + ')'
        $body = if ($special -contains $m.Name) { if ($ret -eq 'void') { "$call;" } else { "return $call;" } }
                else { 'Touched++; throw new global::System.NotImplementedException("' + $m.Name + '");' }
        [void]$sb.AppendLine('  ' + $ret + " $in." + $m.Name + '(' + ($ps -join ', ') + ') { ' + $body + ' }')
    }
}
[void]$sb.AppendLine('}')

$src = @'
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using Sdl.Core.Globalization;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Sdl.FileTypeSupport.Framework.NativeApi;

public partial class FakeDocument
{
    public List<ISegmentPair> Pairs = new List<ISegmentPair>();
    public Dictionary<string, IParagraphUnit> PuOf = new Dictionary<string, IParagraphUnit>();
    public int Touched;
    IParagraphUnit FakeGetParentParagraphUnit(ISegmentPair pair) { return PuOf[pair.Properties.Id.Id]; }
    IEnumerable<ISegmentPair> FakeSegmentPairs { get { return Pairs; } }
}

public static class MergeSplitTest
{
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    public static int Pass, Fail;
    static Assembly P;
    static Type VP, Planner;

    static void Check(bool ok, string what)
    {
        if (ok) Pass++; else Fail++;
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what);
    }
    static Type T_(string name) { return P.GetType("Supervertaler.Trados." + name, true); }
    static void Set(object o, string name, object value) { o.GetType().GetField(name, Any).SetValue(o, value); }
    static object Get(object o, string name) { return o.GetType().GetField(name, Any).GetValue(o); }

    // ── A: the planner ────────────────────────────────────────────────
    static object Cand(string id, string pu, string status = "Draft", bool locked = false, string[] tags = null, bool group = false)
    {
        var c = Activator.CreateInstance(Planner.GetNestedType("MergeCandidate", Any), true);
        Set(c, "Id", id); Set(c, "ParagraphId", pu); Set(c, "Status", status); Set(c, "Locked", locked);
        Set(c, "TargetTagIds", (IList<string>)new List<string>(tags ?? new string[0])); Set(c, "HasGroupTag", group);
        return c;
    }
    static string CheckMerge(params object[] cands)
    {
        var lt = typeof(List<>).MakeGenericType(Planner.GetNestedType("MergeCandidate", Any));
        var list = (IList)Activator.CreateInstance(lt);
        foreach (var c in cands) list.Add(c);
        return (string)Planner.GetMethod("CheckMerge", Any).Invoke(null, new object[] { list });
    }
    static object Run(string text, int[] pairs = null, bool locked = false, bool rev = false)
    {
        var r = Activator.CreateInstance(Planner.GetNestedType("TextRun", Any), true);
        Set(r, "Text", text); Set(r, "TagPairs", (IList<int>)new List<int>(pairs ?? new int[0])); Set(r, "Locked", locked); Set(r, "InRevision", rev);
        return r;
    }
    sealed class Split { public int Run, Offset; public string Error, Note, First, Second; }
    static Split Locate(string splitBefore, bool allow, params object[] runs)
    {
        var lt = typeof(List<>).MakeGenericType(Planner.GetNestedType("TextRun", Any));
        var list = (IList)Activator.CreateInstance(lt);
        foreach (var r in runs) list.Add(r);
        var sp = Planner.GetMethod("LocateSplit", Any).Invoke(null, new object[] { list, splitBefore, allow });
        return new Split { Run = (int)Get(sp, "RunIndex"), Offset = (int)Get(sp, "Offset"), Error = (string)Get(sp, "Error"),
                           Note = (string)Get(sp, "Note"), First = (string)Get(sp, "FirstPart"), Second = (string)Get(sp, "SecondPart") };
    }
    static bool Has(string s, string part) { return s != null && s.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0; }

    static void CasePlanner()
    {
        Check(CheckMerge(Cand("3", "p"), Cand("4", "p")) == null, "A two Draft neighbours in one paragraph: allowed");
        Check(CheckMerge(Cand("3", "p", "Unspecified"), Cand("4", "p", "RejectedTranslation"), Cand("5", "p")) == null,
            "A three segments, untranslated / rejected / draft: allowed");
        Check(Has(CheckMerge(Cand("3", "p")), "at least two"), "A one id: refused");
        Check(Has(CheckMerge(), "at least two"), "A no ids: refused");
        Check(Has(CheckMerge(Cand("3", "p"), Cand("3", "p")), "listed twice"), "A the same id twice: refused");
        Check(Has(CheckMerge(Cand("3", "p"), Cand("4", "q")), "different paragraphs"), "A different paragraphs: refused");
        Check(Has(CheckMerge(Cand("3", "p"), Cand("4", "p", locked: true)), "locked"), "A a locked segment: refused");
        foreach (var st in new[] { "Translated", "ApprovedTranslation", "ApprovedSignOff" })
        {
            var why = CheckMerge(Cand("3", "p"), Cand("4", "p", st));
            Check(Has(why, "confirmed") && Has(why, "Draft") && Has(why, st), "A a " + st + " segment: refused, says why and how (Draft first)");
        }
        Check(Has(CheckMerge(Cand("3", "p", group: true), Cand("4", "p")), "group tag"), "A a group tag in the first segment: refused (Studio shows a warning)");
        Check(CheckMerge(Cand("3", "p"), Cand("4", "p", group: true)) == null, "A a group tag only in a later segment: allowed, as Studio checks only the first");
        Check(Has(CheckMerge(Cand("3", "p", tags: new[] { "5" }), Cand("4", "p", tags: new[] { "5", "6" })), "same tag id"),
            "A two segments sharing a tag id: refused (Studio asks Yes/No)");
        Check(CheckMerge(Cand("3", "p", tags: new[] { "5" }), Cand("4", "p", tags: new[] { "6" })) == null, "A different tag ids: allowed");
        Check(CheckMerge(Cand("3", "p", tags: new[] { "5", "5" }), Cand("4", "p")) == null,
            "A a tag id twice in ONE segment (a pair's start and end): allowed, as in Studio");

        // The row lookup (live bug, 26 Nov: merging 12+13 refused as "hidden by a
        // display filter"). Studio's FindRow at a segment's start can answer the
        // hidden structure row just before it; the row actually holding the
        // segment must be found from there. Rows here: 0 seg11, 1 structure,
        // 2 seg12, 3 structure, 4 seg13.
        var nearest = Planner.GetMethod("NearestMatch", Any);
        Func<int, int, Func<int, bool>, int> near = (count, start, m) => (int)nearest.Invoke(null, new object[] { count, start, m });
        Func<int, bool> is12 = i => i == 2, is13 = i => i == 4, none = i => false;
        Check(near(5, 2, is12) == 2, "A row lookup: the editor's guess is the segment row itself: kept");
        Check(near(5, 1, is12) == 2, "A row lookup: the guess is the hidden structure row BEFORE segment 12: segment 12's row is found (the live bug)");
        Check(near(5, 3, is13) == 4, "A row lookup: the same for segment 13");
        Check(near(5, 3, is12) == 2, "A row lookup: a guess one row too far: the row before is found");
        Check(near(5, -1, is13) == 4, "A row lookup: no guess at all: the grid is searched");
        Check(near(5, 99, is13) == 4, "A row lookup: a guess outside the grid: the grid is searched, no exception");
        Check(near(1000, 0, i => i == 900) == 900, "A row lookup: the segment far from the guess: still found");
        Check(near(5, 2, none) == -1 && near(0, -1, none) == -1, "A row lookup: no row holds the segment: none, so the merge is refused as not in the grid");

        // Michael's case: segment 3's source as two runs (no tags).
        const string michael = "Gaat jouw pensioen over naar het nieuwe pensioen?  Dan krijg je een persoonlijke pensioenpot.";
        var s = Locate("Dan krijg je", false, Run(michael));
        Check(s.Error == null && s.Run == 0 && s.Offset == michael.IndexOf("Dan"), "A split before 'Dan krijg je': cursor on the 'D', got " + s.Offset + " " + s.Error);
        Check(s.First == "Gaat jouw pensioen over naar het nieuwe pensioen?" && s.Second == "Dan krijg je een persoonlijke pensioenpot.",
            "A the two parts reported without the spaces between them");
        s = Locate("  Dan krijg  ", false, Run("Vraag? Dan krijg je dat."));
        Check(s.Error == null && s.Offset == 7, "A spaces around splitBefore are ignored");
        s = Locate("t, then", false, Run("First par"), Run("t, then the rest."));
        Check(s.Error == null && s.Run == 1 && s.Offset == 0, "A text that runs over two text nodes is found; cursor in the node of its first character");
        s = Locate("rest", false, Run("Press "), Run("Start", new[] { 1 }), Run(" to begin, rest."));
        Check(s.Error == null && s.Run == 2 && s.Offset == 11 && s.Note == null, "A a tag pair earlier in the segment: no note, cursor in the right node");
        Check(Has(Locate("", false, Run("a. b.")).Error, "splitBefore"), "A no splitBefore: refused");
        var nf = Locate("Zebra", false, Run("Een. Twee."));
        Check(Has(nf.Error, "does not occur") && Has(nf.Error, "Een. Twee."), "A text not in the source: refused, and the source's plain text is quoted back");
        Check(Has(Locate("<t1>Twee", false, Run("Een. Twee.")).Error, "without tag markers"), "A tag markers in splitBefore: not found, and told to leave them out");
        Check(Has(Locate("de", false, Run("de een en de ander")).Error, "more than once"), "A text occurring twice: refused as ambiguous");
        Check(Has(Locate("Een", false, Run("Een. Twee.")).Error, "first part empty"), "A at the very start: refused");
        Check(Has(Locate("Twee", false, Run("   "), Run("Twee.")).Error, "first part empty"), "A only spaces before it: refused");
        Check(Has(Locate("Twee", false, Run("Een. ", locked: true), Run("Twee.")).Error, "locked"), "A next to locked content: refused");
        Check(Has(Locate("Twee", false, Run("Een. "), Run("Twee.", rev: true)).Error, "tracked change"), "A inside a tracked change: refused");
        var inside = Locate("Twee", false, Run("Een. ", new[] { 1 }), Run("Twee.", new[] { 1 }));
        Check(Has(inside.Error, "inside a tag pair") && Has(inside.Error, "allowInsideTags"), "A inside a tag pair: refused, names allowInsideTags");
        var nested = Locate("Twee", false, Run("Een. ", new[] { 1, 2 }), Run("Twee.", new[] { 1 }));
        Check(Has(nested.Error, "inside a tag pair"), "A inside an outer tag pair (inner one ends at the split): refused");
        var allowed = Locate("Twee", true, Run("Een. ", new[] { 1 }), Run("Twee.", new[] { 1 }));
        Check(allowed.Error == null && Has(allowed.Note, "cuts a tag pair"), "A inside a tag pair with allowInsideTags: allowed, with a note to check the tags");
        var edge = Locate("Twee", false, Run("Een. "), Run("Twee.", new[] { 3 }));
        Check(edge.Error == null && edge.Run == 1 && Has(edge.Note, "starts at the split"), "A a tag pair STARTING at the split: allowed (not cut), with a note");
        var between = Locate("Twee", false, Run("Een.", new[] { 1 }), Run(" "), Run("Twee.", new[] { 2 }));
        Check(between.Error == null && Has(between.Note, "ends or starts"), "A between two separate tag pairs: allowed, with a note");
    }

    // ── B: the handlers, on a stand-in document ────────────────────────
    static IDocumentItemFactory F;
    static IPropertiesFactory PF;
    static IText T(string s) { return F.CreateText(PF.CreateTextProperties(s)); }
    static ITagPair G(string id, bool group, params IAbstractMarkupData[] kids)
    {
        var st = PF.CreateStartTagProperties("<g id=\"" + id + "\">");
        st.TagId = new TagId(id);
        if (group) st.SetMetaData("TagType", "Group");
        var tp = F.CreateTagPair(st, PF.CreateEndTagProperties("</g>"));
        foreach (var k in kids) tp.Add(k);
        return tp;
    }
    static ISegmentPair Pair(string id, ConfirmationLevel level, bool locked, IAbstractMarkupData[] source, IAbstractMarkupData[] target)
    {
        var props = F.CreateSegmentPairProperties();
        props.Id = new SegmentId(id);
        props.ConfirmationLevel = level;
        props.IsLocked = locked;
        var s = F.CreateSegment(props); foreach (var i in source) s.Add(i);
        var t = F.CreateSegment(props); foreach (var i in target) t.Add(i);
        return F.CreateSegmentPair(s, t);
    }
    // Paragraph p1: 1, 2 (Draft), 3 (Translated), 4 (locked), 5 and 6 (targets share tag id 9),
    // 7 (group tag in target), 8; paragraph p2: 20.
    static FakeDocument Doc()
    {
        var d = new FakeDocument();
        var p1 = F.CreateParagraphUnit(LockTypeFlags.Unlocked); p1.Properties.ParagraphUnitId = new ParagraphUnitId("p1");
        var p2 = F.CreateParagraphUnit(LockTypeFlags.Unlocked); p2.Properties.ParagraphUnitId = new ParagraphUnitId("p2");
        Action<ISegmentPair, IParagraphUnit> add = (pair, pu) => { d.Pairs.Add(pair); d.PuOf[pair.Properties.Id.Id] = pu; };
        add(Pair("1", ConfirmationLevel.Draft, false, new IAbstractMarkupData[] { T("Is it moving? Then you get a pot.") }, new IAbstractMarkupData[] { T("Is it?") }), p1);
        add(Pair("2", ConfirmationLevel.Draft, false, new IAbstractMarkupData[] { T("Then you get a pot.") }, new IAbstractMarkupData[0]), p1);
        add(Pair("3", ConfirmationLevel.Translated, false, new IAbstractMarkupData[] { T("Done.") }, new IAbstractMarkupData[] { T("Klaar.") }), p1);
        add(Pair("4", ConfirmationLevel.Draft, true, new IAbstractMarkupData[] { T("Locked.") }, new IAbstractMarkupData[0]), p1);
        add(Pair("5", ConfirmationLevel.Draft, false, new IAbstractMarkupData[] { T("A") }, new IAbstractMarkupData[] { G("9", false, T("a")) }), p1);
        add(Pair("6", ConfirmationLevel.Draft, false, new IAbstractMarkupData[] { T("B") }, new IAbstractMarkupData[] { G("9", false, T("b")) }), p1);
        add(Pair("7", ConfirmationLevel.Draft, false, new IAbstractMarkupData[] { T("C") }, new IAbstractMarkupData[] { G("11", true, T("c")) }), p1);
        add(Pair("8", ConfirmationLevel.Draft, false, new IAbstractMarkupData[] { T("D") }, new IAbstractMarkupData[0]), p1);
        add(Pair("20", ConfirmationLevel.Draft, false, new IAbstractMarkupData[] { T("Other paragraph.") }, new IAbstractMarkupData[0]), p2);
        return d;
    }
    static string Snapshot(FakeDocument d)
    {
        var sb = new StringBuilder();
        foreach (var p in d.Pairs)
            sb.Append(p.Properties.Id.Id).Append('|').Append(p.Properties.ConfirmationLevel).Append('|').Append(p.Properties.IsLocked)
              .Append('|').Append(p.Source.ToString()).Append('|').Append(p.Target.ToString()).Append('\n');
        return sb.ToString();
    }
    static object Part(FakeDocument d)
    {
        var part = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(VP);
        Set(part, "_activeDocument", d);
        Set(part, "_fileIdToName", new Dictionary<string, string>());
        Set(part, "_puIdToFileId", new Dictionary<string, string>());
        Set(part, "_fileIdToSegmentKeys", new Dictionary<string, HashSet<string>>());
        return part;
    }
    static string Call(object part, string method, string json)
    {
        var m = VP.GetMethod(method, Any);
        var reqType = m.GetParameters()[0].ParameterType;
        object req;
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json))) req = new DataContractJsonSerializer(reqType).ReadObject(ms);
        object resp;
        try { resp = m.Invoke(part, new[] { req }); }
        catch (TargetInvocationException ex) { return "THREW " + ex.InnerException; }
        var ser = new DataContractJsonSerializer(resp.GetType());
        using (var ms = new MemoryStream()) { ser.WriteObject(ms, resp); return Encoding.UTF8.GetString(ms.ToArray()); }
    }
    static void Refused(string method, string json, string expect, string what, FakeDocument d = null)
    {
        d = d ?? Doc();
        var before = Snapshot(d);
        var answer = Call(Part(d), method, json);
        var unchanged = Snapshot(d) == before && d.Touched == 0;
        var ok = answer.StartsWith("{\"ok\":false") && Has(answer, expect) && unchanged;
        Check(ok, what + (ok ? "" : "  -> " + answer + (unchanged ? "" : " [DOCUMENT CHANGED or touched " + d.Touched + "x]")));
    }

    static void CaseHandlers(string stage)
    {
        const string M = "BridgeMergeSegments", S = "BridgeSplitSegment";
        Refused(M, "{\"ids\":[\"p1:1\"]}", "at least two", "B merge with one id: refused");
        Refused(M, "{}", "at least two", "B merge with no ids: refused");
        Refused(M, "{\"ids\":[\"p1:1\",\"nonsense\"]}", "malformed id", "B merge with a malformed id: refused");
        Refused(M, "{\"ids\":[\"p1:1\",\"p1:99\"]}", "not found", "B merge with an unknown id: refused");
        Refused(M, "{\"ids\":[\"p1:8\",\"p2:20\"]}", "different paragraphs", "B merge across paragraphs: refused");
        Refused(M, "{\"ids\":[\"p1:2\",\"p1:3\"]}", "confirmed (Translated)", "B merge with a Translated segment: refused before Studio could ask");
        Refused(M, "{\"ids\":[\"p1:3\",\"p1:4\"]}", "confirmed", "B merge Translated + locked: refused (first reason given)");
        Refused(M, "{\"ids\":[\"p1:4\",\"p1:5\"]}", "locked", "B merge with a locked segment: refused");
        Refused(M, "{\"ids\":[\"p1:5\",\"p1:6\"]}", "same tag id", "B merge of targets sharing tag id 9: refused before Studio could ask");
        Refused(M, "{\"ids\":[\"p1:7\",\"p1:8\"]}", "group tag", "B merge with a group tag first: refused before Studio's warning");
        Refused(M, "{\"ids\":[\"p1:1\",\"p1:1\"]}", "listed twice", "B merge with a repeated id: refused");
        Refused(S, "{\"splitBefore\":\"Then\"}", "give the id", "B split with no id: refused");
        Refused(S, "{\"id\":\"p1:99\",\"splitBefore\":\"Then\"}", "not found", "B split of an unknown id: refused");
        Refused(S, "{\"id\":\"p1:3\",\"splitBefore\":\"Done\"}", "confirmed (Translated)", "B split of a Translated segment: refused before Studio could ask");
        Refused(S, "{\"id\":\"p1:4\",\"splitBefore\":\"ed\"}", "locked", "B split of a locked segment: refused");

        // Valid requests that get past every check: the editor parts decide. Here
        // there is no running Studio, so they cannot be had - refused, unchanged.
        Refused(M, "{\"ids\":[\"p1:1\",\"p1:2\"]}", "does not have the part", "B " + stage + ": a valid merge is refused when Studio's editor parts are not there, and nothing changes");
        Refused(S, "{\"id\":\"p1:1\",\"splitBefore\":\"Then you\"}", "does not have the part", "B " + stage + ": a valid split likewise");
        var noDoc = Part(Doc()); Set(noDoc, "_activeDocument", null);
        Check(Has(Call(noDoc, M, "{\"ids\":[\"p1:1\",\"p1:2\"]}"), "no document is open"), "B merge with no document open: refused");
        Check(Has(Call(noDoc, S, "{\"id\":\"p1:1\",\"splitBefore\":\"x\"}"), "no document is open"), "B split with no document open: refused");
    }

    // ── C: Studio's parts ─────────────────────────────────────────────
    static void CaseStudioParts(string studioDir, bool is2022)
    {
        var cmds = P.GetType("Supervertaler.Trados.Core.StudioSegmentCommands", true);
        var find = cmds.GetMethod("FindParts", Any);
        var before = find.Invoke(null, new object[] { false });
        Check(Get(before, "Missing") != null, "C before Studio's editor is loaded: FindParts names a missing part and does not throw (" + Get(before, "Missing") + ")");

        foreach (var n in new[] { "Sdl.DesktopEditor.EditorApi", "Sdl.DesktopEditor.Control", "Sdl.TranslationStudio.Editor", "Sdl.Desktop.Platform" })
            Assembly.LoadFrom(Path.Combine(studioDir, n + ".dll"));
        var parts = find.Invoke(null, new object[] { false });
        var missing = (string)Get(parts, "Missing");
        if (is2022)
        {
            // Studio 2022's editor is built differently (no SideBySideEditor on the
            // view): the tools are not offered there. They must say so, not throw.
            Check(missing != null, "C Studio 2022: a part is reported missing, so the tools refuse there (" + missing + ")");
            var cmd2022 = find.Invoke(null, new object[] { true });
            Check(Get(cmd2022, "Missing") != null, "C Studio 2022: with the service lookup too");
            return;
        }
        Check(missing == null, "C every editor part the tools drive is in this Studio" + (missing == null ? "" : ": MISSING " + missing));
        foreach (var f in new[] { "SelectRowRange", "ClearRowSelections", "MoveTo", "GetSegment", "FindRow", "RowSegmentContainer", "ContainerSegment", "GetAction", "ActionExecute" })
            Check(Get(parts, f) != null, "C   " + f + " found" + (Get(parts, f) == null ? "" : " (" + Get(parts, f) + ")"));

        // The view field on the integration API's document class.
        var api = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "Sdl.TranslationStudioAutomation.IntegrationApi");
        var findField = cmds.GetMethod("FindViewField", Any);
        var viewProp = (PropertyInfo)Get(parts, "ViewEditor");
        var viewType = viewProp == null ? null : viewProp.DeclaringType;
        Type[] types;
        try { types = api.GetTypes(); } catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
        var holders = types.Where(t => t.IsClass && typeof(Sdl.TranslationStudioAutomation.IntegrationApi.IStudioDocument).IsAssignableFrom(t))
                           .Select(t => (FieldInfo)findField.Invoke(null, new object[] { t })).Where(f => f != null).ToList();
        Check(holders.Count > 0 && holders.All(f => viewType != null && f.FieldType.IsAssignableFrom(viewType)),
            "C Studio's document class has the " + Get(null, cmds, "ViewFieldName") + " field, of the side-by-side view's type");

        // The command ids are string literals in the commands' constructors.
        var bytes = File.ReadAllBytes(Path.Combine(studioDir, "Sdl.TranslationStudio.Editor.dll"));
        foreach (var id in new[] { "MergeSegmentsAction", "SplitSegmentsAction" })
        {
            var needle = Encoding.Unicode.GetBytes(id);
            Check(IndexOf(bytes, needle) >= 0, "C the command id \"" + id + "\" is in Studio's editor");
        }

        // With the service: no running Studio, so it cannot be had - named, not thrown.
        var withService = find.Invoke(null, new object[] { true });
        var why = (string)Get(withService, "Missing");
        Check(why != null, "C outside Studio the action service is reported missing, not thrown: " + why);
    }
    static object Get(object o, Type t, string name) { return t.GetField(name, Any).GetValue(o); }
    static int IndexOf(byte[] hay, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= hay.Length; i++)
        {
            int k = 0;
            while (k < needle.Length && hay[i + k] == needle[k]) k++;
            if (k == needle.Length) return i;
        }
        return -1;
    }

    public static int Run(string pluginPath, string studioDir)
    {
        P = Assembly.LoadFrom(pluginPath);
        VP = T_("AiAssistantViewPart");
        Planner = P.GetType("Supervertaler.Trados.Core.SegmentStructurePlanner", true);
        F = (IDocumentItemFactory)Activator.CreateInstance(Type.GetType(
            "Sdl.FileTypeSupport.Framework.Bilingual.DocumentItemFactory, Sdl.FileTypeSupport.Framework.Implementation", true));
        PF = (IPropertiesFactory)Activator.CreateInstance(Type.GetType(
            "Sdl.FileTypeSupport.Framework.Native.PropertiesFactory, Sdl.FileTypeSupport.Framework.Implementation", true));
        F.PropertiesFactory = PF;

        // The AI Assistant panel as a bare UserControl: no handle, no marshalling.
        var panelType = T_("Controls.AiAssistantControl");
        var panel = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(panelType);
        typeof(System.Windows.Forms.UserControl).GetConstructor(Type.EmptyTypes).Invoke(panel, null);
        var lazyFactory = Expression.Lambda(typeof(Func<>).MakeGenericType(panelType), Expression.Constant(panel, panelType)).Compile();
        VP.GetField("_control", Any).SetValue(null, Activator.CreateInstance(typeof(Lazy<>).MakeGenericType(panelType), lazyFactory));

        CasePlanner();
        CaseHandlers("Studio's editor not loaded");
        var is2022 = studioDir.EndsWith("Studio17", StringComparison.OrdinalIgnoreCase);
        CaseStudioParts(studioDir, is2022);
        CaseHandlers("editor loaded, no running Studio");
        Console.WriteLine(Pass + " passed, " + Fail + " failed");
        return Fail == 0 ? 0 : 1;
    }
}
'@

foreach ($n in @('Sdl.FileTypeSupport.Framework.Core.dll', 'Sdl.Core.Globalization.dll')) { [void]$refs.Add((Join-Path $StudioDir $n)) }
[void]$refs.Add($iface.Assembly.Location)
[void]$refs.Add($Dll)
$all = @($refs) + @('System.dll', 'System.Core.dll', 'System.Runtime.Serialization.dll', 'System.Xml.dll', 'System.Windows.Forms.dll')
Add-Type -TypeDefinition ($src + "`n" + $sb.ToString()) -ReferencedAssemblies $all
try { $code = [MergeSplitTest]::Run($Dll, $StudioDir) }
catch { $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }; Write-Host $e.ToString(); exit 2 }
exit $code
