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

                // sourceText / targetText stay in project direction throughout.
                // FindMergeMatches and InsertTerm each do their own per-termbase
                // direction handling — for InsertTerm we compute the swap once
                // just before the call.
                var indexSourceText = sourceText;
                var indexTargetText = targetText;

                string projSrcLang = "";
                try { projSrcLang = doc.ActiveFile?.SourceFile?.Language?.DisplayName ?? ""; }
                catch { /* leave empty if unavailable */ }

                bool isInverted = false;
                try
                {
                    var tbSrcLang = projectTermbase.SourceLang ?? "";
                    if (!string.IsNullOrEmpty(projSrcLang) && !string.IsNullOrEmpty(tbSrcLang))
                    {
                        // Normalize both to shortened form so that "English (United States)"
                        // and "English (US)" compare correctly.
                        var projNorm = LanguageUtils.ShortenLanguageName(projSrcLang);
                        var tbNorm = LanguageUtils.ShortenLanguageName(tbSrcLang);
                        bool match =
                            projNorm.StartsWith(tbNorm, StringComparison.OrdinalIgnoreCase) ||
                            tbNorm.StartsWith(projNorm, StringComparison.OrdinalIgnoreCase);
                        isInverted = !match;
                    }
                }
                catch { /* leave isInverted=false if language info unavailable */ }

                // Check for existing entries with matching source or target.
                // projSrcLang lets FindMergeMatches swap search columns to match
                // a reverse-direction termbase's storage layout — without it,
                // matches in such termbases are silently missed.
                try
                {
                    var mergeMatches = TermMergeChecker.FindMergeMatches(
                        settings.TermbasePath, sourceText, targetText,
                        new List<Models.TermbaseInfo> { projectTermbase },
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
                                // Add as synonym to the matched entry. The "source"/
                                // "target" tag on AddSynonym refers to the termbase's
                                // storage direction — when the termbase is inverted
                                // relative to the project, project source goes in the
                                // target column and vice versa.
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

                    // Normal insert into project termbase. Swap into termbase
                    // storage direction at the point of insertion — we keep the
                    // project-direction text upstream so the merge flow and the
                    // index-update notification both see what the translator typed.
                    var insertSourceText = isInverted ? targetText : sourceText;
                    var insertTargetText = isInverted ? sourceText : targetText;
                    var newId = TermbaseReader.InsertTerm(
                        settings.TermbasePath,
                        settings.ProjectTermbaseId,
                        insertSourceText,
                        insertTargetText,
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
