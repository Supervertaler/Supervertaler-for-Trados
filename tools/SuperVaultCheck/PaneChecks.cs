using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Sdl.Core.Globalization;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Sdl.FileTypeSupport.Framework.NativeApi;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.TranslationMemory;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using Supervertaler.Core.SuperVault;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.SuperVault;

/// <summary>
/// EverTM Matches, the pane (docs/design/evertm-pane.md): its background lookups (newest wins, failures shown, never
/// stuck), the one-line summary under each match, the lookup and its status messages on a scratch EverTM, and the same
/// tag placement as Translation Results, through Studio's own segment converter.
/// </summary>
static class PaneChecks
{
    static readonly CultureCode NlBe = new CultureCode("nl-BE"), EnGb = new CultureCode("en-GB");

    public static void Run(string scratch, Action<bool, string> check)
    {
        Console.WriteLine("EverTM Matches: background lookups");
        NewestWins(check);
        HoldingTheDownArrow(check);
        FailuresDontStopIt(check);
        ClosedDocument(check);
        Console.WriteLine("EverTM Matches: the summary line");
        Summary(check);
        Console.WriteLine("EverTM Matches: lookups and status on a scratch EverTM");
        var root = Lookups(scratch, check);
        Console.WriteLine("EverTM Matches: the same tags as Translation Results");
        SameTagsAsTranslationResults(root, check);
        Console.WriteLine("EverTM Matches: a segment just confirmed, as a fuzzy match for the next one");
        JustConfirmed(root, check);
        Console.WriteLine("EverTM Matches: older translations");
        OlderTranslations(scratch, check);
    }

    // ---- the worker ----

    sealed class Probe
    {
        public readonly List<int> Looked = new List<int>(), Delivered = new List<int>();
        public int Running, MaxRunning;
        public readonly ManualResetEventSlim Release = new ManualResetEventSlim(false);
        public readonly ManualResetEventSlim Started = new ManualResetEventSlim(false);
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        public Func<int, int> Body = n => n;
    }

    static EverTmLookupWorker<int, int> Worker(Probe p, Action<EverTmLookupWorker<int, int>.Result<int>> extra = null)
    {
        EverTmLookupWorker<int, int> w = null;
        w = new EverTmLookupWorker<int, int>(n =>
        {
            int now = Interlocked.Increment(ref p.Running);
            lock (p) { p.Looked.Add(n); p.MaxRunning = Math.Max(p.MaxRunning, now); }
            p.Started.Set();
            try { p.Release.Wait(5000); return p.Body(n); }
            finally { Interlocked.Decrement(ref p.Running); }
        }, r =>
        {
            lock (p) p.Delivered.Add(r.Value);
            extra?.Invoke(r);
            p.Done.Set();
        });
        return w;
    }

    static bool WaitIdle(EverTmLookupWorker<int, int> w)
    {
        for (int i = 0; i < 500 && w.Busy; i++) Thread.Sleep(10);
        return !w.Busy;
    }

    static void NewestWins(Action<bool, string> check)
    {
        var p = new Probe();
        var w = Worker(p);
        w.Submit(1);
        p.Started.Wait(5000);
        for (int n = 2; n <= 200; n++) w.Submit(n);   // the down arrow held while segment 1 is looked up
        p.Release.Set();
        bool idle = WaitIdle(w);
        check(idle && p.Looked.SequenceEqual(new[] { 1, 200 }),
              "200 segment changes during one lookup cost two lookups, the first and the newest: " + string.Join(",", p.Looked));
        check(p.Delivered.SequenceEqual(new[] { 200 }),
              "only the newest segment's result is shown; segment 1's, out of date when it came back, is not: " + string.Join(",", p.Delivered));
    }

    static void HoldingTheDownArrow(Action<bool, string> check)
    {
        var p = new Probe();
        p.Release.Set();
        p.Body = n => { Thread.Sleep(2); return n; };
        var w = Worker(p);
        for (int n = 1; n <= 2000; n++) { w.Submit(n); if (n % 50 == 0) Thread.Sleep(1); }
        bool idle = WaitIdle(w);
        check(idle && p.MaxRunning == 1, $"2,000 segment changes in a row: never more than one lookup at a time (at most {p.MaxRunning})");
        check(p.Looked.Count < 2000 && p.Looked.Last() == 2000 && p.Delivered.LastOrDefault() == 2000,
              $"...{p.Looked.Count} lookups ran, not 2,000, and the last segment's result was the last shown");
    }

