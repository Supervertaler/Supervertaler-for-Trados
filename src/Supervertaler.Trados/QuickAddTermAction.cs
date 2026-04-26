using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Sdl.TranslationStudioAutomation.IntegrationApi.Presentation.DefaultLocations;
using Supervertaler.Trados.Controls;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Licensing;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados
{
    /// <summary>
    /// Editor context menu action: "Quick-add term to write termbases".
    /// Appears in the right-click context menu and responds to Ctrl+Alt+Shift+T.
    /// Extracts selected source/target text and inserts the term directly,
    /// bypassing the AddTermDialog for faster workflow.
    /// </summary>
    [Action("TermLens_QuickAddTerm", typeof(EditorController),
        Name = "Quick-add term to write termbases",
        Description = "Quickly add the selected source/target text to all write termbases (no dialog)")]
    [ActionLayout(
        typeof(TranslationStudioDefaultContextMenus.EditorDocumentContextMenuLocation), 6,
        DisplayType.Default, "", false)]
    [Shortcut(Keys.Alt | Keys.Down)]
    public class QuickAddTermAction : AbstractAction
    {
        protected override void Execute()
        {
            if (!LicenseManager.Instance.HasTier1Access)
            {
                LicenseManager.ShowLicenseRequiredMessage();
                return;
            }

            try
            {
                var editorController = SdlTradosStudio.Application.GetController<EditorController>();
                var doc = editorController?.ActiveDocument;
                if (doc == null)
                {
                    MessageBox.Show("No document is open.",
                        "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var settings = TermLensSettings.Load();

                // Validate at least one write termbase is configured
                if (settings.WriteTermbaseIds == null || settings.WriteTermbaseIds.Count == 0)
                {
                    MessageBox.Show(
                        "No write termbase is configured.\n\n" +
                        "Open TermLens settings (gear icon) and check the \u201cWrite\u201d column " +
                        "for the termbases where new terms should be added.",
                        "TermLens \u2014 Quick-Add Term",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Validate termbase path
                if (string.IsNullOrEmpty(settings.TermbasePath) || !File.Exists(settings.TermbasePath))
                {
                    MessageBox.Show(
                        "Database file not found. Please check the TermLens settings.",
                        "TermLens \u2014 Quick-Add Term",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Get text from source and target segments (use GetFinalText to strip tracked changes)
                string fullSource = doc.ActiveSegmentPair?.Source != null
                    ? SegmentTagHandler.GetFinalText(doc.ActiveSegmentPair.Source) : "";
                string fullTarget = doc.ActiveSegmentPair?.Target != null
                    ? SegmentTagHandler.GetFinalText(doc.ActiveSegmentPair.Target) : "";
                string sourceText = fullSource;
                string targetText = fullTarget;

                try
                {
                    // If there is an active selection, expand it to full word boundaries
                    var selection = doc.Selection;
                    if (selection != null)
                    {
                        try
                        {
                            var srcSel = selection.Source?.ToString();
                            if (!string.IsNullOrWhiteSpace(srcSel))
                                sourceText = SelectionExpander.ExpandToWordBoundaries(fullSource, srcSel);
                        }
                        catch { /* Selection may not be available */ }

                        try
                        {
                            var tgtSel = selection.Target?.ToString();
                            if (!string.IsNullOrWhiteSpace(tgtSel))
                                targetText = SelectionExpander.ExpandToWordBoundaries(fullTarget, tgtSel);
                        }
                        catch { /* Selection may not be available */ }
                    }
                }
                catch
                {
                    // Fall back to full segment text
                    sourceText = fullSource;
                    targetText = fullTarget;
                }

                sourceText = sourceText.Trim();
                targetText = targetText.Trim();

                // Validate we have text to work with
                if (string.IsNullOrWhiteSpace(sourceText) || string.IsNullOrWhiteSpace(targetText))
                {
                    MessageBox.Show(
                        "Both source and target text are required.\n\n" +
                        "Make sure you have an active segment with text in both " +
                        "the source and target columns.",
                        "TermLens \u2014 Quick-Add Term",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Get write termbase metadata for all configured write targets
                var writeTermbases = new List<Models.TermbaseInfo>();
                using (var reader = new TermbaseReader(settings.TermbasePath))
                {
                    if (reader.Open())
                    {
                        foreach (var id in settings.WriteTermbaseIds)
                        {
                            var tb = reader.GetTermbaseById(id);
                            if (tb != null) writeTermbases.Add(tb);
                        }
                    }
                }

                if (writeTermbases.Count == 0)
                {
                    MessageBox.Show(
                        "The configured write termbases were not found in the database.\n" +
                        "Please check the TermLens settings.",
                        "TermLens \u2014 Quick-Add Term",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Capture the project source language — TermbaseReader.InsertTermBatch
                // and TermMergeChecker.FindMergeMatches both use it to decide
                // PER-TERMBASE whether to swap source/target so each write termbase
                // gets its text in (and gets searched on) the right columns
                // regardless of the mix of declared directions in the batch.
                //
                // HISTORY: Previously this method swapped sourceText/targetText ONCE,
                // based on writeTermbases[0]. That meant any write termbase in the
                // batch with a direction different from the first one ended up with
                // text in the wrong columns — which corrupted the PATENTS termbase
                // for a user who had other write termbases of the opposite direction
                // active at various times. The per-termbase swap now happens inside
                // InsertTermBatch and FindMergeMatches themselves.
                string projSrcLang = "";
                try { projSrcLang = doc.ActiveFile?.SourceFile?.Language?.DisplayName ?? ""; }
                catch { /* leave empty if unavailable */ }

                // sourceText / targetText are always kept in project direction from
                // here on. Downstream callees per-termbase decide whether to flip
                // them into the termbase's storage direction.
                var indexSourceText = sourceText;
                var indexTargetText = targetText;

                // Check for existing entries with matching source or target.
                // Pass projSrcLang so FindMergeMatches can per-termbase swap the
                // search columns to match each termbase's storage direction —
                // without this, reverse-direction termbases silently miss every
                // match (the SQL would compare DB English columns against project
                // Dutch text and vice versa). Mirrors the per-termbase swap that
                // InsertTermBatch does internally.
                try
                {
                    var mergeMatches = TermMergeChecker.FindMergeMatches(
                        settings.TermbasePath, sourceText, targetText, writeTermbases,
                        projectSourceLang: projSrcLang);

                    if (mergeMatches.Count > 0)
                    {
                        using (var mergeDlg = new MergePromptDialog(
                            mergeMatches, sourceText, targetText))
                        {
                            var mergeResult = mergeDlg.ShowDialog();

                            if (mergeResult == DialogResult.Cancel)
                                return;

                            if (mergeResult == DialogResult.Yes || mergeResult == DialogResult.Retry)
                            {
                                // Add as synonym to each matched entry. The "source"/
                                // "target" language tag on AddSynonym refers to the
                                // termbase's storage direction, not the project's —
                                // when the termbase is inverted relative to the project,
                                // the project source text belongs in the target column
                                // and vice versa.
                                foreach (var match in mergeMatches)
                                {
                                    var termSourceCol = match.TermbaseInverted ? targetText : sourceText;
                                    var termTargetCol = match.TermbaseInverted ? sourceText : targetText;

                                    if (match.MatchType == "source")
                                        TermbaseReader.AddSynonym(
                                            settings.TermbasePath, match.TermId,
                                            termTargetCol, "target");
                                    else
                                        TermbaseReader.AddSynonym(
                                            settings.TermbasePath, match.TermId,
                                            termSourceCol, "source");
                                }

                                // Insert normally into termbases that had no match
                                var matchedTbIds = new HashSet<long>(
                                    mergeMatches.Select(m => m.TermbaseId));
                                var unmatchedTbs = writeTermbases
                                    .Where(tb => !matchedTbIds.Contains(tb.Id)).ToList();

                                if (unmatchedTbs.Count > 0)
                                {
                                    TermbaseReader.InsertTermBatch(
                                        settings.TermbasePath, sourceText, targetText,
                                        "", unmatchedTbs,
                                        projectSourceLang: projSrcLang);
                                }

                                // Full reload to pick up synonym changes
                                TermLensEditorViewPart.NotifyTermAdded();

                                // "Add & Edit" — open the term entry editor on the first matched entry
                                if (mergeResult == DialogResult.Retry)
                                {
                                    var firstMatch = mergeMatches[0];
                                    var tb = writeTermbases.Find(t => t.Id == firstMatch.TermbaseId);
                                    if (tb != null)
                                    {
                                        var entry = TermbaseReader.GetTermById(settings.TermbasePath, firstMatch.TermId);
                                        if (entry != null)
                                        {
                                            using (var editor = new TermEntryEditorDialog(entry, settings.TermbasePath, tb))
                                            {
                                                if (editor.ShowDialog() == DialogResult.OK)
                                                    TermLensEditorViewPart.NotifyTermAdded();
                                            }
                                        }
                                    }
                                }
                                return;
                            }
                            // DialogResult.No = "Keep Both" — fall through to normal insert
                        }
                    }

                    // Normal insert into all write termbases. InsertTermBatch makes
                    // a per-termbase swap decision using projectSourceLang — each
                    // termbase's declared direction is respected independently.
                    var batchResults = TermbaseReader.InsertTermBatch(
                        settings.TermbasePath, sourceText, targetText, "", writeTermbases,
                        projectSourceLang: projSrcLang);

                    if (batchResults.Count > 0)
                    {
                        // Build TermEntry objects from known data + returned IDs
                        var insertedEntries = new List<Models.TermEntry>();
                        foreach (var (termbaseId, newId) in batchResults)
                        {
                            var tb = writeTermbases.Find(t => t.Id == termbaseId);
                            if (tb == null) continue;
                            insertedEntries.Add(new Models.TermEntry
                            {
                                Id = newId,
                                SourceTerm = indexSourceText,
                                TargetTerm = indexTargetText,
                                SourceLang = tb.SourceLang,
                                TargetLang = tb.TargetLang,
                                TermbaseId = tb.Id,
                                TermbaseName = tb.Name,
                                IsProjectTermbase = tb.IsProjectTermbase,
                                Ranking = tb.Ranking,
                                Definition = "",
                                Domain = "",
                                Notes = "",
                                Forbidden = false,
                                CaseSensitive = false,
                                TargetSynonyms = new List<string>()
                            });
                        }

                        // Incremental index update — no full DB reload
                        TermLensEditorViewPart.NotifyTermInserted(insertedEntries);
                    }
                    else
                    {
                        MessageBox.Show(
                            "This term already exists in the termbase.",
                            "TermLens \u2014 Quick-Add Term",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Failed to add term: {ex.Message}\n\n" +
                        "The database may be locked by another application.",
                        "TermLens \u2014 Quick-Add Term",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unexpected error: {ex.Message}",
                    "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
