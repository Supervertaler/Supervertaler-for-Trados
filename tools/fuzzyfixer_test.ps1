# FuzzyFixer (18.20.208): repair a fuzzy TM match with the AI, preview, Enter writes.
#
# Runs the BUILT plugin's own code outside Studio, on REAL segments from
# Studio's own factory, through a stand-in for Studio's document (generated
# from Studio's IStudioDocument, as tools\write_fault_test.ps1 does) and a
# stand-in AI server on 127.0.0.1 (a free port, taken when the test starts).
# Invented Acme text only. No Studio is started and no real AI request is made.
#
#   A  which match can be repaired: none, below the lowest score, no source,
#      100%, fuzzy, and the 99% of a sentence that differs only in its numbers
#   B  what is sent: the user prompt carries this segment, the memory's source
#      and its translation; the system prompt carries the translator's prompt,
#      the repair instruction and the three segments before and after, with
#      their translations - and never the whole document
#   C  the reply: repaired, unchanged, empty, tags missing / extra / other
#   D  scale: two 5,000-word texts are compared in well under a second
#   E  the window: status, result, problem; closing cancels; a late answer is
#      dropped; disposing it disposes its token source and the token stays
#      readable
#   F  the voice command, the shortcut, and the action in the plugin manifest
#   G  what stops it before anything is searched: no segment, a locked segment,
#      a batch that is running, no AI settings, no profile, no API key
#   H  the memories searched: no project file, every memory switched off or
#      with Lookup unticked, one of two switched off
#   I  the neighbours: three before and three after, fewer at the ends, none
#      when the document cannot be read; 20,000 segments
#   J  the run, against the stand-in AI: a repair; an AI error; an answer that
#      never comes and a window that is closed meanwhile; the match returned
#      unchanged; a reply without the segment's tag; no match; a memory that
#      cannot be opened; and after each failure the next run works
#   K  the write: lands as Draft (also in a confirmed segment); Studio quietly
#      declines; Studio throws before applying; Studio throws AFTER applying
#      (written, and said); the status cannot be set; a tagged repair
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\fuzzyfixer_test.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\fuzzyfixer_test.ps1 -Dll <Supervertaler.Trados.dll>
# A Studio 2024 / 2022 build is 32-bit: the script starts the 32-bit PowerShell
# for it by itself.
#
# What it cannot reach: the key press and the menu item in Studio, reading the
# active segment, its languages and its project from a live document
# (FuzzyFixPrepare), Studio's own ProcessSegmentPair, Track Changes, undo, and
# a real AI's answer. A request that times out by itself (minutes) is not
# waited for: the test closes the window on an answer that never comes.
param(
    [string]$Dll = (Join-Path (Split-Path -Parent $PSScriptRoot) "src\Supervertaler.Trados\bin\Studio19\Debug\Supervertaler.Trados.dll"),
    [string]$Root = (Join-Path $env:TEMP "sv-fuzzyfixer-test"),
    [switch]$Child
)
$ErrorActionPreference = 'Stop'

$Root = [IO.Path]::GetFullPath($Root)
if ($Root -like 'D:\Supervertaler*' -or $Root.Length -lt 10) { throw "Refusing to use $Root as the test root." }
$Dll = [IO.Path]::GetFullPath($Dll)
if (-not (Test-Path -LiteralPath $Dll)) { throw "No plugin build at $Dll" }

