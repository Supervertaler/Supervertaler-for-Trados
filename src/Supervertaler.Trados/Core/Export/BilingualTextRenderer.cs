using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Supervertaler.Trados.Core.Export
{
    /// <summary>
    /// Renders bilingual data as the "Bilingual Text (AI-friendly)" plain-text
    /// format, matching the Supervertaler Workbench's export byte-for-byte at
    /// the segment level so a file produced by either tool round-trips through
    /// the other.
    ///
    /// One block per segment, blank-line separated:
    /// <code>
    ///   [SEGMENT 0001]
    ///   EN: source text
    ///   NL: target text
    ///   Status: Draft
    /// </code>
    ///
    /// This is deliberately plain text, NOT Markdown: the segment blocks rely
    /// on preserved line breaks, which a Markdown renderer would collapse.
    /// In-field hard line breaks are written as the literal token
    /// <c>[newline]</c> so every field stays on a single physical line; the
    /// importer decodes the token back to a real break on re-import. The
    /// machine-readable manifest travels in the separate .svexport.json sidecar.
    /// </summary>
    public class BilingualTextRenderer : IExportRenderer
    {
        /// <summary>Literal token used to encode an in-field hard line break so
        /// each segment field stays on one physical line. Kept identical to the
        /// Workbench's NEWLINE_TOKEN so the two tools stay compatible.</summary>
        public const string NewlineToken = "[newline]";

        /// <summary>Prefix of the "file boundary" marker line emitted in
        /// multi-file (merged Trados document) exports. The importer treats a
        /// line starting with this emoji as a field terminator so the marker
        /// never leaks into the preceding segment's target.</summary>
        private const string FileHeaderPrefix = "📄 File: ";

        public void Render(List<ExportSegment> segments, ExportOptions options, string outputPath)
        {
            var sb = new StringBuilder();
            AppendHeader(sb, options, segments.Count);
            AppendBracketed(sb, segments, options);
            File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(false));
        }

        private static void AppendHeader(StringBuilder sb, ExportOptions opts, int total)
        {
            var srcCode = LanguageDisplayToCode(opts.SourceLanguageDisplay);
            var tgtCode = LanguageDisplayToCode(opts.TargetLanguageDisplay);
            string rule = new string('=', 72);

            sb.Append(rule).Append('\n');
            sb.Append("  SUPERVERTALER BILINGUAL TEXT (AI-friendly)\n");
            sb.Append(rule).Append('\n');
            sb.Append("  Project:      ").Append(opts.ProjectName).Append('\n');
            sb.Append("  Source file:  ").Append(opts.SourceFileName).Append('\n');
            sb.Append("  Languages:    ").Append(opts.SourceLanguageDisplay)
              .Append(" -> ").Append(opts.TargetLanguageDisplay).Append('\n');
            sb.Append("  Segments:     ").Append(total.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("  Tool:         Supervertaler for Trados ").Append(opts.ToolVersion).Append('\n');
            sb.Append('\n');
            sb.Append("  HOW TO EDIT THIS FILE\n");
            sb.Append("  - Do not change the [SEGMENT N] markers or the ").Append(srcCode).Append(": source lines.\n");
            sb.Append("    (A [newline] in a ").Append(srcCode).Append(": source marks a break in the original,\n");
            sb.Append("    read-only source — e.g. a two-line subtitle.)\n");
            sb.Append("  - Edit the ").Append(tgtCode).Append(": target text freely, but keep it on ONE line;\n");
            sb.Append("    write the literal token [newline] where a line break is needed.\n");
            sb.Append("  - Comment: lines show Trados segment comments for reference only;\n");
            sb.Append("    they are not written back into Trados on re-import.\n");
            sb.Append("  - Then re-import into Trados to update the project.\n");
            sb.Append(rule).Append('\n');
            sb.Append('\n');
        }

        private static void AppendBracketed(StringBuilder sb, List<ExportSegment> segments, ExportOptions opts)
        {
            // Zero-pad segment numbers to a consistent width (min 4, matching
            // the Workbench convention "0001") so anchors line up.
            int maxNum = 0;
            foreach (var seg in segments)
                if (seg.Number > maxNum) maxNum = seg.Number;
            int pad = System.Math.Max(4, maxNum.ToString(CultureInfo.InvariantCulture).Length);
            var padFmt = "D" + pad.ToString(CultureInfo.InvariantCulture);

            var srcCode = LanguageDisplayToCode(opts.SourceLanguageDisplay);
            var tgtCode = LanguageDisplayToCode(opts.TargetLanguageDisplay);

            bool multiFile = HasMultipleSourceFiles(segments);
            string previousFile = null;

            foreach (var seg in segments)
            {
                if (multiFile)
                {
                    var thisFile = seg.SourceFileName ?? "";
                    if (!string.Equals(thisFile, previousFile, System.StringComparison.Ordinal))
                    {
                        sb.Append(FileHeaderPrefix).Append(thisFile).Append('\n').Append('\n');
                        previousFile = thisFile;
                    }
                }

                sb.Append('[').Append("SEGMENT ")
                  .Append(seg.Number.ToString(padFmt, CultureInfo.InvariantCulture)).Append(']').Append('\n');
                // Source and target each stay on ONE physical line: hard breaks
                // become [newline] tokens. The target is decoded back to "\n" on
                // import; the source is read-only reference.
                sb.Append(srcCode).Append(": ").Append(EncodeBreaks(seg.SourceText)).Append('\n');
                sb.Append(tgtCode).Append(": ").Append(EncodeBreaks(seg.TargetText ?? "")).Append('\n');
                if (!string.IsNullOrEmpty(seg.DisplayStatus))
                    sb.Append("Status: ").Append(seg.DisplayStatus).Append('\n');
                // Trados segment comments, on one physical line like every
                // other field ([newline] between comments). "Comment:" is
                // the Workbench's line label for this same slot, so files
                // stay parseable by both tools. Omitted when there are none.
                if (!string.IsNullOrEmpty(seg.Comments))
                    sb.Append("Comment: ").Append(EncodeBreaks(seg.Comments)).Append('\n');
                sb.Append('\n');
            }
        }

        /// <summary>Render a (possibly multi-line) value on ONE physical line,
        /// writing each hard line break as the literal <c>[newline]</c> token.
        /// Shared by source and target so every field stays single-line.</summary>
        private static string EncodeBreaks(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", NewlineToken);
        }

        /// <summary>Map a language display name (e.g. "English (US)", "Dutch
        /// (NL)") to its 2-letter ISO 639-1 code (e.g. "EN", "NL") for the
        /// compact source / target line labels. Falls back to the first two
        /// letters of the display name uppercased for anything not in the small
        /// lookup table.</summary>
        private static string LanguageDisplayToCode(string display)
        {
            if (string.IsNullOrWhiteSpace(display)) return "??";
            var s = display.Trim();
            int paren = s.IndexOf('(');
            if (paren > 0) s = s.Substring(0, paren).Trim();
            switch (s.ToLowerInvariant())
            {
                case "english": return "EN";
                case "dutch":
                case "nederlands": return "NL";
                case "german":
                case "deutsch": return "DE";
                case "french":
                case "français":
                case "francais": return "FR";
                case "spanish":
                case "español":
                case "espanol": return "ES";
                case "italian":
                case "italiano": return "IT";
                case "portuguese":
                case "português":
                case "portugues": return "PT";
                case "russian": return "RU";
                case "chinese": return "ZH";
                case "japanese": return "JA";
                case "korean": return "KO";
                case "arabic": return "AR";
                case "polish": return "PL";
                case "swedish": return "SV";
                case "danish": return "DA";
                case "norwegian": return "NO";
                case "finnish": return "FI";
                case "czech": return "CS";
                case "hungarian": return "HU";
                case "romanian": return "RO";
                case "bulgarian": return "BG";
                case "greek": return "EL";
                case "turkish": return "TR";
                case "hebrew": return "HE";
                case "ukrainian": return "UK";
                case "vietnamese": return "VI";
                case "thai": return "TH";
                case "indonesian": return "ID";
            }
            var fallback = s.Length >= 2 ? s.Substring(0, 2) : (s + "?");
            return fallback.ToUpperInvariant();
        }

        /// <summary>True when the segment list contains more than one distinct
        /// SourceFileName (merged Trados document). Empty file names count as a
        /// single implicit group.</summary>
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
    }
}
