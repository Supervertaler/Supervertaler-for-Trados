using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Lightweight markdown-to-RTF converter for the Supervertaler Assistant chat bubbles.
    /// Handles common LLM output: bold, italic, inline code, code blocks, headings,
    /// bullet/numbered lists, and tables.
    /// </summary>
    public static class MarkdownToRtf
    {
        // RTF header: font table (Segoe UI + Consolas), color table, default formatting
        private const string RtfHeader =
            @"{\rtf1\ansi\deff0" +
            @"{\fonttbl{\f0 Segoe UI;}{\f1 Consolas;}}" +
            @"{\colortbl;\red30\green30\blue30;\red100\green100\blue100;\red43\green43\blue43;}" +
            @"\f0\fs18\cf1 ";

        // Font size constants (in half-points)
        private const string BodySize = @"\fs18";      // 9pt
        private const string H1Size = @"\fs24";         // 12pt
        private const string H2Size = @"\fs22";         // 11pt
        private const string H3Size = @"\fs20";         // 10pt
        private const string CodeSize = @"\fs17";       // 8.5pt
        private const string CodeFont = @"\f1";         // Consolas
        private const string CodeColor = @"\cf3";       // dark gray

        /// <summary>
        /// Converts a markdown string to RTF for rendering in a RichTextBox.
        /// </summary>
        public static string Convert(string markdown)
        {
            if (string.IsNullOrEmpty(markdown))
                return RtfHeader + "}";

            var sb = new StringBuilder(markdown.Length * 2);
            sb.Append(RtfHeader);

            var lines = markdown.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            var inCodeBlock = false;
            var codeBlockLines = new List<string>();
            var tableLines = new List<string>();
            var firstBlock = true;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                // ─── Code block handling ────────────────────────────
                if (line.TrimStart().StartsWith("```"))
                {
                    if (!inCodeBlock)
                    {
                        // Flush any pending table
                        FlushTable(sb, tableLines, ref firstBlock);
                        inCodeBlock = true;
                        codeBlockLines.Clear();
                    }
                    else
                    {
                        // End of code block — flush it
                        FlushCodeBlock(sb, codeBlockLines, ref firstBlock);
                        inCodeBlock = false;
                    }
                    continue;
                }

                if (inCodeBlock)
                {
                    codeBlockLines.Add(line);
                    continue;
                }

                // ─── Table handling ─────────────────────────────────
                if (IsTableRow(line))
                {
                    tableLines.Add(line);
                    continue;
                }
                else
                {
                    FlushTable(sb, tableLines, ref firstBlock);
                }

                // ─── Blank line ─────────────────────────────────────
                // Skip blank lines — paragraph separation is already handled by
                // the trailing \par on each content block + AppendPar before the next.
                // Emitting an extra \par here would create double-spaced gaps.
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                // ─── Headings ───────────────────────────────────────
                if (line.StartsWith("### "))
                {
                    AppendPar(sb, ref firstBlock);
                    sb.Append("{\\b").Append(H3Size).Append(" ");
                    AppendInlineRtf(sb, line.Substring(4).Trim());
                    sb.Append("}").Append(BodySize);
                    continue;
                }
                if (line.StartsWith("## "))
                {
                    AppendPar(sb, ref firstBlock);
                    sb.Append("{\\b").Append(H2Size).Append(" ");
                    AppendInlineRtf(sb, line.Substring(3).Trim());
                    sb.Append("}").Append(BodySize);
                    continue;
                }
                if (line.StartsWith("# "))
                {
                    AppendPar(sb, ref firstBlock);
                    sb.Append("{\\b").Append(H1Size).Append(" ");
                    AppendInlineRtf(sb, line.Substring(2).Trim());
                    sb.Append("}").Append(BodySize);
                    continue;
                }

                // ─── Bullet lists ───────────────────────────────────
                if (line.TrimStart().StartsWith("- ") || line.TrimStart().StartsWith("* "))
                {
                    AppendPar(sb, ref firstBlock);
                    var trimmed = line.TrimStart();
                    var content = trimmed.Substring(2);
                    sb.Append(@"{\li360\fi-180 \u8226?  ");
                    AppendInlineRtf(sb, content.Trim());
                    sb.Append("}");
                    continue;
                }

                // ─── Numbered lists ─────────────────────────────────
                var numberedMatch = Regex.Match(line.TrimStart(), @"^(\d+)\.\s+(.*)$");
                if (numberedMatch.Success)
                {
                    AppendPar(sb, ref firstBlock);
                    sb.Append(@"{\li360\fi-180 ");
                    sb.Append(numberedMatch.Groups[1].Value).Append(". ");
                    AppendInlineRtf(sb, numberedMatch.Groups[2].Value.Trim());
                    sb.Append("}");
                    continue;
                }

                // ─── Horizontal rule ────────────────────────────────
                if (Regex.IsMatch(line.Trim(), @"^[-*_]{3,}$"))
                {
                    AppendPar(sb, ref firstBlock);
                    sb.Append(@"{\cf2 \u8212?\u8212?\u8212?\u8212?\u8212?\u8212?\u8212?\u8212?}");
                    continue;
                }

                // ─── Normal paragraph ───────────────────────────────
                AppendPar(sb, ref firstBlock);
                AppendInlineRtf(sb, line);
            }

            // Flush any pending blocks
            if (inCodeBlock)
                FlushCodeBlock(sb, codeBlockLines, ref firstBlock);
            FlushTable(sb, tableLines, ref firstBlock);

            sb.Append("}");
            return sb.ToString();
        }

        /// <summary>
        /// Strips markdown formatting from text, returning plain text.
        /// Used for "Apply to target" which sends to the Trados editor.
        /// </summary>
        public static string StripMarkdown(string markdown)
        {
            if (string.IsNullOrEmpty(markdown))
                return string.Empty;

            var lines = markdown.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            var result = new StringBuilder();
            var inCodeBlock = false;

            foreach (var line in lines)
            {
                if (line.TrimStart().StartsWith("```"))
                {
                    inCodeBlock = !inCodeBlock;
                    continue;
                }

                if (inCodeBlock)
                {
                    result.AppendLine(line);
                    continue;
                }

                var stripped = line;

                // Remove heading markers
                stripped = Regex.Replace(stripped, @"^#{1,6}\s+", "");

                // Remove horizontal rules
                if (Regex.IsMatch(stripped.Trim(), @"^[-*_]{3,}$"))
                {
                    result.AppendLine();
                    continue;
                }

                // Remove table separator rows
                if (Regex.IsMatch(stripped.Trim(), @"^\|[\s\-:|]+\|$"))
                    continue;

                // Clean table rows (remove outer pipes, keep content)
                if (stripped.TrimStart().StartsWith("|") && stripped.TrimEnd().EndsWith("|"))
                {
                    stripped = stripped.Trim().Trim('|');
                    stripped = Regex.Replace(stripped, @"\s*\|\s*", " | ");
                    stripped = stripped.Trim();
                }

                // Remove bullet markers
                stripped = Regex.Replace(stripped, @"^(\s*)[-*]\s+", "$1");

                // Remove bold/italic markers
                stripped = Regex.Replace(stripped, @"\*{3}(.+?)\*{3}", "$1");
                stripped = Regex.Replace(stripped, @"\*{2}(.+?)\*{2}", "$1");
                stripped = Regex.Replace(stripped, @"\*(.+?)\*", "$1");
                stripped = Regex.Replace(stripped, @"_{2}(.+?)_{2}", "$1");
                stripped = Regex.Replace(stripped, @"_(.+?)_", "$1");

                // Remove inline code backticks
                stripped = Regex.Replace(stripped, @"`(.+?)`", "$1");

                result.AppendLine(stripped);
            }

            return result.ToString().TrimEnd();
        }

        // ─── Inline formatting ──────────────────────────────────────

        /// <summary>
        /// Parses inline markdown (bold, italic, code) and appends RTF to the builder.
        /// </summary>
        private static void AppendInlineRtf(StringBuilder sb, string text)
        {
            int i = 0;
            while (i < text.Length)
            {
                // Inline code: `text`
                if (text[i] == '`' && i + 1 < text.Length)
                {
                    var end = text.IndexOf('`', i + 1);
                    if (end > i + 1)
                    {
                        sb.Append("{").Append(CodeFont).Append(CodeSize).Append(CodeColor).Append(" ");
                        AppendEscaped(sb, text.Substring(i + 1, end - i - 1));
                        sb.Append("}").Append(BodySize).Append(@"\cf1 ");
                        i = end + 1;
                        continue;
                    }
                }

                // Bold+italic: ***text***
                if (i + 2 < text.Length && text[i] == '*' && text[i + 1] == '*' && text[i + 2] == '*')
                {
                    var end = text.IndexOf("***", i + 3, StringComparison.Ordinal);
                    if (end > 0)
                    {
                        sb.Append(@"{\b\i ");
                        AppendEscaped(sb, text.Substring(i + 3, end - i - 3));
                        sb.Append("}");
                        i = end + 3;
                        continue;
                    }
                }

                // Bold: **text**
                if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
                {
                    var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                    if (end > 0)
                    {
                        sb.Append(@"{\b ");
                        AppendEscaped(sb, text.Substring(i + 2, end - i - 2));
                        sb.Append("}");
                        i = end + 2;
                        continue;
                    }
                }

                // Italic: *text* (but not ** which is bold)
                if (text[i] == '*' && (i + 1 >= text.Length || text[i + 1] != '*'))
                {
                    var end = text.IndexOf('*', i + 1);
                    if (end > i + 1)
                    {
                        sb.Append(@"{\i ");
                        AppendEscaped(sb, text.Substring(i + 1, end - i - 1));
                        sb.Append("}");
                        i = end + 1;
                        continue;
                    }
                }

                // Regular character
                AppendEscapedChar(sb, text[i]);
                i++;
            }
        }

        // ─── Block helpers ──────────────────────────────────────────

        private static void FlushCodeBlock(StringBuilder sb, List<string> lines, ref bool firstBlock)
        {
            if (lines.Count == 0) return;

            AppendPar(sb, ref firstBlock);
            sb.Append("{").Append(CodeFont).Append(CodeSize).Append(CodeColor).Append(" ");

            for (int j = 0; j < lines.Count; j++)
            {
                AppendEscaped(sb, lines[j]);
                if (j < lines.Count - 1)
                    sb.Append(@"\line ");
            }

            sb.Append("}").Append(BodySize).Append(@"\cf1 ");
            lines.Clear();
        }

        private static void FlushTable(StringBuilder sb, List<string> lines, ref bool firstBlock)
        {
            if (lines.Count == 0) return;

            // Parse table: collect rows, skip separator rows
            var rows = new List<string[]>();
            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                // Skip separator rows like |---|---|
                if (Regex.IsMatch(trimmed, @"^\|[\s\-:|]+\|$"))
                    continue;

                // Split by | and trim cells
                var cells = trimmed.Split('|')
                    .Select(c => c.Trim())
                    .Where(c => c.Length > 0)
                    .ToArray();

                if (cells.Length > 0)
                    rows.Add(cells);
            }

            if (rows.Count == 0)
            {
                lines.Clear();
                return;
            }

            // Determine if this is a "wide" table (cells with long text that would
            // overflow a monospace grid).  Threshold: any cell > 40 chars stripped.
            var colCount = rows.Max(r => r.Length);
            bool wideTable = rows.Any(r => r.Any(c => StripInlineMarkdown(c).Length > 40));

            if (wideTable && rows.Count > 1 && colCount >= 2)
            {
                // ── Wide table: render as labelled rows with inline formatting ──
                // Use the first row as column headers / labels.
                var headers = rows[0];

                for (int r = 1; r < rows.Count; r++)
                {
                    var row = rows[r];
                    AppendPar(sb, ref firstBlock);

                    // For 2-column tables, emit "Header: value" on separate lines
                    // For 3+ column tables, emit "Col0" as bold title, then "ColN: value" lines
                    if (colCount == 2)
                    {
                        // Bold first cell as label
                        sb.Append(@"{\b ");
                        AppendInlineRtf(sb, row.Length > 0 ? row[0] : "");
                        sb.Append("} ");

                        // Second cell as value with full inline markdown
                        if (row.Length > 1)
                            AppendInlineRtf(sb, row[1]);
                    }
                    else
                    {
                        // First cell as bold title line
                        sb.Append(@"{\b ");
                        AppendInlineRtf(sb, row.Length > 0 ? row[0] : "");
                        sb.Append("}");

                        // Remaining cells as "Header: value" lines
                        for (int c = 1; c < colCount; c++)
                        {
                            sb.Append(@"\line ");
                            // Header label
                            var hdr = c < headers.Length ? headers[c] : "";
                            if (!string.IsNullOrEmpty(hdr))
                            {
                                sb.Append(@"{\b ");
                                AppendEscaped(sb, hdr + ": ");
                                sb.Append("}");
                            }
                            // Cell value with inline markdown
                            if (c < row.Length)
                                AppendInlineRtf(sb, row[c]);
                        }
                    }

                    // Separator between rows (except after last)
                    if (r < rows.Count - 1)
                    {
                        sb.Append(@"\par ");
                    }
                }
            }
            else
            {
                // ── Compact table: monospace grid with inline formatting ────
                var colWidths = new int[colCount];
                foreach (var row in rows)
                {
                    for (int c = 0; c < row.Length; c++)
                        colWidths[c] = Math.Max(colWidths[c], StripInlineMarkdown(row[c]).Length);
                }

                AppendPar(sb, ref firstBlock);
                sb.Append("{").Append(CodeFont).Append(CodeSize).Append(@"\cf1 ");

                for (int r = 0; r < rows.Count; r++)
                {
                    var row = rows[r];
                    var isHeader = (r == 0);

                    if (isHeader) sb.Append(@"{\b ");

                    for (int c = 0; c < colCount; c++)
                    {
                        var cell = c < row.Length ? StripInlineMarkdown(row[c]) : "";
                        sb.Append("  ");
                        AppendEscaped(sb, cell.PadRight(colWidths[c]));
                    }

                    if (isHeader) sb.Append("}");

                    if (r < rows.Count - 1)
                        sb.Append(@"\line ");
                }

                sb.Append("}").Append(BodySize).Append(@"\cf1 ");
            }

            lines.Clear();
        }

        /// <summary>
        /// Strips inline markdown markers from a single text span (for table column width calculation).
        /// </summary>
        private static string StripInlineMarkdown(string text)
        {
            text = Regex.Replace(text, @"`(.+?)`", "$1");
            text = Regex.Replace(text, @"\*{3}(.+?)\*{3}", "$1");
            text = Regex.Replace(text, @"\*{2}(.+?)\*{2}", "$1");
            text = Regex.Replace(text, @"\*(.+?)\*", "$1");
            return text;
        }

        private static void AppendPar(StringBuilder sb, ref bool firstBlock)
        {
            if (!firstBlock)
                sb.Append(@"\par ");
            firstBlock = false;
        }

        private static bool IsTableRow(string line)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("|")) return false;
            if (!trimmed.EndsWith("|")) return false;
            // Must have at least two pipe characters (start + end)
            return trimmed.Count(c => c == '|') >= 2;
        }

        // ─── RTF escaping ───────────────────────────────────────────

        private static void AppendEscaped(StringBuilder sb, string text)
        {
            foreach (var ch in text)
                AppendEscapedChar(sb, ch);
        }

        private static void AppendEscapedChar(StringBuilder sb, char ch)
        {
            switch (ch)
            {
                case '\\': sb.Append(@"\\"); break;
                case '{': sb.Append(@"\{"); break;
                case '}': sb.Append(@"\}"); break;
                default:
                    if (ch > 127)
                        sb.Append(@"\u").Append((int)ch).Append("?");
                    else
                        sb.Append(ch);
                    break;
            }
        }
    }
}
