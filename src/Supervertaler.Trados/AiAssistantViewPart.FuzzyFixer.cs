using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Sdl.Core.Globalization;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Sdl.ProjectAutomation.FileBased;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Supervertaler.Trados.Controls;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados
{
    /// <summary>
    /// FuzzyFixer in the Editor (Ctrl+Alt+F, right-click menu): repair the best
    /// fuzzy translation memory match for the active segment with the AI, show
    /// what changed, and write it on Enter. The rules are in
    /// <see cref="Core.FuzzyFixer"/>; the window is
    /// <see cref="FuzzyFixPreviewDialog"/>.
    ///
    /// <para>Like Alt+T without the pane it needs nothing of the Supervertaler
    /// Assistant pane: document from the EditorController, settings, prompt and
    /// memory bank from disk, the same routines.</para>
    ///
    /// <para>Nothing is kept between uses. The window is modal, so one repair at
    /// a time by construction, always for the segment it was started on, and
    /// closing it cancels the search and the request.</para>
    ///
    /// <para><b>Scale.</b> One segment per use. Before the window opens, the
    /// document's segments are walked once on the UI thread to find the
    /// neighbours (it stops three segments after the active one), as Alt+T walks
    /// them for its document context; the memory search and the request run on a
    /// pool thread. Tested on documents of a few segments; the walk is the only
    /// part that grows with the document.</para>
    /// </summary>
    public partial class AiAssistantViewPart
    {
        /// <summary>What one repair needs, read on the UI thread before the window opens.</summary>
        private sealed class FuzzyFixJob
        {
            public AiSettings Ai;
            public string SourceLang, TargetLang;
            public BatchSegment Segment;
            public TmLookupPlan Lookup;
            public List<TermEntry> Terms;
            /// <summary>The translator's prompt, as Alt+T sends it (system prompt).</summary>
            public string Instructions;
            /// <summary>The repair instruction and the surrounding segments (start of the user prompt).</summary>
            public string Lead;
            public StructureContextMode Structure;
            public Task<TranslationBankContext.Result> Bank;      // null: no SuperMemory context
        }

        public static void HandleFuzzyFixActiveSegment()
        {
            try
            {
                var doc = Core.StudioEditor.Controller?.ActiveDocument;
                var pair = doc?.ActiveSegmentPair;
                var ai = SettingsService.Current?.AiSettings;

                var refusal = FuzzyFixRefusal(pair, _currentInstance?._batchTranslator != null, ai, out var icon);
                if (refusal != null)
                {
                    FuzzyFixSay(refusal, icon);
                    return;
                }

                var job = FuzzyFixPrepare(doc, pair, ai, out var problem);
                if (job == null)
                {
                    FuzzyFixSay(problem, MessageBoxIcon.Information);
                    return;
                }

                bool confirmed = FuzzyFixIsConfirmed(pair);
                BatchSegmentResultEventArgs repair = null;
                using (var dialog = new FuzzyFixPreviewDialog(
                    SegmentLabelOf(doc, pair),
                    confirmed ? "This segment is already confirmed. Writing the repair replaces its translation." : null,
                    d => Task.Run(() => FuzzyFixRun(job, d, r => repair = r))))
                {
                    if (dialog.ShowDialog() != DialogResult.OK || repair == null) return;
                }

                // Back on the UI thread, the window closed, the same segment still
                // active (the window was modal).
                if (!FuzzyFixWrite(doc, pair, repair, out var why, out var note))
                    FuzzyFixSay("The repaired translation was not written into " + SegmentLabelOf(doc, pair)
                        + (why != null ? ": " + why : ". The target is as it was."), MessageBoxIcon.Warning);
                else if (note != null)
                    FuzzyFixSay(note, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                FuzzyFixSay("Unexpected error: " + ex.Message, MessageBoxIcon.Error);
            }
        }

        private static void FuzzyFixSay(string message, MessageBoxIcon icon)
        {
            MessageBox.Show(message, Core.FuzzyFixer.Title, MessageBoxButtons.OK, icon);
        }

        private static bool FuzzyFixIsConfirmed(ISegmentPair pair)
        {
            var level = pair?.Properties?.ConfirmationLevel ?? ConfirmationLevel.Unspecified;
            return level != ConfirmationLevel.Unspecified && level != ConfirmationLevel.Draft;
        }

        /// <summary>
        /// Why FuzzyFixer does not start, or null. Checked before anything is
        /// searched or read, so a locked segment, a running batch or a missing key
        /// is said at once. Changes nothing.
        /// </summary>
        private static string FuzzyFixRefusal(ISegmentPair pair, bool batchRunning, AiSettings ai, out MessageBoxIcon icon)
        {
            icon = MessageBoxIcon.Information;
            if (pair == null) return "No active segment.";
            if (pair.Properties?.IsLocked == true)
                return "This segment is locked, so FuzzyFixer leaves it alone. Unlock it first.";
            // A batch writes segments from the background while this window
            // would be open: two writers for one segment. Wait for it.
            if (batchRunning)
                return "A batch translation is running. Wait for it to finish, or stop it, and try again.";

            icon = MessageBoxIcon.Warning;
            return FuzzyFixAiProblem(ai);
        }

        /// <summary>Why no AI request can be made with these settings, or null. Alt+T's own check.</summary>
        private static string FuzzyFixAiProblem(AiSettings ai)
        {
            if (ai == null)
                return "AI settings not configured.\n\nOpen Settings → AI Settings to configure a provider.";

            var provider = ai.SelectedProvider ?? LlmModels.ProviderOpenAi;
            if (provider == LlmModels.ProviderOllama) return null;

            string key, keyProblem;
            if (provider == LlmModels.ProviderCustomOpenAi)
            {
                var profile = ai.GetActiveCustomProfile();
                if (profile == null) return "No custom OpenAI profile configured.";
                key = TradosApiKeys.For(profile, out keyProblem);
            }
            else
            {
                key = TradosApiKeys.For(provider, ai, out keyProblem);
            }
            if (!string.IsNullOrEmpty(key)) return null;
            return string.IsNullOrWhiteSpace(keyProblem) ? "No API key is set for the selected AI provider." : keyProblem;
        }

        /// <summary>
        /// The memories to search: those the project looks things up in for this
        /// language pair (<see cref="Core.TmSearcher.FindLookupTms(string,string,string)"/>,
        /// as Batch Translate), EverTM among them. A memory that is switched off
        /// or has Lookup unticked is not searched. Null, with the reason, when
        /// there is none. Unlike Batch Translate's plan this does not depend on
        /// "Send TM matches": the match is what FuzzyFixer is for.
        /// </summary>
        private static TmLookupPlan FuzzyFixLookup(string anchor, System.Globalization.CultureInfo sourceCulture,
            string targetCulture, int minScore, out string problem)
        {
            problem = null;
            var lookup = Core.TmSearcher.FindLookupTms(anchor, sourceCulture.Name, targetCulture);   // never throws
            bool everTm = targetCulture != null && lookup.EverTm;
            if (lookup.Tms.Count == 0 && !everTm)
            {
                problem = Core.FuzzyFixer.NoMemoryProblem(lookup.Total);
                return null;
            }
            return new TmLookupPlan
            {
                Tms = lookup.Tms, SourceCulture = sourceCulture, MinScore = minScore, EverTm = everTm,
                TargetCulture = targetCulture,
                SkippedNote = Core.TmSearcher.SkippedTmsSentence(lookup.Searched, lookup.Total)
            };
        }

        /// <summary>
        /// The <see cref="Core.FuzzyFixer.NeighbourCount"/> segments before and
        /// after <paramref name="pair"/> in the document, in document order, with
        /// their translations where they have one. Walks the document once and
        /// stops after the last neighbour. Empty lists when the segment is not
        /// found or the document cannot be read: a repair without neighbours is
        /// still a repair. UI thread.
        /// </summary>
        private static void FuzzyFixNeighbours(IStudioDocument doc, ISegmentPair pair,
            out List<Core.FuzzyFixer.Neighbour> before, out List<Core.FuzzyFixer.Neighbour> after)
        {
            before = new List<Core.FuzzyFixer.Neighbour>();
            after = new List<Core.FuzzyFixer.Neighbour>();
            try
            {
                string segId = pair.Properties.Id.Id;
                string puId = doc.GetParentParagraphUnit(pair).Properties.ParagraphUnitId.Id;

                bool found = false;
                int index = 0;
                foreach (var p in doc.SegmentPairs)
                {
                    int at = index++;
                    // The id first: it is the cheap half, and repeats only once per file.
                    if (!found && p.Properties.Id.Id == segId
                        && doc.GetParentParagraphUnit(p).Properties.ParagraphUnitId.Id == puId)
                    {
                        found = true;
                        continue;
                    }

                    var n = new Core.FuzzyFixer.Neighbour
                    {
                        Number = StudioNumberOf(p, at),
                        Source = SegmentTagHandler.ToModelText(p.Source),
                        Target = p.Target != null ? SegmentTagHandler.GetFinalText(p.Target) : ""
                    };
                    if (!found)
                    {
                        before.Add(n);
                        if (before.Count > Core.FuzzyFixer.NeighbourCount) before.RemoveAt(0);
                    }
                    else
                    {
                        after.Add(n);
                        if (after.Count >= Core.FuzzyFixer.NeighbourCount) break;
                    }
                }
                if (!found) before.Clear();
            }
            catch
            {
                // Document in transition: go without.
                before.Clear();
                after.Clear();
            }
        }

        /// <summary>
        /// Reads everything the repair needs from Studio. UI thread. Null, with
        /// the reason, when there is nothing to work on.
        /// </summary>
        private static FuzzyFixJob FuzzyFixPrepare(IStudioDocument doc, ISegmentPair pair, AiSettings ai, out string problem)
        {
            problem = null;

            var serialization = SegmentTagHandler.Serialize(pair.Source);
            var hasTags = serialization.HasTags;
            var sourceText = hasTags ? serialization.SerializedText : (pair.Source?.ToString() ?? "");
            if (string.IsNullOrWhiteSpace(SegmentTagHandler.StripTagPlaceholders(sourceText)))
            {
                problem = "The active segment has no source text.";
                return null;
            }

            string sourceLang = "", targetLang = "", targetCulture = null, projectPath = null, projectName = null, anchor = null;
            System.Globalization.CultureInfo sourceCulture = null;
            try
            {
                var file = doc.ActiveFile;
                sourceLang = file?.SourceFile?.Language?.DisplayName ?? "";
                targetLang = file?.Language?.DisplayName ?? "";
                sourceCulture = file?.SourceFile?.Language?.CultureInfo;
                targetCulture = file?.Language?.CultureInfo?.Name;
                anchor = file?.LocalFilePath;
                var project = doc.Project as FileBasedProject;
                projectPath = project?.FilePath;
                projectName = project?.GetProjectInfo()?.Name;
            }
            catch { /* document in transition */ }
            if (!string.IsNullOrEmpty(projectPath)) anchor = projectPath;
            if (string.IsNullOrEmpty(projectName)) projectName = null;
            if (sourceCulture == null || string.IsNullOrEmpty(sourceLang) || string.IsNullOrEmpty(targetLang))
            {
                problem = "Supervertaler could not read this document's languages, so it cannot search the translation memories.";
                return null;
            }

            int minScore = Core.FuzzyFixer.MinScore(ai.TmFuzzyMinScore);
            var lookup = FuzzyFixLookup(anchor, sourceCulture, targetCulture, minScore, out problem);
            if (lookup == null) return null;
            if (lookup.SkippedNote != null) BridgeLog.Write("FuzzyFixer: " + lookup.SkippedNote);

            var segments = new List<BatchSegment>
            {
                new BatchSegment
                {
                    Index = 0,
                    Number = StudioNumberOf(pair, 0),
                    SourceText = sourceText,
                    ExistingTarget = pair.Target != null ? SegmentTagHandler.GetFinalText(pair.Target) : "",
                    SegmentPairRef = pair,
                    HasTags = hasTags,
                    TagMap = hasTags ? serialization.TagMap : null
                }
            };

            // From here on, what Alt+T sends (TranslateActiveSegmentStandalone),
            // but for the document: the neighbours instead.
            var structure = ApplyStructureMarkers(doc, segments, ai, out var structureNote);
            if (structureNote != null) BridgeLog.Write(structureNote);

            TermLensEditorViewPart.PrewarmFallbackTermsFor(new[] { sourceText });
            var terms = TermLensEditorViewPart.GetCurrentTermbaseTerms()
                .Where(t => ai.IsTermbaseAiEnabled(t.TermbaseId)).ToList();
            terms = TermsForPrompt(terms, new[] { sourceText }, null);

            FuzzyFixNeighbours(doc, pair, out var before, out var after);

            var bank = KbContextForTranslation(doc, ProjectBankNameFor(projectPath, projectName),
                projectName, sourceLang, targetLang);

            string customPrompt = null;
            try
            {
                Settings.ProjectSettings ps = null;
                if (!string.IsNullOrEmpty(projectPath))
                {
                    try { ps = Settings.ProjectSettings.Load(projectPath); }
                    catch { }
                }
                BatchPromptChoice.Stored(ai, ps, false, out var storedPrompt, out _);
                customPrompt = PromptContentFor(
                    TermLensEditorViewPart.GetPromptLibrary() ?? new PromptLibrary(),
                    storedPrompt, false, sourceLang, targetLang);
            }
            catch (Exception ex)
            {
                BridgeLog.Write("FuzzyFixer: the prompt could not be read, so the built-in instructions go alone: " + ex.Message);
            }

            return new FuzzyFixJob
            {
                Ai = ai,
                SourceLang = sourceLang,
                TargetLang = targetLang,
                Segment = segments[0],
                Lookup = lookup,
                Terms = terms,
                Instructions = customPrompt,
                Lead = Core.FuzzyFixer.RepairLead(Core.FuzzyFixer.NeighboursBlock(before, after)),
                Structure = structure,
                Bank = bank
            };
        }

        /// <summary>
        /// The search and the request, on a pool thread. Reports to the window and
        /// hands a usable repair to <paramref name="done"/>; touches no Studio
        /// object. Never throws: every way out ends in the window showing a result
        /// or a reason, or in the window being closed already.
        /// </summary>
        private static async Task FuzzyFixRun(FuzzyFixJob job, FuzzyFixPreviewDialog dialog, Action<BatchSegmentResultEventArgs> done)
        {
            try
            {
                var ct = dialog.Token;
                dialog.ShowStatus("Looking for the best fuzzy match in this project's translation memories…");
                var found = Core.TmFuzzyLookup.FindBest(job.Lookup.Tms, new[] { job.Segment.SourceText },
                    job.Lookup.SourceCulture, job.Lookup.MinScore, null, ct, OpenEverTm(job.Lookup));
                await FuzzyFixRepair(job, found.Length > 0 ? found[0] : null, dialog, done).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                BridgeLog.Write("FuzzyFixer: cancelled.");
            }
            catch (Exception ex)
            {
                BridgeLog.Write("FuzzyFixer failed: " + ex.Message);
                dialog.ShowProblem("FuzzyFixer failed: " + ex.Message);
            }
        }

        /// <summary>
        /// The request for one found match (or none), and the verdict on the
        /// reply. The second half of <see cref="FuzzyFixRun"/>, and like it never
        /// throws. The usage is logged as "Translate active segment".
        /// </summary>
        private static async Task FuzzyFixRepair(FuzzyFixJob job, Core.TmFuzzyLookup.Match match,
            FuzzyFixPreviewDialog dialog, Action<BatchSegmentResultEventArgs> done)
        {
            try
            {
                var ct = dialog.Token;
                int minScore = job.Lookup.MinScore;
                var verdict = Core.FuzzyFixer.JudgeMatch(match?.SourceText, match?.TargetText, match?.Score ?? 0, minScore);
                if (verdict != Core.FuzzyFixer.MatchVerdict.Repairable)
                {
                    dialog.ShowProblem(Core.FuzzyFixer.MatchProblem(verdict, minScore, match?.TmName));
                    return;
                }

                var from = match.Score + "% match" + (string.IsNullOrWhiteSpace(match.TmName) ? "" : " from " + match.TmName);
                var model = job.Ai.GetSelectedModel();
                dialog.ShowStatus(from + ". Asking the AI" + (string.IsNullOrWhiteSpace(model) ? "" : " (" + model + ")")
                    + " to repair it… This is the match's translation:", match.TargetText);
                // Not the memory's name: bridge.log gets pasted into issues, and a memory is often named after a client.
                BridgeLog.Write("FuzzyFixer: repairing a " + match.Score + "% match.");

                job.Segment.TmSourceText = match.SourceText;
                job.Segment.TmTargetText = match.TargetText;
                job.Segment.TmMatchPercent = match.Score;
                job.Segment.TmNumbers = match.Numbers;

                string bankBlock = null;
                if (job.Bank != null)
                    bankBlock = await AwaitKbForTranslation(job.Bank, ct, lines =>
                    {
                        foreach (var line in lines) BridgeLog.Write("FuzzyFixer: " + line.Trim());
                    }, alwaysReport: false).ConfigureAwait(false);

                BatchSegmentResultEventArgs reply = null;
                string lastProblem = null;
                var worker = new BatchTranslator { LogFeature = PromptLogFeature.Translate };
                // Not written here: kept for the preview. WriteSucceeded stays
                // true, so the run counts the request as answered.
                worker.SegmentTranslated += (s, e) => reply = e;
                // The translator reports what goes wrong - a refused key, an
                // overloaded server - as progress, not as exceptions.
                worker.Progress += (s, e) =>
                {
                    if (e.IsError && !string.IsNullOrWhiteSpace(e.Message)) lastProblem = e.Message.Trim();
                };

                // No document: the neighbours go with the request, whatever
                // "Include document context" says. They and the repair instruction
                // open the user prompt, so the system prompt stays the same bytes
                // from one repair to the next and the bank is served from the cache.
                await worker.TranslateAsync(
                    new List<BatchSegment> { job.Segment }, job.SourceLang, job.TargetLang,
                    job.Ai, job.Terms, 1, ct,
                    job.Instructions, job.Ai.CustomSystemPrompt,
                    null, bankBlock,
                    structureContext: job.Structure,
                    requestLead: job.Lead).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                var replyVerdict = Core.FuzzyFixer.JudgeReply(reply?.Translation, match.TargetText, job.Segment.SourceText, out var detail);
                switch (replyVerdict)
                {
                    case Core.FuzzyFixer.ReplyVerdict.Empty:
                        dialog.ShowProblem("The AI returned no usable translation."
                            + (lastProblem != null ? "\n\n" + lastProblem : ""));
                        break;
                    case Core.FuzzyFixer.ReplyVerdict.TagsDiffer:
                        dialog.ShowProblem("The AI's repair does not carry this segment's tags (" + detail
                            + "), so it cannot be written safely.\n\nThe AI returned:\n" + reply.Translation);
                        break;
                    case Core.FuzzyFixer.ReplyVerdict.Unchanged:
                        dialog.ShowProblem("The AI returned the " + from + " unchanged: it found nothing to repair.\n\n"
                            + "To use the match as it is, apply it from Translation Results.\n\n" + match.TargetText);
                        break;
                    default:
                        done(reply);
                        dialog.ShowResult(from + ", repaired. Red is removed, green is added.\n"
                            + "Enter writes it to the target as a draft; Escape discards it.",
                            Core.FuzzyFixer.Changes(match.TargetText, reply.Translation));
                        break;
                }
                BridgeLog.Write("FuzzyFixer: " + replyVerdict + ".");
            }
            catch (OperationCanceledException)
            {
                BridgeLog.Write("FuzzyFixer: cancelled.");
            }
            catch (Exception ex)
            {
                BridgeLog.Write("FuzzyFixer failed: " + ex.Message);
                dialog.ShowProblem("FuzzyFixer failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Writes the repair into its segment, checked (<see cref="Core.SegmentWriteCheck"/>).
        /// Unlike Alt+T's write it has no plain-text fallback: a repair whose tags
        /// cannot be rebuilt is not written at all. With Track Changes on, a
        /// repair written over text is recorded word by word, as Batch Translate's
        /// writes are. False with <paramref name="why"/> when nothing was written;
        /// true with a <paramref name="note"/> when it was written but something
        /// needs saying (Studio threw after taking it, or the status stayed). UI thread.
        /// </summary>
        private static bool FuzzyFixWrite(IStudioDocument doc, ISegmentPair pair, BatchSegmentResultEventArgs repair,
            out string why, out string note)
        {
            why = null;
            note = null;
            string faultNote = null;
            try
            {
                bool wrote = false;
                Write(repair.Translation);
                if (wrote && repair.Comment != null && !TryAddTcComment(doc, pair, repair.Comment))
                    Write(BatchTranslator.InlineComment(repair.Translation, repair.Comment));
                if (!wrote)
                {
                    why = "its tags could not be rebuilt. The target is as it was.";
                    return false;
                }

                // A repair is a draft until the translator confirms it, also in a
                // segment that was confirmed (as the proofreader's Apply does).
                try
                {
                    if (pair.Properties != null && pair.Properties.ConfirmationLevel != ConfirmationLevel.Draft)
                    {
                        pair.Properties.ConfirmationLevel = ConfirmationLevel.Draft;
                        doc.UpdateSegmentPairProperties(pair, pair.Properties);
                    }
                }
                catch (Exception ex)
                {
                    BridgeLog.Write("FuzzyFixer: status not set to Draft: " + ex.Message);
                    note = "The repair was written, but the segment's status could not be set to Draft (" + ex.Message + ").";
                }
                if (faultNote != null) note = note == null ? faultNote : faultNote + "\n\n" + note;
                return true;

                void Write(string translation) => Core.SegmentWriteCheck.ProcessChecked(doc, pair, "Supervertaler", (sp, cancel) =>
                {
                    wrote = false;
                    if (repair.HasTags && repair.TagMap != null && repair.TagMap.Count > 0)
                    {
                        wrote = SegmentTagHandler.ReconstructTarget(sp.Target, sp.Source, translation, repair.TagMap);
                    }
                    else
                    {
                        var template = SegmentTagHandler.FindFirstText(sp.Source);
                        if (template != null && !string.IsNullOrEmpty(translation))
                        {
                            sp.Target.Clear();
                            SegmentTagHandler.AppendText(sp.Target, translation, sp.Source, repair.TagMap, template);
                            wrote = true;
                        }
                    }
                    if (wrote) Core.EditCapture.CaptureController.NoteProposal(sp, doc);
                    else cancel.Cancel = true;
                },
                // Studio threw, but the repair is in the document: written, and said once.
                faultAfterWrite: ex =>
                {
                    if (faultNote == null) faultNote = WrittenDespiteFault(SegmentLabelOf(doc, pair), ex).TrimStart('⚠', ' ');
                },
                // Word by word over text: a repair usually replaces a fuzzy match
                // already in the target, and only the repaired words are then marked.
                wordLevel: true, overTextOnly: true);
            }
            catch (Exception ex)
            {
                BridgeLog.Write("FuzzyFixer write error: " + ex.Message);
                why = ex is Core.SegmentWriteDeclinedException ? ex.Message : ex.Message + " The target may not have changed.";
                return false;
            }
        }
    }
}
