using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Sdl.Core.Globalization;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.Core.Tokenization;
using Sdl.LanguagePlatform.TranslationMemory;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using Supervertaler.Core.SuperVault;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.SuperVault;

/// <summary>
/// AI Batch Translate's "send TM matches" with EverTM (TmFuzzyLookup.FindBest): EverTM takes part in the best-match
/// choice like a project .sdltm, a project TM's match of the same score wins, and an unusable EverTM gives no matches,
/// never an error. Test data: Acme / PROJ-001 only.
/// </summary>
static class TmMatchChecks
{
    static Action<bool, string> Check;
    static readonly CultureCode NlNl = new CultureCode("nl-NL"), EnGb = new CultureCode("en-GB");
    static readonly System.Globalization.CultureInfo Nl = new System.Globalization.CultureInfo("nl-NL");

    public static void Run(string scratch, Action<bool, string> check)
    {
        Check = check;
        var dir = Path.Combine(scratch, "tmmatch");
        Directory.CreateDirectory(dir);
        Console.WriteLine("AI Batch Translate's TM matches include EverTM");
        ProjectFile();
        BestMatch(dir);
        FailurePaths(dir);
    }

    static void ProjectFile()
    {
        XDocument P(string entries) => XDocument.Parse("<Project><CascadeItem>" + entries + "</CascadeItem><LanguageDirections><LanguageDirection><CascadeItem/></LanguageDirection></LanguageDirections></Project>");
        string E(string uri, string lookup = "true", string enabled = "true") =>
            "<CascadeEntryItem PerformConcordanceSearch=\"true\" Penalty=\"0\" PerformUpdate=\"false\" PerformNormalSearch=\"" + lookup + "\"><MainTranslationProviderItem Uri=\"" + uri + "\" State=\"\" Enabled=\"" + enabled + "\" /></CascadeEntryItem>";
        Check(TmSearcher.UsesEverTm(P(E("sdltm.file:///C:/Acme.sdltm") + E("evertm:///"))), "a project with EverTM, Lookup ticked: used");
        Check(TmSearcher.UsesEverTm(P(E("supervault:///"))), "the old name (supervault:///): used");
        Check(!TmSearcher.UsesEverTm(P(E("evertm:///", lookup: "false"))), "Lookup unticked: not used");
        Check(!TmSearcher.UsesEverTm(P(E("evertm:///", enabled: "false"))), "disabled in the project: not used");
        Check(!TmSearcher.UsesEverTm(P(E("sdltm.file:///C:/Acme.sdltm"))), "a project without EverTM: not used");
        var inPair = XDocument.Parse("<Project><CascadeItem/><LanguageDirections><LanguageDirection><CascadeItem OverrideParent=\"true\">" + E("evertm:///") + "</CascadeItem></LanguageDirection></LanguageDirections></Project>");
        Check(TmSearcher.UsesEverTm(inPair), "EverTM set for one language pair only: used");
        Check(!TmSearcher.ProjectUsesEverTm(@"C:\nowhere\at\all\doc.sdlxliff") && !TmSearcher.ProjectUsesEverTm(null), "no project file: not used, no error");
    }

