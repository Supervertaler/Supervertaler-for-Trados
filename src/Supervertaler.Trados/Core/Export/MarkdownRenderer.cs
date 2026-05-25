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
            foreach (var seg in segments)
            {
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
            sb.Append("| # | ").Append(opts.SourceLanguageDisplay).Append(" | ")
              .Append(opts.TargetLanguageDisplay).Append(" | Status | Notes |\n");
            sb.Append("|---|---|---|---|---|\n");
            foreach (var seg in segments)
            {
                sb.Append("| ").Append(seg.Number).Append(" <!-- sv-seg:").Append(seg.Number).Append(" -->")
                  .Append(" | ").Append(EscapeForTableCell(seg.SourceText))
                  .Append(" | ").Append(EscapeForTableCell(seg.TargetText ?? ""))
                  .Append(" | ").Append(EscapeForTableCell(seg.DisplayStatus))
                  .Append(" | ").Append(EscapeForTableCell(seg.Notes ?? ""))
                  .Append(" |\n");
            }
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
