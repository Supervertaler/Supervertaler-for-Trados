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
using Supervertaler.Trados.Models;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados
{
    /// <summary>
    /// Editor context menu action: "Add Term to TermLens".
    /// Appears in the right-click context menu and responds to Ctrl+Shift+T.
    /// Extracts selected source/target text, opens AddTermDialog, and inserts the term.
    /// </summary>
    [Action("TermLens_AddTerm", typeof(EditorController),
        Name = "Add term to TermLens (dialog)",
        Description = "Add the selected source/target text as a new term via dialog")]
    [ActionLayout(
        typeof(TranslationStudioDefaultContextMenus.EditorDocumentContextMenuLocation), 8,
        DisplayType.Default, "", true)]
    [Shortcut(Keys.Control | Keys.Alt | Keys.T)]
    public class AddTermAction : AbstractAction
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
                        "TermLens \u2014 Add Term",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Validate termbase path
                if (string.IsNullOrEmpty(settings.TermbasePath) || !File.Exists(settings.TermbasePath))
                {
                    MessageBox.Show(
                        "Database file not found. Please check the TermLens settings.",
                        "TermLens \u2014 Add Term",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Get text from source and target segments
                string fullSource = doc.ActiveSegmentPair?.Source?.ToString() ?? "";
                string fullTarget = doc.ActiveSegmentPair?.Target?.ToString() ?? "";
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

                // Get write termbase metadata for all configured write targets
                var writeTermbases = new List<TermbaseInfo>();
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
                        "TermLens \u2014 Add Term",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Show the dialog
                using (var dlg = new AddTermDialog(sourceText.Trim(), targetText.Trim(), writeTermbases))
                {
                    if (dlg.ShowDialog() == DialogResult.OK)
                    {
                        if (string.IsNullOrWhiteSpace(dlg.SourceTerm) ||
                            string.IsNullOrWhiteSpace(dlg.TargetTerm))
                        {
                            MessageBox.Show("Both source and target terms are required.",
                                "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }

                        try
                        {
                            // Check for existing entries with matching source or target
                            var mergeMatches = TermMergeChecker.FindMergeMatches(
                                settings.TermbasePath, dlg.SourceTerm, dlg.TargetTerm,
                                writeTermbases);

                            if (mergeMatches.Count > 0)
                            {
                                using (var mergeDlg = new MergePromptDialog(
                                    mergeMatches, dlg.SourceTerm, dlg.TargetTerm))
                                {
                                    var mergeResult = mergeDlg.ShowDialog();

                                    if (mergeResult == DialogResult.Cancel)
                                        return;

                                    if (mergeResult == DialogResult.Yes || mergeResult == DialogResult.Retry)
                                    {
                                        // Add as synonym to each matched entry
                                        foreach (var match in mergeMatches)
                                        {
                                            if (match.MatchType == "source")
                                                TermbaseReader.AddSynonym(
                                                    settings.TermbasePath, match.TermId,
                                                    dlg.TargetTerm, "target");
                                            else
                                                TermbaseReader.AddSynonym(
                                                    settings.TermbasePath, match.TermId,
                                                    dlg.SourceTerm, "source");
                                        }

                                        // Insert normally into termbases without a match
                                        var matchedTbIds = new HashSet<long>(
                                            mergeMatches.Select(m => m.TermbaseId));
                                        foreach (var tb in writeTermbases)
                                        {
                                            if (!matchedTbIds.Contains(tb.Id))
                                            {
                                                TermbaseReader.InsertTerm(
                                                    settings.TermbasePath, tb.Id,
                                                    dlg.SourceTerm, dlg.TargetTerm,
                                                    tb.SourceLang, tb.TargetLang,
                                                    dlg.Definition,
                                                    isNonTranslatable: dlg.IsNonTranslatable);
                                            }
                                        }

                                        // Full reload to pick up synonym changes
                                        TermLensEditorViewPart.NotifyTermAdded();

                                        // "Add & Edit" — open the term entry editor
                                        if (mergeResult == DialogResult.Retry)
                                        {
                                            var firstMatch = mergeMatches[0];
                                            var tb2 = writeTermbases.Find(t => t.Id == firstMatch.TermbaseId);
                                            if (tb2 != null)
                                            {
                                                var entry = TermbaseReader.GetTermById(
                                                    settings.TermbasePath, firstMatch.TermId);
                                                if (entry != null)
                                                {
                                                    using (var editor = new TermEntryEditorDialog(
                                                        entry, settings.TermbasePath, tb2))
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

                            // Normal insert into all write termbases
                            bool anyInserted = false;
                            foreach (var tb in writeTermbases)
                            {
                                var newId = TermbaseReader.InsertTerm(
                                    settings.TermbasePath,
                                    tb.Id,
                                    dlg.SourceTerm,
                                    dlg.TargetTerm,
                                    tb.SourceLang,
                                    tb.TargetLang,
                                    dlg.Definition,
                                    isNonTranslatable: dlg.IsNonTranslatable);

                                if (newId > 0) anyInserted = true;
                            }

                            if (anyInserted)
                            {
                                // Reload term index so the new term appears immediately
                                TermLensEditorViewPart.NotifyTermAdded();
                            }
                            else
                            {
                                MessageBox.Show(
                                    "This term already exists in the termbase.",
                                    "TermLens \u2014 Add Term",
                                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                            }
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(
                                $"Failed to add term: {ex.Message}\n\n" +
                                "The database may be locked by another application.",
                                "TermLens \u2014 Add Term",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
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
