using System;
using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Sdl.TranslationStudioAutomation.IntegrationApi.Presentation.DefaultLocations;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Licensing;

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
                    "Set category: QuickLauncher in a .svprompt file's YAML frontmatter, " +
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

            if (doc != null)
            {
                sourceText = doc.ActiveSegmentPair?.Source?.ToString() ?? "";
                targetText = doc.ActiveSegmentPair?.Target?.ToString() ?? "";

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
            }

            // Build and show the context menu at the current cursor position.
            // Do NOT use a 'using' block or dispose on Closed — Show() is non-blocking
            // and Closed fires before item click handlers run, causing ObjectDisposedException.
            // ContextMenuStrip is small; GC handles it.
            var menu = new ContextMenuStrip();

            foreach (var prompt in prompts)
            {
                var capturedPrompt = prompt;
                var capturedSourceText = sourceText;
                var capturedTargetText = targetText;
                var capturedSelection = selection;
                var capturedSourceLang = sourceLang;
                var capturedTargetLang = targetLang;

                var item = new ToolStripMenuItem(capturedPrompt.MenuLabel);
                if (!string.IsNullOrEmpty(capturedPrompt.Description))
                    item.ToolTipText = capturedPrompt.Description;

                item.Click += (s, e) =>
                {
                    var expanded = PromptLibrary.ApplyVariables(
                        capturedPrompt.Content,
                        capturedSourceLang, capturedTargetLang,
                        capturedSourceText, capturedTargetText, capturedSelection);

                    AiAssistantViewPart.RunQuickLauncherPrompt(expanded);
                };

                menu.Items.Add(item);
            }

            menu.Show(Cursor.Position);
        }
    }
}
