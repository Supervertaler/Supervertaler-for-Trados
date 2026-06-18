using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Exports the raw usage ledger (one row per AI call) to CSV or XLSX for
    /// billing / analysis. Both formats carry the same columns.
    /// </summary>
    public static class UsageExport
    {
        private static readonly string[] Headers =
        {
            "timestamp_utc", "product", "app_version", "task", "provider", "model",
            "project", "project_key", "file", "client", "src_lang", "tgt_lang",
            "input_regular", "input_cache_read", "input_cache_write", "output",
            "input_total", "total_tokens", "source", "cost_usd", "cost_known",
            "duration_s", "ok", "error"
        };

        private static string[] FieldsOf(UsageRecord r)
        {
            long inTotal = (long)r.InputRegular + r.InputCacheRead + r.InputCacheWrite;
            long total = inTotal + r.Output;
            return new[]
            {
                r.Ts ?? "", r.Product ?? "", r.AppVersion ?? "", r.Task ?? "",
                r.Provider ?? "", r.Model ?? "", r.Project ?? "", r.ProjectKey ?? "",
                r.File ?? "", r.Client ?? "", r.SrcLang ?? "", r.TgtLang ?? "",
                r.InputRegular.ToString(CultureInfo.InvariantCulture),
                r.InputCacheRead.ToString(CultureInfo.InvariantCulture),
                r.InputCacheWrite.ToString(CultureInfo.InvariantCulture),
                r.Output.ToString(CultureInfo.InvariantCulture),
                inTotal.ToString(CultureInfo.InvariantCulture),
                total.ToString(CultureInfo.InvariantCulture),
                r.Source ?? "",
                r.CostUsd.ToString("0.######", CultureInfo.InvariantCulture),
                r.CostKnown ? "true" : "false",
                r.DurationS.ToString("0.###", CultureInfo.InvariantCulture),
                r.Ok ? "true" : "false",
                r.Error ?? ""
            };
        }

        // Columns that should be written as numbers in XLSX (0-based indices into Headers/FieldsOf).
        private static readonly HashSet<int> NumericCols =
            new HashSet<int> { 12, 13, 14, 15, 16, 17, 19, 21 };

        public static void WriteCsv(string path, IEnumerable<UsageRecord> records)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", Array.ConvertAll(Headers, CsvEscape)));
            foreach (var r in records)
            {
                if (r == null) continue;
                sb.AppendLine(string.Join(",", Array.ConvertAll(FieldsOf(r), CsvEscape)));
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true)); // BOM so Excel detects UTF-8
        }

        private static string CsvEscape(string s)
        {
            if (s == null) return "";
            bool needsQuote = s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
            if (needsQuote) return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        public static void WriteXlsx(string path, IEnumerable<UsageRecord> records)
        {
            using (var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
            {
                var wbPart = doc.AddWorkbookPart();
                wbPart.Workbook = new Workbook();

                var wsPart = wbPart.AddNewPart<WorksheetPart>();
                var sheetData = new SheetData();
                wsPart.Worksheet = new Worksheet(sheetData);

                var sheets = wbPart.Workbook.AppendChild(new Sheets());
                sheets.Append(new Sheet
                {
                    Id = wbPart.GetIdOfPart(wsPart),
                    SheetId = 1U,
                    Name = "Usage"
                });

                uint rowIndex = 1;
                sheetData.Append(BuildRow(Headers, rowIndex, _ => false));
                rowIndex++;

                foreach (var r in records)
                {
                    if (r == null) continue;
                    sheetData.Append(BuildRow(FieldsOf(r), rowIndex, c => NumericCols.Contains(c)));
                    rowIndex++;
                }

                wbPart.Workbook.Save();
            }
        }

        private static Row BuildRow(string[] values, uint rowIndex, Func<int, bool> isNumeric)
        {
            var row = new Row { RowIndex = rowIndex };
            for (int c = 0; c < values.Length; c++)
            {
                var cellRef = ColumnLetter(c + 1) + rowIndex;
                Cell cell;
                if (isNumeric(c) && double.TryParse(values[c], NumberStyles.Any, CultureInfo.InvariantCulture, out var num))
                {
                    cell = new Cell
                    {
                        CellReference = cellRef,
                        DataType = CellValues.Number,
                        CellValue = new CellValue(num.ToString(CultureInfo.InvariantCulture))
                    };
                }
                else
                {
                    cell = new Cell
                    {
                        CellReference = cellRef,
                        DataType = CellValues.InlineString,
                        InlineString = new InlineString(new Text(values[c] ?? "") { Space = SpaceProcessingModeValues.Preserve })
                    };
                }
                row.Append(cell);
            }
            return row;
        }

        /// <summary>1-based column index to spreadsheet letters (1 -> A, 27 -> AA).</summary>
        private static string ColumnLetter(int index)
        {
            var sb = new StringBuilder();
            while (index > 0)
            {
                int rem = (index - 1) % 26;
                sb.Insert(0, (char)('A' + rem));
                index = (index - 1) / 26;
            }
            return sb.ToString();
        }
    }
}
