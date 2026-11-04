using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados
{
    /// <summary>
    /// Runs a QuickLauncher prompt by shortcut slot (1–10).
    /// Looks up the slot-to-prompt mapping in settings. If no mapping exists,
    /// falls back to menu position (slot 1 = first prompt, etc.).
    /// </summary>
    public static class QuickLauncherSlotRunner
    {
        private static readonly PromptLibrary _library = new PromptLibrary();

        /// <summary>
        /// Returns the slot number (1–10) assigned to a prompt, or 0 if none.
        /// </summary>
        public static int GetSlotForPrompt(string promptFilePath, AiSettings aiSettings)
        {
            if (aiSettings?.QuickLauncherSlots == null || string.IsNullOrEmpty(promptFilePath))
                return 0;

            foreach (var kvp in aiSettings.QuickLauncherSlots)
            {
                if (kvp.Value == promptFilePath)
                {
                    int s;
                    if (int.TryParse(kvp.Key, out s))
                        return s;
                }
            }
            return 0;
        }

        /// <summary>
        /// Returns the shortcut display string for a prompt, or null if unassigned.
        /// </summary>
        public static string GetShortcutDisplay(string promptFilePath, AiSettings aiSettings)
        {
            var slot = GetSlotForPrompt(promptFilePath, aiSettings);
            if (slot == 0) return null;
            var keyDigit = slot == 10 ? "0" : slot.ToString();
            return $"Ctrl+Alt+{keyDigit}";
        }

        public static void RunSlot(int slot, TermLensSettings settings = null)
        {
            // Refresh and get prompts in the same order as the Ctrl+Q menu
            _library.Refresh();
            var prompts = _library.GetQuickLauncherPrompts();

            settings = settings ?? SettingsService.Current;
            var aiSettings = settings?.AiSettings;

            // Look up slot mapping in settings
            PromptTemplate prompt = null;
            var slotKey = slot.ToString();
            if (aiSettings?.QuickLauncherSlots != null &&
                aiSettings.QuickLauncherSlots.ContainsKey(slotKey))
            {
                var targetPath = aiSettings.QuickLauncherSlots[slotKey];
                foreach (var p in prompts)
                {
                    if (p.FilePath == targetPath)
                    {
                        prompt = p;
                        break;
                    }
                }
            }

            // Fallback: auto-assign by menu position
            if (prompt == null)
            {
                var index = slot - 1;
                if (index >= 0 && index < prompts.Count)
                    prompt = prompts[index];
            }

            if (prompt == null)
            {
                var keyDigit = slot == 10 ? "0" : slot.ToString();
                MessageBox.Show(
                    $"No prompt assigned to Ctrl+Alt+{keyDigit}.\n\n" +
                    "You can assign shortcuts in Settings \u2192 Prompts.",
                    "Supervertaler \u2013 QuickLauncher",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Gather segment context (same as QuickLauncherAction)
            var editorController = StudioEditor.Controller;
            var doc = editorController?.ActiveDocument;

            var sourceText = "";
            var targetText = "";
            var selection = "";
            var sourceLang = "";
            var targetLang = "";
            var projectName = "";
            var documentName = "";

            if (doc != null)
            {
                sourceText = doc.ActiveSegmentPair?.Source != null
                    ? SegmentTagHandler.GetFinalText(doc.ActiveSegmentPair.Source) : "";
                targetText = doc.ActiveSegmentPair?.Target != null
                    ? SegmentTagHandler.GetFinalText(doc.ActiveSegmentPair.Target) : "";

                try
                {
                    var sel = doc.Selection;
                    if (sel != null)
                    {
                        var srcSel = sel.Source?.ToString();
                        var tgtSel = sel.Target?.ToString();
                        if (!string.IsNullOrWhiteSpace(srcSel))
                            selection = srcSel.Trim();
                        else if (!string.IsNullOrWhiteSpace(tgtSel))
                            selection = tgtSel.Trim();
                    }
                }
                catch { }

                try
                {
                    var file = doc.ActiveFile;
                    if (file != null)
                    {
                        sourceLang = file.SourceFile?.Language?.DisplayName ?? "";
                        targetLang = file.Language?.DisplayName ?? "";
                    }
                }
                catch { }

                projectName = Core.DocumentContextHelper.GetProjectName(doc);
                documentName = Core.DocumentContextHelper.GetDocumentName(doc);
            }

            // Text transforms: apply find/replace directly to target – no AI call
            if (prompt.IsTransform)
            {
                var result = AiAssistantViewPart.RunTextTransform(prompt);
                AiAssistantViewPart.ShowTransformResult(prompt.Name, result);
                return;
            }

            var surroundingCount = aiSettings?.QuickLauncherSurroundingSegments ?? 5;
            var content = prompt.Content;

            // Lazily gather expensive context
            var surroundingSegments = content.Contains("{{SURROUNDING_SEGMENTS}}")
                ? Core.DocumentContextHelper.FormatSurroundingSegments(doc, surroundingCount)
                : null;

            var projectText = content.Contains("{{PROJECT}}")
                ? Core.DocumentContextHelper.FormatProjectText(doc)
                : null;

            var tmMatchesText = content.Contains("{{TM_MATCHES}}")
                ? PromptLibrary.FormatTmMatches(
                    Core.DocumentContextHelper.GetTmMatches(doc), 70)
                : null;

            var expanded = PromptLibrary.ApplyVariables(
                content,
                sourceLang, targetLang,
                sourceText, targetText, selection,
                projectName, documentName,
                surroundingSegments, projectText, tmMatchesText);

            // Compact display version for chat bubble
            string displayExpanded = null;
            if (projectText != null)
            {
                var segCount = 0;
                foreach (var line in projectText.Split('\n'))
                    if (line.TrimStart().StartsWith("[")) segCount++;

                var placeholder = $"[source document \u2013 {segCount} segment{(segCount == 1 ? "" : "s")}]";
                displayExpanded = PromptLibrary.ApplyVariables(
                    content,
                    sourceLang, targetLang,
                    sourceText, targetText, selection,
                    projectName, documentName,
                    surroundingSegments, placeholder, tmMatchesText);
            }

            AiAssistantViewPart.RunQuickLauncherPrompt(expanded, displayExpanded, prompt.Name);
        }
    }
}
