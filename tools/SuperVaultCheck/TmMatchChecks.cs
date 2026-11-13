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
        Numbers(scratch, check);
    }

    // ---- a sentence that differs only in its numbers (13 Nov 2026) -------------------------------------------------
    // Studio's own TM scores it 100% and keeps the stored texts; sent on as "100% match" the prompt told the model the
    // source was the same while showing another claim number. Checked here against a real file TM and EverTM.

    const string ClaimNl = "Werkwijze volgens conclusie {0}, waarbij de sensor een temperatuursensor is.";
    const string ClaimEn = "The method according to claim {0}, wherein the sensor is a temperature sensor.";

    public static void Numbers(string scratch, Action<bool, string> check)
    {
        Check = check;
        var dir = Path.Combine(scratch, "tmnumbers");
        Directory.CreateDirectory(dir);
        Console.WriteLine("A TM match for the same sentence with other numbers is never sent as 100%");
        Judging();

        var tm = new FileBasedTranslationMemory(Path.Combine(dir, "acme-numbers.sdltm"), "acme-numbers", NlNl, EnGb,
            FuzzyIndexes.SourceWordBased, BuiltinRecognizers.RecognizeAll, TokenizerFlags.DefaultFlags, WordCountFlags.DefaultFlags);
        tm.Save();
        var units = new[]
        {
            new TranslationUnit(Seg(NlNl, string.Format(ClaimNl, 3)), Seg(EnGb, string.Format(ClaimEn, 3))),
            new TranslationUnit(Seg(NlNl, "Het mengsel wordt gedurende 12 minuten bij 4 bar geroerd."), Seg(EnGb, "The mixture is stirred for 12 minutes at 4 bar.")),
            new TranslationUnit(Seg(NlNl, "Stap 5 wordt na 5 uur herhaald met 5 monsters."), Seg(EnGb, "Step 5 is repeated after 5 hours with 5 samples.")),
            new TranslationUnit(Seg(NlNl, "De klep wordt automatisch gesloten na gebruik."), Seg(EnGb, "The valve is closed automatically after use.")),
            new TranslationUnit(Seg(NlNl, "Inrichting volgens conclusie 9, waarbij het filter vervangbaar is."), Seg(EnGb, "The device according to claim 9, wherein the filter is replaceable.")),
        };
        tm.LanguageDirection.AddTranslationUnitsMasked(units, new ImportSettings(), units.Select(u => true).ToArray());
        var tms = new List<string> { tm.FilePath };

        var texts = new List<string>
        {
            string.Format(ClaimNl, 7),                                             // 0 one changed number
            "Het mengsel wordt gedurende 4 minuten bij 12 bar geroerd.",          // 1 two numbers changed places
            "Stap 8 wordt na 8 uur herhaald met 8 monsters.",                     // 2 a number occurring three times
            "De klep wordt automatisch gesloten na gebruik.",                     // 3 identical, no numbers
            "Inrichting volgens conclusie 9, waarbij het filter vervangbaar is.", // 4 identical, with a number
            "De klep wordt handmatig gesloten na gebruik.",                       // 5 an ordinary fuzzy, no numbers
            string.Format(ClaimNl, 7),                                             // 6 a repeat of 0: same answer
        };
        var m = TmFuzzyLookup.FindBest(tms, texts, Nl, 70, null, CancellationToken.None);

        string[] what = { "one changed number", "two numbers that changed places", "a number occurring three times" };
        string[] storedSource = { string.Format(ClaimNl, 3), "Het mengsel wordt gedurende 12 minuten bij 4 bar geroerd.", "Stap 5 wordt na 5 uur herhaald met 5 monsters." };
        string[] storedTarget = { string.Format(ClaimEn, 3), "The mixture is stirred for 12 minutes at 4 bar.", "Step 5 is repeated after 5 hours with 5 samples." };
        for (int i = 0; i < 3; i++)
        {
            var x = m[i];
            Check(x != null && x.Score < 100 && x.Numbers == Supervertaler.Core.FuzzyNumberDifference.Stored,
                  $"Studio TM, {what[i]}: not 100, marked as different numbers ({x?.Score}%, Studio itself scored it 100: {x?.ScoredExact})");
            Check(x != null && x.SourceText == storedSource[i] && x.TargetText == storedTarget[i],
                  $"Studio TM, {what[i]}: the stored texts, no number replaced in the translation ({x?.TargetText})");
        }
        Check(m[0] != null && m[0].ScoredExact, "Studio's own TM scores one changed number as 100%: the case this guards against is real");
        Check(m[3] != null && m[3].Score == 100 && m[3].Numbers == Supervertaler.Core.FuzzyNumberDifference.None && !m[3].ScoredExact, "identical text without numbers stays 100%");
        Check(m[4] != null && m[4].Score == 100 && m[4].Numbers == Supervertaler.Core.FuzzyNumberDifference.None && !m[4].ScoredExact, "identical text with a number stays 100%");
        Check(m[5] != null && m[5].Score < 100 && m[5].Numbers == Supervertaler.Core.FuzzyNumberDifference.None && !m[5].ScoredExact,
              $"an ordinary fuzzy without numbers is unchanged ({m[5]?.Score}%)");
        Check(m[6] != null && ReferenceEquals(m[6], m[0]) || (m[6] != null && m[6].Score == m[0].Score && m[6].Numbers == m[0].Numbers), "a repeated sentence gets the same answer");

        var exactOnly = TmFuzzyLookup.FindBest(tms, texts, Nl, 100, null, CancellationToken.None);
        Check(exactOnly[0] == null && exactOnly[1] == null && exactOnly[2] == null && exactOnly[3] != null && exactOnly[4] != null,
              "lowest match to send = 100: a sentence with other numbers is not offered, identical ones are");

        // EverTM: replaces a number where that can't go wrong, and scores 99.
        var root = Path.Combine(dir, "evertm");
        VaultLibrary.For(root).Import((store, p, c) => VaultTmx.Import(store, Tmx(dir, "numbers.tmx",
            (string.Format(ClaimNl, 3), string.Format(ClaimEn, 3)),
            ("Het mengsel wordt gedurende 12 minuten bij 4 bar geroerd.", "The mixture is stirred for 12 minutes at 4 bar.")), p, c), null, CancellationToken.None);
        var everTm = new SuperVaultProvider(root).GetLanguageDirection(new LanguagePair(NlNl, EnGb));
        var e = TmFuzzyLookup.FindBest(new List<string>(), texts, Nl, 70, null, CancellationToken.None, everTm);
        Check(e[0] != null && e[0].Score == 99 && e[0].Numbers == Supervertaler.Core.FuzzyNumberDifference.Replaced
              && e[0].SourceText == string.Format(ClaimNl, 3) && e[0].TargetText == string.Format(ClaimEn, 7),
              $"EverTM, one changed number: 99%, marked as already replaced ({e[0]?.Score}%, {e[0]?.Numbers}: {e[0]?.TargetText})");
        Check(e[1] != null && e[1].Score < 100 && e[1].Numbers == Supervertaler.Core.FuzzyNumberDifference.Stored
              && e[1].TargetText == "The mixture is stirred for 12 minutes at 4 bar.",
              $"EverTM, numbers that changed places: nothing replaced, marked as different numbers ({e[1]?.Score}%, {e[1]?.Numbers}: {e[1]?.TargetText})");

        // The identical sentence in another memory wins over the one with other numbers.
        var root2 = Path.Combine(dir, "evertm-identical");
        VaultLibrary.For(root2).Import((store, p, c) => VaultTmx.Import(store, Tmx(dir, "identical.tmx",
            (string.Format(ClaimNl, 7), "The method of claim 7, the sensor being a temperature sensor.")), p, c), null, CancellationToken.None);
        var identical = new SuperVaultProvider(root2).GetLanguageDirection(new LanguagePair(NlNl, EnGb));
        var both = TmFuzzyLookup.FindBest(tms, texts, Nl, 70, null, CancellationToken.None, identical);
        Check(both[0] != null && both[0].Score == 100 && both[0].TmName == "EverTM" && both[0].TargetText.StartsWith("The method of claim 7"),
              $"the identical sentence in another memory beats the one with other numbers ({both[0]?.Score}% from {both[0]?.TmName})");

        Prompt(m, texts);
        OriginRule(tms, texts);
    }

    /// <summary>The judging on its own: no memory needed, so every case is exact.</summary>
    static void Judging()
    {
        TmFuzzyLookup.Match D(string searched, string source, int score, bool replaced = false, int min = 70, string target = "x")
            => TmFuzzyLookup.Describe(searched, source, target, score, replaced, min, "tm");

        var one = D("Zie conclusie 7.", "Zie conclusie 3.", 100);
        Check(one.Score == 99 && one.ScoredExact && one.Numbers == Supervertaler.Core.FuzzyNumberDifference.Stored, "judged: one changed number at 100 becomes 99, different numbers");
        var swapped = D("4 minuten bij 12 bar.", "12 minuten bij 4 bar.", 100);
        Check(swapped.Score == 99 && swapped.Numbers == Supervertaler.Core.FuzzyNumberDifference.Stored, "judged: numbers that changed places at 100 become 99, different numbers");
        var twice = D("Van 8 tot 8.", "Van 5 tot 5.", 100);
        Check(twice.Score == 99 && twice.Numbers == Supervertaler.Core.FuzzyNumberDifference.Stored, "judged: a number occurring twice");
        var same = D("Zie conclusie 7.", "Zie conclusie 7.", 100);
        Check(same.Score == 100 && !same.ScoredExact && same.Numbers == Supervertaler.Core.FuzzyNumberDifference.None, "judged: identical text stays 100");
        var spaces = D("Zie  conclusie 7. ", "Zie conclusie 7.", 100);
        Check(spaces.Score == 100 && !spaces.ScoredExact, "judged: only white space differs: still 100");
        var noNumbers = D("De klep is dicht.", "De klep is dicht.", 100);
        Check(noNumbers.Score == 100 && noNumbers.Numbers == Supervertaler.Core.FuzzyNumberDifference.None, "judged: no numbers, identical: 100");
        var word = D("De klep is gesloten.", "De klep is dicht.", 100);
        Check(word.Score == 99 && word.ScoredExact && word.Numbers == Supervertaler.Core.FuzzyNumberDifference.None, "judged: a memory's 100 for another WORD is not believed either (99, no numbers label)");
        var fuzzy = D("De klep is gesloten.", "De klep is dicht.", 85);
        Check(fuzzy.Score == 85 && !fuzzy.ScoredExact && fuzzy.Numbers == Supervertaler.Core.FuzzyNumberDifference.None, "judged: an ordinary fuzzy keeps its score");
        var penalised = D("Zie conclusie 7.", "Zie conclusie 3.", 92);
        Check(penalised.Score == 92 && !penalised.ScoredExact && penalised.Numbers == Supervertaler.Core.FuzzyNumberDifference.Stored, "judged: other numbers below 100 keep the score and get the label");
        var replaced = D("Zie conclusie 7.", "Zie conclusie 3.", 99, replaced: true);
        Check(replaced.Score == 99 && replaced.Numbers == Supervertaler.Core.FuzzyNumberDifference.Replaced, "judged: numbers the memory replaced are marked as replaced");
        var replacedSame = D("Zie conclusie 7.", "Zie conclusie 7.", 100, replaced: true);
        Check(replacedSame.Numbers == Supervertaler.Core.FuzzyNumberDifference.None, "judged: 'replaced' means nothing for identical text");
        Check(D("Zie conclusie 7.", "Zie conclusie 3.", 100, min: 100) == null, "judged: other numbers are below a lowest score of 100");
        Check(D("Zie conclusie 7.", "Zie conclusie 7.", 100, min: 100) != null, "judged: identical text passes a lowest score of 100");
        Check(D("Zie conclusie 7.", "", 100) == null && D("Zie conclusie 7.", "Zie conclusie 7.", 100, target: " ") == null && D(null, null, 100) == null,
              "judged: a hit without a source or a translation is no match, no error");
    }

    /// <summary>What the model is shown for these matches: the run's own conversion and Plugin Core's block.</summary>
    static void Prompt(TmFuzzyLookup.Match[] m, List<string> texts)
    {
        var toInput = typeof(BatchTranslator).GetMethod("ToPromptInput", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var inputs = new List<Supervertaler.Core.BatchSegmentInput>();
        for (int i = 0; i < 5; i++)
        {
            var seg = new BatchSegment { Index = i, SourceText = texts[i], TmSourceText = m[i].SourceText, TmTargetText = m[i].TargetText, TmMatchPercent = m[i].Score, TmNumbers = m[i].Numbers };
            inputs.Add((Supervertaler.Core.BatchSegmentInput)toInput.Invoke(null, new object[] { seg, i + 1, Supervertaler.Core.StructureContextMode.Off }));
        }
        var block = Supervertaler.Core.TranslationPrompt.BuildMemoryBlock(inputs).Replace("\r", "");
        Check(block.Contains("Segment 1 – same sentence, different numbers\n"
                + "  this segment:          " + string.Format(ClaimNl, 7) + "\n"
                + "  source in memory:      " + string.Format(ClaimNl, 3) + "\n"
                + "  translation in memory: " + string.Format(ClaimEn, 3) + "\n"),
              "the prompt names the claim match as different numbers and shows the segment's text above the memory's");
        Check(block.Contains("Segment 2 – same sentence, different numbers\n") && block.Contains("Segment 3 – same sentence, different numbers\n"),
              "so too for numbers that changed places and a number occurring three times");
        Check(block.Contains("Segment 4 – 100% match\n  source in memory:") && block.Contains("Segment 5 – 100% match\n  source in memory:"),
              "identical sentences are still 100% matches");
        Check(System.Text.RegularExpressions.Regex.Matches(block, "100% match\n").Count == 2, "and no other match is called 100%");
        Check(block.Contains("use the segment's own numbers"), "the model is told to use the segment's numbers");
    }

    /// <summary>
    /// A segment Studio pre-translated carries "100%" and a target, but not the source it was matched on. The searched
    /// match that Studio itself scored 100 for other numbers replaces that; an ordinary fuzzy never does.
    /// </summary>
    static void OriginRule(List<string> tms, List<string> texts)
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public;
        var view = typeof(BatchTranslator).Assembly.GetType("Supervertaler.Trados.AiAssistantViewPart");
        var planType = view.GetNestedType("TmLookupPlan", flags);
        var plan = Activator.CreateInstance(planType, true);
        planType.GetField("Tms").SetValue(plan, tms);
        planType.GetField("SourceCulture").SetValue(plan, Nl);
        planType.GetField("MinScore").SetValue(plan, 70);
        var apply = view.GetMethod("ApplyTmMatches", flags);

        var segments = new List<BatchSegment>
        {
            new BatchSegment { Index = 0, SourceText = texts[0], ExistingTarget = string.Format(ClaimEn, 7), TmMatchPercent = 100 },   // pre-translated, numbers replaced by Studio
            new BatchSegment { Index = 1, SourceText = texts[5], ExistingTarget = "The valve is closed by hand after use.", TmMatchPercent = 100 },   // a 100% from a memory no longer attached
            new BatchSegment { Index = 2, SourceText = texts[3], ExistingTarget = "The valve is closed automatically after use.", TmMatchPercent = 100 },
        };
        apply.Invoke(null, new object[] { plan, segments, CancellationToken.None, null });
        Check(segments[0].TmSourceText == string.Format(ClaimNl, 3) && segments[0].TmMatchPercent < 100 && segments[0].TmNumbers == Supervertaler.Core.FuzzyNumberDifference.Stored,
              "a pre-translated 100% that was really other numbers: sent with both texts and the label, not as a bare 100%");
        Check(segments[1].TmSourceText == null && segments[1].TmMatchPercent == 100, "an ordinary fuzzy still never replaces a segment's own 100%");
        Check(segments[2].TmSourceText == texts[3] && segments[2].TmMatchPercent == 100 && segments[2].TmNumbers == Supervertaler.Core.FuzzyNumberDifference.None,
              "an identical match is taken as before, at 100%");
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
