using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.Core.EditDistance;
using Sdl.LanguagePlatform.TranslationMemory;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using Supervertaler.Core;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Asks the project's translation memories for the closest match to each
    /// source segment, and returns the match's SOURCE alongside its target and
    /// score (issue #116).
    ///
    /// <para><b>Why this exists rather than reading the segment's origin.</b>
    /// <c>ITranslationOrigin</c> - what Studio leaves behind after
    /// pre-translation - records how close a match was but not the source it was
    /// made for. A fuzzy taken from there could only be offered to the model as a
    /// bare target: a translation of a sentence it cannot read, with no way to see
    /// which words differ. Searching the memory returns both sides, so the model
    /// can be shown the difference instead of being asked to trust it. Sending a
    /// fuzzy's target without its source is the one thing this must never do.</para>
    ///
    /// <para><b>Scale.</b> Segments are deduplicated (a patent repeats itself
    /// heavily), then searched in batches through <c>SearchSegmentsMasked</c> -
    /// one round trip per batch per TM, not one per segment, which is what makes a
    /// GroupShare memory usable at all. Tested on documents of a few hundred
    /// segments; designed for several thousand against a multi-gigabyte
    /// <c>.sdltm</c>. Every run logs its own timings under the "TmFuzzy" category
    /// so the real cost is measured rather than assumed, and the whole pass is
    /// cancellable between batches.</para>
    /// </summary>
    public static class TmFuzzyLookup
    {
        private const string LogCategory = "TmFuzzy";

        /// <summary>
        /// Segments per <c>SearchSegmentsMasked</c> call. Small enough that
        /// cancellation lands promptly and a GroupShare server is not handed a
        /// request it will reject for size (the concordance path already has to
        /// back off from 100 to 50 on GroupShare 2020 SR1), large enough that the
        /// per-call overhead is amortised.
        /// </summary>
        private const int SearchBatchSize = 20;

        /// <summary>One match: both sides and how close it was.</summary>
        public class Match
        {
            public string SourceText;
            public string TargetText;
            /// <summary>
            /// How close it was. 100 ONLY when <see cref="SourceText"/> is the searched
            /// text itself: a memory gives 100 to a sentence with other numbers too
            /// (it replaces them), and such a match is 99 here at most.
            /// </summary>
            public int Score;
            /// <summary>Which memory it came from - for the log, not the prompt.</summary>
            public string TmName;
            /// <summary>Set when the source differs from the searched text only in its numbers; says whether
            /// the memory already put the searched text's numbers into <see cref="TargetText"/>.</summary>
            public FuzzyNumberDifference Numbers;
            /// <summary>The memory itself scored this 100 although the source is not the searched text.</summary>
            public bool ScoredExact;
        }

        /// <summary>
        /// One hit as a match, judged against the text that was searched for. The
        /// memory's score is not taken at its word: Studio scores a sentence that
        /// differs only in its numbers as 100%, and sent on as "100% match" with the
        /// stored texts it told the model the source was the same while showing it
        /// another claim number (13 Nov 2026). So the two sources are compared here,
        /// once, and everything downstream can rely on 100 meaning identical.
        /// Null when the hit lacks a side, or falls below <paramref name="minScore"/>
        /// once it is no longer called exact. Nothing in the translation is changed.
        /// </summary>
        /// <param name="numbersReplaced">The memory has already replaced the numbers in
        /// <paramref name="target"/> with the searched text's (EverTM does, where it is safe).</param>
        public static Match Describe(string searched, string source, string target, int score,
            bool numbersReplaced, int minScore, string tmName)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target)) return null;

            var difference = TmSourceCompare.Compare(searched, source);
            bool scoredExact = difference != TmSourceDifference.Same && score >= 100;
            if (scoredExact) score = 99;
            if (score < minScore) return null;

            return new Match
            {
                SourceText = source,
                TargetText = target,
                Score = score,
                TmName = tmName,
                ScoredExact = scoredExact,
                Numbers = difference != TmSourceDifference.NumbersOnly ? FuzzyNumberDifference.None
                    : numbersReplaced ? FuzzyNumberDifference.Replaced : FuzzyNumberDifference.Stored
            };
        }

        /// <summary>
        /// Finds the best match for each of <paramref name="sourceTexts"/>.
        /// The returned array is the same length and in the same order; an entry
        /// is null where no memory had anything at or above
        /// <paramref name="minScore"/>.
        ///
        /// Never throws for a memory that cannot be opened or searched - a locked,
        /// corrupt or unauthenticated TM is skipped and logged. Returning fewer
        /// matches degrades the prompt; failing the run would lose the
        /// translator's work.
        /// </summary>
        /// <param name="everTm">EverTM's language direction for the document's pair, when the project uses EverTM
        /// for lookups; null otherwise. Searched after the memories in <paramref name="tmEntries"/>, through EverTM's
        /// own running index (<see cref="SearchEverTm"/>). A project TM's match of the same score wins.</param>
        public static Match[] FindBest(
            List<string> tmEntries,
            IList<string> sourceTexts,
            CultureInfo sourceCulture,
            int minScore,
            Action<int, int> progress,
            CancellationToken ct,
            ITranslationProviderLanguageDirection everTm = null)
        {
            var results = new Match[sourceTexts == null ? 0 : sourceTexts.Count];
            tmEntries = tmEntries ?? new List<string>();
            if (results.Length == 0 || (tmEntries.Count == 0 && everTm == null)) return results;
            if (sourceCulture == null) return results;

            // Deduplicate: the same sentence repeated forty times is one lookup.
            // Keys are the tag-stripped text actually sent to the memory.
            var distinct = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < sourceTexts.Count; i++)
            {
                var plain = SegmentTagHandler.StripTagPlaceholders(sourceTexts[i] ?? "");
                if (string.IsNullOrWhiteSpace(plain)) continue;
                List<int> list;
                if (!distinct.TryGetValue(plain, out list))
                    distinct[plain] = list = new List<int>();
                list.Add(i);
            }
            if (distinct.Count == 0) return results;

            var keys = distinct.Keys.ToList();
            var best = new Match[keys.Count];

            var sw = Stopwatch.StartNew();
            int tmsSearched = 0;

            // This run's server sign-ins, one per host; gone with the run.
            var connections = new ServerTmClient.Connections();
            foreach (var entry in tmEntries)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var ld in OpenDirections(entry, connections))
                {
                    if (ld == null) continue;
                    tmsSearched++;
                    SearchOne(ld, DisplayNameOf(entry), keys, sourceCulture,
                              minScore, best, progress, ct);
                }
            }
            if (everTm != null)
            {
                tmsSearched++;
                SearchEverTm(everTm, keys, sourceCulture, minScore, best, progress, ct);
            }

            // Fan the per-distinct-text results back out to every segment.
            int found = 0;
            for (int k = 0; k < keys.Count; k++)
            {
                if (best[k] == null) continue;
                found++;
                foreach (var idx in distinct[keys[k]]) results[idx] = best[k];
            }

            sw.Stop();
            try
            {
                DiagnosticLog.WriteAlways(LogCategory, string.Format(
                    "{0} segment(s), {1} distinct, {2} memor(ies), minScore {3}: "
                    + "{4} distinct text(s) matched in {5} ms ({6:N1} ms per distinct text).",
                    results.Length, keys.Count, tmsSearched, minScore, found,
                    sw.ElapsedMilliseconds,
                    keys.Count == 0 ? 0 : sw.Elapsed.TotalMilliseconds / keys.Count));
            }
            catch { }

            return results;
        }

        /// <summary>
        /// Opens a TM entry as produced by <see cref="TmSearcher.FindProjectTms"/> -
        /// an absolute <c>.sdltm</c> path or a GroupShare <c>sdltm.http(s)://</c>
        /// URI - as one or more language directions.
        /// </summary>
        private static IEnumerable<ITranslationProviderLanguageDirection> OpenDirections(
            string entry, ServerTmClient.Connections connections)
        {
            if (string.IsNullOrEmpty(entry)) yield break;

            if (ServerTmClient.IsServerTmUri(entry))
            {
                ServerTmClient.ServerTmRef sref;
                if (!ServerTmClient.TryParseServerTmUri(entry, out sref)) yield break;
                List<ITranslationMemoryLanguageDirection> lds;
                try { lds = ServerTmClient.OpenLanguageDirections(sref, connections).ToList(); }
                catch (Exception ex) { LogSkip(entry, ex); yield break; }
                foreach (var ld in lds) yield return ld;
                yield break;
            }

            ITranslationProviderLanguageDirection direction = null;
            try { direction = new FileBasedTranslationMemory(entry).LanguageDirection; }
            catch (Exception ex) { LogSkip(entry, ex); }
            if (direction != null) yield return direction;
        }

        private static void LogSkip(string entry, Exception ex)
        {
            try
            {
                DiagnosticLog.Log(LogCategory, "Skipped '" + DisplayNameOf(entry)
                    + "': " + ex.GetType().Name + ": " + ex.Message);
            }
            catch { }
        }

        private static string DisplayNameOf(string entry)
        {
            try { return TmSearcher.DisplayName(entry); }
            catch { return entry; }
        }

        /// <summary>
        /// Searches one language direction for every distinct text, keeping a hit
        /// only when it beats what another memory already offered. A higher score
        /// wins outright: showing two memories' disagreeing answers would double
        /// the prompt for a case the translator settled by attaching both.
        /// </summary>
        private static void SearchOne(
            ITranslationProviderLanguageDirection ld,
            string tmName,
            List<string> keys,
            CultureInfo sourceCulture,
            int minScore,
            Match[] best,
            Action<int, int> progress,
            CancellationToken ct)
        {
            // NormalSearch is what Studio's own editor lookup uses, and what a
            // fresh SearchSettings already defaults to. SearchMode.FuzzySearch is
            // a specialised mode, not "the ordinary lookup, fuzzies included": set
            // here it returned nothing at all against a 3.5 GB memory on text that
            // plainly had matches in it. Read the name, believed it, and lost an
            // afternoon - the enum value that sounds right is not the one to use.
            var settings = new SearchSettings
            {
                Mode = SearchMode.NormalSearch,
                MinScore = minScore,
                // A few, not one: the best hit is discarded when its source or
                // target will not read, and then the next one should still count.
                MaxResults = 3
            };

            var sw = Stopwatch.StartNew();
            int hits = 0;

            for (int start = 0; start < keys.Count; start += SearchBatchSize)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke(start, keys.Count);

                int count = Math.Min(SearchBatchSize, keys.Count - start);
                var segments = new Segment[count];
                for (int i = 0; i < count; i++)
                {
                    var seg = new Segment(sourceCulture);
                    seg.Add(keys[start + i]);
                    segments[i] = seg;
                }

                // A null mask searches every segment. The mask exists for document
                // search (SearchSettings.IsDocumentSearch), which this is not, and
                // the SDK documents it as meaningless outside that context - so the
                // obvious saving, skipping texts an earlier memory already answered
                // at 100%, is not worth taking outside its documented use. At 245 ms
                // for 20 segments against a 3.5 GB memory there is nothing to save.
                SearchResults[] batch;
                try { batch = ld.SearchSegmentsMasked(settings, segments, null); }
                catch (Exception ex)
                {
                    try
                    {
                        DiagnosticLog.Log(LogCategory, "'" + tmName + "' batch at " + start
                            + " failed: " + ex.GetType().Name + ": " + ex.Message);
                    }
                    catch { }
                    continue;
                }
                if (batch == null) continue;

                for (int i = 0; i < count && i < batch.Length; i++)
                {
                    var found = TakeBest(batch[i], tmName, keys[start + i], minScore, false);
                    if (found == null) continue;
                    var slot = start + i;
                    if (best[slot] == null || found.Score > best[slot].Score)
                    {
                        best[slot] = found;
                        hits++;
                    }
                }
            }

            sw.Stop();
            progress?.Invoke(keys.Count, keys.Count);
            try
            {
                DiagnosticLog.Log(LogCategory, string.Format(
                    "'{0}': {1} distinct text(s) in {2} ms, {3} new best match(es).",
                    tmName, keys.Count, sw.ElapsedMilliseconds, hits));
            }
            catch { }
        }

        /// <summary>
        /// Lookups EverTM runs at once. Its index serves any number of lookups in parallel (one Lucene reader, shared),
        /// and a lookup is mostly CPU (candidate scoring), so a few threads divide the time; more would take cores from
        /// Studio. Measured 10 Nov 2026 on a copy of the real EverTM (871,290 translation units), 20,000 distinct fuzzy
        /// texts: 64-bit 1 thread 23 ms each (7.7 min), 2: 4.9 min, 4: 3.9 min, 8: 4.2 min; 32-bit 1 thread 15 ms
        /// (5.0 min), 4: 2.0 min. Beyond 4 nothing is gained. Settable for the scale check.
        /// </summary>
        public static int EverTmThreads = Math.Max(1, Math.Min(4, Environment.ProcessorCount - 1));

        /// <summary>
        /// Searches EverTM for every distinct text and keeps a hit where it beats what a project memory offered (a tie
        /// keeps the project memory's). One <c>SearchSegment</c> per text, <see cref="EverTmThreads"/> at a time:
        /// EverTM's lookup is about 26 ms at 871,000 translation units, so 20,000 distinct texts one after another
        /// would hold a batch up for about 9 minutes before its first request.
        ///
        /// EverTM never throws from a lookup (an empty, rebuilding or damaged EverTM gives no matches), and anything
        /// unexpected here is caught per text and logged once: the batch always goes on, with fewer matches at worst.
        /// Cancellable between texts.
        /// </summary>
        internal static void SearchEverTm(
            ITranslationProviderLanguageDirection ld,
            List<string> keys,
            CultureInfo sourceCulture,
            int minScore,
            Match[] best,
            Action<int, int> progress,
            CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var found = new Match[keys.Count];
            int done = 0, failed = 0;
            string firstError = null;
            int threads = Math.Max(1, EverTmThreads);
            var options = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = threads };
            Parallel.For(0, keys.Count, options, k =>
            {
                try
                {
                    var settings = new SearchSettings { Mode = SearchMode.NormalSearch, MinScore = minScore, MaxResults = 3 };
                    var seg = new Segment(sourceCulture);
                    seg.Add(keys[k]);
                    var hit = TakeBest(ld.SearchSegment(settings, seg), "EverTM", keys[k], minScore, true);
                    if (hit != null) found[k] = hit;
                }
                catch (Exception ex)
                {
                    if (Interlocked.Increment(ref failed) == 1) firstError = ex.GetType().Name + ": " + ex.Message;
                }
                int n = Interlocked.Increment(ref done);
                if (progress != null && (n % 50 == 0 || n == keys.Count))
                    try { progress(n, keys.Count); } catch { }
            });

            int hits = 0;
            for (int k = 0; k < keys.Count; k++)
                if (found[k] != null && (best[k] == null || found[k].Score > best[k].Score)) { best[k] = found[k]; hits++; }

            sw.Stop();
            try
            {
                DiagnosticLog.WriteAlways(LogCategory, string.Format(
                    "EverTM: {0} distinct text(s) in {1} ms on {2} thread(s), {3} new best match(es){4}.",
                    keys.Count, sw.ElapsedMilliseconds, threads, hits,
                    failed == 0 ? "" : "; " + failed + " lookup(s) failed, first: " + firstError));
            }
            catch { }
        }

        /// <summary>
        /// The single best usable hit out of one segment's results. A hit with no
        /// readable source or no target is discarded rather than sent half-blind -
        /// that is the failure this whole feature exists to prevent.
        /// </summary>
        /// <param name="searched">The text these results were found for.</param>
        /// <param name="replacesNumbers">This memory hands back the translation with the numbers already
        /// replaced, and marks them in the score's alignment (EverTM). Studio's own memories keep the stored
        /// translation in <c>MemoryTranslationUnit</c>, which is what is read here.</param>
        private static Match TakeBest(SearchResults sr, string tmName, string searched, int minScore, bool replacesNumbers)
        {
            if (sr == null || sr.Results == null) return null;

            Match best = null;
            foreach (var r in sr.Results)
            {
                var tu = r == null ? null : r.MemoryTranslationUnit;
                if (tu == null) continue;

                string source = null, target = null;
                try
                {
                    source = tu.SourceSegment == null ? null : tu.SourceSegment.ToPlain();
                    target = tu.TargetSegment == null ? null : tu.TargetSegment.ToPlain();
                }
                catch { continue; }

                var score = r.ScoringResult == null ? 0 : r.ScoringResult.Match;
                var match = Describe(searched, source, target, score,
                    replacesNumbers && HasReplacedNumber(r.ScoringResult), minScore, tmName);
                if (match != null && (best == null || match.Score > best.Score))
                    best = match;
            }
            return best;
        }

        /// <summary>Whether the alignment marks a number the memory replaced (EverTM: SuperVaultResults.ToEditDistance).</summary>
        private static bool HasReplacedNumber(ScoringResult scoring)
        {
            try
            {
                var items = scoring == null || scoring.EditDistance == null ? null : scoring.EditDistance.Items;
                if (items == null) return false;
                foreach (var item in items)
                    if (item.Resolution == EditDistanceResolution.Substitution) return true;
            }
            catch { }
            return false;
        }
    }
}
