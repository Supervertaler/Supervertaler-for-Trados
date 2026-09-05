using System;
using System.IO;
using System.Linq;
using System.Text;
using Supervertaler.Trados.Models;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Renders a proofreading report as Markdown and writes it under
    /// <c>&lt;data&gt;\trados\reports</c> (#105). The report used to exist only in
    /// memory, in the Reports tab, and a long run's results could vanish with the
    /// panel's state. Every completed run is now written here automatically, and
    /// the tab's Save button writes the same rendering wherever the user chooses.
    /// Nothing here throws: a report that cannot be written is logged and skipped -
    /// the run itself has already succeeded.
    /// </summary>
    public static class ProofreadingReportWriter
    {
        public static string ReportsDir => Path.Combine(UserDataPath.TradosDir, "reports");

        /// <summary>"2026-09-05 111732 Inline formatting test (en) proofreading.md".</summary>
        public static string DefaultFileName(ProofreadingReport report, string documentName)
        {
            var stamp = (report?.Timestamp ?? DateTime.Now).ToString("yyyy-MM-dd HHmmss");
            var doc = Path.GetFileNameWithoutExtension(documentName ?? "");
            if (doc.EndsWith(".sdlxliff", StringComparison.OrdinalIgnoreCase))
                doc = doc.Substring(0, doc.Length - ".sdlxliff".Length);
            foreach (var c in Path.GetInvalidFileNameChars()) doc = doc.Replace(c, '_');
            doc = doc.Trim();
            return string.IsNullOrEmpty(doc)
                ? stamp + " proofreading.md"
                : stamp + " " + doc + " proofreading.md";
        }

        public static string ToMarkdown(ProofreadingReport report, string documentName,
            string sourceLang, string targetLang)
        {
            var sb = new StringBuilder();
            var doc = string.IsNullOrWhiteSpace(documentName) ? "(document)" : documentName;
            sb.Append("# Proofreading report \u2013 ").AppendLine(doc);
            sb.AppendLine();
            sb.Append("- Document: ").AppendLine(doc);
            if (!string.IsNullOrEmpty(sourceLang) || !string.IsNullOrEmpty(targetLang))
                sb.Append("- Languages: ").Append(sourceLang ?? "?").Append(" \u2192 ").AppendLine(targetLang ?? "?");
            sb.Append("- Run: ").Append(report.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"))
              .Append(" (").Append(report.Duration.TotalSeconds.ToString("F1")).AppendLine(" s)");
            sb.Append("- Segments checked: ").Append(report.TotalSegmentsChecked).AppendLine();
            sb.Append("- Issues: ").Append(report.IssueCount).AppendLine();
            sb.AppendLine();

            var issues = (report.Issues ?? Enumerable.Empty<ProofreadingIssue>())
                .Where(i => i != null && !i.IsOk)
                .OrderBy(i => i.SegmentNumber)
                .ToList();

            if (issues.Count == 0)
            {
                sb.AppendLine("No issues found.");
                return sb.ToString();
            }

            foreach (var i in issues)
            {
                sb.Append("## Segment ").Append(i.SegmentNumber).AppendLine();
                sb.AppendLine();
                Field(sb, "Issue", i.IssueDescription);
                Field(sb, "Suggestion", i.Suggestion);
                Field(sb, "Evidence", i.Evidence);
                Field(sb, "Source", i.SourceText);
                Field(sb, "Target", i.TargetText);
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static void Field(StringBuilder sb, string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            // One field per paragraph; a multi-line value keeps its line breaks.
            sb.Append("**").Append(label).Append(":** ")
              .AppendLine(value.Trim().Replace("\r\n", "\n").Replace("\n", "  \n"));
            sb.AppendLine();
        }

        /// <summary>
        /// Writes the report to the reports folder. Returns the full path, or null
        /// if it could not be written (logged to the diagnostic log).
        /// </summary>
        public static string TrySave(ProofreadingReport report, string documentName,
            string sourceLang, string targetLang)
        {
            if (report == null) return null;
            try
            {
                Directory.CreateDirectory(ReportsDir);
                var path = Path.Combine(ReportsDir, DefaultFileName(report, documentName));
                File.WriteAllText(path, ToMarkdown(report, documentName, sourceLang, targetLang),
                    new UTF8Encoding(false));
                return path;
            }
            catch (Exception ex)
            {
                try { DiagnosticLog.Log("Reports", "Could not save proofreading report: " + ex.Message); } catch { }
                return null;
            }
        }
    }
}
