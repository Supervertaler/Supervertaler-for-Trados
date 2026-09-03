using System;
using System.Collections.Generic;
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
            var sourceLang = "";

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

                // Get source and target language display names for dialog labels
                try
                {
                    var langPair = doc.ActiveFile?.Language;
                    if (langPair != null)
                        targetLang = LanguageUtils.ShortenLanguageName(langPair.DisplayName);
                }
                catch { }

                try
                {
                    var srcFile = doc.ActiveFile?.SourceFile;
                    if (srcFile?.Language != null)
                        sourceLang = LanguageUtils.ShortenLanguageName(srcFile.Language.DisplayName);
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
            // Read the active bank from the shared settings instance rather
            // than a private Load(): the bank can be switched from the Assistant
            // toolbar, and this action must target whatever it says now.
            var activeBank = SettingsService.Current?.AiSettings?.ActiveMemoryBankName;
            if (string.IsNullOrWhiteSpace(activeBank))
                activeBank = UserDataPath.DefaultMemoryBankName;

            using (var dlg = new SuperMemoryQuickAddDialog(sourceTerm, targetTerm, activePromptName,
                targetLang, sourceLang, activeBank))
            {
                if (dlg.ShowDialog() != DialogResult.OK)
                    return;

                // In structured mode both fields are required; in raw-note
                // mode we're more lenient (at least one field + notes is OK).
                if (!dlg.SaveAsRawNote &&
                    (string.IsNullOrEmpty(dlg.Term) || string.IsNullOrEmpty(dlg.Correction)))
                {
                    MessageBox.Show(
                        "Both the source term and the target term are required for a structured article.\n\n" +
                        "If you want to save a free-form note instead, tick the \"Save as raw note\" checkbox.",
                        "Supervertaler \u2014 SuperMemory",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (dlg.SaveAsRawNote &&
                    string.IsNullOrEmpty(dlg.Term) && string.IsNullOrEmpty(dlg.Correction) &&
                    string.IsNullOrEmpty(dlg.Notes))
                {
                    MessageBox.Show(
                        "Please enter at least something \u2014 a term, a translation, or a note.",
                        "Supervertaler \u2014 SuperMemory",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // ── 1. Write .md to the active memory bank ───────────
                // Was: resolved silently from the active bank and named only in
                // the confirmation, i.e. after the write. The dialog now shows and
                // chooses the destination, so this just honours it.
                var bankName = dlg.SelectedBank;
                if (string.IsNullOrWhiteSpace(bankName))
                    bankName = activeBank;
                var vaultPath = UserDataPath.GetMemoryBankDir(bankName);

                bool mdWritten;
                if (dlg.SaveAsRawNote)
                {
                    mdWritten = WriteRawNote(vaultPath, dlg.Term, dlg.Correction, dlg.Notes);
                }
                else
                {
                    mdWritten = AppendTerminologyRow(vaultPath, dlg.Term, dlg.Correction, dlg.Notes);
                }

                // ── 2. Append to active prompt (if requested) ────────
                bool promptUpdated = false;
                if (dlg.AppendToPrompt && !dlg.SaveAsRawNote)
                {
                    // Only append to the prompt for structured articles – raw
                    // notes need AI processing first, so appending unprocessed
                    // content to the prompt would be premature.
                    promptUpdated = AppendToActivePrompt(dlg.Term, dlg.Correction, dlg.Notes);
                }

                // ── 3. Feedback ──────────────────────────────────────
                var msg = new StringBuilder();
                if (mdWritten)
                {
                    var isShared = string.Equals(bankName, MemoryBankReader.SharedBankName,
                        StringComparison.OrdinalIgnoreCase);
                    if (dlg.SaveAsRawNote)
                        msg.AppendLine($"\u2713  Saved note to reference/ in memory bank \"{bankName}\".");
                    else
                        msg.AppendLine($"\u2713  Added a row to terminology.md in memory bank \"{bankName}\".");
                    if (isShared)
                        msg.AppendLine("   This bank is loaded alongside every other one, so it applies to all your jobs.");
                }
                else
                {
                    msg.AppendLine($"\u26A0  Could not write to memory bank \"{bankName}\".");
                }

                if (dlg.AppendToPrompt && !dlg.SaveAsRawNote)
                {
                    if (promptUpdated)
                        msg.AppendLine("\u2713  Appended to active translation prompt.");
                    else
                        msg.AppendLine("\u26A0  Could not update the active prompt (no prompt selected or section not found).");
                }

                if (dlg.SaveAsRawNote && mdWritten)
                {
                    msg.AppendLine();
                    msg.AppendLine("Saved as reference material. Nothing reads it automatically - fold anything worth keeping into brief.md, terminology.md or style.md yourself.");
                }

                msg.AppendLine();
                if (!string.IsNullOrEmpty(dlg.Term) && !string.IsNullOrEmpty(dlg.Correction))
                    msg.AppendLine($"\"{dlg.Term}\" \u2192 \"{dlg.Correction}\"");
                else if (!string.IsNullOrEmpty(dlg.Term))
                    msg.AppendLine($"Term: \"{dlg.Term}\"");
                else if (!string.IsNullOrEmpty(dlg.Correction))
                    msg.AppendLine($"Term: \"{dlg.Correction}\"");

                MessageBox.Show(
                    msg.ToString().TrimEnd(),
                    "Supervertaler \u2014 SuperMemory",
                    MessageBoxButtons.OK,
                    mdWritten ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  Write a terminology .md article to the vault
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Appends one row to the bank's <c>terminology.md</c> table.
        ///
        /// This used to write a whole .md article per term into 02_TERMINOLOGY.
        /// That is the pattern the bank redesign removed: it produced 136 files
        /// for what is a 136-row table, and a wrong entry among 136 files is
        /// effectively invisible. One row in one table can be scanned, sorted and
        /// corrected in seconds - which is the only reason errors ever get found.
        ///
        /// The row is inserted after the LAST existing table row, so successive
        /// quick-adds accumulate in the table rather than scattering. If the file
        /// has no table yet (a bank converted from the old layout is prose), one
        /// is created at the end under its own heading.
        /// </summary>
        private static bool AppendTerminologyRow(string vaultPath, string term, string correction, string notes)
        {
            try
            {
                Directory.CreateDirectory(vaultPath);
                var path = Path.Combine(vaultPath, MemoryBankReader.TerminologyFile);

                var row = "| " + EscapeCell(term) + " | " + EscapeCell(correction) +
                          " | client | " + EscapeCell(notes) + " |";

                if (!File.Exists(path))
                {
                    var fresh = new StringBuilder();
                    fresh.AppendLine("# Terminology");
                    fresh.AppendLine();
                    fresh.AppendLine("| Source | Target | Scope | Note |");
                    fresh.AppendLine("|---|---|---|---|");
                    fresh.AppendLine(row);
                    File.WriteAllText(path, fresh.ToString(), new UTF8Encoding(false));
                    return true;
                }

                var lines = new List<string>(File.ReadAllLines(path));

                int lastRow = -1;
                for (int i = 0; i < lines.Count; i++)
                {
                    var t = lines[i].Trim();
                    if (t.StartsWith("|") && t.EndsWith("|") && t.Length > 1)
                        lastRow = i;
                }

                if (lastRow < 0)
                {
                    // Prose file (typically a converted bank): start a table at the
                    // end rather than trying to guess where one belongs.
                    lines.Add("");
                    lines.Add("## Quick-added terms");
                    lines.Add("");
                    lines.Add("| Source | Target | Scope | Note |");
                    lines.Add("|---|---|---|---|");
                    lines.Add(row);
                }
                else
                {
                    // The skeleton ships an empty placeholder row; fill it instead
                    // of leaving a blank line in the middle of the table.
                    var existing = lines[lastRow].Replace("|", "").Trim();
                    if (existing.Length == 0)
                        lines[lastRow] = row;
                    else
                        lines.Insert(lastRow + 1, row);
                }

                File.WriteAllLines(path, lines, new UTF8Encoding(false));
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Makes text safe for a Markdown table cell: a raw pipe would
        /// end the cell early and silently shift every column after it.</summary>
        private static string EscapeCell(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            return text.Replace("|", "\\|")
                       .Replace("\r", " ")
                       .Replace("\n", " ")
                       .Trim();
        }

        // ══════════════════════════════════════════════════════════════
        //  Write a background note to the bank's reference/ folder
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Creates a plain Markdown note in the bank's reference/ folder.
        ///
        /// <para>The unstructured alternative to <see cref="AppendTerminologyRow"/>.
        /// Nothing reads reference/ into a prompt - it is the audit trail, so a
        /// derived claim can be checked against what it came from. This doc used
        /// to say 00_INBOX and promise that Process Inbox would compile the note;
        /// the folder was renamed by the bank redesign and Process Inbox is not
        /// implemented in this plugin, so both halves were wrong.</para>
        /// </summary>
        private static bool WriteRawNote(string vaultPath, string term, string correction, string notes)
        {
            try
            {
                var inboxDir = Path.Combine(vaultPath, MemoryBankReader.ReferenceFolder);
                Directory.CreateDirectory(inboxDir);

                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var label = !string.IsNullOrEmpty(term) ? SanitiseFileName(term) : "quick-note";
                var fileName = $"quick-add-{label}-{stamp}.md";
                var filePath = Path.Combine(inboxDir, fileName);

                var sb = new StringBuilder();
                sb.AppendLine("# Quick Add note");
                sb.AppendLine();
                sb.AppendLine($"*Added via Quick Add on {DateTime.Now:yyyy-MM-dd HH:mm}*");
                sb.AppendLine();

                if (!string.IsNullOrEmpty(term))
                {
                    sb.AppendLine($"**Source term:** {term}");
                    sb.AppendLine();
                }
                if (!string.IsNullOrEmpty(correction))
                {
                    sb.AppendLine($"**Target term / translation:** {correction}");
                    sb.AppendLine();
                }
                if (!string.IsNullOrEmpty(notes))
                {
                    sb.AppendLine("**Notes:**");
                    sb.AppendLine(notes);
                    sb.AppendLine();
                }

                sb.AppendLine("---");
                sb.AppendLine("*Captured via Quick Add (Ctrl+Alt+M). Nothing reads this folder into a prompt -");
                sb.AppendLine("it is reference material. To make it count, fold it into brief.md, terminology.md");
                sb.AppendLine("or style.md in this bank.*");

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

                // Find the terminology table – look for a line like
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
                        // End of table – stop here
                        break;
                    }
                    else if (inTable)
                    {
                        // Non-table, non-blank line after table – stop
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
                    var settings = SettingsService.Current;
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
