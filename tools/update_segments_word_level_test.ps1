# update_segments: a second or later write to a segment is recorded word by
# word (18.20.208 / 19.20.208). Until 208 a write was recorded word by word only
# when the target held no tracked change yet; any earlier revision meant one
# whole-segment change (the whole old target struck through, the whole new one
# inserted), even for one changed word.
#
# Runs the BUILT plugin's own update_segments handler (BridgeUpdateSegments),
# get_tracked_changes and add_comment's range anchoring on REAL segments from
# Studio's own factory, through a stand-in for Studio's document (IStudioDocument,
# generated from Studio's interface) that behaves as the editor does where it
# matters:
#   - ProcessSegmentPair hands the edit a copy of the pair and then applies it.
#     With Track Changes on it records ANY edit as the whole segment replaced;
#     with it off it protects a segment's unreviewed tracked changes and quietly
#     declines the edit.
#   - Studio's internal TrackChangesState, which TrackingSuspension reaches by
#     name: reading the setting, switching it off, restoring it.
#   - the editing user's name, which goes on every revision.
# Requests go in as the JSON an MCP client sends. Invented Acme text only.
#
#   A  the acceptance tests: a clean segment (1); a second write that changes
#      another word (2), a word inside an earlier insertion (3), nothing (4),
#      or back to the original wording (5); inline tags (6); a line break (7);
#      Reject All gives the original and Accept All the last text sent (8);
#      add_comment's 'on' still finds its words, and a write after the comment
#      is still word-level (9); Track Changes off + unreviewed revisions is
#      still refused and nothing is written (10).
#   B  another author's tracked changes: their deletions and insertions are
#      never changed; text added at the edge of their insertion goes beside it;
#      an edit that reaches INTO their insertion falls back, with the reason.
#   C  every fallback and failure path: one revision inside another, an
#      insertion covering a tag, a revision on a tag itself, tags added or
#      removed in a segment with revisions, commented text removed from an
#      earlier insertion, an unknown kind of revision, Track Changes that cannot
#      be switched, Studio throwing, Studio declining. Each is still written
#      (or reported as failed), the warning names the actual reason, and Track
#      Changes is on again afterwards.
#   D  scale: 2,000 segments written three times each, and one long segment
#      with hundreds of earlier revisions.
#   E  the other doors to the same code: find_and_replace and SuperSearch's
#      Replace (SegmentWriteCheck.ProcessChecked with TargetTextReplace.Apply)
#      on a segment that already carries tracked changes.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\update_segments_word_level_test.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\update_segments_word_level_test.ps1 -Dll <Supervertaler.Trados.dll>
#
# Default -Dll is the Studio 19 Release build in this checkout. What it cannot
# reach, and the live test in Studio covers: Studio's real editor applying a
# write over a segment that holds revisions while tracking is suspended, how
# Studio draws, saves, accepts and rejects the result, and the MCP server exe
# (which forwards update_segments' body verbatim).
param(
    [string]$Dll = (Join-Path (Split-Path -Parent $PSScriptRoot) "src\Supervertaler.Trados\bin\Studio19\Release\Supervertaler.Trados.dll"),
    [string]$Root = (Join-Path $env:TEMP "sv-update-segments-word-level-test"),
    # Internal: set on the child run.
    [switch]$Child
)
$ErrorActionPreference = 'Stop'

$Root = [IO.Path]::GetFullPath($Root)
if ($Root -like 'D:\Supervertaler*' -or $Root.Length -lt 10) { throw "Refusing to use $Root as the test root." }
$Dll = [IO.Path]::GetFullPath($Dll)
if (-not (Test-Path -LiteralPath $Dll)) { throw "No plugin build at $Dll" }

