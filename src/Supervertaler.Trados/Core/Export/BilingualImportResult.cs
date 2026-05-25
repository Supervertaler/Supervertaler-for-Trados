using System.Collections.Generic;

namespace Supervertaler.Trados.Core.Export
{
    /// <summary>Per-segment outcome of a bilingual round-trip import.</summary>
    public enum ImportChangeKind
    {
        /// <summary>Imported target equals current target — nothing to do.</summary>
        Unchanged,

        /// <summary>Imported target differs from current target — change pending.</summary>
        Changed,

        /// <summary>Manifest entry exists but no matching Trados segment could
        /// be found (e.g. segment was deleted or the project changed).</summary>
        SegmentMissing,

        /// <summary>Source text in the round-tripped file no longer matches
        /// the original source. Either the proofreader edited the source
        /// (forbidden) or the manifest/file are mismatched.</summary>
        SourceMismatch,

        /// <summary>The Trados segment is locked or already
        /// confirmed; needs explicit user override before overwriting.</summary>
        Locked,

        /// <summary>The proofreader's edit has fewer tag markers than the
        /// live source has tags. Applying it would create a Trados QA
        /// failure (source tags must appear in the target). Skipped by
        /// default; can be force-applied by turning off the strict
        /// tag-integrity check in the Import / Export tab.</summary>
        TagMismatch
    }

    public class ImportSegmentDiff
    {
        public int Number { get; set; }
        public string ParagraphUnitId { get; set; }
        public string SegmentId { get; set; }
        public string OldTarget { get; set; }
        public string NewTarget { get; set; }
        public string Notes { get; set; }
        public string Status { get; set; }
        public ImportChangeKind Kind { get; set; }
        public string Detail { get; set; }   // e.g. mismatch reason

        /// <summary>Whether the user has opted in to apply this change. Set
        /// by the UI before the writeback pass; the import core only writes
        /// segments where <c>Apply &amp;&amp; Kind == Changed</c>.</summary>
        public bool Apply { get; set; }
    }

    public class BilingualImportResult
    {
        public ExportManifest Manifest { get; set; }
        public List<ImportSegmentDiff> Diffs { get; set; } = new List<ImportSegmentDiff>();

        public int TotalImported => Diffs.Count;
        public int ChangedCount
        {
            get { int n = 0; foreach (var d in Diffs) if (d.Kind == ImportChangeKind.Changed) n++; return n; }
        }
        public int UnchangedCount
        {
            get { int n = 0; foreach (var d in Diffs) if (d.Kind == ImportChangeKind.Unchanged) n++; return n; }
        }
        public int IssueCount
        {
            get
            {
                int n = 0;
                foreach (var d in Diffs)
                    if (d.Kind == ImportChangeKind.SegmentMissing
                        || d.Kind == ImportChangeKind.SourceMismatch
                        || d.Kind == ImportChangeKind.Locked
                        || d.Kind == ImportChangeKind.TagMismatch) n++;
                return n;
            }
        }

        public int TagMismatchCount
        {
            get { int n = 0; foreach (var d in Diffs) if (d.Kind == ImportChangeKind.TagMismatch) n++; return n; }
        }
    }
}
