using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Supervertaler.Trados.Core.Export
{
    /// <summary>
    /// Parses a Supervertaler Bilingual Table DOCX back into a list of
    /// <see cref="ImportedSegment"/>s.
    ///
    /// Expects the first table in the document to have a header row whose
    /// first cell is "#" and 4 more columns: Source, Target, Status, Notes.
    /// Falls back to ordering if the header isn't an exact match — the
    /// segment number from the first cell is the authoritative key.
    /// </summary>
    public class DocxImporter
    {
        public List<ImportedSegment> Parse(string filePath)
        {
            var rows = new List<ImportedSegment>();

            using (var doc = WordprocessingDocument.Open(filePath, false))
            {
                var body = doc.MainDocumentPart?.Document?.Body;
                if (body == null) return rows;

                var table = body.Elements<Table>().FirstOrDefault();
                if (table == null) return rows;

                var tableRows = table.Elements<TableRow>().ToList();
                if (tableRows.Count < 2) return rows; // header + at least one data row

                // Detect column layout. Cells 0-2 are always #, Source,
                // Target; the trailing columns vary: [File] (multi-file
                // exports), Status, [Comments] (exports where segments
                // carry Trados comments), Notes. We locate the fixed-name
                // columns in the header row by their literal text — the
                // Source/Target headers are language display names and
                // never collide with them. Falls back to the legacy
                // count-based sniff (>= 6 cells = multi-file) for files
                // whose header doesn't parse.
                var headerRow = tableRows[0];
                var headerCells = headerRow.Elements<TableCell>().ToList();
                int statusIdx = -1, notesIdx = -1;
                for (int c = 3; c < headerCells.Count; c++)
                {
                    var h = ExtractCellText(headerCells[c]).Trim();
                    if (h.Equals("Status", System.StringComparison.OrdinalIgnoreCase)) statusIdx = c;
                    else if (h.Equals("Notes", System.StringComparison.OrdinalIgnoreCase)) notesIdx = c;
                    // "File" and "Comments" columns are recognised implicitly:
                    // they shift Status/Notes right and carry no re-importable
                    // data themselves.
                }
                bool headerMapped = statusIdx >= 0;
                bool sixColumn = headerCells.Count >= 6;

                // Skip the header row (row 0); data starts at row 1.
                for (int i = 1; i < tableRows.Count; i++)
                {
                    var cells = tableRows[i].Elements<TableCell>().ToList();
                    if (cells.Count < 3) continue;

                    // Multi-file section-break row: a single spanned cell
                    // (cells.Count == 1) carrying the file-name header.
                    // No segment data; skip.
                    if (cells.Count == 1) continue;

                    var numText = ExtractCellText(cells[0]).Trim();
                    int number;
                    if (!int.TryParse(numText, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                        continue;

                    var seg = new ImportedSegment
                    {
                        Number = number,
                        SourceText = ExtractCellText(cells[1]),
                        TargetText = ExtractCellText(cells[2])
                    };
                    if (headerMapped)
                    {
                        seg.Status = statusIdx < cells.Count ? ExtractCellText(cells[statusIdx]) : "";
                        seg.Notes = notesIdx >= 0 && notesIdx < cells.Count ? ExtractCellText(cells[notesIdx]) : "";
                    }
                    else if (sixColumn && cells.Count >= 6)
                    {
                        // cells: 0=#, 1=Src, 2=Tgt, 3=File, 4=Status, 5=Notes
                        seg.Status = ExtractCellText(cells[4]);
                        seg.Notes = ExtractCellText(cells[5]);
                    }
                    else
                    {
                        // cells: 0=#, 1=Src, 2=Tgt, 3=Status, 4=Notes
                        seg.Status = cells.Count > 3 ? ExtractCellText(cells[3]) : "";
                        seg.Notes = cells.Count > 4 ? ExtractCellText(cells[4]) : "";
                    }
                    rows.Add(seg);
                }
            }

            return rows;
        }

        /// <summary>Concatenate all text runs in a cell, inserting newlines
        /// for Break elements so soft returns survive round-trip.</summary>
        private static string ExtractCellText(TableCell cell)
        {
            var sb = new StringBuilder();
            // Walk paragraph-by-paragraph; insert \n between paragraphs.
            var paragraphs = cell.Elements<Paragraph>().ToList();
            for (int p = 0; p < paragraphs.Count; p++)
            {
                if (p > 0) sb.Append('\n');
                AppendParagraphText(paragraphs[p], sb);
            }
            return sb.ToString().Trim();
        }

        private static void AppendParagraphText(Paragraph p, StringBuilder sb)
        {
            foreach (var child in p.Descendants())
            {
                if (child is Text t)
                {
                    sb.Append(t.Text);
                }
                else if (child is Break)
                {
                    sb.Append('\n');
                }
                else if (child is TabChar)
                {
                    sb.Append('\t');
                }
            }
        }
    }
}
