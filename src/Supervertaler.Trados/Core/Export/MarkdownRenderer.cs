using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Supervertaler.Trados.Core.Export
{
    /// <summary>
    /// Renders bilingual data as Markdown with embedded segment metadata in
    /// HTML comments. The comments are invisible in rendered markdown
    /// (Obsidian, GitHub, VS Code preview) but survive copy-paste and round-
    /// trip cleanly through proofreading workflows. The importer reads the
    /// embedded `&lt;!-- sv-seg:N --&gt;` markers to align edits back to the
    /// Trados segments they came from.
    /// </summary>
    public class MarkdownRenderer : IExportRenderer
    {
        public void Render(List<ExportSegment> segments, ExportOptions options, string outputPath)
        {
            var sb = new StringBuilder();
            AppendHeader(sb, options, segments.Count);
            sb.Append("---\n\n");

            switch (options.Layout)
            {
                case ExportLayout.Table:
                    AppendTableLayout(sb, segments, options);
                    break;
                case ExportLayout.StackedTargetTop:
                    AppendStackedLayout(sb, segments, options, targetFirst: true);
                    break;
                case ExportLayout.StackedSourceTop:
                default:
                    AppendStackedLayout(sb, segments, options, targetFirst: false);
                    break;
            }

            File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(false));
        }

        private static void AppendHeader(StringBuilder sb, ExportOptions opts, int total)
        {
            sb.Append("# Supervertaler Bilingual Review\n\n");
            sb.Append("<!-- sv-export-version: 1.0 -->\n");
            sb.Append("<!-- sv-project: ").Append(opts.ProjectName).Append(" -->\n");
            sb.Append("<!-- sv-source-file: ").Append(opts.SourceFileName).Append(" -->\n");
            sb.Append("<!-- sv-languages: ").Append(opts.SourceLanguageDisplay)
              .Append(" -> ").Append(opts.TargetLanguageDisplay).Append(" -->\n");
            sb.Append("<!-- sv-layout: ").Append(opts.Layout.ToString()).Append(" -->\n");
            sb.Append("<!-- sv-tool-version: ").Append(opts.ToolVersion).Append(" -->\n\n");

            sb.Append("**Project:** ").Append(opts.ProjectName).Append("  \n");
            sb.Append("**Source file:** ").Append(opts.SourceFileName).Append("  \n");
            sb.Append("**Languages:** ").Append(opts.SourceLanguageDisplay)
              .Append(" → ").Append(opts.TargetLanguageDisplay).Append("  \n");
            sb.Append("**Segments:** ").Append(total.ToString(CultureInfo.InvariantCulture)).Append("\n\n");

            sb.Append("> **Important:** Do not change the `## Segment N` headings, ");
            sb.Append("the source text, or the `<!-- sv-seg:... -->` markers. ");
            sb.Append("You can freely edit the target text below each segment. ");
            sb.Append("This file can be re-imported into Trados after proofreading.\n\n");
        }

        private static void AppendStackedLayout(StringBuilder sb, List<ExportSegment> segments,
            ExportOptions opts, bool targetFirst)
        {
            // v4.20.19: same multi-file affordance the DOCX renderer has —
            // when the bilingual file spans more than one source file in
            // a merged Trados document, emit a "📄 File: <name>" marker
            // before the first segment of each new file so the proofreader
            // can see file boundaries at a glance in stacked layouts too.
            bool multiFile = HasMultipleSourceFiles(segments);
            string previousFile = null;

            foreach (var seg in segments)
            {
                if (multiFile)
                {
                    var thisFile = seg.SourceFileName ?? "";
                    if (!string.Equals(thisFile, previousFile, System.StringComparison.Ordinal))
                    {
                        sb.Append("## 📄 File: ").Append(thisFile).Append("\n\n");
                        previousFile = thisFile;
                    }
                }

                sb.Append("## Segment ").Append(seg.Number).Append('\n');
                sb.Append("<!-- sv-seg:").Append(seg.Number).Append(" -->\n\n");

                if (targetFirst)
                {
                    AppendTargetBlock(sb, seg, opts);
                    AppendSourceBlock(sb, seg, opts);
                }
                else
                {
                    AppendSourceBlock(sb, seg, opts);
                    AppendTargetBlock(sb, seg, opts);
                }

                if (!string.IsNullOrEmpty(seg.DisplayStatus))
                    sb.Append("**Status:** ").Append(seg.DisplayStatus).Append("\n\n");

                sb.Append("---\n\n");
            }
        }

        private static void AppendSourceBlock(StringBuilder sb, ExportSegment seg, ExportOptions opts)
        {
            sb.Append("**Source (").Append(opts.SourceLanguageDisplay).Append("):**\n");
            sb.Append(EscapeForMarkdown(seg.SourceText)).Append("\n\n");
        }

        private static void AppendTargetBlock(StringBuilder sb, ExportSegment seg, ExportOptions opts)
        {
            sb.Append("**Target (").Append(opts.TargetLanguageDisplay).Append("):**\n");
            sb.Append(EscapeForMarkdown(seg.TargetText ?? "")).Append("\n\n");
        }

        private static void AppendTableLayout(StringBuilder sb, List<ExportSegment> segments,
            ExportOptions opts)
        {
            // v4.20.19: multi-file affordance mirroring the DOCX renderer.
            // When the file contains segments from more than one source
            // file, the table grows a 6th "File" column and a full-width
            // section-break row appears at every file boundary so the
            // proofreader can spot transitions at a glance — matching
            // the DOCX output's behaviour.
            bool multiFile = HasMultipleSourceFiles(segments);

            if (multiFile)
            {
                sb.Append("| # | ").Append(opts.SourceLanguageDisplay).Append(" | ")
                  .Append(opts.TargetLanguageDisplay).Append(" | File | Status | Notes |\n");
                sb.Append("|---|---|---|---|---|---|\n");
            }
            else
            {
                sb.Append("| # | ").Append(opts.SourceLanguageDisplay).Append(" | ")
                  .Append(opts.TargetLanguageDisplay).Append(" | Status | Notes |\n");
                sb.Append("|---|---|---|---|---|\n");
            }

            string previousFile = null;
            foreach (var seg in segments)
            {
                if (multiFile)
                {
                    var thisFile = seg.SourceFileName ?? "";
                    if (!string.Equals(thisFile, previousFile, System.StringComparison.Ordinal))
                    {
                        // Section-break row that spans all six columns.
                        // Markdown tables don't support real merged cells,
                        // so we put the file name in a single column and
                        // leave the others as empty bold dividers — most
                        // markdown renderers (Obsidian, GitHub, VS Code)
                        // visually show this as a clearly different row.
                        sb.Append("| | **📄 File: ").Append(EscapeForTableCell(thisFile))
                          .Append("** | | | | |\n");
                        previousFile = thisFile;
                    }
                }

                sb.Append("| ").Append(seg.Number).Append(" <!-- sv-seg:").Append(seg.Number).Append(" -->")
                  .Append(" | ").Append(EscapeForTableCell(seg.SourceText))
                  .Append(" | ").Append(EscapeForTableCell(seg.TargetText ?? ""));

                if (multiFile)
                {
                    sb.Append(" | ").Append(EscapeForTableCell(seg.SourceFileName ?? ""));
                }

                sb.Append(" | ").Append(EscapeForTableCell(seg.DisplayStatus))
                  .Append(" | ").Append(EscapeForTableCell(seg.Notes ?? ""))
                  .Append(" |\n");
            }
        }

        /// <summary>Same multi-file detector the DocxRenderer uses — true
        /// when the segment list contains more than one distinct
        /// SourceFileName. Empty file names are treated as a single
        /// implicit "active file" group (single-file mode).</summary>
        private static bool HasMultipleSourceFiles(List<ExportSegment> segments)
        {
            if (segments == null || segments.Count == 0) return false;
            string seen = null;
            foreach (var seg in segments)
            {
                var name = seg.SourceFileName ?? "";
                if (seen == null) { seen = name; continue; }
                if (!string.Equals(seen, name, System.StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>Light Markdown escaping for stacked-layout bodies — preserve
        /// newlines and existing markup, but neutralise the few characters that
        /// would change rendering (we don't expect them in source/target text
        /// often, but the export should be safe regardless).</summary>
        private static string EscapeForMarkdown(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            // Leave most punctuation alone — segment text is read as prose by
            // the proofreader. We just guard against accidental Markdown
            // headings at start of line.
            return text.Replace("\r\n", "\n");
        }

        private static string EscapeForTableCell(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            // Pipe characters and newlines break Markdown tables.
            return text.Replace("|", "\\|").Replace("\r\n", " ").Replace("\n", " ");
        }
    }
}
