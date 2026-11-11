using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using Sdl.Core.Globalization;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.Core.Tokenization;
using Sdl.LanguagePlatform.TranslationMemory;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using Supervertaler.Core.SuperVault;

/// <summary>
/// SuperVaultCheck.exe score-compare [copy of evertm.db] [natural queries]: Studio's fuzzy score against EverTM's
/// (VaultDiff) for the same document segment and the same stored source. Pairs:
///  - synthetic (Acme): base sentences of 5-60 words with one kind of change each, so the cause of a difference is known;
///  - natural, from a COPY of EverTM's store: real English sentences looked up in a Studio file TM made of other real
///    sentences; every result Studio finds at 50% or more is a pair.
/// Studio's score is the BaseScore its own file TM gives (no penalties set). Prints distributions only, never text.
/// </summary>
static class ScoreCompare
{
    static readonly CultureCode EnUs = new CultureCode("en-US"), NlNl = new CultureCode("nl-NL");

    sealed class Pair { public string Kind; public string Doc, Mem; public int Studio, Ever, Words, Proto; }

    static ScoreCompare()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("SVSCORE_MARGIN") ?? "", out var margin)) StudioLikeScore.Margin = margin;
        if (Environment.GetEnvironmentVariable("SVSCORE_NOMOVES") == "1") StudioLikeScore.DetectMoves = false;
        if (Environment.GetEnvironmentVariable("SVSCORE_NOSTOP") == "1") StudioLikeScore.UseStopwords = false;
        if (double.TryParse(Environment.GetEnvironmentVariable("SVSCORE_NUMBER") ?? "", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var nc)) StudioLikeScore.NumberChange = nc;
        // The prototype's stemmer: Lucene's Porter stemmer (Apache-2.0), standing in for Studio's own English stemmer.
        StudioLikeScore.Stem = w => { var st = new Lucene.Net.Tartarus.Snowball.Ext.EnglishStemmer(); st.SetCurrent(w); st.Stem(); return st.Current; };
    }

    public static int Run(string[] args)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sv-score-compare-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        try
        {
            var pairs = new List<Pair>();
            pairs.AddRange(Synthetic(dir));
            if (args.Length > 1) pairs.AddRange(Natural(dir, args[1], args.Length > 2 ? int.Parse(args[2]) : 3000));
            Report(pairs);
            var dump = Path.Combine(Path.GetTempPath(), "sv-score-compare-pairs.tsv");
            if (Environment.GetEnvironmentVariable("SVSCORE_DUMP") is string d && d.Length > 0)
            {
                File.WriteAllLines(d, pairs.Select(p => p.Kind + "\t" + p.Studio + "\t" + p.Ever + "\t" + p.Words + "\t" + p.Doc + "\t" + p.Mem));
                Console.WriteLine("pairs written to " + d);
            }
            return 0;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ---- synthetic pairs ----

    static readonly string[] Nouns = { "pump", "valve", "housing", "sensor", "controller", "processor", "memory", "filter", "motor", "shaft", "bearing", "seal", "outlet", "inlet", "chamber", "piston", "spring", "lever", "circuit", "module", "interface", "device", "member", "element", "portion", "layer", "surface", "frame", "panel", "cable" };
    static readonly string[] Verbs = { "configured", "arranged", "adapted", "coupled", "connected", "mounted", "disposed", "positioned", "attached", "secured" };
    static readonly string[] Adjs = { "first", "second", "upper", "lower", "inner", "outer", "rotatable", "flexible", "rigid", "elongated", "hardware", "electronic" };
    static readonly string[] Inflect = { "disable", "operate", "control", "measure", "process", "rotate", "detect", "transmit", "receive", "store" };

    static string Base(Random r, int words)
    {
        var sb = new List<string>();
        sb.Add("The");
        while (sb.Count < words)
        {
            switch (r.Next(6))
            {
                case 0: sb.AddRange(new[] { Adjs[r.Next(Adjs.Length)], Nouns[r.Next(Nouns.Length)] }); break;
                case 1: sb.AddRange(new[] { "is", Verbs[r.Next(Verbs.Length)], "to" }); break;
                case 2: sb.AddRange(new[] { Inflect[r.Next(Inflect.Length)], "the", Nouns[r.Next(Nouns.Length)] }); break;
                case 3: sb.AddRange(new[] { "of", "the", Nouns[r.Next(Nouns.Length)] + "," }); break;
                case 4: sb.AddRange(new[] { "wherein", "the", Nouns[r.Next(Nouns.Length)], "(" + (100 + r.Next(900)) + ")" }); break;
                default: sb.AddRange(new[] { "and", "a", Adjs[r.Next(Adjs.Length)], Nouns[r.Next(Nouns.Length)] }); break;
            }
        }
        return string.Join(" ", sb.Take(words)).TrimEnd(',') + ".";
    }

    /// <summary>One change of a known kind to a sentence (as a word list).</summary>
    static string Change(Random r, string s, string kind)
    {
        var w = s.TrimEnd('.').Split(' ').ToList();
        int i = 1 + r.Next(Math.Max(1, w.Count - 1));
        if (i >= w.Count) i = w.Count - 1;
        switch (kind)
        {
            case "substitute": w[i] = Nouns[r.Next(Nouns.Length)] + "x"; break;
            case "article": { int k = w.IndexOf("the", 1); if (k < 0) return null; w[k] = "a"; break; }
            case "inflect": { int k = w.FindIndex(x => Inflect.Contains(x)); if (k < 0) return null; w[k] = w[k].TrimEnd('e') + "ing"; break; }
            case "case": w[i] = char.ToUpperInvariant(w[i][0]) + w[i].Substring(1); if (w[i] == s.Split(' ')[i]) return null; break;
            case "comma": { int k = w.FindIndex(x => x.EndsWith(",")); if (k < 0) { w[i] = w[i] + ","; } else w[k] = w[k].TrimEnd(','); break; }
            case "insert1": w.Insert(i, Adjs[r.Next(Adjs.Length)]); break;
            case "insert3": w.InsertRange(i, new[] { "by", "the", Nouns[r.Next(Nouns.Length)] + "," }); break;
            case "delete1": if (w.Count < 4) return null; w.RemoveAt(i); break;
            case "delete3": if (w.Count < 8 || i + 3 > w.Count) return null; w.RemoveRange(i, 3); break;
            case "move": { if (w.Count < 6) return null; var x = w[i]; w.RemoveAt(i); w.Insert(i > 2 ? i - 2 : i + 2, x); break; }
            case "number": { int k = w.FindIndex(x => x.StartsWith("(")); if (k < 0) return null; w[k] = "(" + (100 + r.Next(900)) + ")"; break; }
            case "two": return Change(r, Change(r, s, "substitute") ?? s, "insert1");
            case "phrase": { if (w.Count < 12) return null; w.RemoveRange(i, Math.Min(3, w.Count - i)); w.InsertRange(i, new[] { "and", "causing,", "by", "the", "one", "or", "more", "processors," }); break; }
        }
        return string.Join(" ", w) + ".";
    }

    static IEnumerable<Pair> Synthetic(string dir)
    {
        var r = new Random(11);
        var kinds = new[] { "substitute", "article", "inflect", "case", "comma", "insert1", "insert3", "delete1", "delete3", "move", "number", "two", "phrase" };
        var mems = new List<string>();
        var docs = new List<(string kind, string doc, string mem)>();
        foreach (var len in new[] { 5, 8, 12, 18, 25, 35, 45, 60 })
            for (int n = 0; n < 30; n++)
            {
                var mem = Base(r, len);
                foreach (var k in kinds)
                {
                    var doc = Change(r, mem, k);
                    if (doc == null || doc == mem) continue;
                    docs.Add((k, doc, mem));
                }
                mems.Add(mem);
            }
        var tm = MakeTm(dir, "synthetic", mems.Distinct().Select(m => (m, "NL " + m)));
        var sw = Stopwatch.StartNew();
        var result = new List<Pair>();
        foreach (var (kind, doc, mem) in docs)
        {
            int studio = StudioScore(tm, doc, mem);
            if (studio < 0) continue;   // Studio didn't return that unit (below 30%)
            result.Add(new Pair { Kind = kind, Doc = doc, Mem = mem, Studio = studio, Ever = EverScore(doc, mem), Proto = StudioLikeScore.Score(doc, mem), Words = Words(doc) });
        }
        Console.WriteLine($"synthetic: {result.Count:N0} pairs in {sw.Elapsed.TotalSeconds:F0} s");
        return result;
    }

    // ---- natural pairs, from a COPY of EverTM's store ----

    static IEnumerable<Pair> Natural(string dir, string storeCopy, int queries)
    {
        var units = new List<(string en, string nl)>();
        using (var c = new SqliteConnection("Data Source=" + storeCopy + ";Mode=ReadOnly;Pooling=False"))
        {
            c.Open();
            using (var cmd = c.CreateCommand())
            {
                // Plain-text units only (no tags), 4-80 words, en and nl: the language pair of the case Michael saw.
                cmd.CommandText = "SELECT text_a, text_b FROM units WHERE lang_a = 'en' AND lang_b = 'nl' AND instr(text_a, '<') = 0 AND length(text_a) BETWEEN 25 AND 500 ORDER BY random() LIMIT 60000";
                using (var rd = cmd.ExecuteReader()) while (rd.Read()) units.Add((rd.GetString(0), rd.GetString(1)));
            }
        }
        var memory = units.Skip(queries).ToList();
        var tm = MakeTm(dir, "natural", memory.Select(u => (u.en, u.nl)));
        var result = new List<Pair>();
        var sw = Stopwatch.StartNew();
        foreach (var q in units.Take(queries))
        {
            var seg = new Segment(EnUs); seg.Add(q.en);
            var sr = tm.SearchSegment(new SearchSettings { Mode = SearchMode.NormalSearch, MinScore = 50, MaxResults = 10 }, seg);
            foreach (var hit in sr.Results)
            {
                var mem = hit.MemoryTranslationUnit.SourceSegment.ToPlain();
                if (mem == q.en) continue;
                result.Add(new Pair { Kind = "natural", Doc = q.en, Mem = mem, Studio = hit.ScoringResult.BaseScore, Ever = EverScore(q.en, mem), Proto = StudioLikeScore.Score(q.en, mem), Words = Words(q.en) });
            }
        }
        Console.WriteLine($"natural: {queries:N0} queries against {memory.Count:N0} units: {result.Count:N0} pairs in {sw.Elapsed.TotalSeconds:F0} s");
        return result;
    }

    /// <summary>score-rescore &lt;pairs .tsv&gt;: the pairs (and Studio's scores) written by an earlier run, scored again by EverTM today and the prototype. No Studio needed.</summary>
    public static int RunRescore(string[] args)
    {
        var pairs = new List<Pair>();
        foreach (var line in File.ReadLines(args[1]))
        {
            var f = line.Split('	');
            if (f.Length < 6) continue;
            string doc = f[f.Length - 2], mem = f[f.Length - 1];
            pairs.Add(new Pair { Kind = f[0], Studio = int.Parse(f[1]), Doc = doc, Mem = mem, Ever = EverScore(doc, mem), Proto = StudioLikeScore.Score(doc, mem), Words = Words(doc) });
        }
        var sw = Stopwatch.StartNew();
        foreach (var p in pairs) StudioLikeScore.Score(p.Doc, p.Mem);
        Console.WriteLine($"prototype: {pairs.Count:N0} pairs scored in {sw.ElapsedMilliseconds} ms ({sw.Elapsed.TotalMilliseconds * 1000 / Math.Max(1, pairs.Count):F0} µs a pair)");
        sw.Restart();
        foreach (var p in pairs) EverScore(p.Doc, p.Mem);
        Console.WriteLine($"EverTM today: {sw.ElapsedMilliseconds} ms ({sw.Elapsed.TotalMilliseconds * 1000 / Math.Max(1, pairs.Count):F0} µs a pair)");
        Report(pairs);
        if (Environment.GetEnvironmentVariable("SVSCORE_SHOW") is string show && show.Length > 0)
            foreach (var p in pairs.Where(p => p.Kind == "natural").OrderBy(p => p.Proto - p.Studio).Take(0).Concat(pairs.Where(p => p.Kind == "natural").Where(p => p.Proto > p.Studio).OrderBy(p => p.Doc.Length % 97).Take(int.TryParse(Environment.GetEnvironmentVariable("SVSCORE_SHOW"), out var nshow) ? nshow : 8)))
                Console.WriteLine($"[{p.Studio} vs {p.Proto}] D: {p.Doc} || M: {p.Mem}");
        return 0;
    }

    // ---- real document segments against a COPY of a real Studio TM ----

    /// <summary>
    /// score-compare-xliff &lt;copy of an .sdltm&gt; &lt;folder of copied .sdlxliff&gt; [segment prefix ...]: every source segment
    /// of the documents looked up in the TM copy; every result at 50% or more is a pair. Prints distributions only; for a
    /// segment starting with one of the given prefixes, its scores (no text).
    /// </summary>
    public static int RunXliff(string[] args)
    {
        var tm = new FileBasedTranslationMemory(args[1]).LanguageDirection;
        var src = tm.SourceLanguage;
        var prefixes = args.Skip(3).ToList();
        var pairs = new List<Pair>();
        System.Xml.Linq.XNamespace x = "urn:oasis:names:tc:xliff:document:1.2";
        var sw = Stopwatch.StartNew();
        int segs = 0;
        foreach (var file in Directory.GetFiles(args[2], "*.sdlxliff"))
        {
            var doc = System.Xml.Linq.XDocument.Load(file);
            foreach (var mrk in doc.Descendants(x + "seg-source").SelectMany(s2 => s2.Descendants(x + "mrk")).Where(m => (string)m.Attribute("mtype") == "seg"))
            {
                var text = string.Concat(mrk.DescendantNodes().OfType<System.Xml.Linq.XText>().Select(t => t.Value)).Trim();
                if (VaultDiff.Words(text).Count < 3) continue;
                segs++;
                var seg = new Segment(src); seg.Add(text);
                var sr = tm.SearchSegment(new SearchSettings { Mode = SearchMode.NormalSearch, MinScore = 50, MaxResults = 10 }, seg);
                foreach (var hit in sr.Results)
                {
                    var mem = hit.MemoryTranslationUnit.SourceSegment.ToPlain();
                    var p = new Pair { Kind = "document", Doc = text, Mem = mem, Studio = hit.ScoringResult.BaseScore, Ever = EverScore(text, mem), Proto = StudioLikeScore.Score(text, mem), Words = Words(text) };
                    pairs.Add(p);
                    for (int k = 0; k < prefixes.Count; k++)
                        if (text.StartsWith(prefixes[k], StringComparison.Ordinal))
                            Console.WriteLine($"  marked segment {k + 1}: Studio {p.Studio}%, EverTM today {p.Ever}%, prototype {p.Proto}% ({p.Words} words)");
                }
            }
        }
        Console.WriteLine($"documents: {segs:N0} segments, {pairs.Count:N0} pairs in {sw.Elapsed.TotalSeconds:F0} s");
        Report(pairs.Where(p => p.Studio < 100 || p.Ever < 100).ToList());
        return 0;
    }

    // ---- scoring both ways ----

    static ITranslationMemoryLanguageDirection MakeTm(string dir, string name, IEnumerable<(string src, string trg)> units)
    {
        var tm = new FileBasedTranslationMemory(Path.Combine(dir, name + ".sdltm"), name, EnUs, NlNl,
            FuzzyIndexes.SourceWordBased | FuzzyIndexes.SourceCharacterBased, BuiltinRecognizers.RecognizeAll, TokenizerFlags.DefaultFlags, WordCountFlags.DefaultFlags);
        tm.Save();
        var batch = new List<TranslationUnit>();
        foreach (var (s, t) in units)
        {
            var a = new Segment(EnUs); a.Add(s);
            var b = new Segment(NlNl); b.Add(t);
            batch.Add(new TranslationUnit(a, b));
            if (batch.Count == 1000) { tm.LanguageDirection.AddTranslationUnitsMasked(batch.ToArray(), new ImportSettings(), batch.Select(_ => true).ToArray()); batch.Clear(); }
        }
        if (batch.Count > 0) tm.LanguageDirection.AddTranslationUnitsMasked(batch.ToArray(), new ImportSettings(), batch.Select(_ => true).ToArray());
        return tm.LanguageDirection;
    }

    /// <summary>Studio's BaseScore for the unit whose source is <paramref name="mem"/>, or -1 if Studio didn't return it.</summary>
    static int StudioScore(ITranslationMemoryLanguageDirection tm, string doc, string mem)
    {
        var seg = new Segment(EnUs); seg.Add(doc);
        var sr = tm.SearchSegment(new SearchSettings { Mode = SearchMode.NormalSearch, MinScore = 30, MaxResults = 30 }, seg);
        var hit = sr.Results.FirstOrDefault(h => h.MemoryTranslationUnit.SourceSegment.ToPlain() == mem);
        return hit == null ? -1 : hit.ScoringResult.BaseScore;
    }

    // EverTM's English rules, made per thread: a stemmer keeps state.
    [ThreadStatic] static VaultWordRules _rules;
    static VaultWordRules Rules => _rules ?? (_rules = VaultLanguage.Rules("en"));

    internal static int EverScore(string doc, string mem)
    {
        var a = new List<VaultPiece> { new VaultPiece { Text = doc } };
        var b = new List<VaultPiece> { new VaultPiece { Text = mem } };
        return VaultDiff.Compare(VaultDiff.Tokenize(a), VaultDiff.Tokenize(b), VaultLookup.PlainOf(a) == VaultLookup.PlainOf(b), Rules).Score;
    }

    static int Words(string s) => VaultDiff.Words(s).Count;

    // ---- the report ----

    static int Band(int s) => s >= 100 ? 5 : s >= 95 ? 4 : s >= 85 ? 3 : s >= 75 ? 2 : s >= 50 ? 1 : 0;
    static readonly string[] BandNames = { "<50", "50-74", "75-84", "85-94", "95-99", "100" };

    static void Report(List<Pair> pairs)
    {
        Console.WriteLine("=== EverTM today (VaultDiff) ===");
        Report(pairs, p => p.Ever);
        Console.WriteLine("=== prototype (Studio-like) ===");
        Report(pairs, p => p.Proto);
    }

    static void Report(List<Pair> pairs, Func<Pair, int> ever)
    {
        void Line(string label, IEnumerable<Pair> ps)
        {
            var l = ps.ToList();
            if (l.Count == 0) return;
            var d = l.Select(p => ever(p) - p.Studio).OrderBy(x => x).ToList();
            double mean = d.Average();
            int within2 = d.Count(x => Math.Abs(x) <= 2), higher = d.Count(x => x > 0), bandDiff = l.Count(p => Band(ever(p)) != Band(p.Studio));
            Console.WriteLine($"  {label,-14} {l.Count,6:N0} pairs  EverTM-Studio mean {mean,6:F1}  median {d[d.Count / 2],4}  p5 {d[d.Count / 20],4}  p95 {d[d.Count * 19 / 20],4}  within ±2 {100.0 * within2 / l.Count,5:F1}%  EverTM higher {100.0 * higher / l.Count,5:F1}%  other band {100.0 * bandDiff / l.Count,5:F1}%");
        }
        Console.WriteLine("EverTM's score minus Studio's, for the same pair:");
        Line("all", pairs);
        foreach (var g in pairs.GroupBy(p => p.Kind).OrderBy(g => g.Key)) Line(g.Key, g);
        Console.WriteLine("by length (words in the document segment):");
        foreach (var (lo, hi) in new[] { (0, 7), (8, 15), (16, 30), (31, 50), (51, 999) })
            Line(lo + "-" + (hi == 999 ? "+" : hi.ToString()), pairs.Where(p => p.Words >= lo && p.Words <= hi));
        Console.WriteLine("Analyze bands (rows Studio, columns EverTM):");
        Console.WriteLine("            " + string.Join("", BandNames.Select(b => b.PadLeft(8))));
        for (int s = 0; s < BandNames.Length; s++)
        {
            var row = pairs.Where(p => Band(p.Studio) == s).ToList();
            if (row.Count == 0) continue;
            Console.WriteLine("  " + BandNames[s].PadRight(10) + string.Join("", Enumerable.Range(0, BandNames.Length).Select(e => row.Count(p => Band(ever(p)) == e).ToString("N0").PadLeft(8))));
        }
    }
}