if (-not $Child) {
    # ── Parent: run the cases in a child process of the right bitness, and
    #    check that no real Supervertaler file changed meanwhile. ──
    if (Test-Path -LiteralPath $Root) { Remove-Item -LiteralPath $Root -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $Root | Out-Null

    $protected = @(
        'D:\Supervertaler\settings\api-keys.json',
        'D:\Supervertaler\trados\logs\diagnostic.log',
        (Join-Path $env:APPDATA 'Supervertaler\api-keys.locked.json'),
        (Join-Path $env:APPDATA 'Supervertaler\config.json'),
        'D:\Supervertaler\resources\supervertaler.db'
    )
    $lad = Join-Path $env:LOCALAPPDATA 'Supervertaler.Trados'
    if (Test-Path -LiteralPath $lad) { $protected += @(Get-ChildItem -LiteralPath $lad -File | ForEach-Object { $_.FullName }) }
    function Snapshot { $protected | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { $i = Get-Item -LiteralPath $_; "$($i.FullName)|$($i.Length)|$($i.LastWriteTimeUtc.Ticks)" } }
    $before = @(Snapshot)

    $ps = if ($Dll -like '*Studio19*') { Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe' }
          else { Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe' }
    $psi = New-Object System.Diagnostics.ProcessStartInfo $ps
    $psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Child -Dll `"$Dll`" -Root `"$(Join-Path $Root 'run')`""
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    Write-Host "Plugin: $Dll"
    $p = [Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEndAsync()
    $err = $p.StandardError.ReadToEndAsync()
    $p.WaitForExit()
    $text = $out.Result + $err.Result
    foreach ($line in ($text -split "`r?`n")) { if ($line.Trim()) { Write-Host "  $line" } }
    $failed = $p.ExitCode -ne 0
    # Windows Defender can end a process like this one before it runs: no
    # output and no error. That must not read as "nothing failed".
    if ($text -notmatch '\d+ passed, \d+ failed') { Write-Host "FAIL  the run produced no result line (exit $($p.ExitCode)); if there is no output at all, look at Windows Security > Protection history"; $failed = $true }

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
public static class WordLevelWriteResolver
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
[WordLevelWriteResolver]::Register(@((Split-Path $Dll), $studio))
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Web.Extensions
$plugin = [Reflection.Assembly]::LoadFrom($Dll)
# Its own data folder, before anything else: no test may write the real one.
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
using System.Diagnostics;
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

// What TrackingSuspension reaches through the document's InternalDocument: the
// editor's own Track Changes setting.
public sealed class FakeEditor
{
    public bool TrackChanges;
    public bool Protect = true;      // revision protection, on whenever tracking (and TQA) is off
    public bool FailTurnOff;
    public int TurnOffs, Reverts;
}

// What TrackingSuspension.EditingUser reaches through the document's _sideBySideView.
public sealed class FakeView
{
    public string DocumentEditingUser { get; set; }
}

namespace Sdl.TranslationStudioAutomation.IntegrationApi.Internal
{
    // Studio's internal class of this name, as TrackingSuspension uses it: the
    // constructor records the settings, TurnOffTracking switches tracking and
    // revision protection off, and the revert puts them back.
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
        internal void TurnOffTracking()
        {
            if (_editor.FailTurnOff) throw new InvalidOperationException("fake: tracking could not be switched");
            _editor.TrackChanges = false;
            _editor.Protect = false;
            _editor.TurnOffs++;
        }
        internal void RevertTrackingToOriginalState()
        {
            _editor.TrackChanges = _trackChanges;
            _editor.Protect = _protect;
            _editor.Reverts++;
        }
    }
}

// One paragraph of segments, behaving as Studio's editor does around a write.
public partial class FakeDocument
{
    public const string Me = "Acme Translator";
    public readonly FakeEditor Editor = new FakeEditor();
    // Both reached by name, as on Studio's own document.
    internal object InternalDocument { get { return Editor; } }
    private readonly FakeView _sideBySideView = new FakeView { DocumentEditingUser = Me };
    public string User { get { return _sideBySideView.DocumentEditingUser; } set { _sideBySideView.DocumentEditingUser = value; } }

    public IDocumentItemFactory F;
    public IPropertiesFactory PF;
    public List<ISegmentPair> Pairs = new List<ISegmentPair>();
    public IParagraphUnit Pu;
    public bool ThrowOnApply, DeclineAll;
    public int Edits, Applied, Cancelled, Declined, StatusWrites, WholeSegment;
    public bool? TrackingAtApply;    // the Track Changes setting when Studio applied the last edit

    ISegmentPair LiveOf(ISegmentPair pair) { return Pairs.First(p => p.Properties.Id.Id == pair.Properties.Id.Id); }
    void FakeProcessSegmentPair(ISegmentPair pair, string undoName, Action<ISegmentPair, CancelEventArgs> edit)
    {
        TrackingAtApply = null;
        var live = LiveOf(pair);
        var copy = F.CreateSegmentPair((ISegment)live.Source.Clone(), (ISegment)live.Target.Clone());
        var cancel = new CancelEventArgs();
        Edits++;
        edit(copy, cancel);
        if (cancel.Cancel) { Cancelled++; return; }
        TrackingAtApply = Editor.TrackChanges;
        if (ThrowOnApply) throw new InvalidOperationException("fake: Studio could not apply the edit");
        if (DeclineAll) { Declined++; return; }
        // Protected: declined without a word.
        if (!Editor.TrackChanges && Editor.Protect && HasRevisions(live.Target)) { Declined++; return; }
        Applied++;
        if (Editor.TrackChanges)
        {
            // Any edit made here with tracking on: the whole segment replaced.
            WholeSegment++;
            var dp = PF.CreateRevisionProperties(RevisionType.Delete); dp.Author = User; dp.Date = DateTime.Now;
            var ip = PF.CreateRevisionProperties(RevisionType.Insert); ip.Author = User; ip.Date = DateTime.Now;
            var del = F.CreateRevision(dp);
            live.Target.MoveAllItemsTo(del);
            var ins = F.CreateRevision(ip);
            copy.Target.MoveAllItemsTo(ins);
            live.Target.Add(del);
            live.Target.Add(ins);
        }
        else
        {
            live.Target.Clear();
            copy.Target.MoveAllItemsTo(live.Target);
        }
    }
    IParagraphUnit FakeGetParentParagraphUnit(ISegmentPair pair) { return Pu; }
    IEnumerable<ISegmentPair> FakeGetSegmentPairsFromParagraph(object id) { return Pairs; }
    void FakeUpdateSegmentPairProperties(object pair, object props) { StatusWrites++; }
    IEnumerable<ISegmentPair> FakeSegmentPairs { get { return Pairs; } }
    public static bool HasRevisions(IAbstractMarkupDataContainer c)
    {
        foreach (var item in c)
        {
            if (item is IRevisionMarker) return true;
            var inner = item as IAbstractMarkupDataContainer;
            if (inner != null && HasRevisions(inner)) return true;
        }
        return false;
    }
}

public static class WordLevelWriteTest
{
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    const string Me = FakeDocument.Me;
    const string Reviser = "Acme Reviser";
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
        props.Add(PF.CreateComment(text, Reviser, Severity.Medium));
        var m = F.CreateCommentMarker(props);
        foreach (var k in kids) m.Add(k);
        return m;
    }
    static IRevisionMarker Rev(RevisionType type, string author, params IAbstractMarkupData[] kids)
    {
        var rp = PF.CreateRevisionProperties(type);
        rp.Author = author;
        rp.Date = new DateTime(2026, 11, 1, 9, 0, 0);
        var r = F.CreateRevision(rp);
        foreach (var k in kids) r.Add(k);
        return r;
    }
    static IRevisionMarker Ins(string author, params IAbstractMarkupData[] kids) { return Rev(RevisionType.Insert, author, kids); }
    static IRevisionMarker Del(string author, params IAbstractMarkupData[] kids) { return Rev(RevisionType.Delete, author, kids); }

    sealed class Spec
    {
        public string Id;
        public Func<IAbstractMarkupData[]> Source = () => new IAbstractMarkupData[0];
        public Func<IAbstractMarkupData[]> Target = () => new IAbstractMarkupData[0];
        public ConfirmationLevel Level = ConfirmationLevel.Unspecified;
    }
    static ISegmentPair Pair(Spec s)
    {
        var props = F.CreateSegmentPairProperties();
        props.Id = new SegmentId(s.Id);
        props.ConfirmationLevel = s.Level;
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
        var d = new FakeDocument { F = F, PF = PF, Pu = pu };
        d.Editor.TrackChanges = true;
        foreach (var s in specs) d.Pairs.Add(Pair(s));
        return d;
    }
    // One segment: the same plain text as source and target, unless a target is built.
    static FakeDocument One(string text, Func<IAbstractMarkupData[]> target = null, Func<IAbstractMarkupData[]> source = null)
    {
        return Doc(new Spec
        {
            Id = "1",
            Source = source ?? (() => new IAbstractMarkupData[] { T(text) }),
            Target = target ?? (() => new IAbstractMarkupData[] { T(text) }),
            Level = ConfirmationLevel.Translated
        });
    }
    static ISegment Tgt(FakeDocument d, string id = "1") { return d.Pairs.First(p => p.Properties.Id.Id == id).Target; }

    // ── The target's markup, revisions shown: {-deleted-} {+inserted+},
    //    "@name" after a revision that is not the editing user's, <gN>..</gN>,
    //    <xN/>, [c:commented]. ──
    static string Mk(IAbstractMarkupDataContainer c) { var sb = new StringBuilder(); MkInto(c, sb); return sb.ToString(); }
    static void MkInto(IAbstractMarkupDataContainer c, StringBuilder sb)
    {
        foreach (var item in c)
        {
            if (item is IText) sb.Append(((IText)item).Properties.Text.Replace("\n", "\\n"));
            else if (item is IRevisionMarker)
            {
                var r = (IRevisionMarker)item;
                var t = r.Properties.RevisionType;
                string open = t == RevisionType.Insert ? "{+" : t == RevisionType.Delete ? "{-" : "{?", close = t == RevisionType.Insert ? "+}" : t == RevisionType.Delete ? "-}" : "?}";
                sb.Append(open); MkInto(r, sb); sb.Append(close);
                if (r.Properties.Author != Me) sb.Append('@').Append(r.Properties.Author ?? "nobody");
            }
            else if (item is ICommentMarker) { sb.Append("[c:"); MkInto((ICommentMarker)item, sb); sb.Append(']'); }
            else if (item is ITagPair)
            {
                var p = (ITagPair)item;
                sb.Append("<g").Append(p.StartTagProperties.TagId.Id).Append('>'); MkInto(p, sb); sb.Append("</g").Append(p.StartTagProperties.TagId.Id).Append('>');
            }
            else if (item is IPlaceholderTag) sb.Append("<x").Append(((IPlaceholderTag)item).Properties.TagId.Id).Append("/>");
            else sb.Append("[" + item.GetType().Name + "]");
        }
    }
    static int Count(IAbstractMarkupDataContainer c, RevisionType? type = null)
    {
        int n = 0;
        foreach (var item in c)
        {
            var r = item as IRevisionMarker;
            if (r != null && (type == null || r.Properties.RevisionType == type)) n++;
            if (item is IAbstractMarkupDataContainer) n += Count((IAbstractMarkupDataContainer)item, type);
        }
        return n;
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
    // What Accept All / Reject All would leave, tags included: the plugin's own projection.
    static Type Merge { get { return P.GetType("Supervertaler.Trados.Core.TrackedTargetMerge", true); } }
    static string Accept(IAbstractMarkupDataContainer c) { return (string)Merge.GetMethod("Project", Any).Invoke(null, new object[] { c, true }); }
    static string Reject(IAbstractMarkupDataContainer c) { return (string)Merge.GetMethod("Project", Any).Invoke(null, new object[] { c, false }); }

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
        public string Json;
        public Dictionary<string, object> R;
        public Dictionary<string, object> Item(int i = 0) { var l = List(R, "results"); return i < l.Count ? l[i] : null; }
        public string Mode { get { return Str(Item(), "revisionMode"); } }
        public string Warning { get { return Str(Item(), "warning"); } }
        public string Error { get { return Str(Item(), "error"); } }
        public bool Ok { get { return Int(R, "applied") == 1 && Int(R, "failed") == 0; } }
    }
    static Seen Update(FakeDocument d, string requestJson)
    {
        var req = FromJson(typeof(BridgeUpdateSegmentsRequest), requestJson);
        var json = ToJson(Call(Part(d), "BridgeUpdateSegments", req));
        return new Seen { Json = json, R = (Dictionary<string, object>)Js.DeserializeObject(json) };
    }
    // update_segments for one segment, as an MCP client sends it.
    static Seen Write(FakeDocument d, string target, string id = "1", string extra = "")
    {
        return Update(d, "{\"updates\":[{\"id\":\"pu-1:" + id + "\",\"target\":" + Q(target) + extra + "}]}");
    }
    static string Src(FakeDocument d, string number)
    {
        var json = ToJson(Call(Part(d), "BuildBridgeSegments", new BridgeSegmentsQuery()));
        var r = (Dictionary<string, object>)Js.DeserializeObject(json);
        return Str(List(r, "segments").First(x => Str(x, "number") == number), "source");
    }
    static List<Dictionary<string, object>> List(Dictionary<string, object> r, string key)
    {
        object v;
        if (r == null || !r.TryGetValue(key, out v) || v == null) return new List<Dictionary<string, object>>();
        return ((object[])v).Cast<Dictionary<string, object>>().ToList();
    }
    static string Str(Dictionary<string, object> r, string key) { object v; return r != null && r.TryGetValue(key, out v) && v != null ? v.ToString() : null; }
    static int Int(Dictionary<string, object> r, string key) { object v; return r != null && r.TryGetValue(key, out v) && v != null ? Convert.ToInt32(v) : 0; }

    static bool TrackingRestored(FakeDocument d) { return d.Editor.TrackChanges && d.Editor.Protect && d.Editor.TurnOffs == d.Editor.Reverts; }
    // A word-level write: applied, reported 'word' with no warning, made with
    // tracking switched off for the write, and tracking on again afterwards.
    static bool WordLevel(FakeDocument d, Seen s)
    {
        return s.Ok && s.Mode == "word" && s.Warning == null && d.TrackingAtApply == false && TrackingRestored(d);
    }
    // A fallback: applied as Studio's own whole-segment change, with the reason.
    static bool WholeSegment(FakeDocument d, Seen s, string reason)
    {
        return s.Ok && s.Mode == "whole-segment" && s.Warning != null
            && s.Warning.StartsWith("tracked as a whole-segment change (") && s.Warning.Contains(reason)
            && d.TrackingAtApply == true && TrackingRestored(d);
    }

    const string Original = "The United Kingdom secures exemptions for the currency and social policy.";
    const string First    = "The United Kingdom secures opt-outs from the single currency and social policy.";
    const string Second   = "The United Kingdom secured opt-outs from the single currency and social policy.";

    // ── A: the acceptance tests ───────────────────────────────────────
    static void Acceptance()
    {
        Console.WriteLine("=== A: the acceptance tests (Track Changes on)");

        // 1. A clean segment, one word changed.
        var d = One(Original);
        var s = Write(d, Original.Replace("secures", "secured"));
        Say("1: " + Mk(Tgt(d)));
        Check(WordLevel(d, s) && Mk(Tgt(d)) == Original.Replace("secures", "{-secures-}{+secured+}"),
            "A1 clean segment, one word changed: 'word', one deletion and one insertion (must not regress)");

        // 2. The field report: write 1, then write 2 changes a DIFFERENT word.
        d = One(Original);
        s = Write(d, First);
        var mk1 = Mk(Tgt(d));
        Say("2: after write 1: " + mk1);
        Check(WordLevel(d, s) && Accept(Tgt(d)) == First && Reject(Tgt(d)) == Original, "A2 write 1: 'word'");
        int rev1 = Count(Tgt(d));
        s = Write(d, Second);
        Say("2: after write 2: " + Mk(Tgt(d)));
        Check(WordLevel(d, s), "A2 write 2 on a segment that already carries tracked changes: 'word', no warning (" + s.Mode + "; " + s.Warning + ")");
        Check(Mk(Tgt(d)) == mk1.Replace("secures", "{-secures-}{+secured+}"),
            "A2 exactly one more revision pair, secures -> secured; every earlier marker untouched");
        Check(Count(Tgt(d)) == rev1 + 2 && d.WholeSegment == 0, "A2 two revisions more than before, and Studio recorded no whole-segment change");
        // 8 for this segment.
        Check(Reject(Tgt(d)) == Original, "A8 after test 2: Reject All gives exactly the original");
        Check(Accept(Tgt(d)) == Second, "A8 after test 2: Accept All gives exactly the last text sent");
        // get_tracked_changes agrees.
        var tc = (Dictionary<string, object>)Js.DeserializeObject(ToJson(Call(Part(d), "BridgeGetTrackedChanges", new BridgeTrackedChangesQuery())));
        var change = List(tc, "changes").FirstOrDefault();
        Check(change != null && Str(change, "before") == Original && Str(change, "after") == Second,
            "A2 get_tracked_changes: before = the original, after = the last text sent");

        // 3. A word INSIDE an earlier insertion: edited in place.
        d = One(Original);
        Write(d, First);
        mk1 = Mk(Tgt(d));
        int del1 = Count(Tgt(d), RevisionType.Delete), ins1 = Count(Tgt(d), RevisionType.Insert);
        var third = First.Replace("opt-outs", "carve-outs");
        s = Write(d, third);
        Say("3: " + Mk(Tgt(d)));
        Check(WordLevel(d, s) && Mk(Tgt(d)) == mk1.Replace("{+opt-outs+}", "{+carve-outs+}"),
            "A3 a word inside an earlier insertion: that insertion is edited in place");
        Check(Count(Tgt(d), RevisionType.Delete) == del1 && Count(Tgt(d), RevisionType.Insert) == ins1 && Mk(Tgt(d)).Contains("{-exemptions-}"),
            "A3 no deletion mark for text that was never in the original; the original deletion is still there");
        Check(Reject(Tgt(d)) == Original && Accept(Tgt(d)) == third, "A8 after test 3: Reject All = the original, Accept All = the last text sent");

        // A word added right beside an earlier insertion joins it.
        d = One(Original);
        Write(d, First);
        mk1 = Mk(Tgt(d));
        s = Write(d, First.Replace("opt-outs", "further opt-outs"));
        Say("3b: " + Mk(Tgt(d)));
        Check(WordLevel(d, s) && Mk(Tgt(d)) == mk1.Replace("{+opt-outs+}", "{+further opt-outs+}"),
            "A3b a word added beside an earlier insertion becomes part of it (one insertion, not two)");

        // An earlier insertion removed altogether: gone, no deletion mark.
        d = One(Original);
        Write(d, First);
        mk1 = Mk(Tgt(d));
        s = Write(d, First.Replace("single ", ""));
        Say("3c: " + Mk(Tgt(d)));
        Check(WordLevel(d, s) && Mk(Tgt(d)) == mk1.Replace("{+single +}", "") && Reject(Tgt(d)) == Original,
            "A3c an earlier insertion taken out again simply goes; nothing else changes");

        // 4. The identical text again.
        d = One(Original);
        Write(d, First);
        mk1 = Mk(Tgt(d));
        d.Pairs[0].Properties.ConfirmationLevel = ConfirmationLevel.Translated;
        int statusWrites = d.StatusWrites;
        s = Write(d, First);
        Check(s.Ok && s.Mode == "word" && s.Warning == null && Mk(Tgt(d)) == mk1 && TrackingRestored(d),
            "A4 the identical text again: no new revisions, markup exactly as it was");
        Check(d.Pairs[0].Properties.ConfirmationLevel == ConfirmationLevel.Translated && d.StatusWrites == statusWrites,
            "A4 status unchanged (still Translated, no status write)");

        // 5. Back to the original wording.
        d = One(Original);
        Write(d, First);
        s = Write(d, Original);
        Say("5: " + Mk(Tgt(d)));
        Check(WordLevel(d, s), "A5 the original wording sent back: 'word', no whole-segment fallback");
        Check(Mk(Tgt(d)) == Original && Count(Tgt(d)) == 0,
            "A5 the insertions cancel against their deletions: plain text again, no revision left");
        // Partly back: only one of the two edits undone.
        d = One(Original);
        Write(d, First);
        s = Write(d, First.Replace("opt-outs", "exemptions"));
        Say("5b: " + Mk(Tgt(d)));
        Check(WordLevel(d, s) && !Mk(Tgt(d)).Contains("exemptions-}") && Mk(Tgt(d)).Contains("{-for-}{+from+}") && Reject(Tgt(d)) == Original,
            "A5b one earlier edit undone, the others kept as they were");

        // 6. Inline tags, with earlier revisions; text changed inside and outside a tag pair.
        Func<IAbstractMarkupData[]> tagged = () => new IAbstractMarkupData[]
            { T("Press "), G("5", T("Start now")), T(" then "), G("6", T("Stop")), T(" and wait "), X("7"), T(" to end.") };
        d = One(null, tagged, tagged);
        var src = Src(d, "1");
        Say("6: source as get_segments gives it: " + src);
        var original6 = Accept(Tgt(d));
        s = Write(d, src.Replace("Press", "Push"));
        Check(WordLevel(d, s), "A6 tagged segment, write 1: 'word'");
        mk1 = Mk(Tgt(d));
        var sent6 = src.Replace("Press", "Push").Replace("Start now", "Begin now").Replace("to end", "to finish");
        s = Write(d, sent6);
        Say("6: " + Mk(Tgt(d)));
        Check(WordLevel(d, s) && Int(s.Item(), "tagsReplaced") == 0,
            "A6 write 2, text changed inside and outside a tag pair: 'word', no tag replaced");
        Check(Mk(Tgt(d)) == mk1.Replace("Start now", "{-Start-}{+Begin+} now").Replace("to end", "to {-end-}{+finish+}"),
            "A6 only those two words revised; tags and the earlier revision exactly as they were");
        var qa = (Dictionary<string, object>)Js.DeserializeObject(ToJson(Call(Part(d), "BridgeRunQaCheck", new BridgeQaQuery { Type = "tags", Limit = 50 })));
        Check(List(qa, "issues").Count == 0 && List(qa, "results").Count == 0, "A6 check_tags reports nothing for it");
        Check(Reject(Tgt(d)) == original6, "A8 after test 6: Reject All = the original, tags included");
        Check(Accept(Tgt(d)) == original6.Replace("Press", "Push").Replace("Start now", "Begin now").Replace("to end", "to finish"),
            "A8 after test 6: Accept All = the last text sent, tags included");

        // 7. A line break in the target.
        const string broken = "First line of the label\nSecond line of the label, in bold.";
        d = One(broken);
        Write(d, broken.Replace("First", "Top"));
        mk1 = Mk(Tgt(d));
        s = Write(d, broken.Replace("First", "Top").Replace("bold", "italics"));
        Say("7: " + Mk(Tgt(d)));
        Check(WordLevel(d, s) && Mk(Tgt(d)) == mk1.Replace("bold", "{-bold-}{+italics+}"), "A7 a target with a line break, second write: 'word'");
        Check(Accept(Tgt(d)) == "Top line of the label\nSecond line of the label, in italics." && Reject(Tgt(d)) == broken,
            "A7 the line break is still there, accepted and rejected");

        // 9. add_comment 'on' after a surgical second write, and a write after the comment.
        d = One(Original);
        Write(d, First);
        Write(d, Second);
        var wrap = VP.GetMethod("WrapRangeInCommentMarker", Any);
        // Words that run from plain text into an insertion and out again.
        var failure = (string)wrap.Invoke(null, new object[] { Tgt(d), "the single currency", 1, "Check the term.", Severity.Medium });
        Say("9: " + Mk(Tgt(d)));
        Check(failure == null && Comments(Tgt(d)) >= 1 && Accept(Tgt(d)) == Second && Reject(Tgt(d)) == Original,
            "A9 add_comment 'on' after a surgical second write: anchored, text unchanged (" + (failure ?? "ok") + ")");
        var commented = new StringBuilder();
        foreach (var m in Markers(Tgt(d))) commented.Append(Accept(m));
        Check(commented.ToString() == "the single currency", "A9 the comment sits on exactly the words named: \"" + commented + "\"");
        int commentsBefore = Comments(Tgt(d));
        s = Write(d, Second.Replace("social", "employment"));
        Say("9: after a third write: " + Mk(Tgt(d)));
        Check(WordLevel(d, s) && Comments(Tgt(d)) == commentsBefore && Accept(Tgt(d)) == Second.Replace("social", "employment") && Reject(Tgt(d)) == Original,
            "A9 a third write after the comment: 'word', the comment where it was");

        // 10. Track Changes OFF + unreviewed revisions: refused, nothing written.
        d = One(Original);
        Write(d, First);
        mk1 = Mk(Tgt(d));
        d.Editor.TrackChanges = false;
        s = Write(d, Second);
        Say("10: " + s.Error);
        Check(Int(s.R, "applied") == 0 && Int(s.R, "failed") == 1 && s.Error != null
              && s.Error.Contains("protects them while Track Changes is off") && s.Error.Contains("Nothing was written"),
            "A10 Track Changes off + unreviewed tracked changes: refused, with the reason");
        Check(Mk(Tgt(d)) == mk1 && !d.Editor.TrackChanges && d.Editor.TurnOffs == d.Editor.Reverts && s.Mode == null,
            "A10 nothing written, Track Changes still off, no revisionMode");
        // Track Changes off + a clean segment: applied untracked, as before.
        d = One(Original);
        d.Editor.TrackChanges = false;
        s = Write(d, First);
        Check(s.Ok && s.Mode == null && Mk(Tgt(d)) == First && d.Editor.TurnOffs == 0,
            "A10b Track Changes off + a clean segment: written untracked, no revisionMode (unchanged)");
    }
    static List<ICommentMarker> Markers(IAbstractMarkupDataContainer c)
    {
        var list = new List<ICommentMarker>();
        foreach (var item in c)
        {
            if (item is ICommentMarker) list.Add((ICommentMarker)item);
            else if (item is IAbstractMarkupDataContainer) list.AddRange(Markers((IAbstractMarkupDataContainer)item));
        }
        return list;
    }

    // ── B: another author's tracked changes ───────────────────────────
    static void OtherAuthors()
    {
        Console.WriteLine("=== B: tracked changes by another author, or by nobody");

        // Their deletion (holding a tag, even) and their insertion, not touched by the edit.
        Func<IAbstractMarkupData[]> t = () => new IAbstractMarkupData[]
            { T("Close the "), Del(Reviser, T("main "), G("9", T("red"))), Ins(Reviser, T("inlet")), T(" valve before starting the pump.") };
        var d = One("Close the inlet valve before starting the pump.", t);
        var before = Mk(Tgt(d));
        var s = Write(d, "Close the inlet valve before stopping the pump.");
        Say("B1: " + Mk(Tgt(d)));
        Check(WordLevel(d, s) && Mk(Tgt(d)) == before.Replace("starting", "{-starting-}{+stopping+}"),
            "B1 an edit elsewhere in the segment: 'word'; the reviser's deletion (tag and all) and insertion exactly as they were");

        // Text added at the edge of their insertion: a marker of our own, beside theirs.
        d = One("The valve must be closed.", () => new IAbstractMarkupData[] { T("The valve "), Ins(Reviser, T("must be closed")), T(".") });
        s = Write(d, "The valve must be closed first.");
        Say("B2: " + Mk(Tgt(d)));
        Check(WordLevel(d, s) && Mk(Tgt(d)) == "The valve {+must be closed+}@" + Reviser + "{+ first+}.",
            "B2 text added right after the reviser's insertion: our own insertion beside it, theirs untouched");

        d = One("A b d", () => new IAbstractMarkupData[] { T("A "), Ins(Reviser, T("b ")), Ins(Reviser, T("d")) });
        s = Write(d, "A b c d");
        Say("B3: " + Mk(Tgt(d)));
        Check(WordLevel(d, s) && Mk(Tgt(d)) == "A {+b +}@" + Reviser + "{+c +}{+d+}@" + Reviser,
            "B3 text added between two of the reviser's insertions: our own insertion between them");

        d = One("A b", () => new IAbstractMarkupData[] { T("A "), Ins(Reviser, T("b")) });
        s = Write(d, "A b c");
        Say("B4: " + Mk(Tgt(d)));
        Check(WordLevel(d, s) && Mk(Tgt(d)) == "A {+b+}@" + Reviser + "{+ c+}",
            "B4 text added after the reviser's insertion at the very end: our own insertion after it");

        // An edit that reaches INTO their insertion: not ours to rewrite. Falls back, says why.
        const string Foreign = "another author inserted as a tracked change";
        d = One("The valve must be closed first.", () => new IAbstractMarkupData[] { T("The valve "), Ins(Reviser, T("must be closed")), T(" first.") });
        s = Write(d, "The valve must be shut first.");
        Say("B5: " + s.Warning);
        Check(WholeSegment(d, s, Foreign) && Accept(Tgt(d)) == "The valve must be shut first.",
            "B5 a word changed inside the reviser's insertion: whole-segment, and the warning names that reason");

        d = One("The valve must be closed first.", () => new IAbstractMarkupData[] { T("The valve "), Ins(Reviser, T("must be closed")), T(" first.") });
        s = Write(d, "The valve must always be closed first.");
        Check(WholeSegment(d, s, Foreign), "B6 a word added in the middle of the reviser's insertion: whole-segment, same reason");

        // A revision with no author is nobody's, so not ours either.
        d = One("The valve must be closed first.", () => new IAbstractMarkupData[] { T("The valve "), Ins(null, T("must be closed")), T(" first.") });
        s = Write(d, "The valve must be shut first.");
        Check(WholeSegment(d, s, Foreign), "B7 an insertion with no author is treated as someone else's");

        // Our own name on it: edited in place, by contrast.
        d = One("The valve must be closed first.", () => new IAbstractMarkupData[] { T("The valve "), Ins(Me, T("must be closed")), T(" first.") });
        s = Write(d, "The valve must be shut first.");
        Check(WordLevel(d, s) && Mk(Tgt(d)) == "The valve {+must be shut+} first.", "B8 the same edit inside our OWN insertion: edited in place");

        // The reviser's deletion is not cancelled by our re-typing its words.
        d = One("Open the valve.", () => new IAbstractMarkupData[] { T("Open the "), Del(Reviser, T("main ")), T("valve.") });
        s = Write(d, "Open the main valve.");
        Say("B9: " + Mk(Tgt(d)));
        Check(WordLevel(d, s) && Count(Tgt(d), RevisionType.Delete) == 1 && Count(Tgt(d), RevisionType.Insert) == 1,
            "B9 retyping words the reviser deleted: their deletion stays, ours is a new insertion (no silent cancelling)");

        // After a whole-segment fallback, the next write is word-level again.
        d = One("The valve must be closed first.", () => new IAbstractMarkupData[] { T("The valve "), Ins(Reviser, T("must be closed")), T(" first.") });
        Write(d, "The valve must be shut first.");
        var whole = Mk(Tgt(d));
        s = Write(d, "The valve must be shut at once.");
        Say("B10: " + Mk(Tgt(d)));
        Check(WordLevel(d, s) && Mk(Tgt(d)) == whole.Replace("shut first.+}", "shut at once.+}") && d.WholeSegment == 1,
            "B10 the write after a whole-segment change edits inside that change: 'word', no second whole-segment change");
    }

    // ── C: every other fallback and failure path ──────────────────────
    static void Fallbacks()
    {
        Console.WriteLine("=== C: fallbacks and failures (each must say why, and leave Track Changes on)");
        const string text = "Close the inlet valve first.";
        const string sent = "Close the outlet valve first.";

        var d = One(text, () => new IAbstractMarkupData[] { T("Close the "), Ins(Me, T("inlet"), Del(Me, T("old "))), T(" valve first.") });
        var s = Write(d, sent);
        Say("C1: " + s.Warning);
        Check(WholeSegment(d, s, "one tracked change inside another") && Accept(Tgt(d)) == sent, "C1 one revision inside another: whole-segment, reason named");

        Func<IAbstractMarkupData[]> src2 = () => new IAbstractMarkupData[] { T("Close the "), G("5", T("inlet")), T(" valve first.") };
        d = One(null, () => new IAbstractMarkupData[] { T("Close the "), Ins(Me, G("5", T("inlet"))), T(" valve first.") }, src2);
        s = Write(d, Src(d, "1").Replace("inlet", "outlet"));
        Say("C2: " + s.Warning);
        Check(WholeSegment(d, s, "an earlier tracked insertion in the segment covers a tag"), "C2 an earlier insertion that covers a tag: whole-segment, reason named");

        // A revision recorded on a tag itself (a placeholder deleted with tracking on).
        Func<IAbstractMarkupData[]> src3 = () => new IAbstractMarkupData[] { T("Close the inlet valve "), X("7"), T("first.") };
        d = One(null, () =>
        {
            var x = X("7");
            x.RevisionProperties = PF.CreateRevisionProperties(RevisionType.Delete);
            return new IAbstractMarkupData[] { T("Close the inlet valve "), x, T("first.") };
        }, src3);
        s = Write(d, Src(d, "1").Replace("inlet", "outlet"));
        Say("C3: " + s.Warning);
        Check(WholeSegment(d, s, "a tag in the segment carries a tracked change of its own"), "C3 a revision on a tag itself: whole-segment, reason named");

        // Tags removed from a segment that carries revisions (force: the tag check would refuse it otherwise).
        d = One(null, () => new IAbstractMarkupData[] { T("Close the "), G("5", T("inlet")), Del(Me, T(" main")), T(" valve first.") }, src2);
        s = Write(d, "Close the outlet valve first.", "1", ",\"force\":true");
        Say("C4: " + s.Warning);
        Check(s.Ok && s.Mode == "whole-segment" && s.Warning.Contains("tracked as a whole-segment change (the inline tags differ")
              && d.TrackingAtApply == true && TrackingRestored(d),
            "C4 tags removed and text changed in a segment with revisions: whole-segment, reason named");
        d = One(null, () => new IAbstractMarkupData[] { T("Close the "), G("5", T("inlet")), Del(Me, T(" main")), T(" valve first.") }, src2);
        s = Write(d, "Close the inlet valve first.", "1", ",\"force\":true");
        Say("C4b: " + s.Warning);
        Check(s.Ok && s.Mode == "whole-segment" && s.Warning.Contains("the segment already carries tracked changes") && TrackingRestored(d),
            "C4b a tag pair removed (text unchanged) in a segment with revisions: whole-segment, reason named");

        // Commented words removed from our own earlier insertion.
        d = One("Close the inlet valve first.", () => new IAbstractMarkupData[]
            { T("Close the "), Ins(Me, Comment("Is this the right one?", T("inlet")), T(" valve")), T(" first.") });
        s = Write(d, "Close the valve first.");
        Say("C5: " + s.Warning);
        Check(WholeSegment(d, s, "removes commented text from an earlier tracked insertion"), "C5 commented words removed from an earlier insertion: whole-segment, reason named");
        // ...whereas editing inside that comment is fine.
        d = One("Close the inlet valve first.", () => new IAbstractMarkupData[]
            { T("Close the "), Ins(Me, Comment("Is this the right one?", T("inlet")), T(" valve")), T(" first.") });
        s = Write(d, "Close the outlet valve first.");
        Say("C5b: " + Mk(Tgt(d)));
        Check(WordLevel(d, s) && Mk(Tgt(d)) == "Close the {+[c:outlet] valve+} first." && Comments(Tgt(d)) == 1,
            "C5b a commented word CHANGED inside an earlier insertion: 'word', comment kept");

        // A kind of revision that is neither insertion nor deletion: TQA feedback.
        d = One(text, () =>
        {
            var fp = PF.CreateFeedbackProperties(RevisionType.FeedbackAdded);
            fp.Author = Me;
            var fb = F.CreateFeedback(fp);
            fb.Add(T("inlet"));
            return new IAbstractMarkupData[] { T("Close the "), fb, T(" valve first.") };
        });
        s = Write(d, sent);
        Say("C6: " + s.Warning);
        Check(WholeSegment(d, s, "a kind of tracked change this cannot edit around (FeedbackAdded)"), "C6 TQA feedback in the segment: whole-segment, reason named");

        // Track Changes cannot be switched for the write.
        d = One(Original);
        Write(d, First);
        d.Editor.FailTurnOff = true;
        s = Write(d, Second);
        Say("C7: " + s.Warning);
        Check(s.Ok && s.Mode == "whole-segment" && s.Warning.Contains("Track Changes could not be switched for the write")
              && d.TrackingAtApply == true && d.Editor.TrackChanges && Accept(Tgt(d)) == Second,
            "C7 Track Changes cannot be switched: whole-segment, reason named, tracking still on");

        // Studio throws while applying a surgical write.
        d = One(Original);
        Write(d, First);
        var mk1 = Mk(Tgt(d));
        d.ThrowOnApply = true;
        s = Write(d, Second);
        Say("C8: " + s.Error);
        Check(Int(s.R, "failed") == 1 && s.Error != null && s.Error.Contains("fake: Studio could not apply the edit") && s.Mode == null,
            "C8 Studio throws while applying: reported as failed, with Studio's message");
        Check(Mk(Tgt(d)) == mk1 && TrackingRestored(d), "C8 the segment is as it was, and Track Changes is on again");

        // Studio declines the surgical write without a word.
        d = One(Original);
        Write(d, First);
        mk1 = Mk(Tgt(d));
        d.DeclineAll = true;
        s = Write(d, Second);
        Say("C9: " + s.Error);
        Check(Int(s.R, "failed") == 1 && s.Error != null && s.Error.Contains("Trados Studio did not apply the change") && s.Mode == null,
            "C9 Studio declines the write: reported as failed, not as written");
        Check(Mk(Tgt(d)) == mk1 && TrackingRestored(d), "C9 the segment is as it was, and Track Changes is on again");

        // An earlier revision inside a comment over the whole target (the shape that was refused before 208).
        d = One("Read the manual before you switch the machine on.", () => new IAbstractMarkupData[]
            { Comment("Check this.", T("Read the manual before you switch the machine "), Del(Me, T("off")), Ins(Me, T("on")), T(".")) });
        s = Write(d, "Read the whole manual before you switch the machine on.");
        Say("C10: " + Mk(Tgt(d)));
        Check(WordLevel(d, s) && Mk(Tgt(d)) == "[c:Read the {+whole +}manual before you switch the machine {-off-}{+on+}.]" && Comments(Tgt(d)) == 1,
            "C10 earlier revisions inside a comment over the whole target: 'word', one comment, where it was");

        // A batch: a clean segment, one with earlier revisions, one that must fall back. Each reports its own mode.
        var batch = Doc(
            new Spec { Id = "1", Source = () => new IAbstractMarkupData[] { T(text) }, Target = () => new IAbstractMarkupData[] { T(text) } },
            new Spec { Id = "2", Source = () => new IAbstractMarkupData[] { T(text) }, Target = () => new IAbstractMarkupData[] { T("Close the "), Del(Me, T("main ")), T("inlet valve first.") } },
            new Spec { Id = "3", Source = () => new IAbstractMarkupData[] { T(text) }, Target = () => new IAbstractMarkupData[] { T("Close the "), Ins(Reviser, T("inlet")), T(" valve first.") } });
        s = Update(batch, "{\"updates\":[{\"id\":\"pu-1:1\",\"target\":" + Q(sent) + "},{\"id\":\"pu-1:2\",\"target\":" + Q(sent) + "},{\"id\":\"pu-1:3\",\"target\":" + Q(sent) + "}]}");
        Check(Int(s.R, "applied") == 3 && Str(s.Item(0), "revisionMode") == "word" && Str(s.Item(1), "revisionMode") == "word"
              && Str(s.Item(2), "revisionMode") == "whole-segment" && Str(s.Item(0), "warning") == null && Str(s.Item(1), "warning") == null
              && (Str(s.Item(2), "warning") ?? "").Contains("another author"),
            "C11 a batch of three: word, word, whole-segment - each item reports its own mode, only the fallback warns");
        Check((Str(s.R, "note") ?? "").Contains("1 segment(s) could not be written as word-level revisions") && TrackingRestored(batch),
            "C11 the response note counts the one fallback; Track Changes on again after the batch");
    }

    // ── D: scale ──────────────────────────────────────────────────────
    static void Scale()
    {
        Console.WriteLine("=== D: scale");
        const int N = 2000, PerCall = 40;
        var specs = new Spec[N];
        for (int i = 0; i < N; i++)
        {
            string text = "Segment " + (i + 1) + ": the Acme pump runs at speed " + (i % 97) + " and then stops safely.";
            specs[i] = new Spec { Id = (i + 1).ToString(), Source = () => new IAbstractMarkupData[] { T(text) }, Target = () => new IAbstractMarkupData[] { T(text) } };
        }
        var d = Doc(specs);
        var original = d.Pairs.Select(p => Accept(p.Target)).ToArray();
        Func<string, string>[] passes =
        {
            t => t.Replace("runs", "operates"),                                   // a clean segment
            t => t.Replace("runs", "operates").Replace("stops", "halts"),         // another word
            t => t.Replace("runs", "works").Replace("stops", "halts")             // a word inside an earlier insertion
        };
        var part = Part(d);
        for (int pass = 0; pass < passes.Length; pass++)
        {
            var sw = Stopwatch.StartNew();
            int word = 0, other = 0;
            for (int from = 0; from < N; from += PerCall)
            {
                var sb = new StringBuilder("{\"updates\":[");
                for (int i = from; i < Math.Min(N, from + PerCall); i++)
                    sb.Append(i > from ? "," : "").Append("{\"id\":\"pu-1:").Append(i + 1).Append("\",\"target\":").Append(Q(passes[pass](original[i]))).Append('}');
                sb.Append("]}");
                var json = ToJson(Call(part, "BridgeUpdateSegments", FromJson(typeof(BridgeUpdateSegmentsRequest), sb.ToString())));
                foreach (var item in List((Dictionary<string, object>)Js.DeserializeObject(json), "results"))
                    if (Str(item, "revisionMode") == "word" && Str(item, "warning") == null && Str(item, "error") == null) word++; else other++;
            }
            sw.Stop();
            Check(word == N && other == 0, "D write " + (pass + 1) + " of " + N + " segments: all 'word' (" + word + "), "
                + sw.ElapsedMilliseconds + " ms, " + (sw.ElapsedMilliseconds * 1000 / N) + " microseconds a segment");
        }
        bool allRight = true;
        for (int i = 0; i < N && allRight; i++)
            allRight = Reject(d.Pairs[i].Target) == original[i] && Accept(d.Pairs[i].Target) == passes[2](original[i])
                    && Count(d.Pairs[i].Target) == 4;
        Check(allRight && d.WholeSegment == 0 && TrackingRestored(d),
            "D after three writes each: every segment rejects to its original, accepts to the last text sent, holds 4 revisions; no whole-segment change; Track Changes on");

        // One long segment (600 words) in which every other word was already replaced
        // (600 revisions); 40 more words changed all over it, half of them words that
        // were never touched (a new deletion and insertion each) and half of them
        // words inside an earlier insertion (edited in place: no new revision).
        var words = new List<string>();
        for (int i = 0; i < 600; i++) words.Add("word" + i);
        string longText = string.Join(" ", words) + ".";
        d = One(longText);
        var w1 = new List<string>(words);
        for (int i = 0; i < 600; i += 2) w1[i] = "first" + i;
        var s = Write(d, string.Join(" ", w1) + ".");
        int after1 = Count(Tgt(d));
        var w2 = new List<string>(w1);
        int untouched = 0, insideInsertion = 0;
        for (int i = 1; i < 600; i += 15)
        {
            w2[i] = "second" + i;
            if (i % 2 == 1) untouched++; else insideInsertion++;
        }
        var sw2 = Stopwatch.StartNew();
        s = Write(d, string.Join(" ", w2) + ".");
        sw2.Stop();
        Check(WordLevel(d, s) && Count(Tgt(d)) == after1 + 2 * untouched && Reject(Tgt(d)) == longText && Accept(Tgt(d)) == string.Join(" ", w2) + ".",
            "D a 600-word segment with " + after1 + " earlier revisions, " + (untouched + insideInsertion) + " more words changed (" + insideInsertion
            + " of them inside an earlier insertion): 'word', exactly " + (2 * untouched) + " new revisions, " + sw2.ElapsedMilliseconds + " ms");

        // Far beyond any real segment: 6,000 words, first and last changed on a second
        // write. The diff's size limit makes the change coarse, never wrong.
        words.Clear();
        for (int i = 0; i < 6000; i++) words.Add("word" + i);
        longText = string.Join(" ", words) + ".";
        d = One(longText);
        w1 = new List<string>(words);
        w1[3000] = "middle";
        Write(d, string.Join(" ", w1) + ".");
        w2 = new List<string>(w1);
        w2[0] = "start"; w2[5999] = "finish";
        sw2 = Stopwatch.StartNew();
        s = Write(d, string.Join(" ", w2) + ".");
        sw2.Stop();
        Check(s.Ok && s.Mode == "word" && Reject(Tgt(d)) == longText && Accept(Tgt(d)) == string.Join(" ", w2) + "." && TrackingRestored(d),
            "D a 6,000-word segment, first and last word changed on a second write: still correct both ways (" + Count(Tgt(d)) + " revisions, "
            + sw2.ElapsedMilliseconds + " ms; coarse by design above the diff's size limit)");
    }

    // ── E: find_and_replace and SuperSearch's Replace ─────────────────
    sealed class Replaced { public string Mode, Note; public Exception Error; }
    static Replaced Replace(FakeDocument d, string find, string with)
    {
        var apply = P.GetType("Supervertaler.Trados.Core.TargetTextReplace", true).GetMethod("Apply", Any);
        Func<string, string> swap = t => t.Replace(find, with);
        Action<ISegmentPair, CancelEventArgs> edit = (sp, cancel) => apply.Invoke(null, new object[] { sp.Target, swap });
        var r = new Replaced();
        try
        {
            var o = P.GetType("Supervertaler.Trados.Core.SegmentWriteCheck", true).GetMethod("ProcessChecked", Any)
                .Invoke(null, new object[] { d, d.Pairs[0], "Supervertaler MCP", edit, false, true, null, false });
            if (o != null)
            {
                r.Mode = (string)o.GetType().GetField("RevisionMode").GetValue(o);
                r.Note = (string)o.GetType().GetField("Note").GetValue(o);
            }
        }
        catch (TargetInvocationException ex) { r.Error = ex.InnerException; }
        return r;
    }
    static void OtherDoors()
    {
        Console.WriteLine("=== E: find_and_replace / SuperSearch Replace on a segment with earlier tracked changes");
        Func<string, Func<IAbstractMarkupData[]>> seg = author => () => new IAbstractMarkupData[]
            { T("Make sure the "), G("1", T("EMERGENCY STOP")), T(" button is "), Del(author, T("pressed")), Ins(author, T("released")), T(" before you start.") };
        const string was = "Make sure the <1>EMERGENCY STOP</1> button is pressed before you start.";

        var d = One(null, seg(Me), seg(Me));
        var before = Mk(Tgt(d));
        var r = Replace(d, "before", "whenever");
        Say("E1: " + Mk(Tgt(d)));
        Check(r.Error == null && r.Mode == "word" && r.Note == null && Mk(Tgt(d)) == before.Replace("before", "{-before-}{+whenever+}")
              && d.TrackingAtApply == false && TrackingRestored(d),
            "E1 a word replaced elsewhere in the segment: 'word'; the earlier revisions and the tag exactly as they were");
        Check(Reject(Tgt(d)) == was, "E1 Reject All still gives the segment as it was before any change");

        d = One(null, seg(Me), seg(Me));
        r = Replace(d, "released", "unlocked");
        Say("E2: " + Mk(Tgt(d)));
        Check(r.Error == null && r.Mode == "word" && Mk(Tgt(d)) == before.Replace("{+released+}", "{+unlocked+}") && Reject(Tgt(d)) == was,
            "E2 the word inside our own earlier insertion replaced: 'word', edited in place");

        d = One(null, seg(Reviser), seg(Reviser));
        r = Replace(d, "released", "unlocked");
        Say("E3: " + r.Note);
        Check(r.Error == null && r.Mode == "whole-segment" && r.Note != null && r.Note.Contains("another author inserted as a tracked change")
              && d.TrackingAtApply == true && TrackingRestored(d) && Reject(Tgt(d)) == was,
            "E3 the word inside the REVISER's insertion replaced: whole-segment, reason named, Reject All still the original");
    }

    // ── F: Batch Translate and Alt+T (wordLevel + overTextOnly) ────────
    static Replaced Translate(FakeDocument d, string text, bool overTextOnly = true)
    {
        Action<ISegmentPair, CancelEventArgs> edit = (sp, cancel) => { sp.Target.Clear(); sp.Target.Add(T(text)); };
        var r = new Replaced();
        try
        {
            var o = P.GetType("Supervertaler.Trados.Core.SegmentWriteCheck", true).GetMethod("ProcessChecked", Any)
                .Invoke(null, new object[] { d, d.Pairs[0], "Supervertaler", edit, false, true, null, overTextOnly });
            if (o != null)
            {
                r.Mode = (string)o.GetType().GetField("RevisionMode").GetValue(o);
                r.Note = (string)o.GetType().GetField("Note").GetValue(o);
            }
        }
        catch (TargetInvocationException ex) { r.Error = ex.InnerException; }
        return r;
    }
    static void Translations()
    {
        Console.WriteLine("=== F: a translation written by Batch Translate / Alt+T with Track Changes on");
        const string fuzzy = "The Acme pump is switched on at 230 V before the valve opens.";
        const string adapted = "The Acme pump is switched off at 230 V before the valve closes.";

        var d = One(fuzzy);
        var r = Translate(d, adapted);
        Say("F1: " + Mk(Tgt(d)));
        Check(r.Error == null && r.Mode == "word" && r.Note == null && Reject(Tgt(d)) == fuzzy && Accept(Tgt(d)) == adapted
              && Count(Tgt(d)) == 4 && d.TrackingAtApply == false && TrackingRestored(d),
            "F1 over a fuzzy match, two words changed: 'word', two deletions and two insertions, Reject All gives the match back");

        d = One(null, () => new IAbstractMarkupData[0], () => new IAbstractMarkupData[] { T("De Acme-pomp wordt ingeschakeld.") });
        r = Translate(d, adapted);
        Check(r.Error == null && r.Mode == null && Accept(Tgt(d)) == adapted && d.TrackingAtApply == true
              && d.Editor.TurnOffs == 0 && d.Editor.TrackChanges,
            "F2 into an EMPTY target: left to Studio, Track Changes never switched off (" + d.Editor.TurnOffs + " switches), text written");

        d = One(null, () => new IAbstractMarkupData[0], () => new IAbstractMarkupData[] { T("De Acme-pomp wordt ingeschakeld.") });
        r = Translate(d, adapted, overTextOnly: false);
        Check(r.Error == null && r.Mode != null && Accept(Tgt(d)) == adapted && TrackingRestored(d),
            "F3 the same without overTextOnly (update_segments, unchanged): still recorded by the plugin ('" + r.Mode + "')");

        Func<IAbstractMarkupData[]> seg = () => new IAbstractMarkupData[]
            { T("The Acme pump is switched "), Del(Me, T("on")), Ins(Me, T("off")), T(" at 230 V before the valve opens.") };
        d = One(null, seg, seg);
        r = Translate(d, adapted);
        Say("F4: " + Mk(Tgt(d)));
        Check(r.Error == null && r.Mode == "word" && Reject(Tgt(d)) == fuzzy && Accept(Tgt(d)) == adapted && TrackingRestored(d),
            "F4 over a segment that already holds a tracked change: 'word', Reject All still gives the original");

        d = One(fuzzy);
        r = Translate(d, fuzzy);
        Check(r.Error == null && r.Mode == null && Count(Tgt(d)) == 0 && d.Editor.TurnOffs == 0,
            "F5 the AI returns the match unchanged: no revision, Track Changes never switched off");

        d = One(fuzzy);
        d.Editor.TrackChanges = false;
        r = Translate(d, adapted);
        Check(r.Error == null && r.Mode == null && Accept(Tgt(d)) == adapted && Count(Tgt(d)) == 0 && d.Editor.TurnOffs == 0,
            "F6 Track Changes off: a plain write, no revisions");
    }

    // ── G: what else 208 does with Track Changes ──────────────────────
    static Exception CommentEdit(FakeDocument d, string on, string text)
    {
        var wrap = VP.GetMethod("WrapRangeInCommentMarker", Any);
        Action<ISegmentPair, CancelEventArgs> edit = (sp, cancel) =>
            wrap.Invoke(null, new object[] { sp.Target, on, 1, text, Severity.Medium });
        try
        {
            P.GetType("Supervertaler.Trados.Core.SegmentWriteCheck", true).GetMethod("ProcessChecked", Any)
                .Invoke(null, new object[] { d, d.Pairs[0], "Supervertaler MCP", edit, true, false, null, false });
            return null;
        }
        catch (TargetInvocationException ex) { return ex.InnerException; }
    }
    static void Extras()
    {
        Console.WriteLine("=== G: the tool result names Track Changes; a comment is not a tracked change");
        var d = One(Original);
        var s = Write(d, First);
        Check(s.Ok && Str(s.R, "trackChanges") == "on", "G1 update_segments with Track Changes on: the result says trackChanges 'on' (" + Str(s.R, "trackChanges") + ")");
        d = One(Original);
        d.Editor.TrackChanges = false;
        s = Write(d, First);
        Check(s.Ok && Str(s.R, "trackChanges") == "off" && Count(Tgt(d)) == 0,
            "G2 with Track Changes off: written untracked, and the result says trackChanges 'off' (" + Str(s.R, "trackChanges") + ")");

        var words = Original.Split(' ');
        var on = words[0] + " " + words[1];
        d = One(Original);
        var err = CommentEdit(d, on, "Check the term.");
        Say("G3: " + Mk(Tgt(d)));
        Check(err == null && Comments(Tgt(d)) == 1 && Count(Tgt(d)) == 0 && Accept(Tgt(d)) == Original
              && d.TrackingAtApply == false && TrackingRestored(d),
            "G3 a comment on a clean segment, Track Changes on: the comment is there, NO revision, tracking back on");

        d = One(Original);
        Write(d, First);
        int revisions = Count(Tgt(d));
        var mk = Mk(Tgt(d));
        var firstWords = First.Split(' ');
        err = CommentEdit(d, firstWords[0], "A note.");
        Say("G4: " + Mk(Tgt(d)));
        Check(err == null && Comments(Tgt(d)) == 1 && Count(Tgt(d)) == revisions && Accept(Tgt(d)) == First && Reject(Tgt(d)) == Original
              && TrackingRestored(d),
            "G4 a comment on a segment that holds tracked changes: the " + revisions + " revisions as they were, none added");

        d = One(Original);
        d.Editor.TrackChanges = false;
        err = CommentEdit(d, on, "Check the term.");
        Check(err == null && Comments(Tgt(d)) == 1 && Count(Tgt(d)) == 0 && d.Editor.TurnOffs == 0 && !d.Editor.TrackChanges,
            "G5 a comment with Track Changes off: added, tracking never touched");
    }

    public static int Run(string pluginPath)
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

        Acceptance();
        OtherAuthors();
        Fallbacks();
        Scale();
        OtherDoors();
        Translations();
        Extras();
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
try { $code = [WordLevelWriteTest]::Run($Dll) }
catch { $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }; Write-Host $e.ToString(); exit 2 }
exit $code
