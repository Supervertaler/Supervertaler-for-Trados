using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Supervertaler.Trados.Controls;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados
{
    /// <summary>
    /// Apply on the Reports tab: writes a proofreader's corrected translation
    /// into its segment, one card at a time or every card not ticked off (Apply
    /// all). Decided by Michael on 15 Oct 2026, from Sascha's report that a
    /// proofread's findings could only be retyped by hand:
    ///
    /// <list type="bullet">
    /// <item>tracked changes only when Track Changes is on in the document, then
    /// word by word, through the same write as update_segments
    /// (<see cref="WriteSegmentTarget"/>); a plain edit when it is off;</item>
    /// <item>a fixed segment is set to Draft;</item>
    /// <item>with "Also add issues as Trados comments" ticked, the finding goes on
    /// the segment as a comment too (<see cref="AddFindingComment"/>);</item>
    /// <item>a segment whose source or target no longer reads as it did when it
    /// was checked is refused, and its card says so. So is a correction whose
    /// tags are not the source's.</item>
    /// </list>
    ///
    /// Everything here runs on the UI thread, which is the only thread Studio
    /// lets a segment be written from.
    /// </summary>
    public partial class AiAssistantViewPart
    {
        /// <summary>The Apply all in progress, or null. UI thread only.</summary>
        private ApplyAllRun _applyAll;

        /// <summary>
        /// One Apply all. It writes in slices of about <see cref="ApplySliceMs"/>
        /// on a WinForms timer, so Studio paints and takes input between slices.
        /// A timer, not BeginInvoke and not DoEvents: a chain of posted messages
        /// is served ahead of input and paint and would freeze the window just
        /// the same, and DoEvents would let a click re-enter the loop mid-write.
        /// </summary>
        private sealed class ApplyAllRun
        {
            public ProofreadingReport Report;
            public IStudioDocument Document;
            public Dictionary<string, ISegmentPair> Index;
            public Queue<ProofreadingIssue> Pending;
            public Timer Timer;
            public int Total, Done, Applied, NotApplied;
            public bool StopRequested;
        }

        private const int ApplySliceMs = 80;

        // ─── Apply (one card) ─────────────────────────────────────

        private void OnApplyCorrectionRequested(object sender, ProofreadIssueEventArgs e)
        {
            var issue = e?.Issue;
            if (issue == null || _applyAll != null) return;   // the cards' buttons are off during Apply all
            var reports = _control.Value.ReportsControl;

            var refusal = WhyReportCannotBeApplied(_currentReport);
            if (refusal == null && !_currentReport.Issues.Contains(issue))
                refusal = "this card is from an earlier report.";

            string note = null;
            if (refusal == null)
                refusal = ApplyCorrection(issue, BuildSegmentPairIndex(null), out note);

            ReportOutcome(reports, issue, refusal, note);
            if (refusal == null)
                ProofreadingReportWriter.TrySaveState(_currentReport);
        }

        // ─── Apply all ─────────────────────────────────────────────

        private void OnApplyAllCorrectionsRequested(object sender, EventArgs e)
        {
            // While a run is going the button reads Stop.
            if (_applyAll != null)
            {
                _applyAll.StopRequested = true;
                return;
            }

            var report = _currentReport;
            var form = _control.Value.FindForm();
            var refusal = WhyReportCannotBeApplied(report);
            if (refusal != null)
            {
                MessageBox.Show(form, "Nothing was applied: " + refusal, "Supervertaler \u2013 Apply all",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Every finding not ticked off - the tick box is the filter - that has
            // a correction the card offers to apply.
            var open = report.Issues.Where(i => i != null && !i.IsOk && !i.Dismissed && !i.Applied).ToList();
            var pending = open.Where(i => i.CorrectedTarget != null
                && ProofreadCorrection.Problem(i.SourceText, i.TargetText, i.CorrectedTarget) == null).ToList();
            int skipped = open.Count - pending.Count;

            if (pending.Count == 0)
            {
                MessageBox.Show(form, "None of the open findings has a correction that can be applied.",
                    "Supervertaler \u2013 Apply all", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            bool tracking = TrackingSuspension.IsTracking(_activeDocument) == true;
            var question =
                $"Write the AI's correction into {pending.Count} segment{(pending.Count == 1 ? "" : "s")}?\n\n" +
                "Each segment that changes is set to Draft. " +
                (tracking
                    ? "Track Changes is on, so each fix is recorded as tracked changes."
                    : "Track Changes is off, so the fixes are written as plain edits.") +
                (_control.Value.BatchTranslateControl.AddAsComments
                    ? " Each fix also gets its finding as a Trados comment (\"Also add issues as Trados comments\" is ticked on Batch Operations)."
                    : "") +
                "\n\nA segment that has changed since it was checked is left alone, and its card says so." +
                (skipped > 0
                    ? $"\n\n{skipped} open finding{(skipped == 1 ? " has" : "s have")} no correction that can be applied and {(skipped == 1 ? "is" : "are")} skipped."
                    : "");
            if (MessageBox.Show(form, question, "Supervertaler \u2013 Apply all",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;

            // The dialog pumped messages: check again that nothing moved under it.
            refusal = WhyReportCannotBeApplied(report);
            if (refusal != null || !ReferenceEquals(report, _currentReport))
                return;

            var run = new ApplyAllRun
            {
                Report = report,
                Document = _activeDocument,
                Index = BuildSegmentPairIndex(null),   // once per run: it walks the whole document
                Pending = new Queue<ProofreadingIssue>(pending),
                Total = pending.Count,
                Timer = new Timer { Interval = 1 }
            };
            run.Timer.Tick += OnApplyAllTick;
            _applyAll = run;

            var reports = _control.Value.ReportsControl;
            reports.ShowApplySummary(null);
            reports.SetApplying(true, 0, run.Total);
            run.Timer.Start();
        }

        private void OnApplyAllTick(object sender, EventArgs e)
        {
            var run = _applyAll;
            if (run == null || !ReferenceEquals(sender, run.Timer))
            {
                (sender as Timer)?.Stop();
                return;
            }

            // No tick while this one works: a write that shows a dialog would pump
            // the timer and run a second slice inside the first.
            run.Timer.Stop();

            string stopped = null;
            try
            {
                stopped = WhyApplyAllStops(run);
                if (stopped == null)
                    ApplySlice(run);
            }
            catch (Exception ex)
            {
                // Whatever broke, the run ends cleanly: the button must not be
                // left reading Stop over a timer that will never tick again.
                stopped = "an error (" + ex.Message + ")";
                try { DiagnosticLog.Log("Reports", "Apply all failed: " + ex); } catch { }
            }

            if (stopped != null || run.Pending.Count == 0)
                FinishApplyAll(run, stopped);
            else
                run.Timer.Start();
        }

        /// <summary>Why Apply all must stop before the next slice, or null.
        /// Worded to follow "stopped: ".</summary>
        private string WhyApplyAllStops(ApplyAllRun run)
        {
            if (run.StopRequested) return "you clicked Stop";
            if (!ReferenceEquals(_activeDocument, run.Document)) return "the document in the editor changed";
            if (!ReferenceEquals(_currentReport, run.Report)) return "the report was cleared or replaced";
            if (_batchProofreader != null) return "a proofreading run started";
            return null;
        }

        /// <summary>Applies corrections for about <see cref="ApplySliceMs"/>, then
        /// shows what happened to them in one go.</summary>
        private void ApplySlice(ApplyAllRun run)
        {
            var reports = _control.Value.ReportsControl;
            var done = new List<ProofreadingIssue>();
            var notes = new List<KeyValuePair<ProofreadingIssue, string>>();
            var appliedWithNote = new HashSet<ProofreadingIssue>();

            var sw = Stopwatch.StartNew();
            while (run.Pending.Count > 0 && sw.ElapsedMilliseconds < ApplySliceMs)
            {
                var issue = run.Pending.Dequeue();
                run.Done++;

                // Ticked off, or applied from its own card, since the run began.
                if (issue.Dismissed || issue.Applied) continue;

                var refusal = ApplyCorrection(issue, run.Index, out var note);
                if (refusal != null)
                {
                    run.NotApplied++;
                    notes.Add(new KeyValuePair<ProofreadingIssue, string>(issue, "Not applied: " + refusal));
                }
                else
                {
                    run.Applied++;
                    if (note == null)
                    {
                        done.Add(issue);
                    }
                    else
                    {
                        appliedWithNote.Add(issue);
                        notes.Add(new KeyValuePair<ProofreadingIssue, string>(issue, "Applied, but " + note));
                    }
                }
            }

            reports.ShowApplyOutcome(done, notes, appliedWithNote);
            reports.SetApplying(true, run.Done, run.Total);
        }

        private void FinishApplyAll(ApplyAllRun run, string stopped)
        {
            run.Timer.Tick -= OnApplyAllTick;
            run.Timer.Dispose();
            if (ReferenceEquals(_applyAll, run)) _applyAll = null;

            // Saved once, at the end, not once per segment. Not when the report has
            // gone: Clear deleted its file, and saving would bring it back.
            if (ReferenceEquals(_currentReport, run.Report))
                ProofreadingReportWriter.TrySaveState(run.Report);

            try
            {
                var reports = _control.Value.ReportsControl;
                reports.SetApplying(false);
                var summary = $"Applied {run.Applied} of {run.Total}";
                if (run.NotApplied > 0) summary += $", {run.NotApplied} not applied (see the cards)";
                if (stopped != null) summary += $"; stopped: {stopped}";
                reports.ShowApplySummary(summary);
            }
            catch { /* the panel is going away */ }
        }

        /// <summary>Studio is closing the panel: stop writing.</summary>
        private void StopApplyAll()
        {
            var run = _applyAll;
            if (run == null) return;
            _applyAll = null;
            try
            {
                run.Timer.Stop();
                run.Timer.Tick -= OnApplyAllTick;
                run.Timer.Dispose();
            }
            catch { }
            if (ReferenceEquals(_currentReport, run.Report))
                ProofreadingReportWriter.TrySaveState(run.Report);
        }

        // ─── The write ─────────────────────────────────────────────

        /// <summary>Why nothing in <paramref name="report"/> can be applied right
        /// now, or null. Asked before every Apply, and before Apply all starts.</summary>
        private string WhyReportCannotBeApplied(ProofreadingReport report)
        {
            if (report == null) return "there is no proofreading report.";
            if (_activeDocument == null) return "no document is open in the editor.";
            if (_batchProofreader != null) return "a proofreading run is in progress. Apply when it has finished.";
            if (!ReportIsForOpenDocument(report))
                return "this report is for " + (report.DocumentName ?? "another document") + ". Open that document to apply it.";
            return null;
        }

        /// <summary>
        /// True when the report was made on a file of the document open in the
        /// editor. Any of its files, not only the active one: in a document that
        /// merges several files, the active file follows the cursor - and clicking
        /// a card moves the cursor.
        /// </summary>
        private bool ReportIsForOpenDocument(ProofreadingReport report)
        {
            try
            {
                foreach (var f in _activeDocument.Files)
                    if (f != null && ProofreadingReportWriter.BelongsTo(report, f.LocalFilePath, f.Name))
                        return true;
            }
            catch { /* fall back to the active file */ }
            return ProofreadingReportWriter.BelongsTo(report, GetActiveFilePathSafe(), GetActiveFileNameSafe());
        }

        /// <summary>
        /// Writes <paramref name="issue"/>'s correction into its segment. Returns
        /// null when it was written, otherwise why not - in words for its card, and
        /// nothing has been changed. <paramref name="note"/> is set when it was
        /// written but something needs a look.
        /// </summary>
        private string ApplyCorrection(ProofreadingIssue issue, Dictionary<string, ISegmentPair> index, out string note)
        {
            note = null;
            if (issue.CorrectedTarget == null) return "there is no correction to apply.";

            var problem = ProofreadCorrection.Problem(issue.SourceText, issue.TargetText, issue.CorrectedTarget);
            if (problem != null) return problem;

            if (string.IsNullOrEmpty(issue.ParagraphUnitId) || string.IsNullOrEmpty(issue.SegmentId)
                || !index.TryGetValue(KeyOf(issue.ParagraphUnitId, issue.SegmentId), out var pair) || pair == null)
                return "the segment is not in the open document.";

            if (pair.Properties?.IsLocked == true) return "the segment is locked.";

            // Has the segment changed since it was checked? Compared as the
            // proofreader was shown it - tags included - so the correction is
            // written over exactly the text it was made from. This is also what
            // stops a pair found under the same ids in another file of a merged
            // document from being written: it does not read the same.
            string sourceNow, targetNow;
            try
            {
                SegmentTagHandler.ToProofreadText(pair.Source, pair.Target, out sourceNow, out targetNow);
            }
            catch (Exception ex)
            {
                return "the segment could not be read (" + ex.Message + ").";
            }
            if (!string.Equals(sourceNow, issue.SourceText, StringComparison.Ordinal)
                || !string.Equals(targetNow, issue.TargetText, StringComparison.Ordinal))
                return "the segment has changed since it was checked. Proofread it again, or fix it by hand.";

            // The target's non-breaking spaces where the correction left the text
            // alone. Normalise already does this for a new report; done again here
            // for a report saved by an earlier build. Idempotent.
            var text = ProofreadCorrection.KeepTargetCharacters(issue.TargetText, issue.CorrectedTarget);

            // How the target's tags differ from the source's before the write
            // (null: they match). A difference that is still exactly this one
            // afterwards was there already and is not this write's doing (#141).
            var tagsBefore = DescribeTagIdMismatch(pair.Source, pair.Target);

            SegmentTargetWrite written;
            try
            {
                written = WriteSegmentTarget(pair, text, "Supervertaler", semanticTagNames: false,
                    keepTargetTags: true);
            }
            catch (Exception ex)
            {
                return "Studio did not accept the change (" + ex.Message + ").";
            }

            // A fixed segment is set to Draft: someone has to look at it again.
            // Not when the write left the text as it was (a change the accepted
            // view does not show), as update_segments does.
            if (!written.UnchangedText)
            {
                try
                {
                    pair.Properties.ConfirmationLevel = Sdl.Core.Globalization.ConfirmationLevel.Draft;
                    _activeDocument.UpdateSegmentPairProperties(pair, pair.Properties);
                }
                catch (Exception ex)
                {
                    note = "its status could not be set to Draft (" + ex.Message + ").";
                }
            }

            // The tags were checked before the write, so a difference from the
            // source that this write made should never be seen; if it is,
            // Studio's Tag Verifier will flag the segment and the card must not
            // vanish as if all were well. One the target already had, and still
            // has unchanged, is the translator's and stays theirs: the Tag
            // Verifier flagged it before and still does.
            if (written.TagWarning != null && !string.Equals(written.TagWarning, tagsBefore, StringComparison.Ordinal))
                note = (note == null ? "" : note + " ") + written.TagWarning;

            // With "Also add issues as Trados comments" ticked, the finding goes on
            // the segment with its fix, so a reviser accepting or rejecting the
            // tracked change can see why it was made (Michael, 17 Oct 2026). After
            // the write, never before: a comment added first would be written over.
            // One added after the run is still there - the write keeps comments -
            // and is not added twice.
            if (_control.Value.BatchTranslateControl.AddAsComments
                && AddFindingComment(issue, _currentReport?.Model, index, out var commentRefusal) == FindingCommentOutcome.NotAdded)
                note = (note == null ? "" : note + " ") + "the finding could not be added as a comment: " + commentRefusal;

            issue.Applied = true;
            // Dealt with, like a ticked-off card - unless there is something to check.
            if (note == null) issue.Dismissed = true;

            // run_verification reads the saved files; say the document has writes
            // of ours not saved yet, as update_segments does.
            _bridgeUnsavedWritesDoc = _activeDocument;
            return null;
        }

        private static void ReportOutcome(ReportsControl reports, ProofreadingIssue issue, string refusal, string note)
        {
            if (refusal != null)
            {
                reports.ShowApplyOutcome(null,
                    new[] { new KeyValuePair<ProofreadingIssue, string>(issue, "Not applied: " + refusal) });
            }
            else if (note != null)
            {
                reports.ShowApplyOutcome(null,
                    new[] { new KeyValuePair<ProofreadingIssue, string>(issue, "Applied, but " + note) },
                    new HashSet<ProofreadingIssue> { issue });
            }
            else
            {
                reports.ShowApplyOutcome(new[] { issue }, null);
            }
        }
    }
}
