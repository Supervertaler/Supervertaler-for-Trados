using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Sdl.Core.Globalization;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Sdl.ProjectAutomation.FileBased;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Supervertaler.Trados.Controls;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Models;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados
{
    /// <summary>
    /// Proofread active segment in the Editor (Ctrl+Alt+P, right-click menu):
    /// the AI proofreads the one segment the cursor is in and a small window
    /// offers its correction, which Enter writes. The rules are in
    /// <see cref="Core.SegmentProofread"/>; the window is FuzzyFixer's
    /// (<see cref="FuzzyFixPreviewDialog"/>); the flow is FuzzyFixer's too
    /// (<see cref="HandleFuzzyFixActiveSegment"/>): refuse, read the segment on
    /// the UI thread, open the modal window, ask on a pool thread, write on the
    /// UI thread once the window has closed.
    ///
    /// <para>Like FuzzyFixer it needs nothing of the Supervertaler Assistant
    /// pane, and nothing is kept between uses. The window is modal, so one check
    /// at a time, always for the segment it was started on, and closing it
    /// cancels the request.</para>
    ///
    /// <para>It makes no proofreading report and touches none: not the Reports
    /// tab's findings, not the saved report. (Its request does get the card
    /// every AI request gets there.) The write is Apply's own
    /// (<see cref="WriteCorrectedTarget"/>).</para>
    ///
    /// <para><b>Races.</b> The Editor is behind the modal window, but the window
    /// pumps messages: an Apply all on the Reports tab keeps writing on its
    /// timer, and update_segments can arrive over MCP. Neither is blocked; the
    /// write checks that the segment still reads as it did when it was sent and
    /// refuses if not. A batch translation is refused up front, as FuzzyFixer
    /// refuses it.</para>
    ///
    /// <para><b>Scale.</b> One segment per use. Before the window opens, the
    /// document's segments are walked once on the UI thread to find the
    /// neighbours (<see cref="FuzzyFixNeighbours"/>: it stops three segments
    /// after the active one); the request runs on a pool thread. Tested on
    /// documents of a few segments; the walk is the only part that grows with
    /// the document, and FuzzyFixer's test times it at 20,000 segments.</para>
    /// </summary>
    public partial class AiAssistantViewPart
    {
        /// <summary>What one check needs, read on the UI thread before the window opens.</summary>
        private sealed class ProofreadSegmentJob
        {
            public AiSettings Ai;
            public string SourceLang, TargetLang;
            /// <summary>The segment as the proofreader is shown it
            /// (<see cref="SegmentTagHandler.ToProofreadText"/>): SourceText and
            /// ExistingTarget are also what the write compares the live segment with.</summary>
            public BatchSegment Segment;
            /// <summary>The translator's proofreading prompt, as a full run sends it (system prompt).</summary>
            public string Instructions;
            /// <summary>One segment, its terms and its neighbours (start of the user prompt).</summary>
            public string Lead;
            public Task<TranslationBankContext.Result> Bank;      // null: no SuperMemory context
        }

        public static void HandleProofreadActiveSegment()
        {
            try
            {
                var doc = Core.StudioEditor.Controller?.ActiveDocument;
                var pair = doc?.ActiveSegmentPair;
                var ai = SettingsService.Current?.AiSettings;

                var refusal = ProofreadSegmentRefusal(pair, _currentInstance?._batchTranslator != null, ai, out var icon);
                if (refusal != null)
                {
                    ProofreadSegmentSay(refusal, icon);
                    return;
                }

                var job = ProofreadSegmentPrepare(doc, pair, ai, out var problem);
                if (job == null)
                {
                    ProofreadSegmentSay(problem, MessageBoxIcon.Information);
                    return;
                }

                string correction = null;
                using (var dialog = new FuzzyFixPreviewDialog(
                    Core.SegmentProofread.Title, HelpSystem.Topics.AiProofreader,
                    SegmentLabelOf(doc, pair),
                    FuzzyFixIsConfirmed(pair) ? "This segment is already confirmed. Writing a correction sets it back to Draft." : null,
                    d => Task.Run(() => ProofreadSegmentRun(job, d, c => correction = c))))
                {
                    if (dialog.ShowDialog() != DialogResult.OK || correction == null) return;
                }

                // Back on the UI thread, the window closed.
                var why = ProofreadSegmentWrite(doc, pair, job, correction, out var note);
                if (why != null)
                    ProofreadSegmentSay("The correction was not written into " + SegmentLabelOf(doc, pair) + ": " + why,
                        MessageBoxIcon.Warning);
                else if (note != null)
                    ProofreadSegmentSay("The correction was written into " + SegmentLabelOf(doc, pair) + ", but " + note,
                        MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                ProofreadSegmentSay("Unexpected error: " + ex.Message, MessageBoxIcon.Error);
            }
        }

        private static void ProofreadSegmentSay(string message, MessageBoxIcon icon)
        {
            MessageBox.Show(message, Core.SegmentProofread.Title, MessageBoxButtons.OK, icon);
        }

        /// <summary>
        /// Why the check does not start, or null. Asked before anything is read
        /// from the document or sent, so a locked segment, an empty target, a
        /// running batch or a missing key is said at once. Changes nothing.
        /// </summary>
        private static string ProofreadSegmentRefusal(ISegmentPair pair, bool batchRunning, AiSettings ai, out MessageBoxIcon icon)
        {
            icon = MessageBoxIcon.Information;
            if (pair == null) return "No active segment.";
            if (pair.Properties?.IsLocked == true)
                return "This segment is locked, so it is not proofread: a correction could not be written. Unlock it first.";
            if (pair.Target == null || string.IsNullOrWhiteSpace(SegmentTagHandler.GetFinalText(pair.Target)))
                return "This segment has no translation yet, so there is nothing to proofread.";
            // A batch writes segments from the background while this window
            // would be open: two writers for one segment. Wait for it.
            if (batchRunning)
                return "A batch translation is running. Wait for it to finish, or stop it, and try again.";

            icon = MessageBoxIcon.Warning;
            return FuzzyFixAiProblem(ai);
        }

        /// <summary>
        /// Reads everything the check needs from Studio. UI thread. Null, with
        /// the reason, when there is nothing to work on.
        /// </summary>
        private static ProofreadSegmentJob ProofreadSegmentPrepare(IStudioDocument doc, ISegmentPair pair, AiSettings ai, out string problem)
        {
            problem = null;

            // Both sides as a full run sends them, tags as numbered placeholders,
            // the target's numbered as the source's: the correction comes back in
            // them, and the write compares the live segment with these strings.
            string sourceText, targetText;
            try
            {
                SegmentTagHandler.ToProofreadText(pair.Source, pair.Target, out sourceText, out targetText);
            }
            catch (Exception ex)
            {
                problem = "The active segment could not be read (" + ex.Message + ").";
                return null;
            }
            if (string.IsNullOrWhiteSpace(SegmentTagHandler.StripTagPlaceholders(sourceText)))
            {
                problem = "The active segment has no source text.";
                return null;
            }
            if (string.IsNullOrWhiteSpace(SegmentTagHandler.StripTagPlaceholders(targetText)))
            {
                problem = "This segment has no translation yet, so there is nothing to proofread.";
                return null;
            }

            string sourceLang = "", targetLang = "", projectPath = null, projectName = null;
            try
            {
                var file = doc.ActiveFile;
                sourceLang = file?.SourceFile?.Language?.DisplayName ?? "";
                targetLang = file?.Language?.DisplayName ?? "";
                var project = doc.Project as FileBasedProject;
                projectPath = project?.FilePath;
                projectName = project?.GetProjectInfo()?.Name;
            }
            catch { /* document in transition */ }
            if (string.IsNullOrEmpty(projectName)) projectName = null;
            if (string.IsNullOrEmpty(sourceLang) || string.IsNullOrEmpty(targetLang))
            {
                problem = "Supervertaler could not read this document's languages, so it cannot proofread the segment.";
                return null;
            }

            string puId = null, segId = null;
            try
            {
                segId = pair.Properties.Id.Id;
                puId = doc.GetParentParagraphUnit(pair).Properties.ParagraphUnitId.Id;
            }
            catch { /* the request does not need them */ }

            // The terms a full run would send for this segment, by the same
            // routines; they travel in the user prompt (SegmentProofread.Lead).
            TermLensEditorViewPart.PrewarmFallbackTermsFor(new[] { sourceText });
            var terms = TermLensEditorViewPart.GetCurrentTermbaseTerms()
                .Where(t => ai.IsTermbaseAiEnabled(t.TermbaseId)).ToList();
            terms = TermsForPrompt(terms, new[] { sourceText }, null);

            FuzzyFixNeighbours(doc, pair, out var before, out var after);

            var bank = KbContextForTranslation(doc, ProjectBankNameFor(projectPath, projectName),
                projectName, sourceLang, targetLang);

            // The proofreading prompt the Batch tab shows for this project.
            string customPrompt = null;
            try
            {
                Settings.ProjectSettings ps = null;
                if (!string.IsNullOrEmpty(projectPath))
                {
                    try { ps = Settings.ProjectSettings.Load(projectPath); }
                    catch { }
                }
                BatchPromptChoice.Stored(ai, ps, true, out var storedPrompt, out _);
                customPrompt = PromptContentFor(
                    TermLensEditorViewPart.GetPromptLibrary() ?? new PromptLibrary(),
                    storedPrompt, true, sourceLang, targetLang);
            }
            catch (Exception ex)
            {
                BridgeLog.Write("Proofread active segment: the prompt could not be read, so the built-in instructions go alone: " + ex.Message);
            }

            int number = Core.SegmentProofread.NumberFor(StudioNumberOf(pair, 0));
            return new ProofreadSegmentJob
            {
                // The Translation model, as a full proofreading run: not a Light task.
                Ai = ai,
                SourceLang = sourceLang,
                TargetLang = targetLang,
                Segment = new BatchSegment
                {
                    Index = number - 1,   // BatchProofreader sends and matches Index + 1
                    Number = StudioNumberOf(pair, 0),
                    SourceText = sourceText,
                    ExistingTarget = targetText,
                    SegmentPairRef = new[] { puId, segId }
                },
                Instructions = customPrompt,
                Lead = Core.SegmentProofread.Lead(number,
                    ProofreadingPrompt.TermbaseBlock(terms, ai.IncludeTermMetadata),
                    Core.FuzzyFixer.NeighbourLines(before, after)),
                Bank = bank
            };
        }

        /// <summary>
        /// The request, on a pool thread. Reports to the window and hands a
        /// correction that can be written to <paramref name="done"/>; touches no
        /// Studio object. Never throws: every way out ends in the window showing
        /// a result or a reason, or in the window being closed already.
        /// </summary>
        private static async Task ProofreadSegmentRun(ProofreadSegmentJob job, FuzzyFixPreviewDialog dialog, Action<string> done)
        {
            try
            {
                var ct = dialog.Token;
                // The model by the name the settings show it under, the id when it has none.
                var model = job.Ai.SelectedProvider == LlmModels.ProviderCustomOpenAi
                    ? job.Ai.GetActiveCustomProfile()?.Model
                    : job.Ai.GetSelectedModel();
                model = LlmModels.FindModel(model)?.DisplayName ?? model;
                bool named = !string.IsNullOrWhiteSpace(model);
                dialog.ShowStatus("Asking the AI" + (named ? " (" + model + ")" : "")
                    + " to proofread this segment… This is its translation:",
                    Core.FuzzyFixer.Plain(job.Segment.ExistingTarget));

                string bankBlock = null;
                if (job.Bank != null)
                    bankBlock = await AwaitKbForTranslation(job.Bank, ct, lines =>
                    {
                        foreach (var line in lines) BridgeLog.Write("Proofread active segment: " + line.Trim());
                    }, alwaysReport: false).ConfigureAwait(false);

                ProofreadingIssue verdict = null;
                bool noVerdict = false;
                string lastProblem = null;
                var worker = new BatchProofreader();
                worker.SegmentProofread += (s, e) => { verdict = e.Issue; noVerdict = e.NoVerdict; };
                // The proofreader reports what goes wrong - a refused key, an
                // overloaded server - as progress, not as exceptions.
                worker.Progress += (s, e) =>
                {
                    if (e.IsError && !string.IsNullOrWhiteSpace(e.Message)) lastProblem = e.Message.Trim().TrimStart('✗', ' ');
                };

                // No document and no terms in the system prompt: with the
                // neighbours they open the user prompt (job.Lead), so the system
                // prompt is the same bytes from one segment to the next and the
                // bank is served from the provider's cache.
                await worker.ProofreadAsync(
                    new List<BatchSegment> { job.Segment }, job.SourceLang, job.TargetLang,
                    job.Ai, new List<TermEntry>(), 1, ct,
                    job.Instructions,
                    documentSegments: null, kbContext: bankBlock, targetOnly: false,
                    requestLead: job.Lead).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                var by = named ? " by " + model : "";
                var outcome = Core.SegmentProofread.Judge(verdict, noVerdict, out var whyNot);
                switch (outcome)
                {
                    case Core.SegmentProofread.Outcome.NoVerdict:
                        dialog.ShowProblem(verdict == null
                            ? "The AI gave no verdict for this segment." + (lastProblem != null ? "\n\n" + lastProblem : "")
                            : "No verdict could be read in the AI's reply, so nothing is known about this segment.\n\n"
                              + "Try again, or try another model.");
                        break;
                    case Core.SegmentProofread.Outcome.Clean:
                        dialog.ShowReview("No issues found in this segment.",
                            "Checked" + by + " on its own, with the segments just before and after it as context. "
                            + "It was not compared with the rest of the document: for that, proofread from the Batch tab.",
                            null);
                        break;
                    case Core.SegmentProofread.Outcome.Writable:
                        done(verdict.CorrectedTarget);
                        dialog.ShowReview("Issue found" + by + ". Enter writes the correction as a draft; Escape discards it.",
                            Core.SegmentProofread.WritableText(verdict),
                            Core.FuzzyFixer.Changes(verdict.TargetText, verdict.CorrectedTarget));
                        break;
                    default:
                        dialog.ShowReview("Issue found" + by + ". Nothing can be written: the reason is below.",
                            Core.SegmentProofread.NotWritableText(verdict, whyNot), null);
                        break;
                }
                BridgeLog.Write("Proofread active segment: " + outcome + ".");
            }
            catch (OperationCanceledException)
            {
                BridgeLog.Write("Proofread active segment: cancelled.");
            }
            catch (Exception ex)
            {
                BridgeLog.Write("Proofread active segment failed: " + ex.Message);
                dialog.ShowProblem("Proofread active segment failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Writes the correction into its segment, the way Apply on the Reports
        /// tab does (<see cref="WriteCorrectedTarget"/>): refused when the
        /// segment no longer reads as it did when it was sent, the target's own
        /// characters and tags kept, word by word with Track Changes on, Draft
        /// when the text changed. Null when it was written, otherwise why not,
        /// and nothing has been changed; <paramref name="note"/> when it was
        /// written but something needs a look. Never throws. UI thread.
        /// </summary>
        private static string ProofreadSegmentWrite(IStudioDocument doc, ISegmentPair pair, ProofreadSegmentJob job,
            string correction, out string note)
        {
            note = null;
            try
            {
                var source = job.Segment.SourceText;
                var target = job.Segment.ExistingTarget;
                // Checked before it was offered; asked again here because it costs nothing.
                var problem = ProofreadCorrection.Problem(source, target, correction);
                if (problem != null) return problem;

                var refusal = WriteCorrectedTarget(doc, pair, source, target, correction, out note);
                if (refusal != null) return refusal;

                // run_verification reads the saved files; where the pane follows
                // this document, say it has a write of ours not saved yet, as Apply does.
                var pane = _currentInstance;
                if (pane != null && ReferenceEquals(pane._activeDocument, doc))
                    pane._bridgeUnsavedWritesDoc = doc;
                return null;
            }
            catch (Exception ex)
            {
                BridgeLog.Write("Proofread active segment write error: " + ex.Message);
                note = null;
                return ex.Message + " The target may not have changed.";
            }
        }
    }
}
