using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sdl.Core.Globalization;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.Core.EditDistance;
using Sdl.LanguagePlatform.Core.Tokenization;
using Sdl.LanguagePlatform.TranslationMemory;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using Supervertaler.Core.SuperVault;
using Supervertaler.Trados.SuperVault;

/// <summary>
/// The differences EverTM hands Studio to draw (ScoringResult.EditDistance), checked against Studio's own file TM for
/// the same pairs, both looked up through Studio's cascade.
///
/// Studio draws a match row from the edit list turned round (stored source -> document): each document word only the
/// document has goes BEFORE the stored token whose position it carries; a stored word only the stored unit has is struck;
/// a changed word is drawn unless it is marked as a resolved substitution (TranslationMemoryViewPartControl,
/// ShowResolvedSubstitutionOperations = false). <see cref="Render"/> rebuilds the row that way. Read for the document,
/// it must give the document's tokens in order; read for the stored unit, the stored tokens in order. Studio's own edit
/// lists pass that, which shows the rebuild follows Studio's convention.
///
/// Fixtures: Michael's two claim pairs (11 Nov; the claim text is fine to use, no client or project names), and Acme
/// cases for the edges: an insertion at the very start and end, a deletion next to a tag, a change next to punctuation,
/// and a segment too long to align.
/// </summary>
static class DiffDisplayChecks
{
    static readonly CultureCode EnUs = new CultureCode("en-US"), NlNl = new CultureCode("nl-NL");
    static Action<bool, string> Check;
    static string _dir;
    static int _n;

    internal const string ClaimDoc1 = "in response to the amount of memory bandwidth being consumed exceeding a second higher threshold, disabling, by the one or more processors, the hardware prefetcher (116) after a preconfigured time passes, and causing, by the one or more processors, the hardware prefetcher (116) to remain disabled until the memory bandwidth being consumed falls below the first lower threshold and stays below the first lower threshold for the preconfigured time;";
    internal const string ClaimMem1 = "in response to the amount of memory bandwidth being consumed exceeding the second higher threshold, disable the hardware prefetcher (116) after a preconfigured time passes, wherein the hardware prefetcher (116) is configured to remain disabled until the memory bandwidth being consumed falls below the first lower threshold and stays below the first lower threshold for the preconfigured time;";
    // The rows as Michael saw them in Studio on 11 Nov with the display fix, and chose to keep ("clearer highlighting"):
    // the score is the Studio-like one, the differences drawn by the display alignment (VaultDisplay).
    internal const string ClaimRow1 = "in response to the amount of memory bandwidth being consumed exceeding [the>a] second higher threshold, [disable>disabling][+,][+ ][+by][+ ][+the][+ ][+one][+ ][+or][+ ][+more][+ ][+processors][+,] the hardware prefetcher (116) after a preconfigured time passes[+,][+ ][+and][+ ][+causing],[+ ][+by][+ ][+the][+ ][+one][+ ][+or][+ ][+more] [wherein>processors][+,] the hardware prefetcher (116)[- ][-is][- ][-configured] to remain disabled until the memory bandwidth being consumed falls below the first lower threshold and stays below the first lower threshold for the preconfigured time;";
    internal const string ClaimRow2 = "[+The][+ ][+method][+ ][+of][+ ][+claim][+ ][+8][+ ][+or][+ ][+9][+,][+ ][+further][+ ][+comprising][+ ][determine>determining][+,][+ ][+by][+ ][+the][+ ][+one][+ ][+or][+ ][+more][+ ][+processors][+,] the first lower threshold and the second higher threshold based on ablation studies performed on a plurality of server machines, wherein the ablation studies use a first group of server machines running with a plurality of modulating hardware prefetchers and a second group of server machines running with a plurality of hardware prefetchers (116) that are always active.";
    internal const string ClaimDoc2 = "The method of claim 8 or 9, further comprising determining, by the one or more processors, the first lower threshold and the second higher threshold based on ablation studies performed on a plurality of server machines, wherein the ablation studies use a first group of server machines running with a plurality of modulating hardware prefetchers and a second group of server machines running with a plurality of hardware prefetchers (116) that are always active.";
    internal const string ClaimMem2 = "determine the first lower threshold and the second higher threshold based on ablation studies performed on a plurality of server machines, wherein the ablation studies use a first group of server machines running with a plurality of modulating hardware prefetchers and a second group of server machines running with a plurality of hardware prefetchers (116) that are always active.";

