# Restated passages: the lines Batch Translate writes at the end of a run
# (docs/design/restated-passages.md; the finding itself is core's RestatedPassages,
# tested in core\tests\RestatedPassagesTests.cs).
#
# Runs the real batch loop (BatchTranslator.TranslateAsync) of the built plugin DLL,
# outside Studio, against a stand-in AI server inside this script: it answers each
# numbered segment with the translation the test gave for that source. No API key, no
# cost. The document is invented (Acme): a claim, and a description sentence that
# repeats the claim's wording inside a longer sentence.
#
#   A  the description in a later batch, worded differently  -> the count and one line,
#      naming both segments as Studio numbers them
#   B  worded the same                                        -> no line at all
#   C  nothing sent changes: the requests are the same with the check on and off
#   D  near-identical segments, a [[TC: ...]] note on one     -> one line; the same when
#      notes are written as Trados comments
#   E  failure: the AI leaves the description out             -> skipped, no line
#   F  failure: Studio declines the write                     -> skipped, no line
#   G  a segment the run did not write (translated earlier)   -> compared with the run's
#   H  tags in the passage (<t1>..</t1>) decide nothing
#   I  a stopped run still raises Completed, with or without lines
#   J  10,000 segments through the run's collector, timed
#   K  the diagnostic log gets counts and a time, never text
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\restated_passages_test.ps1
#   ... -Dll <path to Supervertaler.Trados.dll>   (default: this repo's build)
# 32-bit builds (Studio 2024 / 2022) need the 32-bit PowerShell:
#   C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\restated_passages_test.ps1 -Dll <...\bin\Studio18\Debug\Supervertaler.Trados.dll>
#
# Everything is written under a data folder in %TEMP% (SupervertalerPaths.Set), deleted
# afterwards. The real diagnostic log is checked at the end: it may have grown (Studio
# may be running), but nothing this test wrote may be in what was added.
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

# The real diagnostic log: where it is, and how long it is now.
$realLog = $null
$realLength = 0
$cfg = Join-Path $env:APPDATA "Supervertaler\config.json"
if (Test-Path -LiteralPath $cfg) {
    try {
        $dataDir = (Get-Content -LiteralPath $cfg -Raw | ConvertFrom-Json).user_data_path
        if ($dataDir) { $realLog = Join-Path $dataDir "trados\logs\diagnostic.log" }
    } catch { }
}
if ($realLog -and (Test-Path -LiteralPath $realLog)) { $realLength = (Get-Item -LiteralPath $realLog).Length }

