using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
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

        /// <summary>The last report, as shown in the tab, for putting back after a
        /// restart (#105). One file: a new run replaces it, Clear deletes it.</summary>
        public static string StatePath => Path.Combine(ReportsDir, "last-report.json");

        /// <summary>Why the last save/load/state call failed, for diagnosis; null when it succeeded.</summary>
        public static string LastError { get; private set; }

        // ─── State: the report as the tab shows it ───────────────────────

        // A DTO rather than the model: ProofreadingIssue.SegmentPairRef holds a live
        // ISegmentPair, which must not be serialised and would not survive a restart
        // anyway. Navigation uses ParagraphUnitId + SegmentId, which are kept.
        [DataContract]
        private class IssueState
        {
            [DataMember] public int SegmentIndex;
            [DataMember] public int SegmentNumber;
            [DataMember] public string SourceText;
            [DataMember] public string TargetText;
            [DataMember] public bool IsOk;
            [DataMember] public string IssueDescription;
            [DataMember] public string Suggestion;
            [DataMember] public string Evidence;
            [DataMember] public string ParagraphUnitId;
            [DataMember] public string SegmentId;
            [DataMember] public bool Dismissed;
        }

        [DataContract]
        private class ReportState
        {
            [DataMember] public int Version = 1;
            [DataMember] public string DocumentPath;
            [DataMember] public string DocumentName;
            [DataMember] public string SourceLang;
            [DataMember] public string TargetLang;
            [DataMember] public string Timestamp;      // round-trip ("o") format
            [DataMember] public double DurationSeconds;
            [DataMember] public int TotalSegmentsChecked;
            [DataMember] public IssueState[] Issues;
        }

        /// <summary>Writes the report's current state (dismissals included). Never throws.</summary>
        public static bool TrySaveState(ProofreadingReport report)
        {
            if (report == null) return false;
            try
            {
                var state = new ReportState
                {
                    DocumentPath = report.DocumentPath,
                    DocumentName = report.DocumentName,
                    SourceLang = report.SourceLang,
                    TargetLang = report.TargetLang,
                    Timestamp = report.Timestamp.ToString("o"),
                    DurationSeconds = report.Duration.TotalSeconds,
                    TotalSegmentsChecked = report.TotalSegmentsChecked,
                    Issues = (report.Issues ?? new System.Collections.Generic.List<ProofreadingIssue>())
                        .Where(i => i != null)
                        .Select(i => new IssueState
                        {
                            SegmentIndex = i.SegmentIndex, SegmentNumber = i.SegmentNumber,
                            SourceText = i.SourceText, TargetText = i.TargetText, IsOk = i.IsOk,
                            IssueDescription = i.IssueDescription, Suggestion = i.Suggestion, Evidence = i.Evidence,
                            ParagraphUnitId = i.ParagraphUnitId, SegmentId = i.SegmentId, Dismissed = i.Dismissed
                        }).ToArray()
                };
                Directory.CreateDirectory(ReportsDir);
                var tmp = StatePath + ".tmp";
                using (var fs = File.Create(tmp))
                    new DataContractJsonSerializer(typeof(ReportState)).WriteObject(fs, state);
                // Replace atomically so a crash mid-write cannot leave a half file.
                if (File.Exists(StatePath)) File.Replace(tmp, StatePath, null);
                else File.Move(tmp, StatePath);
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.ToString();
                try { DiagnosticLog.Log("Reports", "Could not save report state: " + ex.Message); } catch { }
                return false;
            }
        }

        /// <summary>The saved report, or null if there is none or it cannot be read.</summary>
        public static ProofreadingReport TryLoadState()
        {
            try
            {
                if (!File.Exists(StatePath)) return null;
                ReportState state;
                using (var fs = File.OpenRead(StatePath))
                    state = (ReportState)new DataContractJsonSerializer(typeof(ReportState)).ReadObject(fs);
                if (state == null) return null;
                var report = new ProofreadingReport
                {
                    DocumentPath = state.DocumentPath,
                    DocumentName = state.DocumentName,
                    SourceLang = state.SourceLang,
                    TargetLang = state.TargetLang,
                    TotalSegmentsChecked = state.TotalSegmentsChecked,
                    Duration = TimeSpan.FromSeconds(state.DurationSeconds)
                };
                DateTime ts;
                if (DateTime.TryParse(state.Timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind, out ts))
                    report.Timestamp = ts;
                foreach (var s in state.Issues ?? new IssueState[0])
                {
                    if (s == null) continue;
                    report.Issues.Add(new ProofreadingIssue
                    {
                        SegmentIndex = s.SegmentIndex, SegmentNumber = s.SegmentNumber,
                        SourceText = s.SourceText, TargetText = s.TargetText, IsOk = s.IsOk,
                        IssueDescription = s.IssueDescription, Suggestion = s.Suggestion, Evidence = s.Evidence,
                        ParagraphUnitId = s.ParagraphUnitId, SegmentId = s.SegmentId, Dismissed = s.Dismissed
                    });
                }
                return report;
            }
            catch (Exception ex)
            {
                LastError = ex.ToString();
                try { DiagnosticLog.Log("Reports", "Could not load report state: " + ex.Message); } catch { }
                return null;
            }
        }

        /// <summary>Clear pressed: the user is finished with the report.</summary>
        public static void DeleteState()
        {
            try { if (File.Exists(StatePath)) File.Delete(StatePath); } catch { }
        }

        /// <summary>
        /// True when <paramref name="report"/> was made on the document at
        /// <paramref name="activePath"/> (or, lacking paths, of that name).
        /// </summary>
        public static bool BelongsTo(ProofreadingReport report, string activePath, string activeName)
        {
            if (report == null) return false;
            if (!string.IsNullOrEmpty(report.DocumentPath) && !string.IsNullOrEmpty(activePath))
                return string.Equals(report.DocumentPath, activePath, StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(report.DocumentName) && !string.IsNullOrEmpty(activeName))
                return string.Equals(report.DocumentName, activeName, StringComparison.OrdinalIgnoreCase);
            return false;
        }

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
                LastError = ex.ToString();
                try { DiagnosticLog.Log("Reports", "Could not save proofreading report: " + ex.Message); } catch { }
                return null;
            }
        }
    }
}
