using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Sdl.TranslationStudioAutomation.IntegrationApi.Presentation.DefaultLocations;
using TermLens.Core;
using TermLens.Settings;

namespace TermLens
{
    /// <summary>
    /// Editor context menu action: "Quick add Term to TermLens".
    /// Appears in the right-click context menu and responds to Ctrl+Alt+Shift+T.
    /// Extracts selected source/target text and inserts the term directly,
    /// bypassing the AddTermDialog for faster workflow.
    /// </summary>
    [Action("TermLens_QuickAddTerm", typeof(EditorController),
        Name = "Quick Add Term to Glossary Set to 'Write'",
        Description = "Quickly add the selected source/target text to all Write termbases (no dialog)")]
    [ActionLayout(
        typeof(TranslationStudioDefaultContextMenus.EditorDocumentContextMenuLocation), 6,
        DisplayType.Default, "", false)]
    [Shortcut(Keys.Alt | Keys.Down)]
    public class QuickAddTermAction : AbstractAction
    {
        protected override void Execute()
        {
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
                        "TermLens \u2014 Quick Add Term",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Validate termbase path
                if (string.IsNullOrEmpty(settings.TermbasePath) || !File.Exists(settings.TermbasePath))
                {
                    MessageBox.Show(
                        "Termbase file not found. Please check the TermLens settings.",
                        "TermLens \u2014 Quick Add Term",
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
                        "TermLens \u2014 Quick Add Term",
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
                        "TermLens \u2014 Quick Add Term",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Insert the term into all write termbases — no dialog
                try
                {
                    bool anyInserted = false;
                    foreach (var tb in writeTermbases)
                    {
                        var newId = TermbaseReader.InsertTerm(
                            settings.TermbasePath,
                            tb.Id,
                            sourceText,
                            targetText,
                            tb.SourceLang,
                            tb.TargetLang,
                            ""); // No definition for quick-add

                        if (newId > 0) anyInserted = true;
                    }

                    if (anyInserted)
                    {
                        // Reload term index so the new term appears immediately
                        TermLensEditorViewPart.NotifyTermAdded();
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Failed to add term: {ex.Message}\n\n" +
                        "The database may be locked by another application.",
                        "TermLens \u2014 Quick Add Term",
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