$temp = Join-Path $env:TEMP ("sv-restated-test-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $temp | Out-Null

Add-Type -AssemblyName System.Windows.Forms
[void][Reflection.Assembly]::LoadFrom($Dll)
Add-Type -ReferencedAssemblies @('System.dll', 'System.Core.dll', 'System.Web.Extensions.dll', 'System.Windows.Forms.dll', 'System.Drawing.dll', $Dll) -TypeDefinition @"
using System; using System.IO; using System.Linq; using System.Net; using System.Net.Sockets; using System.Reflection;
using System.Text; using System.Text.RegularExpressions; using System.Threading; using System.Collections.Generic;
using Supervertaler.Core; using Supervertaler.Core.Models;
using Supervertaler.Trados.Core; using Supervertaler.Trados.Settings;

public static class RestatedPassagesTest {
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

    // ---- the stand-in AI: answers each numbered segment with the translation given for its source ----

    static HttpListener _listener;
    static string _endpoint;
    static readonly Dictionary<string, string> Answers = new Dictionary<string, string>();
    static readonly List<string> Requests = new List<string>();   // each request's user prompt
    static int _delayMs;

    static void StartServer() {
        // A port the system hands out, so no other test's is taken.
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        _listener = new HttpListener();
        _listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
        _listener.Start();
        _endpoint = "http://127.0.0.1:" + port + "/v1";
        var t = new Thread(Serve); t.IsBackground = true; t.Start();
    }

    static void Serve() {
        var json = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        while (_listener.IsListening) {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); } catch { return; }
            try {
                string body;
                using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = r.ReadToEnd();
                var req = (Dictionary<string, object>)json.DeserializeObject(body);
                string user = "";
                foreach (Dictionary<string, object> m in (object[])req["messages"]) {
                    if ((string)m["role"] != "user") continue;
                    user = m["content"] as string;
                    if (user == null) user = string.Concat(((object[])m["content"]).Cast<Dictionary<string, object>>()
                        .Select(x => x.ContainsKey("text") ? (string)x["text"] : ""));
                }
                lock (Requests) Requests.Add(user);
                if (_delayMs > 0) Thread.Sleep(_delayMs);

                // The segments only: the instructions above them are a numbered list too.
                var part = user;
                int at = part.IndexOf("ONLY the numbered translations.", StringComparison.Ordinal);
                if (at >= 0) part = part.Substring(at);
                at = part.IndexOf("**YOUR TRANSLATIONS", StringComparison.Ordinal);
                if (at >= 0) part = part.Substring(0, at);

                var lines = new List<string>();
                foreach (Match m in Regex.Matches(part, @"^(\d+)\. (.*?)\r?$", RegexOptions.Multiline)) {
                    string answer;
                    lock (Answers) if (!Answers.TryGetValue(m.Groups[2].Value.Trim(), out answer)) answer = "[translated " + m.Groups[1].Value + "]";
                    if (answer == null) continue;   // left out of the reply
                    lines.Add(m.Groups[1].Value + ". " + answer);
                }
                var reply = new Dictionary<string, object> {
                    { "choices", new object[] { new Dictionary<string, object> {
                        { "message", new Dictionary<string, object> { { "role", "assistant" }, { "content", string.Join("\n", lines) } } },
                        { "finish_reason", "stop" } } } },
                    { "usage", new Dictionary<string, object> { { "prompt_tokens", 100 }, { "completion_tokens", 5 } } } };
                var bytes = Encoding.UTF8.GetBytes(json.Serialize(reply));
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
            } catch { try { ctx.Response.Abort(); } catch { } }
        }
    }

    // ---- the run ----

    static AiSettings Settings(bool tcAsComments) {
        var ai = new AiSettings();
        ai.SelectedProvider = "custom_openai";
        ai.CustomOpenAiProfiles = new List<CustomOpenAiProfile>();
        var p = new CustomOpenAiProfile();
        p.Name = "fake"; p.Endpoint = _endpoint; p.Model = "fake-unlisted-model"; p.ApiKey = "x";
        ai.CustomOpenAiProfiles.Add(p);
        ai.SelectedCustomProfileName = "fake";
        ai.IncludeDocumentContext = false;
        ai.LogPromptsToReports = false;
        ai.TcMarkersAsComments = tcAsComments;
        return ai;
    }

    static BatchSegment Seg(int number, string source) {
        var b = new BatchSegment(); b.Index = number - 1; b.Number = number.ToString(); b.SourceText = source; return b;
    }

    // RestatedCheck is internal: reached by reflection, the code under test unchanged.
    static Type CheckType;
    static object NewCheck() { return Activator.CreateInstance(CheckType, true); }
    static void Sent(object check, BatchSegment s) { CheckType.GetMethod("Sent").Invoke(check, new object[] { s }); }
    static void Kept(object check, string label, string source, string target) { CheckType.GetMethod("Kept").Invoke(check, new object[] { label, source, target }); }
    static string Summary(object check) { return (string)CheckType.GetMethod("Summary").Invoke(check, null); }

    class Result {
        public BatchCompletedEventArgs Done;
        public List<string> Requests;
        public int CompletedCount;
        public string[] Lines { get {
            return Done == null || Done.RestatedSummary == null ? new string[0]
                : Done.RestatedSummary.Split(new[] { Environment.NewLine }, StringSplitOptions.None); } }
    }

    /// <summary>One run over the segments, noted in the check the way CollectSegments notes them.</summary>
    static Result Go(List<BatchSegment> segs, bool check = true, int batchSize = 2, bool tcAsComments = false,
        HashSet<int> decline = null, object given = null, CancellationTokenSource cts = null) {
        var r = new Result();
        int before; lock (Requests) before = Requests.Count;
        var t = new BatchTranslator();
        if (check) {
            var c = given ?? NewCheck();
            foreach (var s in segs) Sent(c, s);
            typeof(BatchTranslator).GetProperty("Restated", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).SetValue(t, c);
        }
        t.SegmentTranslated += (s, e) => { e.WriteSucceeded = decline == null || !decline.Contains(e.SegmentIndex); };
        t.Completed += (s, e) => { r.Done = e; r.CompletedCount++; };
        var st = new TermLensSettings(); st.AiSettings = Settings(tcAsComments);
        typeof(SettingsService).GetField("_current", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, st);
        t.TranslateAsync(segs, "Dutch", "English", st.AiSettings, new List<TermEntry>(), batchSize,
            cts != null ? cts.Token : CancellationToken.None, null, null, null, null, false, StructureContextMode.Off).Wait();
        lock (Requests) r.Requests = Requests.Skip(before).ToList();
        return r;
    }

    // ---- the invented document ----

    const string Claim = "Inrichting volgens conclusie 1, waarbij de klemarm in een richting loodrecht op de lengteas van het huis beweegbaar is opgesteld.";
    const string Desc = "In een voorkeursuitvoering van de inrichting van Acme is voorzien dat de klemarm in een richting loodrecht op de lengteas van het huis beweegbaar is opgesteld, zodat de klep vrij blijft.";
    const string ClaimEn = "Device according to claim 1, wherein the clamping arm is arranged to be movable in a direction perpendicular to the longitudinal axis of the housing.";
    const string DescEn = "In a preferred embodiment of the Acme device, provision is made that the clamping arm is arranged to be movable in a direction perpendicular to the longitudinal axis of the housing, so that the valve remains free.";
    const string DescEnReworded = "In a preferred embodiment of the Acme device, provision is made that the clamping arm is arranged to be movable in a perpendicular direction to the longitudinal axis of the housing, so that the valve remains free.";
    const string Pin12 = "De borgpen 12 wordt in de uitsparing 14 van de flens 16 geplaatst en daarna vastgezet met de moer.";
    const string Pin22 = "De borgpen 22 wordt in de uitsparing 14 van de flens 16 geplaatst en daarna vastgezet met de moer.";
    const string Pin12En = "The locking pin 12 is placed in the recess 14 of the flange 16 and then secured with the nut.";
    const string Pin22En = "The locking pin 22 is placed in the recess 14 of the flange 16 and then secured with the nut.";

    static void Answer(string source, string translation) { lock (Answers) Answers[source] = translation; }

    /// <summary>The claim is segment 2 (first batch), the description segment 5 (third batch).</summary>
    static List<BatchSegment> Document(string claim = Claim, string desc = Desc) {
        return new List<BatchSegment> {
            Seg(1, "De uitvinding heeft betrekking op een inrichting voor het vastzetten van een klep."), Seg(2, claim),
            Seg(3, "Figuur 1 toont een aanzicht van de inrichting."), Seg(4, "Het huis is vervaardigd uit staal."),
            Seg(5, desc), Seg(6, "De klep wordt daarna gesloten.") };
    }

    public static void Run(string temp) {
        SupervertalerPaths.Set(temp);
        Check(string.Equals(SupervertalerPaths.Root, temp, StringComparison.OrdinalIgnoreCase), "data folder is the temp folder");
        DiagnosticLog.Enabled = true;
        CheckType = typeof(BatchTranslator).Assembly.GetType("Supervertaler.Trados.Core.RestatedCheck");
        Check(CheckType != null, "the plugin has the check");
        StartServer();
        Answer(Claim, ClaimEn);

        Console.WriteLine("A  the description, three batches later, worded differently");
        Answer(Desc, DescEnReworded);
        var a = Go(Document());
        Check(a.Requests.Count == 3, "three batches (" + a.Requests.Count + ")");
        Check(a.Done != null && a.Done.Translated == 6 && a.Done.Failed == 0, "six written, none failed");
        Check(a.Lines.Length == 2, "two lines: the count and the case (" + a.Lines.Length + ")");
        Check(a.Lines.Length == 2 && a.Lines[0] == "1 translated differently (listed).", "the count: " + (a.Lines.Length > 0 ? a.Lines[0] : "(none)"));
        Check(a.Lines.Length == 2 && a.Lines[1] == "Translated differently: segment 2 and segment 5 \u2013 source \u201Cde klemarm in een richting loodrecht op de \u2026\u201D (15 words the same)",
            "the case, by Studio's numbers: " + (a.Lines.Length > 1 ? a.Lines[1] : "(none)"));

        Console.WriteLine("B  worded the same");
        Answer(Desc, DescEn);
        var b = Go(Document());
        Check(b.Done != null && b.Done.Translated == 6, "six written");
        Check(b.Done != null && b.Done.RestatedSummary == null, "nothing to say, so no line at all");

        Console.WriteLine("C  nothing sent changes");
        var off = Go(Document(), check: false);
        Check(off.Done != null && off.Done.RestatedSummary == null, "without the check (Alt+T, SuperBench): no lines");
        Check(off.Requests.Count == b.Requests.Count && off.Requests.SequenceEqual(b.Requests),
            "the AI is sent exactly the same with the check on as off (" + b.Requests.Count + " requests)");

        Console.WriteLine("D  near-identical segments, a note on one only");
        Answer(Pin12, Pin12En + " [[TC: the figures show no flange 16]]");
        Answer(Pin22, Pin22En);
        foreach (var asComments in new[] { false, true }) {
            var d = Go(Document(Pin12, Pin22), tcAsComments: asComments);
            var how = asComments ? "notes as Trados comments" : "notes kept in the target";
            Check(d.Lines.Length == 2 && d.Lines[0] == "1 near-identical pair with a note on only one (listed).", how + ": the count: " + (d.Lines.Length > 0 ? d.Lines[0] : "(none)"));
            Check(d.Lines.Length == 2 && d.Lines[1] == "Note on one only: segment 2 has a note, the near-identical segment 5 has none",
                how + ": the case: " + (d.Lines.Length > 1 ? d.Lines[1] : "(none)"));
        }
        Answer(Pin22, Pin22En + " [[TC: the figures show no flange 16]]");
        var both = Go(Document(Pin12, Pin22));
        Check(both.Done != null && both.Done.RestatedSummary == null, "a note on both: nothing to say");

        Console.WriteLine("E  failure: the AI leaves the description out of its reply");
        Answer(Desc, null);
        var e = Go(Document());
        Check(e.Done != null && e.Done.Translated == 5 && e.Done.Failed == 1, "five written, one failed (" + (e.Done != null ? e.Done.Translated + "/" + e.Done.Failed : "?") + ")");
        Check(e.Done != null && e.Done.RestatedSummary == null, "a segment with no translation is skipped, not reported as different");

        Console.WriteLine("F  failure: Studio declines the write");
        Answer(Desc, DescEnReworded);
        var f = Go(Document(), decline: new HashSet<int> { 4 });
        Check(f.Done != null && f.Done.Failed == 1, "the declined segment counts as failed");
        Check(f.Done != null && f.Done.RestatedSummary == null, "a translation Studio did not take is not compared");

        Console.WriteLine("G  a segment translated before the run (claims first, then the rest)");
        var keptCheck = NewCheck();
        Kept(keptCheck, "segment 2 of Claims.docx", Claim, ClaimEn);
        var rest = new List<BatchSegment> { Seg(5, Desc), Seg(6, "De klep wordt daarna gesloten.") };
        var g = Go(rest, given: keptCheck);
        Check(g.Lines.Length == 2 && g.Lines[1].StartsWith("Translated differently: segment 2 of Claims.docx and segment 5 "),
            "the run's translation is compared with the one already there: " + (g.Lines.Length > 1 ? g.Lines[1] : "(none)"));

        Console.WriteLine("H  tags in the passage");
        var tagged = Claim.Replace("de klemarm", "de <t1>klemarm</t1>").Replace("lengteas", "lengte<t2/>as");
        Answer(tagged, ClaimEn.Replace("clamping arm", "<t1>clamping arm</t1>"));
        Answer(Desc, DescEn);
        var h = Go(Document(tagged));
        Check(h.Done != null && h.Done.Translated == 6 && h.Done.RestatedSummary == null, "same wording with tags in one of them: no line");
        Answer(Desc, DescEnReworded);
        var h2 = Go(Document(tagged));
        Check(h2.Lines.Length == 2 && h2.Lines[1].StartsWith("Translated differently: segment 2 and segment 5 "), "different wording is still found through the tags");

        Console.WriteLine("I  a stopped run");
        _delayMs = 300;
        var cts = new CancellationTokenSource();
        var stopper = new Timer(_ => cts.Cancel(), null, 450, Timeout.Infinite);
        var i = Go(Document(), cts: cts);
        stopper.Dispose(); _delayMs = 0;
        Check(i.CompletedCount == 1 && i.Done.WasCancelled, "Completed is raised once, as cancelled");
        Check(i.Done.Translated < 6, "stopped before the end (" + i.Done.Translated + " written)");
        Check(i.Done.RestatedSummary == null, "the description was never written, so there is nothing to compare");

        Console.WriteLine("J  10,000 segments");
        var big = NewCheck();
        var random = new Random(20261114);
        var words = Enumerable.Range(0, 2000).Select(n => "w" + n).ToArray();
        var sources = new List<string>();
        for (int n = 0; n < 10000; n++) sources.Add(string.Join(" ", Enumerable.Range(0, 30).Select(_ => words[random.Next(2000)])));
        for (int p = 0; p < 200; p++) {
            var passage = " " + string.Join(" ", Enumerable.Range(0, 14).Select(k => "p" + p + "x" + k));
            sources[p * 7] += passage; sources[9999 - p * 7] += passage + " en verder";
        }
        for (int n = 0; n < 10000; n++) {
            var target = sources[n].ToUpperInvariant();
            if (n >= 9000 && n % 2 == 0) target = string.Join(" ", target.Split(' ').Reverse());   // some of the later ones worded differently
            Kept(big, "segment " + (n + 1), sources[n], target);
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var bigSummary = Summary(big);
        clock.Stop();
        var bigLines = bigSummary == null ? new string[0] : bigSummary.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
        Check(clock.ElapsedMilliseconds < 10000, "found and compared in " + clock.ElapsedMilliseconds + " ms (limit 10,000)");
        Check(bigLines.Length == 22 && bigLines[0].EndsWith("translated differently (listed)."), "the log gets the count, 20 cases and one line for the rest ("
            + bigLines.Length + " lines: " + (bigLines.Length > 0 ? bigLines[0] : "") + ")");

        Console.WriteLine("K  the diagnostic log");
        var log = Path.Combine(temp, "trados", "logs", "diagnostic.log");
        var logText = File.Exists(log) ? File.ReadAllText(log) : "";
        var restatedLines = logText.Split('\n').Where(l => l.Contains("Restated")).ToList();
        Check(restatedLines.Count > 0, "the test's own log has a line per check (" + restatedLines.Count + ")");
        Check(restatedLines.Any(l => l.Contains("10000 segments") && l.Contains(" ms")), "with the segment count and the time");
        Check(!restatedLines.Any(l => l.Contains("klemarm") || l.Contains("clamping")), "and no text from the document");

        try { _listener.Stop(); } catch { }
    }
}
"@

