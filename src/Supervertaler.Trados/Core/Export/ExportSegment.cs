using System;

namespace Supervertaler.Trados.Core.Export
{
    /// <summary>
    /// One row of bilingual data, format-agnostic. The renderers consume a
    /// <see cref="System.Collections.Generic.List{T}"/> of these and produce
    /// DOCX/Markdown/HTML; the importer reverses the flow.
    ///
    /// Notes on identity:
    /// - <see cref="Number"/> is the human-visible export number (1..N) written
    ///   into the file itself. Proofreaders can see it; renames break the
    ///   re-import alignment.
    /// - <see cref="ParagraphUnitId"/> + <see cref="SegmentId"/> together form
    ///   the unambiguous Trados segment key. They are NOT written into the
    ///   user-visible file content; they live in the sidecar manifest so even
    ///   if a proofreader accidentally reorders rows we can still match by
    ///   the manifest's (Number → Pu/Seg id) mapping.
    /// </summary>
    public class ExportSegment
    {
        /// <summary>1-based export number, matches the column header "#" in the DOCX table.</summary>
        public int Number { get; set; }

        /// <summary>Stable Trados ParagraphUnit id (string GUID-like).</summary>
        public string ParagraphUnitId { get; set; }

        /// <summary>Stable Trados Segment id (string GUID-like, unique within its paragraph unit).</summary>
        public string SegmentId { get; set; }

        /// <summary>Plain source text with tag placeholders left in (e.g. &lt;t1&gt;...&lt;/t1&gt;).</summary>
        public string SourceText { get; set; }

        /// <summary>Plain target text. Empty for untranslated segments.</summary>
        public string TargetText { get; set; }

        /// <summary>
        /// Trados confirmation level snapshot at export time — "Draft",
        /// "Translated", "Approved", "Rejected", etc. Surfaced in the
        /// "Status" column. Empty when not applicable.
        /// </summary>
        public string Status { get; set; }

        /// <summary>Optional free-form notes column, blank on initial export.</summary>
        public string Notes { get; set; }

        /// <summary>SHA-256 prefix of the source text at export time. Used by the
        /// importer to detect source tampering before applying changes.</summary>
        public string SourceHash { get; set; }

        /// <summary>True when the segment's parent paragraph (or the segment's
        /// own IText runs) carry paragraph-level bold styling — e.g. a DOCX
        /// "Heading 1" or a whole paragraph set to bold in the source file.
        /// Distinct from inline cf-bold tags, which are serialised as
        /// <c>&lt;b&gt;...&lt;/b&gt;</c> markers inside the cell text.
        /// The DocxRenderer applies this flag visually to both the source and
        /// target cells so the proofreader sees the segment styled the way
        /// it'll appear in the final translated document — purely cosmetic,
        /// not round-tripped (Trados regenerates paragraph styling from its
        /// own metadata on export).</summary>
        public bool IsBold { get; set; }

        /// <summary>Paragraph-level italic. Same semantics as <see cref="IsBold"/>.</summary>
        public bool IsItalic { get; set; }

        /// <summary>Paragraph-level underline. Same semantics as <see cref="IsBold"/>.</summary>
        public bool IsUnderline { get; set; }

        /// <summary>Trados file id (GUID-ish string) that the segment
        /// belongs to. Same as the file's IProjectFile.Id. Empty for
        /// single-file exports or when the SDK can't surface it.
        /// Recorded in the manifest so re-import can route each diff to
        /// the correct file in a merged multi-file document.</summary>
        public string SourceFileId { get; set; }

        /// <summary>Human-readable file name (e.g. "US8312383.docx") for
        /// the file this segment belongs to. Used as the value in the
        /// "File" column when the bilingual export is rendered in multi-
        /// file mode.</summary>
        public string SourceFileName { get; set; }
    }
}
