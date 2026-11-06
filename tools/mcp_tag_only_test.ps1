# MCP: segments of tags only (18.20.205 / 19.20.205). Runs the BUILT plugin's
# own bridge handlers - get_segments (BuildBridgeSegments), update_segments
# (BridgeUpdateSegments), check_tags (BridgeRunQaCheck), get_coverage and
# get_active_project's count (BuildBridgeProjectSnapshot) - on REAL segments
# from Studio's own factory, through a stand-in for Studio's document
# (IStudioDocument, generated from Studio's interface) whose
# ProcessSegmentPair hands the edit a copy and applies it unless cancelled.
# Requests go in as the JSON an MCP client sends; answers come back as the
# JSON it receives. Synthetic Acme segments only: no client text.
#
# The handoff's acceptance tests, and the cases around them:
#   A  get_segments lists a segment of tags only, marked tagOnly, so with no
#      filters it returns get_active_project's totalSegments; one asked for by
#      number comes back; an EMPTY segment is not listed but named in
#      'omitted' (returned + omittedCount = totalSegments).
#   B  update_segments copySource: the target becomes a clone of the source
#      (same items, same tag ids - what Ctrl+Insert gives), check_tags then
#      reports nothing for it; with status, fp, locks, Track Changes and the
#      target's comments; refused with 'target', on an empty source, on a
#      stale fp.
#   C  a target with wrong tag markers and NO fp is refused and nothing
#      changes (target, status, lock); with an fp, or force, it is written
#      with a warning as before; the source's exact markers now build on a
#      segment of tags only (they built nothing before).
#   D  existing text-segment writes behave exactly as before: with
#      -BaselineDll (a build of the commit before this change), every text
#      case is run on both builds and their answers must be identical.
#   E  <tN/> written for a source tag PAIR (206): a tag mismatch like any
#      other - refused without an fp, warned with one, and never the
#      source text inside the pair copied into the target.
#   F  <tN>text</tN> written for a source PLACEHOLDER (206): the text is
#      kept, the tag left out - a tag mismatch, never the text dropped.
#   K  check_tags and get_coverage now see segments of tags only.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\mcp_tag_only_test.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\mcp_tag_only_test.ps1 -BaselineDll <older Supervertaler.Trados.dll>
#
# What it cannot reach, and the live test in Studio covers: Studio's own
# ProcessSegmentPair and Ctrl+Insert side by side, the saved SDLXLIFF, and the
# MCP server exe (which forwards update_segments' body verbatim).
param(
    [string]$Dll = (Join-Path (Split-Path -Parent $PSScriptRoot) "src\Supervertaler.Trados\bin\Studio19\Debug\Supervertaler.Trados.dll"),
    [string]$BaselineDll,
    [string]$Root = (Join-Path $env:TEMP "sv-mcp-tag-only-test"),
    # Internal: set on the child runs.
    [switch]$Child,
    [switch]$TextCasesOnly,
    [string]$TextCasesOut
)
$ErrorActionPreference = 'Stop'

$Root = [IO.Path]::GetFullPath($Root)
if ($Root -like 'D:\Supervertaler*' -or $Root.Length -lt 10) { throw "Refusing to use $Root as the test root." }
$Dll = [IO.Path]::GetFullPath($Dll)
if (-not (Test-Path -LiteralPath $Dll)) { throw "No plugin build at $Dll" }