[RestatedPassagesTest]::Install([string[]]@((Split-Path $Dll), $studio))
try {
    [RestatedPassagesTest]::Run($temp)
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}

# The real diagnostic log: nothing of this test in whatever was added to it meanwhile.
$clean = $true
if ($realLog -and (Test-Path -LiteralPath $realLog)) {
    $now = (Get-Item -LiteralPath $realLog).Length
    if ($now -gt $realLength) {
        $fs = [IO.File]::Open($realLog, 'Open', 'Read', 'ReadWrite')
        try {
            [void]$fs.Seek($realLength, 'Begin')
            $buf = New-Object byte[] ($now - $realLength)
            [void]$fs.Read($buf, 0, $buf.Length)
            $added = [Text.Encoding]::UTF8.GetString($buf)
            if ($added -match 'Restated|fake-unlisted-model') { $clean = $false }
        } finally { $fs.Dispose() }
    }
}
if ($clean) { [RestatedPassagesTest]::Pass++ ; Write-Host "  ok   the real diagnostic log has nothing from this test" }
else { [RestatedPassagesTest]::Fail++ ; Write-Host "  FAIL the real diagnostic log gained lines from this test: $realLog" }
Write-Host ""
Write-Host ("{0} passed, {1} failed" -f [RestatedPassagesTest]::Pass, [RestatedPassagesTest]::Fail)
if ([RestatedPassagesTest]::Fail -gt 0) { exit 1 }
