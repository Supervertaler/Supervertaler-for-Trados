using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Sdl.TranslationStudioAutomation.IntegrationApi.Presentation.DefaultLocations;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Licensing;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados
{
    /// <summary>
    /// Editor context menu action: "Quick-add Non-Translatable Term".
    /// Marks the selected source text as non-translatable in all Write termbases.
    /// The target term is set to the source term automatically.
    /// Triggered by Ctrl+Alt+N.
    /// </summary>
    [Action("TermLens_QuickAddNonTranslatable", typeof(EditorController),
        Name = "Quick-add non-translatable term",
        Description = "Mark the selected source text as non-translatable in all write termbases (no dialog)")]
    [ActionLayout(
        typeof(TranslationStudioDefaultContextMenus.EditorDocumentContextMenuLocation), 5,
        DisplayType.Default, "", false)]
    [Shortcut(Keys.Control | Keys.Alt | Keys.N)]
    public class QuickAddNonTranslatableAction : AbstractAction
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
                        "TermLens \u2014 Non-Translatable",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Validate termbase path
                if (string.IsNullOrEmpty(settings.TermbasePath) || !File.Exists(settings.TermbasePath))
                {
                    MessageBox.Show(
                        "Database file not found. Please check the TermLens settings.",
                        "TermLens \u2014 Non-Translatable",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Get the term text — check source selection first, then target selection.
                // Non-translatables only need one text (target = source), so we accept
                // selected text from either column.
                string fullSource = doc.ActiveSegmentPair?.Source?.ToString() ?? "";
                string fullTarget = doc.ActiveSegmentPair?.Target?.ToString() ?? "";
                string sourceText = null;

                try
                {
                    var selection = doc.Selection;
                    if (selection != null)
                    {
                        // Try source selection first
                        try
                        {
                            var srcSel = selection.Source?.ToString();
                            if (!string.IsNullOrWhiteSpace(srcSel))
                                sourceText = SelectionExpander.ExpandToWordBoundaries(fullSource, srcSel);
                        }
                        catch { /* Selection may not be available */ }

                        // If no source selection, try target selection
                        if (string.IsNullOrWhiteSpace(sourceText))
                        {
                            try
                            {
                                var tgtSel = selection.Target?.ToString();
                                if (!string.IsNullOrWhiteSpace(tgtSel))
                                    sourceText = SelectionExpander.ExpandToWordBoundaries(fullTarget, tgtSel);
                            }
                            catch { /* Selection may not be available */ }
                        }
                    }
                }
                catch
                {
                    // Ignore selection errors
                }

                // Fall back to full source segment if no selection was found
                if (string.IsNullOrWhiteSpace(sourceText))
                    sourceText = fullSource;

                sourceText = sourceText.Trim();

                if (string.IsNullOrWhiteSpace(sourceText))
                {
                    MessageBox.Show(
                        "No text found.\n\n" +
                        "Select the text in the source or target column that should be marked as non-translatable.",
                        "TermLens \u2014 Non-Translatable",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Get write termbase metadata
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
                        "TermLens \u2014 Non-Translatable",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Insert: target = source, non-translatable = true
                try
                {
                    var batchResults = TermbaseReader.InsertTermBatch(
                        settings.TermbasePath, sourceText, sourceText, "",
                        writeTermbases, isNonTranslatable: true);

                    if (batchResults.Count > 0)
                    {
                        var insertedEntries = new List<Models.TermEntry>();
                        foreach (var (termbaseId, newId) in batchResults)
                        {
                            var tb = writeTermbases.Find(t => t.Id == termbaseId);
                            if (tb == null) continue;
                            insertedEntries.Add(new Models.TermEntry
                            {
                                Id = newId,
                                SourceTerm = sourceText,
                                TargetTerm = sourceText,
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
                                IsNonTranslatable = true,
                                TargetSynonyms = new List<string>()
                            });
                        }

                        TermLensEditorViewPart.NotifyTermInserted(insertedEntries);
                    }
                    else
                    {
                        MessageBox.Show(
                            "This term already exists in the termbase.",
                            "TermLens \u2014 Non-Translatable",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Failed to add non-translatable term: {ex.Message}\n\n" +
                        "The database may be locked by another application.",
                        "TermLens \u2014 Non-Translatable",
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