    public static void Run(string scratch, Action<bool, string> check)
    {
        Check = check;
        _dir = Path.Combine(scratch, "diffdisplay");
        Directory.CreateDirectory(_dir);
        Console.WriteLine("Difference display: what EverTM hands Studio, against Studio's own TM");

        Case("claim pair 1 (81% in Studio)", Seg(ClaimDoc1), Seg(ClaimMem1), show: true, score: 81, row: ClaimRow1);
        Case("claim pair 2 (87% in Studio)", Seg(ClaimDoc2), Seg(ClaimMem2), show: true, score: 87, row: ClaimRow2);
        Case("an insertion at the very start", Seg("Moreover, the pump starts when the valve opens."), Seg("the pump starts when the valve opens."));
        Case("an insertion at the very end", Seg("the pump starts when the valve opens fully"), Seg("the pump starts when the valve opens"));
        Case("a word missing at the very start", Seg("pump starts when the valve opens."), Seg("The pump starts when the valve opens."));
        Case("a word missing at the very end", Seg("the pump starts when the valve"), Seg("the pump starts when the valve opens"));
        Case("a change next to punctuation", Seg("Close the inlet valve, then start the motor."), Seg("Close the outlet valve, then start the motor."));
        Case("a deletion next to a tag", Seg("Press ", Open(1), "Start", Close(1), " now to begin."), Seg("Press ", Open(1), "Start", Close(1), " to begin."));
        Case("an insertion next to a tag", Seg("Press the ", Open(1), "Start", Close(1), " button."), Seg("Press ", Open(1), "Start", Close(1), " button."));
        TooLong();
    }

    // ---- one pair: Studio's TM and EverTM, through the cascade ----

