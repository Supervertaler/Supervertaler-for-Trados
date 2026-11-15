# A write Studio takes and then throws on (seen 14 Nov 2026: "Attempted to read
# or write protected memory" out of ProcessSegmentPair on long segments in
# Batch Translate, the target complete in the document each time). Runs the
# BUILT plugin's own SegmentWriteCheck.ProcessChecked and the batch run's
# TryAddTcComment on REAL segments from Studio's own factory, through a
# stand-in for Studio's document (IStudioDocument, generated from Studio's
# interface) that can be told to throw an AccessViolationException after it
# has applied an edit, before it has, or to make the segment unreadable.
# Invented text only. No Studio is started, no AI request is made.
#
#   1  an ordinary write: lands, nothing reported
#   2  Studio applies the edit, then throws: the write STANDS - nothing thrown,
#      the caller is told once, the log has the exception type, a stack trace,
#      lengths and ids, and not the text
#   3  Studio throws before applying: the exception reaches the caller as
#      before (same type), nobody is told the write landed
#   4  the edit itself throws: reaches the caller untouched, no fault logged
#   5  Studio applies, throws, and the segment cannot be read back: not given
#      the benefit of the doubt - the exception reaches the caller
#   6  an edit that changes nothing, and Studio throws: reaches the caller
#   7  Studio quietly declines an edit (no exception): still refused as before
#   8  scale: 2,000 writes of a 1,500-character target, every 7th throwing
#      after it landed: none lost, none reported as failed
#   9  a comment add that throws AFTER adding: counted as added (so the note is
#      not written inline as well); one that throws without adding: not
#  10  the MCP tool add_comment (and a proofreading finding added as a
#      comment), through SegmentWriteCheck.AddCommentChecked: an add that
#      throws AFTER adding stands and is logged with its stack trace, without
#      the text; one that throws without adding reaches the caller - also when
#      a comment with the same text was already on the segment; unreadable
#      afterwards: reaches the caller; 2,000 adds, every 7th throwing after
#      it was added: none reported as failed, none added twice
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\write_fault_test.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\write_fault_test.ps1 -Dll <Supervertaler.Trados.dll>
# A Studio 2024 / 2022 build is 32-bit: the script starts the 32-bit PowerShell
# for it by itself.
#
# What it cannot reach: Studio's own ProcessSegmentPair and AddCommentOnSegment.
# Why Studio throws is not tested here, only what Supervertaler does when it
# has. Nor the MCP bridge around add_comment (BridgeAddComment needs the
# running view part): case 10 runs the call it now makes.
param(
    [string]$Dll = (Join-Path (Split-Path -Parent $PSScriptRoot) "src\Supervertaler.Trados\bin\Studio19\Debug\Supervertaler.Trados.dll"),
    [string]$Root = (Join-Path $env:TEMP "sv-write-fault-test"),
    [switch]$Child
)
$ErrorActionPreference = 'Stop'

$Root = [IO.Path]::GetFullPath($Root)
if ($Root -like 'D:\Supervertaler*' -or $Root.Length -lt 10) { throw "Refusing to use $Root as the test root." }
$Dll = [IO.Path]::GetFullPath($Dll)
if (-not (Test-Path -LiteralPath $Dll)) { throw "No plugin build at $Dll" }

