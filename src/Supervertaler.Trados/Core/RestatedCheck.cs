using System;
using System.Collections.Generic;
using Supervertaler.Core;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// What one Batch Translate run needs to say, when it ends, where it
    /// translated a restated passage two ways: every segment's source, in
    /// document order, and the translation each has - the one the run wrote, or
    /// the one that was already there for a segment the run left alone
    /// (docs/design/restated-passages.md; the finding is core's
    /// <see cref="RestatedPassages"/>).
    ///
    /// <para>Nothing here is sent anywhere or written to the document. It lives
    /// and dies with the run's <see cref="BatchTranslator"/>: a run that is
    /// stopped reports on what it wrote, a run that throws reports nothing, and
    /// neither leaves anything behind.</para>
    ///
    /// <para>Threads: the segments are added on Studio's UI thread before the run
    /// starts, translations are recorded on the run's thread as Studio takes
    /// them, and <see cref="Summary"/> runs on a pool thread at the end. One
    /// lock covers all three.</para>
    ///
    /// <para>Scale: tested at 10,000 segments, designed for 10,000. It keeps two
    /// strings per segment.</para>
    /// </summary>
    internal sealed class RestatedCheck
    {
        private readonly object _lock = new object();
        private readonly List<string> _sources = new List<string>();
        private readonly List<RestatedTarget> _targets = new List<RestatedTarget>();
        private readonly Dictionary<BatchSegment, int> _sent = new Dictionary<BatchSegment, int>();

        /// <summary>
        /// A segment the run will not write, with the translation it has. Its
        /// comments are not read, so whether it has a note is not known.
        /// </summary>
        public void Kept(string label, string source, string target)
        {
            lock (_lock)
            {
                _sources.Add(Plain(source));
                _targets.Add(new RestatedTarget { Label = label, Text = Plain(target), HasNote = null });
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
                _targets.Add(new RestatedTarget { Label = segment.Label(), Text = null, HasNote = false });
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
        /// The lines for the end of the run, or null when there is nothing to
        /// say. Finds the restated passages and compares their translations:
        /// call it off the UI thread.
        /// </summary>
        public string Summary()
        {
            List<string> sources;
            List<RestatedTarget> targets;
            lock (_lock)
            {
                sources = new List<string>(_sources);
                targets = new List<RestatedTarget>(_targets.Count);
                foreach (var t in _targets)
                    targets.Add(new RestatedTarget { Label = t.Label, Text = t.Text, HasNote = t.HasNote });
            }

            // Counts and time only, never text: the diagnostic log gets pasted
            // into issues. The time is how the check is watched at real sizes.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var report = RestatedPassages.Report(sources, targets);
            clock.Stop();
            DiagnosticLog.Log("Restated", sources.Count + " segments: " + report.Compared + " pairs compared, "
                + report.Skipped + " skipped (no translation), " + report.Different.Count + " translated differently, "
                + report.OneNote.Count + " with a note on one only; " + clock.ElapsedMilliseconds + " ms"
                + (report.Limited ? "; pair limit reached" : ""));

            return report.Summary();
        }

        /// <summary>Text without the <c>&lt;tN&gt;</c> placeholders Batch Translate writes tags as.</summary>
        private static string Plain(string text) =>
            string.IsNullOrEmpty(text) ? "" : SegmentTagHandler.StripTagPlaceholders(text);
    }
}