if (-not $Child) {
    # Parent: run the cases in a child of the right bitness; guard the real files.
    $real = @('D:\Supervertaler\trados\logs\diagnostic.log', 'D:\Supervertaler\trados\settings\settings.json',
              'D:\Supervertaler\trados\settings\voice_commands.json', 'D:\Supervertaler\settings\api-keys.json')
    function Stamp { ($real | ForEach-Object { if (Test-Path -LiteralPath $_) { $i = Get-Item -LiteralPath $_; "$_|$($i.Length)|$($i.LastWriteTimeUtc.Ticks)" } else { "$_|absent" } }) -join "`n" }
    $before = Stamp
    $ps = if ($Dll -like '*Studio19*') { Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe' }
          else { Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe' }
    Write-Host "Plugin: $Dll"
    & $ps -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -Child -Dll $Dll -Root $Root
    $code = $LASTEXITCODE
    if ((Stamp) -ne $before) { Write-Host "NOTE  a real settings, key or log file changed during the test (a running Studio writes to them too; the test's own files are under $Root)" }
    if ($code -ne 0) { Write-Host "RESULT: FAILED"; exit 1 }
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
public static class FuzzyFixerResolver
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
[FuzzyFixerResolver]::Register(@((Split-Path $Dll), $studio))
Add-Type -AssemblyName System.Windows.Forms
$plugin = [Reflection.Assembly]::LoadFrom($Dll)
$dataRoot = [string](Join-Path $Root 'data')
# The test's own data folder, before anything can log: never the real one.
$plugin.GetType("Supervertaler.Core.SupervertalerPaths").GetMethod("Set").Invoke($null, @($dataRoot))
$logType = $plugin.GetType("Supervertaler.Trados.Core.DiagnosticLog", $true)
$logPath = [string]$logType.GetProperty("LogFilePath").GetValue($null)
if (-not $logPath.StartsWith($Root, [StringComparison]::OrdinalIgnoreCase)) { throw "The diagnostic log would be written to $logPath, outside the test root." }

# The stand-in document: IStudioDocument generated from this Studio's own
# interface, every member throwing except the ones FuzzyFixer's paths use.
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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
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

// One paragraph of segments. ProcessSegmentPair hands the edit a copy of the
// pair and applies its target - as Studio does - and can be told to decline, or
// to throw before or after applying.
public partial class FakeDocument
{
    public IDocumentItemFactory F;
    public List<ISegmentPair> Pairs = new List<ISegmentPair>();
    public IParagraphUnit Pu;

    public bool ThrowAfterApply, ThrowBeforeApply, DontApply, StatusThrows, ListThrows;
    public int Writes, StatusUpdates, Listed;

    ISegmentPair LiveOf(ISegmentPair pair) { return Pairs.First(p => p.Properties.Id.Id == pair.Properties.Id.Id); }
    void FakeProcessSegmentPair(ISegmentPair pair, string undoName, Action<ISegmentPair, CancelEventArgs> edit)
    {
        var live = LiveOf(pair);
        var copy = F.CreateSegmentPair((ISegment)live.Source.Clone(), (ISegment)live.Target.Clone());
        var cancel = new CancelEventArgs();
        edit(copy, cancel);
        if (cancel.Cancel || DontApply) return;
        Writes++;
        if (ThrowBeforeApply) throw new AccessViolationException();
        live.Target.Clear();
        copy.Target.MoveAllItemsTo(live.Target);
        if (ThrowAfterApply) throw new AccessViolationException();
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
    IEnumerable<ISegmentPair> FakeSegmentPairs
    {
        get
        {
            if (ListThrows) throw new InvalidOperationException("fake: the document cannot be read");
            return Walk();
        }
    }
    IEnumerable<ISegmentPair> Walk() { foreach (var p in Pairs) { Listed++; yield return p; } }
}

public static class FuzzyFixerTest
{
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static int Pass, Fail;
    static Assembly P;
    static Type Fixer, View, DlgType, JobType, PlanType;
    static IDocumentItemFactory F;
    static IPropertiesFactory PF;
    static string Root;

    static void Check(bool ok, string what)
    {
        if (ok) Pass++; else Fail++;
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what);
    }
    static object Call(Type t, string name, params object[] args) { return t.GetMethod(name, Any).Invoke(null, args); }
    static void Set(object o, string field, object value) { o.GetType().GetField(field, Any).SetValue(o, value); }
    static object Get(object o, string field) { return o.GetType().GetField(field, Any).GetValue(o); }

    // Invented: a claim of an Acme water heater, an 82% match, and a good repair of it.
    const string Ending = "until the water temperature falls below the first lower limit;";
    const string NewSource = "in response to the water temperature exceeding a second upper limit, switching off, by the control unit, the heating element (12) after a set time has passed, and keeping, by the control unit, the heating element (12) switched off " + Ending;
    const string MatchSource = "in response to the water temperature exceeding the second upper limit, switch off the heating element (12) after a set time has passed, wherein the heating element (12) is configured to stay switched off " + Ending;
    const string NlEnding = "totdat de watertemperatuur onder de eerste ondergrens daalt;";
    const string MatchTarget = "als reactie op het overschrijden van de tweede bovengrens door de watertemperatuur, het verwarmingselement (12) uit te schakelen nadat een ingestelde tijd is verstreken, waarbij het verwarmingselement (12) is ingericht om uitgeschakeld te blijven " + NlEnding;
    const string Repair = "als reactie op het overschrijden van een tweede bovengrens door de watertemperatuur, het uitschakelen, door de regeleenheid, van het verwarmingselement (12) nadat een ingestelde tijd is verstreken, en het, door de regeleenheid, uitgeschakeld houden van het verwarmingselement (12) " + NlEnding;

    static string Verdict(string reply, string matchTarget, string source, out string detail)
    {
        var args = new object[] { reply, matchTarget, source, null };
        var v = Fixer.GetMethod("JudgeReply", Any).Invoke(null, args);
        detail = (string)args[3];
        return v.ToString();
    }
    static string Verdict(string reply, string matchTarget, string source) { string d; return Verdict(reply, matchTarget, source, out d); }

    // TokenDiff.Piece is internal: read through ToString(), "Op[Text]".
    static List<KeyValuePair<string, string>> Changes(string matchTarget, string reply)
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (var piece in (IEnumerable)Call(Fixer, "Changes", matchTarget, reply))
        {
            var s = piece.ToString(); int i = s.IndexOf('[');
            list.Add(new KeyValuePair<string, string>(s.Substring(0, i), s.Substring(i + 1, s.Length - i - 2)));
        }
        return list;
    }

    // ---- real segments -------------------------------------------------------

    static IText T(string s) { return F.CreateText(PF.CreateTextProperties(s)); }
    static FakeDocument Doc(int segments, Func<int, string> target = null)
    {
        var pu = F.CreateParagraphUnit(LockTypeFlags.Unlocked);
        pu.Properties.ParagraphUnitId = new ParagraphUnitId("pu-1");
        var d = new FakeDocument { F = F, Pu = pu };
        for (int i = 1; i <= segments; i++)
        {
            var props = F.CreateSegmentPairProperties();
            props.Id = new SegmentId(i.ToString());
            var source = F.CreateSegment(props);
            source.Add(T("Acme source " + i + "."));
            var t = F.CreateSegment(props);
            var text = target == null ? "" : target(i);
            if (!string.IsNullOrEmpty(text)) t.Add(T(text));
            d.Pairs.Add(F.CreateSegmentPair(source, t));
        }
        return d;
    }
    static string Text(IAbstractMarkupDataContainer c)
    {
        var sb = new StringBuilder();
        foreach (var item in c)
        {
            if (item is IText) sb.Append(((IText)item).Properties.Text);
            else if (item is IAbstractMarkupDataContainer) sb.Append(Text((IAbstractMarkupDataContainer)item));
        }
        return sb.ToString();
    }
    static int Placeholders(IAbstractMarkupDataContainer c)
    {
        int n = 0;
        foreach (var item in c)
        {
            if (item is IPlaceholderTag) n++;
            if (item is IAbstractMarkupDataContainer) n += Placeholders((IAbstractMarkupDataContainer)item);
        }
        return n;
    }

    // ---- the stand-in AI -----------------------------------------------------

    sealed class Request { public string System, User; }
    static HttpListener _listener;
    static string _endpoint;
    static readonly List<Request> Requests = new List<Request>();
    static volatile string _answer;          // what the AI says for segment 1
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
                        { "message", new Dictionary<string, object> { { "role", "assistant" }, { "content", "1. " + _answer } } },
                        { "finish_reason", "stop" } } } },
                    // As many prompt tokens as were sent: the translator refuses an answer
                    // from a model that read only part of its prompt.
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
        // On, to show that FuzzyFixer does not send the document whatever this says.
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
        return (Form)Activator.CreateInstance(DlgType, new object[] { "Segment 12", note, start });
    }
    static object Field(Form f, string name) { return DlgType.GetField(name, Any).GetValue(f); }
    static void Say(Form f, string name, params object[] args) { DlgType.GetMethod(name, Any).Invoke(f, args); }
    static CancellationToken TokenOf(Form f) { return (CancellationToken)DlgType.GetProperty("Token").GetValue(f, null); }
    static bool SourceDisposed(Form f)
    {
        var cts = (CancellationTokenSource)Field(f, "_cts");
        try { var unused = cts.Token; return false; } catch (ObjectDisposedException) { return true; }
    }

    static object Plan(List<string> tms)
    {
        var plan = Activator.CreateInstance(PlanType, true);
        Set(plan, "Tms", tms); Set(plan, "SourceCulture", new CultureInfo("en-GB")); Set(plan, "MinScore", 70);
        Set(plan, "EverTm", false); Set(plan, "TargetCulture", "nl-NL");
        return plan;
    }
    const string OwnPrompt = "Translate Acme manuals formally.";
    static object Job(string source, string lead, List<string> tms = null)
    {
        var seg = new BatchSegment { Index = 0, Number = "12", SourceText = source, ExistingTarget = "" };
        var job = Activator.CreateInstance(JobType, true);
        Set(job, "Ai", Ai()); Set(job, "SourceLang", "English"); Set(job, "TargetLang", "Dutch");
        Set(job, "Segment", seg); Set(job, "Lookup", Plan(tms ?? new List<string>()));
        Set(job, "Terms", new List<TermEntry>()); Set(job, "Instructions", OwnPrompt); Set(job, "Lead", lead);
        Set(job, "Structure", Enum.Parse(JobType.GetField("Structure", Any).FieldType, "Off"));
        return job;
    }
    static object Match(string source, string target, int score)
    {
        var t = P.GetType("Supervertaler.Trados.Core.TmFuzzyLookup+Match", true);
        var m = Activator.CreateInstance(t, true);
        Set(m, "SourceText", source); Set(m, "TargetText", target); Set(m, "Score", score); Set(m, "TmName", "Acme EN-NL");
        return m;
    }

    sealed class RunResult
    {
        public BatchSegmentResultEventArgs Repair;
        public string Header, Body; public bool CanWrite, TimedOut, Threw, Disposed, Cancelled;
        public long Ms;
    }

    // One use, as HandleFuzzyFixActiveSegment makes it but for the modal loop:
    // the window on screen, the work on a pool thread, the window disposed at
    // the end. method: "FuzzyFixRun" (the search first) or "FuzzyFixRepair"
    // (with the match handed in). closeAfterRequest: close the window as soon
    // as the AI has the request.
    static RunResult Run(object job, object match, string method, bool closeAfterRequest = false)
    {
        var r = new RunResult();
        Action<BatchSegmentResultEventArgs> done = e => r.Repair = e;
        Task task = null;
        int requestsBefore = RequestCount();
        var f = Open(d =>
        {
            task = Task.Run(() =>
            {
                var args = method == "FuzzyFixRun" ? new object[] { job, d, done } : new object[] { job, match, d, done };
                return (Task)View.GetMethod(method, Any).Invoke(null, args);
            });
        }, null);
        var sw = Stopwatch.StartNew();
        f.Show();
        bool closed = false;
        while (sw.ElapsedMilliseconds < 30000)
        {
            Application.DoEvents(); Thread.Sleep(5);
            if (closeAfterRequest && !closed && RequestCount() > requestsBefore) { f.Close(); closed = true; }
            if (task != null && task.IsCompleted) break;
        }
        r.Ms = sw.ElapsedMilliseconds;
        r.TimedOut = task == null || !task.IsCompleted;
        r.Threw = task != null && task.IsFaulted;
        Pump(150);   // what the work posted to the window
        if (!closed)
        {
            r.Header = ((Label)Field(f, "_header")).Text;
            r.Body = ((RichTextBox)Field(f, "_text")).Text;
            r.CanWrite = ((Button)Field(f, "_write")).Enabled;
            f.Close();
        }
        f.Dispose();
        r.Disposed = SourceDisposed(f);
        r.Cancelled = TokenOf(f).IsCancellationRequested;
        return r;
    }

    // ---- the write -----------------------------------------------------------

    static BatchSegmentResultEventArgs Result(string translation)
    {
        return new BatchSegmentResultEventArgs { SegmentIndex = 0, SegmentName = "segment 1", Translation = translation };
    }
    sealed class WriteOutcome { public bool Ok; public string Why, Note; }
    static WriteOutcome Write(FakeDocument d, ISegmentPair pair, BatchSegmentResultEventArgs repair)
    {
        var args = new object[] { d, pair, repair, null, null };
        var o = new WriteOutcome();
        o.Ok = (bool)View.GetMethod("FuzzyFixWrite", Any).Invoke(null, args);
        o.Why = (string)args[3]; o.Note = (string)args[4];
        return o;
    }

    static string Refusal(ISegmentPair pair, bool batchRunning, AiSettings ai)
    {
        return (string)View.GetMethod("FuzzyFixRefusal", Any).Invoke(null, new object[] { pair, batchRunning, ai, null });
    }

    static void Neighbours(FakeDocument d, ISegmentPair pair, out IList before, out IList after)
    {
        var args = new object[] { d, pair, null, null };
        View.GetMethod("FuzzyFixNeighbours", Any).Invoke(null, args);
        before = (IList)args[2]; after = (IList)args[3];
    }
    static string Numbers(IList list) { return string.Join(",", list.Cast<object>().Select(n => (string)Get(n, "Number"))); }

    public static int Run(string pluginPath, string root, string manifestPath)
    {
        P = Assembly.LoadFrom(pluginPath);
        Root = root;
        Fixer = P.GetType("Supervertaler.Trados.Core.FuzzyFixer", true);
        View = P.GetType("Supervertaler.Trados.AiAssistantViewPart", true);
        DlgType = P.GetType("Supervertaler.Trados.Controls.FuzzyFixPreviewDialog", true);
        JobType = View.GetNestedType("FuzzyFixJob", Any);
        PlanType = View.GetNestedType("TmLookupPlan", Any);
        F = (IDocumentItemFactory)Activator.CreateInstance(Type.GetType(
            "Sdl.FileTypeSupport.Framework.Bilingual.DocumentItemFactory, Sdl.FileTypeSupport.Framework.Implementation", true));
        PF = (IPropertiesFactory)Activator.CreateInstance(Type.GetType(
            "Sdl.FileTypeSupport.Framework.Native.PropertiesFactory, Sdl.FileTypeSupport.Framework.Implementation", true));
        F.PropertiesFactory = PF;
        Check(SupervertalerPaths.Root.StartsWith(root, StringComparison.OrdinalIgnoreCase), "the data folder is the test's own");

        Console.WriteLine("A  which match can be repaired");
        Check(Call(Fixer, "JudgeMatch", null, null, 0, 70).ToString() == "NoMatch", "A no match at all: nothing to repair");
        Check(Call(Fixer, "JudgeMatch", MatchSource, MatchTarget, 69, 70).ToString() == "NoMatch", "A 69% with the lowest score at 70: nothing to repair");
        Check(Call(Fixer, "JudgeMatch", "", MatchTarget, 82, 70).ToString() == "NoMatch", "A a match without its source is not repaired (the model could not see what differs)");
        Check(Call(Fixer, "JudgeMatch", MatchSource, "  ", 82, 70).ToString() == "NoMatch", "A a match without a translation is not repaired");
        Check(Call(Fixer, "JudgeMatch", MatchSource, MatchTarget, 100, 70).ToString() == "Exact", "A 100%: nothing to repair");
        Check(Call(Fixer, "JudgeMatch", MatchSource, MatchTarget, 82, 70).ToString() == "Repairable", "A 82%: repairable");
        Check(Call(Fixer, "JudgeMatch", MatchSource, MatchTarget, 70, 70).ToString() == "Repairable", "A exactly the lowest score: repairable");
        Check(Call(Fixer, "JudgeMatch", "Acme valve 12 opens.", "Acme-klep 12 gaat open.", 99, 70).ToString() == "Repairable",
            "A 99%, the score of a sentence that differs only in its numbers: repairable");
        var verdictType = Fixer.GetNestedType("MatchVerdict", Any);
        var noMatch = (string)Call(Fixer, "MatchProblem", Enum.Parse(verdictType, "NoMatch"), 70, null);
        var exact = (string)Call(Fixer, "MatchProblem", Enum.Parse(verdictType, "Exact"), 70, "Acme EN-NL");
        Check(noMatch != null && noMatch.Contains("70%") && noMatch.Contains("nothing to repair"), "A no match: the message names the lowest score");
        Check(exact != null && exact.Contains("100%") && exact.Contains("Acme EN-NL"), "A 100%: the message names the memory");
        Check(Call(Fixer, "MatchProblem", Enum.Parse(verdictType, "Repairable"), 70, null) == null, "A repairable: no message");
        Check((int)Call(Fixer, "MinScore", 0) == 70 && (int)Call(Fixer, "MinScore", 101) == 70 && (int)Call(Fixer, "MinScore", 85) == 85,
            "A an unusable stored lowest score reads as 70, a usable one as itself");

        Console.WriteLine("B  what is sent");
        var segment = new BatchSegment { Index = 0, Number = "12", SourceText = NewSource, ExistingTarget = "",
            TmSourceText = MatchSource, TmTargetText = MatchTarget, TmMatchPercent = 82 };
        var input = (BatchSegmentInput)Call(typeof(BatchTranslator), "ToPromptInput", segment, 1, StructureContextMode.Off);
        var user = (string)Call(typeof(BatchTranslator), "UserPromptFor", 1, new List<TermEntry>(), true, new List<BatchSegmentInput> { input });
        Check(user.Contains("82% match"), "B user prompt: the score");
        Check(user.Contains("this segment:") && user.Contains(NewSource), "B user prompt: this segment's source, beside the match");
        Check(user.Contains("source in memory:") && user.Contains(MatchSource), "B user prompt: the sentence the match was made for");
        Check(user.Contains("translation in memory:") && user.Contains(MatchTarget), "B user prompt: the match's translation");
        var instruction = (string)Fixer.GetField("RepairInstruction", Any).GetValue(null);
        Check(instruction.Contains("translation in memory") && instruction.Contains("source in memory") && instruction.Contains("this segment"),
            "B the instruction uses the labels the memory block uses");

        var nType = Fixer.GetNestedType("Neighbour", Any);
        Func<string, string, string, object> N = (num, s, t) => { var n = Activator.CreateInstance(nType, true); Set(n, "Number", num); Set(n, "Source", s); Set(n, "Target", t); return n; };
        Func<object[], IList> L = items => { var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(nType)); foreach (var i in items) list.Add(i); return list; };
        var block = (string)Call(Fixer, "NeighboursBlock",
            L(new[] { N("10", "The Acme heater has a <t1>tank</t1>.", "De Acme-boiler heeft een tank."), N("11", "It holds 80 litres.", "") }),
            L(new[] { N("13", "The valve then closes.", null) }));
        Check(block.Contains("segment 10 source: The Acme heater has a tank.") && block.Contains("translation: De Acme-boiler heeft een tank."),
            "B the neighbours: a translated one goes with its translation, without tag markers");
        Check(block.Contains("segment 11 source: It holds 80 litres.") && block.Contains("segment 13 source: The valve then closes.")
            && block.Split('\n').Count(l => l.Contains("translation:")) == 1, "B an untranslated neighbour goes with its source alone");
        Check(block.IndexOf("Before this segment:") < block.IndexOf("segment 10") && block.IndexOf("After this segment:") > block.IndexOf("segment 11")
            && block.IndexOf("After this segment:") < block.IndexOf("segment 13"), "B before and after are told apart");
        Check(Call(Fixer, "NeighboursBlock", L(new object[0]), L(new object[0])) == null && Call(Fixer, "NeighboursBlock", null, null) == null,
            "B a one-segment document: no neighbours section at all");
        Check((int)Fixer.GetField("NeighbourCount", Any).GetValue(null) == 3, "B three segments before and three after");

        var lead = (string)Call(Fixer, "RepairLead", block);
        Check(lead.StartsWith(instruction) && lead.EndsWith(block), "B the lead of a repair: the repair instruction, then the neighbours");
        Check((string)Call(Fixer, "RepairLead", new object[] { null }) == instruction && (string)Call(Fixer, "RepairLead", " ") == instruction,
            "B no neighbours: the repair instruction alone");
        var system = (string)Call(typeof(BatchTranslator), "SystemPromptFor", 1, "English", "Dutch", OwnPrompt, new List<TermEntry>(), null,
            null, 500, true, "# SUPERMEMORY\nAcme house rules.", StructureContextMode.Off);
        Check(system.Contains(OwnPrompt) && system.Contains("Acme house rules.") && !system.Contains(instruction) && !system.Contains("segment 13 source:"),
            "B system prompt: the translator's prompt and the bank, and nothing that differs from one repair to the next");
        Check(!system.Contains("# DOCUMENT CONTENT"), "B system prompt: no document section");
        var user = (string)Call(typeof(BatchTranslator), "WithLead", lead, "**SEGMENTS TO TRANSLATE**");
        Check(user.StartsWith(instruction) && user.IndexOf("segment 13 source:") < user.IndexOf("**SEGMENTS TO TRANSLATE**"),
            "B user prompt: the repair instruction and the neighbours first, then the segment");
        Check((string)Call(typeof(BatchTranslator), "WithLead", null, "x") == "x" && (string)Call(typeof(BatchTranslator), "WithLead", "  ", "x") == "x",
            "B no lead (Alt+T, Batch Translate): the user prompt as it was");

        Console.WriteLine("C  the reply");
        Check(Verdict(Repair, MatchTarget, NewSource) == "Repaired", "C a real repair: repaired");
        var changes = Changes(MatchTarget, Repair);
        var oldText = string.Concat(changes.Where(c => c.Key != "Insert").Select(c => c.Value));
        var newText = string.Concat(changes.Where(c => c.Key != "Delete").Select(c => c.Value));
        Check(oldText == MatchTarget && newText == Repair, "C the changes add up to exactly the match's translation and the repair");
        Check(changes.Any(c => c.Key == "Delete") && changes.Any(c => c.Key == "Insert"), "C it has removed and added words");
        Check(changes.First().Key == "Equal" && changes.Last().Key == "Equal" && changes.Last().Value.Contains("ondergrens daalt;"),
            "C the unchanged start and ending are shown as unchanged");
        Check(Verdict(MatchTarget, MatchTarget, NewSource) == "Unchanged", "C failure: the match returned as it was -> unchanged, nothing to write");
        Check(Verdict("  " + MatchTarget.Replace(", ", ",  ") + "\r\n", MatchTarget, NewSource) == "Unchanged", "C failure: the same but for spacing -> unchanged");
        Check(Verdict(null, MatchTarget, NewSource) == "Empty" && Verdict("   ", MatchTarget, NewSource) == "Empty", "C failure: no reply -> empty");
        Check(Verdict("<t1></t1>", MatchTarget, "<t1>" + NewSource + "</t1>") == "Empty", "C failure: a reply of tags alone -> empty");
        string detail;
        var tagged = "<t1>switching off</t1> the element<t2/>";
        Check(Verdict("het uitschakelen van het element", "het element uitschakelen", tagged, out detail) == "TagsDiffer" && detail.Contains("3 tag markers") && detail.Contains("0 tag markers"),
            "C failure: the segment has tags and the reply has none -> not written (" + detail + ")");
        Check(Verdict("<t1>het uitschakelen</t1> van het element<t2/><t3/>", "x", tagged, out detail) == "TagsDiffer", "C failure: an extra tag -> not written");
        Check(Verdict("<t1>het uitschakelen</t1> van het element<t4/>", "x", tagged, out detail) == "TagsDiffer" && detail.Contains("not the same ones"),
            "C failure: the right number of tags but another tag -> not written (" + detail + ")");
        Check(Verdict("<t2/>van het element <t1>het uitschakelen</t1>", "het element uitschakelen", tagged) == "Repaired", "C the segment's tags in another order: accepted");
        Check(Verdict("<t1>het element</t1> uitschakelen<t2/>", "het element uitschakelen", tagged) == "Unchanged",
            "C the match's words with only the tags added: unchanged");

        Console.WriteLine("D  scale");
        var sb1 = new StringBuilder(); var sb2 = new StringBuilder();
        for (int i = 0; i < 5000; i++) { sb1.Append("woord").Append(i % 97).Append(' '); sb2.Append(i % 13 == 0 ? "ander" : "woord").Append(i % 97).Append(' '); }
        var sw = Stopwatch.StartNew();
        var big = Changes(sb1.ToString(), sb2.ToString());
        sw.Stop();
        Check(sw.ElapsedMilliseconds < 3000, "D two 5,000-word texts compared in " + sw.ElapsedMilliseconds + " ms");
        Check(string.Concat(big.Where(c => c.Key != "Delete").Select(c => c.Value)) == sb2.ToString().Trim(), "D and the result is still exact");

        Console.WriteLine("E  the window");
        {
            int started = 0;
            var f = Open(d => started++, "This segment is already confirmed. Writing the repair replaces its translation.");
            var write = (Button)Field(f, "_write"); var text = (RichTextBox)Field(f, "_text"); var header = (Label)Field(f, "_header");
            var token = TokenOf(f);
            f.Show(); Pump(200);
            Check(started == 1, "E the work is started once, when the window is on screen");
            Check(f.Text.Contains("FuzzyFixer") && f.Text.Contains("Segment 12"), "E the title names the segment (" + f.Text + ")");
            Check(!write.Enabled, "E while working, Write to target is disabled, so Enter writes nothing");
            Check(((Label)Field(f, "_note")).Visible, "E a confirmed segment is said to be confirmed");
            // From another thread, as the real work does.
            var t = new Thread(() => Say(f, "ShowStatus", "82% match from Acme EN-NL. Asking the AI...", MatchTarget));
            t.Start(); t.Join(); Pump(200);
            Check(header.Text.StartsWith("82% match") && text.Text == MatchTarget && !write.Enabled, "E status from a background thread: shown, with the match's translation; still nothing to write");
            t = new Thread(() => Say(f, "ShowResult", "82% match, repaired.", Call(Fixer, "Changes", MatchTarget, Repair)));
            t.Start(); t.Join(); Pump(200);
            Check(write.Enabled && f.AcceptButton == write, "E result: Write to target is enabled and is what Enter presses");
            Check(text.Text.Contains("het uitschakelen, door de regeleenheid,") && text.Text.Contains("uit te schakelen"),
                "E the window shows both the added and the removed words");
            text.Select(text.Text.IndexOf("uit te schakelen"), 5);
            Check(text.SelectionFont != null && text.SelectionFont.Strikeout, "E removed words are struck through");
            text.Select(text.Text.IndexOf("door de regeleenheid, van"), 5);
            Check(text.SelectionFont != null && text.SelectionFont.Underline, "E added words are underlined");
            Check(!token.IsCancellationRequested, "E not cancelled while open");
            f.Close(); Pump(100);
            Check(token.IsCancellationRequested, "E closing the window cancels the work");
            bool threw = false;
            try
            {
                t = new Thread(() => { try { Say(f, "ShowResult", "late", Call(Fixer, "Changes", "a", "b")); Say(f, "ShowProblem", "late"); } catch { threw = true; } });
                t.Start(); t.Join(); Pump(100);
            }
            catch { threw = true; }
            Check(!threw, "E failure: an answer that arrives after the window closed is dropped, without an error");
            f.Dispose();
            bool readable = true;
            try { readable = TokenOf(f).IsCancellationRequested; } catch { readable = false; }
            Check(SourceDisposed(f) && readable, "E disposing the window disposes its token source; the token it gave out still reads as cancelled");
        }
        {
            var f = Open(d => { }, null);
            var write = (Button)Field(f, "_write"); var text = (RichTextBox)Field(f, "_text");
            f.Show(); Pump(100);
            Check(!((Label)Field(f, "_note")).Visible, "E a draft segment: no warning line");
            Say(f, "ShowProblem", "No translation memory has a match of 70% or higher."); Pump(100);
            Check(!write.Enabled && text.Text.Contains("70% or higher") && ((Label)Field(f, "_header")).Text == "Nothing was written.",
                "E failure: a problem is shown in the window and nothing can be written");
            Check(((Button)Field(f, "_discard")).Text == "Close", "E and the only button left is Close");
            f.Close(); f.Dispose();
        }
        {
            var f = Open(d => { throw new InvalidOperationException("boom"); }, null);
            f.Show(); Pump(100);
            Check(((RichTextBox)Field(f, "_text")).Text.Contains("boom") && !((Button)Field(f, "_write")).Enabled,
                "E failure: work that cannot even start is reported in the window, not thrown at Studio");
            f.Close(); f.Dispose();
        }
        {
            // Never shown (the handler's using block when ShowDialog itself fails).
            var f = Open(d => { }, null);
            var token = TokenOf(f);
            f.Dispose();
            Check(SourceDisposed(f) && token.IsCancellationRequested, "E failure: a window disposed without ever being shown leaves no live token source");
        }

        Console.WriteLine("F  voice command, shortcut, manifest");
        var voice = P.GetType("Supervertaler.Trados.VoiceControl.VoiceCommandSet", true);
        var defaults = (IEnumerable)voice.GetMethod("Defaults", Any).Invoke(null, null);
        object repairCmd = null;
        foreach (var c in defaults) if ((string)c.GetType().GetProperty("Phrase").GetValue(c, null) == "repair match") repairCmd = c;
        Check(repairCmd != null && (string)repairCmd.GetType().GetProperty("Action").GetValue(repairCmd, null) == "ctrl+alt+f",
            "F the built-in voice command \"repair match\" presses Ctrl+Alt+F");
        var current = (int)voice.GetField("CurrentDefaultsVersion", Any).GetValue(null);
        Check(repairCmd != null && (int)repairCmd.GetType().GetProperty("AddedIn", Any).GetValue(repairCmd, null) == current,
            "F it is marked as new in defaults version " + current + ", so existing users get it too");

        const int CtrlAltF = (int)(Keys.Control | Keys.Alt | Keys.F);
        var owners = new List<string>();
        Type[] types; try { types = P.GetTypes(); } catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(x => x != null).ToArray(); }
        foreach (var type in types)
            foreach (var a in CustomAttributeData.GetCustomAttributes(type))
                if (a.AttributeType.Name == "ShortcutAttribute")
                    foreach (var arg in a.ConstructorArguments)
                        if (Convert.ToInt32(arg.Value) == CtrlAltF) owners.Add(type.Name);
        Check(owners.Count == 1 && owners[0] == "FuzzyFixActiveSegmentAction",
            "F Ctrl+Alt+F belongs to FuzzyFixActiveSegmentAction and to no other Supervertaler action (" + string.Join(", ", owners) + ")");
        // Studio registers actions and shortcuts from the manifest, not from the attributes.
        var manifest = File.ReadAllText(manifestPath, Encoding.Unicode);
        int at = manifest.IndexOf("<extension type=\"Supervertaler.Trados.FuzzyFixActiveSegmentAction,", StringComparison.Ordinal);
        var entry = at < 0 ? "" : manifest.Substring(at, manifest.IndexOf("</extension>", at, StringComparison.Ordinal) - at);
        Check(entry.Contains("Supervertaler_FuzzyFixActiveSegment") && entry.Contains(">" + CtrlAltF + "<") && entry.Contains("EditorDocumentContextMenuLocation"),
            "F the manifest registers the action, its place in the Editor's right-click menu and Ctrl+Alt+F (" + CtrlAltF + ")");
        Check(manifest.Split(new[] { ">" + CtrlAltF + "<" }, StringSplitOptions.None).Length == 2, "F and no other manifest entry claims that key");

        Console.WriteLine("G  what stops it before anything is searched");
        StartServer();
        {
            var d = Doc(2);
            Check(Refusal(null, false, Ai()) == "No active segment.", "G failure: no active segment");
            d.Pairs[0].Properties.IsLocked = true;
            var locked = Refusal(d.Pairs[0], false, Ai());
            Check(locked != null && locked.Contains("locked"), "G failure: a locked segment is left alone (" + locked + ")");
            var busy = Refusal(d.Pairs[1], true, Ai());
            Check(busy != null && busy.Contains("batch translation is running"), "G failure: a batch is running");
            var none = Refusal(d.Pairs[1], false, null);
            Check(none != null && none.Contains("AI settings not configured"), "G failure: no AI settings");
            var noProfile = new AiSettings { SelectedProvider = "custom_openai" };
            Check((Refusal(d.Pairs[1], false, noProfile) ?? "").Contains("No custom OpenAI profile"), "G failure: no profile for the chosen provider");
            var noKey = Ai(); noKey.CustomOpenAiProfiles[0].ApiKey = "";
            var keyProblem = Refusal(d.Pairs[1], false, noKey);
            Check(!string.IsNullOrWhiteSpace(keyProblem), "G failure: no API key (" + (keyProblem ?? "").Replace("\r", " ").Replace("\n", " ") + ")");
            Check(Refusal(d.Pairs[1], false, Ai()) == null, "G an unlocked segment, no batch, a key: it starts");
            Check(RequestCount() == 0 && d.Writes == 0 && d.Listed == 0, "G none of these sent a request, wrote, or read the document");
        }

        Console.WriteLine("H  the memories searched");
        {
            Func<string, string, object[]> lookup = (anchor, target) =>
            {
                var args = new object[] { anchor, new CultureInfo("en-GB"), target, 70, null };
                var plan = View.GetMethod("FuzzyFixLookup", Any).Invoke(null, args);
                return new[] { plan, args[4] };
            };
            var empty = Path.Combine(Root, "no-project"); Directory.CreateDirectory(empty);
            var r0 = lookup(Path.Combine(empty, "Acme.docx.sdlxliff"), "nl-NL");
            Check(r0[0] == null && ((string)r0[1]).Contains("found no translation memory"), "H failure: no project file -> said, nothing searched");

            Func<string, string, string> project = (name, entries) =>
            {
                var dir = Path.Combine(Root, name); Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "A.sdltm"), "not a memory"); File.WriteAllText(Path.Combine(dir, "B.sdltm"), "not a memory");
                var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><Project Version=\"4.0.0.0\"><LanguageDirections>"
                    + "<LanguageDirection TargetLanguageCode=\"nl-NL\" SourceLanguageCode=\"en-GB\"><CascadeItem OverrideParent=\"false\" /></LanguageDirection>"
                    + "</LanguageDirections><CascadeItem OverrideParent=\"true\">" + entries.Replace("{dir}", dir.Replace('\\', '/'))
                    + "</CascadeItem><GeneralProjectInfo Name=\"PROJ-001\" /></Project>";
                var path = Path.Combine(dir, "PROJ-001.sdlproj");
                File.WriteAllText(path, xml);
                return path;
            };
            const string EntryXml = "<CascadeEntryItem PerformNormalSearch=\"{1}\"><MainTranslationProviderItem Uri=\"sdltm.file:///{{dir}}/{0}.sdltm\" Enabled=\"{2}\" /></CascadeEntryItem>";
            var allOff = lookup(project("all-off", string.Format(EntryXml, "A", "true", "false") + string.Format(EntryXml, "B", "false", "true")), "nl-NL");
            Check(allOff[0] == null && ((string)allOff[1]).Contains("All 2") && ((string)allOff[1]).Contains("switched off or have Lookup unticked"),
                "H failure: every memory switched off or with Lookup unticked -> said, nothing searched (" + allOff[1] + ")");
            var oneOff = lookup(project("one-off", string.Format(EntryXml, "A", "true", "true") + string.Format(EntryXml, "B", "true", "false")), "nl-NL");
            var tms = oneOff[0] == null ? null : (List<string>)Get(oneOff[0], "Tms");
            Check(tms != null && tms.Count == 1 && tms[0].EndsWith("A.sdltm", StringComparison.OrdinalIgnoreCase) && oneOff[1] == null,
                "H one of two memories switched off: only the other is searched");
            Check(oneOff[0] != null && ((string)Get(oneOff[0], "SkippedNote") ?? "").Contains("1 of 2"), "H and the log line says one was left out");
            Check((string)Call(Fixer, "NoMemoryProblem", 1) != null && ((string)Call(Fixer, "NoMemoryProblem", 1)).Contains("translation memory is switched off"),
                "H one memory, switched off: the message is in the singular");
        }

        Console.WriteLine("I  the neighbours");
        {
            IList before, after;
            var d = Doc(10, i => i <= 5 ? "Acme doel " + i + "." : "");
            Neighbours(d, d.Pairs[4], out before, out after);
            Check(Numbers(before) == "2,3,4" && Numbers(after) == "6,7,8", "I segment 5 of 10: segments 2-4 before and 6-8 after (" + Numbers(before) + " | " + Numbers(after) + ")");
            Check((string)Get(before[2], "Source") == "Acme source 4." && (string)Get(before[2], "Target") == "Acme doel 4."
                && string.IsNullOrEmpty((string)Get(after[0], "Target")), "I a translated neighbour carries its translation, an untranslated one none");
            Check(d.Listed == 8, "I the walk stops after the last neighbour (" + d.Listed + " of 10 segments read)");
            Neighbours(d, d.Pairs[0], out before, out after);
            Check(before.Count == 0 && Numbers(after) == "2,3,4", "I the first segment: none before, three after");
            Neighbours(d, d.Pairs[9], out before, out after);
            Check(Numbers(before) == "7,8,9" && after.Count == 0, "I the last segment: three before, none after");
            Neighbours(d, d.Pairs[1], out before, out after);
            Check(Numbers(before) == "1" && Numbers(after) == "3,4,5", "I the second segment: one before");
            var one = Doc(1);
            Neighbours(one, one.Pairs[0], out before, out after);
            Check(before.Count == 0 && after.Count == 0, "I a one-segment document: none");
            var stranger = Doc(1).Pairs[0]; stranger.Properties.Id = new SegmentId("999");
            Neighbours(d, stranger, out before, out after);
            Check(before.Count == 0 && after.Count == 0, "I failure: a segment that is not in the document gets no neighbours (not the document's last three)");
            d.ListThrows = true;
            Neighbours(d, d.Pairs[4], out before, out after);
            Check(before.Count == 0 && after.Count == 0, "I failure: a document that cannot be read gives none, and nothing is thrown");

            var large = Doc(20000, i => "Acme doel " + i + ".");
            sw = Stopwatch.StartNew();
            Neighbours(large, large.Pairs[19000], out before, out after);
            sw.Stop();
            Check(Numbers(before) == "18998,18999,19000" && Numbers(after) == "19002,19003,19004" && sw.ElapsedMilliseconds < 5000,
                "I scale: segment 19,001 of 20,000 in " + sw.ElapsedMilliseconds + " ms");
        }

        Console.WriteLine("J  the run");
        {
            var instructions = (string)Call(Fixer, "RepairLead", block);

            _answer = Repair; _fail = false; _hold = null;
            int n0 = RequestCount();
            var r = Run(Job(NewSource, instructions), Match(MatchSource, MatchTarget, 82), "FuzzyFixRepair");
            Check(!r.TimedOut && !r.Threw && r.Repair != null && r.Repair.Translation == Repair, "J a repair comes back and is handed over for writing" + (r.Repair == null ? " [" + r.Header + " | " + (r.Body ?? "").Replace('\n', ' ') + "]" : ""));
            Check(r.CanWrite && r.Header.Contains("82% match") && r.Body.Contains("door de regeleenheid"), "J the window shows it and Enter can write it");
            Check(RequestCount() == n0 + 1, "J one AI request per repair");
            var sent = LastRequest();
            Check(sent.System.Contains(OwnPrompt) && sent.User.Contains("## Fuzzy match repair"), "J the request carried the translator's prompt and the repair instruction");
            Check(sent.User.Contains("## Surrounding segments") && sent.User.Contains("segment 10 source:") && sent.User.Contains("segment 13 source:"),
                "J and the neighbours");
            Check(!sent.System.Contains("## Fuzzy match repair") && !sent.System.Contains("## Surrounding segments")
                  && sent.User.IndexOf("## Surrounding segments") < sent.User.IndexOf(NewSource),
                "J the instruction and the neighbours open the user prompt; the system prompt has neither");
            {
                // Another segment, other neighbours: the system prompt must be the same bytes, or the
                // provider's cache never serves the bank and every repair pays for it again.
                var otherLead = (string)Call(Fixer, "RepairLead", "## Surrounding segments (context only)\n- segment 77 source: Another Acme sentence.");
                var r2 = Run(Job("The Acme pump starts.", otherLead), Match("The Acme pump stops.", "De Acme-pomp stopt.", 85), "FuzzyFixRepair");
                var sent2 = LastRequest();
                Check(sent2.System == sent.System && sent2.User != sent.User && sent2.User.Contains("segment 77 source:"),
                    "J a repair of another segment sends the same system prompt, byte for byte (" + sent.System.Length + " chars): only the user prompt differs");
                n0++;
            }
            Check(!sent.System.Contains("# DOCUMENT CONTENT") && !sent.User.Contains("# DOCUMENT CONTENT"),
                "J and not the document, although \"Include document context\" is ticked in these settings");
            Check(sent.User.Contains(MatchSource) && sent.User.Contains(MatchTarget) && sent.User.Contains(NewSource) && sent.User.Contains("82% match"),
                "J and the match with its source, beside the segment");
            Check(r.Disposed && r.Cancelled, "J afterwards the window's token source is disposed");

            // An AI error.
            _fail = true;
            r = Run(Job(NewSource, instructions), Match(MatchSource, MatchTarget, 82), "FuzzyFixRepair");
            Check(!r.TimedOut && !r.Threw && r.Repair == null && !r.CanWrite, "J failure: the AI answers with an error -> nothing to write, nothing thrown");
            Check(r.Header == "Nothing was written." && r.Body.Contains("stand-in failure"), "J the window says so, with the AI's own message (" + r.Body.Replace("\n", " ").Trim() + ")");
            Check(r.Disposed && r.Cancelled, "J failure: nothing left behind (token source disposed)");
            _fail = false;
            r = Run(Job(NewSource, instructions), Match(MatchSource, MatchTarget, 82), "FuzzyFixRepair");
            Check(r.Repair != null && r.CanWrite, "J and the next repair works");

            // An answer that never comes; the translator closes the window.
            _hold = new ManualResetEvent(false);
            r = Run(Job(NewSource, instructions), Match(MatchSource, MatchTarget, 82), "FuzzyFixRepair", closeAfterRequest: true);
            Check(!r.TimedOut && !r.Threw && r.Repair == null, "J failure: no answer, window closed -> the work ends (" + r.Ms + " ms), nothing to write, nothing thrown");
            Check(r.Disposed && r.Cancelled, "J failure: nothing left behind (cancelled, token source disposed)");
            _hold.Set(); _hold = null; Pump(300);
            r = Run(Job(NewSource, instructions), Match(MatchSource, MatchTarget, 82), "FuzzyFixRepair");
            Check(r.Repair != null && r.CanWrite, "J and the next repair works");

            // The match comes back as it was.
            _answer = MatchTarget;
            r = Run(Job(NewSource, instructions), Match(MatchSource, MatchTarget, 82), "FuzzyFixRepair");
            Check(r.Repair == null && !r.CanWrite && r.Body.Contains("unchanged") && r.Body.Contains(MatchTarget), "J failure: the AI returns the match unchanged -> said, nothing to write");

            // The segment has a tag, the reply has none.
            _answer = "De Acme-klep gaat open.";
            r = Run(Job("The Acme valve <t1/>opens.", instructions), Match("The Acme valve closes.", "De Acme-klep gaat dicht.", 85), "FuzzyFixRepair");
            Check(r.Repair == null && !r.CanWrite && r.Body.Contains("does not carry this segment's tags"), "J failure: a reply without the segment's tag -> not offered for writing");

            // Numbers only: 99%.
            _answer = "Acme-klep 14 gaat open.";
            r = Run(Job("Acme valve 14 opens.", instructions), Match("Acme valve 12 opens.", "Acme-klep 12 gaat open.", 99), "FuzzyFixRepair");
            Check(r.Repair != null && r.CanWrite && r.Header.Contains("99% match"), "J a 99% match that differs only in its numbers is repaired");

            // No match, 100%: no request at all.
            n0 = RequestCount();
            r = Run(Job(NewSource, instructions), null, "FuzzyFixRepair");
            Check(r.Repair == null && !r.CanWrite && r.Body.Contains("nothing to repair") && RequestCount() == n0, "J failure: no match -> said, and no AI request is made");
            r = Run(Job(NewSource, instructions), Match(NewSource, MatchTarget, 100), "FuzzyFixRepair");
            Check(r.Repair == null && !r.CanWrite && r.Body.Contains("100% match") && RequestCount() == n0, "J failure: the best match is 100% -> said, and no AI request is made");

            // The search itself: nothing to search, and a memory that cannot be opened.
            r = Run(Job(NewSource, instructions), null, "FuzzyFixRun");
            Check(!r.TimedOut && !r.Threw && r.Repair == null && r.Body.Contains("nothing to repair") && RequestCount() == n0,
                "J failure: the search finds nothing -> said, no AI request");
            var broken = Path.Combine(Root, "all-off", "A.sdltm");
            r = Run(Job(NewSource, instructions, new List<string> { broken }), null, "FuzzyFixRun");
            Check(!r.TimedOut && !r.Threw && r.Repair == null && !r.CanWrite && r.Header == "Nothing was written." && RequestCount() == n0,
                "J failure: a memory that cannot be opened -> the window says why nothing was written, nothing thrown (" + r.Body.Replace("\n", " ").Trim() + ")");

            var kept = View.GetFields(Any).Where(fi => fi.IsStatic && fi.Name.IndexOf("FuzzyFix", StringComparison.OrdinalIgnoreCase) >= 0).Select(fi => fi.Name).ToList();
            Check(kept.Count == 0 && View.GetField("_currentInstance", Any).GetValue(null) == null,
                "J nothing is kept between uses: the view part has no FuzzyFixer state" + (kept.Count > 0 ? " (" + string.Join(", ", kept) + ")" : ""));
        }

        Console.WriteLine("K  the write");
        {
            const string Old = "De Acme-klep gaat dicht.";
            const string New = "De Acme-klep gaat open.";

            var d = Doc(2, i => Old);
            var o = Write(d, d.Pairs[0], Result(New));
            Check(o.Ok && o.Why == null && o.Note == null && Text(d.Pairs[0].Target) == New, "K the repair lands in the target, nothing to report");
            Check(d.Pairs[0].Properties.ConfirmationLevel == ConfirmationLevel.Draft && d.StatusUpdates >= 1, "K as a draft");
            Check(Text(d.Pairs[1].Target) == Old, "K and the segment beside it is untouched");

            d = Doc(2, i => Old); d.Pairs[1].Properties.ConfirmationLevel = ConfirmationLevel.Translated;
            o = Write(d, d.Pairs[1], Result(New));
            Check(o.Ok && Text(d.Pairs[1].Target) == New && d.Pairs[1].Properties.ConfirmationLevel == ConfirmationLevel.Draft,
                "K a confirmed segment gets the repair and becomes a draft again");

            d = Doc(2);
            o = Write(d, d.Pairs[0], Result(New));
            Check(o.Ok && Text(d.Pairs[0].Target) == New && d.Pairs[0].Properties.ConfirmationLevel == ConfirmationLevel.Draft, "K an empty target: written, draft");

            d = Doc(2, i => Old); d.DontApply = true; d.Pairs[0].Properties.ConfirmationLevel = ConfirmationLevel.Translated;
            o = Write(d, d.Pairs[0], Result(New));
            Check(!o.Ok && !string.IsNullOrWhiteSpace(o.Why), "K failure: Studio quietly declines the write -> reported as not written (" + o.Why + ")");
            Check(Text(d.Pairs[0].Target) == Old && d.Pairs[0].Properties.ConfirmationLevel == ConfirmationLevel.Translated && d.StatusUpdates == 0,
                "K the target and the status are as they were");

            d = Doc(2, i => Old); d.ThrowBeforeApply = true; d.Pairs[0].Properties.ConfirmationLevel = ConfirmationLevel.Translated;
            o = Write(d, d.Pairs[0], Result(New));
            Check(!o.Ok && !string.IsNullOrWhiteSpace(o.Why) && o.Why.Contains("may not have changed"), "K failure: Studio throws before applying -> reported as not written, nothing thrown (" + o.Why + ")");
            Check(Text(d.Pairs[0].Target) == Old && d.Pairs[0].Properties.ConfirmationLevel == ConfirmationLevel.Translated && d.StatusUpdates == 0,
                "K the target and the status are as they were");

            d = Doc(2, i => Old); d.ThrowAfterApply = true;
            o = Write(d, d.Pairs[0], Result(New));
            Check(o.Ok && o.Why == null && Text(d.Pairs[0].Target) == New, "K failure: Studio throws AFTER applying -> the repair is in the document and counts as written");
            Check(o.Note != null && o.Note.Contains("IS in the document"), "K and the translator is told (" + (o.Note ?? "").Replace("\n", " ") + ")");
            Check(d.Pairs[0].Properties.ConfirmationLevel == ConfirmationLevel.Draft, "K and it is a draft");

            d = Doc(2, i => Old); d.StatusThrows = true; d.Pairs[0].Properties.ConfirmationLevel = ConfirmationLevel.Translated;
            o = Write(d, d.Pairs[0], Result(New));
            Check(o.Ok && Text(d.Pairs[0].Target) == New && o.Note != null && o.Note.Contains("could not be set to Draft"),
                "K failure: written, but the status cannot be set -> said, not passed over");

            d = Doc(2, i => Old);
            o = Write(d, d.Pairs[0], Result(""));
            Check(!o.Ok && Text(d.Pairs[0].Target) == Old && d.Writes == 0, "K failure: an empty repair is never written");

            // A segment with a tag: the repair's marker becomes the segment's own tag.
            d = Doc(1, i => Old);
            d.Pairs[0].Source.Add(F.CreatePlaceholderTag(PF.CreatePlaceholderTagProperties("<x id=\"1\"/>")));
            d.Pairs[0].Source.Add(T(" Acme end."));
            var handler = P.GetType("Supervertaler.Trados.Core.SegmentTagHandler", true);
            var ser = handler.GetMethod("Serialize", Any).Invoke(null, new object[] { d.Pairs[0].Source });
            var serialized = (string)ser.GetType().GetProperty("SerializedText").GetValue(ser, null);
            var withTag = Result("De Acme-klep gaat open.<t1/> Acme einde.");
            withTag.HasTags = true;
            typeof(BatchSegmentResultEventArgs).GetProperty("TagMap").SetValue(withTag, ser.GetType().GetProperty("TagMap").GetValue(ser, null), null);
            o = Write(d, d.Pairs[0], withTag);
            Check(serialized.Contains("<t1/>") && o.Ok && Placeholders(d.Pairs[0].Target) == 1 && Text(d.Pairs[0].Target) == "De Acme-klep gaat open. Acme einde.",
                "K a tagged repair: the tag is in the target as the segment's own tag, the words around it");
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
$all = @($refs) + @('System.dll', 'System.Core.dll', 'System.Web.Extensions.dll', 'System.Windows.Forms.dll', 'System.Drawing.dll', $Dll)
Add-Type -TypeDefinition ($src + "`n" + $stub) -ReferencedAssemblies $all
$manifest = Join-Path (Split-Path $Dll) 'Supervertaler.Trados.plugin.xml'
try { $code = [FuzzyFixerTest]::Run($Dll, $Root, $manifest) }
catch { $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }; Write-Host $e.ToString(); exit 2 }
finally { Remove-Item -LiteralPath $Root -Recurse -Force -ErrorAction SilentlyContinue }
exit $code