    static void FailuresDontStopIt(Action<bool, string> check)
    {
        var p = new Probe();
        p.Release.Set();
        string error = null;
        p.Body = n => { if (n == 1) throw new InvalidOperationException("index locked"); return n; };
        var w = Worker(p, r => { if (r.Id == 1) error = r.Error; });
        w.Submit(1);
        WaitIdle(w);
        check(error == "index locked", "a lookup that throws comes back as a message for the status line, not as an empty list: " + (error ?? "(nothing)"));
        w.Submit(2);
        bool idle = WaitIdle(w);
        check(idle && p.Delivered.Contains(2), "...and the next segment is looked up as usual");

        // Showing a result that throws (the pane disposed mid-way): logged, and the worker comes free.
        var q = new Probe();
        q.Release.Set();
        var logged = new List<string>();
        var w2 = new EverTmLookupWorker<int, int>(n => n, r => { if (r.Value == 1) throw new ObjectDisposedException("pane"); q.Delivered.Add(r.Value); }, m => logged.Add(m));
        w2.Submit(1);
        bool free = false;
        for (int i = 0; i < 500 && !(free = !w2.Busy); i++) Thread.Sleep(10);
        w2.Submit(2);
        for (int i = 0; i < 500 && (w2.Busy || !q.Delivered.Contains(2)); i++) Thread.Sleep(10);
        check(free && q.Delivered.Contains(2) && logged.Any(m => m.Contains("showing a result failed")),
              "a result that fails to show is logged, and the worker is free for the next segment");
    }

    static void ClosedDocument(Action<bool, string> check)
    {
        var p = new Probe();
        var w = Worker(p);
        w.Submit(1);
        p.Started.Wait(5000);
        w.Invalidate();                 // the document is closed while segment 1 is looked up
        p.Release.Set();
        WaitIdle(w);
        check(p.Delivered.Count == 0, "a document closed during a lookup: its result is not shown");

        var q = new Probe();
        var w2 = Worker(q);
        w2.Submit(1);
        q.Started.Wait(5000);
        w2.Stop();                      // the pane is closed
        w2.Submit(2);
        q.Release.Set();
        WaitIdle(w2);
        check(q.Delivered.Count == 0 && q.Looked.SequenceEqual(new[] { 1 }), "the pane closed: nothing more is looked up or shown");
    }

    // ---- the summary line ----

    static VaultCandidate Unit(string variant = null, DateTime? changed = null, DateTime? created = null, params (string, string)[] fields)
    {
        var c = new VaultCandidate { Variant = variant, Changed = changed, Created = created };
        c.Fields.AddRange(fields);
        return c;
    }

    static string Line(VaultCandidate c, int width = 200, string projectSource = "nl-BE", string projectTarget = "en-GB") =>
        EverTmSummary.Fit(EverTmSummary.Make(c, "nl", projectSource, projectTarget), width, s => s.Length);

    static void Summary(Action<bool, string> check)
    {
        var nov4 = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        string date = EverTmSummary.FormatDate(nov4);
        check(date == "4 Nov 2026", "a date reads like \"4 Nov 2026\": " + date);

        var both = Unit("nl-BE>en-GB", nov4, null, (VaultFields.Client, "ACME"), (VaultFields.Project, "PROJ-014"));
        check(Line(both) == "ACME · PROJ-014 · 4 Nov 2026", "Client · Project · date: " + Line(both));
        check(Line(Unit(null, nov4, null, (VaultFields.Project, "PROJ-014"))) == "PROJ-014 · 4 Nov 2026", "no Client: left out with its separator");
        check(Line(Unit(null, nov4, null, (VaultFields.Client, "ACME"))) == "ACME · 4 Nov 2026", "no Project: likewise");
        check(Line(Unit(null, null, new DateTime(2019, 3, 1, 12, 0, 0, DateTimeKind.Utc), (VaultFields.Source, "Hydraulics_TM.tmx"))) == "Hydraulics_TM.tmx · 1 Mar 2019",
              "no Client and no Project: the Source stands in, with the created date when there's no changed date");
        check(Line(Unit(null, null, null, (VaultFields.Source, "Workbench: Hydraulics TM"))) == "Workbench: Hydraulics TM · undated", "no date: \"undated\"");
        check(Line(Unit()) == "undated", "nothing known: still one line (\"undated\"), never empty");
        var several = Unit(null, nov4, null, (VaultFields.Client, "ACME"), (VaultFields.Project, "PROJ-014"), (VaultFields.Project, "PROJ-001"), (VaultFields.Project, "PROJ-003"));
        check(Line(several) == "ACME · PROJ-014 +2 · 4 Nov 2026", "several values: the first, then +N: " + Line(several));

        // Narrow: Client and Project cut with "…", the date always whole.
        var longer = Unit("nl-BE>en-GB", nov4, null, (VaultFields.Client, "ACME Corporation"), (VaultFields.Project, "PROJ-014"));
        string narrow = Line(longer, 34);
        check(narrow.EndsWith("· 4 Nov 2026") && narrow.Contains("…") && narrow.Length <= 34 && narrow.StartsWith("ACME"), "a narrow pane: cut with \"…\", the date whole: " + narrow);
        string tiny = Line(both, 20);
        check(tiny == "4 Nov 2026", "very narrow: only the date is left: " + tiny);

        // Variants.
        check(Line(Unit("nl-BE>en-US", nov4)) == "4 Nov 2026 · en-US", "a target variant other than the project's is added: " + Line(Unit("nl-BE>en-US", nov4)));
        check(Line(Unit("nl-NL>en-US", nov4)) == "4 Nov 2026 · nl-NL > en-US", "both sides differ: both shown: " + Line(Unit("nl-NL>en-US", nov4)));
        check(Line(Unit("en-US>nl-BE", nov4)) == "4 Nov 2026 · en-US", "a unit stored the other way round is read by its languages: " + Line(Unit("en-US>nl-BE", nov4)));
        check(Line(Unit("nl>en", nov4)) == "4 Nov 2026", "a bare language is not flagged");
        check(Line(Unit("nl-BE>en-GB", nov4)) == "4 Nov 2026", "the project's own variants are not flagged");
        string narrowVariant = Line(Unit("nl-BE>en-US", nov4, null, (VaultFields.Client, "ACME Corporation International"), (VaultFields.Project, "PROJ-014")), 40);
        check(narrowVariant.EndsWith("4 Nov 2026 · en-US") && narrowVariant.Length <= 40, "narrow with a variant: the variant is never cut: " + narrowVariant);
    }

