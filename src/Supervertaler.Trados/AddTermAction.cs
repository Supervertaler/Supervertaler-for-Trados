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

                // Pick target termbase — prefer project termbase, fall back to first write termbase
                var primaryTb = settings.ProjectTermbaseId > 0
                    ? writeTermbases.Find(t => t.Id == settings.ProjectTermbaseId) ?? writeTermbases[0]
                    : writeTermbases[0];

                // Pass the project source language so the dialog can show labels in project
                // order and swap source/target internally when saving to the termbase.
                string projectSourceLang = null;
                try { projectSourceLang = doc.ActiveFile?.SourceFile?.Language?.DisplayName; } catch { }

                // Open the full term entry editor in add mode
                using (var dlg = new TermEntryEditorDialog(
                    sourceText.Trim(), targetText.Trim(), settings.TermbasePath, primaryTb, projectSourceLang))
                {
                    if (dlg.ShowDialog() == DialogResult.OK)
                        TermLensEditorViewPart.NotifyTermAdded();
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
