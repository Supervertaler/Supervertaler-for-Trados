using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Supervertaler.Trados.Core.Export
{
    /// <summary>
    /// Renders bilingual data as a self-contained HTML document with inline
    /// CSS. Intended primarily for client-facing review reports — HTML is
    /// NOT designed to round-trip (the importer doesn't support it). For
    /// editable round-trip workflows use Markdown or DOCX instead.
    ///
    /// Embedded segment IDs are still emitted as HTML comments so that, if a
    /// later release wants to add HTML re-import, the data is already there.
    /// </summary>
    public class HtmlRenderer : IExportRenderer
    {
        public void Render(List<ExportSegment> segments, ExportOptions options, string outputPath)
        {
            var sb = new StringBuilder();
            AppendHeader(sb, options, segments.Count);

            switch (options.Layout)
            {
                case ExportLayout.Table:
                    AppendTable(sb, segments, options);
                    break;
                case ExportLayout.StackedTargetTop:
                    AppendStacked(sb, segments, options, targetFirst: true);
                    break;
                case ExportLayout.StackedSourceTop:
                default:
                    AppendStacked(sb, segments, options, targetFirst: false);
                    break;
            }

            AppendFooter(sb);
            File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(false));
        }

        private static void AppendHeader(StringBuilder sb, ExportOptions opts, int total)
        {
            sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n");
            sb.Append("<meta charset=\"UTF-8\">\n");
            sb.Append("<title>").Append(HtmlEscape(opts.ProjectName)).Append(" – Bilingual Review</title>\n");
            sb.Append("<!-- sv-export-version: 1.0 -->\n");
            sb.Append("<!-- sv-project: ").Append(HtmlEscape(opts.ProjectName)).Append(" -->\n");
            sb.Append("<!-- sv-source-file: ").Append(HtmlEscape(opts.SourceFileName)).Append(" -->\n");
            sb.Append("<!-- sv-languages: ").Append(HtmlEscape(opts.SourceLanguageDisplay))
              .Append(" -> ").Append(HtmlEscape(opts.TargetLanguageDisplay)).Append(" -->\n");
            sb.Append("<!-- sv-layout: ").Append(opts.Layout.ToString()).Append(" -->\n");
            sb.Append("<!-- sv-tool-version: ").Append(HtmlEscape(opts.ToolVersion)).Append(" -->\n");

            sb.Append("<style>\n");
            sb.Append("  body { font-family: Inter, 'Segoe UI', system-ui, sans-serif; max-width: 980px; ");
            sb.Append("margin: 30px auto; padding: 0 24px; color: #222; line-height: 1.55; }\n");
            sb.Append("  h1 { color: #0066CC; border-bottom: 2px solid #0066CC; padding-bottom: 8px; }\n");
            sb.Append("  .meta { background: #F5F8FA; border: 1px solid #DBE3EA; border-radius: 6px; ");
            sb.Append("padding: 14px 18px; margin: 16px 0 28px; font-size: 0.92em; color: #444; }\n");
            sb.Append("  .meta strong { color: #222; }\n");
            sb.Append("  .seg { margin: 0 0 28px 0; padding: 14px 18px; border: 1px solid #E2E8EA; ");
            sb.Append("border-radius: 6px; background: #FFF; }\n");
            sb.Append("  .seg .num { font-weight: 600; color: #0066CC; font-size: 0.85em; ");
            sb.Append("text-transform: uppercase; letter-spacing: 0.04em; }\n");
            sb.Append("  .seg .label { font-weight: 600; font-size: 0.82em; color: #555; ");
            sb.Append("text-transform: uppercase; letter-spacing: 0.04em; margin-top: 10px; }\n");
            sb.Append("  .seg .text { margin: 4px 0 12px 0; white-space: pre-wrap; }\n");
            sb.Append("  .seg .target .text { color: #0A4D8E; }\n");
            sb.Append("  .seg .status { font-size: 0.8em; color: #888; }\n");
            sb.Append("  table.biltable { width: 100%; border-collapse: collapse; margin-top: 20px; ");
            sb.Append("font-size: 0.92em; }\n");
            sb.Append("  table.biltable th, table.biltable td { border: 1px solid #DBE3EA; ");
            sb.Append("padding: 8px 10px; vertical-align: top; }\n");
            sb.Append("  table.biltable th { background: #F5F8FA; text-align: left; }\n");
            sb.Append("  table.biltable td.num { font-weight: 600; color: #0066CC; width: 3em; ");
            sb.Append("text-align: right; }\n");
            sb.Append("  footer { color: #888; font-size: 0.85em; margin-top: 40px; ");
            sb.Append("padding-top: 16px; border-top: 1px solid #E2E8EA; }\n");
            sb.Append("  footer a { color: #0066CC; }\n");
            sb.Append("</style>\n</head>\n<body>\n");

            sb.Append("<h1>").Append(HtmlEscape(opts.ProjectName)).Append("</h1>\n");
            sb.Append("<div class=\"meta\">\n");
            sb.Append("  <div><strong>Source file:</strong> ").Append(HtmlEscape(opts.SourceFileName)).Append("</div>\n");
            sb.Append("  <div><strong>Languages:</strong> ").Append(HtmlEscape(opts.SourceLanguageDisplay))
              .Append(" → ").Append(HtmlEscape(opts.TargetLanguageDisplay)).Append("</div>\n");
            sb.Append("  <div><strong>Segments:</strong> ").Append(total.ToString(CultureInfo.InvariantCulture)).Append("</div>\n");
            sb.Append("</div>\n");
        }

        private static void AppendStacked(StringBuilder sb, List<ExportSegment> segments,
            ExportOptions opts, bool targetFirst)
        {
            foreach (var seg in segments)
            {
                sb.Append("<div class=\"seg\">\n");
                sb.Append("  <!-- sv-seg:").Append(seg.Number).Append(" -->\n");
                sb.Append("  <div class=\"num\">Segment ").Append(seg.Number).Append("</div>\n");

                if (targetFirst)
                {
                    AppendHtmlTarget(sb, seg, opts);
                    AppendHtmlSource(sb, seg, opts);
                }
                else
                {
                    AppendHtmlSource(sb, seg, opts);
                    AppendHtmlTarget(sb, seg, opts);
                }

                if (!string.IsNullOrEmpty(seg.DisplayStatus))
                    sb.Append("  <div class=\"status\">").Append(HtmlEscape(seg.DisplayStatus)).Append("</div>\n");

                sb.Append("</div>\n");
            }
        }

        private static void AppendHtmlSource(StringBuilder sb, ExportSegment seg, ExportOptions opts)
        {
            sb.Append("  <div class=\"source\"><div class=\"label\">").Append(HtmlEscape(opts.SourceLanguageDisplay))
              .Append("</div><div class=\"text\">").Append(HtmlEscape(seg.SourceText)).Append("</div></div>\n");
        }

        private static void AppendHtmlTarget(StringBuilder sb, ExportSegment seg, ExportOptions opts)
        {
            sb.Append("  <div class=\"target\"><div class=\"label\">").Append(HtmlEscape(opts.TargetLanguageDisplay))
              .Append("</div><div class=\"text\">").Append(HtmlEscape(seg.TargetText ?? "")).Append("</div></div>\n");
        }

        private static void AppendTable(StringBuilder sb, List<ExportSegment> segments, ExportOptions opts)
        {
            sb.Append("<table class=\"biltable\">\n<thead><tr>");
            sb.Append("<th>#</th>");
            sb.Append("<th>").Append(HtmlEscape(opts.SourceLanguageDisplay)).Append("</th>");
            sb.Append("<th>").Append(HtmlEscape(opts.TargetLanguageDisplay)).Append("</th>");
            sb.Append("<th>Status</th><th>Notes</th>");
            sb.Append("</tr></thead>\n<tbody>\n");
            foreach (var seg in segments)
            {
                sb.Append("<tr><!-- sv-seg:").Append(seg.Number).Append(" -->");
                sb.Append("<td class=\"num\">").Append(seg.Number).Append("</td>");
                sb.Append("<td>").Append(HtmlEscape(seg.SourceText)).Append("</td>");
                sb.Append("<td>").Append(HtmlEscape(seg.TargetText ?? "")).Append("</td>");
                sb.Append("<td>").Append(HtmlEscape(seg.DisplayStatus)).Append("</td>");
                sb.Append("<td>").Append(HtmlEscape(seg.Notes ?? "")).Append("</td>");
                sb.Append("</tr>\n");
            }
            sb.Append("</tbody></table>\n");
        }

        private static void AppendFooter(StringBuilder sb)
        {
            sb.Append("<footer>Generated by ");
            sb.Append("<a href=\"https://supervertaler.com\">Supervertaler for Trados</a>.</footer>\n");
            sb.Append("</body>\n</html>\n");
        }

        private static string HtmlEscape(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&#39;");
        }
    }
}