if (-not $Child) {
    # Parent: run the cases in a child of the right bitness; guard the real log.
    $realLog = 'D:\Supervertaler\trados\logs\diagnostic.log'
    function Stamp { if (Test-Path -LiteralPath $realLog) { $i = Get-Item -LiteralPath $realLog; "$($i.Length)|$($i.LastWriteTimeUtc.Ticks)" } else { 'absent' } }
    $before = Stamp
    $ps = if ($Dll -like '*Studio19*') { Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe' }
          else { Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe' }
    Write-Host "Plugin: $Dll"
    & $ps -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -Child -Dll $Dll -Root $Root
    $code = $LASTEXITCODE
    if ((Stamp) -ne $before) { Write-Host "NOTE  the real diagnostic log changed during the test (a running Studio writes to it too; the test's own log is under $Root)" }
    if ($code -ne 0) { Write-Host "RESULT: FAILED"; exit 1 }
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
public static class WriteFaultResolver
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
[WriteFaultResolver]::Register(@((Split-Path $Dll), $studio))
Add-Type -AssemblyName System.Windows.Forms
$plugin = [Reflection.Assembly]::LoadFrom($Dll)
$dataRoot = [string](Join-Path $Root 'data')
# The test's own data folder, before anything can log: never the real one.
$plugin.GetType("Supervertaler.Core.SupervertalerPaths").GetMethod("Set").Invoke($null, @($dataRoot))
$logType = $plugin.GetType("Supervertaler.Trados.Core.DiagnosticLog", $true)
$logPath = [string]$logType.GetProperty("LogFilePath").GetValue($null)
if (-not $logPath.StartsWith($Root, [StringComparison]::OrdinalIgnoreCase)) { throw "The diagnostic log would be written to $logPath, outside the test root." }
$logType.GetProperty("Enabled").SetValue($null, $true)

# The stand-in document: IStudioDocument generated from this Studio's own
# interface, every member throwing except the ones the write paths use.
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
$specialProps = @('ActiveSegmentPair')
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
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Sdl.Core.Globalization;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Sdl.FileTypeSupport.Framework.NativeApi;

// One paragraph of segments. ProcessSegmentPair hands the edit a copy of the
// pair and applies its target - as Studio does - and can be told to throw
// what Studio threw on 14 Nov 2026, before or after applying.
public partial class FakeDocument
{
    public IDocumentItemFactory F;
    public IPropertiesFactory PF;
    public List<ISegmentPair> Pairs = new List<ISegmentPair>();
    public IParagraphUnit Pu;

    public bool ThrowAfterApply, ThrowBeforeApply, DontApply, Unreadable;
    public int FaultEvery;                 // 0 = never; n = throw after applying on every nth write
    public bool CommentThrowAfter, CommentThrowBefore;
    public int CommentFaultEvery;          // 0 = never; n = throw after adding on every nth comment
    public int Writes, Faults, CommentAdds;

    ISegmentPair LiveOf(ISegmentPair pair) { return Pairs.First(p => p.Properties.Id.Id == pair.Properties.Id.Id); }
    void FakeProcessSegmentPair(ISegmentPair pair, string undoName, Action<ISegmentPair, CancelEventArgs> edit)
    {
        var live = LiveOf(pair);
        var copy = F.CreateSegmentPair((ISegment)live.Source.Clone(), (ISegment)live.Target.Clone());
        var cancel = new CancelEventArgs();
        edit(copy, cancel);
        if (cancel.Cancel || DontApply) return;
        Writes++;
        if (ThrowBeforeApply) { Faults++; throw new AccessViolationException(); }
        live.Target.Clear();
        copy.Target.MoveAllItemsTo(live.Target);
        if (ThrowAfterApply || (FaultEvery > 0 && Writes % FaultEvery == 0)) { Faults++; throw new AccessViolationException(); }
    }
    IParagraphUnit FakeGetParentParagraphUnit(ISegmentPair pair) { return Pu; }
    IEnumerable<ISegmentPair> FakeGetSegmentPairsFromParagraph(object id)
    {
        if (Unreadable) throw new InvalidOperationException("fake: the paragraph cannot be read");
        return Pairs;
    }
    void FakeUpdateSegmentPairProperties(object pair, object props) { }
    void FakeChangeTranslationOriginOnSegment(object pair, object origin) { }
    ISegmentPair FakeActiveSegmentPair { get { return Pairs.Count > 0 ? Pairs[0] : null; } }
    void FakeAddCommentOnSegment(ISegmentPair pair, string text, Severity severity)
    {
        if (CommentThrowBefore) throw new AccessViolationException();
        var live = LiveOf(pair);
        var props = PF.CreateCommentProperties();
        props.Add(PF.CreateComment(text, "Acme reviser", severity));
        var marker = F.CreateCommentMarker(props);
        live.Target.MoveAllItemsTo(marker);
        live.Target.Add(marker);
        CommentAdds++;
        if (CommentThrowAfter || (CommentFaultEvery > 0 && CommentAdds % CommentFaultEvery == 0)) { Faults++; throw new AccessViolationException(); }
    }
}

public static class WriteFaultTest
{
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static int Pass, Fail;
    static Assembly P;
    static MethodInfo ProcessChecked, TryAddTcComment, AddCommentChecked;
    static IDocumentItemFactory F;
    static IPropertiesFactory PF;
    static string LogPath;

    static void Check(bool ok, string what)
    {
        if (ok) Pass++; else Fail++;
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what);
    }

    static IText T(string s) { return F.CreateText(PF.CreateTextProperties(s)); }
    static FakeDocument Doc(int segments, string target = "")
    {
        var pu = F.CreateParagraphUnit(LockTypeFlags.Unlocked);
        pu.Properties.ParagraphUnitId = new ParagraphUnitId("pu-1");
        var d = new FakeDocument { F = F, PF = PF, Pu = pu };
        for (int i = 1; i <= segments; i++)
        {
            var props = F.CreateSegmentPairProperties();
            props.Id = new SegmentId(i.ToString());
            var source = F.CreateSegment(props);
            source.Add(T("Acme bronzin " + i + "."));
            var t = F.CreateSegment(props);
            if (target.Length > 0) t.Add(T(target));
            d.Pairs.Add(F.CreateSegmentPair(source, t));
        }
        return d;
    }
    static string Text(ISegment s)
    {
        var sb = new StringBuilder();
        Walk(s, sb);
        return sb.ToString();
    }
    static void Walk(IAbstractMarkupDataContainer c, StringBuilder sb)
    {
        foreach (var item in c)
        {
            if (item is IText) sb.Append(((IText)item).Properties.Text);
            else if (item is IAbstractMarkupDataContainer) Walk((IAbstractMarkupDataContainer)item, sb);
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

    sealed class Outcome { public Exception Thrown; public List<Exception> Told = new List<Exception>(); }

    // The plugin's own AddCommentChecked, which add_comment calls; what it threw, or null.
    static Exception AddComment(FakeDocument d, ISegmentPair pair, string text)
    {
        try { AddCommentChecked.Invoke(null, new object[] { d, pair, text, Severity.Low }); return null; }
        catch (TargetInvocationException ex) { return ex.InnerException; }
    }

    // The plugin's own ProcessChecked, with an edit that sets the target's text.
    static Outcome Write(FakeDocument d, ISegmentPair pair, string text, Action<ISegmentPair> edit = null)
    {
        var o = new Outcome();
        Action<ISegmentPair, CancelEventArgs> e = (sp, cancel) =>
        {
            if (edit != null) { edit(sp); return; }
            sp.Target.Clear();
            sp.Target.Add(T(text));
        };
        Action<Exception> told = ex => o.Told.Add(ex);
        try { ProcessChecked.Invoke(null, new object[] { d, pair, "Supervertaler", e, false, false, told }); }
        catch (TargetInvocationException ex) { o.Thrown = ex.InnerException; }
        return o;
    }
    static string Log() { return File.Exists(LogPath) ? File.ReadAllText(LogPath) : ""; }
    static void ClearLog() { if (File.Exists(LogPath)) File.Delete(LogPath); }

    const string Dutch = "De Acme-installatie wordt ingeschakeld bij 230 V.";

    public static int Run(string pluginPath, string logPath)
    {
        P = Assembly.LoadFrom(pluginPath);
        LogPath = logPath;
        var swc = P.GetType("Supervertaler.Trados.Core.SegmentWriteCheck", true);
        ProcessChecked = swc.GetMethod("ProcessChecked", Any);
        Check(ProcessChecked != null && ProcessChecked.GetParameters().Length == 7, "the build has ProcessChecked with the faultAfterWrite parameter");
        if (ProcessChecked == null || ProcessChecked.GetParameters().Length != 7) return 1;
        TryAddTcComment = P.GetType("Supervertaler.Trados.AiAssistantViewPart", true).GetMethod("TryAddTcComment", Any);
        F = (IDocumentItemFactory)Activator.CreateInstance(Type.GetType(
            "Sdl.FileTypeSupport.Framework.Bilingual.DocumentItemFactory, Sdl.FileTypeSupport.Framework.Implementation", true));
        PF = (IPropertiesFactory)Activator.CreateInstance(Type.GetType(
            "Sdl.FileTypeSupport.Framework.Native.PropertiesFactory, Sdl.FileTypeSupport.Framework.Implementation", true));
        F.PropertiesFactory = PF;

        // 1
        var d = Doc(2);
        var o = Write(d, d.Pairs[0], Dutch);
        Check(o.Thrown == null && o.Told.Count == 0 && Text(d.Pairs[0].Target) == Dutch, "1 an ordinary write lands, nothing thrown, nothing reported");

        // 2
        ClearLog();
        d = Doc(2); d.ThrowAfterApply = true;
        o = Write(d, d.Pairs[1], Dutch);
        var log = Log();
        Check(o.Thrown == null, "2 Studio applies, then throws: nothing is thrown to the caller" + (o.Thrown != null ? " (threw " + o.Thrown.GetType().Name + ")" : ""));
        Check(Text(d.Pairs[1].Target) == Dutch, "2 the target is in the document");
        Check(o.Told.Count == 1 && o.Told[0] is AccessViolationException, "2 the caller is told once, with Studio's exception");
        Check(log.Contains("System.AccessViolationException") && log.Contains("IS in the document"), "2 the log names the exception and says the write stands");
        Check(log.Contains("FakeProcessSegmentPair"), "2 the log has the stack trace (the throwing method is named)");
        Check(log.Contains(Dutch.Length + " chars") && log.Contains("segment id 2") && log.Contains("active segment: no") && log.Contains("-bit"),
            "2 the log has the length, the segment id, whether it is the active segment, and the bitness");
        Check(!log.Contains("Acme"), "2 the log does not hold the text");

        // 3
        ClearLog();
        d = Doc(2, "Oud."); d.ThrowBeforeApply = true;
        o = Write(d, d.Pairs[0], Dutch);
        log = Log();
        Check(o.Thrown is AccessViolationException && o.Told.Count == 0, "3 Studio throws before applying: the same exception reaches the caller, nobody is told it landed");
        Check(o.Thrown != null && (o.Thrown.StackTrace ?? "").Contains("FakeProcessSegmentPair"), "3 with its original stack trace");
        Check(Text(d.Pairs[0].Target) == "Oud." && log.Contains("is NOT in the document"), "3 the target is as it was, and the log says so");

        // 4
        ClearLog();
        d = Doc(2); d.ThrowAfterApply = true;
        o = Write(d, d.Pairs[0], Dutch, sp => { throw new InvalidOperationException("fake: the edit failed"); });
        Check(o.Thrown is InvalidOperationException && o.Told.Count == 0 && !Log().Contains("Studio threw"),
            "4 an exception from the edit itself reaches the caller, and is not logged as Studio's");

        // 5
        d = Doc(2); d.ThrowAfterApply = true; d.Unreadable = true;
        o = Write(d, d.Pairs[0], Dutch);
        Check(o.Thrown is AccessViolationException && o.Told.Count == 0, "5 applied, thrown, but unreadable afterwards: the exception reaches the caller");

        // 6
        d = Doc(2, Dutch); d.ThrowAfterApply = true;
        o = Write(d, d.Pairs[0], Dutch);
        Check(o.Thrown is AccessViolationException && o.Told.Count == 0, "6 an edit that changes nothing, and Studio throws: the exception reaches the caller");

        // 7
        d = Doc(2, "Oud."); d.DontApply = true;
        o = Write(d, d.Pairs[0], Dutch);
        Check(o.Thrown != null && o.Thrown.GetType().Name == "SegmentWriteDeclinedException" && o.Told.Count == 0,
            "7 an edit Studio quietly declines is still refused as before");

        // 8: 100x a real long run's worst case.
        ClearLog();
        var longText = new StringBuilder();
        while (longText.Length < 1500) longText.Append("tussen 10 en 90 gew.%, bij voorkeur tussen 20 en 80 gew.%, ");
        d = Doc(2000); d.FaultEvery = 7;
        int thrown = 0, told = 0, wrong = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < d.Pairs.Count; i++)
        {
            var text = longText.ToString() + i;
            o = Write(d, d.Pairs[i], text);
            if (o.Thrown != null) thrown++;
            told += o.Told.Count;
            if (Text(d.Pairs[i].Target) != text) wrong++;
        }
        sw.Stop();
        Check(thrown == 0 && wrong == 0 && told == d.Faults && d.Faults == 2000 / 7,
            "8 2,000 writes of 1,500 characters, " + d.Faults + " thrown on after landing: none lost, none reported as failed, each one told ("
            + sw.ElapsedMilliseconds + " ms, log " + (new FileInfo(LogPath).Length / 1024) + " KB)");

        // 9
        Check(TryAddTcComment != null, "9 the build has TryAddTcComment");
        if (TryAddTcComment != null)
        {
            const string note = "Acme: 'bronzin' is not a Dutch word.";
            d = Doc(2, Dutch);
            bool ok = (bool)TryAddTcComment.Invoke(null, new object[] { d, d.Pairs[0], note });
            Check(ok && Comments(d.Pairs[0].Target) == 1, "9 an ordinary comment add: added");
            ClearLog();
            d = Doc(2, Dutch); d.CommentThrowAfter = true;
            ok = (bool)TryAddTcComment.Invoke(null, new object[] { d, d.Pairs[0], note });
            log = Log();
            Check(ok && Comments(d.Pairs[0].Target) == 1, "9 a comment add that throws AFTER adding counts as added (so it is not written inline too)");
            Check(log.Contains("IS on the segment") && log.Contains("AccessViolationException") && !log.Contains("bronzin"), "9 and is logged with the exception, without the text");
            d = Doc(2, Dutch); d.CommentThrowBefore = true;
            ok = (bool)TryAddTcComment.Invoke(null, new object[] { d, d.Pairs[0], note });
            Check(!ok && Comments(d.Pairs[0].Target) == 0, "9 a comment add that throws without adding: not added (the caller writes it inline, as before)");
            d = Doc(2, Dutch); d.CommentThrowBefore = true; d.Unreadable = true;
            ok = (bool)TryAddTcComment.Invoke(null, new object[] { d, d.Pairs[0], note });
            Check(!ok, "9 and one that throws on a segment that cannot be read back: not added");
        }

        // 10
        AddCommentChecked = swc.GetMethod("AddCommentChecked", Any);
        Check(AddCommentChecked != null, "10 the build has AddCommentChecked");
        if (AddCommentChecked != null)
        {
            const string remark = "Acme: check 'bronzin' against the PROJ-001 drawing.";
            ClearLog();
            d = Doc(2, Dutch);
            var threw = AddComment(d, d.Pairs[0], remark);
            Check(threw == null && Comments(d.Pairs[0].Target) == 1 && !Log().Contains("AddCommentOnSegment"), "10 an ordinary add: added, nothing thrown, nothing logged");

            ClearLog();
            d = Doc(2, Dutch); d.CommentThrowAfter = true;
            threw = AddComment(d, d.Pairs[1], remark);
            log = Log();
            Check(threw == null, "10 Studio adds the comment, then throws: nothing is thrown, so add_comment answers ok" + (threw != null ? " (threw " + threw.GetType().Name + ")" : ""));
            Check(Comments(d.Pairs[1].Target) == 1 && Comments(d.Pairs[0].Target) == 0, "10 the comment is on that segment, once, and on no other");
            Check(log.Contains("System.AccessViolationException") && log.Contains("from AddCommentOnSegment") && log.Contains("IS on the segment"), "10 the log names the exception and says the add stands");
            Check(log.Contains("FakeAddCommentOnSegment"), "10 the log has the stack trace (the throwing method is named)");
            Check(log.Contains(remark.Length + " chars") && log.Contains("segment id 2") && log.Contains("-bit"), "10 the log has the comment's length, the segment id and the bitness");
            Check(!log.Contains("Acme") && !log.Contains("bronzin"), "10 the log does not hold the comment's text");

            ClearLog();
            d = Doc(2, Dutch); d.CommentThrowBefore = true;
            threw = AddComment(d, d.Pairs[0], remark);
            log = Log();
            Check(threw is AccessViolationException && Comments(d.Pairs[0].Target) == 0, "10 Studio throws without adding: the same exception reaches the caller, so add_comment reports the failure as before");
            Check(threw != null && (threw.StackTrace ?? "").Contains("FakeAddCommentOnSegment"), "10 with its original stack trace");
            Check(log.Contains("is NOT on the segment"), "10 and the log says the comment is not there");

            // The same remark is already on the segment (an earlier add): a failed
            // second add must not be taken for a successful one because of it.
            d = Doc(2, Dutch);
            AddComment(d, d.Pairs[0], remark);
            d.CommentThrowBefore = true;
            threw = AddComment(d, d.Pairs[0], remark);
            Check(threw is AccessViolationException && Comments(d.Pairs[0].Target) == 1, "10 throws without adding while the same text is already on the segment: still reported as failed");
            d.CommentThrowBefore = false; d.CommentThrowAfter = true;
            threw = AddComment(d, d.Pairs[0], remark);
            Check(threw == null && Comments(d.Pairs[0].Target) == 2, "10 throws after adding a second comment with the same text: stands");

            d = Doc(2, Dutch); d.CommentThrowAfter = true; d.Unreadable = true;
            threw = AddComment(d, d.Pairs[0], remark);
            Check(threw is AccessViolationException, "10 added, thrown, but unreadable afterwards: not given the benefit of the doubt, the exception reaches the caller");

            ClearLog();
            d = Doc(2000, Dutch); d.CommentFaultEvery = 7;
            int failedAdds = 0, wrongCount = 0;
            var sw2 = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < d.Pairs.Count; i++)
            {
                if (AddComment(d, d.Pairs[i], remark + " " + i) != null) failedAdds++;
                if (Comments(d.Pairs[i].Target) != 1) wrongCount++;
            }
            sw2.Stop();
            Check(failedAdds == 0 && wrongCount == 0 && d.Faults == 2000 / 7,
                "10 2,000 comment adds, " + d.Faults + " thrown on after adding: none reported as failed, each segment has its comment once ("
                + sw2.ElapsedMilliseconds + " ms, log " + (new FileInfo(LogPath).Length / 1024) + " KB)");
        }

        Console.WriteLine(Pass + " passed, " + Fail + " failed");
        return Fail == 0 ? 0 : 1;
    }
}
'@

foreach ($n in @('Sdl.FileTypeSupport.Framework.Core.dll', 'Sdl.Core.Globalization.dll')) { [void]$refs.Add((Join-Path $studio $n)) }
[void]$refs.Add($iface.Assembly.Location)
$all = @($refs) + @('System.dll', 'System.Core.dll', 'System.Windows.Forms.dll', 'System.Drawing.dll')
Add-Type -TypeDefinition ($src + "`n" + $stub) -ReferencedAssemblies $all
try { $code = [WriteFaultTest]::Run($Dll, $logPath) }
catch { $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }; Write-Host $e.ToString(); exit 2 }
exit $code
