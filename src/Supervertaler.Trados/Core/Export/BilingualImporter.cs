using System;
using System.Collections.Generic;
using System.IO;

namespace Supervertaler.Trados.Core.Export
{
    /// <summary>
    /// Reads back a round-tripped DOCX or Markdown bilingual file, joins each
    /// row to the originating Trados segment via the sidecar manifest, and
    /// produces a list of pending diffs.
    ///
    /// The class is pure: it doesn't write to Trados itself. The caller (the
    /// ViewPart) applies confirmed diffs via <c>ProcessSegmentPair</c>.
    /// </summary>
    public class BilingualImporter
    {
        /// <summary>Build the diff list. Caller supplies "current target"
        /// lookups via <paramref name="currentTargetLookup"/> — given a
        /// (paragraphUnitId, segmentId) pair return the current target text,
        /// or <c>null</c> if no such segment exists in the active document.
        /// Lock/confirmation status is supplied via
        /// <paramref name="isWriteable"/> — return false to mark a segment
        /// as Locked.</summary>
        // Pattern that counts only STRUCTURAL tag markers (numbered <tN>
        // / <tN/>) in a proofreader's edit. Semantic formatting markers
        // (<b>, <i>, <u>, <bi>) are deliberately excluded because adding
        // or removing character formatting in the target is harmless from
        // a Trados-QA perspective — it just changes the cosmetic
        // formatting of the translation. Structural tags (field codes,
        // page numbers, custom format pairs, line breaks) are the ones
        // Trados QA enforces; those must round-trip 1:1.
        private static readonly System.Text.RegularExpressions.Regex
            OpeningStructuralMarkerRe = new System.Text.RegularExpressions.Regex(
                @"<(?!/)t\d+\b",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        public BilingualImportResult Build(
            string importedFilePath,
            ExportManifest manifest,
            Func<string, string, string> currentTargetLookup,
            Func<string, string, bool> isWriteable,
            Func<string, string, string> currentSourceLookup = null,
            Func<string, string, int> currentSourceTagCountLookup = null)
        {
            var ext = Path.GetExtension(importedFilePath).ToLowerInvariant();
            List<ImportedSegment> imported;
            if (ext == ".docx")
                imported = new DocxImporter().Parse(importedFilePath);
            else
                imported = new MarkdownImporter().Parse(importedFilePath);

            var result = new BilingualImportResult { Manifest = manifest };

            // Index manifest segments by number for O(1) lookup.
            var manifestByNumber = new Dictionary<int, ExportManifestSegment>();
            foreach (var m in manifest.Segments)
                manifestByNumber[m.Number] = m;

            foreach (var row in imported)
            {
                ExportManifestSegment m;
                if (!manifestByNumber.TryGetValue(row.Number, out m))
                {
                    result.Diffs.Add(new ImportSegmentDiff
                    {
                        Number = row.Number,
                        NewTarget = row.TargetText,
                        Kind = ImportChangeKind.SegmentMissing,
                        Detail = "No manifest entry for segment #" + row.Number
                    });
                    continue;
                }

                var diff = new ImportSegmentDiff
                {
                    Number = row.Number,
                    ParagraphUnitId = m.ParagraphUnitId,
                    SegmentId = m.SegmentId,
                    NewTarget = row.TargetText ?? "",
                    Notes = row.Notes,
                    Status = row.Status
                };

                // Source-text tamper check, if the imported file kept the
                // source column (table layout) and a source lookup was given.
                if (currentSourceLookup != null && !string.IsNullOrEmpty(row.SourceText) && !string.IsNullOrEmpty(m.SourceHash))
                {
                    var currentHash = BilingualExporter.HashPrefix(row.SourceText);
                    if (!string.Equals(currentHash, m.SourceHash, StringComparison.Ordinal))
                    {
                        diff.Kind = ImportChangeKind.SourceMismatch;
                        diff.Detail = "Source text has been changed in the round-tripped file";
                        result.Diffs.Add(diff);
                        continue;
                    }
                }

                var currentTarget = currentTargetLookup?.Invoke(m.ParagraphUnitId, m.SegmentId);
                if (currentTarget == null)
                {
                    diff.Kind = ImportChangeKind.SegmentMissing;
                    diff.Detail = "Segment not present in the active Trados document";
                    result.Diffs.Add(diff);
                    continue;
                }
                diff.OldTarget = currentTarget;

                if (string.Equals(NormaliseForCompare(currentTarget), NormaliseForCompare(row.TargetText ?? ""),
                        StringComparison.Ordinal))
                {
                    diff.Kind = ImportChangeKind.Unchanged;
                    result.Diffs.Add(diff);
                    continue;
                }

                if (isWriteable != null && !isWriteable(m.ParagraphUnitId, m.SegmentId))
                {
                    diff.Kind = ImportChangeKind.Locked;
                    diff.Detail = "Segment is locked or rejected; needs explicit override";
                    result.Diffs.Add(diff);
                    continue;
                }

                // Structural-tag integrity check. Counts only numbered
                // <tN> markers (NOT semantic <b>/<i>/<u>/<bi>) in both
                // the source and the proofreader's edit, then enforces
                // strict equality:
                //
                // - Semantic formatting tags (bold / italic / underline)
                //   can be freely added or removed in the target — they
                //   only affect cosmetic rendering and don't drive Trados
                //   QA rules.
                // - Structural tags (field codes, page numbers, custom
                //   format pairs, line breaks — anything that didn't get
                //   a friendly name via BilingualTagNamer) MUST round-trip
                //   1:1 with the source. Adding one creates a tag the
                //   target file can't render; removing one drops a tag
                //   Trados expects.
                //
                // The source-side count is supplied by the caller's
                // currentSourceStructuralCountLookup so the importer
                // doesn't need to re-serialise the segment itself.
                if (currentSourceTagCountLookup != null)
                {
                    int sourceStructural = currentSourceTagCountLookup(m.ParagraphUnitId, m.SegmentId);
                    int targetStructural = OpeningStructuralMarkerRe.Matches(diff.NewTarget ?? "").Count;
                    if (sourceStructural != targetStructural)
                    {
                        diff.Kind = ImportChangeKind.TagMismatch;
                        diff.Detail =
                            $"Edit has {targetStructural} structural tag marker(s) (<tN>) " +
                            $"but source has {sourceStructural}; applying would break Trados " +
                            "(structural tags must round-trip exactly — semantic <b>/<i>/<u> " +
                            "can be freely added or removed)";
                        diff.Apply = false; // strict mode default — caller can flip to true to force
                        result.Diffs.Add(diff);
                        continue;
                    }
                }

                diff.Kind = ImportChangeKind.Changed;
                diff.Apply = true; // default: include changed segments in the writeback
                result.Diffs.Add(diff);
            }

            return result;
        }

        private static string NormaliseForCompare(string s)
        {
            if (s == null) return "";
            // Normalise newlines + collapse trailing whitespace per line.
            s = s.Replace("\r\n", "\n");
            var lines = s.Split('\n');
            for (int i = 0; i < lines.Length; i++)
                lines[i] = lines[i].TrimEnd();
            return string.Join("\n", lines).Trim();
        }
    }
}
