using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Sdl.TranslationStudioAutomation.IntegrationApi.Presentation.DefaultLocations;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Licensing;
using Supervertaler.Trados.Models;
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
            if (!LicenseManager.Instance.HasAssistantAccess)
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
            // Do NOT use a 'using' block or dispose on Closed – Show() is non-blocking
            // and Closed fires before item click handlers run, causing ObjectDisposedException.
            // ContextMenuStrip is small; GC handles it.
            var menu = new ContextMenuStrip();
            menu.ShowItemToolTips = true;

            // Header: "Supervertaler QuickLauncher" – opens Settings → Prompts tab
            var header = new ToolStripMenuItem("Supervertaler QuickLauncher");
            header.Font = new System.Drawing.Font(header.Font, System.Drawing.FontStyle.Bold);
            header.ToolTipText = "Click to open the Prompt Manager";
            header.Click += (s, e) =>
            {
                using (var form = new Settings.TermLensSettingsForm(
                    Settings.TermLensSettings.Load(), new Core.PromptLibrary(), defaultTab: 3))
                {
                    form.ShowDialog();
                }
            };
            menu.Items.Add(header);
            menu.Items.Add(new ToolStripSeparator());

            // Determine whether custom slot assignments exist
            var hasCustomSlots = settings?.AiSettings?.QuickLauncherSlots != null
                                 && settings.AiSettings.QuickLauncherSlots.Count > 0;

            // Build a position map for keyboard shortcut numbering (flat order).
            // This keeps Ctrl+Alt+1..0 consistent with QuickLauncherSlotRunner.
            var slotPositions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < prompts.Count; i++)
            {
                if (!string.IsNullOrEmpty(prompts[i].FilePath))
                    slotPositions[prompts[i].FilePath] = i + 1; // 1-based
            }

            // Build folder tree and populate the menu recursively
            var folderTree = _library.GetQuickLauncherFolderStructure();

            // Determine which folders should render as flat sections
            var flatFolders = settings?.AiSettings?.QuickLauncherFlatFolders ?? new List<string>();

            // Add subfolders first (Default pinned first by GetQuickLauncherFolderStructure)
            foreach (var child in folderTree.Children)
            {
                // Tree node tags use the full relative path (e.g. "QuickLauncher/Default")
                // while GetQuickLauncherFolderStructure strips the prefix (e.g. "Default").
                // Check both forms so the setting always matches.
                var rel = child.RelativePath ?? "";
                var fullRel = "QuickLauncher/" + rel;
                bool isFlat = flatFolders.Contains(rel) || flatFolders.Contains(fullRel);

                if (isFlat)
                {
                    // Flat section: separator, bold header, then items directly in the menu
                    var items = new List<ToolStripItem>();
                    PopulateFlatSection(items, child, flatFolders, slotPositions,
                        hasCustomSlots, settings, doc, sourceText, targetText, selection,
                        sourceLang, targetLang, projectName, documentName, surroundingCount);

                    if (items.Count > 0)
                    {
                        // Only add separator if the last item isn't already one
                        if (menu.Items.Count > 0 && !(menu.Items[menu.Items.Count - 1] is ToolStripSeparator))
                            menu.Items.Add(new ToolStripSeparator());

                        var sectionHeader = new ToolStripMenuItem(child.Name.ToUpperInvariant() + ":");
                        sectionHeader.Font = new System.Drawing.Font(sectionHeader.Font, System.Drawing.FontStyle.Bold);
                        sectionHeader.Enabled = false;
                        menu.Items.Add(sectionHeader);

                        foreach (var item in items)
                            menu.Items.Add(item);
                    }
                }
                else
                {
                    // Expandable submenu (existing behaviour)
                    var subMenu = new ToolStripMenuItem(child.Name);
                    PopulateFolderMenu(subMenu.DropDownItems, child, slotPositions,
                        hasCustomSlots, settings, doc, sourceText, targetText, selection,
                        sourceLang, targetLang, projectName, documentName, surroundingCount);

                    if (subMenu.DropDownItems.Count > 0)
                        menu.Items.Add(subMenu);
                }
            }

            // Add top-level prompts (not in any subfolder)
            if (folderTree.Prompts.Count > 0)
            {
                if (folderTree.Children.Count > 0)
                    menu.Items.Add(new ToolStripSeparator());

                foreach (var p in folderTree.Prompts)
                {
                    slotPositions.TryGetValue(p.FilePath ?? "", out var slotNum);
                    menu.Items.Add(CreatePromptMenuItem(p, slotNum, hasCustomSlots, settings,
                        doc, sourceText, targetText, selection,
                        sourceLang, targetLang, projectName, documentName, surroundingCount));
                }
            }

            menu.Show(Cursor.Position);
        }

        /// <summary>
        /// Collects menu items from a folder (and its children) into a flat list
        /// for rendering as a section with a bold header.
        /// Child subfolders that are also marked as flat get their own section header;
        /// others are rendered as expandable submenus.
        /// </summary>
        private void PopulateFlatSection(
            List<ToolStripItem> items,
            PromptFolderNode folder,
            List<string> flatFolders,
            Dictionary<string, int> slotPositions,
            bool hasCustomSlots,
            TermLensSettings settings,
            Sdl.TranslationStudioAutomation.IntegrationApi.IStudioDocument doc,
            string sourceText, string targetText, string selection,
            string sourceLang, string targetLang,
            string projectName, string documentName, int surroundingCount)
        {
            // Add prompts in this folder first
            foreach (var p in folder.Prompts)
            {
                slotPositions.TryGetValue(p.FilePath ?? "", out var slotNum);
                items.Add(CreatePromptMenuItem(p, slotNum, hasCustomSlots, settings,
                    doc, sourceText, targetText, selection,
                    sourceLang, targetLang, projectName, documentName, surroundingCount));
            }

            // Then child subfolders – flat children get their own section header
            foreach (var child in folder.Children)
            {
                var childRel = child.RelativePath ?? "";
                var childFullRel = "QuickLauncher/" + childRel;
                bool childIsFlat = flatFolders.Contains(childRel) || flatFolders.Contains(childFullRel);

                if (childIsFlat)
                {
                    var childItems = new List<ToolStripItem>();
                    PopulateFlatSection(childItems, child, flatFolders, slotPositions,
                        hasCustomSlots, settings, doc, sourceText, targetText, selection,
                        sourceLang, targetLang, projectName, documentName, surroundingCount);

                    if (childItems.Count > 0)
                    {
                        items.Add(new ToolStripSeparator());

                        var childHeader = new ToolStripMenuItem(child.Name.ToUpperInvariant() + ":");
                        childHeader.Font = new System.Drawing.Font(childHeader.Font, System.Drawing.FontStyle.Bold);
                        childHeader.Enabled = false;
                        items.Add(childHeader);

                        foreach (var ci in childItems)
                            items.Add(ci);
                    }
                }
                else
                {
                    var subMenu = new ToolStripMenuItem(child.Name);
                    PopulateFolderMenu(subMenu.DropDownItems, child, slotPositions,
                        hasCustomSlots, settings, doc, sourceText, targetText, selection,
                        sourceLang, targetLang, projectName, documentName, surroundingCount);

                    if (subMenu.DropDownItems.Count > 0)
                        items.Add(subMenu);
                }
            }
        }

        /// <summary>
        /// Recursively populates a menu/submenu from a PromptFolderNode.
        /// </summary>
        private void PopulateFolderMenu(
            ToolStripItemCollection parent,
            PromptFolderNode folder,
            Dictionary<string, int> slotPositions,
            bool hasCustomSlots,
            TermLensSettings settings,
            Sdl.TranslationStudioAutomation.IntegrationApi.IStudioDocument doc,
            string sourceText, string targetText, string selection,
            string sourceLang, string targetLang,
            string projectName, string documentName, int surroundingCount)
        {
            // Add child subfolders first
            foreach (var child in folder.Children)
            {
                var subMenu = new ToolStripMenuItem(child.Name);
                PopulateFolderMenu(subMenu.DropDownItems, child, slotPositions,
                    hasCustomSlots, settings, doc, sourceText, targetText, selection,
                    sourceLang, targetLang, projectName, documentName, surroundingCount);

                if (subMenu.DropDownItems.Count > 0)
                    parent.Add(subMenu);
            }

            // Separator between subfolders and prompts
            if (folder.Children.Count > 0 && folder.Prompts.Count > 0)
                parent.Add(new ToolStripSeparator());

            // Add prompts in this folder
            foreach (var p in folder.Prompts)
            {
                slotPositions.TryGetValue(p.FilePath ?? "", out var slotNum);
                parent.Add(CreatePromptMenuItem(p, slotNum, hasCustomSlots, settings,
                    doc, sourceText, targetText, selection,
                    sourceLang, targetLang, projectName, documentName, surroundingCount));
            }
        }

        /// <summary>
        /// Creates a single ToolStripMenuItem for a QuickLauncher prompt,
        /// including shortcut display and click handler.
        ///
        /// When the prompt is configured with two or more
        /// <see cref="PromptTemplate.QuickLauncherModes"/> (e.g. both
        /// "assistant" and "clipboard"), the item gets a cascading submenu
        /// that lets the user pick the destination at runtime. The default
        /// mode is rendered first so the natural keyboard flow
        /// (Right Arrow then Enter, or hover-then-Enter) fires it.
        ///
        /// Single-mode prompts (the common case — "assistant" only) keep the
        /// previous flat behaviour: click fires the single mode directly.
        /// </summary>
        private ToolStripMenuItem CreatePromptMenuItem(
            PromptTemplate prompt, int slotNum,
            bool hasCustomSlots, TermLensSettings settings,
            Sdl.TranslationStudioAutomation.IntegrationApi.IStudioDocument doc,
            string sourceText, string targetText, string selection,
            string sourceLang, string targetLang,
            string projectName, string documentName, int surroundingCount)
        {
            var item = new ToolStripMenuItem(prompt.MenuLabel);

            if (hasCustomSlots)
            {
                var shortcutDisplay = QuickLauncherSlotRunner.GetShortcutDisplay(
                    prompt.FilePath, settings?.AiSettings);
                if (shortcutDisplay != null)
                    item.ShortcutKeyDisplayString = shortcutDisplay;
            }
            else if (slotNum >= 1 && slotNum <= 10)
            {
                var keyDigit = slotNum == 10 ? "0" : slotNum.ToString();
                item.ShortcutKeyDisplayString = $"Ctrl+Alt+{keyDigit}";
            }

            if (!string.IsNullOrEmpty(prompt.Description))
                item.ToolTipText = prompt.Description;

            // Capture for closure
            var capturedPrompt = prompt;
            var capturedDoc = doc;
            var capturedSourceText = sourceText;
            var capturedTargetText = targetText;
            var capturedSelection = selection;
            var capturedSourceLang = sourceLang;
            var capturedTargetLang = targetLang;
            var capturedProjectName = projectName;
            var capturedDocumentName = documentName;
            var capturedSurroundingCount = surroundingCount;
            var capturedSettings = settings;

            if (capturedPrompt.HasMultipleQuickLauncherModes)
            {
                // Build a cascading submenu with one entry per configured mode.
                // Order: default mode first, then the others in declared order.
                // ContextMenuStrip auto-renders the right-arrow indicator on
                // items with DropDownItems; right-arrow expansion + mnemonic
                // letters work out of the box.
                var orderedModes = new List<string>();
                if (!string.IsNullOrEmpty(capturedPrompt.DefaultMode) &&
                    capturedPrompt.QuickLauncherModes.Contains(capturedPrompt.DefaultMode))
                {
                    orderedModes.Add(capturedPrompt.DefaultMode);
                }
                foreach (var m in capturedPrompt.QuickLauncherModes)
                {
                    if (!orderedModes.Contains(m))
                        orderedModes.Add(m);
                }

                foreach (var mode in orderedModes)
                {
                    var subItem = new ToolStripMenuItem(GetModeMenuLabel(mode, capturedSettings));
                    var tooltip = GetModeTooltip(mode);
                    if (!string.IsNullOrEmpty(tooltip))
                        subItem.ToolTipText = tooltip;
                    var capturedMode = mode;
                    subItem.Click += (s, e) =>
                    {
                        RunPromptInMode(capturedPrompt, capturedMode, capturedSettings,
                            capturedDoc, capturedSourceText, capturedTargetText, capturedSelection,
                            capturedSourceLang, capturedTargetLang,
                            capturedProjectName, capturedDocumentName, capturedSurroundingCount);
                    };
                    item.DropDownItems.Add(subItem);
                }
            }
            else
            {
                // Single-mode (the default for almost all existing prompts):
                // flat item that fires the single configured mode on click.
                item.Click += (s, e) =>
                {
                    var mode = (capturedPrompt.QuickLauncherModes != null && capturedPrompt.QuickLauncherModes.Count == 1)
                        ? capturedPrompt.QuickLauncherModes[0]
                        : "assistant";
                    RunPromptInMode(capturedPrompt, mode, capturedSettings,
                        capturedDoc, capturedSourceText, capturedTargetText, capturedSelection,
                        capturedSourceLang, capturedTargetLang,
                        capturedProjectName, capturedDocumentName, capturedSurroundingCount);
                };
            }

            return item;
        }

        /// <summary>
        /// User-facing label for a single QuickLauncher mode. Uses an
        /// ampersand mnemonic so the user can pick the mode with a single
        /// keystroke once the submenu is open (S = Send, C = Copy).
        /// </summary>
        private static string GetModeMenuLabel(string mode, TermLensSettings settings)
        {
            switch ((mode ?? "").ToLowerInvariant())
            {
                case "clipboard":
                    return "&Copy prompt to clipboard";
                case "assistant":
                default:
                    var target = settings?.AiSettings?.QuickLauncherTarget ?? "TradosAssistant";
                    return string.Equals(target, "WorkbenchSidekick", StringComparison.OrdinalIgnoreCase)
                        ? "&Send to Supervertaler Workbench Chat"
                        : "&Send to Supervertaler";
            }
        }

        private static string GetModeTooltip(string mode)
        {
            switch ((mode ?? "").ToLowerInvariant())
            {
                case "clipboard":
                    return "Copy the expanded prompt to the system clipboard so you can paste it into an external chat (e.g. claude.ai).";
                case "assistant":
                    return "Send the prompt to the AI Assistant chat (in Trados or in Supervertaler Workbench's Chat panel, per your global setting).";
                default:
                    return null;
            }
        }

        /// <summary>
        /// Expand the prompt's variables against the captured segment context,
        /// then dispatch the result to the requested destination.
        ///
        /// Modes:
        ///   "assistant"  — current behaviour: route to in-Trados AI Assistant
        ///                  or Workbench Sidekick per the global setting.
        ///   "clipboard"  — copy the expanded prompt to the system clipboard
        ///                  and show a transient status message via the
        ///                  AI Assistant log (no chat round-trip).
        /// </summary>
        private static void RunPromptInMode(
            PromptTemplate prompt, string mode, TermLensSettings settings,
            Sdl.TranslationStudioAutomation.IntegrationApi.IStudioDocument doc,
            string sourceText, string targetText, string selection,
            string sourceLang, string targetLang,
            string projectName, string documentName, int surroundingCount)
        {
            // Text transforms bypass mode entirely — they apply find/replace
            // to the target segment directly without going anywhere else.
            if (prompt.IsTransform)
            {
                var result = AiAssistantViewPart.RunTextTransform(prompt);
                AiAssistantViewPart.ShowTransformResult(prompt.Name, result);
                return;
            }

            var content = prompt.Content;

            // Lazily gather expensive context only if the prompt actually uses it
            var surroundingSegments = content.Contains("{{SURROUNDING_SEGMENTS}}")
                ? DocumentContextHelper.FormatSurroundingSegments(doc, surroundingCount)
                : null;

            var projectText = content.Contains("{{PROJECT}}")
                ? DocumentContextHelper.FormatProjectText(doc)
                : null;

            var tmMatchesText = content.Contains("{{TM_MATCHES}}")
                ? PromptLibrary.FormatTmMatches(
                    DocumentContextHelper.GetTmMatches(doc), 70)
                : null;

            var expanded = PromptLibrary.ApplyVariables(
                content,
                sourceLang, targetLang,
                sourceText, targetText, selection,
                projectName, documentName,
                surroundingSegments, projectText, tmMatchesText);

            string displayExpanded = null;
            if (projectText != null)
            {
                var segCount = 0;
                foreach (var line in projectText.Split('\n'))
                    if (line.TrimStart().StartsWith("[")) segCount++;

                var placeholder = "[source document — " + segCount + " segment" + (segCount == 1 ? "" : "s") + "]";
                displayExpanded = PromptLibrary.ApplyVariables(
                    content,
                    sourceLang, targetLang,
                    sourceText, targetText, selection,
                    projectName, documentName,
                    surroundingSegments, placeholder, tmMatchesText);
            }

            switch ((mode ?? "assistant").ToLowerInvariant())
            {
                case "clipboard":
                    DispatchToClipboard(expanded, prompt.Name);
                    return;

                case "assistant":
                default:
                    DispatchToAssistant(prompt.Name, expanded, displayExpanded, settings);
                    return;
            }
        }

        /// <summary>
        /// Copy the expanded prompt to the system clipboard. Uses a brief
        /// retry because Clipboard.SetText can occasionally throw
        /// ExternalException when another process holds the clipboard
        /// (the classic Office / TeamViewer race). Silent on success —
        /// the menu closing is itself the user's confirmation that the
        /// action fired, and they're typically alt-tabbing to paste it
        /// somewhere within a second. Only surfaces a dialog if all
        /// retries fail and the clipboard genuinely couldn't be written.
        /// </summary>
        private static void DispatchToClipboard(string expanded, string promptName)
        {
            const int maxAttempts = 3;
            Exception lastErr = null;
            for (int i = 0; i < maxAttempts; i++)
            {
                try
                {
                    Clipboard.SetText(expanded ?? "", TextDataFormat.UnicodeText);
                    return;
                }
                catch (Exception ex)
                {
                    lastErr = ex;
                    System.Threading.Thread.Sleep(50);
                }
            }

            MessageBox.Show(
                "Could not copy the prompt to the clipboard.\n\n" +
                (lastErr?.Message ?? "Unknown error"),
                "Supervertaler — QuickLauncher",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        /// <summary>
        /// Route the expanded prompt to the user's configured AI Assistant
        /// destination (in-Trados or Workbench Sidekick), with the original
        /// silent-fallback behaviour on Sidekick failures.
        /// </summary>
        private static void DispatchToAssistant(
            string promptName, string expanded, string displayExpanded, TermLensSettings settings)
        {
            var target = settings?.AiSettings?.QuickLauncherTarget ?? "TradosAssistant";
            if (string.Equals(target, "WorkbenchSidekick", StringComparison.OrdinalIgnoreCase))
            {
                var (ok, _) = Core.WorkbenchBridgeClient.RunPrompt(
                    expanded, displayExpanded ?? expanded, promptName);
                if (ok) return;
                // Fell through – fall back to the in-Trados Assistant.
            }
            AiAssistantViewPart.RunQuickLauncherPrompt(expanded, displayExpanded, promptName);
        }
    }
}