    static void Case(string what, Segment doc, Segment mem, bool show = false, int score = -1, bool sameAsStudio = false, string[] marks = null, string row = null)
    {
        int n = ++_n;
        var tm = new FileBasedTranslationMemory(Path.Combine(_dir, "case" + n + ".sdltm"), "case" + n, EnUs, NlNl, FuzzyIndexes.SourceWordBased | FuzzyIndexes.SourceCharacterBased, BuiltinRecognizers.RecognizeAll, TokenizerFlags.DefaultFlags, WordCountFlags.DefaultFlags);
        tm.Save();
        tm.LanguageDirection.AddTranslationUnitsMasked(new[] { new TranslationUnit(mem, Target(mem)) }, new ImportSettings(), new[] { true });
        var root = Path.Combine(_dir, "evertm" + n);
        var tmx = Path.Combine(_dir, "case" + n + ".tmx");
        File.WriteAllText(tmx, "<?xml version=\"1.0\" encoding=\"utf-8\"?><tmx version=\"1.4\"><header srclang=\"en-US\" segtype=\"sentence\" datatype=\"plaintext\" o-tmf=\"t\" adminlang=\"en\" creationtool=\"t\" creationtoolversion=\"1\"/><body>" +
            "<tu><tuv xml:lang=\"en-US\"><seg>" + TmxSeg(mem) + "</seg></tuv><tuv xml:lang=\"nl-NL\"><seg>" + TmxSeg(Target(mem)) + "</seg></tuv></tu></body></tmx>");
        VaultLibrary.For(root).Import((store, p, c) => VaultTmx.Import(store, tmx, p, c), null, System.Threading.CancellationToken.None);

        var studio = Through(tm.LanguageDirection, doc, null);
        string sRow = null, sWhy = null; bool sDocOk = false, sMemOk = false;
        if (studio != null) sRow = Render(studio, out sDocOk, out sMemOk, out sWhy);
        var ever = Through(new SuperVaultProvider(root).GetLanguageDirection(new LanguagePair(EnUs, NlNl)), doc, what);
        if (studio == null || ever == null) { Check(false, what + ": both find the unit (Studio " + (studio != null) + ", EverTM " + (ever != null) + ")"); return; }
        var eRow = Render(ever, out bool eDocOk, out bool eMemOk, out string eWhy);
        if (show)
        {
            Console.WriteLine("        Studio TM " + studio.ScoringResult.BaseScore + "%: " + sRow);
            Console.WriteLine("        EverTM   " + ever.ScoringResult.BaseScore + "%: " + eRow);
        }
        Check(sDocOk && sMemOk, what + ": Studio's own row rebuilt with Studio's convention reads back both texts" + (sWhy == null ? "" : " (" + sWhy + ")"));
        Check(eDocOk && eMemOk, what + ": EverTM's row reads back the document and the stored text, every word in its place" + (eWhy == null ? "" : " (" + eWhy + ")"));
        var ed = ever.ScoringResult.EditDistance;
        Check(ed.Items.Where(i => i.Operation == EditOperation.Change).All(i => i.Resolution == EditDistanceResolution.None),
              what + ": EverTM's changed words are drawn (not marked as resolved substitutions)");
        var mixed = ed.Items.Where(i => i.Operation == EditOperation.Change && _lastDoc != null && i.Source < _lastDoc.Count
                                        && IsPunct(_lastDoc[i.Source].Text) != IsPunct(ever.MemoryTranslationUnit.SourceSegment.Tokens[i.Target].Text))
                            .Select(i => ever.MemoryTranslationUnit.SourceSegment.Tokens[i.Target].Text + ">" + _lastDoc[i.Source].Text).ToList();
        Check(mixed.Count == 0, what + ": no word is shown changed into a punctuation mark or back" + (mixed.Count == 0 ? "" : ": " + string.Join(", ", mixed)));
        if (score >= 0)
            Check(ever.ScoringResult.BaseScore == score && studio.ScoringResult.BaseScore == score,
                  what + $": EverTM scores it as Studio's own TM does ({score}%): EverTM {ever.ScoringResult.BaseScore}%, Studio {studio.ScoringResult.BaseScore}%");
        if (sameAsStudio) Check(eRow == sRow, what + ": EverTM's row reads exactly as Studio's own TM draws it");
        if (row != null) Check(eRow == row, what + ": EverTM's row reads as Michael saw it on 11 Nov (the clearer highlighting he chose), with the Studio-like score");
        foreach (var mk in marks ?? new string[0]) Check(eRow.Contains(mk), what + ": EverTM's row shows " + mk + " as Studio's does");
        var unmarked = UnmarkedDifferences(ever);
        Check(unmarked.Count == 0, what + ": no stored word that differs from the document is shown as kept" + (unmarked.Count == 0 ? "" : ": " + string.Join(", ", unmarked)));
    }

    /// <summary>
    /// The provider's best result through Studio's cascade, with the provider's own tokenised document (Studio pairs a
    /// result's edit list with it: SearchResults.SourceSegment). Also checks the cascade hands the edit list on unchanged.
    /// </summary>
    static SearchResult Through(ITranslationProviderLanguageDirection dir, Segment doc, string what)
    {
        var settings = new SearchSettings { Mode = SearchMode.NormalSearch, MinScore = 30, MaxResults = 5 };
        var direct = dir.SearchSegment(settings, doc.Duplicate());
        _lastDoc = direct.SourceSegment?.Tokens;
        var cascade = new Cascade<CascadeEntry>(new[] { new CascadeEntry(dir, 0) }, false, true, EnUs, NlNl);
        var r = cascade.SearchSegment(settings, doc.Duplicate()).Results.FirstOrDefault();
        var d = direct.Results.FirstOrDefault();
        if (r != null && d != null && what != null)
            Check(Describe(r.ScoringResult.EditDistance) == Describe(d.ScoringResult.EditDistance), what + ": the cascade passes the differences on as the provider made them");
        return r;
    }

