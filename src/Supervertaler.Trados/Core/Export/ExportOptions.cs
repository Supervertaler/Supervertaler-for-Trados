namespace Supervertaler.Trados.Core.Export
{
    public enum ExportFormat
    {
        /// <summary>Word .docx — the 5-column Supervertaler Bilingual Table.
        /// Re-importable.</summary>
        Docx,

        /// <summary>Plain-text .txt — the "Bilingual Text (AI-friendly)"
        /// [SEGMENT NNNN] format, matching the Supervertaler Workbench.
        /// Re-importable.</summary>
        Text,

        /// <summary>HTML report — read-only, cannot be re-imported.</summary>
        Html
    }

    public enum ExportLayout
    {
        /// <summary>5-column table (#, Source, Target, Status, Notes).
        /// The canonical Supervertaler Bilingual Table, used by the DOCX and
        /// HTML renderers; the only DOCX layout that round-trips on
        /// re-import.</summary>
        Table,

        /// <summary>Compact AI-friendly plain-text layout matching the
        /// Supervertaler Workbench's "Bilingual Text (AI-friendly)" export.
        /// One block per segment, separated by blank lines:
        ///   <code>
        ///   [SEGMENT 0001]
        ///   EN: source text
        ///   NL: target text
        ///   </code>
        /// In-field hard line breaks are encoded as the literal token
        /// <c>[newline]</c> so every field stays on one physical line (decoded
        /// back to a real break on re-import). Used by the Text (.txt) format;
        /// re-importable via the bracketed parser.</summary>
        Bracketed
    }

    public class ExportOptions
    {
        public ExportFormat Format { get; set; } = ExportFormat.Docx;
        public ExportLayout Layout { get; set; } = ExportLayout.Table;

        /// <summary>Display name of the source language (e.g. "English (US)").</summary>
        public string SourceLanguageDisplay { get; set; } = "Source";

        /// <summary>Display name of the target language (e.g. "Dutch (Belgium)").</summary>
        public string TargetLanguageDisplay { get; set; } = "Target";

        /// <summary>Used in the document title + filename.</summary>
        public string ProjectName { get; set; } = "Untitled";

        /// <summary>Identifies which file these segments belong to. Written to
        /// the manifest, and (when the export is a single source file) used as
        /// the stem of the suggested export file name.</summary>
        public string SourceFileName { get; set; } = "";

        /// <summary>True when the export combines segments from more than one
        /// source file into one output file. Combined exports are named after
        /// the project; single-file exports are named after their source file.</summary>
        public bool IsMultiFileCombined { get; set; }

        /// <summary>Plugin version that produced the export, written into the manifest.</summary>
        public string ToolVersion { get; set; } = "";

        /// <summary>When true (default), locked segments are exported
        /// along with everything else and visually marked with a 🔒
        /// prefix in the Status column. When false, they are skipped
        /// entirely — useful on large projects where the bulk of the
        /// work is locked-approved and the proofreader should only see
        /// what's actually still editable. Default: true (backwards-
        /// compatible with the pre-v4.20.18 behaviour, which always
        /// included locked segments but didn't flag them).</summary>
        public bool IncludeLocked { get; set; } = true;

        /// <summary>v4.20.24: optional confirmation-status filter. When
        /// non-empty, only segments whose <c>ConfirmationLevel</c> name
        /// (e.g. "Translated", "ApprovedTranslation", "Draft") is in the
        /// set are included in the export. Empty (the default) = no
        /// filter, every segment is included regardless of status — same
        /// as pre-v4.20.24 behaviour. Comparison is case-insensitive on
        /// the enum's <c>ToString()</c> form.</summary>
        public System.Collections.Generic.HashSet<string> IncludedStatuses { get; set; }
            = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
    }
}
