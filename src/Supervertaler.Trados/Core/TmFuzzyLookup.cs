using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.TranslationMemory;
using Sdl.LanguagePlatform.TranslationMemoryApi;

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
            public int Score;
            /// <summary>Which memory it came from - for the log, not the prompt.</summary>
            public string TmName;
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
        public static Match[] FindBest(
            List<string> tmEntries,
            IList<string> sourceTexts,
            CultureInfo sourceCulture,
            int minScore,
            Action<int, int> progress,
            CancellationToken ct)
        {
            var results = new Match[sourceTexts == null ? 0 : sourceTexts.Count];
            if (results.Length == 0 || tmEntries == null || tmEntries.Count == 0) return results;
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

            try
            {
                foreach (var entry in tmEntries)
                {
                    ct.ThrowIfCancellationRequested();
                    foreach (var ld in OpenDirections(entry))
                    {
                        if (ld == null) continue;
                        tmsSearched++;
                        SearchOne(ld, DisplayNameOf(entry), keys, sourceCulture,
                                  minScore, best, progress, ct);
                    }
                }
            }
            finally
            {
                // The authenticated-server cache is per run, like the concordance path's.
                try { ServerTmClient.ResetCache(); } catch { }
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
        private static IEnumerable<ITranslationProviderLanguageDirection> OpenDirections(string entry)
        {
            if (string.IsNullOrEmpty(entry)) yield break;

            if (ServerTmClient.IsServerTmUri(entry))
            {
                ServerTmClient.ServerTmRef sref;
                if (!ServerTmClient.TryParseServerTmUri(entry, out sref)) yield break;
                List<ITranslationMemoryLanguageDirection> lds;
                try { lds = ServerTmClient.OpenLanguageDirections(sref).ToList(); }
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
                    var found = TakeBest(batch[i], tmName);
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
        /// The single best usable hit out of one segment's results. A hit with no
        /// readable source or no target is discarded rather than sent half-blind -
        /// that is the failure this whole feature exists to prevent.
        /// </summary>
        private static Match TakeBest(SearchResults sr, string tmName)
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

                if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
                    continue;

                var score = r.ScoringResult == null ? 0 : r.ScoringResult.Match;
                if (best == null || score > best.Score)
                    best = new Match
                    {
                        SourceText = source,
                        TargetText = target,
                        Score = score,
                        TmName = tmName
                    };
            }
            return best;
        }
    }
}
