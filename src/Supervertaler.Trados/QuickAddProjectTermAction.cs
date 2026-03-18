using System;
using System.Collections.Generic;
using System.IO;
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
    /// Keyboard-only action: "Quick-add Term to Project Termbase".
    /// Responds to Alt+Up. Extracts selected source/target text and inserts
    /// the term directly into the project termbase, bypassing the AddTermDialog.
    /// </summary>
    [Action("TermLens_QuickAddProjectTerm", typeof(EditorController),
        Name = "Quick-add term to project termbase",
        Description = "Quickly add the selected source/target text to the project termbase (no dialog)")]
    [ActionLayout(
        typeof(TranslationStudioDefaultContextMenus.EditorDocumentContextMenuLocation), 7,
        DisplayType.Default, "", false)]
    [Shortcut(Keys.Alt | Keys.Up)]
    public class QuickAddProjectTermAction : AbstractAction
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

                // Validate project termbase is configured
                if (settings.ProjectTermbaseId < 0)
                {
                    MessageBox.Show(
                        "No project termbase is configured.\n\n" +
                        "Open TermLens settings (gear icon) and check the \u201cProject\u201d column " +
                        "for the termbase that should receive project-specific terms.",
                        "TermLens \u2014 Quick-Add to Project",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Validate termbase path
                if (string.IsNullOrEmpty(settings.TermbasePath) || !File.Exists(settings.TermbasePath))
                {
                    MessageBox.Show(
                        "Database file not found. Please check the TermLens settings.",
                        "TermLens \u2014 Quick-Add to Project",
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

                sourceText = sourceText.Trim();
                targetText = targetText.Trim();

                // Validate we have text to work with
                if (string.IsNullOrWhiteSpace(sourceText) || string.IsNullOrWhiteSpace(targetText))
                {
                    MessageBox.Show(
                        "Both source and target text are required.\n\n" +
                        "Make sure you have an active segment with text in both " +
                        "the source and target columns.",
                        "TermLens \u2014 Quick-Add to Project",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Get project termbase metadata
                Models.TermbaseInfo projectTermbase = null;
                using (var reader = new TermbaseReader(settings.TermbasePath))
                {
                    if (reader.Open())
                        projectTermbase = reader.GetTermbaseById(settings.ProjectTermbaseId);
                }

                if (projectTermbase == null)
                {
                    MessageBox.Show(
                        "The configured project termbase was not found in the database.\n" +
                        "Please check the TermLens settings.",
                        "TermLens \u2014 Quick-Add to Project",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Swap source/target if project direction doesn't match the project termbase direction.
                // Keep originals for the in-memory index update.
                var indexSourceText = sourceText;
                var indexTargetText = targetText;
                try
                {
                    var projSrcLang = doc.ActiveFile?.SourceFile?.Language?.DisplayName ?? "";
                    var tbSrcLang = projectTermbase.SourceLang ?? "";
                    if (!string.IsNullOrEmpty(projSrcLang) && !string.IsNullOrEmpty(tbSrcLang))
                    {
                        bool match =
                            projSrcLang.StartsWith(tbSrcLang, StringComparison.OrdinalIgnoreCase) ||
                            tbSrcLang.StartsWith(projSrcLang, StringComparison.OrdinalIgnoreCase);
                        if (!match)
                        {
                            var tmp = sourceText;
                            sourceText = targetText;
                            targetText = tmp;
                        }
                    }
                }
                catch { /* leave sourceText/targetText as-is if language info unavailable */ }

                // Check for existing entries with matching source or target
                try
                {
                    var mergeMatches = TermMergeChecker.FindMergeMatches(
                        settings.TermbasePath, sourceText, targetText,
                        new List<Models.TermbaseInfo> { projectTermbase });

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
                                // Add as synonym to the matched entry
                                foreach (var match in mergeMatches)
                                {
                                    if (match.MatchType == "source")
                                        TermbaseReader.AddSynonym(
                                            settings.TermbasePath, match.TermId,
                                            targetText, "target");
                                    else
                                        TermbaseReader.AddSynonym(
                                            settings.TermbasePath, match.TermId,
                                            sourceText, "source");
                                }

                                // Full reload to pick up synonym changes
                                TermLensEditorViewPart.NotifyTermAdded();

                                // "Add & Edit" — open the term entry editor
                                if (mergeResult == DialogResult.Retry)
                                {
                                    var firstMatch = mergeMatches[0];
                                    var entry = TermbaseReader.GetTermById(
                                        settings.TermbasePath, firstMatch.TermId);
                                    if (entry != null)
                                    {
                                        using (var editor = new TermEntryEditorDialog(
                                            entry, settings.TermbasePath, projectTermbase))
                                        {
                                            if (editor.ShowDialog() == DialogResult.OK)
                                                TermLensEditorViewPart.NotifyTermAdded();
                                        }
                                    }
                                }
                                return;
                            }
                            // DialogResult.No = "Keep Both" — fall through to normal insert
                        }
                    }

                    // Normal insert into project termbase
                    var newId = TermbaseReader.InsertTerm(
                        settings.TermbasePath,
                        settings.ProjectTermbaseId,
                        sourceText,
                        targetText,
                        projectTermbase.SourceLang,
                        projectTermbase.TargetLang,
                        ""); // No definition for quick-add

                    if (newId > 0)
                    {
                        // Incremental index update — no full DB reload
                        var entry = new Models.TermEntry
                        {
                            Id = newId,
                            SourceTerm = indexSourceText,
                            TargetTerm = indexTargetText,
                            SourceLang = projectTermbase.SourceLang,
                            TargetLang = projectTermbase.TargetLang,
                            TermbaseId = projectTermbase.Id,
                            TermbaseName = projectTermbase.Name,
                            IsProjectTermbase = projectTermbase.IsProjectTermbase,
                            Ranking = projectTermbase.Ranking,
                            Definition = "",
                            Domain = "",
                            Notes = "",
                            Forbidden = false,
                            CaseSensitive = false,
                            TargetSynonyms = new List<string>()
                        };
                        TermLensEditorViewPart.NotifyTermInserted(
                            new List<Models.TermEntry> { entry });
                    }
                    else
                    {
                        MessageBox.Show(
                            "This term already exists in the termbase.",
                            "TermLens \u2014 Quick-Add to Project",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Failed to add term: {ex.Message}\n\n" +
                        "The database may be locked by another application.",
                        "TermLens \u2014 Quick-Add to Project",
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