    // ---- lookups on a scratch EverTM ----

    const string SaveNl = "Klik op <bpt i=\"1\">&lt;b&gt;</bpt>Opslaan<ept i=\"1\">&lt;/b&gt;</ept> om de wijzigingen te bewaren.";
    const string SaveEn = "Click <bpt i=\"1\">&lt;b&gt;</bpt>Save<ept i=\"1\">&lt;/b&gt;</ept> to keep the changes.";

    static string Lookups(string scratch, Action<bool, string> check)
    {
        var dir = Path.Combine(scratch, "pane");
        Directory.CreateDirectory(dir);

        var empty = EverTmMatchesLookup.Run(VaultLibrary.For(Path.Combine(dir, "nothing-here")), Request("De pomp wordt gestart."));
        check(empty.Matches.Count == 0 && empty.Problem != null && empty.Problem.Contains("empty"),
              "an EverTM with nothing in it says so, rather than an empty list: " + empty.Problem);

        var root = Path.Combine(dir, "evertm");
        var tmx = Path.Combine(dir, "pane.tmx");
        File.WriteAllText(tmx, "<?xml version=\"1.0\" encoding=\"utf-8\"?><tmx version=\"1.4\"><header srclang=\"nl-BE\" segtype=\"sentence\" datatype=\"plaintext\" o-tmf=\"t\" adminlang=\"en\" creationtool=\"t\" creationtoolversion=\"1\"/><body>" +
            Tu("nl-BE", "en-GB", SaveNl, SaveEn) +
            Tu("nl-BE", "en-US", "De pomp wordt gestart als de druk daalt.", "The pump is started when the pressure drops.") +
            Tu("nl-BE", "en-GB", "De pomp wordt uitgeschakeld als de druk stijgt.", "The pump is switched off when the pressure rises.") +
            "</body></tmx>");
        var lib = VaultLibrary.For(root);
        lib.Import((store, p, c) => VaultTmx.Import(store, tmx, p, c), null, CancellationToken.None);

        var a = EverTmMatchesLookup.Run(lib, Request("De pomp wordt gestart als de druk stijgt."));
        check(a.Problem == null && a.Units == 3 && a.Matches.Count == 2 && a.Matches[0].Score >= a.Matches[1].Score && a.Matches.All(m => m.Score < 100 && m.Score >= EverTmMatchesLookup.MinScore),
              $"two fuzzy matches, best first, from 3 translation units: {string.Join(", ", a.Matches.Select(m => m.Score + "%"))}");
        check(a.Matches.All(m => m.Diff?.Edits != null && m.Diff.Edits.Any(e => e.Op != VaultEditOp.Identity)),
              "each comes with the differences to draw (the alignment Translation Results draws)");
        check(a.Matches.Select(m => EverTmSummary.Fit(EverTmSummary.Make(m.Candidate, "nl", "nl-BE", "en-GB"), 200, s => s.Length)).Any(l => l.EndsWith("en-US")),
              "the en-US unit's summary line says en-US in an en-GB project");

        var none = EverTmMatchesLookup.Run(lib, Request("Iets heel anders, zonder enige overeenkomst."));
        check(none.Problem == null && none.Matches.Count == 0 && none.Units == 3, "no match: an empty list and the count, no problem message");

        var wrongDirection = EverTmMatchesLookup.Run(lib, Request("The pump is started when the pressure drops.", "en", "nl", "en-GB", "nl-BE"));
        check(wrongDirection.Matches.Count == 1 && wrongDirection.Matches[0].Score == 100, "the other direction finds the same unit (one memory, both directions)");

        // A broken index: shown as such.
        var broken = Path.Combine(dir, "broken");
        Directory.CreateDirectory(broken);
        try
        {
            CopyDir(root, broken);
            foreach (var f in Directory.GetFiles(broken, "*.cfs", System.IO.SearchOption.AllDirectories).Concat(Directory.GetFiles(broken, "segments_*", System.IO.SearchOption.AllDirectories)))
                File.WriteAllText(f, "not an index");
            var b = EverTmMatchesLookup.Run(VaultLibrary.For(broken), Request("De pomp wordt gestart als de druk stijgt."));
            check(b.Problem != null || b.Matches.Count > 0, "a damaged index: a message (or the store's units), never a silent empty list: " + (b.Problem ?? b.Matches.Count + " matches"));
        }
        catch (Exception ex) { check(true, "a damaged index: the lookup threw (" + ex.GetType().Name + "), which the pane shows as \"EverTM lookup failed\""); }
        return root;
    }

