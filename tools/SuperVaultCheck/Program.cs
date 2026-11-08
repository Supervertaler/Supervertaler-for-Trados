using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Data.Sqlite;
using Sdl.Core.Globalization;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.Core.Tokenization;
using Sdl.LanguagePlatform.TranslationMemory;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using Supervertaler.Core.SuperVault;
using Supervertaler.Trados.SuperVault;

/// <summary>See SuperVaultCheck.csproj. Exit code 0 = every check passed.</summary>
static class Program
{
    static string StudioDir => Environment.Is64BitProcess
        ? @"C:\Program Files\Trados\Trados Studio\Studio19"
        : @"C:\Program Files (x86)\Trados\Trados Studio\Studio18";

    static int Main(string[] args)
    {
        // As in Studio: Studio's folder first, then ours (the plugin's AssemblyResolve hook does the second part there).
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            var name = new AssemblyName(e.Name).Name + ".dll";
            foreach (var dir in new[] { StudioDir, AppDomain.CurrentDomain.BaseDirectory })
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p)) return Assembly.LoadFrom(p);
            }
            return null;
        };
        if (args.Length > 0 && args[0] == "kit")
            return Kit.Run(args.Length > 1 ? args[1] : "SuperVault acceptance", args.Length > 2 ? args[2] : @"D:\Supervertaler\resources\supervertaler.db");
        if (args.Length > 1 && args[0] == "lookup-xliff")
            return XliffLookup.Run(args[1], args.Length > 2 ? args[2] : null);
        if (args.Length > 1 && args[0] == "import-sdltm")
            return RealTm.Run(args[1], args.Length > 2 ? args[2] : null);
        return Checks.Run(args);
    }
}

