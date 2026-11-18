# Proofread active segment (18.20.209 / 19.20.209): the AI proofreads the one
# segment the cursor is in, a window offers its correction, Enter writes it.
# Also: the QuickLauncher menu's rule for a prompt that is in a folder twice.
#
# Runs the BUILT plugin's own code outside Studio, on REAL segments from
# Studio's own factory, through a stand-in for Studio's document (generated
# from Studio's IStudioDocument, as tools\fuzzyfixer_test.ps1 and
# tools\update_segments_word_level_test.ps1 do, with the latter's Track Changes)
# and a stand-in AI server on 127.0.0.1 (a free port, taken when the test
# starts). Invented Acme text only. No Studio is started and no real AI request
# is made.
#
#   A  the rules: the number a segment goes under, what opens the request, what
#      a reply amounts to (no verdict, clean, writable, no correction, not
#      writable), the finding in words; the termbase section is the same text a
#      full run sends
#   B  the window under its own title and help page, FuzzyFixer's unchanged;
#      a finding with a correction, a finding without, a very long finding
#   C  the shortcut and the action in the plugin manifest
#   D  what stops it before anything is read or sent: no segment, a locked
#      segment, no translation, a batch that is running, no AI settings, no
#      profile, no API key; a document whose languages cannot be read
#   E  the run, against the stand-in AI: what is sent (prompt, bank, terms,
#      neighbours, never the document; the same system prompt for two
#      segments); no issue; an issue with a correction; a correction that drops
#      a tag; an issue without a correction; a correction that changes nothing;
#      an empty reply, a garbled one, a verdict for another segment; an AI
#      error; an answer that never comes and a window that is closed meanwhile;
#      and after each failure the next run works
#   F  the write: lands as Draft, the target's own characters kept; a segment
#      that changed meanwhile, was locked meanwhile; Studio declines, throws;
#      the status cannot be set; a correction that changes nothing visible
#   G  run and write together: tags kept, a tag only the target has among
#      them; Track Changes on (recorded word by word)
#   H  no proofreading report is written, nothing is kept between uses
#   Q  QuickLauncher: a prompt tagged for Trados hides its untagged twin
#
#   powershell -NoProfile -File tools\proofread_active_segment_test.ps1
#   powershell -NoProfile -File tools\proofread_active_segment_test.ps1 -Dll <Supervertaler.Trados.dll>
# A Studio 2024 / 2022 build is 32-bit: the script starts the 32-bit PowerShell
# for it by itself.
#
# What it cannot reach: the key press and the menu item in Studio, reading the
# active segment, its languages, its project, its prompt and its terms from a
# live document (ProofreadSegmentPrepare past the first checks), the message
# boxes, Studio's own ProcessSegmentPair and Track Changes, undo, and a real
# AI's answer.
param(
    [string]$Dll = (Join-Path (Split-Path -Parent $PSScriptRoot) "src\Supervertaler.Trados\bin\Studio19\Release\Supervertaler.Trados.dll"),
    [string]$Root = (Join-Path $env:TEMP "sv-proofread-active-segment-test"),
    [switch]$Child
)
$ErrorActionPreference = 'Stop'

$Root = [IO.Path]::GetFullPath($Root)
if ($Root -like 'D:\Supervertaler*' -or $Root.Length -lt 10) { throw "Refusing to use $Root as the test root." }
$Dll = [IO.Path]::GetFullPath($Dll)
if (-not (Test-Path -LiteralPath $Dll)) { throw "No plugin build at $Dll" }