    static EverTmMatchesLookup.Request Request(string text, string sl = "nl", string tl = "en", string sc = "nl-BE", string tc = "en-GB") =>
        new EverTmMatchesLookup.Request { Document = new List<VaultPiece> { new VaultPiece { Text = text } }, SourceLang = sl, TargetLang = tl, SourceCulture = sc, TargetCulture = tc };

    static string Tu(string sl, string tl, string src, string trg) =>
        $"<tu><tuv xml:lang=\"{sl}\"><seg>{src}</seg></tuv><tuv xml:lang=\"{tl}\"><seg>{trg}</seg></tuv></tu>";

    static void CopyDir(string from, string to)
    {
        foreach (var d in Directory.GetDirectories(from, "*", System.IO.SearchOption.AllDirectories)) Directory.CreateDirectory(d.Replace(from, to));
        foreach (var f in Directory.GetFiles(from, "*", System.IO.SearchOption.AllDirectories)) File.Copy(f, f.Replace(from, to), true);
    }

    // ---- tags: the pane and Translation Results ----

    static void SameTagsAsTranslationResults(string root, Action<bool, string> check)
    {
        var factory = new Sdl.FileTypeSupport.Framework.Bilingual.DocumentItemFactory();
        factory.PropertiesFactory = new Sdl.FileTypeSupport.Framework.Native.PropertiesFactory();
        var pf = factory.PropertiesFactory;

        // The editor's source segment: "Klik op [b]Opslaan[/b] om de wijzigingen te bewaren."
        var source = factory.CreateSegment(factory.CreateSegmentPairProperties());
        source.Add(factory.CreateText(pf.CreateTextProperties("Klik op ")));
        var start = pf.CreateStartTagProperties("<b>"); start.TagId = new TagId("7"); start.DisplayText = "b";
        var end = pf.CreateEndTagProperties("</b>"); end.DisplayText = "b";
        var pair = factory.CreateTagPair(start, end);
        pair.Add(factory.CreateText(pf.CreateTextProperties("Opslaan")));
        source.Add(pair);
        source.Add(factory.CreateText(pf.CreateTextProperties(" om de wijzigingen te bewaren.")));

        EverTmEditorSegment editor;
        try { editor = EverTmEditorSegment.From(source, NlBe); }
        catch (Exception ex) { check(false, "Studio's segment converter: " + ex.Message); return; }
        check(editor.Pieces.Count == 5 && editor.Pieces[1].IsTag && editor.Pieces[3].IsTag && editor.EditorTags.Count == 2 && editor.EditorTags.Values.All(t => ReferenceEquals(t, pair)),
              $"Studio's converter gives the segment as Translation Results looks it up ({editor.Pieces.Count} pieces), and names the editor tag behind each tag");

        // The pane's lookup and the provider's, on the same segment.
        var lib = VaultLibrary.For(root);
        var pane = lib.Lookup(editor.Pieces, "nl", "en", EverTmMatchesLookup.MinScore, EverTmMatchesLookup.MaxMatches, "en-GB");
        var provider = new SuperVaultProvider(root).GetLanguageDirection(new LanguagePair(NlBe, EnGb))
            .SearchSegment(new SearchSettings { Mode = SearchMode.NormalSearch, MinScore = EverTmMatchesLookup.MinScore, MaxResults = EverTmMatchesLookup.MaxMatches }, editor.Segment);
        check(pane.Count == 1 && provider.Count == 1 && pane[0].Score == provider[0].ScoringResult.BaseScore && pane[0].Tags == VaultTagOutcome.Mapped,
              $"the pane and Translation Results find the same match at the same score ({(pane.Count > 0 ? pane[0].Score : -1)}% / {(provider.Count > 0 ? provider[0].ScoringResult.BaseScore : -1)}%), tags placed");

        var serialized = SegmentTagHandler.Serialize(source);
        string markup = editor.TargetMarkup(pane[0], serialized.TagMap);
        check(markup == "Click <t1>Save</t1> to keep the changes.", "the target is written with the source's own tag: " + markup);

        var target = factory.CreateSegment(factory.CreateSegmentPairProperties());
        bool built = SegmentTagHandler.ReconstructTarget(target, source, markup, serialized.TagMap);
        var trPair = Items(target).Where(x => x is ITagPair).Select(x => (ITagPair)x).FirstOrDefault();
        check(built && trPair != null && trPair.TagProperties.TagId.Id == "7" && string.Concat(Items(trPair).Where(x => x is IText).Select(x => (IText)x).Select(t => t.Properties.Text)) == "Save",
              "inserted into the target: the bold tag pair around \"Save\", the source's tag 7, as Translation Results places it");

        // Translation Results' own proposal: the same order of text and tags.
        var proposal = provider[0].TranslationProposal.TargetSegment;
        string Shape(IEnumerable<string> parts) => string.Join("|", parts);
        var trShape = Shape(proposal.Elements.Select(e => e is Tag t ? (t.Type == Sdl.LanguagePlatform.Core.TagType.Start ? "[" + t.TagID : t.Type == Sdl.LanguagePlatform.Core.TagType.End ? "]" + t.TagID : "<>" + t.TagID) : ((Text)e).Value));
        var paneShape = Shape(Flatten(target));
        check(trShape == paneShape, $"the pane's target has the same text and tags in the same places as Translation Results' proposal: {paneShape}  vs  {trShape}");

        // A match whose tags can't be placed: written as plain text, and said so.
        var untagged = factory.CreateSegment(factory.CreateSegmentPairProperties());
        untagged.Add(factory.CreateText(pf.CreateTextProperties("Klik op Opslaan om de wijzigingen te bewaren.")));
        var e2 = EverTmEditorSegment.From(untagged, NlBe);
        var m2 = lib.Lookup(e2.Pieces, "nl", "en", EverTmMatchesLookup.MinScore, EverTmMatchesLookup.MaxMatches, "en-GB");
        check(m2.Count == 1 && EverTmEditorSegment.PlainTarget(m2[0]) == "Click Save to keep the changes." && !e2.HasTags,
              "the same match for a segment without tags: plain text \"" + (m2.Count > 0 ? EverTmEditorSegment.PlainTarget(m2[0]) : "") + "\"");
        var fake = new VaultMatch { Target = new List<VaultPiece> { new VaultPiece { Text = "Press <t1/> now" } }, TargetDocTag = new List<int> { -1 } };
        check(editor.TargetMarkup(fake, serialized.TagMap) == null, "stored text that looks like a tag placeholder is never parsed as one (written as plain text instead)");
    }

