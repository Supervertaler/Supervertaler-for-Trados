using System;
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
    /// Editor context menu action: "QuickLauncher".
    /// Appears as a single entry in the right-click context menu.
    /// When clicked, shows a submenu listing all prompts marked as QuickLauncher
    /// (sv_quickmenu: true or category: QuickLauncher in YAML frontmatter).
    /// Selecting a prompt expands its variables from the current segment context
    /// and submits it to the AI Assistant chat.
    /// </summary>
    [Action("Supervertaler_QuickLauncher", typeof(EditorController),
        Name = "QuickLauncher",
        Description = "Run a QuickLauncher prompt on the current segment using the AI Assistant")]
    [ActionLayout(
        typeof(TranslationStudioDefaultContextMenus.EditorDocumentContextMenuLocation), 9,
        DisplayType.Default, "", true)]
    [Shortcut(Keys.Control | Keys.Q)]
    public class QuickLauncherAction : AbstractAction
    {
        private static readonly PromptLibrary _library = new PromptLibrary();

        protected override void Execute()
        {
            if (!LicenseManager.Instance.HasTier2Access)
            {
                LicenseManager.ShowUpgradeMessage();
                return;
            }

            // Always refresh so newly created or edited prompts appear immediately
            // without requiring a Trados restart.
            _library.Refresh();
            var prompts = _library.GetQuickLauncherPrompts();

            if (prompts.Count == 0)
            {
                MessageBox.Show(
                    "No QuickLauncher prompts are configured.\n\n" +
                    "Set category: QuickLauncher in a prompt file's YAML frontmatter, " +
                    "or place the file in a folder named 'QuickLauncher' inside your prompt library.",
                    "Supervertaler \u2014 QuickLauncher",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Gather segment context once (before showing the menu)
            var editorController = SdlTradosStudio.Application.GetController<EditorController>();
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
                        // Use whichever side has a selection; prefer source
                        if (!string.IsNullOrWhiteSpace(srcSel))
                            selection = srcSel.Trim();
                        else if (!string.IsNullOrWhiteSpace(tgtSel))
                            selection = tgtSel.Trim();
                    }
                }
                catch { /* Selection API may not be available */ }

                try
                {
                    var file = doc.ActiveFile;
                    if (file != null)
                    {
                        sourceLang = file.SourceFile?.Language?.DisplayName ?? "";
                        targetLang = file.Language?.DisplayName ?? "";
                    }
                }
                catch { /* Language info may not be available */ }

                projectName = DocumentContextHelper.GetProjectName(doc);
                documentName = DocumentContextHelper.GetDocumentName(doc);
            }

            // Load settings once for surrounding segments count
            var settings = TermLensSettings.Load();
            var surroundingCount = settings?.AiSettings?.QuickLauncherSurroundingSegments ?? 5;

            // Build and show the context menu at the current cursor position.
            // Do NOT use a 'using' block or dispose on Closed — Show() is non-blocking
            // and Closed fires before item click handlers run, causing ObjectDisposedException.
            // ContextMenuStrip is small; GC handles it.
            var menu = new ContextMenuStrip();
            menu.ShowItemToolTips = true;

            // Header: "Supervertaler QuickLauncher" — opens Settings → Prompts tab
            var header = new ToolStripMenuItem("Supervertaler QuickLauncher");
            header.Font = new System.Drawing.Font(header.Font, System.Drawing.FontStyle.Bold);
            header.ToolTipText = "Click to open the Prompt Manager";
            header.Click += (s, e) =>
            {
                using (var form = new Settings.TermLensSettingsForm(
                    Settings.TermLensSettings.Load(), new Core.PromptLibrary(), defaultTab: 2))
                {
                    form.ShowDialog();
                }
            };
            menu.Items.Add(header);
            menu.Items.Add(new ToolStripSeparator());

            // Determine whether custom slot assignments exist
            var hasCustomSlots = settings?.AiSettings?.QuickLauncherSlots != null
                                 && settings.AiSettings.QuickLauncherSlots.Count > 0;

            for (int idx = 0; idx < prompts.Count; idx++)
            {
                var capturedPrompt = prompts[idx];
                var capturedSourceText = sourceText;
                var capturedTargetText = targetText;
                var capturedSelection = selection;
                var capturedSourceLang = sourceLang;
                var capturedTargetLang = targetLang;
                var capturedProjectName = projectName;
                var capturedDocumentName = documentName;
                var capturedDoc = doc;
                var capturedSurroundingCount = surroundingCount;

                var slotNum = idx + 1;
                var label = capturedPrompt.MenuLabel;
                var item = new ToolStripMenuItem(label);

                if (hasCustomSlots)
                {
                    // Show only custom slot assignments (no position-based fallback)
                    var shortcutDisplay = QuickLauncherSlotRunner.GetShortcutDisplay(
                        capturedPrompt.FilePath, settings?.AiSettings);
                    if (shortcutDisplay != null)
                        item.ShortcutKeyDisplayString = shortcutDisplay;
                }
                else if (slotNum <= 10)
                {
                    // No custom slots configured — auto-assign by position
                    var keyDigit = slotNum == 10 ? "0" : slotNum.ToString();
                    item.ShortcutKeyDisplayString = $"Ctrl+Alt+{keyDigit}";
                }
                if (!string.IsNullOrEmpty(capturedPrompt.Description))
                    item.ToolTipText = capturedPrompt.Description;

                item.Click += (s, e) =>
                {
                    var content = capturedPrompt.Content;

                    // Lazily gather expensive context only if the prompt actually uses it
                    var surroundingSegments = content.Contains("{{SURROUNDING_SEGMENTS}}")
                        ? DocumentContextHelper.FormatSurroundingSegments(capturedDoc, capturedSurroundingCount)
                        : null;

                    var projectText = content.Contains("{{PROJECT}}")
                        ? DocumentContextHelper.FormatProjectText(capturedDoc)
                        : null;

                    var tmMatchesText = content.Contains("{{TM_MATCHES}}")
                        ? PromptLibrary.FormatTmMatches(
                            DocumentContextHelper.GetTmMatches(capturedDoc), 70)
                        : null;

                    var expanded = PromptLibrary.ApplyVariables(
                        content,
                        capturedSourceLang, capturedTargetLang,
                        capturedSourceText, capturedTargetText, capturedSelection,
                        capturedProjectName, capturedDocumentName,
                        surroundingSegments, projectText, tmMatchesText);

                    // Build a compact display version for the chat bubble when {{PROJECT}} is used.
                    // The full expanded text is still sent to the AI — only the bubble is shortened.
                    string displayExpanded = null;
                    if (projectText != null)
                    {
                        var segCount = 0;
                        foreach (var line in projectText.Split('\n'))
                            if (line.TrimStart().StartsWith("[")) segCount++;

                        var placeholder = $"[source document \u2014 {segCount} segment{(segCount == 1 ? "" : "s")}]";
                        displayExpanded = PromptLibrary.ApplyVariables(
                            content,
                            capturedSourceLang, capturedTargetLang,
                            capturedSourceText, capturedTargetText, capturedSelection,
                            capturedProjectName, capturedDocumentName,
                            surroundingSegments, placeholder, tmMatchesText);
                    }

                    AiAssistantViewPart.RunQuickLauncherPrompt(expanded, displayExpanded, capturedPrompt.Name);
                };

                menu.Items.Add(item);
            }

            menu.Show(Cursor.Position);
        }
    }
}