    static string Describe(EditDistance ed) => ed == null ? "(none)" :
        string.Join(" ", ed.Items.Select(i => i.Operation.ToString()[0] + "" + i.Source + ">" + i.Target + (i.Resolution != EditDistanceResolution.None ? "(" + i.Resolution + ")" : "")));

    /// <summary>
    /// The match row as Studio draws it, as text: the stored source, with [+word] for a document word inserted before
    /// the stored token whose position it carries, [-word] for a struck stored word, [old>new] for a changed word.
    /// Also checks that reading it for the document gives the document's tokens 0..n-1 in order, and for the stored unit
    /// 0..m-1 in order.
    /// </summary>
    internal static string Render(SearchResult r, out bool docOk, out bool memOk, out string why)
    {
        var ed = r.ScoringResult.EditDistance;
        var memToks = r.MemoryTranslationUnit.SourceSegment.Tokens;
        int m = memToks?.Count ?? 0;
        var before = new Dictionary<int, List<int>>();   // stored position -> document tokens drawn before it
        var atMem = new Dictionary<int, (EditOperation op, int doc)>();
        foreach (var it in ed.Items)
        {
            switch (it.Operation)
            {
                case EditOperation.Delete:   // a document token the stored unit lacks: drawn before stored token it.Target
                    if (!before.TryGetValue(it.Target, out var l)) before[it.Target] = l = new List<int>();
                    l.Add(it.Source); break;
                case EditOperation.Insert: atMem[it.Target] = (EditOperation.Insert, -1); break;
                case EditOperation.Move: atMem[it.Target] = (EditOperation.Move, it.Source); break;
                default: atMem[it.Target] = (it.Operation, it.Source); break;
            }
        }
        var docRead = new List<int>(); var memRead = new List<int>();
        var sb = new System.Text.StringBuilder();
        var docToks = _lastDoc;
        string D(int i) => docToks != null && i >= 0 && i < docToks.Count ? docToks[i].Text : "?" + i;
        for (int k = 0; k <= m; k++)
        {
            if (before.TryGetValue(k, out var ins))
                foreach (var d in ins) { docRead.Add(d); sb.Append("[+" + D(d) + "]"); }
            if (k == m) break;
            if (!atMem.TryGetValue(k, out var at)) { sb.Append("{?" + memToks[k].Text + "}"); memRead.Add(k); continue; }
            memRead.Add(k);
            switch (at.op)
            {
                case EditOperation.Identity: docRead.Add(at.doc); sb.Append(memToks[k].Text); break;
                case EditOperation.Change: docRead.Add(at.doc); sb.Append("[" + memToks[k].Text + ">" + D(at.doc) + "]"); break;
                case EditOperation.Move: sb.Append("[~" + memToks[k].Text + "]"); break;
                default: sb.Append("[-" + memToks[k].Text + "]"); break;
            }
        }
        // A move is the same word in both: the document copy is read where the document has it.
        var moved = ed.Items.Where(i => i.Operation == EditOperation.Move).Select(i => i.Source).ToList();
        var docOrder = docRead.Concat(moved).ToList();
        int n = docToks?.Count ?? 0;
        docOk = moved.Count == 0 ? docRead.SequenceEqual(Enumerable.Range(0, n)) : docOrder.OrderBy(x => x).SequenceEqual(Enumerable.Range(0, n));
        memOk = memRead.SequenceEqual(Enumerable.Range(0, m));
        why = !docOk ? "document read as " + string.Join(",", docRead.Take(40)) : !memOk ? "stored read as " + string.Join(",", memRead.Take(40)) : null;
        return sb.ToString();
    }

    static List<Token> _lastDoc;