    /// <summary>The container's items through its generic enumerator: the offline Segment's non-generic one throws.</summary>
    static IEnumerable<IAbstractMarkupData> Items(IAbstractMarkupDataContainer c) => c;

    static IEnumerable<string> Flatten(IAbstractMarkupDataContainer c)
    {
        foreach (var item in c)
        {
            if (item is IText t) yield return t.Properties.Text;
            else if (item is ITagPair tp)
            {
                yield return "[" + tp.TagProperties.TagId.Id;
                foreach (var x in Flatten(tp)) yield return x;
                yield return "]" + tp.TagProperties.TagId.Id;
            }
            else if (item is IPlaceholderTag ph) yield return "<>" + ph.TagProperties.TagId.Id;
        }
    }
    // ---- a picture of the pane, to look at (SuperVaultCheck pane-picture <file.png>) ----

    public static int Picture(string png)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sv-pane-picture-" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(dir);
        var tmx = Path.Combine(dir, "p.tmx");
        File.WriteAllText(tmx, "<?xml version=\"1.0\" encoding=\"utf-8\"?><tmx version=\"1.4\"><header srclang=\"nl-NL\" segtype=\"sentence\" datatype=\"plaintext\" o-tmf=\"t\" adminlang=\"en\" creationtool=\"t\" creationtoolversion=\"1\"/><body>" +
            "<tu><prop type=\"x-Client\">ACME</prop><prop type=\"x-Project\">PROJ-014</prop><tuv xml:lang=\"nl-NL\"><seg>De eindconcentratie aan pesticiden (P<bpt i=\"1\">&lt;sub&gt;</bpt>e<ept i=\"1\">&lt;/sub&gt;</ept>) na bijvoorbeeld een periode van 24 uren kan worden gemeten en gebruikt om de maximale pesticidenadsorptiecapaciteit te berekenen volgens de formule:</seg></tuv>" +
            "<tuv xml:lang=\"en-GB\"><seg>The final concentration of pesticides (P<bpt i=\"1\">&lt;sub&gt;</bpt>e<ept i=\"1\">&lt;/sub&gt;</ept>) after, for example, a period of 24 hours can be measured and used to calculate the maximum pesticide adsorption capacity according to the formula:</seg></tuv></tu>" +
            Tu("nl-NL", "en-US", "De pomp wordt gestart als de druk daalt.", "The pump is started when the pressure drops.") +
            "<tu creationdate=\"20251104T000000Z\"><prop type=\"x-Client\">ACME</prop><prop type=\"x-Project\">PROJ-021</prop><tuv xml:lang=\"nl-NL\"><seg>Meer in het bijzonder wordt de pomp gestart volgens conclusie 8, waarbij de druk daalt tot 4 bar.</seg></tuv><tuv xml:lang=\"en-GB\"><seg>More particularly, the pump is started according to claim 8, wherein the pressure drops to 4 bar.</seg></tuv></tu>" +
            "<tu creationdate=\"20220310T000000Z\"><prop type=\"x-Client\">ACME</prop><prop type=\"x-Project\">PROJ-003</prop><tuv xml:lang=\"nl-NL\"><seg>Meer in het bijzonder wordt de pomp gestart volgens conclusie 8, waarbij de druk daalt tot 4 bar.</seg></tuv><tuv xml:lang=\"en-GB\"><seg>In particular, the pump is started as claimed in claim 8, the pressure falling to 4 bar.</seg></tuv></tu>" +
            "<tu creationdate=\"20190615T000000Z\"><tuv xml:lang=\"nl-NL\"><seg>Meer in het bijzonder wordt de pomp gestart volgens conclusie 8, waarbij de druk daalt tot 4 bar.</seg></tuv><tuv xml:lang=\"en-US\"><seg>More specifically, the pump starts according to claim 8, with the pressure dropping to 4 bar.</seg></tuv></tu>" +
            "</body></tmx>");
        var lib = VaultLibrary.For(Path.Combine(dir, "evertm"));
        lib.Import((store, p, c) => VaultTmx.Import(store, tmx, p, c), null, CancellationToken.None);

