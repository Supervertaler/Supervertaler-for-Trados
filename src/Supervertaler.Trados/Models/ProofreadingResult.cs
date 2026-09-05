using System;
using System.Collections.Generic;
using System.Linq;

namespace Supervertaler.Trados.Models
{
    public class ProofreadingIssue
    {
        public int SegmentIndex { get; set; }       // 0-based
        public int SegmentNumber { get; set; }       // 1-based display
        public string SourceText { get; set; }
        public string TargetText { get; set; }
        public bool IsOk { get; set; }
        public string IssueDescription { get; set; }
        public string Suggestion { get; set; }
        public string Evidence { get; set; }          // Specific source segments cited by the model (terminology consistency etc.)
        public object SegmentPairRef { get; set; }   // ISegmentPair or string[] for navigation
        public string ParagraphUnitId { get; set; }
        public string SegmentId { get; set; }

        /// <summary>Ticked off in the Reports tab (#105). Survives a restore, so a
        /// report put back after a restart shows only what is still open.</summary>
        public bool Dismissed { get; set; }
    }

    public class ProofreadingReport
    {
        public List<ProofreadingIssue> Issues { get; set; } = new List<ProofreadingIssue>();
        public int TotalSegmentsChecked { get; set; }
        public int IssueCount => Issues.Count(i => !i.IsOk);
        public int OkCount => Issues.Count(i => i.IsOk);
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public TimeSpan Duration { get; set; }

        /// <summary>The document the run was made on (#105): a restored report is
        /// shown only when this document is the active one again.</summary>
        public string DocumentPath { get; set; }
        public string DocumentName { get; set; }
        public string SourceLang { get; set; }
        public string TargetLang { get; set; }
    }

    public enum BatchMode { Translate, Proofread }
    public enum ProofreadScope { ConfirmedOnly, TranslatedAndConfirmed, AllSegments, Filtered, FilteredConfirmedOnly }
}
