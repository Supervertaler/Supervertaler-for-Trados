using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Supervertaler.Trados.Models;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Searches the user's terminology for SuperSearch, across all three kinds
    /// of termbase the plugin can read:
    ///
    ///   * Supervertaler termbases  – the shared SQLite database, restricted to
    ///     the ones whose Read tick is on;
    ///   * MultiTerm <c>.sdltb</c>  – the Trados project's own termbases;
    ///   * Trados <c>.ttb</c>       – the Studio 2026 format, read through the
    ///     same <see cref="ITermbaseReader"/> abstraction.
    ///
    /// Why it lives inside SuperSearch rather than in a panel of its own:
    /// "where does this phrase appear?" and "what have I called this term?" are
    /// the same question at different granularities, and answering them in two
    /// places means searching twice. Results reuse <see cref="SearchResult"/>
    /// with <see cref="ResultKind.TermbaseEntry"/>, so the existing grid,
    /// filtering and export paths need no special cases beyond disabling
    /// replace (a term is not a document location).
    ///
    /// Matching goes through <see cref="XliffSearcher.QueryMatches"/> – the same
    /// predicate the file and TM searches use – so the Aa / .* / Word options
    /// behave identically no matter which scope is selected.
    /// </summary>
    public static class TermbaseSearcher
    {
        /// <summary>Cap per termbase, so a 90k-term database can't flood the grid.</summary>
        private const int MaxHitsPerTermbase = 500;

        /// <summary>Describes one searchable termbase for the caller's UI.</summary>
        public class TermbaseSource
        {
            /// <summary>Display name, shown in the File/TM column.</summary>
            public string Name;
            /// <summary>"Supervertaler", "MultiTerm" or "TTB" – shown in Status.</summary>
            public string Kind;
            /// <summary>File path for .sdltb/.ttb; the shared DB path for Supervertaler.</summary>
            public string Path;
            /// <summary>Supervertaler termbase id; -1 for file-based termbases.</summary>
            public long SupervertalerId = -1;
            public string SourceIndexName;
            public string TargetIndexName;
            /// <summary>True when this termbase's terms come from TermLens's
            /// in-memory index rather than a fresh read of the database/file.</summary>
            public bool FromLoadedIndex;
        }

        /// <summary>The label shown in the TBs picker and used to identify a
        /// termbase in the user's include/exclude selection. Includes the kind so
        /// a Supervertaler and a MultiTerm termbase of the same name stay
        /// distinguishable.</summary>
        public static string Label(TermbaseSource s)
            => s == null ? "" : s.Name + " (" + s.Kind + ")";

        /// <summary>
        /// Lists every termbase SuperSearch can search: the Supervertaler ones
        /// from the shared database, plus the open project's MultiTerm/.ttb
        /// termbases (<paramref name="projectTermbases"/>, from
        /// MultiTermProjectDetector, resolved on the UI thread by the caller).
        /// </summary>
        public static List<TermbaseSource> Discover(
            List<MultiTermTermbaseConfig> projectTermbases)
        {
            // FAST PATH: TermLens already holds every loaded term in memory —
            // oriented to the project, restricted to the Read-enabled
            // termbases, with MultiTerm/.ttb entries merged in. Re-reading the
            // database here cost ~26 s on a 90k-term database for results
            // TermLens was already holding, so use its index whenever it is
            // populated and keep the database path purely as a fallback for
            // when TermLens hasn't loaded yet.
            var loaded = LoadedTerms();
            if (loaded.Count > 0)
            {
                var byName = new Dictionary<string, TermbaseSource>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in loaded)
                {
                    if (e == null) continue;
                    var name = string.IsNullOrEmpty(e.TermbaseName) ? "(unnamed)" : e.TermbaseName;
                    if (byName.ContainsKey(name)) continue;
                    byName[name] = new TermbaseSource
                    {
                        Name = name,
                        Kind = e.IsMultiTerm ? "MultiTerm" : "Supervertaler",
                        SupervertalerId = e.IsMultiTerm ? -1 : e.TermbaseId,
                        FromLoadedIndex = true,
                    };
                }
                if (byName.Count > 0) return byName.Values.ToList();
            }

            var sources = new List<TermbaseSource>();

            // ─── Supervertaler termbases (shared SQLite DB) ───────────────
            try
            {
                var settings = SettingsService.Current;
                var dbPath = settings?.TermbasePath;
                if (!string.IsNullOrEmpty(dbPath) && System.IO.File.Exists(dbPath))
                {
                    // Only termbases whose Read tick is ON. The Read column is
                    // the user's statement of which terminology is in play for
                    // this job; searching the rest would both contradict that
                    // and make every search pay for termbases they switched off
                    // on purpose.
                    var disabled = new HashSet<long>(settings.DisabledTermbaseIds ?? new List<long>());
                    using (var reader = new TermbaseReader(dbPath))
                    {
                        if (reader.Open())
                        {
                            foreach (var tb in reader.GetTermbases() ?? new List<TermbaseInfo>())
                            {
                                if (disabled.Contains(tb.Id)) continue;
                                sources.Add(new TermbaseSource
                                {
                                    Name = tb.Name,
                                    Kind = "Supervertaler",
                                    Path = dbPath,
                                    SupervertalerId = tb.Id,
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Log("SuperSearch", "Termbase discovery (Supervertaler) failed: " + ex.Message);
            }

            // ─── The project's MultiTerm (.sdltb) / Trados (.ttb) termbases ──
            foreach (var cfg in projectTermbases ?? new List<MultiTermTermbaseConfig>())
            {
                if (cfg == null || string.IsNullOrEmpty(cfg.FilePath)) continue;
                // Same rule as above: a termbase disabled in Trados Project
                // Settings is not part of this job.
                if (!cfg.TradosEnabled) continue;
                var isTtb = cfg.FilePath.EndsWith(".ttb", StringComparison.OrdinalIgnoreCase);
                sources.Add(new TermbaseSource
                {
                    Name = cfg.TermbaseName ?? System.IO.Path.GetFileNameWithoutExtension(cfg.FilePath),
                    Kind = isTtb ? "TTB" : "MultiTerm",
                    Path = cfg.FilePath,
                    SourceIndexName = cfg.SourceIndexName,
                    TargetIndexName = cfg.TargetIndexName,
                });
            }

            return sources;
        }

        /// <summary>
        /// Searches the given termbases and returns matching term pairs as
        /// <see cref="SearchResult"/>s. <paramref name="scope"/> selects which
        /// side of the pair the query is applied to, mirroring file/TM search.
        /// </summary>
        /// <param name="projectSourceLang">The OPEN PROJECT's source language.
        /// Termbases are oriented to it before matching, so the Src box always
        /// means "the language you translate from" rather than "whichever
        /// column this particular termbase happens to call source". Without
        /// this, searching a Dutch phrase in an NL→EN project found nothing in
        /// an EN→NL termbase, because the Dutch sat in its target column.</param>
        public static List<SearchResult> Search(
            IEnumerable<TermbaseSource> sources,
            string query,
            SearchScope scope,
            bool caseSensitive,
            bool useRegex,
            bool wholeWord,
            string projectSourceLang,
            Action<int, int> progress,
            CancellationToken ct)
        {
            var results = new List<SearchResult>();
            if (string.IsNullOrEmpty(query)) return results;

            var list = (sources ?? Enumerable.Empty<TermbaseSource>()).ToList();
            int done = 0;

            // The Supervertaler termbases all live in ONE SQLite database, so
            // read it ONCE and bucket the entries by termbase id. Loading per
            // termbase meant a full pass over every term in the database for
            // each termbase searched — with a 90k-term database and a dozen
            // termbases that is dozens of full loads for a single search, which
            // is exactly how slow it felt.
            // When discovery used TermLens's index, take the terms from there
            // too: no database read, no second copy in memory, and the entries
            // are already oriented to the project.
            if (list.Count > 0 && list.All(s => s.FromLoadedIndex))
            {
                var wantedNames = new HashSet<string>(list.Select(s => s.Name),
                    StringComparer.OrdinalIgnoreCase);
                var kindByName = list.ToDictionary(s => s.Name, s => s.Kind,
                    StringComparer.OrdinalIgnoreCase);
                var perTermbase = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var e in LoadedTerms())
                {
                    ct.ThrowIfCancellationRequested();
                    if (e == null) continue;
                    var name = string.IsNullOrEmpty(e.TermbaseName) ? "(unnamed)" : e.TermbaseName;
                    if (!wantedNames.Contains(name)) continue;

                    int seen;
                    perTermbase.TryGetValue(name, out seen);
                    if (seen >= MaxHitsPerTermbase) continue;

                    var srcText = e.SourceTerm ?? "";
                    var tgtText = e.TargetTerm ?? "";
                    if (!Matches(srcText, tgtText, query, scope, caseSensitive, useRegex, wholeWord))
                        continue;

                    perTermbase[name] = seen + 1;
                    string kind;
                    kindByName.TryGetValue(name, out kind);
                    results.Add(new SearchResult
                    {
                        Kind = ResultKind.TermbaseEntry,
                        FileName = name,
                        FilePath = name,
                        SourceText = srcText,
                        TargetText = tgtText,
                        Status = kind ?? "Termbase",
                        Term = e,   // keeps the id, so the hit can be edited (#104)
                    });
                }
                progress?.Invoke(list.Count, list.Count);
                return results;
            }

            Dictionary<long, List<Tuple<string, string>>> svByTermbase = null;
            var svSources = list.Where(s => s.SupervertalerId >= 0).ToList();
            if (svSources.Count > 0)
            {
                svByTermbase = LoadSupervertalerTermsGrouped(
                    svSources[0].Path,
                    new HashSet<long>(svSources.Select(s => s.SupervertalerId)),
                    projectSourceLang,
                    ct);
            }

            foreach (var src in list)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    List<Tuple<string, string>> pairs;
                    if (src.SupervertalerId >= 0)
                    {
                        if (svByTermbase == null
                            || !svByTermbase.TryGetValue(src.SupervertalerId, out pairs))
                            pairs = new List<Tuple<string, string>>();
                    }
                    else
                    {
                        pairs = LoadFileTermbaseTerms(src);
                    }

                    int hitsThisTermbase = 0;
                    foreach (var pair in pairs)
                    {
                        ct.ThrowIfCancellationRequested();
                        var srcText = pair.Item1 ?? "";
                        var tgtText = pair.Item2 ?? "";

                        bool hit;
                        switch (scope)
                        {
                            case SearchScope.SourceOnly:
                                hit = XliffSearcher.QueryMatches(srcText, query, caseSensitive, useRegex, wholeWord);
                                break;
                            case SearchScope.TargetOnly:
                                hit = XliffSearcher.QueryMatches(tgtText, query, caseSensitive, useRegex, wholeWord);
                                break;
                            default:
                                hit = XliffSearcher.QueryMatches(srcText, query, caseSensitive, useRegex, wholeWord)
                                   || XliffSearcher.QueryMatches(tgtText, query, caseSensitive, useRegex, wholeWord);
                                break;
                        }
                        if (!hit) continue;

                        results.Add(new SearchResult
                        {
                            Kind = ResultKind.TermbaseEntry,
                            FileName = src.Name,
                            FilePath = src.Path,
                            SourceText = srcText,
                            TargetText = tgtText,
                            // The Status column carries provenance: which kind
                            // of termbase the term came from.
                            Status = src.Kind,
                        });

                        // Per termbase, not a running total across all of them:
                        // as a global count this fired on the 500th hit overall
                        // and then truncated every later termbase.
                        if (++hitsThisTermbase >= MaxHitsPerTermbase) break;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    DiagnosticLog.Log("SuperSearch",
                        $"Termbase search failed for '{src.Name}': {ex.Message}");
                }

                done++;
                progress?.Invoke(done, list.Count);
            }

            return results;
        }

        /// <summary>TermLens's in-memory term index, or an empty list when it
        /// hasn't loaded yet. Never throws.</summary>
        private static List<TermEntry> LoadedTerms()
        {
            try { return TermLensEditorViewPart.GetCurrentTermbaseTerms() ?? new List<TermEntry>(); }
            catch { return new List<TermEntry>(); }
        }

        /// <summary>The scope-aware match test, shared by both paths.</summary>
        private static bool Matches(string srcText, string tgtText, string query,
            SearchScope scope, bool caseSensitive, bool useRegex, bool wholeWord)
        {
            switch (scope)
            {
                case SearchScope.SourceOnly:
                    return XliffSearcher.QueryMatches(srcText, query, caseSensitive, useRegex, wholeWord);
                case SearchScope.TargetOnly:
                    return XliffSearcher.QueryMatches(tgtText, query, caseSensitive, useRegex, wholeWord);
                default:
                    return XliffSearcher.QueryMatches(srcText, query, caseSensitive, useRegex, wholeWord)
                        || XliffSearcher.QueryMatches(tgtText, query, caseSensitive, useRegex, wholeWord);
            }
        }

        /// <summary>
        /// Reads the shared Supervertaler database ONCE and returns the
        /// source→target pairs bucketed by termbase id, restricted to
        /// <paramref name="wanted"/> (the Read-enabled termbases).
        /// </summary>
        private static Dictionary<long, List<Tuple<string, string>>> LoadSupervertalerTermsGrouped(
            string dbPath, HashSet<long> wanted, string projectSourceLang, CancellationToken ct)
        {
            var byTermbase = new Dictionary<long, List<Tuple<string, string>>>();
            try
            {
                using (var reader = new TermbaseReader(dbPath))
                {
                    if (!reader.Open()) return byTermbase;
                    // Passing the project's source language makes LoadAllTerms
                    // orient every entry to the project direction (it swaps
                    // source/target for termbases declared the other way round),
                    // which is the same treatment TermLens gives them.
                    var index = reader.LoadAllTerms(
                        disabledTermbaseIds: null,
                        globalCaseSensitive: false,
                        projectSourceLang: projectSourceLang);
                    foreach (var kv in index ?? new Dictionary<string, List<TermEntry>>())
                    {
                        ct.ThrowIfCancellationRequested();
                        foreach (var e in kv.Value ?? new List<TermEntry>())
                        {
                            if (e == null || !wanted.Contains(e.TermbaseId)) continue;
                            List<Tuple<string, string>> bucket;
                            if (!byTermbase.TryGetValue(e.TermbaseId, out bucket))
                            {
                                bucket = new List<Tuple<string, string>>();
                                byTermbase[e.TermbaseId] = bucket;
                            }
                            bucket.Add(Tuple.Create(e.SourceTerm ?? "", e.TargetTerm ?? ""));
                        }
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                DiagnosticLog.Log("SuperSearch",
                    "Loading Supervertaler termbases failed: " + ex.Message);
            }
            return byTermbase;
        }

        /// <summary>Source→target pairs from a .sdltb or .ttb termbase.</summary>
        private static List<Tuple<string, string>> LoadFileTermbaseTerms(TermbaseSource src)
        {
            var pairs = new List<Tuple<string, string>>();
            using (var reader = TermbaseReaderFactory.Create(src.Path))
            {
                if (!reader.Open()) return pairs;
                var index = reader.LoadAllTerms(
                    src.SourceIndexName, src.TargetIndexName, -1, src.Name);
                foreach (var kv in index ?? new Dictionary<string, List<TermEntry>>())
                {
                    foreach (var e in kv.Value ?? new List<TermEntry>())
                    {
                        if (e == null) continue;
                        pairs.Add(Tuple.Create(e.SourceTerm ?? "", e.TargetTerm ?? ""));
                    }
                }
            }
            return pairs;
        }
    }
}
