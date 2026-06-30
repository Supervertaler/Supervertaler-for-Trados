using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Supervertaler.Trados.Core.Export
{
    /// <summary>
    /// Parses a Supervertaler-exported Markdown file back into a list of
    /// <see cref="ImportedSegment"/>s. Tolerant of:
    ///
    /// - Light proofreader edits (added whitespace, slightly different
    ///   surrounding markup).
    /// - Both stacked-source-top and stacked-target-top layouts (detected by
    ///   the relative order of the <c>**Source ...:**</c> and
    ///   <c>**Target ...:**</c> labels).
    /// - The Markdown table layout (5 columns: # | Source | Target | Status | Notes).
    ///
    /// NOT tolerant of:
    /// - Renaming the <c>## Segment N</c> headings (the segment-number is the
    ///   primary alignment key).
    /// - Removing the <c>&lt;!-- sv-seg:N --&gt;</c> markers entirely AND
    ///   renaming the heading; one of them must survive.
    /// </summary>
    public class MarkdownImporter
    {
        private static readonly Regex SegMarkerRe = new Regex(@"<!--\s*sv-seg:(\d+)\s*-->", RegexOptions.IgnoreCase);
        private static readonly Regex SegHeadingRe = new Regex(@"^##\s*Segment\s+(\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        private static readonly Regex LabelLineRe = new Regex(@"^\*\*(Source|Target)\b[^*]*\*\*\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        private static readonly Regex StatusLineRe = new Regex(@"^\*\*Status:\*\*\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        private static readonly Regex TableRowRe = new Regex(@"^\|\s*(\d+)\s*(?:<!--\s*sv-seg:\d+\s*-->)?\s*\|(.*)\|\s*$", RegexOptions.Multiline);
        // v4.20.20: bracketed-layout anchors. The number is zero-padded
        // (e.g. "0001") but the regex accepts any digit run for safety.
        private static readonly Regex BracketedAnchorRe = new Regex(@"^\[SEGMENT\s+(\d+)\]\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        // Within a bracketed block: a line that starts with 2-3 letters,
        // a colon, optional spaces/tabs, then the body. Captures (lang-
        // code, body). v4.20.24: was `\s*` for the post-colon whitespace,
        // which .NET treats as matching newlines too — meaning on a line
        // like "NL: \n" (empty body) the engine would consume the newline
        // and then match (.*) against the NEXT line, falsely capturing
        // "Status: Unspecified" (etc.) as the body. Using [ \t]* keeps
        // the match anchored to the current line.
        private static readonly Regex BracketedLangLineRe = new Regex(@"^([A-Za-z]{2,3}):[ \t]*(.*)$", RegexOptions.Multiline);

        public List<ImportedSegment> Parse(string filePath)
        {
            var text = File.ReadAllText(filePath, Encoding.UTF8);
            return ParseText(text);
        }

        public List<ImportedSegment> ParseText(string text)
        {
            text = (text ?? "").Replace("\r\n", "\n");

            // Try table layout first — it has a recognisable header row.
            if (text.Contains("| # |") || Regex.IsMatch(text, @"^\|\s*#\s*\|", RegexOptions.Multiline))
            {
                var fromTable = ParseTableLayout(text);
                if (fromTable.Count > 0) return fromTable;
            }

            // v4.20.20: bracketed [SEGMENT NNNN] layout. Recognisable by
            // its bracketed anchor lines; falls through to stacked if no
            // anchors match (shared parser shape with stacked but
            // different anchor / body conventions).
            if (BracketedAnchorRe.IsMatch(text))
            {
                var fromBracketed = ParseBracketedLayout(text);
                if (fromBracketed.Count > 0) return fromBracketed;
            }

            // Stacked layout fallback.
            return ParseStackedLayout(text);
        }

        private static List<ImportedSegment> ParseBracketedLayout(string text)
        {
            var segments = new List<ImportedSegment>();
            // Collect anchors with their positions.
            var anchors = new List<KeyValuePair<int, int>>();
            foreach (Match m in BracketedAnchorRe.Matches(text))
            {
                int num;
                if (int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out num))
                    anchors.Add(new KeyValuePair<int, int>(m.Index, num));
            }
            if (anchors.Count == 0) return segments;

            // Walk each anchor → next anchor, parse the EN: / NL: pair.
            for (int i = 0; i < anchors.Count; i++)
            {
                int blockStart = anchors[i].Key;
                int blockEnd = (i + 1 < anchors.Count) ? anchors[i + 1].Key : text.Length;
                int number = anchors[i].Value;
                var blockText = text.Substring(blockStart, blockEnd - blockStart);

                // v4.20.24: collect every non-"Status" lang-line in the
                // block in order; first = source, LAST = target. Picking
                // the last (rather than the second) is robust against
                // proofreaders who edit by inserting extra lines instead
                // of replacing the empty target placeholder — e.g.
                //   EN: source
                //   NL:
                //   NL: my actual translation here
                // The user's actual translation always wins. Source
                // languages 2-3 letters; the renderer also emits a
                // "Status: …" line, but that's 6 letters so the
                // BracketedLangLineRe regex skips it naturally.
                // Collect lang-line fields, each of which may span MULTIPLE
                // physical lines. A field opens on a "XX: body" line and
                // continues onto the following lines until the next "XX:"
                // lang-line or the "Status:" line.
                //
                // v4.20.83 fix: previously this matched only the first physical
                // line of each field (BracketedLangLineRe is single-line), so a
                // segment whose body contains a hard line break — e.g. a source
                // "VEILIGHEIDS-\nHELM" exported as
                //     NL: VEILIGHEIDS-
                //     HELM
                // — silently lost everything after the first line on re-import
                // (the target came back as just "SAFETY-"). The renderer emits
                // segment text verbatim (newlines preserved), so the importer
                // must reassemble them.
                var langBodies = new List<string>(4);
                string curBody = null;        // body being accumulated (null = no open field)
                foreach (var line in blockText.Split('\n'))
                {
                    var lm = BracketedLangLineRe.Match(line);
                    if (lm.Success)
                    {
                        // A new lang-line ("NL:", "EN:", …) opens a new field;
                        // close out the previous one.
                        if (curBody != null) langBodies.Add(curBody.Trim());
                        curBody = lm.Groups[2].Value;
                    }
                    else if (Regex.IsMatch(line, @"^Status:", RegexOptions.IgnoreCase))
                    {
                        // The "Status:" line terminates the current field. It's
                        // 6 letters so it never matches BracketedLangLineRe (2-3),
                        // and without this guard it would be wrongly appended as a
                        // continuation of the target body. The status value itself
                        // is captured separately below.
                        if (curBody != null) { langBodies.Add(curBody.Trim()); curBody = null; }
                    }
                    else if (curBody != null)
                    {
                        curBody += "\n" + line; // continuation line — preserve the hard break
                    }
                    // else: line before any field (e.g. the "[SEGMENT N]" anchor) — ignore
                }
                if (curBody != null) langBodies.Add(curBody.Trim());
                string sourceBody;
                string targetBody;
                if (langBodies.Count >= 2)
                {
                    sourceBody = langBodies[0];
                    targetBody = langBodies[langBodies.Count - 1];
                }
                else if (langBodies.Count == 1)
                {
                    // Only one lang-line in the block — best-effort
                    // treat it as the target. Source stays empty; the
                    // diff path skips the source-tamper check when the
                    // file omits the source.
                    sourceBody = "";
                    targetBody = langBodies[0];
                }
                else
                {
                    continue; // no recognisable lang-lines, skip
                }

                var seg = new ImportedSegment
                {
                    Number = number,
                    SourceText = sourceBody ?? "",
                    TargetText = targetBody
                };
                // Status line, if present anywhere in the block.
                var statusMatch = Regex.Match(blockText, @"^Status:\s*(.+)$", RegexOptions.Multiline);
                if (statusMatch.Success) seg.Status = statusMatch.Groups[1].Value.Trim();
                segments.Add(seg);
            }
            return segments;
        }

        private static List<ImportedSegment> ParseTableLayout(string text)
        {
            var rows = new List<ImportedSegment>();
            foreach (Match m in TableRowRe.Matches(text))
            {
                int num;
                if (!int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out num))
                    continue;

                var rest = m.Groups[2].Value;
                var cells = rest.Split('|');
                if (cells.Length < 2) continue;

                // v4.20.19: detect 6-column layout (multi-file mode).
                // 5-col: # | Source | Target | Status | Notes        → cells = [Src, Tgt, Status, Notes]
                // 6-col: # | Source | Target | File | Status | Notes → cells = [Src, Tgt, File, Status, Notes]
                // Heuristic: if we have 5 or more cells, assume 6-column
                // and skip the File column (which is informational on
                // re-import — the manifest's per-segment SourceFileId is
                // the authoritative routing key, not what the table says).
                bool multiFile = cells.Length >= 5;
                var seg = new ImportedSegment
                {
                    Number = num,
                    SourceText = UnescapeCell(cells[0]),
                    TargetText = UnescapeCell(cells[1]),
                    Status = multiFile
                        ? (cells.Length > 3 ? UnescapeCell(cells[3]) : "")
                        : (cells.Length > 2 ? UnescapeCell(cells[2]) : ""),
                    Notes = multiFile
                        ? (cells.Length > 4 ? UnescapeCell(cells[4]) : "")
                        : (cells.Length > 3 ? UnescapeCell(cells[3]) : "")
                };
                rows.Add(seg);
            }
            return rows;
        }

        private static string UnescapeCell(string cell)
        {
            if (string.IsNullOrEmpty(cell)) return "";
            return cell.Trim().Replace("\\|", "|");
        }

        private static List<ImportedSegment> ParseStackedLayout(string text)
        {
            // Split into segment blocks by `## Segment N` headings (or
            // `<!-- sv-seg:N -->` markers as fallback).
            var segments = new List<ImportedSegment>();

            // Find all anchors (heading or marker) and their positions.
            var anchors = new List<KeyValuePair<int, int>>(); // position → number
            foreach (Match m in SegHeadingRe.Matches(text))
            {
                int num;
                if (int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out num))
                    anchors.Add(new KeyValuePair<int, int>(m.Index, num));
            }
            // Markers only count if they aren't immediately preceded by their own heading
            // (which would double-count); we just dedupe by number further down.
            foreach (Match m in SegMarkerRe.Matches(text))
            {
                int num;
                if (int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out num))
                    anchors.Add(new KeyValuePair<int, int>(m.Index, num));
            }

            anchors.Sort((a, b) => a.Key.CompareTo(b.Key));

            // Dedupe consecutive anchors with the same number (heading + marker
            // for the same segment) by keeping the earliest one.
            var dedup = new List<KeyValuePair<int, int>>();
            var seen = new HashSet<int>();
            foreach (var a in anchors)
            {
                if (seen.Contains(a.Value)) continue;
                seen.Add(a.Value);
                dedup.Add(a);
            }

            for (int i = 0; i < dedup.Count; i++)
            {
                int blockStart = dedup[i].Key;
                int blockEnd = (i + 1 < dedup.Count) ? dedup[i + 1].Key : text.Length;
                int number = dedup[i].Value;
                var blockText = text.Substring(blockStart, blockEnd - blockStart);

                var seg = ParseStackedBlock(number, blockText);
                if (seg != null) segments.Add(seg);
            }

            return segments;
        }

        private static ImportedSegment ParseStackedBlock(int number, string block)
        {
            // Find Source / Target label lines and capture the text between them.
            var labelMatches = LabelLineRe.Matches(block);
            if (labelMatches.Count == 0) return null;

            // Collect (label, start-of-body, end-of-body) tuples.
            var sections = new List<Section>();
            for (int i = 0; i < labelMatches.Count; i++)
            {
                var lm = labelMatches[i];
                var labelKind = lm.Groups[1].Value.Trim().ToLowerInvariant(); // "source" or "target"
                int bodyStart = lm.Index + lm.Length;
                int bodyEnd = (i + 1 < labelMatches.Count) ? labelMatches[i + 1].Index : block.Length;

                // Also stop at "---" separators or "## Segment" (block boundary).
                int sep = IndexOfAny(block, bodyStart, bodyEnd, "\n---", "\n## ");
                if (sep > 0) bodyEnd = sep;

                // Status line acts as a soft body end too.
                var statusMatch = StatusLineRe.Match(block, bodyStart, bodyEnd - bodyStart);
                if (statusMatch.Success) bodyEnd = statusMatch.Index;

                var body = block.Substring(bodyStart, bodyEnd - bodyStart).Trim('\n', '\r', ' ');
                sections.Add(new Section { Label = labelKind, Body = body });
            }

            var seg = new ImportedSegment { Number = number };
            foreach (var s in sections)
            {
                if (s.Label == "source" && seg.SourceText == null) seg.SourceText = s.Body;
                if (s.Label == "target" && seg.TargetText == null) seg.TargetText = s.Body;
            }

            var status = StatusLineRe.Match(block);
            if (status.Success) seg.Status = status.Groups[1].Value.Trim();

            // Only return a valid segment if we got at least target text (the
            // proofreader's actual edit). Source-only segments are skipped.
            if (seg.TargetText == null) return null;
            return seg;
        }

        private static int IndexOfAny(string text, int start, int end, params string[] needles)
        {
            int best = -1;
            foreach (var n in needles)
            {
                int idx = text.IndexOf(n, start, Math.Min(text.Length, end) - start, StringComparison.Ordinal);
                if (idx >= 0 && (best == -1 || idx < best)) best = idx;
            }
            return best;
        }

        private class Section { public string Label; public string Body; }
    }
}