    static bool IsPunct(string t) => t.Length > 0 && t.All(c => char.IsPunctuation(c) || char.IsSymbol(c));

    /// <summary>Stored tokens drawn as kept (Identity) although their text differs from the document token they're paired with.</summary>
    static List<string> UnmarkedDifferences(SearchResult r)
    {
        var list = new List<string>();
        var mem = r.MemoryTranslationUnit.SourceSegment.Tokens;
        foreach (var it in r.ScoringResult.EditDistance.Items.Where(i => i.Operation == EditOperation.Identity))
        {
            var a = _lastDoc != null && it.Source < _lastDoc.Count ? _lastDoc[it.Source].Text : null;
            var b = mem != null && it.Target < mem.Count ? mem[it.Target].Text : null;
            if (a != null && b != null && a != b) list.Add(b + "/" + a);
        }
        return list;
    }

    /// <summary>A segment longer than EverTM aligns (VaultDiff.MaxAlignTokens): no differences handed over, no crash, a score.</summary>
    static void TooLong()
    {
        var words = string.Join(" ", Enumerable.Range(0, 230).Select(i => "word" + (i % 37)));
        var doc = Seg(words + " extra end."); var mem = Seg(words + " end.");
        int n = ++_n;
        var root = Path.Combine(_dir, "evertm" + n);
        var tmx = Path.Combine(_dir, "case" + n + ".tmx");
        File.WriteAllText(tmx, "<?xml version=\"1.0\" encoding=\"utf-8\"?><tmx version=\"1.4\"><header srclang=\"en-US\" segtype=\"sentence\" datatype=\"plaintext\" o-tmf=\"t\" adminlang=\"en\" creationtool=\"t\" creationtoolversion=\"1\"/><body>" +
            "<tu><tuv xml:lang=\"en-US\"><seg>" + TmxSeg(mem) + "</seg></tuv><tuv xml:lang=\"nl-NL\"><seg>NL</seg></tuv></tu></body></tmx>");
        VaultLibrary.For(root).Import((store, p, c) => VaultTmx.Import(store, tmx, p, c), null, System.Threading.CancellationToken.None);
        Exception error = null; SearchResult r = null;
        try { r = Through(new SuperVaultProvider(root).GetLanguageDirection(new LanguagePair(EnUs, NlNl)), doc, null); }
        catch (Exception ex) { error = ex; }
        Check(error == null && r != null && r.ScoringResult.EditDistance == null && r.ScoringResult.BaseScore > 0 && r.ScoringResult.BaseScore < 100,
              $"a segment over {VaultDiff.MaxAlignTokens} tokens: found at {r?.ScoringResult.BaseScore}%, no differences handed to Studio (none drawn, none wrong), no error" + (error == null ? "" : ": " + error.Message));
    }

    // ---- segments ----

    static Tag Open(int id) => new Tag(TagType.Start, "pt" + id, id, id, null);
    static Tag Close(int id) => new Tag(TagType.End, "pt" + id, id, 0, null);

    static Segment Seg(params object[] parts)
    {
        var s = new Segment(EnUs);
        foreach (var p in parts) if (p is string t) s.Add(t); else s.Add((Tag)p);
        return s;
    }

    static Segment Target(Segment src)
    {
        var t = new Segment(NlNl);
        foreach (var e in src.Elements) if (e is Text x) t.Add("NL " + x.Value); else t.Add((Tag)((Tag)e).Duplicate());
        return t;
    }

    static string TmxSeg(Segment s) => string.Concat(s.Elements.Select(e => e is Text x ? System.Security.SecurityElement.Escape(x.Value)
        : ((Tag)e).Type == TagType.Start ? "<bpt i=\"" + ((Tag)e).Anchor + "\">&lt;b&gt;</bpt>" : ((Tag)e).Type == TagType.End ? "<ept i=\"" + ((Tag)e).Anchor + "\">&lt;/b&gt;</ept>" : "<ph/>"));
}
