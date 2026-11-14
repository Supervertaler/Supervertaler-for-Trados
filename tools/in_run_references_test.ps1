# In-run references: Batch Translate shows the AI its own earlier translation of a passage
# a segment repeats (docs/design/in-run-references.md; the choosing and the prompt block are
# core's EarlierTranslations and TranslationPrompt.BuildReferenceBlock, tested in
# core\tests\EarlierTranslationsTests.cs).
#
# Runs the real batch loop (BatchTranslator.TranslateAsync) of the built plugin DLL, outside
# Studio, against a stand-in AI server inside this script that records every request (system
# and user prompt) and answers each numbered segment with the translation the test gave for
# that source. No API key, no cost. All text is invented (Acme).
#
#   A  a sentence in batch 1, a later one that repeats its wording in batch 3: the earlier
#      segment and its translation are in batch 3's user prompt, and in no other request
#   B  the system prompt is byte for byte the same in every request, and the same as with
#      the feature off
#   C  with nothing to show, the requests are byte for byte what they are with it off
#   D  the end-of-run lines: how many segments were shown one, and what still differs
#   E  failure: the earlier segment was left out of the AI's reply   -> nothing shown later
#   F  failure: the request for the earlier batch got an HTTP error  -> nothing shown later
#   G  failure: Studio declined the earlier write                    -> nothing shown later
#   H  stop, then run again on the empty segments: the same reference as an uninterrupted run
#   I  "All segments": targets about to be overwritten are never shown; the new one is
#   J  a [[TC: ...]] note travels with the earlier translation, kept in the target or as a
#      Trados comment; an earlier near-twin that was missed is not revisited
#   K  tags are left out of the earlier texts
#   L  Preview prompt's lookup shows what is in the document and counts nothing as shown
#   M  how much longer the requests get (for the cost estimate)
#   N  10,000 segments: the first lookup (finds the pairs once) and all the others, timed
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\in_run_references_test.ps1
#   ... -Dll <path to Supervertaler.Trados.dll>   (default: this repo's build)
# 32-bit builds (Studio 2024 / 2022) need the 32-bit PowerShell:
#   C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\in_run_references_test.ps1 -Dll <...\bin\Studio18\Debug\Supervertaler.Trados.dll>
#
# Everything is written under a data folder in %TEMP% (SupervertalerPaths.Set), deleted
# afterwards. The real diagnostic log is checked at the end: it may have grown (Studio may
# be running), but nothing this test wrote may be in what was added.
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