function PowerShellFor([string]$dll) {
    if ($dll -like '*Studio19*') { Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe' }
    else { Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe' }
}

if (-not $Child) {
    # ── Parent: run the cases in child processes, compare, guard real files ──
    if (Test-Path -LiteralPath $Root) { Remove-Item -LiteralPath $Root -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $Root | Out-Null

    $protected = @(
        'D:\Supervertaler\settings\api-keys.json',
        (Join-Path $env:APPDATA 'Supervertaler\api-keys.locked.json'),
        (Join-Path $env:APPDATA 'Supervertaler\config.json'),
        'D:\Supervertaler\resources\supervertaler.db'
    )
    $lad = Join-Path $env:LOCALAPPDATA 'Supervertaler.Trados'
    if (Test-Path -LiteralPath $lad) { $protected += @(Get-ChildItem -LiteralPath $lad -File | ForEach-Object { $_.FullName }) }
    function Snapshot { $protected | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { $i = Get-Item -LiteralPath $_; "$($i.FullName)|$($i.Length)|$($i.LastWriteTimeUtc.Ticks)" } }
    $before = @(Snapshot)

    function RunChild([string]$dll, [string]$root, [string[]]$extra) {
        $psi = New-Object System.Diagnostics.ProcessStartInfo (PowerShellFor $dll)
        $psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Child -Dll `"$dll`" -Root `"$root`" " + ($extra -join ' ')
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.CreateNoWindow = $true
        $p = [Diagnostics.Process]::Start($psi)
        $out = $p.StandardOutput.ReadToEndAsync()
        $err = $p.StandardError.ReadToEndAsync()
        $p.WaitForExit()
        foreach ($line in (($out.Result + $err.Result) -split "`r?`n")) { if ($line.Trim()) { Write-Host "  $line" } }
        return $p.ExitCode
    }

    Write-Host "Plugin: $Dll"
    $newJson = Join-Path $Root 'text-cases.new.json'
    $code = RunChild $Dll (Join-Path $Root 'new') @('-TextCasesOut', "`"$newJson`"")
    $failed = $code -ne 0

    if ($BaselineDll) {
        $BaselineDll = [IO.Path]::GetFullPath($BaselineDll)
        Write-Host ""
        Write-Host "Baseline (text cases only): $BaselineDll"
        $baseJson = Join-Path $Root 'text-cases.baseline.json'
        $bcode = RunChild $BaselineDll (Join-Path $Root 'baseline') @('-TextCasesOnly', '-TextCasesOut', "`"$baseJson`"")
        if ($bcode -ne 0 -or -not (Test-Path -LiteralPath $baseJson)) {
            Write-Host "FAIL  the baseline run did not complete (exit $bcode)"; $failed = $true
        } elseif (-not (Test-Path -LiteralPath $newJson)) {
            Write-Host "FAIL  the run of this build wrote no text-case results to compare"; $failed = $true
        } else {
            Add-Type -AssemblyName System.Web.Extensions
            $js = New-Object System.Web.Script.Serialization.JavaScriptSerializer
            $js.MaxJsonLength = 64MB
            $a = $js.DeserializeObject([IO.File]::ReadAllText($baseJson))
            $b = $js.DeserializeObject([IO.File]::ReadAllText($newJson))
            $same = 0
            # The one intended difference: the "nothing to do" error now also names copySource.
            # Normalised on both sides, so a baseline from 205 or later compares too.
            function Normalise([string]$s) { $s.Replace("provide 'target' (or copySource: true), 'status'", "provide 'target', 'status'") }
            foreach ($k in $a.Keys) {
                if (-not $b.ContainsKey($k)) { Write-Host "FAIL  D text case '$k' missing from the new run"; $failed = $true; continue }
                if ((Normalise $a[$k]) -ceq (Normalise $b[$k])) { $same++ }
                else {
                    Write-Host "FAIL  D text case '$k' answers differently from the baseline"
                    Write-Host "        baseline: $($a[$k])"
                    Write-Host "        new:      $($b[$k])"
                    $failed = $true
                }
            }
            Write-Host ("{0}  D {1} of {2} text-segment cases answer exactly as the baseline build does" -f $(if ($same -eq $a.Count -and $a.Count -gt 0) { 'PASS' } else { 'FAIL' }), $same, $a.Count)
            if ($a.Count -eq 0) { $failed = $true }
        }
    }

    $after = @(Snapshot)
    if ((Compare-Object $before $after)) { Write-Host "FAIL  a real Supervertaler file changed during the test"; $failed = $true }
    else { Write-Host "PASS  real files unchanged ($($before.Count) checked: size and modified time)" }
    if ($failed) { Write-Host "RESULT: FAILED"; exit 1 }
    Write-Host "RESULT: all passed"
    exit 0
}

# ── Child: the harness ─────────────────────────────────────────────────────
if (Test-Path -LiteralPath $Root) { Remove-Item -LiteralPath $Root -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $Root 'data') | Out-Null
$studio = if ($Dll -like '*Studio19*') { 'C:\Program Files\Trados\Trados Studio\Studio19' }
          elseif ($Dll -like '*Studio17*') { 'C:\Program Files (x86)\Trados\Trados Studio\Studio17' }
          else { 'C:\Program Files (x86)\Trados\Trados Studio\Studio18' }
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
public static class TagOnlyResolver
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
[TagOnlyResolver]::Register(@((Split-Path $Dll), $studio))
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Web.Extensions
$plugin = [Reflection.Assembly]::LoadFrom($Dll)
$dataRoot = [string](Join-Path $Root 'data')
$plugin.GetType("Supervertaler.Core.SupervertalerPaths").GetMethod("Set").Invoke($null, @($dataRoot))
$rlf = $plugin.GetType("Supervertaler.Core.ApiKeyStore").GetMethod("RedirectLockedFile", [Reflection.BindingFlags]"Static,NonPublic")
if ($rlf) { [void]$rlf.Invoke($null, [object[]]@([string](Join-Path $dataRoot "api-keys.locked.json"))) }

# The stand-in document: IStudioDocument generated from this Studio's own
# interface, every member throwing except the ones the bridge paths use.
$api = [Reflection.Assembly]::LoadFrom((Join-Path $studio 'Sdl.TranslationStudioAutomation.IntegrationApi.dll'))
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
$special = @('ProcessSegmentPair', 'GetParentParagraphUnit', 'GetSegmentPairsFromParagraph', 'UpdateSegmentPairProperties')
$specialProps = @('SegmentPairs')
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('public partial class FakeDocument : ' + (CsType $iface) + ' {')
foreach ($i in @($iface) + $iface.GetInterfaces()) {
    $in = CsType $i
    foreach ($p in $i.GetProperties()) {
        $ix = @($p.GetIndexParameters())
        $head = if ($ix.Count -gt 0) {
            $k = 0; $ps = @($ix | ForEach-Object { (CsType $_.ParameterType) + ' i' + ($k++) })
            "$in.this[" + ($ps -join ', ') + "]"
        } else { "$in." + $p.Name }
        $body = ''
        if ($p.CanRead) {
            $body += if ($specialProps -contains $p.Name -and $ix.Count -eq 0) { ' get { return Fake' + $p.Name + '; }' }
                     else { ' get { throw new global::System.NotImplementedException(); }' }
        }
        if ($p.CanWrite) { $body += ' set { throw new global::System.NotImplementedException(); }' }
        [void]$sb.AppendLine('  ' + (CsType $p.PropertyType) + ' ' + $head + ' {' + $body + ' }')
    }
    foreach ($e in $i.GetEvents()) {
        [void]$sb.AppendLine('  event ' + (CsType $e.EventHandlerType) + " $in." + $e.Name + ' { add { } remove { } }')
    }
    foreach ($m in $i.GetMethods()) {
        if ($m.IsSpecialName) { continue }
        if ($m.IsGenericMethodDefinition) { throw "Generic interface method $($m.Name): extend the generator." }
        $k = 0; $names = @(); $ps = @()
        foreach ($pa in $m.GetParameters()) {
            $mod = if ($pa.ParameterType.IsByRef) { if ($pa.IsOut) { 'out ' } else { 'ref ' } } else { '' }
            $n = 'p' + ($k++); $names += ($mod + $n)
            $ps += ($mod + (CsType $pa.ParameterType) + ' ' + $n)
        }
        $ret = CsType $m.ReturnType
        $call = 'Fake' + $m.Name + '(' + ($names -join ', ') + ')'
        $body = if ($special -contains $m.Name) { if ($ret -eq 'void') { "$call;" } else { "return $call;" } }
                else { 'throw new global::System.NotImplementedException();' }
        [void]$sb.AppendLine('  ' + $ret + " $in." + $m.Name + '(' + ($ps -join ', ') + ') { ' + $body + ' }')
    }
}
[void]$sb.AppendLine('}')
$stub = $sb.ToString()

$src = @'
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Web.Script.Serialization;
using Sdl.Core.Globalization;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Sdl.FileTypeSupport.Framework.NativeApi;
using Supervertaler.Trados.Core;

// A segment pair every member of which throws: a segment the bridge cannot read.
public sealed class ThrowingProxy : System.Runtime.Remoting.Proxies.RealProxy
{
    public ThrowingProxy(Type t) : base(t) { }
    public override System.Runtime.Remoting.Messaging.IMessage Invoke(System.Runtime.Remoting.Messaging.IMessage msg)
    {
        return new System.Runtime.Remoting.Messaging.ReturnMessage(
            new InvalidOperationException("fake: an unreadable segment"), (System.Runtime.Remoting.Messaging.IMethodCallMessage)msg);
    }
}

// What TrackingSuspension reaches through the document's InternalDocument.
public sealed class FakeEditor
{
    public bool TrackChanges;
    public bool Protect = true;
    public int TurnOffs, Reverts;
}

namespace Sdl.TranslationStudioAutomation.IntegrationApi.Internal
{
    // Studio's internal class of this name, as TrackingSuspension uses it.
    internal sealed class TrackChangesState
    {
        private readonly FakeEditor _editor;
        private readonly bool _trackChanges;
        private readonly bool _protect;
        internal TrackChangesState(FakeEditor editor)
        {
            _editor = editor;
            _trackChanges = editor.TrackChanges;
            _protect = editor.Protect;
        }
        internal void TurnOffTracking() { _editor.TrackChanges = false; _editor.Protect = false; _editor.TurnOffs++; }
        internal void RevertTrackingToOriginalState() { _editor.TrackChanges = _trackChanges; _editor.Protect = _protect; _editor.Reverts++; }
    }
}

// One paragraph of segments. ProcessSegmentPair hands the edit a copy of the
// pair and applies its target unless the edit cancels - as Studio does.
public partial class FakeDocument
{
    public readonly FakeEditor Editor = new FakeEditor();
    internal object InternalDocument { get { return Editor; } }
    public IDocumentItemFactory F;
    public List<ISegmentPair> Pairs = new List<ISegmentPair>();
    public IParagraphUnit Pu;
    public int Edits, Applied, Cancelled, StatusWrites;

    ISegmentPair LiveOf(ISegmentPair pair) { return Pairs.First(p => p.Properties.Id.Id == pair.Properties.Id.Id); }
    void FakeProcessSegmentPair(ISegmentPair pair, string undoName, Action<ISegmentPair, CancelEventArgs> edit)
    {
        var live = LiveOf(pair);
        var copy = F.CreateSegmentPair((ISegment)live.Source.Clone(), (ISegment)live.Target.Clone());
        var cancel = new CancelEventArgs();
        Edits++;
        edit(copy, cancel);
        if (cancel.Cancel) { Cancelled++; return; }
        Applied++;
        live.Target.Clear();
        copy.Target.MoveAllItemsTo(live.Target);
    }
    IParagraphUnit FakeGetParentParagraphUnit(ISegmentPair pair) { return Pu; }
    IEnumerable<ISegmentPair> FakeGetSegmentPairsFromParagraph(object id) { return Pairs; }
    void FakeUpdateSegmentPairProperties(object pair, object props) { StatusWrites++; }
    IEnumerable<ISegmentPair> FakeSegmentPairs { get { return Pairs; } }
}

public static class TagOnlyTest
{
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    public static int Pass, Fail;
    static Assembly P;
    static Type VP;
    static readonly JavaScriptSerializer Js = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };

    static void Check(bool ok, string what)
    {
        if (ok) Pass++; else Fail++;
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what);
    }
    static void Say(string what) { Console.WriteLine("      " + what); }
    static Type T_(string name) { return P.GetType("Supervertaler.Trados." + name, true); }
    static void Set(object o, string name, object value) { o.GetType().GetField(name, Any).SetValue(o, value); }

    // ── Real segments, from Studio's own factory ──────────────────────
    static IDocumentItemFactory F;
    static IPropertiesFactory PF;
    static void Factory()
    {
        F = (IDocumentItemFactory)Activator.CreateInstance(Type.GetType(
            "Sdl.FileTypeSupport.Framework.Bilingual.DocumentItemFactory, Sdl.FileTypeSupport.Framework.Implementation", true));
        PF = (IPropertiesFactory)Activator.CreateInstance(Type.GetType(
            "Sdl.FileTypeSupport.Framework.Native.PropertiesFactory, Sdl.FileTypeSupport.Framework.Implementation", true));
        F.PropertiesFactory = PF;
    }
    static IText T(string s) { return F.CreateText(PF.CreateTextProperties(s)); }
    static ITagPair G(string id, params IAbstractMarkupData[] kids)
    {
        var st = PF.CreateStartTagProperties("<g id=\"" + id + "\">");
        st.TagId = new TagId(id);
        var tp = F.CreateTagPair(st, PF.CreateEndTagProperties("</g>"));
        foreach (var k in kids) tp.Add(k);
        return tp;
    }
    static IPlaceholderTag X(string id)
    {
        var p = PF.CreatePlaceholderTagProperties("<x id=\"" + id + "\"/>");
        p.TagId = new TagId(id);
        return F.CreatePlaceholderTag(p);
    }
    static ICommentMarker Comment(string text, params IAbstractMarkupData[] kids)
    {
        var props = PF.CreateCommentProperties();
        props.Add(PF.CreateComment(text, "Acme reviser", Severity.Medium));
        var m = F.CreateCommentMarker(props);
        foreach (var k in kids) m.Add(k);
        return m;
    }
    static IRevisionMarker Deleted(params IAbstractMarkupData[] kids)
    {
        var r = F.CreateRevision(PF.CreateRevisionProperties(RevisionType.Delete));
        foreach (var k in kids) r.Add(k);
        return r;
    }

    // A segment spec: its source, its target, and what to set on the pair.
    sealed class Spec
    {
        public string Id;
        public Func<IAbstractMarkupData[]> Source = () => new IAbstractMarkupData[0];
        public Func<IAbstractMarkupData[]> Target = () => new IAbstractMarkupData[0];
        public ConfirmationLevel Level = ConfirmationLevel.Unspecified;
        public bool Locked;
    }
    static ISegmentPair Pair(Spec s)
    {
        var props = F.CreateSegmentPairProperties();
        props.Id = new SegmentId(s.Id);
        props.ConfirmationLevel = s.Level;
        props.IsLocked = s.Locked;
        var source = F.CreateSegment(props);
        foreach (var i in s.Source()) source.Add(i);
        var target = F.CreateSegment(props);
        foreach (var i in s.Target()) target.Add(i);
        return F.CreateSegmentPair(source, target);
    }
    static FakeDocument Doc(params Spec[] specs)
    {
        var pu = F.CreateParagraphUnit(LockTypeFlags.Unlocked);
        pu.Properties.ParagraphUnitId = new ParagraphUnitId("pu-1");
        var d = new FakeDocument { F = F, Pu = pu };
        foreach (var s in specs) d.Pairs.Add(Pair(s));
        return d;
    }

    // The Acme fixture. 2 and 3 hold only tags - 2 is the field report's
    // shape (<x/><x/><g><x/></g>, two email links); 5 holds nothing at all.
    static Spec S1(string target = "") { return new Spec { Id = "1", Source = () => new IAbstractMarkupData[] { T("Contact Acme support.") }, Target = () => target.Length == 0 ? new IAbstractMarkupData[0] : new IAbstractMarkupData[] { T(target) } }; }
    static Spec S2() { return new Spec { Id = "2", Source = () => new IAbstractMarkupData[] { X("795"), X("810"), G("821", X("826")) } }; }
    static Spec S3() { return new Spec { Id = "3", Source = () => new IAbstractMarkupData[] { X("901") } }; }
    static Spec S4() { return new Spec { Id = "4", Source = () => new IAbstractMarkupData[] { T("Press "), G("5", T("Start")), T(" to begin.") } }; }
    static Spec S5() { return new Spec { Id = "5" }; }
    static Spec S6() { return new Spec { Id = "6", Source = () => new IAbstractMarkupData[] { T("Acme Ltd, Leeds.") }, Target = () => new IAbstractMarkupData[] { T("Acme Ltd, Leeds (NL).") }, Level = ConfirmationLevel.Translated, Locked = true }; }
    static FakeDocument TextDoc() { return Doc(S1(), S4(), S6()); }
    static FakeDocument TagDoc() { return Doc(S1(), S2(), S3(), S4(), S6()); }
    static FakeDocument FullDoc() { return Doc(S1(), S2(), S3(), S4(), S5(), S6()); }

    static ISegmentPair Seg(FakeDocument d, string id) { return d.Pairs.First(p => p.Properties.Id.Id == id); }

    // What a segment holds, item by item, as it reads (tracked deletions out,
    // tracked insertions as plain content, neighbouring text joined): kind,
    // tag id, tag content, text. Two of these equal = the same markup.
    static string Shape(IAbstractMarkupDataContainer c)
    {
        var sb = new StringBuilder();
        var text = new StringBuilder();
        ShapeInto(c, sb, text);
        Flush(sb, text);
        return sb.ToString();
    }
    static void Flush(StringBuilder sb, StringBuilder text)
    {
        if (text.Length > 0) { sb.Append('"').Append(text).Append('"'); text.Clear(); }
    }
    static void ShapeInto(IAbstractMarkupDataContainer c, StringBuilder sb, StringBuilder text)
    {
        foreach (var item in c)
        {
            if (item is IRevisionMarker)
            {
                var r = (IRevisionMarker)item;
                if (r.Properties.RevisionType != RevisionType.Delete) ShapeInto(r, sb, text);
            }
            else if (item is IText) text.Append(((IText)item).Properties.Text);
            else
            {
                Flush(sb, text);
                if (item is ICommentMarker) sb.Append("{c:").Append(Shape((ICommentMarker)item)).Append("}");
                else if (item is ITagPair)
                {
                    var p = (ITagPair)item;
                    sb.Append("[g").Append(p.StartTagProperties.TagId.Id).Append(' ').Append(p.StartTagProperties.TagContent).Append(':')
                      .Append(Shape(p)).Append(']');
                }
                else if (item is IPlaceholderTag)
                {
                    var x = (IPlaceholderTag)item;
                    sb.Append("[x").Append(x.Properties.TagId.Id).Append(' ').Append(x.Properties.TagContent).Append(']');
                }
                else sb.Append("[" + item.GetType().Name + "]");
            }
        }
    }
    static string ShapeNoComments(IAbstractMarkupDataContainer c)
    {
        var s = Shape(c);
        // drop the comment wrappers, keep what they wrap
        while (true)
        {
            int i = s.IndexOf("{c:", StringComparison.Ordinal);
            if (i < 0) return s;
            int depth = 0, j = i;
            for (; j < s.Length; j++)
            {
                if (s[j] == '{') depth++;
                else if (s[j] == '}') { depth--; if (depth == 0) break; }
            }
            s = s.Substring(0, i) + s.Substring(i + 3, j - i - 3) + s.Substring(j + 1);
        }
    }
    static int Comments(IAbstractMarkupDataContainer c)
    {
        int n = 0;
        foreach (var item in c)
        {
            if (item is ICommentMarker) n++;
            if (item is IAbstractMarkupDataContainer) n += Comments((IAbstractMarkupDataContainer)item);
        }
        return n;
    }
    static int Revisions(IAbstractMarkupDataContainer c)
    {
        int n = 0;
        foreach (var item in c)
        {
            if (item is IRevisionMarker) n++;
            if (item is IAbstractMarkupDataContainer) n += Revisions((IAbstractMarkupDataContainer)item);
        }
        return n;
    }

    // ── The view part, holding only what the bridge paths use ─────────
    static object Part(FakeDocument d)
    {
        var part = FormatterServices.GetUninitializedObject(VP);
        Set(part, "_activeDocument", d);
        Set(part, "_fileIdToName", new Dictionary<string, string>());
        Set(part, "_puIdToFileId", new Dictionary<string, string>());
        Set(part, "_fileIdToSegmentKeys", new Dictionary<string, HashSet<string>>());
        Set(part, "_bridgeCoverageLock", new object());
        Set(part, "_bridgeWrittenIds", new HashSet<string>(StringComparer.Ordinal));
        Set(part, "_bridgeReviewedIds", new HashSet<string>(StringComparer.Ordinal));
        return part;
    }
    static object Call(object part, string method, object arg)
    {
        var m = VP.GetMethod(method, Any);
        try { return m.Invoke(part, m.GetParameters().Length == 0 ? new object[0] : new[] { arg }); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    static string ToJson(object o)
    {
        var ser = new DataContractJsonSerializer(o.GetType());
        using (var ms = new MemoryStream()) { ser.WriteObject(ms, o); return Encoding.UTF8.GetString(ms.ToArray()); }
    }
    static object FromJson(Type t, string json)
    {
        var ser = new DataContractJsonSerializer(t);
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json))) return ser.ReadObject(ms);
    }
    static string Q(string s) { return Js.Serialize(s); }

    sealed class Seen
    {
        public string Json;                      // the answer, as the MCP client gets it
        public Dictionary<string, object> R;     // the same, parsed
    }
    static Seen GetSegments(FakeDocument d, Action<BridgeSegmentsQuery> q = null, object part = null)
    {
        var query = new BridgeSegmentsQuery();
        if (q != null) q(query);
        var json = ToJson(Call(part ?? Part(d), "BuildBridgeSegments", query));
        return new Seen { Json = json, R = (Dictionary<string, object>)Js.DeserializeObject(json) };
    }
    static Seen Update(FakeDocument d, string requestJson, object part = null)
    {
        var req = FromJson(typeof(BridgeUpdateSegmentsRequest), requestJson);
        var json = ToJson(Call(part ?? Part(d), "BridgeUpdateSegments", req));
        return new Seen { Json = json, R = (Dictionary<string, object>)Js.DeserializeObject(json) };
    }
    static Seen QaTags(object part)
    {
        var json = ToJson(Call(part, "BridgeRunQaCheck", new BridgeQaQuery { Type = "tags", Limit = 50 }));
        return new Seen { Json = json, R = (Dictionary<string, object>)Js.DeserializeObject(json) };
    }
    static List<Dictionary<string, object>> List(Dictionary<string, object> r, string key)
    {
        object v;
        if (r == null || !r.TryGetValue(key, out v) || v == null) return new List<Dictionary<string, object>>();
        return ((object[])v).Cast<Dictionary<string, object>>().ToList();
    }
    static string Str(Dictionary<string, object> r, string key) { object v; return r != null && r.TryGetValue(key, out v) && v != null ? v.ToString() : null; }
    static int Int(Dictionary<string, object> r, string key) { object v; return r != null && r.TryGetValue(key, out v) && v != null ? Convert.ToInt32(v) : 0; }
    static bool Bool(Dictionary<string, object> r, string key) { object v; return r != null && r.TryGetValue(key, out v) && v is bool && (bool)v; }
    static Dictionary<string, object> Item(Seen s, int i = 0) { var l = List(s.R, "results"); return i < l.Count ? l[i] : null; }
    static Dictionary<string, object> SegRec(Seen s, string number) { return List(s.R, "segments").FirstOrDefault(x => Str(x, "number") == number); }

    // ── D: text segments, run on this build AND the baseline ──────────
    // Each case: a fresh document, one call, and everything it left behind.
    static readonly SortedDictionary<string, string> TextOutcomes = new SortedDictionary<string, string>(StringComparer.Ordinal);
    static string Outcome(FakeDocument d, Seen s)
    {
        var sb = new StringBuilder(s.Json);
        foreach (var p in d.Pairs)
            sb.Append(" | ").Append(p.Properties.Id.Id).Append(": ").Append(Shape(p.Target))
              .Append(' ').Append(p.Properties.ConfirmationLevel).Append(p.Properties.IsLocked ? " locked" : "");
        sb.Append(" | edits=").Append(d.Edits).Append(" applied=").Append(d.Applied).Append(" statusWrites=").Append(d.StatusWrites);
        return sb.ToString();
    }
    static void TextCase(string name, Func<FakeDocument, Seen> run, Func<FakeDocument, Seen, bool> expect, string what)
    {
        var d = TextDoc();
        Seen s;
        try { s = run(d); }
        catch (Exception ex) { Check(false, "D " + name + ": threw " + ex.GetType().Name + ": " + ex.Message); return; }
        TextOutcomes[name] = Outcome(d, s);
        Check(expect(d, s), "D " + name + ": " + what);
    }
    static string Src(FakeDocument d, string number)
    {
        return Str(SegRec(GetSegments(d), number), "source");
    }
    static string Fp(FakeDocument d, string number)
    {
        return Str(SegRec(GetSegments(d), number), "fp");
    }
    static void TextCases()
    {
        TextCase("get_segments, no filters", d => GetSegments(d),
            (d, s) => Int(s.R, "returned") == 3 && !s.Json.Contains("tagOnly") && !s.Json.Contains("omitted"),
            "the three text segments, nothing else in the answer");
        TextCase("get_segments, number 4", d => GetSegments(d, q => { q.FromNumber = 4; q.ToNumber = 4; }),
            (d, s) => Int(s.R, "returned") == 1, "one segment");
        TextCase("plain write, no fp", d => Update(d, "{\"updates\":[{\"id\":\"pu-1:1\",\"target\":\"Neem contact op met Acme.\"}]}"),
            (d, s) => Int(s.R, "applied") == 1 && Shape(Seg(d, "1").Target) == "\"Neem contact op met Acme.\"" && Seg(d, "1").Properties.ConfirmationLevel == ConfirmationLevel.Draft,
            "written, Draft");
        TextCase("plain write, fp, Translated", d => Update(d, "{\"updates\":[{\"id\":\"pu-1:1\",\"fp\":" + Q(Fp(d, "1")) + ",\"target\":\"Neem contact op met Acme.\",\"status\":\"Translated\"}]}"),
            (d, s) => Int(s.R, "applied") == 1 && Seg(d, "1").Properties.ConfirmationLevel == ConfirmationLevel.Translated,
            "written, Translated");
        TextCase("tagged write, the source's markers, fp", d =>
            {
                var src = Src(d, "4");
                return Update(d, "{\"updates\":[{\"id\":\"pu-1:4\",\"fp\":" + Q(Fp(d, "4")) + ",\"target\":" + Q(src.Replace("Press ", "Druk op ").Replace(" to begin.", " om te beginnen.")) + "}]}");
            },
            (d, s) => Int(s.R, "applied") == 1 && Str(Item(s), "warning") == null && Shape(Seg(d, "4").Target).Contains("[g5 "),
            "written with the source's tag, no warning");
        TextCase("tagged write, the source's markers, no fp", d =>
            {
                var src = Src(d, "4");
                return Update(d, "{\"updates\":[{\"id\":\"pu-1:4\",\"target\":" + Q(src.Replace("Press ", "Druk op ").Replace(" to begin.", " om te beginnen.")) + "}]}");
            },
            (d, s) => Int(s.R, "applied") == 1 && Str(Item(s), "warning") == null,
            "tags match, so no fp is needed: written as before");
        TextCase("tagged write, tags dropped, fp", d => Update(d, "{\"updates\":[{\"id\":\"pu-1:4\",\"fp\":" + Q(Fp(d, "4")) + ",\"target\":\"Druk op Start om te beginnen.\"}]}"),
            (d, s) => Int(s.R, "applied") == 1 && (Str(Item(s), "warning") ?? "").Contains("tag-id mismatch"),
            "with an fp a mismatch is still written, with a warning");
        TextCase("tagged write, unknown marker, fp", d => Update(d, "{\"updates\":[{\"id\":\"pu-1:4\",\"fp\":" + Q(Fp(d, "4")) + ",\"target\":\"Druk op <t9>Start</t9> om te beginnen.\"}]}"),
            (d, s) => Int(s.R, "applied") == 1, "written");
        TextCase("empty target refused", d => Update(d, "{\"updates\":[{\"id\":\"pu-1:1\",\"target\":\"\"}]}"),
            (d, s) => Int(s.R, "failed") == 1 && d.Edits == 0, "refused");
        TextCase("empty target, allowEmpty", d => Update(d, "{\"updates\":[{\"id\":\"pu-1:6\",\"target\":\"\",\"allowEmpty\":true,\"unlockForWrite\":true}]}"),
            (d, s) => Int(s.R, "applied") == 1 && Shape(Seg(d, "6").Target) == "" && Seg(d, "6").Properties.IsLocked,
            "blanked, lock put back");
        TextCase("empty target, allowEmpty, tagged, no fp", d => Update(d, "{\"updates\":[{\"id\":\"pu-1:4\",\"target\":\"\",\"allowEmpty\":true}]}"),
            (d, s) => Int(s.R, "applied") == 1 && Shape(Seg(d, "4").Target) == "" && (Str(Item(s), "warning") ?? "").Contains("tag-id mismatch"),
            "a deliberate blank of a tagged segment: blanked, its missing tags reported");
        TextCase("tagged write, the source's markers moved, no fp", d =>
            {
                var src = Src(d, "4");   // Press <tN>Start</tN> to begin.
                var open = src.Substring(src.IndexOf('<'), src.IndexOf('>') - src.IndexOf('<') + 1);
                var close = "</" + open.Substring(1);
                return Update(d, "{\"updates\":[{\"id\":\"pu-1:4\",\"target\":" + Q(open + "Start" + close + " drukken om te beginnen.") + "}]}");
            },
            (d, s) => Int(s.R, "applied") == 1 && Str(Item(s), "warning") == null, "the same tags in another place: written, no warning");
        TextCase("locked, refused", d => Update(d, "{\"updates\":[{\"id\":\"pu-1:6\",\"target\":\"Acme BV, Leeds.\"}]}"),
            (d, s) => Int(s.R, "failed") == 1 && d.Edits == 0, "refused");
        TextCase("locked, unlockForWrite", d => Update(d, "{\"updates\":[{\"id\":\"pu-1:6\",\"target\":\"Acme BV, Leeds.\",\"status\":\"Translated\",\"unlockForWrite\":true}]}"),
            (d, s) => Int(s.R, "applied") == 1 && Seg(d, "6").Properties.IsLocked && Shape(Seg(d, "6").Target) == "\"Acme BV, Leeds.\"",
            "written, locked again");
        TextCase("status only", d => Update(d, "{\"updates\":[{\"id\":\"pu-1:1\",\"status\":\"Translated\"}]}"),
            (d, s) => Int(s.R, "applied") == 1 && d.Edits == 0 && Seg(d, "1").Properties.ConfirmationLevel == ConfirmationLevel.Translated, "status set, no edit");
        TextCase("lock only", d => Update(d, "{\"updates\":[{\"id\":\"pu-1:1\",\"locked\":true}]}"),
            (d, s) => Int(s.R, "applied") == 1 && d.Edits == 0 && Seg(d, "1").Properties.IsLocked, "locked, no edit");
        TextCase("stale fp", d => Update(d, "{\"updates\":[{\"id\":\"pu-1:1\",\"fp\":\"0000000000\",\"target\":\"X\"}]}"),
            (d, s) => Int(s.R, "failed") == 1 && d.Edits == 0, "refused");
        TextCase("nothing to do", d => Update(d, "{\"updates\":[{\"id\":\"pu-1:1\"}]}"),
            (d, s) => Int(s.R, "failed") == 1, "refused");
        TextCase("decodeEntities", d => Update(d, "{\"decodeEntities\":true,\"updates\":[{\"id\":\"pu-1:1\",\"target\":\"Bel 230&nbsp;V.\"}]}"),
            (d, s) => Int(s.R, "applied") == 1 && Shape(Seg(d, "1").Target) == "\"Bel 230\u00a0V.\"", "decoded");
        TextCase("a batch of three", d => Update(d, "{\"updates\":[{\"id\":\"pu-1:1\",\"target\":\"Een.\"},{\"id\":\"pu-1:9\",\"target\":\"X\"},{\"id\":\"pu-1:4\",\"fp\":" + Q(Fp(d, "4")) + ",\"target\":" + Q(Src(d, "4")) + "}]}"),
            (d, s) => Int(s.R, "applied") == 2 && Int(s.R, "failed") == 1, "two written, the unknown id refused");
    }

    // ── A: get_segments ───────────────────────────────────────────────
    static void CaseGetSegments()
    {
        var d = TagDoc();
        var part = Part(d);
        var s = GetSegments(d, null, part);
        Say("get_segments: " + s.Json);
        // get_active_project's count, from its own handler. Its warnings read
        // settings, so it gets fresh scratch ones (never the real file).
        var snap = Call(part, "BuildBridgeProjectSnapshot", null);
        var snapR = (Dictionary<string, object>)Js.DeserializeObject(ToJson(snap));
        Say("get_active_project: available=" + Bool(snapR, "available") + " totalSegments=" + Int(snapR, "totalSegments") + " " + Str(snapR, "note"));
        Check(Bool(snapR, "available") && Int(snapR, "totalSegments") == 5, "A get_active_project counts 5 segments");
        Check(Int(s.R, "returned") == Int(snapR, "totalSegments") && Int(s.R, "totalMatching") == 5,
            "A ACCEPTANCE: with no filters get_segments returns get_active_project's totalSegments (" + Int(s.R, "returned") + ")");
        Check(Bool(SegRec(s, "2"), "tagOnly") && Bool(SegRec(s, "3"), "tagOnly"), "A the two segments of tags only are marked tagOnly");
        Check(!SegRec(s, "1").ContainsKey("tagOnly") && !SegRec(s, "4").ContainsKey("tagOnly") && !SegRec(s, "6").ContainsKey("tagOnly"),
            "A text segments carry no tagOnly field at all");
        Check(Str(SegRec(s, "2"), "source") != null && Str(SegRec(s, "2"), "source").Contains("/>") && Str(SegRec(s, "2"), "fp") != null,
            "A a segment of tags only has its markers as source, and an fp: " + Str(SegRec(s, "2"), "source"));
        Check((Str(s.R, "note") ?? "").Contains("copySource"), "A the note says to fill them with copySource");
        Check(!s.R.ContainsKey("omitted") && !s.R.ContainsKey("omittedCount"), "A nothing omitted, so no 'omitted' field");

        s = GetSegments(d, q => { q.FromNumber = 2; q.ToNumber = 2; });
        Check(Int(s.R, "returned") == 1 && Bool(SegRec(s, "2"), "tagOnly"), "A ACCEPTANCE: fromNumber=toNumber=2 returns that segment of tags only");
        s = GetSegments(d, q => { q.Status = "Unspecified"; });
        Check(Int(s.R, "returned") == 4, "A a status filter keeps them (4 Unspecified, 2 and 3 among them)");

        d = FullDoc();
        s = GetSegments(d);
        Say("with an empty segment: " + s.Json);
        var om = List(s.R, "omitted");
        Check(Int(s.R, "returned") == 5 && Int(s.R, "omittedCount") == 1 && om.Count == 1
              && Str(om[0], "number") == "5" && Str(om[0], "reason") == "empty" && Str(om[0], "id") == "pu-1:5",
            "A an empty segment is not listed but named in 'omitted' (number 5, reason empty)");
        Check(Int(s.R, "totalMatching") + Int(s.R, "omittedCount") == d.Pairs.Count,
            "A totalMatching + omittedCount = every segment in the document (" + d.Pairs.Count + ")");
        Check((Str(s.R, "note") ?? "").Contains("empty"), "A and the note says why");
        s = GetSegments(d, q => { q.FromNumber = 5; q.ToNumber = 5; });
        Check(Int(s.R, "returned") == 0 && Int(s.R, "omittedCount") == 1, "A asked for by number, the empty one is reported in 'omitted', not silently missing");
        s = GetSegments(d, q => { q.Contains = "Acme"; });
        Check(Int(s.R, "returned") == 2 && !s.R.ContainsKey("omitted"), "A a 'contains' filter does not report empties (they cannot contain anything)");
        s = GetSegments(d, q => { q.Limit = 2; });
        Check(Int(s.R, "returned") == 2 && Int(s.R, "totalMatching") == 5 && Int(s.R, "omittedCount") == 1 && Bool(s.R, "truncated"),
            "A paging: omitted counted over the whole document, not the page");

        d = TagDoc();
        d.Pairs.Insert(2, (ISegmentPair)new ThrowingProxy(typeof(ISegmentPair)).GetTransparentProxy());
        s = GetSegments(d);
        om = List(s.R, "omitted");
        Check(Int(s.R, "returned") == 5 && Int(s.R, "omittedCount") == 1 && om.Count == 1 && Str(om[0], "reason") == "unreadable"
              && (Str(s.R, "note") ?? "").Contains("could not be read"),
            "A a segment that cannot be read is reported in 'omitted' (reason unreadable) with a warning, not silently skipped");

        // 100x the fixture: 600 segments, 100 of them empty - 'omitted' stays bounded.
        var many = new List<Spec>();
        for (int i = 1; i <= 600; i++)
        {
            var id = i.ToString();
            if (i % 6 == 0) many.Add(new Spec { Id = id });
            else if (i % 6 == 1) many.Add(new Spec { Id = id, Source = () => new IAbstractMarkupData[] { X("7" + id) } });
            else many.Add(new Spec { Id = id, Source = () => new IAbstractMarkupData[] { T("Acme line " + id + ".") } });
        }
        var big = Doc(many.ToArray());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        s = GetSegments(big, q => { q.Limit = 2000; });
        sw.Stop();
        Check(Int(s.R, "returned") == 500 && Int(s.R, "omittedCount") == 100 && List(s.R, "omitted").Count == 50
              && (Str(s.R, "note") ?? "").Contains("first 50 of 100"),
            "A 600 segments: 500 listed (100 tagOnly), 100 empties counted, 50 named and the note says so - in " + sw.ElapsedMilliseconds + " ms");

        // And 10x that: 6,000 segments, the default page of 200.
        many.Clear();
        for (int i = 1; i <= 6000; i++)
        {
            var id = i.ToString();
            if (i % 6 == 0) many.Add(new Spec { Id = id });
            else if (i % 6 == 1) many.Add(new Spec { Id = id, Source = () => new IAbstractMarkupData[] { X("7" + id), G("8" + id, X("9" + id)) } });
            else many.Add(new Spec { Id = id, Source = () => new IAbstractMarkupData[] { T("Acme line " + id + " with "), G("5" + id, T("bold")), T(" text.") } });
        }
        big = Doc(many.ToArray());
        sw.Restart();
        s = GetSegments(big);
        sw.Stop();
        Check(Int(s.R, "returned") == 200 && Int(s.R, "totalMatching") == 5000 && Int(s.R, "omittedCount") == 1000 && List(s.R, "omitted").Count == 50,
            "A 6,000 segments: a page of 200, 5,000 matching, 1,000 empties counted, 50 named - in " + sw.ElapsedMilliseconds + " ms");

        // update_segments at its limit: 40 copies in one call.
        var forty = new StringBuilder("{\"updates\":[");
        for (int i = 0; i < 40; i++) forty.Append(i == 0 ? "" : ",").Append("{\"id\":\"pu-1:" + (i * 6 + 1) + "\",\"copySource\":true,\"status\":\"Translated\"}");
        forty.Append("]}");
        sw.Restart();
        s = Update(big, forty.ToString());
        sw.Stop();
        Check(Int(s.R, "applied") == 40 && Shape(Seg(big, "235").Target) == Shape(Seg(big, "235").Source),
            "B 40 copySource items in one call (the maximum), in a 6,000-segment document: all copied - in " + sw.ElapsedMilliseconds + " ms");
    }

    // ── B: copySource ─────────────────────────────────────────────────
    static void CaseCopySource()
    {
        var d = TagDoc();
        var part = Part(d);
        var fp2 = Str(SegRec(GetSegments(d, null, part), "2"), "fp");
        var s = Update(d, "{\"updates\":[{\"id\":\"pu-1:2\",\"fp\":" + Q(fp2) + ",\"copySource\":true,\"status\":\"Translated\"}]}", part);
        Say("copySource: " + s.Json);
        var seg2 = Seg(d, "2");
        Check(Int(s.R, "applied") == 1 && Bool(Item(s), "ok") && Str(Item(s), "warning") == null, "B copySource with fp and status: ok, no warning");
        Check(Shape(seg2.Target) == Shape(seg2.Source),
            "B ACCEPTANCE: the target is the source's markup item for item, each tag with the source's id (Ctrl+Insert): " + Shape(seg2.Target));
        Check(seg2.Properties.ConfirmationLevel == ConfirmationLevel.Translated, "B status Translated, as asked");
        var qa = QaTags(part);
        Say("check_tags: " + qa.Json);
        Check(!List(qa.R, "issues").Any(i => Str(i, "id") == "pu-1:2") && Int(qa.R, "segmentsChecked") >= 1,
            "B ACCEPTANCE: check_tags reports 0 issues for it, and did check it (segmentsChecked " + Int(qa.R, "segmentsChecked") + ")");

        d = TagDoc();
        s = Update(d, "{\"updates\":[{\"id\":\"pu-1:3\",\"copySource\":true}]}");
        Check(Int(s.R, "applied") == 1 && Seg(d, "3").Properties.ConfirmationLevel == ConfirmationLevel.Draft
              && Shape(Seg(d, "3").Target) == Shape(Seg(d, "3").Source), "B copySource without status: Draft, like any write");
        var editsBefore = d.Edits;
        s = Update(d, "{\"updates\":[{\"id\":\"pu-1:3\",\"copySource\":true}]}");
        Check(Int(s.R, "applied") == 1 && Seg(d, "3").Properties.ConfirmationLevel == ConfirmationLevel.Draft,
            "B copying again changes nothing, and the status stays as it was");

        d = TagDoc();
        s = Update(d, "{\"updates\":[{\"id\":\"pu-1:2\",\"copySource\":true,\"target\":\"<t1/>\"}]}");
        Check(Int(s.R, "failed") == 1 && (Str(Item(s), "error") ?? "").Contains("not both") && d.Edits == 0 && Shape(Seg(d, "2").Target) == "",
            "B copySource with a target: refused, nothing written");

        d = FullDoc();
        s = Update(d, "{\"updates\":[{\"id\":\"pu-1:5\",\"copySource\":true}]}");
        Check(Int(s.R, "failed") == 1 && (Str(Item(s), "error") ?? "").Contains("nothing to copy") && d.Edits == 0
              && Seg(d, "5").Properties.ConfirmationLevel == ConfirmationLevel.Unspecified,
            "B copySource on an empty source: refused, nothing written");

        d = TagDoc();
        s = Update(d, "{\"updates\":[{\"id\":\"pu-1:2\",\"fp\":\"0000000000\",\"copySource\":true}]}");
        Check(Int(s.R, "failed") == 1 && (Str(Item(s), "error") ?? "").Contains("stale fp") && d.Edits == 0, "B copySource with a stale fp: refused");

        d = TagDoc();
        s = Update(d, "{\"updates\":[{\"id\":\"pu-1:6\",\"copySource\":true}]}");
        Check(Int(s.R, "failed") == 1 && (Str(Item(s), "error") ?? "").Contains("locked") && d.Edits == 0, "B copySource on a locked segment: refused");
        s = Update(d, "{\"updates\":[{\"id\":\"pu-1:6\",\"copySource\":true,\"unlockForWrite\":true}]}");
        Check(Int(s.R, "applied") == 1 && Seg(d, "6").Properties.IsLocked && Shape(Seg(d, "6").Target) == Shape(Seg(d, "6").Source),
            "B with unlockForWrite: copied, locked again");

        // The target's comment is kept; the source's comment and its tracked
        // deletion are not copied.
        d = Doc(new Spec { Id = "1", Source = () => new IAbstractMarkupData[] { T("Visit "), Comment("Source note.", T("our site")), Deleted(T(" now")), T(".") },
                           Target = () => new IAbstractMarkupData[] { Comment("Check this.", T("Oude tekst.")) } });
        s = Update(d, "{\"updates\":[{\"id\":\"pu-1:1\",\"copySource\":true}]}");
        var t1 = Seg(d, "1").Target;
        Say("source " + Shape(Seg(d, "1").Source) + " -> target " + Shape(t1));
        Check(Int(s.R, "applied") == 1 && Comments(t1) == 1 && ShapeNoComments(t1) == "\"Visit our site.\"",
            "B copySource keeps the target's comment, and copies the source as it reads (no source comment, no deleted text)");
        Check(Revisions(t1) == 0, "B no revision marker copied from the source");

        // Track Changes on: the copy goes in as a tracked change, and tracking is on afterwards.
        d = Doc(S1("Oude tekst."), S2());
        d.Editor.TrackChanges = true;
        s = Update(d, "{\"updates\":[{\"id\":\"pu-1:1\",\"copySource\":true},{\"id\":\"pu-1:2\",\"copySource\":true}]}");
        Say("tracked: " + s.Json);
        Say("seg 1 target " + Shape(Seg(d, "1").Target) + "  revisions " + Revisions(Seg(d, "1").Target));
        Check(Int(s.R, "applied") == 2 && Str(Item(s, 0), "revisionMode") != null && Str(Item(s, 1), "revisionMode") != null,
            "B Track Changes on: both copied, each reports how it was recorded (" + Str(Item(s, 0), "revisionMode") + ", " + Str(Item(s, 1), "revisionMode") + ")");
        Check(Revisions(Seg(d, "1").Target) > 0 && Shape(Seg(d, "1").Target) == Shape(Seg(d, "1").Source),
            "B the text segment's copy is recorded as revisions, and reads as the source");
        Check(d.Editor.TrackChanges && d.Editor.TurnOffs == d.Editor.Reverts, "B Track Changes is on again afterwards");
    }

    // ── C: a target with wrong tags and no fp ─────────────────────────
    static void CaseRefusals()
    {
        // The field report: a segment of tags only, its markers guessed.
        var d = TagDoc();
        var before = Shape(Seg(d, "2").Target);
        var s = Update(d, "{\"updates\":[{\"id\":\"pu-1:2\",\"target\":\"<t1/><t2/>\",\"status\":\"Translated\"}]}");
        Say("wrong guess, no fp: " + s.Json);
        Check(Int(s.R, "failed") == 1 && Int(s.R, "applied") == 0 && (Str(Item(s), "error") ?? "").Contains("refused, nothing written")
              && (Str(Item(s), "error") ?? "").Contains("copySource"),
            "C ACCEPTANCE: a wrong <tN> guess with no fp is refused, the error says why and points to copySource");
        Check(d.Edits == 0 && d.StatusWrites == 0 && Shape(Seg(d, "2").Target) == before
              && Seg(d, "2").Properties.ConfirmationLevel == ConfirmationLevel.Unspecified,
            "C ACCEPTANCE: target unchanged - no edit made, no status written");
        Check((Str(s.R, "note") ?? "").Contains("Do not guess tag markers"), "C the response note says so too");

        d = TagDoc();
        var fp = Str(SegRec(GetSegments(d), "2"), "fp");
        s = Update(d, "{\"updates\":[{\"id\":\"pu-1:2\",\"fp\":" + Q(fp) + ",\"target\":\"<t1/><t2/>\"}]}");
        Check(Int(s.R, "applied") == 1 && (Str(Item(s), "warning") ?? "").Contains("tag-id mismatch") && Shape(Seg(d, "2").Target).Contains("[x795"),
            "C the same with its fp: written, with the mismatch warning, as before");

        d = TagDoc();
        s = Update(d, "{\"updates\":[{\"id\":\"pu-1:2\",\"target\":\"<t1/><t2/>\",\"force\":true}]}");
        Check(Int(s.R, "applied") == 1 && (Str(Item(s), "warning") ?? "").Contains("tag-id mismatch"),
            "C the same with force: true and no fp: written, with the warning");
        var qa = QaTags(Part(d));
        Check(List(qa.R, "issues").Any(i => Str(i, "id") == "pu-1:2" && (Str(i, "detail") ?? "").Contains("copySource")),
            "K check_tags now finds the broken segment of tags only, and says to repair it with copySource");

        d = TagDoc();
        var src = Str(SegRec(GetSegments(d), "2"), "source");
        s = Update(d, "{\"updates\":[{\"id\":\"pu-1:2\",\"target\":" + Q(src) + "}]}");
        Check(Int(s.R, "applied") == 1 && Str(Item(s), "warning") == null && Shape(Seg(d, "2").Target) == Shape(Seg(d, "2").Source),
            "C the source's exact markers now build on a segment of tags only (" + src + "): no warning, same tags as the source");

        d = TagDoc();
        s = Update(d, "{\"updates\":[{\"id\":\"pu-1:2\",\"target\":\"<t1/>Mail us<t2/>\",\"force\":true}]}");
        Check(Int(s.R, "failed") == 1 && (Str(Item(s), "error") ?? "").Contains("only tags") && d.Edits == 0,
            "C text in a segment of tags only cannot be built: refused (even with force), nothing written - it used to be 'written' as nothing");

        d = TagDoc();
        s = Update(d, "{\"updates\":[{\"id\":\"pu-1:4\",\"target\":\"Druk op Start om te beginnen.\"}]}");
        Check(Int(s.R, "failed") == 1 && (Str(Item(s), "error") ?? "").Contains("tag-id mismatch") && d.Edits == 0 && Shape(Seg(d, "4").Target) == "",
            "C a text segment's tags dropped with no fp: refused too, nothing written");

        d = Doc(new Spec { Id = "7", Source = () => new IAbstractMarkupData[] { T("Acme "), G("9", T("Ltd")) },
                           Target = () => new IAbstractMarkupData[] { T("Acme "), G("9", T("Ltd")) }, Locked = true, Level = ConfirmationLevel.Translated });
        s = Update(d, "{\"updates\":[{\"id\":\"pu-1:7\",\"target\":\"Acme BV\",\"unlockForWrite\":true}]}");
        Check(Int(s.R, "failed") == 1 && (Str(Item(s), "error") ?? "").Contains("tag-id mismatch") && Seg(d, "7").Properties.IsLocked
              && d.Edits == 0 && d.StatusWrites == 0 && Shape(Seg(d, "7").Target) == "\"Acme \"[g9 <g id=\"9\">:\"Ltd\"]",
            "C refused before the lock is touched: still locked, target and status as they were");

        d = TagDoc();
        s = Update(d, "{\"updates\":[{\"id\":\"pu-1:2\",\"target\":\"<t2/><t1/>\"},{\"id\":\"pu-1:1\",\"target\":\"Neem contact op met Acme.\"},{\"id\":\"pu-1:3\",\"copySource\":true}]}");
        Check(Int(s.R, "applied") == 2 && Int(s.R, "failed") == 1 && !Bool(Item(s, 0), "ok") && Bool(Item(s, 1), "ok") && Bool(Item(s, 2), "ok"),
            "C in a batch, only the refused item is left out; the rest are written");

        d = Doc(new Spec { Id = "1", Source = () => new IAbstractMarkupData[] { T("A"), X("1"), T("B") }, Target = () => new IAbstractMarkupData[] { T("Oud") } });
        s = Update(d, "{\"updates\":[{\"id\":\"pu-1:1\",\"target\":\"\",\"allowEmpty\":true}]}");
        Check(Int(s.R, "applied") == 1 && Shape(Seg(d, "1").Target) == "", "C allowEmpty on a tagged segment, no fp: still blanks it (a deliberate blank is not a tag guess)");
        d = TagDoc();
        s = Update(d, "{\"updates\":[{\"id\":\"pu-1:3\",\"copySource\":true},{\"id\":\"pu-1:3\",\"target\":\"\",\"allowEmpty\":true}]}");
        Check(Int(s.R, "applied") == 2 && Shape(Seg(d, "3").Target) == "",
            "C allowEmpty on a filled segment of tags only, no fp: blanked (it used to be left as it was, and reported written)");
    }

    // ── E: <tN/> written where the source has the tag PAIR <tN>…</tN> ──
    // Until 206 the pair was cloned whole, its SOURCE text with it: the
    // target got untranslated source text, its tags matched, and nothing
    // warned or refused. Now the pair is left out, so it is a tag mismatch
    // like any other.
    static void CaseSelfClosingPair()
    {
        // update_segments, no fp: refused, nothing written.
        var d = TextDoc();
        var src = Src(d, "4");                              // Press <tN>Start</tN> to begin.
        var n = src.Substring(src.IndexOf("<t") + 2, src.IndexOf('>') - src.IndexOf("<t") - 2);
        var reply = "Druk op <t" + n + "/> om te beginnen.";
        var s = Update(d, "{\"updates\":[{\"id\":\"pu-1:4\",\"target\":" + Q(reply) + "}]}");
        Say("<t" + n + "/> for a pair, no fp: " + s.Json + "  target " + Shape(Seg(d, "4").Target));
        Check(Int(s.R, "failed") == 1 && (Str(Item(s), "error") ?? "").Contains("tag-id mismatch") && d.Edits == 0
              && Shape(Seg(d, "4").Target) == "",
            "E update_segments, no fp: <tN/> for a source tag pair is refused as a tag mismatch, nothing written");

        // With an fp: written with the mismatch warning, and without the source's text.
        d = TextDoc();
        s = Update(d, "{\"updates\":[{\"id\":\"pu-1:4\",\"fp\":" + Q(Fp(d, "4")) + ",\"target\":" + Q(reply) + "}]}");
        var t4 = Shape(Seg(d, "4").Target);
        Say("with fp: " + s.Json + "  target " + t4);
        Check(Int(s.R, "applied") == 1 && (Str(Item(s), "warning") ?? "").Contains("tag-id mismatch"),
            "E update_segments with an fp: written, with the tag-mismatch warning");
        Check(!t4.Contains("Start") && !t4.Contains("[g5"),
            "E ... and the source's text inside the pair is NOT copied into the target: " + t4);

        // force, no fp: the same as with an fp.
        d = TextDoc();
        s = Update(d, "{\"updates\":[{\"id\":\"pu-1:4\",\"target\":" + Q(reply) + ",\"force\":true}]}");
        Check(Int(s.R, "applied") == 1 && (Str(Item(s), "warning") ?? "").Contains("tag-id mismatch") && !Shape(Seg(d, "4").Target).Contains("Start"),
            "E with force: written with the warning, no source text");

        // Batch Translate, Alt+T and the proofreader import write through
        // ReconstructTarget directly: no source text either, and the tag is
        // missing, which Batch Translate's reply check reports in its log.
        d = TextDoc();
        var seg = Seg(d, "4");
        var ser = SegmentTagHandler.Serialize(seg.Source);
        bool built = SegmentTagHandler.ReconstructTarget(seg.Target, seg.Source, reply, ser.TagMap);
        var shape = Shape(seg.Target);
        Check(built && shape == "\"Druk op  om te beginnen.\"",
            "E ReconstructTarget (Batch Translate, Alt+T): the pair is left out, no source text: " + shape);
        Check(Supervertaler.Core.ReplyCheck.TagDifference(ser.SerializedText, reply) != null,
            "E Batch Translate's reply check reports the tag difference (logged, as for any tag mismatch)");

        // A proper pair later in the same reply still gets the tag.
        d = TextDoc();
        seg = Seg(d, "4");
        built = SegmentTagHandler.ReconstructTarget(seg.Target, seg.Source, "<t" + n + "/>Druk op <t" + n + ">Start</t" + n + ">.", ser.TagMap);
        shape = Shape(seg.Target);
        Check(built && shape == "\"Druk op \"[g5 <g id=\"5\">:\"Start\"]\".\"",
            "E a <tN/> for the pair does not use up the tag: a later <tN>…</tN> still gets it: " + shape);

        // The field report's segment of tags only: <t3/> where t3 is a pair
        // holding <t4/>. It used to clone the pair with its tag inside and match.
        d = TagDoc();
        s = Update(d, "{\"updates\":[{\"id\":\"pu-1:2\",\"target\":\"<t1/><t2/><t3/>\"}]}");
        Check(Int(s.R, "failed") == 1 && (Str(Item(s), "error") ?? "").Contains("copySource") && d.Edits == 0,
            "E a segment of tags only, its pair written as <t3/>, no fp: refused, pointing to copySource");

        // A placeholder written as <tN/> is unchanged: no mismatch.
        d = Doc(new Spec { Id = "1", Source = () => new IAbstractMarkupData[] { T("A"), X("1"), T("B") } });
        s = Update(d, "{\"updates\":[{\"id\":\"pu-1:1\",\"target\":\"C<t1/>D\"}]}");
        Check(Int(s.R, "applied") == 1 && Str(Item(s), "warning") == null && Shape(Seg(d, "1").Target) == "\"C\"[x1 <x id=\"1\"/>]\"D\"",
            "E a placeholder's <tN/> is written as before, no warning");
    }

    // ── F: <tN>text</tN> written where the source's tN is a PLACEHOLDER ──
    // The reverse of E. Until 206 the placeholder was cloned and the text
    // meant to go inside it was dropped; the tag ids matched, so nothing
    // warned: translated text silently lost. Now the text is kept and the
    // tag left out - a tag mismatch like any other.
    static Spec PhSpec() { return new Spec { Id = "1", Source = () => new IAbstractMarkupData[] { T("Press "), X("7"), T(" to begin.") } }; }
    static void CasePairForPlaceholder()
    {
        const string reply = "Druk op <t1>Enter</t1> om te beginnen.";

        var d = Doc(PhSpec());
        var s = Update(d, "{\"updates\":[{\"id\":\"pu-1:1\",\"target\":" + Q(reply) + "}]}");
        Say("<t1>text</t1> for a placeholder, no fp: " + s.Json + "  target " + Shape(Seg(d, "1").Target));
        Check(Int(s.R, "failed") == 1 && (Str(Item(s), "error") ?? "").Contains("tag-id mismatch") && d.Edits == 0
              && Shape(Seg(d, "1").Target) == "",
            "F update_segments, no fp: <tN>text</tN> for a source placeholder is refused as a tag mismatch, nothing written");

        d = Doc(PhSpec());
        s = Update(d, "{\"updates\":[{\"id\":\"pu-1:1\",\"fp\":" + Q(Fp(d, "1")) + ",\"target\":" + Q(reply) + "}]}");
        var t = Shape(Seg(d, "1").Target);
        Say("with fp: " + s.Json + "  target " + t);
        Check(Int(s.R, "applied") == 1 && (Str(Item(s), "warning") ?? "").Contains("tag-id mismatch"),
            "F update_segments with an fp: written, with the tag-mismatch warning");
        Check(t == "\"Druk op Enter om te beginnen.\"",
            "F ... and the translated text inside the markers is kept, not dropped: " + t);

        d = Doc(PhSpec());
        s = Update(d, "{\"updates\":[{\"id\":\"pu-1:1\",\"target\":" + Q(reply) + ",\"force\":true}]}");
        Check(Int(s.R, "applied") == 1 && (Str(Item(s), "warning") ?? "").Contains("tag-id mismatch")
              && Shape(Seg(d, "1").Target).Contains("Enter"),
            "F with force: written with the warning, the text kept");

        // Batch Translate, Alt+T, the proofreader import: ReconstructTarget direct.
        d = Doc(PhSpec());
        var seg = Seg(d, "1");
        var ser = SegmentTagHandler.Serialize(seg.Source);
        bool built = SegmentTagHandler.ReconstructTarget(seg.Target, seg.Source, reply, ser.TagMap);
        var shape = Shape(seg.Target);
        Check(built && shape == "\"Druk op Enter om te beginnen.\"",
            "F ReconstructTarget (Batch Translate, Alt+T, proofreader import): the text is kept, the tag left out: " + shape);
        Check(Supervertaler.Core.ReplyCheck.TagDifference(ser.SerializedText, reply) != null
              && !SegmentTagHandler.ValidateTagsPresent(reply, ser.TagMap),
            "F Batch Translate's reply check and the AI-reply import's tag check both report it");

        // An empty <t1></t1> loses nothing: the placeholder is written, as before.
        d = Doc(PhSpec());
        s = Update(d, "{\"updates\":[{\"id\":\"pu-1:1\",\"target\":\"Druk op <t1></t1> om te beginnen.\"}]}");
        Check(Int(s.R, "applied") == 1 && Str(Item(s), "warning") == null
              && Shape(Seg(d, "1").Target) == "\"Druk op \"[x7 <x id=\"7\"/>]\" om te beginnen.\"",
            "F an empty <tN></tN> for a placeholder: the placeholder is written as before, no warning");

        // The placeholder's own <t1/> is unaffected.
        d = Doc(PhSpec());
        s = Update(d, "{\"updates\":[{\"id\":\"pu-1:1\",\"target\":\"Druk op <t1/> om te beginnen.\"}]}");
        Check(Int(s.R, "applied") == 1 && Str(Item(s), "warning") == null && Shape(Seg(d, "1").Target).Contains("[x7 "),
            "F the placeholder written as <tN/>: as before, no warning");

        // A real pair is unaffected: <tN>text</tN> on a source pair still wraps the text.
        d = TextDoc();
        var src = Src(d, "4");
        s = Update(d, "{\"updates\":[{\"id\":\"pu-1:4\",\"target\":" + Q(src.Replace("Press ", "Druk op ").Replace(" to begin.", " om te beginnen.")) + "}]}");
        Check(Int(s.R, "applied") == 1 && Shape(Seg(d, "4").Target) == "\"Druk op \"[g5 <g id=\"5\">:\"Start\"]\" om te beginnen.\"",
            "F a source pair written as a pair: unchanged");
    }

    // ── K: coverage counts segments of tags only ──────────────────────
    static void CaseCoverage()
    {
        var d = FullDoc();
        var part = Part(d);
        var cov = (Dictionary<string, object>)Js.DeserializeObject(ToJson(Call(part, "BridgeGetCoverage", null)));
        Check(Int(cov, "totalSegments") == 5, "K get_coverage counts the segments of tags only (5: all but the empty one), got " + Int(cov, "totalSegments"));
    }

    public static int Run(string pluginPath, bool textOnly, string textOut)
    {
        P = Assembly.LoadFrom(pluginPath);
        VP = T_("AiAssistantViewPart");
        Factory();

        // The AI Assistant panel as a bare UserControl: no handle, no marshalling.
        var panelType = T_("Controls.AiAssistantControl");
        var panel = FormatterServices.GetUninitializedObject(panelType);
        typeof(System.Windows.Forms.UserControl).GetConstructor(Type.EmptyTypes).Invoke(panel, null);
        var lazyFactory = Expression.Lambda(typeof(Func<>).MakeGenericType(panelType), Expression.Constant(panel, panelType)).Compile();
        VP.GetField("_control", Any).SetValue(null, Activator.CreateInstance(typeof(Lazy<>).MakeGenericType(panelType), lazyFactory));
        // Settings pre-seeded: Current would otherwise load the real ones.
        var svc = T_("Settings.SettingsService");
        svc.GetField("_current", Any).SetValue(null, Activator.CreateInstance(T_("Settings.TermLensSettings")));

        TextCases();
        if (textOut != null) File.WriteAllText(textOut, Js.Serialize(TextOutcomes), new UTF8Encoding(false));
        if (!textOnly)
        {
            CaseGetSegments();
            CaseCopySource();
            CaseRefusals();
            CaseSelfClosingPair();
            CasePairForPlaceholder();
            CaseCoverage();
        }
        Console.WriteLine(Pass + " passed, " + Fail + " failed");
        return Fail == 0 ? 0 : 1;
    }
}
'@

foreach ($n in @('Sdl.FileTypeSupport.Framework.Core.dll', 'Sdl.Core.Globalization.dll')) { [void]$refs.Add((Join-Path $studio $n)) }
[void]$refs.Add($iface.Assembly.Location)
[void]$refs.Add($Dll)
$all = @($refs) + @('System.dll', 'System.Core.dll', 'System.Runtime.Serialization.dll', 'System.Xml.dll', 'System.Windows.Forms.dll',
                    'System.Drawing.dll', 'System.Web.Extensions.dll')
Add-Type -TypeDefinition ($src + "`n" + $stub) -ReferencedAssemblies $all
try { $code = [TagOnlyTest]::Run($Dll, [bool]$TextCasesOnly, $(if ($TextCasesOut) { $TextCasesOut } else { $null })) }
catch { $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }; Write-Host $e.ToString(); exit 2 }
exit $code
