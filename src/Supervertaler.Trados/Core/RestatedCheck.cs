using System;
using System.Collections.Generic;
using Supervertaler.Core;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// What one Batch Translate run knows about the passages its document
    /// repeats: every segment's source, in document order, and the translation
    /// each has - the one the run wrote, or the one that was already there for a
    /// segment the run left alone. Two things come out of it, both from core's
    /// <see cref="RestatedPassages"/>:
    ///
    /// <para>1. During the run, the earlier translations to show the AI for a
    /// segment that repeats a passage (<see cref="ReferencesFor"/>;
    /// docs/design/in-run-references.md).</para>
    ///
    /// <para>2. At the end, where the run still translated a repeated passage two
    /// ways (<see cref="Summary"/>; docs/design/restated-passages.md).</para>
    ///
    /// <para>The document is the memory. A translation is known here only once
    /// Studio has taken the write, or because it was in the document before the
    /// run and the run will not overwrite it. A target the run is about to
    /// overwrite is not known. Nothing is written to disk, a TM or a memory
    /// bank. It lives and dies with the run's <see cref="BatchTranslator"/>: a
    /// run that is stopped reports on what it wrote, a run that throws reports
    /// nothing, and neither leaves anything behind. A second run over the same
    /// document starts from the document again.</para>
    ///
    /// <para>Threads: the segments are added on Studio's UI thread before the run
    /// starts; translations are recorded and looked up on the run's thread;
    /// <see cref="Summary"/> runs on a pool thread at the end. One lock covers
    /// all of them. The pairs are found once, at the first lookup or the
    /// summary, whichever comes first - never on the UI thread during a run.</para>
    ///
    /// <para>Scale: tested at 10,000 segments, designed for 10,000. It keeps two
    /// strings per segment; a lookup reads one segment's few partners.</para>
    /// </summary>
    internal sealed class RestatedCheck
    {
        private readonly object _lock = new object();
        private readonly List<string> _sources = new List<string>();
        private readonly List<RestatedTarget> _targets = new List<RestatedTarget>();
        private readonly Dictionary<BatchSegment, int> _sent = new Dictionary<BatchSegment, int>();

        // Found once the segments are all in (see Pairs).
        private RestatedPairs _pairs;
        private EarlierTranslations _earlier;

        /// <summary>Segments of this run that were shown an earlier translation.</summary>
        private readonly HashSet<int> _shown = new HashSet<int>();

        /// <summary>
        /// A segment the run will not write, with the translation it has.
        /// </summary>
        public void Kept(string label, string source, string target)
        {
            lock (_lock)
            {
                _sources.Add(Plain(source));
                _targets.Add(new RestatedTarget { Label = label, Text = Plain(target) });
                Forget();
            }
        }

        /// <summary>
        /// A segment the run will translate. It has no translation until
        /// <see cref="Written"/>: whatever is in the document now is about to be
        /// replaced, and if the segment fails it is left out of the report.
        /// </summary>
        public void Sent(BatchSegment segment)
        {
            if (segment == null) return;
            lock (_lock)
            {
                _sent[segment] = _sources.Count;
                _sources.Add(Plain(segment.SourceText));
                _targets.Add(new RestatedTarget { Label = segment.Label(), Text = null });
                Forget();
            }
        }

        /// <summary>
        /// The translation Studio has just taken for a segment, as the run
        /// delivered it: with the source's tag placeholders, and with the AI's
        /// <c>[[TC: ...]]</c> note still at its end.
        /// </summary>
        public void Written(BatchSegment segment, string translation)
        {
            if (segment == null) return;
            lock (_lock)
            {
                if (!_sent.TryGetValue(segment, out var index)) return;
                _targets[index].Text = Plain(translation);
            }
        }

        /// <summary>
        /// The earlier translations to show the AI for a segment of this run:
        /// those of segments that share a passage with it and have a translation
        /// now. Empty for a segment that repeats nothing, or whose partners are
        /// not translated yet - in the same request, later in the run, failed,
        /// or declined by Studio.
        /// </summary>
        public List<EarlierTranslation> ReferencesFor(BatchSegment segment)
        {
            if (segment == null) return new List<EarlierTranslation>();
            lock (_lock)
            {
                if (!_sent.TryGetValue(segment, out var index)) return new List<EarlierTranslation>();
                Pairs();
                var found = _earlier.For(index, other => _targets[other].Text);
                if (found.Count > 0) _shown.Add(index);
                return found;
            }
        }

        /// <summary>
        /// The same for a segment given by its place among the segments added,
        /// without counting it as shown: for Preview prompt, which sends nothing.
        /// </summary>
        public List<EarlierTranslation> PreviewReferencesFor(BatchSegment segment)
        {
            if (segment == null) return new List<EarlierTranslation>();
            lock (_lock)
            {
                if (!_sent.TryGetValue(segment, out var index)) return new List<EarlierTranslation>();
                Pairs();
                return _earlier.For(index, other => _targets[other].Text);
            }
        }

        /// <summary>
        /// The lines for the end of the run, or null when there is nothing to
        /// say. Compares the translations of the repeated passages: call it off
        /// the UI thread.
        /// </summary>
        public string Summary()
        {
            RestatedPairs pairs;
            List<RestatedTarget> targets;
            int segments, shown;
            lock (_lock)
            {
                pairs = Pairs();
                segments = _sources.Count;
                shown = _shown.Count;
                targets = new List<RestatedTarget>(_targets.Count);
                foreach (var t in _targets)
                    targets.Add(new RestatedTarget { Label = t.Label, Text = t.Text });
            }

            // Counts and time only, never text: the diagnostic log gets pasted
            // into issues. The time is how the check is watched at real sizes.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var report = RestatedPassages.ReportOn(pairs, targets);
            clock.Stop();
            DiagnosticLog.Log("Restated", segments + " segments: " + shown + " shown an earlier translation, "
                + report.Compared + " pairs compared, "
                + report.Skipped + " skipped (no translation), " + report.Different.Count + " translated differently; "
                + clock.ElapsedMilliseconds + " ms"
                + (report.Limited ? "; pair limit reached" : ""));

            var lines = report.Summary();
            if (shown == 0) return lines;

            var first = shown.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                + (shown == 1 ? " segment was" : " segments were")
                + " shown an earlier translation of a passage "
                + (shown == 1 ? "it repeats." : "they repeat.");
            return lines == null ? first : first + Environment.NewLine + lines;
        }

        /// <summary>The document's pairs, found on first use. Call with the lock held.</summary>
        private RestatedPairs Pairs()
        {
            if (_pairs == null)
            {
                _pairs = RestatedPassages.Find(_sources);
                _earlier = new EarlierTranslations(_sources, _pairs);
            }
            return _pairs;
        }

        /// <summary>A segment was added: the pairs, if already found, are for a shorter document.</summary>
        private void Forget()
        {
            _pairs = null;
            _earlier = null;
        }

        /// <summary>Text without the <c>&lt;tN&gt;</c> placeholders Batch Translate writes tags as.</summary>
        private static string Plain(string text) =>
            string.IsNullOrEmpty(text) ? "" : SegmentTagHandler.StripTagPlaceholders(text);
    }
}