$temp = Join-Path $env:TEMP ("sv-references-test-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $temp | Out-Null

Add-Type -AssemblyName System.Windows.Forms
[void][Reflection.Assembly]::LoadFrom($Dll)
Add-Type -ReferencedAssemblies @('System.dll', 'System.Core.dll', 'System.Web.Extensions.dll', 'System.Windows.Forms.dll', 'System.Drawing.dll', $Dll) -TypeDefinition @"
using System; using System.IO; using System.Linq; using System.Net; using System.Net.Sockets; using System.Reflection;
using System.Text; using System.Text.RegularExpressions; using System.Threading; using System.Collections.Generic;
using Supervertaler.Core; using Supervertaler.Core.Models;
using Supervertaler.Trados.Core; using Supervertaler.Trados.Settings;

public static class InRunReferencesTest {
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

    // ---- the stand-in AI ----

    class Request { public string System, User; }
    static HttpListener _listener;
    static string _endpoint;
    static readonly Dictionary<string, string> Answers = new Dictionary<string, string>();
    /// <summary>The answer for a source when the request shows an earlier translation for it: an AI that follows the block.</summary>
    static readonly Dictionary<string, string> AnswersWhenShown = new Dictionary<string, string>();
    static readonly List<Request> Requests = new List<Request>();
    static int _delayMs;
    /// <summary>A request whose user prompt contains this gets HTTP 500.</summary>
    static string _failOn;

    const string Block = "**EARLIER TRANSLATIONS FROM THIS DOCUMENT";

    static void StartServer() {
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        _listener = new HttpListener();
        _listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
        _listener.Start();
        _endpoint = "http://127.0.0.1:" + port + "/v1";
        var t = new Thread(Serve); t.IsBackground = true; t.Start();
    }

    static string Text(object content) {
        var s = content as string;
        if (s != null) return s;
        return string.Concat(((object[])content).Cast<Dictionary<string, object>>().Select(x => x.ContainsKey("text") ? (string)x["text"] : ""));
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
                var seen = new Request { System = "", User = "" };
                foreach (Dictionary<string, object> m in (object[])req["messages"]) {
                    if ((string)m["role"] == "user") seen.User = Text(m["content"]);
                    else if ((string)m["role"] == "system") seen.System += Text(m["content"]);
                }
                lock (Requests) Requests.Add(seen);
                if (_delayMs > 0) Thread.Sleep(_delayMs);

                byte[] bytes;
                if (_failOn != null && seen.User.Contains(_failOn)) {
                    ctx.Response.StatusCode = 400;
                    bytes = Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"stand-in failure\",\"type\":\"invalid_request_error\"}}");
                } else {
                    var user = seen.User;
                    var part = user;
                    int at = part.IndexOf("ONLY the numbered translations.", StringComparison.Ordinal);
                    if (at >= 0) part = part.Substring(at);
                    at = part.IndexOf("**YOUR TRANSLATIONS", StringComparison.Ordinal);
                    if (at >= 0) part = part.Substring(0, at);

                    var lines = new List<string>();
                    foreach (Match m in Regex.Matches(part, @"^(\d+)\. (.*?)\r?$", RegexOptions.Multiline)) {
                        var source = m.Groups[2].Value.Trim();
                        string answer;
                        bool shown = user.Contains("Segment " + m.Groups[1].Value + " \u2013 repeats");
                        lock (Answers) {
                            if (!(shown && AnswersWhenShown.TryGetValue(source, out answer))
                                && !Answers.TryGetValue(source, out answer)) answer = "[translated " + m.Groups[1].Value + "]";
                        }
                        if (answer == null) continue;
                        lines.Add(m.Groups[1].Value + ". " + answer);
                    }
                    var reply = new Dictionary<string, object> {
                        { "choices", new object[] { new Dictionary<string, object> {
                            { "message", new Dictionary<string, object> { { "role", "assistant" }, { "content", string.Join("\n", lines) } } },
                            { "finish_reason", "stop" } } } },
                        { "usage", new Dictionary<string, object> { { "prompt_tokens", 100 }, { "completion_tokens", 5 } } } };
                    bytes = Encoding.UTF8.GetBytes(json.Serialize(reply));
                }
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

    static BatchSegment Seg(int number, string source, string existing = null) {
        var b = new BatchSegment(); b.Index = number - 1; b.Number = number.ToString(); b.SourceText = source; b.ExistingTarget = existing; return b;
    }

    // RestatedCheck is internal: reached by reflection, the code under test unchanged.
    static Type CheckType;
    static object NewCheck() { return Activator.CreateInstance(CheckType, true); }
    static void Sent(object check, BatchSegment s) { CheckType.GetMethod("Sent").Invoke(check, new object[] { s }); }
    static void Kept(object check, string label, string source, string target) { CheckType.GetMethod("Kept").Invoke(check, new object[] { label, source, target }); }
    static void Written(object check, BatchSegment s, string t) { CheckType.GetMethod("Written").Invoke(check, new object[] { s, t }); }
    static string Summary(object check) { return (string)CheckType.GetMethod("Summary").Invoke(check, null); }
    static int Count(object check, string method, BatchSegment s) {
        return ((System.Collections.ICollection)CheckType.GetMethod(method).Invoke(check, new object[] { s })).Count;
    }

    class Result {
        public BatchCompletedEventArgs Done;
        public List<Request> Requests;
        public Dictionary<int, string> Written = new Dictionary<int, string>();
        public string[] Lines { get {
            return Done == null || Done.RestatedSummary == null ? new string[0]
                : Done.RestatedSummary.Split(new[] { Environment.NewLine }, StringSplitOptions.None); } }
        public List<Request> WithBlock { get { return Requests.Where(r => r.User.Contains(Block)).ToList(); } }
    }

    /// <summary>One run. references: null = no check at all (Alt+T, SuperBench), false = step 1 only, true = Batch Translate.</summary>
    static Result Go(List<BatchSegment> segs, bool? references = true, int batchSize = 2, bool tcAsComments = false,
        HashSet<int> decline = null, object given = null, CancellationTokenSource cts = null) {
        var r = new Result();
        int before; lock (Requests) before = Requests.Count;
        var t = new BatchTranslator();
        if (references != null) {
            var c = given ?? NewCheck();
            foreach (var s in segs) Sent(c, s);
            typeof(BatchTranslator).GetProperty("Restated", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).SetValue(t, c);
            t.InRunReferences = references.Value;
        }
        t.SegmentTranslated += (s, e) => {
            e.WriteSucceeded = decline == null || !decline.Contains(e.SegmentIndex);
            if (e.WriteSucceeded) lock (r) r.Written[e.SegmentIndex] = e.Translation;
        };
        t.Completed += (s, e) => { r.Done = e; };
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
    const string Note = " [[TC: the figures show no flange 16]]";

    static void Answer(string source, string translation) { lock (Answers) Answers[source] = translation; }
    static void AnswerWhenShown(string source, string translation) {
        lock (Answers) { if (translation == null) AnswersWhenShown.Remove(source); else AnswersWhenShown[source] = translation; }
    }

    /// <summary>The first sentence is segment 2 (first batch), the one that repeats it segment 5 (third batch).</summary>
    static List<BatchSegment> Document(string first = Claim, string later = Desc, bool withOld = false) {
        var d = new List<BatchSegment> {
            Seg(1, "De uitvinding heeft betrekking op een inrichting voor het vastzetten van een klep."), Seg(2, first),
            Seg(3, "Figuur 1 toont een aanzicht van de inrichting."), Seg(4, "Het huis is vervaardigd uit staal."),
            Seg(5, later), Seg(6, "De klep wordt daarna gesloten.") };
        if (withOld) foreach (var s in d) s.ExistingTarget = "OLD TRANSLATION of the clamping arm in a direction perpendicular, segment " + s.Number;
        return d;
    }

    static string Entry(string number, string source, string target) {
        return "Segment " + number + " \u2013 repeats \u201Cde klemarm in een richting loodrecht op de \u2026\u201D (15 words)\r\n"
            + "  earlier source:      " + source + "\r\n"
            + "  earlier translation: " + target + "\r\n";
    }

    public static void Run(string temp) {
        SupervertalerPaths.Set(temp);
        Check(string.Equals(SupervertalerPaths.Root, temp, StringComparison.OrdinalIgnoreCase), "data folder is the temp folder");
        DiagnosticLog.Enabled = true;
        CheckType = typeof(BatchTranslator).Assembly.GetType("Supervertaler.Trados.Core.RestatedCheck");
        StartServer();
        Answer(Claim, ClaimEn);
        Answer(Desc, DescEnReworded);          // an AI that does not see the earlier wording words it differently
        AnswerWhenShown(Desc, DescEn);         // one that sees it keeps it

        Console.WriteLine("A  the earlier segment and its translation, in the later batch's user prompt");
        var on = Go(Document());
        Check(on.Requests.Count == 3, "three requests (" + on.Requests.Count + ")");
        Check(on.WithBlock.Count == 1 && on.Requests[2].User.Contains(Block), "only the third carries the block (" + on.WithBlock.Count + " with it)");
        Check(on.Requests[2].User.Contains(Entry("5", Claim, ClaimEn)), "it shows segment 5 the earlier source and the translation this run wrote for it");
        Check(on.Requests[2].User.StartsWith(Block) && on.Requests[2].User.Contains("**SEGMENTS TO TRANSLATE (2 segments):**"), "ahead of the segments, the rest of the request as it was");
        Check(on.Written.ContainsKey(4) && on.Written[4] == DescEn, "the AI that was shown the earlier wording kept it");

        Console.WriteLine("B  the system prompt");
        var off = Go(Document(), references: false);
        var none = Go(Document(), references: null);
        Check(on.Requests.Select(r => r.System).Distinct().Count() == 1 && on.Requests[0].System.Length > 0,
            "byte for byte the same in all three requests (" + on.Requests[0].System.Length + " characters)");
        Check(on.Requests[0].System == off.Requests[0].System && off.Requests[0].System == none.Requests[0].System,
            "and the same as with the feature off, and as Alt+T's translator sends");
        Check(!on.Requests[0].System.Contains("EARLIER TRANSLATIONS"), "nothing of the block is in it");

        Console.WriteLine("C  nothing to show: the requests are what they were");
        Check(off.WithBlock.Count == 0 && none.WithBlock.Count == 0, "feature off (step 1 only), and no check at all: no block");
        Check(off.Requests.Select(r => r.User).SequenceEqual(none.Requests.Select(r => r.User)), "step 1 alone still sends exactly what a run without it sends");
        Check(on.Requests[0].User == off.Requests[0].User && on.Requests[1].User == off.Requests[1].User,
            "with the feature on, the two requests that have nothing to show are byte for byte the same as with it off");
        var plain = new List<BatchSegment> { Seg(1, "De klep wordt gesloten."), Seg(2, "Het huis is van staal."), Seg(3, "Figuur 2 toont de klep.") };
        var plainOn = Go(plain); var plainOff = Go(plain, references: null);
        Check(plainOn.Requests.Select(r => r.User).SequenceEqual(plainOff.Requests.Select(r => r.User)),
            "a document that repeats nothing: every request byte for byte the same, on or off");

        Console.WriteLine("D  the end-of-run lines");
        Check(on.Lines.Length == 1 && on.Lines[0] == "1 segment was shown an earlier translation of a passage it repeats.",
            "shown and kept: one line, nothing left to report: " + string.Join(" / ", on.Lines));
        Check(off.Lines.Length == 2 && off.Lines[0] == "1 translated differently (listed).", "with the feature off the same document is reported as before: " + string.Join(" / ", off.Lines));
        AnswerWhenShown(Desc, null);           // an AI that ignores the block
        var ignored = Go(Document());
        Check(ignored.Lines.Length == 3 && ignored.Lines[0].StartsWith("1 segment was shown")
            && ignored.Lines[1] == "1 translated differently (listed)." && ignored.Lines[2].StartsWith("Translated differently: segment 2 and segment 5 "),
            "shown but worded differently all the same: the report says what is left: " + string.Join(" / ", ignored.Lines));
        AnswerWhenShown(Desc, DescEn);

        Console.WriteLine("E  failure: the earlier segment is left out of the AI's reply");
        Answer(Claim, null);
        var e = Go(Document());
        Check(e.Done != null && e.Done.Failed == 1 && e.WithBlock.Count == 0, "no translation was written, so nothing is shown later");
        Check(e.Lines.Length == 0, "and nothing is reported");
        Answer(Claim, ClaimEn);

        Console.WriteLine("F  failure: the request for the earlier batch gets an HTTP error");
        _failOn = "2. " + Claim;
        var f = Go(Document());
        _failOn = null;
        Check(f.Done != null && f.Done.Failed >= 2, "the batch failed (" + (f.Done != null ? f.Done.Failed.ToString() : "?") + " segments not written)");
        Check(f.WithBlock.Count == 0, "a failed batch gives no reference");
        Check(f.Requests.Any(r => r.User.Contains("5. " + Desc)), "the later batch was still sent, without one");

        Console.WriteLine("G  failure: Studio declines the earlier write");
        var g = Go(Document(), decline: new HashSet<int> { 1 });
        Check(g.Done != null && g.Done.Failed == 1 && g.WithBlock.Count == 0, "a translation Studio did not take is never shown");

        Console.WriteLine("H  stop, then run again on the empty segments");
        _delayMs = 300;
        var cts = new CancellationTokenSource();
        var stopper = new Timer(_ => cts.Cancel(), null, 450, Timeout.Infinite);
        var stopped = Go(Document(), cts: cts);
        stopper.Dispose(); _delayMs = 0;
        Check(stopped.Done.WasCancelled && stopped.Written.ContainsKey(1) && !stopped.Written.ContainsKey(4),
            "stopped after the first batch: segment 2 written, segment 5 not (" + stopped.Written.Count + " written)");
        // The second run reads the document: what is translated is kept, the rest is sent.
        var resumedKnown = NewCheck();
        var doc = Document();
        var rest = new List<BatchSegment>();
        foreach (var s in doc) {
            string written;
            if (stopped.Written.TryGetValue(s.Index, out written)) Kept(resumedKnown, "segment " + s.Number, s.SourceText, written);
            else rest.Add(s);
        }
        var resumed = Go(rest, given: resumedKnown);
        Check(resumed.WithBlock.Count == 1 && resumed.WithBlock[0].User.Contains("  earlier source:      " + Claim + "\r\n  earlier translation: " + ClaimEn + "\r\n"),
            "the second run shows the same earlier segment and translation, read from the document");
        Check(resumed.Written.Values.Contains(DescEn), "and the later segment comes out as in an uninterrupted run");
        Check(Summary(NewCheck()) == null, "a check nobody used has nothing to say, and nothing was left behind by the stopped run");

        Console.WriteLine("I  All segments: targets about to be overwritten");
        var all = Go(Document(withOld: true));
        Check(all.Requests.All(r => !r.User.Contains("OLD TRANSLATION")), "the old targets are in no request");
        Check(all.WithBlock.Count == 1 && all.Requests[2].User.Contains(Entry("5", Claim, ClaimEn)), "the later batch is shown the new translation, once it is written");

        Console.WriteLine("J  a note travels with the earlier translation");
        Answer(Pin12, Pin12En + Note);
        Answer(Pin22, Pin22En);
        foreach (var asComments in new[] { false, true }) {
            var j = Go(Document(Pin12, Pin22), tcAsComments: asComments);
            var how = asComments ? "notes as Trados comments" : "notes kept in the target";
            Check(j.WithBlock.Count == 1 && j.WithBlock[0].User.Contains("  earlier translation: " + Pin12En + Note + "\r\n"),
                how + ": the later near-twin is shown the earlier translation with its note");
            Check(j.WithBlock.Count == 1 && j.WithBlock[0].User.Contains("If your segment has the same problem, flag it in the same way"),
                how + ": and told what the note is");
        }
        // The other way round: the note is on the LATER twin. The earlier one is not sent again.
        Answer(Pin12, Pin12En);
        Answer(Pin22, Pin22En + Note);
        var later = Go(Document(Pin12, Pin22));
        Check(later.Requests.Count == 3 && later.Requests.Count(r => r.User.Contains("2. " + Pin12)) == 1,
            "an earlier twin that got no note is not revisited (3 requests, segment 2 sent once)");
        Check(later.Lines.Any(l => l == "Note on one only: segment 5 has a note, the near-identical segment 2 has none"),
            "the end-of-run report is what catches it: " + string.Join(" / ", later.Lines));
        Answer(Pin22, Pin22En);

        Console.WriteLine("K  tags");
        var tagged = Claim.Replace("de klemarm", "de <t1>klemarm</t1>");
        Answer(tagged, ClaimEn.Replace("clamping arm", "<t1>clamping arm</t1>"));
        var k = Go(Document(tagged));
        Check(k.WithBlock.Count == 1 && k.WithBlock[0].User.Contains(Entry("5", Claim, ClaimEn)), "the earlier texts are shown without the tag placeholders");
        Check(k.WithBlock[0].User.Contains("take the tags from your own segment"), "and the block says so");

        Console.WriteLine("L  Preview prompt's lookup");
        var preview = NewCheck();
        Kept(preview, "segment 2", Claim, ClaimEn);
        var previewSeg = Seg(5, Desc);
        Sent(preview, previewSeg);
        Check(Count(preview, "PreviewReferencesFor", previewSeg) == 1, "shows what is in the document now");
        Check(Summary(preview) == null, "and counts nothing as shown: no run has sent anything");
        Check(Count(preview, "ReferencesFor", previewSeg) == 1 && Summary(preview) == "1 segment was shown an earlier translation of a passage it repeats.",
            "the run's own lookup does count");

        Console.WriteLine("M  how much longer the requests get");
        long onChars = on.Requests.Sum(r => (long)r.User.Length), offChars = off.Requests.Sum(r => (long)r.User.Length);
        var block = on.Requests[2].User.Substring(0, on.Requests[2].User.IndexOf("**SEGMENTS TO TRANSLATE", StringComparison.Ordinal));
        int heading = block.IndexOf("Segment 5", StringComparison.Ordinal);
        Console.WriteLine("       block for one reference: " + block.Length + " characters = " + heading + " of instruction (once per request that has any) + "
            + (block.Length - heading) + " for the reference itself (a " + Claim.Split(' ').Length + "-word source and its translation)");
        Console.WriteLine("       about " + (heading / 4) + " + " + ((block.Length - heading) / 4) + " tokens at 4 characters a token; the system prompt is unchanged");
        Check(onChars - offChars == block.Length, "the run with the feature on sent exactly the block more (" + (onChars - offChars) + " characters), nothing else");
        Check(heading < 1200, "the instruction is under 1,200 characters (" + heading + ")");

        Console.WriteLine("N  10,000 segments");
        var big = NewCheck();
        var random = new Random(20261114);
        var words = Enumerable.Range(0, 2000).Select(n => "w" + n).ToArray();
        var bigSegs = new List<BatchSegment>();
        var sources = new List<string>();
        for (int n = 0; n < 10000; n++) sources.Add(string.Join(" ", Enumerable.Range(0, 30).Select(_ => words[random.Next(2000)])));
        for (int p = 0; p < 1000; p++) {
            var passage = " " + string.Join(" ", Enumerable.Range(0, 14).Select(q => "p" + p + "x" + q));
            sources[p * 3] += passage; sources[9999 - p * 3] += passage + " en verder";
        }
        for (int n = 0; n < 10000; n++) { var s = Seg(n + 1, sources[n]); bigSegs.Add(s); Sent(big, s); }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int first = Count(big, "ReferencesFor", bigSegs[0]);
        long firstMs = clock.ElapsedMilliseconds;
        clock.Restart();
        int shownBig = 0;
        for (int n = 0; n < 10000; n++) {
            if (Count(big, "ReferencesFor", bigSegs[n]) > 0) shownBig++;
            Written(big, bigSegs[n], sources[n].ToUpperInvariant());      // as the run does: written, then known to the next
        }
        long restMs = clock.ElapsedMilliseconds;
        Check(first == 0 && shownBig == 1000, "each of the 1,000 later segments is shown the earlier one, and no earlier one a later (" + shownBig + ")");
        Check(firstMs < 5000, "the first lookup, which finds the document's pairs once: " + firstMs + " ms (limit 5,000)");
        Check(restMs < 3000, "10,000 lookups and 10,000 recorded writes after it: " + restMs + " ms (limit 3,000)");

        Console.WriteLine("   the diagnostic log");
        var log = Path.Combine(temp, "trados", "logs", "diagnostic.log");
        var logText = File.Exists(log) ? File.ReadAllText(log) : "";
        Check(logText.Contains("shown an earlier translation"), "counts how many were shown");
        Check(!logText.Contains("klemarm") && !logText.Contains("clamping"), "and has no text from the document");

        try { _listener.Stop(); } catch { }
    }
}
"@

[InRunReferencesTest]::Install([string[]]@((Split-Path $Dll), $studio))
try {
    [InRunReferencesTest]::Run($temp)
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}

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
if ($clean) { [InRunReferencesTest]::Pass++ ; Write-Host "  ok   the real diagnostic log has nothing from this test" }
else { [InRunReferencesTest]::Fail++ ; Write-Host "  FAIL the real diagnostic log gained lines from this test: $realLog" }
Write-Host ""
Write-Host ("{0} passed, {1} failed" -f [InRunReferencesTest]::Pass, [InRunReferencesTest]::Fail)
if ([InRunReferencesTest]::Fail -gt 0) { exit 1 }