static class Checks
{
    static int _failed, _passed;
    static void Check(bool ok, string what)
    {
        if (ok) _passed++; else _failed++;
        Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + what);
    }

    static readonly CultureCode NlBe = new CultureCode("nl-BE"), EnUs = new CultureCode("en-US");
    static string _scratch;

    public static int Run(string[] args)
    {
        Console.WriteLine($"{(Environment.Is64BitProcess ? "64" : "32")}-bit, Studio TM API {typeof(Cascade<>).Assembly.GetName().Version}");
        _scratch = Path.Combine(Path.GetTempPath(), "sv-supervault-check-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_scratch);
        try
        {
            PluginReferencesArePublic();
            var root = BuildScratchVault();
            var clientTm = ClientTm();
            var vault = new SuperVaultProvider(root);
            LanguageDirections(vault);
            NeverLowersTheClientTm(vault, clientTm);
            TagsAndPenalties(vault);
            FuzzyDifferences(vault);
            Concordance(vault, clientTm);
            Learning(vault);
            FailureModes(clientTm);
            if (args.Length > 0) Scale(args[0], args.Length > 1 ? args[1] : @"D:\Supervertaler\resources\supervertaler.db");
        }
        catch (Exception ex) { _failed++; Console.WriteLine("  FAIL  unexpected: " + ex); }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_scratch, true); } catch { Console.WriteLine("(scratch left at " + _scratch + ")"); }
        }
        Console.WriteLine(_failed == 0 ? $"ALL {_passed} CHECKS PASSED" : $"{_failed} FAILED, {_passed} passed");
        return _failed == 0 ? 0 : 1;
    }

    // ---- fixtures ----

    // Units for SuperVault, as a TMX file would carry them (tags as <bpt>/<ept>/<ph>).
    static readonly (string src, string trg, string sl, string tl)[] VaultUnits =
    {
        ("Klik op <bpt i=\"1\">&lt;b&gt;</bpt>Opslaan<ept i=\"1\">&lt;/b&gt;</ept> om de wijzigingen te bewaren.", "Click <bpt i=\"1\">&lt;b&gt;</bpt>Save<ept i=\"1\">&lt;/b&gt;</ept> to keep the changes.", "nl", "en"),
        ("De pomp wordt gestart.", "The pump is started.", "nl-BE", "en-US"),
        ("Druk op Start.", "Press Start.", "nl", "en"),                                             // stored without formatting
        ("The valve is closed automatically.", "De klep wordt automatisch gesloten.", "en-GB", "nl-NL"), // stored the other way round
        ("Het apparaat moet worden schoongemaakt na elk gebruik.", "The device must be cleaned after each use.", "nl-NL", "en-GB"),
    };

    static string _vaultTmx;

    static string WriteTmx(string name, IEnumerable<(string src, string trg, string sl, string tl)> units)
    {
        var path = Path.Combine(_scratch, name);
        var sb = new System.Text.StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><tmx version=\"1.4\"><header srclang=\"*all*\" segtype=\"sentence\" datatype=\"plaintext\" o-tmf=\"t\" adminlang=\"en\" creationtool=\"t\" creationtoolversion=\"1\"/><body>");
        foreach (var u in units)
            sb.Append("<tu><tuv xml:lang=\"").Append(u.sl).Append("\"><seg>").Append(u.src).Append("</seg></tuv><tuv xml:lang=\"").Append(u.tl).Append("\"><seg>").Append(u.trg).Append("</seg></tuv></tu>");
        sb.Append("</body></tmx>");
        File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(false));
        return path;
    }

    /// <summary>SuperVault filled the way a user fills it: a TMX file, and a Studio TM (its own tags, through Studio's public API).</summary>
    static string BuildScratchVault()
    {
        _vaultTmx = WriteTmx("vault.tmx", VaultUnits);
        var studioTm = new FileBasedTranslationMemory(Path.Combine(_scratch, "for-supervault.sdltm"), "for SuperVault", NlBe, EnUs,
            FuzzyIndexes.SourceWordBased, BuiltinRecognizers.RecognizeAll, TokenizerFlags.DefaultFlags, WordCountFlags.DefaultFlags);
        studioTm.Save();
        studioTm.LanguageDirection.AddTranslationUnitsMasked(new[] { Tu(WaterDoc(), Open("5", 1), "Never", Close("5", 1), " use water.") }, new ImportSettings(), new[] { true });

        var lib = VaultLibrary.For(Path.Combine(_scratch, "vault"));
        var r1 = lib.Import((store, p, c) => VaultTmx.Import(store, _vaultTmx, p, c), null, CancellationToken.None);
        var r2 = lib.Import((store, p, c) => SuperVaultImport.Sdltm(store, studioTm.FilePath, p, c), null, CancellationToken.None);
        var r3 = lib.Import((store, p, c) => VaultTmx.Import(store, _vaultTmx, p, c), null, CancellationToken.None);
        Check(r1.Added == 5 && r2.Added == 1 && r3.Added == 0 && r3.Duplicates == 5 && lib.Status.Units == 6,
              $"SuperVault filled without Workbench: TMX {r1.Added}, Studio TM {r2.Added}, same TMX again {r3.Added} added / {r3.Duplicates} duplicates; index {lib.Status.Units} units");
        return lib.Root;
    }

    static void Exec(SqliteConnection c, string sql) { using (var cmd = c.CreateCommand()) { cmd.CommandText = sql; cmd.ExecuteNonQuery(); } }

    // Document segments as Studio gives them: Text and Tag elements, Studio-style tag ids.
    static Segment Doc(params object[] parts)
    {
        var s = new Segment(NlBe);
        foreach (var p in parts) if (p is string t) s.Add(t); else s.Add((Tag)p);
        return s;
    }
    static Tag Open(string id, int anchor) => new Tag(TagType.Start, id, anchor, anchor, null);
    static Tag Close(string id, int anchor) => new Tag(TagType.End, id, anchor, 0, null);
    static Segment SaveDoc() => Doc("Klik op ", Open("pt12", 1), "Opslaan", Close("pt12", 1), " om de wijzigingen te bewaren.");
    static Segment PumpDoc() => Doc("De pomp wordt gestart.");
    static Segment StartDoc() => Doc("Druk op ", Open("pt3", 1), "Start", Close("pt3", 1), ".");
    static Segment ValveDoc() => Doc("De klep wordt automatisch gesloten.");
    static Segment WaterDoc() => Doc("Gebruik ", Open("pt7", 1), "nooit", Close("pt7", 1), " water.");

    /// <summary>A normal client TM, as a translator has it: formatted translations of the same sentences, one different.</summary>
    static ITranslationProviderLanguageDirection ClientTm()
    {
        var tm = new FileBasedTranslationMemory(Path.Combine(_scratch, "client.sdltm"), "client", NlBe, EnUs,
            FuzzyIndexes.SourceWordBased | FuzzyIndexes.TargetWordBased, BuiltinRecognizers.RecognizeAll, TokenizerFlags.DefaultFlags, WordCountFlags.DefaultFlags);
        tm.Save();
        var units = new[]
        {
            Tu(SaveDoc(), "Click ", Open("1", 1), "Save", Close("1", 1), " to keep the changes."),   // same translation as SuperVault
            Tu(PumpDoc(), "The pump starts."),                                                     // a different translation
            Tu(WaterDoc(), Open("1", 1), "Never", Close("1", 1), " use water."),                   // same, formatted
        };
        var r = tm.LanguageDirection.AddTranslationUnitsMasked(units, new ImportSettings(), units.Select(_ => true).ToArray());
        Check(r.All(x => x.Action == Sdl.LanguagePlatform.TranslationMemory.Action.Add), "client TM filled with 3 formatted units");
        return tm.LanguageDirection;
    }

    static TranslationUnit Tu(Segment source, params object[] target)
    {
        var t = new Segment(EnUs);
        foreach (var p in target) if (p is string s) t.Add(s); else t.Add((Tag)p);
        return new TranslationUnit(source, t);
    }

    static SearchSettings Settings()
    {
        // As a project with Studio's default penalties: multiple translations 1, missing/different formatting 1.
        var s = new SearchSettings { Mode = SearchMode.NormalSearch, MinScore = 70, MaxResults = 10, IsDocumentSearch = true, ComputeTranslationProposal = true };
        s.AddPenalty(PenaltyType.MultipleTranslations, 1);
        s.AddPenalty(PenaltyType.TagMismatch, 1);
        s.AddPenalty(PenaltyType.MemoryTagsDeleted, 1);
        return s;
    }

    static SearchResults Cascade(Segment doc, params ITranslationProviderLanguageDirection[] dirs)
    {
        var cascade = new Cascade<CascadeEntry>(dirs.Select(d => new CascadeEntry(d, 0)), false, true, NlBe, EnUs);
        return cascade.SearchSegment(Settings(), doc);
    }

    static SearchResults[] CascadeBatch(Segment[] docs, params ITranslationProviderLanguageDirection[] dirs)
    {
        var cascade = new Cascade<CascadeEntry>(dirs.Select(d => new CascadeEntry(d, 0)), false, true, NlBe, EnUs);
        return cascade.SearchSegmentsMasked(Settings(), docs, docs.Select(_ => true).ToArray());
    }

    static bool IsVault(SearchResult r) => r.MemoryTranslationUnit?.OriginSystem == "SuperVault";
    static string Text(Segment s) => s == null ? "" : string.Concat(s.Elements.OfType<Text>().Select(t => t.Value));
    static string Show(SearchResults r) => r == null ? "(none)" : string.Join("; ", r.Results.Select(x => (IsVault(x) ? "SV " : "TM ") + x.ScoringResult.Match + "% " + Text(x.TranslationProposal?.TargetSegment)));

    // ---- checks ----

    static void PluginReferencesArePublic()
    {
        Console.WriteLine("Plugin references");
        var refs = typeof(SuperVaultProvider).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
        Check(!refs.Contains("Sdl.Core.LanguageProcessing") && !refs.Contains("Sdl.LanguagePlatform.Lingua"),
              "no reference to Studio's non-public tokenizer/scorer assemblies (" + refs.Count(r => r.StartsWith("Sdl.")) + " Sdl.* references)");
    }

    static void LanguageDirections(SuperVaultProvider vault)
    {
        Console.WriteLine("Language directions");
        foreach (var (s, t, want) in new[] { ("nl-BE", "en-US", true), ("en-GB", "nl-NL", true), ("nl-NL", "en-GB", true), ("en-US", "nl-BE", true), ("fr-FR", "de-DE", true), ("nl-NL", "nl-BE", false) })
            Check(vault.SupportsLanguageDirection(new LanguagePair(s, t)) == want, $"{s} > {t}: {(want ? "served (a pair not held yet is learned from its first confirmed segment)" : "not served")}");
    }

    /// <summary>The acceptance rule: with SuperVault in the cascade, in either position, the client TM's matches are exactly as without it.</summary>
    static void NeverLowersTheClientTm(SuperVaultProvider vault, ITranslationProviderLanguageDirection clientTm)
    {
        Console.WriteLine("Client TM matches are unchanged by SuperVault");
        var sv = vault.GetLanguageDirection(new LanguagePair(NlBe, EnUs));
        foreach (var (name, make) in new (string, Func<Segment>)[] { ("same translation, formatted", SaveDoc), ("different translation", PumpDoc), ("same translation, pt tags", WaterDoc) })
        {
            var alone = Cascade(make(), clientTm);
            var tmScores = alone.Results.Select(r => r.ScoringResult.Match).ToList();
            foreach (var first in new[] { false, true })
            {
                var both = first ? Cascade(make(), sv, clientTm) : Cascade(make(), clientTm, sv);
                var tmBoth = both.Results.Where(r => !IsVault(r)).Select(r => r.ScoringResult.Match).ToList();
                var svTop = both.Results.FirstOrDefault(IsVault);
                // SuperVault's match is either listed after the client TM at 100%, or - when it is the very same translation
                // with the same tags - merged away by Studio as a duplicate of the client TM's, which is kept.
                Check(tmBoth.SequenceEqual(tmScores) && both.Results[0] != null && !IsVault(both.Results[0]) && (svTop == null || svTop.ScoringResult.Match == 100),
                      $"{name}, SuperVault {(first ? "first" : "last")}: client TM {string.Join("/", tmBoth)}% (alone {string.Join("/", tmScores)}%), listed first; SuperVault {(svTop == null ? "merged into it as the same translation" : svTop.ScoringResult.Match + "%")}   [{Show(both)}]");
            }
        }
        // Batch path (Analyze, Pre-translate).
        var docs = new[] { SaveDoc(), PumpDoc(), WaterDoc(), StartDoc(), ValveDoc() };
        var aloneB = CascadeBatch(docs, clientTm);
        var bothB = CascadeBatch(docs, vault.GetLanguageDirection(new LanguagePair(NlBe, EnUs)), clientTm);
        bool same = true;
        for (int i = 0; i < docs.Length; i++)
        {
            var a = aloneB[i]?.Results.Select(r => r.ScoringResult.Match) ?? Enumerable.Empty<int>();
            var b = bothB[i]?.Results.Where(r => !IsVault(r)).Select(r => r.ScoringResult.Match) ?? Enumerable.Empty<int>();
            same &= a.SequenceEqual(b);
        }
        Check(same, "batch search (SearchSegmentsMasked, as Analyze): every client TM match unchanged");
        Check(bothB[3].Results.Any(IsVault) && bothB[4].Results.Any(IsVault), "batch search: SuperVault fills segments the client TM doesn't have");

        // Control: Studio's own penalty between two client TMs that disagree still happens with SuperVault present.
        var tm2 = new FileBasedTranslationMemory(Path.Combine(_scratch, "client2.sdltm"), "client2", NlBe, EnUs,
            FuzzyIndexes.SourceWordBased, BuiltinRecognizers.RecognizeAll, TokenizerFlags.DefaultFlags, WordCountFlags.DefaultFlags);
        tm2.Save();
        tm2.LanguageDirection.AddTranslationUnitsMasked(new[] { Tu(PumpDoc(), "The pump gets started.") }, new ImportSettings(), new[] { true });
        var two = Cascade(PumpDoc(), clientTm, tm2.LanguageDirection, sv);
        Check(two.Results.Where(r => !IsVault(r)).All(r => r.ScoringResult.Match == 99), "two disagreeing client TMs still get Studio's own penalty: " + Show(two));
    }

    /// <summary>
    /// Confirmed segments, handed over the way Studio's Editor and "Update Main TMs" hand them over
    /// (AddOrUpdateTranslationUnitsMasked), then looked up through Studio's own Cascade.
    /// </summary>
    static void Learning(SuperVaultProvider vault)
    {
        Console.WriteLine("Learning as you work");
        Check(vault.SupportsUpdate && !vault.IsReadOnly, "SupportsUpdate, not read-only: Studio offers the Update box");
        var sv = vault.GetLanguageDirection(new LanguagePair(NlBe, EnUs));
        Segment SwitchDoc() => Doc("Zet de ", Open("pt9", 1), "schakelaar", Close("pt9", 1), " op uit.");
        var tu = Tu(SwitchDoc(), "Set the ", Open("pt9", 1), "switch", Close("pt9", 1), " to of.");
        var masked = Tu(Doc("Deze zin is uitgemaskeerd."), "This sentence is masked out.");
        var sw = Stopwatch.StartNew();
        var r = sv.AddOrUpdateTranslationUnitsMasked(new[] { tu, masked }, new[] { 0, 0 }, new ImportSettings(), new[] { true, false });
        Check(sw.ElapsedMilliseconds < 100 && r.Length == 2 && r[0]?.Action == Sdl.LanguagePlatform.TranslationMemory.Action.Add && r[1] == null,
              $"confirm returns at once ({sw.ElapsedMilliseconds} ms): Add for the unit, null for the masked-out one");
        var found = WaitFor(() => Hits(SwitchDoc(), sv).FirstOrDefault(x => x.ScoringResult.Match == 100));
        Check(found != null, $"found through Studio's Cascade {sw.ElapsedMilliseconds / 1000.0:F1} s after confirming");
        var tags = found?.TranslationProposal.TargetSegment.Elements.OfType<Tag>().ToList();
        Check(tags != null && tags.Count == 2 && tags.All(t => t.TagID == "pt9"), "its formatting is kept: the document's own tags are placed in the target");

        // The translator corrects the translation and confirms again.
        sv.AddOrUpdateTranslationUnitsMasked(new[] { Tu(SwitchDoc(), "Set the ", Open("pt9", 1), "switch", Close("pt9", 1), " to off.") }, new[] { 0 }, new ImportSettings(), new[] { true });
        var fixedOne = WaitFor(() => Hits(SwitchDoc(), sv).FirstOrDefault(x => Text(x.TranslationProposal?.TargetSegment) == "Set the switch to off."));
        var all = Hits(SwitchDoc(), sv).Where(x => x.ScoringResult.Match >= 99).ToList();
        Check(fixedOne != null && all.Count == 1, "a corrected translation replaces the one learned before: " + Show(Cascade(SwitchDoc(), sv)));
        Check(Hits(Doc("Deze zin is uitgemaskeerd."), sv).Count == 0, "the masked-out unit was not stored");

        // Editing a unit from the Translation Results window.
        var u = sv.UpdateTranslationUnits(new[] { Tu(Doc("De lamp brandt."), "The lamp is on.") });
        Check(u.Length == 1 && u[0].Action == Sdl.LanguagePlatform.TranslationMemory.Action.Add && WaitFor(() => Hits(Doc("De lamp brandt."), sv).FirstOrDefault()) != null,
              "UpdateTranslationUnits (an edited TM unit) is stored too");

        // A SuperVault folder that can't be written: confirming still returns at once and throws nothing.
        var file = Path.Combine(_scratch, "a-file");
        File.WriteAllText(file, "x");
        var broken = new SuperVaultProvider(Path.Combine(file, "supervault")).GetLanguageDirection(new LanguagePair(NlBe, EnUs));
        sw.Restart();
        ImportResult[] br = null; Exception thrown = null;
        try { br = broken.AddOrUpdateTranslationUnitsMasked(new[] { tu }, new[] { 0 }, new ImportSettings(), new[] { true }); }
        catch (Exception ex) { thrown = ex; }
        Check(thrown == null && br?.Length == 1 && sw.ElapsedMilliseconds < 100, $"an unwritable SuperVault folder: confirm returns in {sw.ElapsedMilliseconds} ms, no exception reaches Studio");
        Check(broken.AddOrUpdateTranslationUnitsMasked(null, null, null, null).Length == 0, "no units: an empty answer, no exception");
    }

    static List<SearchResult> Hits(Segment doc, ITranslationProviderLanguageDirection dir) => Cascade(doc, dir)?.Results.ToList() ?? new List<SearchResult>();

    static T WaitFor<T>(Func<T> probe) where T : class
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < 20)
        {
            var v = probe();
            if (v != null) return v;
            Thread.Sleep(200);
        }
        return null;
    }

    static void TagsAndPenalties(SuperVaultProvider vault)
    {
        Console.WriteLine("Tags");
        var sv = vault.GetLanguageDirection(new LanguagePair(NlBe, EnUs));
        var r = Cascade(SaveDoc(), sv).Results.First();
        var tags = r.TranslationProposal.TargetSegment.Elements.OfType<Tag>().ToList();
        Check(r.ScoringResult.Match == 100 && tags.Count == 2 && tags.All(t => t.TagID == "pt12"),
              "formatted unit: 100%, target carries the document's own tags (" + string.Join(",", tags.Select(t => t.Type + ":" + t.TagID)) + ")");
        Check(Text(r.TranslationProposal.TargetSegment) == "Click Save to keep the changes.", "target text: " + Text(r.TranslationProposal.TargetSegment));

        r = Cascade(StartDoc(), sv).Results.First();
        Check(r.ScoringResult.Match == 99 && r.ScoringResult.BaseScore == 100 && r.ScoringResult.AppliedPenalties.Any(p => p.Type == PenaltyType.TagMismatch),
              $"unit stored without formatting, document formatted: {r.ScoringResult.Match}% (the project's formatting penalty), text only: {Text(r.TranslationProposal.TargetSegment)}");

        r = Cascade(ValveDoc(), sv).Results.First();
        Check(r.ScoringResult.Match == 100 && Text(r.TranslationProposal.TargetSegment) == "The valve is closed automatically.", "unit stored en-GB>nl-NL serves nl-BE>en-US at 100%");

        var penalised = new SearchSettings { Mode = SearchMode.NormalSearch, MinScore = 70, MaxResults = 5 };
        penalised.AddPenalty(PenaltyType.ProviderPenalty, 3);
        r = sv.SearchSegment(penalised, PumpDoc()).Results.First();
        Check(r.ScoringResult.Match == 97, "the provider penalty the project sets for SuperVault is applied: " + r.ScoringResult.Match + "%");
    }

    static void FuzzyDifferences(SuperVaultProvider vault)
    {
        Console.WriteLine("Fuzzy matches and difference highlighting");
        var sv = vault.GetLanguageDirection(new LanguagePair(NlBe, EnUs));
        var res = Cascade(Doc("Het apparaat moet worden schoongemaakt na ieder gebruik."), sv);
        var r = res.Results.First();
        var ed = r.ScoringResult.EditDistance;
        var docTokens = res.SourceSegment.Tokens;
        var memTokens = r.MemoryTranslationUnit.SourceSegment.Tokens;
        Check(r.ScoringResult.Match >= 80 && r.ScoringResult.Match < 100, "fuzzy: " + r.ScoringResult.Match + "%");
        Check(ed != null && docTokens != null && memTokens != null && ed.SourceObjectCount == docTokens.Count && ed.TargetObjectCount == memTokens.Count,
              "EditDistance sized to the document and memory tokens");
        Check(ed != null && ed.Items.Any(i => i.Operation == Sdl.LanguagePlatform.Core.EditDistance.EditOperation.Change), "the changed word is a Change");
        Check(SpansValid(res.SourceSegment) && SpansValid(r.MemoryTranslationUnit.SourceSegment) && SpansValid(r.MemoryTranslationUnit.TargetSegment), "every token span points into its segment");
    }

    static bool SpansValid(Segment s)
    {
        if (s.Tokens == null) return false;
        foreach (var t in s.Tokens)
        {
            var span = t.Span;
            if (span.From.Index >= s.Elements.Count) return false;
            if (s.Elements[span.From.Index] is Text text && (span.Into.Position >= Math.Max(1, text.Value.Length) || span.From.Position > span.Into.Position)) return false;
        }
        return true;
    }

    static void Concordance(SuperVaultProvider vault, ITranslationProviderLanguageDirection clientTm)
    {
        Console.WriteLine("Concordance");
        var sv = vault.GetLanguageDirection(new LanguagePair(NlBe, EnUs));
        var src = sv.SearchSegment(new SearchSettings { Mode = SearchMode.ConcordanceSearch, MinScore = 50, MaxResults = 10 }, Doc("pomp"));
        Check(src.Results.Any(x => Text(x.MemoryTranslationUnit.TargetSegment) == "The pump is started."), "source concordance 'pomp': " + src.Count + " result(s)");
        var hit = src.Results.FirstOrDefault(x => Text(x.MemoryTranslationUnit.TargetSegment) == "The pump is started.");
        var ranges = hit?.ScoringResult.MatchingConcordanceRanges;
        string marked = ranges == null ? "(none)" : string.Join(",", ranges.Select(r => Text(hit.MemoryTranslationUnit.SourceSegment).Substring(r.From.Position, r.Into.Position - r.From.Position + 1)));
        Check(marked == "pomp", "source concordance highlights the searched word: " + marked);
        // Studio's own TM, for the convention (which side, element index, Into inclusive).
        foreach (var mode in new[] { SearchMode.ConcordanceSearch, SearchMode.TargetConcordanceSearch })
        {
            var q = mode == SearchMode.ConcordanceSearch ? Doc("pomp") : new Func<Segment>(() => { var s = new Segment(EnUs); s.Add("pump"); return s; })();
            foreach (var r in clientTm.SearchSegment(new SearchSettings { Mode = mode, MinScore = 50, MaxResults = 3 }, q).Results)
                Console.WriteLine($"    Studio TM {mode}: src '{Text(r.MemoryTranslationUnit.SourceSegment)}' trg '{Text(r.MemoryTranslationUnit.TargetSegment)}' ranges " +
                    string.Join(" ", (r.ScoringResult.MatchingConcordanceRanges ?? new List<SegmentRange>()).Select(x => $"[{x.From.Index}:{x.From.Position}-{x.Into.Index}:{x.Into.Position}]")));
        }
        var trg = sv.SearchSegment(new SearchSettings { Mode = SearchMode.TargetConcordanceSearch, MinScore = 50, MaxResults = 10 }, new Func<Segment>(() => { var s = new Segment(EnUs); s.Add("valve"); return s; })());
        var first = trg.Results.FirstOrDefault();
        Check(first != null && Text(first.MemoryTranslationUnit.SourceSegment).StartsWith("De klep") && first.MemoryTranslationUnit.SourceSegment.Culture.Name == "nl-BE",
              "target concordance 'valve': the unit comes back source nl-BE > target en-US");
        var tr = first?.ScoringResult.MatchingConcordanceRanges;
        string tmarked = tr == null ? "(none)" : string.Join(",", tr.Select(r => Text(first.MemoryTranslationUnit.TargetSegment).Substring(r.From.Position, r.Into.Position - r.From.Position + 1)));
        Check(tmarked == "valve", "target concordance highlights the searched word in the target: " + tmarked);
    }

    static void FailureModes(ITranslationProviderLanguageDirection clientTm)
    {
        Console.WriteLine("Failure modes");
        var pair = new LanguagePair(NlBe, EnUs);

        // An empty SuperVault is a normal state: available (Studio's Editor disables a TM that says it isn't, and saves
        // that in the project), no matches, the client TM unaffected, and the first confirmation fills it.
        SearchResults r = null;
        var emptyIndex = Path.Combine(_scratch, "empty-index");
        VaultLibrary.For(emptyIndex).Import((store, p, c) => { using (var w = new VaultStore.Writer(store)) { w.Commit(); return w.Result; } }, null, CancellationToken.None);
        var emptyStore = Path.Combine(_scratch, "empty-store");
        Directory.CreateDirectory(emptyStore);
        VaultStore.Create(Path.Combine(emptyStore, "supervault.db"));
        foreach (var (what, root) in new[] { ("no folder", Path.Combine(_scratch, "no-folder", "supervault")), ("empty store, no index", emptyStore), ("index with 0 units", emptyIndex) })
        {
            var empty = new SuperVaultProvider(root);
            Check(empty.StatusInfo.Available, what + ": available, with a message: " + empty.StatusInfo.StatusMessage);
            var frDe = new LanguagePair(new CultureCode("fr-FR"), new CultureCode("de-DE"));
            Check(empty.SupportsLanguageDirection(pair) && empty.SupportsLanguageDirection(frDe), what + ": offered for nl-BE>en-US and fr-FR>de-DE");
            var dir = empty.GetLanguageDirection(pair);
            r = Cascade(SaveDoc(), clientTm, dir);
            Check(r.Results.Count == 1 && r.Results[0].ScoringResult.Match == 100, what + ": no matches of its own, the client TM works as before");
            Check(Hits(Doc("Een zin die er niet in staat."), dir).Count == 0, what + ": a lookup gives nothing and throws nothing");
            var conc = dir.SearchSegment(new SearchSettings { Mode = SearchMode.ConcordanceSearch, MinScore = 50, MaxResults = 5 }, Doc("pomp"));
            Check(conc.Results.Count == 0, what + ": concordance gives nothing and throws nothing");
            var learned = Doc("Een eerste zin in een lege SuperVault.");
            dir.AddOrUpdateTranslationUnitsMasked(new[] { Tu(learned, "A first sentence in an empty SuperVault.") }, new[] { 0 }, new ImportSettings(), new[] { true });
            Check(WaitFor(() => Hits(learned, dir).FirstOrDefault(x => x.ScoringResult.Match == 100)) != null && empty.StatusInfo.Available,
                  what + ": the first confirmed segment is stored, indexed and found");
        }

        // A damaged index: every file of the generation overwritten.
        var damagedRoot = Path.Combine(_scratch, "vault-damaged");
        VaultLibrary.For(Path.Combine(_scratch, "vault-to-damage")).Import((store, p, c) => VaultTmx.Import(store, _vaultTmx, p, c), null, CancellationToken.None);
        var gen = Directory.GetDirectories(Path.Combine(_scratch, "vault-to-damage"), "gen-*").Single();
        Directory.CreateDirectory(Path.Combine(damagedRoot, Path.GetFileName(gen)));
        foreach (var f in Directory.GetFiles(gen))
            File.WriteAllBytes(Path.Combine(damagedRoot, Path.GetFileName(gen), Path.GetFileName(f)), f.EndsWith("info.txt") ? File.ReadAllBytes(f) : new byte[] { 1, 2, 3 });
        File.WriteAllText(Path.Combine(damagedRoot, "current.txt"), Path.GetFileName(gen));
        var damaged = new SuperVaultProvider(damagedRoot);
        Check(!damaged.StatusInfo.Available && damaged.StatusInfo.StatusMessage.Contains("could not be opened"), "damaged index: unavailable with a message: " + damaged.StatusInfo.StatusMessage);
        r = Cascade(SaveDoc(), clientTm, damaged.GetLanguageDirection(pair));
        Check(r.Results.Count == 1 && r.Results[0].ScoringResult.Match == 100, "damaged index: no exception, the client TM works as before");

        // A file that isn't a Studio TM (or one Studio can't open, e.g. with a password): a clear error, nothing added.
        var vaultLib = VaultLibrary.For(Path.Combine(_scratch, "vault"));
        long before = vaultLib.StoreCount();
        var notTm = Path.Combine(_scratch, "not a tm.sdltm");
        File.WriteAllText(notTm, "this is not a translation memory");
        string message = null;
        try { vaultLib.Import((store, p, c) => SuperVaultImport.Sdltm(store, notTm, p, c), null, CancellationToken.None); }
        catch (InvalidOperationException ex) { message = ex.Message; }
        Check(message != null && message.Contains("TMX") && vaultLib.StoreCount() == before, "unreadable .sdltm: refused with a message, nothing added: " + message);

        // Importing a Studio TM leaves the file free: the user can move or delete it afterwards.
        var tmFile = Path.Combine(_scratch, "for-supervault.sdltm");
        bool free;
        try { File.Move(tmFile, tmFile + ".moved"); File.Move(tmFile + ".moved", tmFile); free = true; } catch (IOException) { free = false; }
        Check(free, "the imported .sdltm is not left locked");

        // Index being rebuilt: lookups keep working on the old one throughout, and the new one takes over.
        var lib = VaultLibrary.For(Path.Combine(_scratch, "vault"));
        var vault = new SuperVaultProvider(lib.Root);
        var big = WriteTmx("big.tmx", Enumerable.Range(0, 30000).Select(i => ($"Zin {i} over de klep.", $"Sentence {i} about the valve.", "nl", "en")));
        var done = new ManualResetEventSlim();
        string error = "?";
        lib.StartImport((store, p, c) => VaultTmx.Import(store, big, p, c), e => { error = e; done.Set(); });
        int lookups = 0; bool allGood = true;
        var sv = vault.GetLanguageDirection(pair);
        while (!done.IsSet) { allGood &= Cascade(PumpDoc(), sv).Results.Any(x => x.ScoringResult.Match == 100); lookups++; }
        Check(error == null && allGood, $"30,000-unit TMX imported in the background: {lookups} lookups during it, all answered from the old index");
        Thread.Sleep(2100);
        Check(Cascade(Doc("Zin 29999 over de klep."), sv).Results.Any(x => x.ScoringResult.Match == 100), "after the import the new units are found");
    }

    static void Scale(string root, string db)
    {
        Console.WriteLine("Scale: " + root);
        var vault = new SuperVaultProvider(root);
        Check(vault.StatusInfo.Available, "index: " + vault.StatusInfo.StatusMessage);
        var samples = new List<string>();
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            c.Open();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT source_text FROM translation_units WHERE source_lang LIKE 'nl%' AND target_lang LIKE 'en%' AND length(source_text) BETWEEN 40 AND 300 AND id % 7919 = 3 LIMIT 200";
                using (var r = cmd.ExecuteReader()) while (r.Read()) samples.Add(VaultMarkup.Plain(r.GetString(0)));
            }
        }
        var rnd = new Random(7);
        var docs = samples.Select(s => { var w = s.Split(' '); w[rnd.Next(w.Length)] = "xyzzy"; return Doc(string.Join(" ", w)); }).ToArray();
        var sv = vault.GetLanguageDirection(new LanguagePair(NlBe, EnUs));
        sv.SearchSegment(Settings(), Doc("opwarmen"));
        var times = new List<double>();
        int found = 0;
        foreach (var d in docs)
        {
            var sw = Stopwatch.StartNew();
            var r = sv.SearchSegment(Settings(), d);
            times.Add(sw.Elapsed.TotalMilliseconds);
            if (r.Count > 0) found++;
        }
        times.Sort();
        Console.WriteLine($"  fuzzy lookups through the provider (one word changed): {found}/{docs.Length} with a match; median {times[times.Count / 2]:F1} ms, p90 {times[(int)(times.Count * 0.9)]:F1} ms, max {times.Last():F0} ms");
        var swb = Stopwatch.StartNew();
        sv.SearchSegmentsMasked(Settings(), docs, docs.Select(_ => true).ToArray());
        Console.WriteLine($"  batch of {docs.Length} (as Analyze): {swb.Elapsed.TotalMilliseconds / docs.Length:F1} ms per segment");
        Check(found >= docs.Length * 0.9, "at least 90% of the one-word-changed sentences find their match");
    }
}
