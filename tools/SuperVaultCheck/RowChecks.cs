using System;
using System.IO;
using System.Linq;
using Sdl.Core.Globalization;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.Core.Tokenization;
using Sdl.LanguagePlatform.TranslationMemory;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using Supervertaler.Core.SuperVault;
using Supervertaler.Trados.SuperVault;

/// <summary>
/// The very same unit in a project TM and in EverTM: Studio shows one row (its duplicate merge), the better-scored one,
/// and the project TM's on a tie. Michael's answer 3 (11 Nov 2026): EverTM's row may replace the TM's when EverTM scores
/// higher, without capping; and EverTM never lowers the TM's own match.
/// </summary>
static class RowChecks
{
    static readonly CultureCode EnUs = new CultureCode("en-US"), NlNl = new CultureCode("nl-NL");

    public static void Run(string scratch, Action<bool, string> check)
    {
        Console.WriteLine("One row for the same unit in a project TM and in EverTM");
        var dir = Path.Combine(scratch, "rows");
        Directory.CreateDirectory(dir);
        // The same score in both: Michael's second claim pair (Studio 87%, EverTM 87%).
        Pair(dir, "tie", DiffDisplayChecks.ClaimDoc2, DiffDisplayChecks.ClaimMem2, everHigher: false, check);
        // EverTM a few points higher: a moved word Studio's alignment doesn't see as a move (Studio 92%, EverTM 95%).
        Pair(dir, "higher", "The wherein the store bearing (352) the panel.", "The wherein the bearing (352) store the panel.", everHigher: true, check);
    }

    static void Pair(string dir, string name, string doc, string mem, bool everHigher, Action<bool, string> check)
    {
        var tm = new FileBasedTranslationMemory(Path.Combine(dir, name + ".sdltm"), name, EnUs, NlNl, FuzzyIndexes.SourceWordBased | FuzzyIndexes.SourceCharacterBased, BuiltinRecognizers.RecognizeAll, TokenizerFlags.DefaultFlags, WordCountFlags.DefaultFlags);
        tm.Save();
        tm.LanguageDirection.AddTranslationUnitsMasked(new[] { new TranslationUnit(Seg(EnUs, mem), Seg(NlNl, "NL " + mem)) }, new ImportSettings(), new[] { true });
        var root = Path.Combine(dir, "evertm-" + name);
        var tmx = Path.Combine(dir, name + ".tmx");
        File.WriteAllText(tmx, "<?xml version=\"1.0\" encoding=\"utf-8\"?><tmx version=\"1.4\"><header srclang=\"en-US\" segtype=\"sentence\" datatype=\"plaintext\" o-tmf=\"t\" adminlang=\"en\" creationtool=\"t\" creationtoolversion=\"1\"/><body>" +
            "<tu><tuv xml:lang=\"en-US\"><seg>" + System.Security.SecurityElement.Escape(mem) + "</seg></tuv><tuv xml:lang=\"nl-NL\"><seg>" + System.Security.SecurityElement.Escape("NL " + mem) + "</seg></tuv></tu></body></tmx>");
        VaultLibrary.For(root).Import((store, p, c) => VaultTmx.Import(store, tmx, p, c), null, System.Threading.CancellationToken.None);

        var settings = new SearchSettings { Mode = SearchMode.NormalSearch, MinScore = 30, MaxResults = 10 };
        int alone = tm.LanguageDirection.SearchSegment(settings, Seg(EnUs, doc)).Results.FirstOrDefault()?.ScoringResult.BaseScore ?? -1;
        int ever = new SuperVaultProvider(root).GetLanguageDirection(new LanguagePair(EnUs, NlNl)).SearchSegment(settings, Seg(EnUs, doc)).Results.FirstOrDefault()?.ScoringResult.BaseScore ?? -1;
        check(everHigher ? ever > alone : ever == alone, $"{name}: Studio's TM {alone}%, EverTM {ever}% for the same unit");

        foreach (var everFirst in new[] { false, true })
        {
            var e = new CascadeEntry(new SuperVaultProvider(root).GetLanguageDirection(new LanguagePair(EnUs, NlNl)), 0);
            var t = new CascadeEntry(tm.LanguageDirection, 0);
            var cascade = new Cascade<CascadeEntry>(everFirst ? new[] { e, t } : new[] { t, e }, false, true, EnUs, NlNl);
            var rows = cascade.SearchSegment(settings, Seg(EnUs, doc)).Results;
            bool everRow = rows.Count == 1 && rows[0].MemoryTranslationUnit.OriginSystem == "EverTM";
            check(rows.Count == 1 && everRow == everHigher && rows[0].ScoringResult.BaseScore == Math.Max(alone, ever),
                  $"{name}, EverTM {(everFirst ? "first" : "last")} in the list: one row, {(everHigher ? "EverTM's (it scores higher)" : "the project TM's (a tie)")}, at {Math.Max(alone, ever)}%: " +
                  string.Join(", ", rows.Select(r => (r.MemoryTranslationUnit.OriginSystem == "EverTM" ? "EverTM " : "Studio TM ") + r.ScoringResult.BaseScore + "%")));
        }
        // "Never lowers your project TMs' matches" (penalties between the two) is NeverLowersTheClientTm's, in the full run.
    }

    static Segment Seg(CultureCode c, string t) { var s = new Segment(c); s.Add(t); return s; }
}