    static string Tmx(string dir, string name, params (string nl, string en)[] units)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "<?xml version=\"1.0\" encoding=\"utf-8\"?><tmx version=\"1.4\"><header srclang=\"nl-NL\" segtype=\"sentence\" datatype=\"plaintext\" o-tmf=\"t\" adminlang=\"en\" creationtool=\"t\" creationtoolversion=\"1\"/><body>" +
            string.Concat(units.Select(u => "<tu><tuv xml:lang=\"nl-NL\"><seg>" + u.nl + "</seg></tuv><tuv xml:lang=\"en-GB\"><seg>" + u.en + "</seg></tuv></tu>")) + "</body></tmx>");
        return path;
    }

    static Segment Seg(CultureCode c, string t) { var s = new Segment(c); s.Add(t); return s; }

    static void BestMatch(string dir)
    {
        // The project's own TM.
        var tm = new FileBasedTranslationMemory(Path.Combine(dir, "acme.sdltm"), "acme", NlNl, EnGb,
            FuzzyIndexes.SourceWordBased, BuiltinRecognizers.RecognizeAll, TokenizerFlags.DefaultFlags, WordCountFlags.DefaultFlags);
        tm.Save();
        tm.LanguageDirection.AddTranslationUnitsMasked(new[]
        {
            new TranslationUnit(Seg(NlNl, "De rode pomp wordt elke ochtend gestart."), Seg(EnGb, "The red pump is started every morning.")),
            new TranslationUnit(Seg(NlNl, "De klep wordt automatisch gesloten na gebruik."), Seg(EnGb, "The valve is closed automatically after use.")),
        }, new ImportSettings(), new[] { true, true });

        // EverTM: one of the same sentences with another translation, and sentences the project TM doesn't have.
        var root = Path.Combine(dir, "evertm");
        VaultLibrary.For(root).Import((store, p, c) => VaultTmx.Import(store, Tmx(dir, "evertm.tmx",
            ("De rode pomp wordt elke ochtend gestart.", "Every morning the red pump is started."),
            ("De blauwe ventilator draait sneller bij hitte.", "The blue fan turns faster in the heat."),
            ("De klep wordt automatisch gesloten.", "The valve closes automatically.")), p, c), null, CancellationToken.None);
        var everTm = new SuperVaultProvider(root).GetLanguageDirection(new LanguagePair(NlNl, EnGb));

        var texts = new List<string>
        {
            "De rode pomp wordt elke ochtend gestart.",                // both have it at 100%: the project TM's
            "De blauwe ventilator draait langzamer bij hitte.",        // a fuzzy only EverTM has
            "De klep wordt automatisch gesloten na gebruik.",          // project TM 100%, EverTM fuzzy: the project TM's
            "Een zin die nergens in staat, over iets heel anders.",    // nothing
        };
        var m = TmFuzzyLookup.FindBest(new List<string> { tm.FilePath }, texts, Nl, 70, null, CancellationToken.None, everTm);
        Check(m[0] != null && m[0].TmName != "EverTM" && m[0].Score == 100, "same score in both: the project TM's match is kept (" + m[0]?.TmName + ")");
        Check(m[1] != null && m[1].TmName == "EverTM" && m[1].Score >= 70 && m[1].Score < 100 && m[1].SourceText == "De blauwe ventilator draait sneller bij hitte." && m[1].TargetText == "The blue fan turns faster in the heat.",
              $"a fuzzy only EverTM has: offered with its own source text ({m[1]?.Score}%: {m[1]?.SourceText})");
        Check(m[2] != null && m[2].TmName != "EverTM" && m[2].Score == 100, "the project TM's 100% beats EverTM's fuzzy");
        Check(m[3] == null, "nothing found: no match");

        var only = TmFuzzyLookup.FindBest(new List<string>(), texts, Nl, 70, null, CancellationToken.None, everTm);
        Check(only[0]?.TmName == "EverTM" && only[0].Score == 100 && only[0].TargetText == "Every morning the red pump is started.", "a project with EverTM and no other TM: EverTM's matches");
        var none = TmFuzzyLookup.FindBest(new List<string> { tm.FilePath }, texts, Nl, 70, null, CancellationToken.None);
        Check(none[1] == null, "without EverTM (Lookup unticked): EverTM is not searched");
        var high = TmFuzzyLookup.FindBest(new List<string>(), texts, Nl, 95, null, CancellationToken.None, everTm);
        Check(high[1] == null, "an EverTM fuzzy below the lowest score to send is not offered");

        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            bool cancelled = false;
            try { TmFuzzyLookup.FindBest(new List<string>(), texts, Nl, 70, null, cts.Token, everTm); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Cancel stops the EverTM search");
        }
    }

    static void FailurePaths(string dir)
    {
        var texts = new List<string> { "De rode pomp wordt elke ochtend gestart.", "De klep sluit." };
        // A folder that can't exist (a file in the way), an empty EverTM, and a damaged index.
        var file = Path.Combine(dir, "a-file");
        File.WriteAllText(file, "x");
        var empty = Path.Combine(dir, "empty");
        var damaged = Path.Combine(dir, "damaged");
        VaultLibrary.For(damaged).Import((store, p, c) => VaultTmx.Import(store, Tmx(dir, "d.tmx", ("De klep sluit.", "The valve closes.")), p, c), null, CancellationToken.None);
        var gen = Directory.GetDirectories(damaged, "gen-*").Single();
        foreach (var f in Directory.GetFiles(gen, "segments*")) File.WriteAllText(f, "not an index");
        foreach (var (what, root) in new[] { ("an EverTM folder that can't be made", Path.Combine(file, "evertm")), ("an empty EverTM", empty), ("a damaged index", damaged) })
        {
            Exception error = null;
            TmFuzzyLookup.Match[] m = null;
            try { m = TmFuzzyLookup.FindBest(new List<string>(), texts, Nl, 70, null, CancellationToken.None, new SuperVaultProvider(root).GetLanguageDirection(new LanguagePair(NlNl, EnGb))); }
            catch (Exception ex) { error = ex; }
            Check(error == null && m != null && m.All(x => x == null), what + ": no matches and no error, so the batch goes on" + (error == null ? "" : " (" + error.Message + ")"));
        }
    }

    /// <summary>
    /// SuperVaultCheck.exe tmmatch-scale &lt;EverTM folder (a COPY, with an index)&gt; [n]: times FindBest with EverTM only,
    /// for n distinct sentences sampled from the store with one word changed (fuzzy lookups, the usual case), one thread
    /// and the default. Prints counts and times only.
    /// </summary>
    public static int Scale(string root, int n)
    {
        var lib = VaultLibrary.For(root);
        if (lib.Current == null)
        {
            var b = Stopwatch.StartNew();
            Console.WriteLine($"index built: {lib.Build(null, CancellationToken.None):N0} translation units in {b.Elapsed.TotalSeconds:F0} s");
        }
        var sample = new List<string>();
        using (var c = new SqliteConnection("Data Source=" + lib.StorePath + ";Mode=ReadOnly;Pooling=False"))
        {
            c.Open();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT text_b FROM units WHERE lang_a = 'en' AND lang_b = 'nl' AND length(text_b) BETWEEN 30 AND 300 ORDER BY random() LIMIT " + n;
                using (var r = cmd.ExecuteReader()) while (r.Read()) sample.Add(VaultMarkup.Plain(r.GetString(0)));
            }
        }
        var rnd = new Random(7);
        var texts = sample.Select(s => { var w = s.Split(' '); w[rnd.Next(w.Length)] = "xyzzy" + rnd.Next(); return string.Join(" ", w); }).Distinct().ToList();
        var everTm = new SuperVaultProvider(root).GetLanguageDirection(new LanguagePair(NlNl, EnGb));
        TmFuzzyLookup.FindBest(new List<string>(), texts.Take(20).ToList(), Nl, 70, null, CancellationToken.None, everTm);   // warm-up
        int defaultThreads = TmFuzzyLookup.EverTmThreads;
        Console.WriteLine($"{(Environment.Is64BitProcess ? "64" : "32")}-bit, {Environment.ProcessorCount} cores; {lib.Status.Units:N0} translation units; {texts.Count:N0} distinct fuzzy texts");
        var env = Environment.GetEnvironmentVariable("SVCHECK_THREADS");   // e.g. "2,8": other counts to compare
        var counts = env == null ? new[] { 1, defaultThreads } : env.Split(',').Select(int.Parse).ToArray();
        foreach (var threads in counts.Distinct())
        {
            TmFuzzyLookup.EverTmThreads = threads;
            var part = threads == 1 ? texts.Take(Math.Min(texts.Count, 2000)).ToList() : texts;   // one thread: a sample, extrapolated
            var sw = Stopwatch.StartNew();
            var m = TmFuzzyLookup.FindBest(new List<string>(), part, Nl, 70, null, CancellationToken.None, everTm);
            sw.Stop();
            double perText = sw.Elapsed.TotalMilliseconds / part.Count;
            Console.WriteLine($"  {threads} thread(s): {part.Count:N0} texts in {sw.Elapsed.TotalSeconds:F1} s, {perText:F1} ms each, {m.Count(x => x != null):N0} matched; " +
                              $"20,000 texts: about {perText * 20000 / 60000:F1} min; peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576} MB");
        }
        TmFuzzyLookup.EverTmThreads = defaultThreads;
        SqliteConnection.ClearAllPools();
        return 0;
    }
}
