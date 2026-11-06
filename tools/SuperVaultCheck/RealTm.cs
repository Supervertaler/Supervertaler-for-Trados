using System;
using System.IO;
using System.Linq;
using System.Threading;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.TranslationMemory;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using Supervertaler.Core.SuperVault;
using Supervertaler.Trados.SuperVault;

/// <summary>
/// <c>SuperVaultCheck.exe import-sdltm &lt;copy of a .sdltm&gt; [scratch folder]</c>: imports a real Studio TM into a
/// scratch SuperVault, then looks up every unit's own source segment (tags and all, as Studio stored it) and counts how
/// many come back at 100% with their tags placed. Use a COPY: the TM is only read, but a live TM belongs to Studio.
/// </summary>
static class RealTm
{
    public static int Run(string sdltm, string root)
    {
        root = root ?? Path.Combine(Path.GetTempPath(), "sv-supervault-realtm-" + Guid.NewGuid().ToString("N").Substring(0, 6));
        var lib = VaultLibrary.For(root);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = lib.Import((store, p, c) => SuperVaultImport.Sdltm(store, sdltm, p, c), null, CancellationToken.None);
        Console.WriteLine($"{Path.GetFileName(sdltm)}: {r} in {sw.Elapsed.TotalSeconds:F1} s; index {lib.Status.Units} units; store {root}");

        var tm = new FileBasedTranslationMemory(sdltm);
        var dir = tm.LanguageDirection;
        var pair = new LanguagePair(dir.SourceLanguage, dir.TargetLanguage);
        var sv = new SuperVaultProvider(root).GetLanguageDirection(pair);
        var settings = new SearchSettings { Mode = SearchMode.NormalSearch, MinScore = 70, MaxResults = 5, IsDocumentSearch = true };
        int total = 0, exact = 0, withTags = 0, tagsPlaced = 0, sameTarget = 0;
        var it = new RegularIterator { MaxCount = 500 };
        TranslationUnit[] units;
        while ((units = dir.GetTranslationUnits(ref it)) != null && units.Length > 0)
            foreach (var tu in units)
            {
                total++;
                bool hasTags = tu.SourceSegment.Elements.OfType<Tag>().Any();
                if (hasTags) withTags++;
                var res = sv.SearchSegment(settings, tu.SourceSegment);
                var top = res.Results.FirstOrDefault(x => x.ScoringResult.BaseScore == 100);
                if (top == null) continue;
                exact++;
                if (hasTags && top.ScoringResult.Match == 100 && top.TranslationProposal.TargetSegment.Elements.OfType<Tag>().Count() == tu.TargetSegment.Elements.OfType<Tag>().Count()) tagsPlaced++;
                if (res.Results.Any(x => x.ScoringResult.BaseScore == 100 && SuperVaultImport.Codes(x.TranslationProposal.TargetSegment).Replace(" ", "") != "" &&
                                         Plain(x.TranslationProposal.TargetSegment) == Plain(tu.TargetSegment))) sameTarget++;
            }
        Console.WriteLine($"looked up each unit's own source: {exact}/{total} at 100%; its own translation among them {sameTarget}/{total}; " +
                          $"{withTags} units have tags, {tagsPlaced} of them came back at 100% with all target tags placed");
        return 0;
    }

    static string Plain(Segment s) => VaultMarkup.Normalize(string.Concat(s.Elements.OfType<Text>().Select(t => t.Value)));
}
