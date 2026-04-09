using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
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
    /// Editor context menu action: "Add to SuperMemory".
    /// Captures the selected text and a correction, writes a .md article to the
    /// SuperMemory vault, and optionally appends a row to the active translation
    /// prompt's terminology table so the next Ctrl+T picks it up immediately.
    /// </summary>
    [Action("Supervertaler_SuperMemoryQuickAdd", typeof(EditorController),
        Name = "Add to SuperMemory",
        Description = "Quick-add a term or correction pattern to your SuperMemory knowledge base")]
    [ActionLayout(
        typeof(TranslationStudioDefaultContextMenus.EditorDocumentContextMenuLocation), 8,
        DisplayType.Default, "", false)]
    [Shortcut(Keys.Control | Keys.Alt | Keys.M)]
    public class SuperMemoryQuickAddAction : AbstractAction
    {
        /// <summary>Terminology sub-folder inside every memory bank.</summary>
        private const string TermFolder = "02_TERMINOLOGY";

        protected override void Execute()
        {
            if (!LicenseManager.Instance.HasAssistantAccess)
            {
                LicenseManager.ShowUpgradeMessage();
                return;
            }

            // ── Gather context from editor ───────────────────────────
            var editorController = SdlTradosStudio.Application.GetController<EditorController>();
            var doc = editorController?.ActiveDocument;

            var sourceTerm = "";
            var targetTerm = "";
            var targetLang = "";

            if (doc != null)
            {
                // Try to get selection first; fall back to full segment text
                try
                {
                    var sel = doc.Selection;
                    if (sel != null)
                    {
                        var selSource = sel.Source?.ToString()?.Trim();
                        var selTarget = sel.Target?.ToString()?.Trim();
                        if (!string.IsNullOrEmpty(selSource))
                            sourceTerm = selSource;
                        if (!string.IsNullOrEmpty(selTarget))
                            targetTerm = selTarget;
                    }
                }
                catch { /* selection API can throw in some states */ }

                // If no selection, try word at cursor from source
                if (string.IsNullOrEmpty(sourceTerm))
                {
                    sourceTerm = doc.ActiveSegmentPair?.Source != null
                        ? SegmentTagHandler.GetFinalText(doc.ActiveSegmentPair.Source) : "";
                }

                // Get target language for dialog label
                try
                {
                    var langPair = doc.ActiveFile?.Language;
                    if (langPair != null)
                        targetLang = LanguageUtils.ShortenLanguageName(langPair.DisplayName);
                }
                catch { }
            }

            // ── Resolve active prompt name for display ─────────────────
            string activePromptName = null;
            try
            {
                var promptPath = ResolveActivePromptPath();
                if (!string.IsNullOrEmpty(promptPath))
                {
                    // Extract display name from filename (strip extension and path)
                    activePromptName = Path.GetFileNameWithoutExtension(promptPath);
                }
            }
            catch { }

            // ── Show dialog ──────────────────────────────────────────
            using (var dlg = new SuperMemoryQuickAddDialog(sourceTerm, targetTerm, activePromptName, targetLang))
            {
                if (dlg.ShowDialog() != DialogResult.OK)
                    return;

                if (string.IsNullOrEmpty(dlg.Term) || string.IsNullOrEmpty(dlg.Correction))
                {
                    MessageBox.Show(
                        "Both the term and its correction are required.",
                        "Supervertaler — SuperMemory",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // ── 1. Write .md to the active memory bank ───────────
                // Resolve the active bank from settings so Quick Add always writes
                // to the same vault the AI Assistant is currently reading from.
                var qaSettings = TermLensSettings.Load();
                var bankName = qaSettings?.AiSettings?.ActiveMemoryBankName;
                if (string.IsNullOrWhiteSpace(bankName))
                    bankName = UserDataPath.DefaultMemoryBankName;
                var vaultPath = UserDataPath.GetMemoryBankDir(bankName);
                bool mdWritten = WriteTermArticle(vaultPath, dlg.Term, dlg.Correction, dlg.Notes);

                // ── 2. Append to active prompt (if requested) ────────
                bool promptUpdated = false;
                if (dlg.AppendToPrompt)
                {
                    promptUpdated = AppendToActivePrompt(dlg.Term, dlg.Correction, dlg.Notes);
                }

                // ── 3. Feedback ──────────────────────────────────────
                var msg = new StringBuilder();
                if (mdWritten)
                    msg.AppendLine($"✓  Added to memory bank \"{bankName}\".");
                else
                    msg.AppendLine($"⚠  Could not write to memory bank \"{bankName}\".");

                if (dlg.AppendToPrompt)
                {
                    if (promptUpdated)
                        msg.AppendLine("✓  Appended to active translation prompt.");
                    else
                        msg.AppendLine("⚠  Could not update the active prompt (no prompt selected or section not found).");
                }

                msg.AppendLine();
                msg.AppendLine($"\"{dlg.Term}\" → \"{dlg.Correction}\"");

                MessageBox.Show(
                    msg.ToString().TrimEnd(),
                    "Supervertaler — SuperMemory",
                    MessageBoxButtons.OK,
                    mdWritten ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  Write a terminology .md article to the vault
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Creates a Markdown file in the SuperMemory vault's 02_TERMINOLOGY folder.
        /// Uses the same YAML frontmatter format as existing articles.
        /// </summary>
        private static bool WriteTermArticle(string vaultPath, string term, string correction, string notes)
        {
            try
            {
                var termDir = Path.Combine(vaultPath, TermFolder);
                Directory.CreateDirectory(termDir);

                // Build a safe filename: "e-mail vs email.md"
                var safeTerm = SanitiseFileName(term);
                var safeCorrection = SanitiseFileName(correction);
                var fileName = $"{safeTerm} vs {safeCorrection}.md";
                var filePath = Path.Combine(termDir, fileName);

                // Don't overwrite existing articles silently
                if (File.Exists(filePath))
                {
                    // Append a timestamp to make it unique
                    var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    fileName = $"{safeTerm} vs {safeCorrection} ({stamp}).md";
                    filePath = Path.Combine(termDir, fileName);
                }

                var today = DateTime.Now.ToString("yyyy-MM-dd");
                var sb = new StringBuilder();
                sb.AppendLine("---");
                sb.AppendLine($"term_source: \"{EscapeYaml(term)}\"");
                sb.AppendLine($"term_target: \"{EscapeYaml(correction)}\"");
                sb.AppendLine("source_lang: \"*\"");
                sb.AppendLine("target_lang: \"en-GB\"");
                sb.AppendLine("domain: \"[[General]]\"");
                sb.AppendLine("clients: []");
                sb.AppendLine("status: \"approved\"");
                sb.AppendLine($"last_updated: {today}");
                sb.AppendLine("---");
                sb.AppendLine();
                sb.AppendLine($"# {term} \u2192 {correction}");
                sb.AppendLine();
                sb.AppendLine("## Preferred form");
                sb.AppendLine($"**{correction}**");
                sb.AppendLine();

                if (!string.IsNullOrEmpty(notes))
                {
                    sb.AppendLine("## Context and usage");
                    sb.AppendLine($"- {notes}");
                    sb.AppendLine();
                }

                sb.AppendLine("## Sources");
                sb.AppendLine("- Added via Supervertaler Quick Add");
                sb.AppendLine();

                File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(false));
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  Append a row to the active translation prompt
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Finds the "TERMINOLOGY" table in the active Batch Translate prompt
        /// and appends a new row. The prompt is a plain .md file that Trados
        /// reads fresh from disk on every Ctrl+T, so the change takes effect
        /// immediately.
        /// </summary>
        private static bool AppendToActivePrompt(string term, string correction, string notes)
        {
            try
            {
                var fullPath = ResolveActivePromptPath();
                if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
                    return false;

                var content = File.ReadAllText(fullPath, Encoding.UTF8);

                // Find the terminology table — look for a line like
                //   "| Source term | Correct target | Notes |"
                // or the section header "TERMINOLOGY" followed by a table.
                // We'll insert after the last table row before the next blank line or section.

                // Strategy: find the TERMINOLOGY section, then find the last "|...|" row
                var termSectionIdx = content.IndexOf("TERMINOLOGY", StringComparison.OrdinalIgnoreCase);
                if (termSectionIdx < 0)
                    return false;

                // Find lines from that section onward
                var afterSection = content.Substring(termSectionIdx);
                var lines = afterSection.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

                // Walk forward to find the table, then find the last row
                int lastTableRowOffset = -1;
                int lastTableRowLength = 0;
                bool inTable = false;
                int charOffset = termSectionIdx;

                foreach (var line in lines)
                {
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("|"))
                    {
                        inTable = true;
                        lastTableRowOffset = charOffset;
                        lastTableRowLength = line.Length;
                    }
                    else if (inTable && string.IsNullOrWhiteSpace(trimmed))
                    {
                        // End of table — stop here
                        break;
                    }
                    else if (inTable)
                    {
                        // Non-table, non-blank line after table — stop
                        break;
                    }

                    // Advance past this line + its line ending
                    charOffset += line.Length;
                    // Account for the line ending that was consumed by Split
                    var remaining = content.Substring(charOffset);
                    if (remaining.StartsWith("\r\n"))
                        charOffset += 2;
                    else if (remaining.StartsWith("\n"))
                        charOffset += 1;
                }

                if (lastTableRowOffset < 0)
                    return false;

                // Build the new row
                var notesCell = string.IsNullOrEmpty(notes) ? "" : notes;
                var newRow = $"| {term} | {correction} | {notesCell} |";

                // Insert after the last table row
                var insertAt = lastTableRowOffset + lastTableRowLength;
                // Determine the line ending used
                var lineEnding = "\n";
                if (insertAt < content.Length && content.Substring(insertAt).StartsWith("\r\n"))
                    lineEnding = "\r\n";

                var updated = content.Substring(0, insertAt) + lineEnding + newRow + content.Substring(insertAt);
                File.WriteAllText(fullPath, updated, new UTF8Encoding(false));
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  Helpers
        // ══════════════════════════════════════════════════════════════

        /// <summary>Remove characters that are invalid in Windows file names.</summary>
        private static string SanitiseFileName(string input)
        {
            if (string.IsNullOrEmpty(input)) return "term";
            var invalid = new Regex("[" + Regex.Escape(new string(Path.GetInvalidFileNameChars())) + "]");
            var safe = invalid.Replace(input, "").Trim();
            // Limit length
            if (safe.Length > 60) safe = safe.Substring(0, 60).Trim();
            return string.IsNullOrEmpty(safe) ? "term" : safe;
        }

        /// <summary>Escape double quotes for YAML string values.</summary>
        private static string EscapeYaml(string input)
        {
            return (input ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        /// <summary>
        /// Resolves the full path to the active translation prompt for the current project.
        /// Checks per-project settings first, then falls back to global SelectedPromptPath.
        /// Returns null if no prompt is configured.
        /// </summary>
        private static string ResolveActivePromptPath()
        {
            try
            {
                string relativePath = null;

                // 1. Check per-project override
                var projectPath = TermLensEditorViewPart.GetCurrentProjectPath();
                if (!string.IsNullOrEmpty(projectPath))
                {
                    var ps = ProjectSettings.Load(projectPath);
                    if (ps != null && !string.IsNullOrEmpty(ps.ActivePromptPath))
                        relativePath = ps.ActivePromptPath;
                }

                // 2. Fall back to global setting
                if (string.IsNullOrEmpty(relativePath))
                {
                    var settings = TermLensSettings.Load();
                    relativePath = settings?.AiSettings?.SelectedPromptPath;
                }

                if (string.IsNullOrEmpty(relativePath))
                    return null;

                var fullPath = Path.Combine(UserDataPath.PromptLibraryDir, relativePath);
                return File.Exists(fullPath) ? fullPath : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
