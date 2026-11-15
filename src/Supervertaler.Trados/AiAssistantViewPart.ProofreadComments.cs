using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados
{
    /// <summary>
    /// "Also add issues as Trados comments" on Batch Operations, in Proofread
    /// mode: when a run ends, each finding is put on its segment as a Trados
    /// comment - the issue and the suggestion (<see cref="ProofreadFindingComment"/>).
    /// The box had been there since 4.8.0 and had never done anything.
    ///
    /// <para>The comments go on in slices on a WinForms timer, as Apply all does,
    /// so a report of hundreds of findings does not freeze Studio. A segment gets
    /// one comment from the proofreader: one that already carries one is left
    /// alone (<see cref="ProofreadFindingComment.AlreadyOn"/>), so a second run
    /// does not stack them. The comments are part of the document's unsaved
    /// changes, like any comment.</para>
    ///
    /// <para>Apply on the Reports tab follows the same box: ticked, each fix it
    /// writes gets its finding as a comment, through the same
    /// <see cref="AddFindingComment"/>.</para>
    ///
    /// UI thread only.
    /// </summary>
    public partial class AiAssistantViewPart
    {
        /// <summary>The comments being added, or null. UI thread only.</summary>
        private FindingCommentsRun _findingComments;

        private sealed class FindingCommentsRun
        {
            public ProofreadingReport Report;
            public IStudioDocument Document;
            public Dictionary<string, ISegmentPair> Index;
            public Queue<ProofreadingIssue> Pending;
            public Timer Timer;
            public int Total, Added, AlreadyThere, NotAdded;

            /// <summary>For the diagnostic log: how long the UI thread was held,
            /// in all and at most in one slice.</summary>
            public long BusyMs, LongestSliceMs;
            public int Slices;

            /// <summary>The first few refusals, for the log line at the end.</summary>
            public readonly List<string> Refusals = new List<string>();
        }

        private enum FindingCommentOutcome { Added, AlreadyThere, NotAdded }

        /// <summary>How many refusals the log names; the rest are counted, and all
        /// of them go to the diagnostic log.</summary>
        private const int RefusalsNamed = 5;

        /// <summary>
        /// Starts adding <paramref name="report"/>'s findings as comments. Called
        /// when a run has completed with the box ticked. Findings ticked off are
        /// not added; none are, in a report this new, but a run that was stopped
        /// can only have been read so far.
        /// </summary>
        private void StartFindingComments(ProofreadingReport report)
        {
            StopFindingComments();
            if (report == null || _activeDocument == null) return;

            var pending = report.Issues
                .Where(i => i != null && !i.Dismissed && ProofreadFindingComment.Text(i, null, false) != null)
                .ToList();
            // File by file, then by number: by number alone, a merged
            // document's files would be visited in turn.
            pending = report.InDocumentOrder(pending).ToList();
            if (pending.Count == 0) return;

            var run = new FindingCommentsRun
            {
                Report = report,
                Document = _activeDocument,
                Index = BuildSegmentPairIndex(null),   // once per run: it walks the whole document
                Pending = new Queue<ProofreadingIssue>(pending),
                Total = pending.Count,
                Timer = new Timer { Interval = 1 }
            };
            run.Timer.Tick += OnFindingCommentsTick;
            _findingComments = run;

            BatchLog($"Adding {run.Total} finding{(run.Total == 1 ? "" : "s")} as Trados comments… " +
                     "Untick “Also add issues as Trados comments” to stop.", false);
            run.Timer.Start();
        }

        private void OnFindingCommentsTick(object sender, EventArgs e)
        {
            var run = _findingComments;
            if (run == null || !ReferenceEquals(sender, run.Timer))
            {
                (sender as Timer)?.Stop();
                return;
            }

            // No tick while this one works: Studio may pump messages while it
            // takes a comment, and a second slice must not run inside the first.
            run.Timer.Stop();

            string stopped = null;
            try
            {
                stopped = WhyFindingCommentsStop(run);
                if (stopped == null)
                    AddFindingCommentsSlice(run);
            }
            catch (Exception ex)
            {
                // Whatever broke, the run ends and says so: a timer that is never
                // started again must not leave the comments half-added in silence.
                stopped = "an error (" + ex.Message + ")";
                try { DiagnosticLog.Log("Proofread", "Adding findings as comments failed: " + ex); } catch { }
            }

            if (stopped != null || run.Pending.Count == 0)
                FinishFindingComments(run, stopped);
            else
                run.Timer.Start();
        }

        /// <summary>Why the comments must stop before the next slice, or null.
        /// Worded to follow "stopped: ". A new proofreading run replaces the
        /// report, so it stops them too. Unticking the box is the way to stop on
        /// purpose: Studio takes about a fifth of a second over each comment
        /// (measured, 17 Oct 2026), so a few hundred findings take a minute.</summary>
        private string WhyFindingCommentsStop(FindingCommentsRun run)
        {
            if (!_control.Value.BatchTranslateControl.AddAsComments) return "the box was unticked";
            if (!ReferenceEquals(_activeDocument, run.Document)) return "the document in the editor changed";
            if (!ReferenceEquals(_currentReport, run.Report)) return "the report was cleared or replaced";
            return null;
        }

        private void AddFindingCommentsSlice(FindingCommentsRun run)
        {
            var sw = Stopwatch.StartNew();
            // The document is asked again before each finding, not only per slice:
            // the pairs in the index belong to the document the run began on.
            while (run.Pending.Count > 0 && sw.ElapsedMilliseconds < ApplySliceMs
                   && ReferenceEquals(_activeDocument, run.Document))
            {
                var issue = run.Pending.Dequeue();
                if (issue.Dismissed) continue;   // ticked off since the run began

                switch (AddFindingComment(issue, run.Report.Model, run.Index, out var refusal))
                {
                    case FindingCommentOutcome.Added: run.Added++; break;
                    case FindingCommentOutcome.AlreadyThere: run.AlreadyThere++; break;
                    default:
                        run.NotAdded++;
                        var line = "segment " + issue.Label + ": " + refusal;
                        if (run.Refusals.Count < RefusalsNamed) run.Refusals.Add(line);
                        try { DiagnosticLog.Log("Proofread", "Finding not added as a comment, " + line); } catch { }
                        break;
                }
            }

            run.Slices++;
            run.BusyMs += sw.ElapsedMilliseconds;
            run.LongestSliceMs = Math.Max(run.LongestSliceMs, sw.ElapsedMilliseconds);

            // Progress where the translator is looking: a run with findings ends on
            // the Reports tab. The outcome replaces it at the end.
            if (run.Pending.Count > 0 && ReferenceEquals(_currentReport, run.Report))
            {
                try
                {
                    _control.Value.ReportsControl.ShowApplySummary(
                        $"Adding findings as Trados comments: {run.Total - run.Pending.Count} of {run.Total}… " +
                        "Untick the box on Batch Operations to stop.");
                }
                catch { /* the panel is going away */ }
            }
        }

        private void FinishFindingComments(FindingCommentsRun run, string stopped)
        {
            run.Timer.Tick -= OnFindingCommentsTick;
            run.Timer.Dispose();
            if (ReferenceEquals(_findingComments, run)) _findingComments = null;

            try
            {
                DiagnosticLog.Log("Proofread", $"Findings as comments: {run.Added} added, {run.AlreadyThere} already there, " +
                    $"{run.NotAdded} not added, of {run.Total}; {run.Slices} slices, {run.BusyMs} ms in all, " +
                    $"longest {run.LongestSliceMs} ms" + (stopped != null ? "; stopped: " + stopped : ""));
            }
            catch { }

            var summary = $"Added {run.Added} of {run.Total} finding{(run.Total == 1 ? "" : "s")} as Trados comments";
            if (run.AlreadyThere > 0)
                summary += $"; {run.AlreadyThere} {(run.AlreadyThere == 1 ? "segment already had" : "segments already had")} a comment from the proofreader";
            if (run.NotAdded > 0)
            {
                summary += $"; {run.NotAdded} could not be added ({string.Join("; ", run.Refusals)}" +
                           (run.NotAdded > run.Refusals.Count ? $"; and {run.NotAdded - run.Refusals.Count} more" : "") + ")";
            }
            if (stopped != null) summary += $"; stopped: {stopped}";
            if (run.Added > 0) summary += ". Save the document to keep them.";
            BatchLog(summary, run.NotAdded > 0 || stopped != null);

            // A run with findings ends on the Reports tab, so the outcome goes in
            // its footer too, in short - but only under the report it is about.
            if (ReferenceEquals(_currentReport, run.Report))
            {
                var footer = $"Added {run.Added} of {run.Total} as Trados comments";
                if (run.NotAdded > 0) footer += $", {run.NotAdded} could not be (see the Batch Operations log)";
                if (stopped != null) footer += $"; stopped: {stopped}";
                try { _control.Value.ReportsControl.ShowApplySummary(footer); } catch { /* the panel is going away */ }
            }
        }

        /// <summary>Studio is closing the panel: stop adding comments.</summary>
        private void StopFindingComments()
        {
            var run = _findingComments;
            if (run == null) return;
            _findingComments = null;
            try
            {
                run.Timer.Stop();
                run.Timer.Tick -= OnFindingCommentsTick;
                run.Timer.Dispose();
            }
            catch { }
        }

        /// <summary>
        /// Puts <paramref name="issue"/> on its segment as a Trados comment, unless
        /// the segment already has one from the proofreader. <paramref name="model"/> is the name of
        /// the model that made the report, for the label (AI Settings decides
        /// whether there is one). <paramref name="refusal"/> says why not, in
        /// words for the log, when nothing was added. Asks the segment for the
        /// source the finding was made on, not for the target: a fix applied since
        /// changes the target, and its comment belongs there all the same.
        /// </summary>
        private FindingCommentOutcome AddFindingComment(ProofreadingIssue issue, string model,
            Dictionary<string, ISegmentPair> index, out string refusal)
        {
            refusal = null;
            var text = ProofreadFindingComment.Text(issue, model,
                withLabel: _settings?.AiSettings?.ProofreadCommentLabel != false);
            if (text == null)
            {
                refusal = "the finding has no text.";
                return FindingCommentOutcome.NotAdded;
            }

            if (string.IsNullOrEmpty(issue.ParagraphUnitId) || string.IsNullOrEmpty(issue.SegmentId)
                || !index.TryGetValue(KeyOf(issue.ParagraphUnitId, issue.SegmentId), out var pair) || pair == null)
            {
                refusal = "the segment is not in the open document.";
                return FindingCommentOutcome.NotAdded;
            }

            // The segment the finding is about. In a document that merges several
            // files, two segments can share these ids; the one found must read as
            // the one that was checked.
            try
            {
                SegmentTagHandler.ToProofreadText(pair.Source, null, out var sourceNow, out _);
                if (!string.Equals(sourceNow, issue.SourceText, StringComparison.Ordinal))
                {
                    refusal = "the segment found under its id is not the one that was checked.";
                    return FindingCommentOutcome.NotAdded;
                }

                if (ProofreadFindingComment.AlreadyOn(EnumerateSegmentComments(pair).Select(c => c?.Text), text))
                    return FindingCommentOutcome.AlreadyThere;

                // Checked: Studio can throw after it has added the comment.
                Core.SegmentWriteCheck.AddCommentChecked(_activeDocument, pair, text, Sdl.FileTypeSupport.Framework.NativeApi.Severity.Medium);
                return FindingCommentOutcome.Added;
            }
            catch (Exception ex)
            {
                refusal = "Studio did not accept the comment (" + ex.Message + ").";
                return FindingCommentOutcome.NotAdded;
            }
        }

        private void BatchLog(string message, bool isError)
        {
            try { _control.Value.BatchTranslateControl.AppendLog(message, isError); } catch { /* the panel is going away */ }
        }
    }
}