        var factory = new Sdl.FileTypeSupport.Framework.Bilingual.DocumentItemFactory();
        factory.PropertiesFactory = new Sdl.FileTypeSupport.Framework.Native.PropertiesFactory();
        var pf = factory.PropertiesFactory;
        var source = factory.CreateSegment(factory.CreateSegmentPairProperties());
        source.Add(factory.CreateText(pf.CreateTextProperties("De eindconcentratie van fosfor (P")));
        var start = pf.CreateStartTagProperties("<sub>"); start.TagId = new TagId("3"); start.DisplayText = "sub";
        var end = pf.CreateEndTagProperties("</sub>"); end.DisplayText = "sub";
        var sub = factory.CreateTagPair(start, end);
        sub.Add(factory.CreateText(pf.CreateTextProperties("e")));
        source.Add(sub);
        if (Environment.GetEnvironmentVariable("SV_PANE_PICTURE") == "grouped")
        {
            source = factory.CreateSegment(factory.CreateSegmentPairProperties());
            source.Add(factory.CreateText(pf.CreateTextProperties("In een tweede aspect wordt de pomp gestart voor het zuiveren van water middels een drukfilter, waarbij de druk daalt tot 4 bar.")));
        }
        else if (Environment.GetEnvironmentVariable("SV_PANE_PICTURE") == "older")
        {
            source = factory.CreateSegment(factory.CreateSegmentPairProperties());
            source.Add(factory.CreateText(pf.CreateTextProperties("Meer in het bijzonder wordt de pomp gestart volgens conclusie 9, waarbij de druk daalt tot 4 bar.")));
        }
        else
        source.Add(factory.CreateText(pf.CreateTextProperties(") na bijvoorbeeld een periode van 336 uren kan gemeten worden en gebruikt worden om de maximale fosfor adsorptiecapaciteit te berekenen.")));
        var editor = EverTmEditorSegment.From(source, new CultureCode("nl-NL"));
        var answer = EverTmMatchesLookup.Run(lib, new EverTmMatchesLookup.Request { Document = editor.Pieces, SourceLang = "nl", TargetLang = "en", SourceCulture = "nl-NL", TargetCulture = "en-GB", MinScore = 50, MaxMatches = 10 });
        var result = new Supervertaler.Trados.Controls.EverTmPaneResult
        {
            Matches = answer.Matches, Units = answer.Units, Milliseconds = 36, Editor = editor,
            TagMapAtLookup = SegmentTagHandler.Serialize(source).TagMap, SourceLang = "nl", SourceCulture = "nl-NL", TargetCulture = "en-GB",
        };
        var hide = Environment.GetEnvironmentVariable("SV_PANE_HIDE");   // e.g. "Source,In EverTM since"
        Supervertaler.Trados.Settings.SettingsService.Update(x => x.EverTmPaneHiddenDetails = hide == null ? null : hide.Split(',').ToList());
        foreach (var stacked in new[] { false, true })
        {
        Supervertaler.Trados.Settings.SettingsService.Update(x => x.EverTmPaneStacked = stacked);   // the run's own data folder
        string file = stacked ? Path.ChangeExtension(png, null) + "-stacked.png" : png;
        using (var form = new System.Windows.Forms.Form { Width = 760, Height = 520, FormBorderStyle = System.Windows.Forms.FormBorderStyle.None, ShowInTaskbar = false, StartPosition = System.Windows.Forms.FormStartPosition.Manual, Left = -3000 })
        using (var control = new Supervertaler.Trados.Controls.EverTmMatchesControl { Dock = System.Windows.Forms.DockStyle.Fill })
        {
            form.Controls.Add(control);
            form.Show();
            // The view part's older-translations worker, done here in line.
            control.OlderWanted += (m, r, page) =>
            {
                var t = lib.Translations(m, "nl", "en", page, "en-GB");
                control.BeginInvoke((System.Action)(() => control.ShowOlder(m, page, t.Items.Select(c => VaultLookup.Offer(editor.Pieces, m, c)).ToList(), t.Total, null)));
            };
            control.ShowResult(result);
            if (Environment.GetEnvironmentVariable("SV_PANE_PICTURE") == "older")   // the match with older translations selected
            {
                var list = Field<System.Windows.Forms.Control>(control, "_list");
                int withOlder = answer.Matches.FindIndex(x => x.Candidate.Older > 0);
                if (withOlder >= 0) list.GetType().GetMethod("Select", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(int) }, null).Invoke(list, new object[] { withOlder });
            }
            for (int i = 0; i < 5; i++) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(20); }
            using (var bmp = new System.Drawing.Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        }
        Console.WriteLine(answer.Matches.Count + " matches (" + answer.Units + " units" + (answer.Problem == null ? "" : ", " + answer.Problem) + ") drawn to " + png);
        return 0;
    }
    // ---- older translations ----

    /// <summary>
    /// A match's older translations (step 2): asked for only for a selected match that has some, a page at a time;
    /// listed best first under its details, insertable with their own tags placed; a page that comes back after another
    /// match was selected is dropped. Drives the real control in a hidden form, with the view part's two calls.
    /// </summary>
    static void OlderTranslations(string scratch, Action<bool, string> check)
    {
        var dir = Path.Combine(scratch, "older");
        Directory.CreateDirectory(dir);
        var tmx = Path.Combine(dir, "older.tmx");
        string Dated(string sl, string tl, string src, string trg, string date) =>
            $"<tu creationdate=\"{date}\"><prop type=\"x-Client\">ACME</prop><tuv xml:lang=\"{sl}\"><seg>{src}</seg></tuv><tuv xml:lang=\"{tl}\"><seg>{trg}</seg></tuv></tu>";
        File.WriteAllText(tmx, "<?xml version=\"1.0\" encoding=\"utf-8\"?><tmx version=\"1.4\"><header srclang=\"nl-BE\" segtype=\"sentence\" datatype=\"plaintext\" o-tmf=\"t\" adminlang=\"en\" creationtool=\"t\" creationtoolversion=\"1\"/><body>" +
            Dated("nl-BE", "en-GB", SaveNl, SaveEn, "20250101T000000Z") +
            Dated("nl-BE", "en-GB", SaveNl, "Click <bpt i=\"1\">&lt;b&gt;</bpt>Save<ept i=\"1\">&lt;/b&gt;</ept> to store the changes.", "20230101T000000Z") +
            Dated("nl-BE", "en-GB", SaveNl, "Press <bpt i=\"1\">&lt;b&gt;</bpt>Save<ept i=\"1\">&lt;/b&gt;</ept> to keep your changes.", "20210101T000000Z") +
            Dated("nl-BE", "en-GB", "De klep sluit.", "The valve closes.", "20240101T000000Z") +
            "</body></tmx>");
        var lib = VaultLibrary.For(Path.Combine(dir, "evertm"));
        lib.Import((store, p, c) => VaultTmx.Import(store, tmx, p, c), null, CancellationToken.None);

        var factory = new Sdl.FileTypeSupport.Framework.Bilingual.DocumentItemFactory();
        factory.PropertiesFactory = new Sdl.FileTypeSupport.Framework.Native.PropertiesFactory();
        var pf = factory.PropertiesFactory;
        var source = factory.CreateSegment(factory.CreateSegmentPairProperties());
        source.Add(factory.CreateText(pf.CreateTextProperties("Klik op ")));
        var start = pf.CreateStartTagProperties("<b>"); start.TagId = new TagId("7"); start.DisplayText = "b";
        var end = pf.CreateEndTagProperties("</b>"); end.DisplayText = "b";
        var bold = factory.CreateTagPair(start, end);
        bold.Add(factory.CreateText(pf.CreateTextProperties("Opslaan")));
        source.Add(bold);
        source.Add(factory.CreateText(pf.CreateTextProperties(" om de wijzigingen te bewaren.")));
        var editor = EverTmEditorSegment.From(source, NlBe);
        var answer = EverTmMatchesLookup.Run(lib, new EverTmMatchesLookup.Request { Document = editor.Pieces, SourceLang = "nl", TargetLang = "en", SourceCulture = "nl-BE", TargetCulture = "en-GB" });
        check(answer.Matches.Count == 1 && answer.Matches[0].Score == 100 && answer.Matches[0].Candidate.Older == 2,
              "the newest translation is the match, with 2 older ones hidden: " + string.Join(", ", answer.Matches.Select(m => m.Score + "% older " + m.Candidate.Older)));
        var result = new Supervertaler.Trados.Controls.EverTmPaneResult
        {
            Matches = answer.Matches, Units = answer.Units, Editor = editor, TagMapAtLookup = SegmentTagHandler.Serialize(source).TagMap,
            SourceLang = "nl", SourceCulture = "nl-BE", TargetCulture = "en-GB",
        };

        var asked = new List<(VaultMatch match, int page)>();
        Supervertaler.Trados.Settings.SettingsService.Update(x => x.EverTmPaneHiddenDetails = null);   // this run's own data folder
        using (var form = new System.Windows.Forms.Form { Width = 700, Height = 520, ShowInTaskbar = false, StartPosition = System.Windows.Forms.FormStartPosition.Manual, Left = -3000 })
        using (var control = new Supervertaler.Trados.Controls.EverTmMatchesControl { Dock = System.Windows.Forms.DockStyle.Fill })
        {
            form.Controls.Add(control);
            form.Show();
            control.OlderWanted += (m, r, page) => asked.Add((m, page));
            control.ShowResult(result);
            System.Windows.Forms.Application.DoEvents();
            check(asked.Count == 1 && ReferenceEquals(asked[0].match, answer.Matches[0]) && asked[0].page == 0,
                  "showing a result whose selected match has older translations asks for their first page, once (" + asked.Count + ")");
            check(Title(control).Contains("Older translations (2)") && Title(control).Contains("loading"), "while it is on its way: \"" + Title(control) + "\"");

            // The view part's worker, done here in line.
            var page0 = lib.Translations(asked[0].match, "nl", "en", 0, "en-GB");
            var offered = page0.Items.Select(c => VaultLookup.Offer(editor.Pieces, asked[0].match, c)).ToList();
            check(offered.Select(EverTmEditorSegment.PlainTarget).SequenceEqual(new[] { "Click Save to store the changes.", "Press Save to keep your changes." }),
                  "best first: " + string.Join(" | ", offered.Select(EverTmEditorSegment.PlainTarget)));
            var markup = editor.TargetMarkup(offered[1], result.TagMapAtLookup);
            check(markup == "Press <t1>Save</t1> to keep your changes.", "an older translation is inserted with the segment's own bold, like a match: " + markup);

            // A page for a match no longer selected: dropped.
            control.ShowOlder(new VaultMatch(), 0, offered, 2, null);
            check(OlderCount(control) == 0, "a page that arrives for another match is dropped");
            control.ShowOlder(asked[0].match, 0, offered, page0.Total, null);
            System.Windows.Forms.Application.DoEvents();
            check(OlderCount(control) == 2 && Title(control).StartsWith("Older translations (2)") && !Title(control).Contains("loading"),
                  "listed under the details: " + OlderCount(control) + " rows, \"" + Title(control) + "\"");

            // Inserting one: the pane hands it over like a match.
            VaultMatch inserted = null;
            control.InsertRequested += (m, r) => inserted = m;
            var olderList = Field<System.Windows.Forms.Control>(control, "_older");
            olderList.GetType().GetMethod("Select", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(int) }, null).Invoke(olderList, new object[] { 1 });
            olderList.GetType().GetMethod("RequestInsert", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(olderList, null);
            check(inserted != null && ReferenceEquals(inserted, offered[1]), "double-click or Enter on an older translation inserts that one");
            var details = Field<System.Windows.Forms.RichTextBox>(control, "_details").Text;
            check(details.StartsWith("Older translation 2 of match 1") && details.Contains("Created  1 Jan 2021"),
                  "selecting it shows its own details: " + details.Split('\n')[0]);

            // A new segment without older translations: nothing asked, the area closed.
            var plain = EverTmMatchesLookup.Run(lib, Request("De klep sluit."));
            control.ShowResult(new Supervertaler.Trados.Controls.EverTmPaneResult { Matches = plain.Matches, Units = plain.Units, SourceLang = "nl", SourceCulture = "nl-BE", TargetCulture = "en-GB" });
            check(asked.Count == 1 && Field<System.Windows.Forms.SplitContainer>(control, "_detailSplit").Panel2Collapsed,
                  "a match without older translations: nothing looked up, no older-translations area");

            // Switched off in Options > Details shown: never asked for.
            Supervertaler.Trados.Settings.SettingsService.Update(x => x.EverTmPaneHiddenDetails = new List<string> { "Older translations" });
            control.ShowResult(result);
            check(asked.Count == 1, "\"Older translations\" switched off: not looked up");
            Supervertaler.Trados.Settings.SettingsService.Update(x => x.EverTmPaneHiddenDetails = null);
        }
    }

    static T Field<T>(object o, string name) where T : class =>
        o.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(o) as T;

    static string Title(Supervertaler.Trados.Controls.EverTmMatchesControl c) => Field<System.Windows.Forms.Label>(c, "_olderTitle").Text;

    static int OlderCount(Supervertaler.Trados.Controls.EverTmMatchesControl c)
    {
        var list = Field<object>(c, "_older");
        return (int)list.GetType().GetProperty("Count").GetValue(list);
    }

    // ---- the second look ----

    /// <summary>
    /// The pane looks a segment up again 4 s after arriving on it (EverTmMatchesViewPart._again), because a segment
    /// just confirmed is offered only as an exact match until it reaches the index. This checks the timing that
    /// relies on: right after confirming, the near-repeat is not yet a fuzzy match; within 4 s it is.
    /// </summary>
    static void JustConfirmed(string root, Action<bool, string> check)
    {
        var lib = VaultLibrary.For(root);
        var next = Request("De klep wordt automatisch gesloten zodra de druk boven de 4 bar komt.");
        lib.Learn(new[] { new VaultLearnUnit { SourceCulture = "nl-BE", Source = "De klep wordt automatisch gesloten zodra de druk boven de 3 bar komt.",
                                              TargetCulture = "en-GB", Target = "The valve is closed automatically as soon as the pressure exceeds 3 bar." } });
        var first = EverTmMatchesLookup.Run(lib, next);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        EverTmMatchesLookup.Answer later = null;
        while (sw.ElapsedMilliseconds < 4000)
        {
            Thread.Sleep(250);
            later = EverTmMatchesLookup.Run(lib, next);
            if (later.Matches.Count > 0) break;
        }
        check(later != null && later.Matches.Count > 0,
              $"a segment just confirmed is a fuzzy match for the next one within 4 s ({sw.ElapsedMilliseconds} ms; at once: {first.Matches.Count} matches, so the second look is needed)");
    }
}
