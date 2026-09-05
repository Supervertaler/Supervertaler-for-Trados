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
    /// Editor context menu action: "Add term with abbreviation (AI)".
    ///
    /// For the specific case where a concept is spelled out in the segment
    /// alongside its abbreviation – "Sustainable Finance Disclosure Regulation
    /// (SFDR, Verordening (EU) 2019/2088)" – and both the term and the
    /// abbreviation would otherwise be typed into the dialog by hand.
    ///
    /// It does NOT extract ordinary term pairs, deliberately. See
    /// <see cref="AbbreviationTermExtractor"/> for why: the abbreviation is the
    /// anchor that says which span matters, and without one the model is merely
    /// guessing which concept the translator cared about – while
    /// <see cref="AddTermAction"/> (Ctrl+Alt+T) and QuickAddTermAction (Alt+Down)
    /// already use the translator's own selection exactly.
    ///
    /// The dialog still opens and the translator still saves it. Nothing is
    /// written without confirmation: an extraction can pick the wrong span or
    /// the wrong abbreviation, and a silent fan-out into every write termbase is
    /// exactly the failure mode that corrupted two termbases in 20.153.
    ///
    /// Falls back to plain <see cref="AddTermAction"/> behaviour – the raw
    /// selection, no abbreviations – whenever the AI is unconfigured, fails,
    /// finds no abbreviated term, or returns something that does not survive
    /// validation. The action never leaves the translator with nothing.
    /// </summary>
    [Action("TermLens_AddTermWithAbbreviation", typeof(EditorController),
        Name = "Add term with abbreviation (AI)",
        Description = "Find the term in this segment that carries an abbreviation, and pre-fill the term entry dialog with both")]
    [ActionLayout(
        typeof(TranslationStudioDefaultContextMenus.EditorDocumentContextMenuLocation), 7,
        DisplayType.Default, "", true)]
    // Ctrl+Alt+A ("A" for abbreviation), sitting beside Ctrl+Alt+T for the plain
    // add-term dialog and Ctrl+Alt+N for non-translatables.
    //
    // NOT Alt+Shift+Down, which was the first choice: that is Trados's own
    // factory binding for "Select Next Row", so the two fired together.
    [Shortcut(Keys.Control | Keys.Alt | Keys.A)]
    public class AddTermWithAbbreviationAction : AbstractAction
    {
        protected override void Execute()
        {
            if (!LicenseManager.Instance.HasAssistantAccess)
            {
                LicenseManager.ShowUpgradeMessage();
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

                var settings = SettingsService.Current;

                if (settings.WriteTermbaseIds == null || settings.WriteTermbaseIds.Count == 0)
                {
                    MessageBox.Show(
                        "No write termbase is configured.\n\n" +
                        "Open TermLens settings (gear icon) and check the “Write” column " +
                        "for the termbases where new terms should be added.",
                        "TermLens — Add Term with Abbreviation",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (string.IsNullOrEmpty(settings.TermbasePath) || !File.Exists(settings.TermbasePath))
                {
                    MessageBox.Show(
                        "Database file not found. Please check the TermLens settings.",
                        "TermLens — Add Term with Abbreviation",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Full segment text on both sides – the model needs the whole
                // segment, since the abbreviation usually sits outside whatever
                // the translator happened to select.
                string fullSource = doc.ActiveSegmentPair?.Source != null
                    ? SegmentTagHandler.GetFinalText(doc.ActiveSegmentPair.Source) : "";
                string fullTarget = doc.ActiveSegmentPair?.Target != null
                    ? SegmentTagHandler.GetFinalText(doc.ActiveSegmentPair.Target) : "";

                if (string.IsNullOrWhiteSpace(fullSource) || string.IsNullOrWhiteSpace(fullTarget))
                {
                    MessageBox.Show(
                        "Both source and target text are required.\n\n" +
                        "Make sure the active segment has text on both sides.",
                        "TermLens — Add Term with Abbreviation",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // A selection is the translator's statement of WHICH concept they
                // mean, so it binds: the extracted term must overlap it, enforced
                // in Parse rather than merely requested in the prompt. Selecting
                // half a term still yields the whole term - the model completes
                // the boundaries, it just may not switch to a different term.
                string srcSelection = null, tgtSelection = null;
                try
                {
                    var selection = doc.Selection;
                    if (selection != null)
                    {
                        try { srcSelection = selection.Source?.ToString(); } catch { }
                        try { tgtSelection = selection.Target?.ToString(); } catch { }
                    }
                }
                catch { /* selection unavailable – the model works from the segment alone */ }

                // Fallback pre-fills, used when extraction is unavailable or fails.
                string fallbackSource = string.IsNullOrWhiteSpace(srcSelection) ? fullSource : srcSelection.Trim();
                string fallbackTarget = string.IsNullOrWhiteSpace(tgtSelection) ? fullTarget : tgtSelection.Trim();

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
                        "TermLens — Add Term with Abbreviation",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var primaryTb = settings.ProjectTermbaseId > 0
                    ? writeTermbases.Find(t => t.Id == settings.ProjectTermbaseId) ?? writeTermbases[0]
                    : writeTermbases[0];

                string projectSourceLang = null, projectTargetLang = null;
                try { projectSourceLang = doc.ActiveFile?.SourceFile?.Language?.DisplayName; } catch { }
                try { projectTargetLang = doc.ActiveFile?.Language?.DisplayName; } catch { }

                // ── AI extraction ──────────────────────────────────────────────
                var extracted = TryExtract(
                    settings, fullSource, fullTarget,
                    projectSourceLang, projectTargetLang,
                    srcSelection, tgtSelection);

                string preSource = fallbackSource;
                string preTarget = fallbackTarget;
                string preSourceAbbr = null;
                string preTargetAbbr = null;

                if (extracted != null && extracted.Found)
                {
                    preSource = extracted.SourceTerm;
                    preTarget = extracted.TargetTerm;
                    preSourceAbbr = extracted.SourceAbbreviation;
                    preTargetAbbr = extracted.TargetAbbreviation;
                }

                // Pre-fills are passed in PROJECT direction; the dialog swaps them
                // per its own termbase's declared direction (see its add-mode ctor).
                using (var dlg = new TermEntryEditorDialog(
                    preSource, preTarget, settings.TermbasePath, primaryTb, projectSourceLang,
                    preSourceAbbr, preTargetAbbr))
                {
                    if (dlg.ShowDialog() == DialogResult.OK)
                    {
                        if (dlg.SavedEntry != null)
                            TermLensEditorViewPart.NotifyTermInserted(
                                new List<TermEntry> { dlg.SavedEntry });
                        else
                            TermLensEditorViewPart.NotifyTermAdded();
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unexpected error: {ex.Message}",
                    "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Runs the extraction call behind the shared busy dialog. Returns null on
        /// any failure – an unconfigured provider, a network error, an unparseable
        /// or unverifiable reply – so the caller falls back to the plain selection.
        /// A failure here must never block adding a term by hand.
        /// </summary>
        private static AbbreviationTermExtractor.Result TryExtract(
            TermLensSettings settings,
            string fullSource, string fullTarget,
            string sourceLang, string targetLang,
            string srcSelection, string tgtSelection)
        {
            try
            {
                var aiSettings = settings?.AiSettings;
                if (aiSettings == null) return null;

                var provider = aiSettings.SelectedProvider ?? LlmModels.ProviderOpenAi;
                string apiKey;
                string baseUrl = null;
                string model = aiSettings.GetSelectedModel();

                if (provider == LlmModels.ProviderOllama)
                {
                    apiKey = "ollama";
                    baseUrl = aiSettings.OllamaEndpoint ?? "http://localhost:11434";
                }
                else if (provider == LlmModels.ProviderCustomOpenAi)
                {
                    var profile = aiSettings.GetActiveCustomProfile();
                    if (profile == null) return null;
                    apiKey = profile.ApiKey;
                    baseUrl = profile.Endpoint;
                    model = profile.Model;
                }
                else
                {
                    apiKey = LlmClient.ResolveApiKey(provider, aiSettings.ApiKeys);
                }

                if (string.IsNullOrEmpty(apiKey)) return null;

                var userPrompt = AbbreviationTermExtractor.BuildUserPrompt(
                    fullSource, fullTarget, sourceLang, targetLang, srcSelection, tgtSelection);

                string reply;
                using (var client = new LlmClient(provider, model, apiKey, baseUrl))
                using (var busy = new AutoPromptBusyForm(
                    () => client.SendPromptAsync(
                        userPrompt,
                        AbbreviationTermExtractor.SystemPrompt,
                        maxTokens: 400,
                        suppressLog: true),
                    title: "Add term with abbreviation",
                    message: "Reading the segment to extract the term pair and its abbreviation…"))
                {
                    busy.ShowDialog();
                    reply = busy.Result;
                }

                // The selections go to Parse as well as to the prompt: the model
                // is asked to respect them, and then checked on it.
                var result = AbbreviationTermExtractor.Parse(
                    reply, fullSource, fullTarget, srcSelection, tgtSelection);
                if (!result.Found && !string.IsNullOrEmpty(result.Note))
                    DiagnosticLog.Log("AddTermWithAbbreviation", "Extraction not used: " + result.Note);
                else if (!string.IsNullOrEmpty(result.Note))
                    DiagnosticLog.Log("AddTermWithAbbreviation", result.Note);

                return result;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Log("AddTermWithAbbreviation", "Extraction failed: " + ex.Message);
                return null;
            }
        }
    }
}