if (-not $Child) {
    # Parent: run the cases in a child process of the right bitness, and check
    # that no real Supervertaler file changed meanwhile.
    if (Test-Path -LiteralPath $Root) { Remove-Item -LiteralPath $Root -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $Root | Out-Null

    $protected = @(
        'D:\Supervertaler\settings\api-keys.json',
        'D:\Supervertaler\trados\logs\diagnostic.log',
        'D:\Supervertaler\trados\settings\settings.json',
        'D:\Supervertaler\trados\settings\voice_commands.json',
        'D:\Supervertaler\trados\reports\last-report.json',
        (Join-Path $env:APPDATA 'Supervertaler\api-keys.locked.json'),
        (Join-Path $env:APPDATA 'Supervertaler\config.json'),
        'D:\Supervertaler\resources\supervertaler.db'
    )
    $lad = Join-Path $env:LOCALAPPDATA 'Supervertaler.Trados'
    if (Test-Path -LiteralPath $lad) { $protected += @(Get-ChildItem -LiteralPath $lad -File | ForEach-Object { $_.FullName }) }
    function Snapshot {
        $list = @($protected | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { $i = Get-Item -LiteralPath $_; "$($i.FullName)|$($i.Length)|$($i.LastWriteTimeUtc.Ticks)" })
        # The real reports folder: no file may appear in it.
        $reports = 'D:\Supervertaler\trados\reports'
        if (Test-Path -LiteralPath $reports) { $list += "reports|" + @(Get-ChildItem -LiteralPath $reports -File).Count }
        $list
    }
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
    if ((Compare-Object $before $after)) { Write-Host "FAIL  a real Supervertaler file changed during the test (a running Studio writes to them too: close it and run again)"; $failed = $true }
    else { Write-Host "PASS  real files unchanged ($($before.Count) checked: size and modified time)" }
    Remove-Item -LiteralPath $Root -Recurse -Force -ErrorAction SilentlyContinue
    if ($failed) { Write-Host "RESULT: FAILED"; exit 1 }
    Write-Host "RESULT: all passed"
    exit 0
}

# -- Child: the harness -------------------------------------------------------
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
public static class ProofreadSegmentResolver
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
[ProofreadSegmentResolver]::Register(@((Split-Path $Dll), $studio))
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Web.Extensions
$plugin = [Reflection.Assembly]::LoadFrom($Dll)
$dataRoot = [string](Join-Path $Root 'data')
# The test's own data folder, before anything can log: never the real one.
$plugin.GetType("Supervertaler.Core.SupervertalerPaths").GetMethod("Set").Invoke($null, @($dataRoot))
$rlf = $plugin.GetType("Supervertaler.Core.ApiKeyStore").GetMethod("RedirectLockedFile", [Reflection.BindingFlags]"Static,NonPublic")
if ($rlf) { [void]$rlf.Invoke($null, [object[]]@([string](Join-Path $dataRoot "api-keys.locked.json"))) }
$logType = $plugin.GetType("Supervertaler.Trados.Core.DiagnosticLog", $true)
$logPath = [string]$logType.GetProperty("LogFilePath").GetValue($null)
if (-not $logPath.StartsWith($Root, [StringComparison]::OrdinalIgnoreCase)) { throw "The diagnostic log would be written to $logPath, outside the test root." }

# The stand-in document: IStudioDocument generated from this Studio's own
# interface, every member throwing except the ones these paths use.
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
$special = @('ProcessSegmentPair', 'GetParentParagraphUnit', 'GetSegmentPairsFromParagraph', 'UpdateSegmentPairProperties',
             'AddCommentOnSegment', 'ChangeTranslationOriginOnSegment')
$specialProps = @('ActiveSegmentPair', 'SegmentPairs')
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
using System.Drawing;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Sdl.Core.Globalization;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Sdl.FileTypeSupport.Framework.NativeApi;
using Supervertaler.Core;
using Supervertaler.Core.Models;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Settings;

// What TrackingSuspension reaches through the document's InternalDocument: the
// editor's own Track Changes setting.
public sealed class FakeEditor
{
    public bool TrackChanges;
    public bool Protect = true;
    public int TurnOffs, Reverts;
}

// What TrackingSuspension.EditingUser reaches through the document's _sideBySideView.
public sealed class FakeView
{
    public string DocumentEditingUser { get; set; }
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
        internal void TurnOffTracking()
        {
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

// One paragraph of segments, behaving as Studio's editor does around a write:
// ProcessSegmentPair hands the edit a copy of the pair and then applies it.
// With Track Changes on it records ANY edit as the whole segment replaced.
public partial class FakeDocument
{
    public const string Me = "Acme Translator";
    public readonly FakeEditor Editor = new FakeEditor();
    internal object InternalDocument { get { return Editor; } }
    private readonly FakeView _sideBySideView = new FakeView { DocumentEditingUser = Me };
    public string User { get { return _sideBySideView.DocumentEditingUser; } }

    public IDocumentItemFactory F;
    public IPropertiesFactory PF;
    public List<ISegmentPair> Pairs = new List<ISegmentPair>();
    public IParagraphUnit Pu;
    public bool ThrowOnApply, DeclineAll, StatusThrows;
    public int Edits, Applied, StatusUpdates, WholeSegment, Listed;
    public bool? TrackingAtApply;

    ISegmentPair LiveOf(ISegmentPair pair) { return Pairs.First(p => p.Properties.Id.Id == pair.Properties.Id.Id); }
    void FakeProcessSegmentPair(ISegmentPair pair, string undoName, Action<ISegmentPair, CancelEventArgs> edit)
    {
        TrackingAtApply = null;
        var live = LiveOf(pair);
        var copy = F.CreateSegmentPair((ISegment)live.Source.Clone(), (ISegment)live.Target.Clone());
        var cancel = new CancelEventArgs();
        Edits++;
        edit(copy, cancel);
        if (cancel.Cancel) return;
        TrackingAtApply = Editor.TrackChanges;
        if (ThrowOnApply) throw new InvalidOperationException("fake: Studio could not apply the edit");
        if (DeclineAll) return;
        Applied++;
        if (Editor.TrackChanges)
        {
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
    void FakeUpdateSegmentPairProperties(ISegmentPair pair, ISegmentPairProperties props)
    {
        if (StatusThrows) throw new InvalidOperationException("fake: the status cannot be set");
        LiveOf(pair).Properties.ConfirmationLevel = props.ConfirmationLevel;
        StatusUpdates++;
    }
    void FakeChangeTranslationOriginOnSegment(object pair, object origin) { }
    void FakeAddCommentOnSegment(object pair, object text, object severity) { }
    ISegmentPair FakeActiveSegmentPair { get { return Pairs.Count > 0 ? Pairs[0] : null; } }
    IEnumerable<ISegmentPair> FakeSegmentPairs { get { return Walk(); } }
    IEnumerable<ISegmentPair> Walk() { foreach (var p in Pairs) { Listed++; yield return p; } }
}

public static class ProofreadSegmentTest
{
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    const string Me = FakeDocument.Me;
    static int Pass, Fail;
    static Assembly P;
    static Type Rules, View, DlgType, JobType, Fixer, Prompt, Correction;
    static IDocumentItemFactory F;
    static IPropertiesFactory PF;
    static string Root, Title, HelpTopic;

    static void Check(bool ok, string what)
    {
        if (ok) Pass++; else Fail++;
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what);
    }
    static string Flat(string s) { return (s ?? "(null)").Replace("\r", "").Replace("\n", " | "); }
    static object Call(Type t, string name, params object[] args) { return t.GetMethod(name, Any).Invoke(null, args); }
    static void Set(object o, string field, object value) { o.GetType().GetField(field, Any).SetValue(o, value); }
    static object Get(object o, string field) { return o.GetType().GetField(field, Any).GetValue(o); }

    // ---- real segments -------------------------------------------------------

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
    static ISegmentPair Pair(string id, IAbstractMarkupData[] source, IAbstractMarkupData[] target, ConfirmationLevel level)
    {
        var props = F.CreateSegmentPairProperties();
        props.Id = new SegmentId(id);
        props.ConfirmationLevel = level;
        var s = F.CreateSegment(props);
        foreach (var i in source) s.Add(i);
        var t = F.CreateSegment(props);
        foreach (var i in target) t.Add(i);
        return F.CreateSegmentPair(s, t);
    }
    static FakeDocument Doc()
    {
        var pu = F.CreateParagraphUnit(LockTypeFlags.Unlocked);
        pu.Properties.ParagraphUnitId = new ParagraphUnitId("pu-1");
        return new FakeDocument { F = F, PF = PF, Pu = pu };
    }
    const string Before = "De Acme-boiler heeft een tank.";
    const string After = "Daarna sluit de kraan.";
    // Segments 11, 12 and 13; 12 is the one proofread, a confirmed translation.
    static FakeDocument Three(IAbstractMarkupData[] source, IAbstractMarkupData[] target)
    {
        var d = Doc();
        d.Pairs.Add(Pair("11", new IAbstractMarkupData[] { T("The Acme heater has a tank.") }, new IAbstractMarkupData[] { T(Before) }, ConfirmationLevel.Translated));
        d.Pairs.Add(Pair("12", source, target, ConfirmationLevel.Translated));
        d.Pairs.Add(Pair("13", new IAbstractMarkupData[] { T("The tap then closes.") }, new IAbstractMarkupData[] { T(After) }, ConfirmationLevel.Translated));
        return d;
    }
    static FakeDocument Three(string source, string target)
    {
        return Three(new IAbstractMarkupData[] { T(source) },
            string.IsNullOrEmpty(target) ? new IAbstractMarkupData[0] : new IAbstractMarkupData[] { T(target) });
    }
    static ISegmentPair Seg(FakeDocument d) { return d.Pairs.First(p => p.Properties.Id.Id == "12"); }
    static ISegment Tgt(FakeDocument d, string id = "12") { return d.Pairs.First(p => p.Properties.Id.Id == id).Target; }

    // The target's markup, revisions shown: {-deleted-} {+inserted+}, <gN>..</gN>, <xN/>.
    static string Mk(IAbstractMarkupDataContainer c) { var sb = new StringBuilder(); MkInto(c, sb); return sb.ToString(); }
    static void MkInto(IAbstractMarkupDataContainer c, StringBuilder sb)
    {
        foreach (var item in c)
        {
            if (item is IText) sb.Append(((IText)item).Properties.Text);
            else if (item is IRevisionMarker)
            {
                var r = (IRevisionMarker)item;
                bool ins = r.Properties.RevisionType == RevisionType.Insert;
                sb.Append(ins ? "{+" : "{-"); MkInto(r, sb); sb.Append(ins ? "+}" : "-}");
            }
            else if (item is ITagPair)
            {
                var p = (ITagPair)item;
                sb.Append("<g").Append(p.StartTagProperties.TagId.Id).Append('>'); MkInto(p, sb); sb.Append("</g").Append(p.StartTagProperties.TagId.Id).Append('>');
            }
            else if (item is IPlaceholderTag) sb.Append("<x").Append(((IPlaceholderTag)item).Properties.TagId.Id).Append("/>");
            else sb.Append("[" + item.GetType().Name + "]");
        }
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
    static string Accept(IAbstractMarkupDataContainer c) { return (string)P.GetType("Supervertaler.Trados.Core.TrackedTargetMerge", true).GetMethod("Project", Any).Invoke(null, new object[] { c, true }); }
    static string Reject(IAbstractMarkupDataContainer c) { return (string)P.GetType("Supervertaler.Trados.Core.TrackedTargetMerge", true).GetMethod("Project", Any).Invoke(null, new object[] { c, false }); }
    // Both sides as the proofreader is shown them.
    static string[] Proof(ISegmentPair pair)
    {
        var args = new object[] { pair.Source, pair.Target, null, null };
        P.GetType("Supervertaler.Trados.Core.SegmentTagHandler", true).GetMethod("ToProofreadText", Any).Invoke(null, args);
        return new[] { (string)args[2], (string)args[3] };
    }

    // ---- the stand-in AI -----------------------------------------------------

    sealed class Request { public string System, User; }
    static HttpListener _listener;
    static string _endpoint;
    static readonly List<Request> Requests = new List<Request>();
    static volatile string _answer;          // the AI's whole reply
    static volatile bool _fail;              // HTTP 400 with an error message
    static ManualResetEvent _hold;           // set: the answer is held back until it is released

    static void StartServer()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        _listener = new HttpListener();
        _listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
        _listener.Start();
        _endpoint = "http://127.0.0.1:" + port + "/v1";
        var t = new Thread(Serve); t.IsBackground = true; t.Start();
    }
    static string Content(object content)
    {
        var s = content as string;
        if (s != null) return s;
        return string.Concat(((object[])content).Cast<Dictionary<string, object>>().Select(x => x.ContainsKey("text") ? (string)x["text"] : ""));
    }
    static void Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); } catch { return; }
            ThreadPool.QueueUserWorkItem(delegate { Answer(ctx); });
        }
    }
    static void Answer(HttpListenerContext ctx)
    {
        var json = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        try
        {
            string body;
            using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = r.ReadToEnd();
            var req = (Dictionary<string, object>)json.DeserializeObject(body);
            var seen = new Request { System = "", User = "" };
            foreach (Dictionary<string, object> m in (object[])req["messages"])
            {
                if ((string)m["role"] == "user") seen.User = Content(m["content"]);
                else if ((string)m["role"] == "system") seen.System += Content(m["content"]);
            }
            lock (Requests) Requests.Add(seen);
            var hold = _hold;
            if (hold != null) hold.WaitOne(20000);

            byte[] bytes;
            if (_fail)
            {
                ctx.Response.StatusCode = 400;
                bytes = Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"stand-in failure: the model is not available\",\"type\":\"invalid_request_error\"}}");
            }
            else
            {
                var reply = new Dictionary<string, object> {
                    { "choices", new object[] { new Dictionary<string, object> {
                        { "message", new Dictionary<string, object> { { "role", "assistant" }, { "content", _answer } } },
                        { "finish_reason", "stop" } } } },
                    { "usage", new Dictionary<string, object> { { "prompt_tokens", body.Length / 3 }, { "completion_tokens", 5 } } } };
                bytes = Encoding.UTF8.GetBytes(json.Serialize(reply));
            }
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }
        catch { try { ctx.Response.Abort(); } catch { } }
    }
    static int RequestCount() { lock (Requests) return Requests.Count; }
    static Request LastRequest() { lock (Requests) return Requests[Requests.Count - 1]; }

    static AiSettings Ai()
    {
        var ai = new AiSettings();
        ai.SelectedProvider = "custom_openai";
        ai.CustomOpenAiProfiles = new List<CustomOpenAiProfile>();
        var p = new CustomOpenAiProfile();
        p.Name = "fake"; p.Endpoint = _endpoint; p.Model = "fake-unlisted-model"; p.ApiKey = "x";
        ai.CustomOpenAiProfiles.Add(p);
        ai.SelectedCustomProfileName = "fake";
        // On, to show that the document is not sent whatever this says.
        ai.IncludeDocumentContext = true;
        ai.LogPromptsToReports = false;
        return ai;
    }

    // ---- the window and a run ------------------------------------------------

    static void Pump(int ms) { var sw = Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(5); } }
    static Form Open(Action<object> onStart, string note)
    {
        var p = Expression.Parameter(DlgType, "d");
        var start = Expression.Lambda(typeof(Action<>).MakeGenericType(DlgType),
            Expression.Invoke(Expression.Constant(onStart), Expression.Convert(p, typeof(object))), p).Compile();
        return (Form)Activator.CreateInstance(DlgType, new object[] { Title, HelpTopic, "segment 12", note, start });
    }
    static object Field(Form f, string name) { return DlgType.GetField(name, Any).GetValue(f); }
    static void Say(Form f, string name, params object[] args) { DlgType.GetMethod(name, Any).Invoke(f, args); }
    static CancellationToken TokenOf(Form f) { return (CancellationToken)DlgType.GetProperty("Token").GetValue(f, null); }
    static bool SourceDisposed(Form f)
    {
        var cts = (CancellationTokenSource)Field(f, "_cts");
        try { var unused = cts.Token; return false; } catch (ObjectDisposedException) { return true; }
    }
    // Whether the header's text fits the label it is in (three lines).
    static bool HeaderFits(Form f)
    {
        var header = (Label)Field(f, "_header");
        var need = TextRenderer.MeasureText(header.Text, header.Font, new Size(header.Width, 10000), TextFormatFlags.WordBreak);
        return need.Height <= header.Height;
    }
    static object Changes(string oldText, string newText) { return Call(Fixer, "Changes", oldText, newText); }

    const string OwnPrompt = "Proofread Acme manuals strictly: flag every wrong verb.";
    const string Bank = "# SUPERMEMORY\nAcme house rules: a valve is a klep.";
    const string Source = "The Acme valve closes at 80\u00A0bar.";
    const string Target = "De Acme-klep gaat open bij 80\u00A0bar.";
    const string Fixed = "De Acme-klep gaat dicht bij 80\u00A0bar.";
    const string IssueLine = "\"gaat open\" says the valve opens; the source says it closes.";
    const string SuggestionLine = "Write \"gaat dicht\".";
    // As a model writes it: the non-breaking space comes back as an ordinary one.
    static string IssueReply(string corrected)
    {
        return "[SEGMENT 0012] ISSUE\nIssue: " + IssueLine + "\nSuggestion: " + SuggestionLine
            + (corrected == null ? "" : "\nCorrected: " + corrected);
    }

    static object BankTask(string block)
    {
        var resultType = P.GetType("Supervertaler.Trados.Core.TranslationBankContext+Result", true);
        var result = Activator.CreateInstance(resultType, true);
        Set(result, "Block", block);
        return typeof(Task).GetMethod("FromResult").MakeGenericMethod(resultType).Invoke(null, new[] { result });
    }
    static object Job(string source, string target, string lead, int number = 12, bool bank = true)
    {
        var seg = new BatchSegment { Index = number - 1, Number = number.ToString(), SourceText = source, ExistingTarget = target };
        var job = Activator.CreateInstance(JobType, true);
        Set(job, "Ai", Ai()); Set(job, "SourceLang", "English"); Set(job, "TargetLang", "Dutch");
        Set(job, "Segment", seg); Set(job, "Instructions", OwnPrompt); Set(job, "Lead", lead);
        if (bank) Set(job, "Bank", BankTask(Bank));
        return job;
    }
    static object JobFor(FakeDocument d, string lead = null)
    {
        var shown = Proof(Seg(d));
        return Job(shown[0], shown[1], lead ?? (string)Call(Rules, "Lead", 12, null, null));
    }

    sealed class RunResult
    {
        public string Correction; public bool Handed;
        public string Header, Body, Discard, StatusHeader, StatusBody;
        public bool CanWrite, TimedOut, Threw, Disposed, Cancelled, Fits, Struck, Underlined;
        public long Ms;
    }

    // One use, as HandleProofreadActiveSegment makes it but for the modal loop:
    // the window on screen, the work on a pool thread, the window disposed at
    // the end. closeAfterRequest: close the window as soon as the AI has the
    // request. With _hold set and no close, the status is read while the AI
    // holds its answer, and the answer is then released.
    static RunResult Run(object job, bool closeAfterRequest = false, string struck = null, string underlined = null)
    {
        var r = new RunResult();
        Action<string> done = c => { r.Correction = c; r.Handed = true; };
        Task task = null;
        int requestsBefore = RequestCount();
        var f = Open(d =>
        {
            task = Task.Run(() => (Task)View.GetMethod("ProofreadSegmentRun", Any).Invoke(null, new object[] { job, d, done }));
        }, null);
        var sw = Stopwatch.StartNew();
        f.Show();
        bool closed = false, seen = false;
        while (sw.ElapsedMilliseconds < 30000)
        {
            Application.DoEvents(); Thread.Sleep(5);
            if (!seen && RequestCount() > requestsBefore)
            {
                seen = true;
                Pump(50);
                r.StatusHeader = ((Label)Field(f, "_header")).Text;
                r.StatusBody = ((RichTextBox)Field(f, "_text")).Text;
                if (closeAfterRequest) { f.Close(); closed = true; }
                else { var hold = _hold; if (hold != null) hold.Set(); }
            }
            if (task != null && task.IsCompleted) break;
        }
        r.Ms = sw.ElapsedMilliseconds;
        r.TimedOut = task == null || !task.IsCompleted;
        r.Threw = task != null && task.IsFaulted;
        Pump(150);   // what the work posted to the window
        if (!closed)
        {
            var text = (RichTextBox)Field(f, "_text");
            r.Header = ((Label)Field(f, "_header")).Text;
            r.Body = text.Text;
            r.CanWrite = ((Button)Field(f, "_write")).Enabled;
            r.Discard = ((Button)Field(f, "_discard")).Text;
            r.Fits = HeaderFits(f);
            // The last occurrence: the changes come after the finding, which may hold the same words.
            if (struck != null && text.Text.LastIndexOf(struck) >= 0)
            {
                text.Select(text.Text.LastIndexOf(struck), struck.Length);
                r.Struck = text.SelectionFont != null && text.SelectionFont.Strikeout;
            }
            if (underlined != null && text.Text.LastIndexOf(underlined) >= 0)
            {
                text.Select(text.Text.LastIndexOf(underlined), underlined.Length);
                r.Underlined = text.SelectionFont != null && text.SelectionFont.Underline;
            }
            f.Close();
        }
        f.Dispose();
        r.Disposed = SourceDisposed(f);
        r.Cancelled = TokenOf(f).IsCancellationRequested;
        return r;
    }

    // ---- refusals and the write ----------------------------------------------

    static string Refusal(ISegmentPair pair, bool batchRunning, AiSettings ai)
    {
        return (string)View.GetMethod("ProofreadSegmentRefusal", Any).Invoke(null, new object[] { pair, batchRunning, ai, null });
    }
    sealed class WriteOutcome { public string Why, Note; }
    static WriteOutcome Write(FakeDocument d, object job, string correction)
    {
        var args = new object[] { d, Seg(d), job, correction, null };
        var o = new WriteOutcome();
        o.Why = (string)View.GetMethod("ProofreadSegmentWrite", Any).Invoke(null, args);
        o.Note = (string)args[4];
        return o;
    }
    static string Judge(Supervertaler.Trados.Models.ProofreadingIssue issue, bool noVerdict, out string why)
    {
        var args = new object[] { issue, noVerdict, null };
        var v = Rules.GetMethod("Judge", Any).Invoke(null, args);
        why = (string)args[2];
        return v.ToString();
    }
    static Supervertaler.Trados.Models.ProofreadingIssue Issue(bool ok, string target, string corrected, string source = Source)
    {
        return new Supervertaler.Trados.Models.ProofreadingIssue
        {
            IsOk = ok, SourceText = source, TargetText = target, CorrectedTarget = corrected,
            IssueDescription = ok ? null : IssueLine, Suggestion = ok ? null : SuggestionLine
        };
    }
    static bool TrackingRestored(FakeDocument d) { return d.Editor.TrackChanges && d.Editor.Protect && d.Editor.TurnOffs == d.Editor.Reverts; }

    static PromptTemplate Prompt_(string name, string app, string file, string label = null)
    {
        var p = new PromptTemplate();
        p.Name = name; p.App = app; p.FilePath = file; p.QuickLauncherLabel = label;
        return p;
    }
    static string Files(IEnumerable list) { return string.Join(",", list.Cast<PromptTemplate>().Select(p => p.FilePath)); }

    public static int Run(string pluginPath, string root, string manifestPath)
    {
        P = Assembly.LoadFrom(pluginPath);
        Root = root;
        Rules = P.GetType("Supervertaler.Trados.Core.SegmentProofread", true);
        Fixer = P.GetType("Supervertaler.Trados.Core.FuzzyFixer", true);
        Prompt = P.GetType("Supervertaler.Trados.Core.ProofreadingPrompt", true);
        Correction = P.GetType("Supervertaler.Trados.Core.ProofreadCorrection", true);
        View = P.GetType("Supervertaler.Trados.AiAssistantViewPart", true);
        DlgType = P.GetType("Supervertaler.Trados.Controls.FuzzyFixPreviewDialog", true);
        JobType = View.GetNestedType("ProofreadSegmentJob", Any);
        Title = (string)Rules.GetField("Title", Any).GetValue(null);
        HelpTopic = (string)P.GetType("Supervertaler.Trados.Core.HelpSystem+Topics", true).GetField("AiProofreader", Any).GetValue(null);
        F = (IDocumentItemFactory)Activator.CreateInstance(Type.GetType(
            "Sdl.FileTypeSupport.Framework.Bilingual.DocumentItemFactory, Sdl.FileTypeSupport.Framework.Implementation", true));
        PF = (IPropertiesFactory)Activator.CreateInstance(Type.GetType(
            "Sdl.FileTypeSupport.Framework.Native.PropertiesFactory, Sdl.FileTypeSupport.Framework.Implementation", true));
        F.PropertiesFactory = PF;
        Check(SupervertalerPaths.Root.StartsWith(root, StringComparison.OrdinalIgnoreCase), "the data folder is the test's own");
        // Settings pre-seeded: Current would otherwise load the real ones.
        var svc = P.GetType("Supervertaler.Trados.Settings.SettingsService", true);
        svc.GetField("_current", Any).SetValue(null, Activator.CreateInstance(P.GetType("Supervertaler.Trados.Settings.TermLensSettings", true)));

        var reportWriter = P.GetType("Supervertaler.Trados.Core.ProofreadingReportWriter", true);
        var reportsDir = (string)reportWriter.GetProperty("ReportsDir", Any).GetValue(null, null);
        var statePath = (string)reportWriter.GetProperty("StatePath", Any).GetValue(null, null);
        Check(reportsDir.StartsWith(root, StringComparison.OrdinalIgnoreCase), "a proofreading report would be written under the test's own folder");

        Console.WriteLine("A  the rules");
        Check(Title == "Proofread active segment", "A the name: " + Title);
        Check((int)Call(Rules, "NumberFor", "12") == 12 && (int)Call(Rules, "NumberFor", " 7 ") == 7, "A the segment goes under the number Studio shows");
        Check((int)Call(Rules, "NumberFor", "331 a") == 1 && (int)Call(Rules, "NumberFor", new object[] { null }) == 1 && (int)Call(Rules, "NumberFor", "0") == 1,
            "A failure: a number that is not a whole number (a split segment's \"331 a\"), none, or 0 -> 1");

        var nType = Fixer.GetNestedType("Neighbour", Any);
        Func<string, string, string, object> N = (num, s, t) => { var n = Activator.CreateInstance(nType, true); Set(n, "Number", num); Set(n, "Source", s); Set(n, "Target", t); return n; };
        Func<object[], IList> L = items => { var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(nType)); foreach (var i in items) list.Add(i); return list; };
        var lines = (string)Call(Fixer, "NeighbourLines",
            L(new[] { N("10", "The Acme heater has a <t1>tank</t1>.", "De Acme-boiler heeft een tank."), N("11", "It holds 80 litres.", "") }),
            L(new[] { N("13", "The tap then closes.", null) }));
        Check(lines.StartsWith("Before this segment:") && lines.Contains("segment 10 source: The Acme heater has a tank.")
            && lines.Contains("translation: De Acme-boiler heeft een tank.") && lines.Contains("After this segment:") && lines.Contains("segment 13 source:"),
            "A the neighbours, as FuzzyFixer lists them, without its heading");
        Check(!lines.Contains("repair") && !lines.Contains("##"), "A and without the sentence written for a repair");
        Check(Call(Fixer, "NeighbourLines", L(new object[0]), L(new object[0])) == null && Call(Fixer, "NeighbourLines", null, null) == null,
            "A a one-segment document: no neighbours at all");
        var block = (string)Call(Fixer, "NeighboursBlock",
            L(new[] { N("10", "The Acme heater has a <t1>tank</t1>.", "De Acme-boiler heeft een tank."), N("11", "It holds 80 litres.", "") }),
            L(new[] { N("13", "The tap then closes.", null) }));
        Check(block.EndsWith(lines) && block.StartsWith("## Surrounding segments (context only)\n") && block.Contains("context for the repair only"),
            "A FuzzyFixer's own neighbours section still reads as it did");

        var terms = new List<TermEntry>();
        var term = new TermEntry(); term.SourceTerm = "valve"; term.TargetTerm = "klep"; terms.Add(term);
        var term2 = new TermEntry(); term2.SourceTerm = "tap"; term2.TargetTerm = "tapkraan"; term2.Forbidden = true; terms.Add(term2);
        var termBlock = (string)Call(Prompt, "TermbaseBlock", terms, true);
        Check(termBlock.StartsWith("# TERMBASE") && termBlock.Contains("- valve") && termBlock.Contains("klep") && termBlock.Contains("DO NOT USE: tapkraan"),
            "A the termbase section on its own");
        Check(Call(Prompt, "TermbaseBlock", new List<TermEntry>(), true) == null && Call(Prompt, "TermbaseBlock", null, true) == null,
            "A no terms: no termbase section");
        var sysWith = (string)Call(Prompt, "BuildSystemPrompt", "English", "Dutch", terms, OwnPrompt, null, true, Bank, false);
        var sysWithout = (string)Call(Prompt, "BuildSystemPrompt", "English", "Dutch", new List<TermEntry>(), OwnPrompt, null, true, Bank, false);
        string NL = Environment.NewLine;
        Check(sysWith.Contains(NL + NL + termBlock + NL) && sysWith.Replace(NL + NL + termBlock + NL, "") == sysWithout,
            "A it is, to the byte, the section a full run has in its system prompt: that prompt minus the section is the prompt without terms");
        Check(sysWithout.Contains(OwnPrompt) && sysWithout.Contains("Acme house rules") && !sysWithout.Contains("# TERMBASE") && !sysWithout.Contains("FULL document"),
            "A the system prompt for one segment: the proofreading prompt and the bank; no terms, no document");

        var lead = (string)Call(Rules, "Lead", 12, termBlock, lines);
        Check(lead.StartsWith("## One segment (this request)") && lead.Contains("[SEGMENT 0012]") && lead.Contains("no # DOCUMENT CONTENT section"),
            "A the lead says it is one segment, by its number, and that there is no document");
        Check(lead.IndexOf("# TERMBASE") > 0 && lead.IndexOf("# TERMBASE") < lead.IndexOf("## Surrounding segments (context only)")
            && lead.EndsWith(lines) && lead.Contains("give no verdict for them"),
            "A then the terms, then the neighbours, which are not to be reviewed");
        var bare = (string)Call(Rules, "Lead", 3, null, " ");
        Check(bare.Contains("[SEGMENT 0003]") && !bare.Contains("# TERMBASE") && !bare.Contains("Surrounding segments"),
            "A no terms and no neighbours: the first paragraph alone");
        Check(!lead.Contains("repair"), "A nothing of FuzzyFixer's wording in it");

        string why;
        Check(Judge(null, false, out why) == "NoVerdict", "A failure: nothing came back -> no verdict");
        Check(Judge(Issue(true, Target, null), true, out why) == "NoVerdict", "A failure: OK only because no verdict could be read -> no verdict, not \"no issues\"");
        Check(Judge(Issue(true, Target, null), false, out why) == "Clean" && why == null, "A the AI says OK -> clean");
        Check(Judge(Issue(false, Target, Fixed), false, out why) == "Writable" && why == null, "A an issue with a correction -> writable");
        Check(Judge(Issue(false, Target, null), false, out why) == "NoCorrection" && why != null && why.Contains("no corrected translation"),
            "A failure: an issue without a correction -> nothing to write (" + why + ")");
        const string TagSrc = "<t1>The valve</t1> closes.";
        Check(Judge(Issue(false, "<t1>De klep</t1> gaat open.", "De klep gaat dicht.", TagSrc), false, out why) == "NotWritable"
            && why == "the correction is missing the source's <t1> tag."
            && why == (string)Call(Correction, "Problem", TagSrc, "<t1>De klep</t1> gaat open.", "De klep gaat dicht."),
            "A failure: a correction that drops a tag -> not writable, for the reason Apply on the Reports tab gives (" + why + ")");
        Check(Judge(Issue(false, "<t1>De klep</t1> gaat open.", "<t1>De klep</t1> gaat dicht.", TagSrc), false, out why) == "Writable",
            "A the same correction with its tag -> writable");
        Check(Judge(Issue(false, "De klep\ngaat open.", "De klep gaat dicht."), false, out why) == "NotWritable" && why.Contains("line breaks"),
            "A failure: a correction that loses a line break -> not writable (" + why + ")");

        var words = (string)Call(Rules, "Findings", Issue(false, Target, Fixed));
        Check(words == "Issue: " + IssueLine + "\nSuggestion: " + SuggestionLine, "A the finding in words: the issue and the suggestion, a line each");
        var withEvidence = Issue(false, Target, Fixed); withEvidence.Evidence = "Segment 11";
        Check(((string)Call(Rules, "Findings", withEvidence)).Contains("\nEvidence: Segment 11\n"), "A with its evidence when it has any");
        var silent = Issue(false, Target, Fixed); silent.IssueDescription = null; silent.Suggestion = " ";
        Check((string)Call(Rules, "Findings", silent) == "Issue: (the AI did not describe it)", "A failure: an issue the AI did not describe is still shown as one");
        var notWritable = (string)Call(Rules, "NotWritableText", Issue(false, "<t1>De klep</t1> gaat open.", "De klep gaat dicht."), "the correction is missing the source's <t1> tag.");
        Check(notWritable.Contains("Issue: ") && notWritable.Contains("Nothing can be written: the correction is missing the source's <t1> tag.")
            && notWritable.EndsWith("The AI's corrected translation was:\nDe klep gaat dicht."),
            "A not writable: the finding, why, and the AI's correction to copy by hand");
        Check(((string)Call(Rules, "WritableText", Issue(false, Target, Fixed))).EndsWith("(red is removed, green is added):"), "A writable: the finding, then the line that introduces the changes");
        Check(((string)Call(Rules, "WritableText", Issue(false, "<t1>De klep</t1> sluit.", "De <t1>klep</t1> sluit."))).Contains("changes only tags"),
            "A a correction that only moves a tag says so, since the changes shown are words");

        Console.WriteLine("B  the window");
        {
            int started = 0;
            var f = Open(d => started++, "This segment is already confirmed. Writing a correction sets it back to Draft.");
            var write = (Button)Field(f, "_write"); var text = (RichTextBox)Field(f, "_text"); var header = (Label)Field(f, "_header");
            var discard = (Button)Field(f, "_discard");
            var token = TokenOf(f);
            f.Show(); Pump(200);
            Check(started == 1, "B the work is started once, when the window is on screen");
            Check(f.Text.StartsWith("Proofread active segment") && f.Text.Contains("segment 12") && !f.Text.Contains("FuzzyFixer"), "B the title is its own (" + f.Text + ")");
            Check((string)Field(f, "_helpTopic") == "trados/ai-proofreader/", "B ? and F1 open the AI Proofreader help page (" + Field(f, "_helpTopic") + ")");
            Check(!write.Enabled && ((Label)Field(f, "_note")).Visible, "B while working nothing can be written; a confirmed segment is said to be confirmed");
            int headerHeight = header.Height;

            var t = new Thread(() => Say(f, "ShowReview", "Issue found by Acme Model. Enter writes the correction as a draft; Escape discards it.",
                (string)Call(Rules, "WritableText", Issue(false, Target, Fixed)), Changes(Target, Fixed)));
            t.Start(); t.Join(); Pump(200);
            Check(write.Enabled && f.AcceptButton == write && discard.Text == "&Discard", "B a finding with a correction: Write to target is what Enter presses, Alt+D discards");
            Check(text.Text.StartsWith("Issue: " + IssueLine) && text.Text.Contains("Suggestion: " + SuggestionLine), "B the finding is in the text box, in words");
            Check(text.Text.IndexOf("Corrected translation") > text.Text.IndexOf("Suggestion:") && text.Text.LastIndexOf("dicht") > text.Text.IndexOf("Corrected translation"),
                "B the correction follows under it");
            text.Select(text.Text.LastIndexOf("open"), 4);
            Check(text.SelectionFont != null && text.SelectionFont.Strikeout, "B the removed word is struck through");
            text.Select(text.Text.LastIndexOf("dicht"), 5);
            Check(text.SelectionFont != null && text.SelectionFont.Underline, "B the added word is underlined");
            text.Select(text.Text.IndexOf("Issue:"), 5);
            Check(text.SelectionFont != null && !text.SelectionFont.Strikeout && !text.SelectionFont.Underline, "B the finding itself is plain text");
            Check(HeaderFits(f), "B the header fits its three lines");

            // Several problems in one long Issue line: none of it may be cut off.
            var longIssue = Issue(false, Target, Fixed);
            var sbLong = new StringBuilder();
            for (int i = 1; i <= 60; i++) sbLong.Append("(").Append(i).Append(") Acme problem number ").Append(i).Append(" in this translation; ");
            longIssue.IssueDescription = sbLong.ToString() + "END-OF-ISSUE";
            Say(f, "ShowReview", "Issue found by Acme Model. Nothing can be written: the reason is below.",
                (string)Call(Rules, "NotWritableText", longIssue, "the AI gave no corrected translation for it."), null);
            Pump(100);
            Check(text.Text.Contains("(1) Acme problem number 1 ") && text.Text.Contains("(60) Acme problem number 60 ") && text.Text.Contains("END-OF-ISSUE")
                && text.Text.Contains("Nothing can be written: the AI gave no corrected translation"),
                "B a finding of " + longIssue.IssueDescription.Length + " characters is in the text box whole, with why nothing can be written");
            Check(text.ScrollBars == RichTextBoxScrollBars.Vertical && header.Height == headerHeight && HeaderFits(f) && write.Bottom <= f.ClientSize.Height,
                "B the box scrolls; the header and the buttons are where they were");
            Check(!write.Enabled && discard.Text == "Close", "B a finding with nothing to write: Write to target is disabled, the button left is Close");

            Say(f, "ShowReview", "No issues found in this segment.", "Checked on its own.", null); Pump(100);
            Check(!write.Enabled && header.Text == "No issues found in this segment." && text.Text == "Checked on its own.", "B no issue: said, nothing to write");
            f.Close(); Pump(100);
            Check(token.IsCancellationRequested, "B closing the window cancels the work");
            bool threw = false;
            try
            {
                t = new Thread(() => { try { Say(f, "ShowReview", "late", "late", Changes("a", "b")); } catch { threw = true; } });
                t.Start(); t.Join(); Pump(100);
            }
            catch { threw = true; }
            Check(!threw, "B failure: a finding that arrives after the window closed is dropped, without an error");
            f.Dispose();
            Check(SourceDisposed(f), "B disposing the window disposes its token source");
        }
        {
            var f = Open(d => { throw new InvalidOperationException("boom"); }, null);
            f.Show(); Pump(100);
            var body = ((RichTextBox)Field(f, "_text")).Text;
            Check(body.Contains("Proofread active segment could not start: boom") && !((Button)Field(f, "_write")).Enabled,
                "B failure: work that cannot even start is reported in the window under this feature's name (" + body + ")");
            f.Close(); f.Dispose();
        }
        {
            // FuzzyFixer's own constructor: as it was.
            var p = Expression.Parameter(DlgType, "d");
            var start = Expression.Lambda(typeof(Action<>).MakeGenericType(DlgType), Expression.Empty(), p).Compile();
            var f = (Form)Activator.CreateInstance(DlgType, new object[] { "Segment 12", null, start });
            Check(f.Text.StartsWith("FuzzyFixer") && (string)Field(f, "_helpTopic") == "trados/fuzzyfixer/",
                "B FuzzyFixer's window keeps its title and its help page (" + f.Text + ")");
            f.Dispose();
        }

        Console.WriteLine("C  shortcut and manifest");
        const int CtrlAltP = (int)(Keys.Control | Keys.Alt | Keys.P);
        var owners = new List<string>();
        Type[] types; try { types = P.GetTypes(); } catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(x => x != null).ToArray(); }
        foreach (var type in types)
            foreach (var a in CustomAttributeData.GetCustomAttributes(type))
                if (a.AttributeType.Name == "ShortcutAttribute")
                    foreach (var arg in a.ConstructorArguments)
                        if (Convert.ToInt32(arg.Value) == CtrlAltP) owners.Add(type.Name);
        Check(owners.Count == 1 && owners[0] == "ProofreadActiveSegmentAction",
            "C Ctrl+Alt+P belongs to ProofreadActiveSegmentAction and to no other Supervertaler action (" + string.Join(", ", owners) + ")");
        var actionType = P.GetType("Supervertaler.Trados.ProofreadActiveSegmentAction", true);
        var actionAttr = CustomAttributeData.GetCustomAttributes(actionType).First(a => a.AttributeType.Name == "ActionAttribute");
        var menuName = (string)actionAttr.NamedArguments.First(a => a.MemberName == "Name").TypedValue.Value;
        Check(menuName == "Proofread active segment", "C the menu text: " + menuName);
        // Studio registers actions and shortcuts from the manifest, not from the attributes.
        var manifest = File.ReadAllText(manifestPath, Encoding.Unicode);
        int at = manifest.IndexOf("<extension type=\"Supervertaler.Trados.ProofreadActiveSegmentAction,", StringComparison.Ordinal);
        var entry = at < 0 ? "" : manifest.Substring(at, manifest.IndexOf("</extension>", at, StringComparison.Ordinal) - at);
        Check(entry.Contains("Supervertaler_ProofreadActiveSegment") && entry.Contains(">" + CtrlAltP + "<") && entry.Contains("EditorDocumentContextMenuLocation")
            && entry.Contains("<property name=\"Name\">Proofread active segment</property>"),
            "C the manifest registers the action, its name, its place in the Editor's right-click menu and Ctrl+Alt+P (" + CtrlAltP + ")");
        Check(manifest.Split(new[] { ">" + CtrlAltP + "<" }, StringSplitOptions.None).Length == 2, "C and no other manifest entry claims that key");
        int fuzzyAt = manifest.IndexOf("<extension type=\"Supervertaler.Trados.FuzzyFixActiveSegmentAction,", StringComparison.Ordinal);
        Check(fuzzyAt >= 0 && fuzzyAt < at && manifest.IndexOf("<extension ", fuzzyAt + 10, StringComparison.Ordinal) == at, "C it is registered next to FuzzyFixer");

        Console.WriteLine("D  what stops it before anything is read or sent");
        StartServer();
        {
            var d = Three(Source, Target);
            Check(Refusal(null, false, Ai()) == "No active segment.", "D failure: no active segment");
            var lockedDoc = Three(Source, Target); Seg(lockedDoc).Properties.IsLocked = true;
            var locked = Refusal(Seg(lockedDoc), false, Ai());
            Check(locked != null && locked.Contains("locked"), "D failure: a locked segment is left alone (" + locked + ")");
            var empty = Refusal(Seg(Three(Source, "")), false, Ai());
            Check(empty != null && empty.Contains("nothing to proofread"), "D failure: no translation (" + empty + ")");
            var blank = Refusal(Seg(Three(Source, "   ")), false, Ai());
            Check(blank != null && blank.Contains("nothing to proofread"), "D failure: a translation of spaces alone");
            var tagOnly = Refusal(Seg(Three(new IAbstractMarkupData[] { T(Source) }, new IAbstractMarkupData[] { X("1") })), false, Ai());
            Check(tagOnly != null && tagOnly.Contains("nothing to proofread"), "D failure: a translation of a tag alone");
            var busy = Refusal(Seg(d), true, Ai());
            Check(busy != null && busy.Contains("batch translation is running"), "D failure: a batch is running");
            var none = Refusal(Seg(d), false, null);
            Check(none != null && none.Contains("AI settings not configured"), "D failure: no AI settings");
            var noProfile = new AiSettings { SelectedProvider = "custom_openai" };
            Check((Refusal(Seg(d), false, noProfile) ?? "").Contains("No custom OpenAI profile"), "D failure: no profile for the chosen provider");
            var noKey = Ai(); noKey.CustomOpenAiProfiles[0].ApiKey = "";
            var keyProblem = Refusal(Seg(d), false, noKey);
            Check(!string.IsNullOrWhiteSpace(keyProblem), "D failure: no API key (" + Flat(keyProblem) + ")");
            Check(Refusal(Seg(d), false, Ai()) == null, "D an unlocked, translated segment, no batch, a key: it starts");
            Check(RequestCount() == 0 && d.Edits == 0 && d.Listed == 0, "D none of these sent a request, wrote, or walked the document");

            // Past the refusals: what the stand-in document cannot give.
            var args = new object[] { d, Seg(d), Ai(), null };
            var job = View.GetMethod("ProofreadSegmentPrepare", Any).Invoke(null, args);
            Check(job == null && args[3] != null && ((string)args[3]).Contains("languages"),
                "D failure: a document whose languages cannot be read -> said, nothing sent (" + args[3] + ")");
            var emptyDoc = Three(Source, "");
            args = new object[] { emptyDoc, Seg(emptyDoc), Ai(), null };
            job = View.GetMethod("ProofreadSegmentPrepare", Any).Invoke(null, args);
            Check(job == null && ((string)args[3]).Contains("nothing to proofread"), "D failure: an empty target is caught there too");
            Check(RequestCount() == 0 && d.Edits == 0, "D still nothing sent or written");
        }

        Console.WriteLine("E  the run");
        {
            var instructions = (string)Call(Rules, "Lead", 12, termBlock, lines);

            _answer = IssueReply("De Acme-klep gaat dicht bij 80 bar."); _fail = false;
            _hold = new ManualResetEvent(false);
            int n0 = RequestCount();
            var r = Run(Job(Source, Target, instructions), false, "open", "dicht");
            _hold = null;
            Check(!r.TimedOut && !r.Threw && r.Handed && r.Correction == Fixed,
                "E an issue with a correction: the correction is handed over for writing, with the target's non-breaking space put back"
                + (r.Handed ? "" : " [" + r.Header + " | " + Flat(r.Body) + "]"));
            Check(r.StatusHeader != null && r.StatusHeader.Contains("fake-unlisted-model") && r.StatusHeader.Contains("proofread")
                && (r.StatusBody ?? "").Contains("De Acme-klep gaat open bij 80"),
                "E while waiting the window names the model and shows the translation (" + r.StatusHeader + ")");
            Check(r.CanWrite && r.Header.StartsWith("Issue found by fake-unlisted-model") && r.Header.Contains("Enter writes") && r.Fits,
                "E the header says an issue was found, by which model, and what Enter does; it fits (" + r.Header + ")");
            Check(r.Body.Contains("Issue: " + IssueLine) && r.Body.Contains("Suggestion: " + SuggestionLine) && r.Struck && r.Underlined && r.Discard == "&Discard",
                "E the window shows the issue and the suggestion in words, and the correction as removed and added words");
            Check(RequestCount() == n0 + 1, "E one AI request per use");
            var sent = LastRequest();
            Check(sent.System.Contains(OwnPrompt) && sent.System.Contains("Acme house rules") && sent.System.Contains("OUTPUT CONTRACT"),
                "E the system prompt carried the proofreading prompt, the memory bank and the proofreader's output contract");
            Check(sent.System == sysWithout, "E it is exactly a full run's system prompt without terms and without the document");
            Check(sent.User.StartsWith("## One segment (this request)") && sent.User.Contains("# TERMBASE") && sent.User.Contains("- valve")
                && sent.User.Contains("segment 10 source:") && sent.User.Contains("segment 13 source:"),
                "E the user prompt opened with the one-segment note, the terms and the neighbours");
            Check(sent.User.Contains("[SEGMENT 0012]\r\nSource: " + Source + "\r\nTarget: " + Target) || sent.User.Contains("[SEGMENT 0012]\nSource: " + Source + "\nTarget: " + Target),
                "E then the segment, source and target, under its number");
            Check(sent.User.IndexOf("segment 13 source:") < sent.User.IndexOf("**SEGMENTS TO REVIEW (1 segments):**"), "E in that order");
            Check(!sent.System.Contains("FULL document") && !sent.User.Contains("FULL document") && !sent.System.Contains("[SEGMENT 0001]\r\nSource:")
                && !sent.User.Contains("[SEGMENT 0011]") && !sent.User.Contains("[SEGMENT 0013]"),
                "E and not the document, although \"Include document context\" is ticked in these settings");
            Check(r.Disposed && r.Cancelled, "E afterwards the window's token source is disposed");
            {
                // Another segment, other terms, other neighbours: the system prompt must be the
                // same bytes, or the provider's cache never serves the bank.
                _answer = "[SEGMENT 0040] OK";
                var otherLead = (string)Call(Rules, "Lead", 40, null, "Before this segment:\n- segment 39 source: Another Acme sentence.");
                var r2 = Run(Job("The Acme pump starts.", "De Acme-pomp start.", otherLead, 40));
                var sent2 = LastRequest();
                Check(r2.Header == "No issues found in this segment." && sent2.System == sent.System && sent2.User != sent.User
                    && sent2.User.Contains("segment 39 source:") && sent2.User.Contains("[SEGMENT 0040]"),
                    "E a check of another segment sends the same system prompt, byte for byte (" + sent.System.Length + " chars): only the user prompt differs");
            }
            {
                // No bank, no prompt: still a request, with the built-in instructions.
                _answer = "[SEGMENT 0012] OK";
                var plain = Job(Source, Target, (string)Call(Rules, "Lead", 12, null, null), 12, false);
                Set(plain, "Instructions", null);
                var r3 = Run(plain);
                var sent3 = LastRequest();
                Check(r3.Header == "No issues found in this segment." && !sent3.System.Contains("# SUPERMEMORY") && !sent3.System.Contains("# CUSTOM INSTRUCTIONS")
                    && !sent3.User.Contains("Surrounding segments"), "E no memory bank, no prompt, no neighbours: the built-in instructions and the segment alone");
            }

            // No issue.
            _answer = "[SEGMENT 0012] OK";
            r = Run(Job(Source, Target, instructions));
            Check(!r.TimedOut && !r.Threw && !r.Handed && !r.CanWrite && r.Header == "No issues found in this segment." && r.Discard == "Close",
                "E no issue: said, nothing to write, Write to target disabled");
            Check(r.Body.Contains("fake-unlisted-model") && r.Body.Contains("not compared with the rest of the document") && r.Fits,
                "E and the window says what was not checked (" + Flat(r.Body) + ")");

            // A verdict given as a header line and a verdict line (as one model writes it).
            _answer = "[SEGMENT 0012]\nISSUE\nIssue: " + IssueLine + "\nSuggestion: " + SuggestionLine + "\nCorrected: De Acme-klep gaat dicht bij 80\u00A0bar.";
            r = Run(Job(Source, Target, instructions));
            Check(r.Handed && r.Correction == Fixed && r.CanWrite, "E a verdict split over two lines is read");

            // A correction that drops a tag.
            const string TaggedSource = "<t1>WARNING</t1> the valve<t2/> closes.";
            const string TaggedTarget = "<t1>WAARSCHUWING</t1> de klep<t2/> gaat open.";
            _answer = IssueReply("WAARSCHUWING de klep<t2/> gaat dicht.");
            r = Run(Job(TaggedSource, TaggedTarget, instructions));
            Check(!r.TimedOut && !r.Threw && !r.Handed && !r.CanWrite && r.Discard == "Close",
                "E failure: a correction that drops a tag -> not offered for writing");
            Check(r.Header.Contains("Issue found") && r.Header.Contains("Nothing can be written") && r.Fits
                && r.Body.Contains("Issue: " + IssueLine) && r.Body.Contains("Nothing can be written: the correction is missing the source's <t1> tag.")
                && r.Body.Contains("WAARSCHUWING de klep<t2/> gaat dicht."),
                "E the window shows the issue, says why nothing can be written, and gives the AI's correction (" + Flat(r.Body) + ")");
            _answer = IssueReply("<t1>WAARSCHUWING</t1> de klep<t2/> gaat dicht.");
            r = Run(Job(TaggedSource, TaggedTarget, instructions));
            Check(r.Handed && r.Correction == "<t1>WAARSCHUWING</t1> de klep<t2/> gaat dicht." && r.CanWrite && !r.Body.Contains("<t1>WAARSCHUWING</t1> de klep<t2/> gaat dicht."),
                "E the same correction with its tags is offered, and shown as words without the tag markers");

            // An issue without a correction, and one whose correction changes nothing.
            _answer = IssueReply(null);
            r = Run(Job(Source, Target, instructions));
            Check(!r.Handed && !r.CanWrite && r.Body.Contains("Issue: " + IssueLine) && r.Body.Contains("Nothing can be written: the AI gave no corrected translation"),
                "E failure: an issue without a correction -> the issue is shown, nothing to write");
            _answer = IssueReply(Target);
            r = Run(Job(Source, Target, instructions));
            Check(!r.Handed && !r.CanWrite && r.Body.Contains("Nothing can be written") && r.Body.Contains("reads exactly as the translation does now"),
                "E failure: a correction that is the translation as it stands -> nothing to write");

            // Replies that hold no verdict for this segment.
            _answer = "I think this translation looks fine overall!";
            r = Run(Job(Source, Target, instructions));
            Check(!r.TimedOut && !r.Threw && !r.Handed && !r.CanWrite && r.Header == "Nothing was written." && r.Body.Contains("No verdict could be read"),
                "E failure: a garbled reply -> said, and NOT shown as \"no issues\" (" + Flat(r.Body) + ")");
            _answer = "[SEGMENT 0001] OK";
            r = Run(Job(Source, Target, instructions));
            Check(!r.Handed && !r.CanWrite && r.Header == "Nothing was written." && r.Body.Contains("No verdict could be read"),
                "E failure: a verdict for another segment number -> not taken for this one");
            _answer = "";
            r = Run(Job(Source, Target, instructions));
            Check(!r.TimedOut && !r.Threw && !r.Handed && !r.CanWrite && r.Header == "Nothing was written." && r.Body.Trim().Length > 0,
                "E failure: an empty reply -> nothing to write, said (" + Flat(r.Body) + ")");
            _answer = "[SEGMENT 0012] OK";
            r = Run(Job(Source, Target, instructions));
            Check(r.Header == "No issues found in this segment.", "E and the next check works");

            // An AI error.
            _fail = true;
            r = Run(Job(Source, Target, instructions));
            Check(!r.TimedOut && !r.Threw && !r.Handed && !r.CanWrite, "E failure: the AI answers with an error -> nothing to write, nothing thrown");
            Check(r.Header == "Nothing was written." && r.Body.Contains("stand-in failure") && r.Discard == "Close", "E the window says so, with the AI's own message (" + Flat(r.Body) + ")");
            Check(r.Disposed && r.Cancelled, "E failure: nothing left behind (token source disposed)");
            _fail = false;
            _answer = IssueReply("De Acme-klep gaat dicht bij 80 bar.");
            r = Run(Job(Source, Target, instructions));
            Check(r.Handed && r.CanWrite, "E and the next check works");

            // An answer that never comes; the translator closes the window.
            _hold = new ManualResetEvent(false);
            n0 = RequestCount();
            r = Run(Job(Source, Target, instructions), true);
            Check(!r.TimedOut && !r.Threw && !r.Handed && RequestCount() == n0 + 1,
                "E failure: no answer, window closed -> the work ends (" + r.Ms + " ms), nothing handed over, nothing thrown");
            Check(r.Disposed && r.Cancelled, "E failure: nothing left behind (cancelled, token source disposed)");
            _hold.Set(); _hold = null; Pump(300);
            r = Run(Job(Source, Target, instructions));
            Check(r.Handed && r.CanWrite, "E and the next check works");

            // A bank that is still being selected when the window is closed.
            {
                var resultType = P.GetType("Supervertaler.Trados.Core.TranslationBankContext+Result", true);
                var tcs = Activator.CreateInstance(typeof(TaskCompletionSource<>).MakeGenericType(resultType));
                var waiting = Job(Source, Target, instructions, 12, false);
                Set(waiting, "Bank", tcs.GetType().GetProperty("Task").GetValue(tcs, null));
                n0 = RequestCount();
                var rr = new RunResult();
                Task task = null;
                var f = Open(d => { task = Task.Run(() => (Task)View.GetMethod("ProofreadSegmentRun", Any).Invoke(null, new object[] { waiting, d, (Action<string>)(c => rr.Handed = true) })); }, null);
                f.Show(); Pump(300);
                f.Close();
                var sw2 = Stopwatch.StartNew();
                while (sw2.ElapsedMilliseconds < 10000 && (task == null || !task.IsCompleted)) { Application.DoEvents(); Thread.Sleep(5); }
                f.Dispose();
                Check(task != null && task.IsCompleted && !task.IsFaulted && !rr.Handed && RequestCount() == n0 && SourceDisposed(f),
                    "E failure: the window closed while the memory bank was still being read -> the work ends, no request is made, nothing left behind");
                tcs.GetType().GetMethod("TrySetResult").Invoke(tcs, new[] { Activator.CreateInstance(resultType, true) });
            }
        }

        Console.WriteLine("F  the write");
        {
            var d = Three(Source, Target);
            var o = Write(d, JobFor(d), Fixed);
            Check(o.Why == null && o.Note == null && Mk(Tgt(d)) == Fixed, "F the correction lands in the target, nothing to report" + (o.Why == null ? "" : " [" + o.Why + "]"));
            Check(Seg(d).Properties.ConfirmationLevel == ConfirmationLevel.Draft && d.StatusUpdates == 1, "F a confirmed segment becomes a draft again");
            Check(Mk(Tgt(d, "11")) == Before && Mk(Tgt(d, "13")) == After && d.Pairs[0].Properties.ConfirmationLevel == ConfirmationLevel.Translated,
                "F and the segments beside it are untouched");

            // The correction as a model gives it, with an ordinary space: the target's own character is kept.
            d = Three(Source, Target);
            o = Write(d, JobFor(d), Fixed.Replace('\u00A0', ' '));
            Check(o.Why == null && Mk(Tgt(d)) == Fixed, "F the target's non-breaking space is kept where the correction left the text alone");

            // The segment changed while the window was open (Apply all, or update_segments over MCP).
            d = Three(Source, Target);
            var staleJob = JobFor(d);
            Tgt(d).Clear(); Tgt(d).Add(T("De Acme-klep opent bij 80\u00A0bar."));
            o = Write(d, staleJob, Fixed);
            Check(o.Why != null && o.Why.Contains("has changed since it was checked") && d.Edits == 0 && Mk(Tgt(d)) == "De Acme-klep opent bij 80\u00A0bar."
                && Seg(d).Properties.ConfirmationLevel == ConfirmationLevel.Translated,
                "F failure: the segment was changed meanwhile -> refused, nothing written, status as it was (" + o.Why + ")");

            d = Three(Source, Target);
            var lockedJob = JobFor(d);
            Seg(d).Properties.IsLocked = true;
            o = Write(d, lockedJob, Fixed);
            Check(o.Why != null && o.Why.Contains("locked") && d.Edits == 0 && Mk(Tgt(d)) == Target, "F failure: the segment was locked meanwhile -> refused (" + o.Why + ")");

            d = Three(Source, Target);
            o = Write(d, JobFor(d), "De Acme-klep\ngaat dicht bij 80\u00A0bar.");
            Check(o.Why != null && o.Why.Contains("line breaks") && d.Edits == 0 && Mk(Tgt(d)) == Target,
                "F failure: a correction that must not be written is refused at the write too (" + o.Why + ")");
            o = Write(d, JobFor(d), "  ");
            Check(o.Why != null && d.Edits == 0 && Mk(Tgt(d)) == Target, "F failure: an empty correction is never written (" + o.Why + ")");

            d = Three(Source, Target); d.DeclineAll = true;
            o = Write(d, JobFor(d), Fixed);
            Check(o.Why != null && o.Why.Contains("Studio did not accept the change") && Mk(Tgt(d)) == Target
                && Seg(d).Properties.ConfirmationLevel == ConfirmationLevel.Translated && d.StatusUpdates == 0,
                "F failure: Studio quietly declines the write -> reported as not written, target and status as they were (" + o.Why + ")");

            d = Three(Source, Target); d.ThrowOnApply = true;
            o = Write(d, JobFor(d), Fixed);
            Check(o.Why != null && o.Why.Contains("Studio did not accept the change") && o.Why.Contains("could not apply") && Mk(Tgt(d)) == Target
                && Seg(d).Properties.ConfirmationLevel == ConfirmationLevel.Translated && d.StatusUpdates == 0,
                "F failure: Studio throws -> reported as not written, nothing thrown (" + o.Why + ")");

            d = Three(Source, Target); d.StatusThrows = true;
            o = Write(d, JobFor(d), Fixed);
            Check(o.Why == null && Mk(Tgt(d)) == Fixed && o.Note != null && o.Note.Contains("could not be set to Draft"),
                "F failure: written, but the status cannot be set -> said, not passed over (" + o.Note + ")");

            // Where the pane follows the document, it is told of the unsaved write, as Apply does.
            d = Three(Source, Target);
            var pane = FormatterServices.GetUninitializedObject(View);
            Set(pane, "_activeDocument", d);
            View.GetField("_currentInstance", Any).SetValue(null, pane);
            try
            {
                var other = Three(Source, Target);
                o = Write(other, JobFor(other), Fixed);
                Check(o.Why == null && Get(pane, "_bridgeUnsavedWritesDoc") == null, "F a write into a document the pane does not follow leaves the pane alone");
                o = Write(d, JobFor(d), Fixed);
                Check(o.Why == null && ReferenceEquals(Get(pane, "_bridgeUnsavedWritesDoc"), d), "F a write into the pane's document marks it as holding an unsaved write");
                Check(Get(pane, "_currentReport") == null, "F and the pane's proofreading report is not touched (there is none)");
            }
            finally { View.GetField("_currentInstance", Any).SetValue(null, null); }
        }

        Console.WriteLine("G  run and write together");
        {
            // The source's tags, and one the translator added (bold by hand): kept, all of them.
            Func<IAbstractMarkupData[]> src = () => new IAbstractMarkupData[] { G("1", T("WARNING")), T(" the valve"), X("2"), T(" closes.") };
            Func<IAbstractMarkupData[]> tgt = () => new IAbstractMarkupData[] { G("1", T("WAARSCHUWING")), T(" de klep"), X("2"), T(" gaat "), G("b1", T("open")), T(".") };
            var d = Three(src(), tgt());
            var shown = Proof(Seg(d));
            Check(shown[0] == "<t1>WARNING</t1> the valve<t2/> closes." && shown[1] == "<t1>WAARSCHUWING</t1> de klep<t2/> gaat <t3>open</t3>.",
                "G the segment as it is sent: the target's own tag has a number above the source's (" + shown[1] + ")");
            var job = JobFor(d);
            _answer = IssueReply("<t1>WAARSCHUWING</t1> de klep<t2/> gaat <t3>dicht</t3>.");
            var r = Run(job);
            Check(r.Handed && r.CanWrite && r.Correction == "<t1>WAARSCHUWING</t1> de klep<t2/> gaat <t3>dicht</t3>.", "G a correction that keeps all three tags is offered");
            var before = Mk(Tgt(d));
            var o = Write(d, job, r.Correction);
            Check(o.Why == null && o.Note == null && Mk(Tgt(d)) == before.Replace("open", "dicht") && Mk(Tgt(d)) == "<g1>WAARSCHUWING</g1> de klep<x2/> gaat <gb1>dicht</gb1>.",
                "G written: the tag pair, the standalone tag and the target-only tag are all in place (" + Mk(Tgt(d)) + ")" + (o.Why == null ? "" : " [" + o.Why + "]") + (o.Note == null ? "" : " [note: " + o.Note + "]"));
            Check(Seg(d).Properties.ConfirmationLevel == ConfirmationLevel.Draft, "G as a draft");

            // The same segment, a correction that keeps the translator's own tag and drops one of the
            // source's: neither side's tags, never written.
            d = Three(src(), tgt());
            job = JobFor(d);
            _answer = IssueReply("<t1>WAARSCHUWING</t1> de klep gaat <t3>dicht</t3>.");
            r = Run(job);
            Check(!r.Handed && !r.CanWrite && r.Body.Contains("Nothing can be written: the correction is missing the target's <t2/> tag."),
                "G failure: a correction that loses a tag is not offered, and the window says which tag (" + Flat(r.Body) + ")");
            Check(d.Edits == 0 && Mk(Tgt(d)) == before, "G and the segment is as it was");

            // Track Changes on: only the changed word is recorded.
            d = Three(Source, Target);
            d.Editor.TrackChanges = true;
            job = JobFor(d);
            _answer = IssueReply("De Acme-klep gaat dicht bij 80 bar.");
            r = Run(job);
            o = Write(d, job, r.Correction);
            Check(r.Handed && o.Why == null && Accept(Tgt(d)) == Fixed && Reject(Tgt(d)) == Target,
                "G Track Changes on: written; Accept All gives the correction, Reject All the translation as it was" + (o.Why == null ? "" : " [" + o.Why + "]"));
            Check(Mk(Tgt(d)).Contains("{-open-}") && Mk(Tgt(d)).Contains("{+dicht+}") && Revisions(Tgt(d)) == 2 && d.WholeSegment == 0,
                "G recorded word by word: one word removed, one added, the rest untouched (" + Mk(Tgt(d)) + ")");
            Check(d.TrackingAtApply == false && TrackingRestored(d), "G Track Changes was suspended for the write only, and is on again");
            Check(Seg(d).Properties.ConfirmationLevel == ConfirmationLevel.Draft, "G as a draft");

            // Track Changes on and the write fails: tracking is on again.
            d = Three(Source, Target);
            d.Editor.TrackChanges = true; d.ThrowOnApply = true;
            o = Write(d, JobFor(d), Fixed);
            Check(o.Why != null && TrackingRestored(d) && Mk(Tgt(d)) == Target, "G failure: Studio throws with Track Changes on -> not written, and Track Changes is on again");
        }

        Console.WriteLine("H  no report, nothing kept");
        {
            Check(!File.Exists(statePath), "H no proofreading report state was saved (" + statePath + ")");
            Check(!Directory.Exists(reportsDir) || Directory.GetFiles(reportsDir).Length == 0, "H no proofreading report file was written");
            var stray = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(p => Path.GetFileName(p).IndexOf("report", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            Check(stray.Count == 0, "H nothing named like a report anywhere in the test's data folder" + (stray.Count > 0 ? " (" + string.Join(", ", stray) + ")" : ""));
            var kept = View.GetFields(Any).Where(fi => fi.Name.IndexOf("ProofreadSegment", StringComparison.OrdinalIgnoreCase) >= 0
                || fi.Name.IndexOf("ProofreadActive", StringComparison.OrdinalIgnoreCase) >= 0).Select(fi => fi.Name).ToList();
            Check(kept.Count == 0 && View.GetField("_currentInstance", Any).GetValue(null) == null,
                "H nothing is kept between uses: the view part has no field for this feature" + (kept.Count > 0 ? " (" + string.Join(", ", kept) + ")" : ""));
            Check(Rules.GetFields(Any).All(fi => fi.IsLiteral), "H and its rules class holds constants only");
        }

        Console.WriteLine("Q  QuickLauncher: a prompt that is in a folder twice");
        {
            var menu = P.GetType("Supervertaler.Trados.Core.QuickLauncherMenu", true);
            Func<List<PromptTemplate>, IEnumerable> shownOf = list => (IEnumerable)menu.GetMethod("WithoutTwins", Any).Invoke(null, new object[] { list });
            var list1 = new List<PromptTemplate> {
                Prompt_("Define", "both", "Define.md"), Prompt_("Define", "trados", "Define [Trados].md"),
                Prompt_("Explain", "both", "Explain.md") };
            Check(Files(shownOf(list1)) == "Define [Trados].md,Explain.md", "Q a prompt tagged for Trados hides its untagged twin; a prompt with a name of its own stays (" + Files(shownOf(list1)) + ")");
            Check(list1.Count == 3, "Q the list it was given is not changed");
            var list2 = new List<PromptTemplate> { Prompt_("define", "trados", "a.md"), Prompt_("DEFINE", null, "b.md"), Prompt_(" Define ", "", "c.md") };
            Check(Files(shownOf(list2)) == "a.md", "Q names are compared without regard to case or spaces around them; no app at all counts as untagged");
            var list3 = new List<PromptTemplate> { Prompt_("Define", "both", "a.md"), Prompt_("Define", "both", "b.md") };
            Check(Files(shownOf(list3)) == "a.md,b.md", "Q two untagged prompts of one name both stay: there is nothing to choose by");
            var list4 = new List<PromptTemplate> { Prompt_("Define", "trados", "a.md"), Prompt_("Define", "Trados", "b.md"), Prompt_("Define", "both", "c.md") };
            Check(Files(shownOf(list4)) == "a.md,b.md", "Q two tagged prompts of one name both stay, their untagged twin goes");
            var list5 = new List<PromptTemplate> { Prompt_("Define term", "both", "a.md", "Define"), Prompt_("Define", "trados", "b.md") };
            Check(Files(shownOf(list5)) == "b.md", "Q the name compared is the one the menu shows (a prompt's QuickLauncher label)");
            var list6 = new List<PromptTemplate> { Prompt_("Zeta", "both", "z.md"), null, Prompt_("Alpha", "trados", "a.md"), Prompt_("Beta", "memoq", "b.md") };
            Check(Files(shownOf(list6)) == "z.md,a.md,b.md", "Q order is kept; a prompt tagged for something else is not hidden by this rule; a null entry is dropped");
            Check(!shownOf(null).Cast<object>().Any() && !shownOf(new List<PromptTemplate>()).Cast<object>().Any(), "Q failure: no list, or an empty one -> an empty list");
            var many = new List<PromptTemplate>();
            for (int i = 0; i < 20000; i++) { many.Add(Prompt_("Prompt " + i, "both", i + ".md")); if (i % 2 == 0) many.Add(Prompt_("Prompt " + i, "trados", i + " [Trados].md")); }
            var swq = Stopwatch.StartNew();
            int left = shownOf(many).Cast<object>().Count();
            swq.Stop();
            Check(left == 20000 && swq.ElapsedMilliseconds < 2000, "Q scale: 30,000 prompts in one folder, 10,000 twins hidden, in " + swq.ElapsedMilliseconds + " ms");
        }

        var realLog = "D:\\Supervertaler\\trados\\logs\\diagnostic.log";
        Check(!string.Equals(Path.GetFullPath((string)P.GetType("Supervertaler.Trados.Core.DiagnosticLog", true).GetProperty("LogFilePath").GetValue(null, null)), realLog, StringComparison.OrdinalIgnoreCase),
            "the diagnostic log used is not the real one");

        try { _listener.Stop(); } catch { }
        Console.WriteLine(Pass + " passed, " + Fail + " failed");
        return Fail == 0 ? 0 : 1;
    }
}
'@

foreach ($n in @('Sdl.FileTypeSupport.Framework.Core.dll', 'Sdl.Core.Globalization.dll')) { [void]$refs.Add((Join-Path $studio $n)) }
[void]$refs.Add($iface.Assembly.Location)
$all = @($refs) + @('System.dll', 'System.Core.dll', 'System.Runtime.Serialization.dll', 'System.Web.Extensions.dll', 'System.Windows.Forms.dll', 'System.Drawing.dll', $Dll)
Add-Type -TypeDefinition ($src + "`n" + $stub) -ReferencedAssemblies $all
$manifest = Join-Path (Split-Path $Dll) 'Supervertaler.Trados.plugin.xml'
try { $code = [ProofreadSegmentTest]::Run($Dll, $Root, $manifest) }
catch { $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }; Write-Host $e.ToString(); exit 2 }
exit $code
