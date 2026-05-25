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

                // Detect column layout. Single-file mode = 5 cells
                // (#, Source, Target, Status, Notes). Multi-file mode = 6
                // cells (#, Source, Target, File, Status, Notes). We
                // sniff the header row; failing that, fall back to the
                // 5-column layout.
                var headerRow = tableRows[0];
                var headerCells = headerRow.Elements<TableCell>().ToList();
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
                    if (sixColumn && cells.Count >= 6)
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
