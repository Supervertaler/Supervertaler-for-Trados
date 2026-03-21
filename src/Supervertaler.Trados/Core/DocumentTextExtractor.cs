using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using iTextSharp.text.pdf;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Extracts plain text from various document formats for use as AI context.
    /// Dependency-free — uses only .NET Framework built-in classes.
    /// </summary>
    public static class DocumentTextExtractor
    {
        /// <summary>Maximum extracted text length (characters) to avoid overwhelming the LLM context.</summary>
        public const int MaxExtractedLength = 200_000;

        /// <summary>Maximum file size in bytes (20 MB).</summary>
        public const long MaxFileSize = 20 * 1024 * 1024;

        /// <summary>
        /// All supported document extensions (lowercase, with leading dot).
        /// </summary>
        public static readonly HashSet<string> SupportedExtensions = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            // Documents
            ".docx", ".doc", ".pdf", ".rtf",
            // Presentations
            ".pptx", ".ppt",
            // Spreadsheets
            ".xlsx", ".xls", ".csv", ".tsv",
            // Translation
            ".tmx", ".sdlxliff", ".xliff", ".xlf", ".tbx",
            // Text / Markup
            ".txt", ".md", ".htm", ".html", ".json", ".xml"
        };

        /// <summary>
        /// All supported image extensions (lowercase, with leading dot).
        /// </summary>
        public static readonly HashSet<string> ImageExtensions = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"
        };

        /// <summary>
        /// Returns true if the file extension is a supported document type.
        /// </summary>
        public static bool IsDocumentFile(string filePath)
        {
            var ext = Path.GetExtension(filePath);
            return !string.IsNullOrEmpty(ext) && SupportedExtensions.Contains(ext);
        }

        /// <summary>
        /// Returns true if the file extension is a supported image type.
        /// </summary>
        public static bool IsImageFile(string filePath)
        {
            var ext = Path.GetExtension(filePath);
            return !string.IsNullOrEmpty(ext) && ImageExtensions.Contains(ext);
        }

        /// <summary>
        /// Extracts text from a document file. Returns the extracted text, or throws on failure.
        /// </summary>
        public static string ExtractText(string filePath)
        {
            var fi = new FileInfo(filePath);
            if (!fi.Exists)
                throw new FileNotFoundException("File not found.", filePath);
            if (fi.Length > MaxFileSize)
                throw new InvalidOperationException(
                    $"File is too large ({fi.Length / (1024 * 1024):F1} MB). Maximum is {MaxFileSize / (1024 * 1024)} MB.");

            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            string text;

            switch (ext)
            {
                // Plain text formats — read as-is
                case ".txt":
                case ".md":
                case ".csv":
                case ".tsv":
                case ".json":
                    text = ReadTextFile(filePath);
                    break;

                // XML-based text formats — strip tags
                case ".xml":
                case ".htm":
                case ".html":
                    text = ReadAndStripXmlTags(filePath);
                    break;

                // Translation interchange formats — extract source/target text
                case ".tmx":
                    text = ExtractTmx(filePath);
                    break;
                case ".sdlxliff":
                case ".xliff":
                case ".xlf":
                    text = ExtractXliff(filePath);
                    break;
                case ".tbx":
                    text = ExtractTbx(filePath);
                    break;

                // Office Open XML formats — extract from ZIP
                case ".docx":
                    text = ExtractDocx(filePath);
                    break;
                case ".pptx":
                    text = ExtractPptx(filePath);
                    break;
                case ".xlsx":
                    text = ExtractXlsx(filePath);
                    break;

                // RTF
                case ".rtf":
                    text = ExtractRtf(filePath);
                    break;

                // PDF — best-effort text extraction
                case ".pdf":
                    text = ExtractPdf(filePath);
                    break;

                // Legacy binary Office formats
                case ".doc":
                case ".ppt":
                case ".xls":
                    text = ExtractLegacyOffice(filePath);
                    break;

                default:
                    throw new NotSupportedException($"Unsupported file format: {ext}");
            }

            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException(
                    "Could not extract any text from this file. It may be empty, image-only, or in a format that requires specialised software to read.");

            // Truncate if too long
            if (text.Length > MaxExtractedLength)
                text = text.Substring(0, MaxExtractedLength) + "\n\n[... text truncated — file too large for full extraction ...]";

            return text.Trim();
        }

        // ─── Plain text ──────────────────────────────────────────────

        private static string ReadTextFile(string path)
        {
            // Auto-detect encoding (BOM) with UTF-8 fallback
            return File.ReadAllText(path, Encoding.UTF8);
        }

        private static string ReadAndStripXmlTags(string path)
        {
            var raw = File.ReadAllText(path, Encoding.UTF8);
            // Strip HTML/XML tags, decode entities
            var stripped = Regex.Replace(raw, @"<style[^>]*>[\s\S]*?</style>", " ", RegexOptions.IgnoreCase);
            stripped = Regex.Replace(stripped, @"<script[^>]*>[\s\S]*?</script>", " ", RegexOptions.IgnoreCase);
            stripped = Regex.Replace(stripped, @"<[^>]+>", " ");
            stripped = System.Net.WebUtility.HtmlDecode(stripped);
            stripped = Regex.Replace(stripped, @"[ \t]+", " ");
            stripped = Regex.Replace(stripped, @"\n{3,}", "\n\n");
            return stripped.Trim();
        }

        // ─── TMX ─────────────────────────────────────────────────────

        private static string ExtractTmx(string path)
        {
            var sb = new StringBuilder();
            try
            {
                var doc = new XmlDocument();
                doc.Load(path);
                var nsMgr = new XmlNamespaceManager(doc.NameTable);

                var tus = doc.SelectNodes("//tu");
                if (tus == null) return "";

                foreach (XmlNode tu in tus)
                {
                    var tuvs = tu.SelectNodes("tuv");
                    if (tuvs == null) continue;

                    var parts = new List<string>();
                    foreach (XmlNode tuv in tuvs)
                    {
                        var lang = tuv.Attributes?["xml:lang"]?.Value
                                ?? tuv.Attributes?["lang"]?.Value ?? "?";
                        var seg = tuv.SelectSingleNode("seg");
                        if (seg != null)
                            parts.Add($"[{lang}] {seg.InnerText.Trim()}");
                    }

                    if (parts.Count > 0)
                    {
                        sb.AppendLine(string.Join(" → ", parts));
                    }
                }
            }
            catch
            {
                // Fallback: strip tags
                return ReadAndStripXmlTags(path);
            }

            return sb.ToString();
        }

        // ─── XLIFF / SDLXLIFF ────────────────────────────────────────

        private static string ExtractXliff(string path)
        {
            var sb = new StringBuilder();
            try
            {
                var doc = new XmlDocument();
                doc.Load(path);

                // Handle namespaced XLIFF (1.2, 2.0, SDL variants)
                var nsMgr = new XmlNamespaceManager(doc.NameTable);

                // Try common XLIFF namespaces
                var root = doc.DocumentElement;
                var ns = root?.NamespaceURI ?? "";
                if (!string.IsNullOrEmpty(ns))
                    nsMgr.AddNamespace("x", ns);

                var prefix = string.IsNullOrEmpty(ns) ? "" : "x:";

                var units = doc.SelectNodes($"//{prefix}trans-unit", nsMgr);
                if (units == null || units.Count == 0)
                    units = doc.SelectNodes($"//{prefix}unit", nsMgr); // XLIFF 2.0

                if (units == null || units.Count == 0)
                    return ReadAndStripXmlTags(path);

                foreach (XmlNode unit in units)
                {
                    var source = unit.SelectSingleNode($"{prefix}source", nsMgr)
                              ?? unit.SelectSingleNode($"{prefix}segment/{prefix}source", nsMgr);
                    var target = unit.SelectSingleNode($"{prefix}target", nsMgr)
                              ?? unit.SelectSingleNode($"{prefix}segment/{prefix}target", nsMgr);

                    var srcText = source?.InnerText?.Trim() ?? "";
                    var tgtText = target?.InnerText?.Trim() ?? "";

                    if (!string.IsNullOrEmpty(srcText))
                    {
                        sb.Append(srcText);
                        if (!string.IsNullOrEmpty(tgtText))
                            sb.Append(" → ").Append(tgtText);
                        sb.AppendLine();
                    }
                }
            }
            catch
            {
                return ReadAndStripXmlTags(path);
            }

            return sb.ToString();
        }

        // ─── TBX ──────────────────────────────────────────────────────

        private static string ExtractTbx(string path)
        {
            var sb = new StringBuilder();
            try
            {
                var doc = new XmlDocument();
                doc.Load(path);

                var terms = doc.SelectNodes("//termEntry") ?? doc.SelectNodes("//conceptEntry");
                if (terms == null) return ReadAndStripXmlTags(path);

                foreach (XmlNode entry in terms)
                {
                    var langSets = entry.SelectNodes("langSet");
                    if (langSets == null) continue;

                    var parts = new List<string>();
                    foreach (XmlNode ls in langSets)
                    {
                        var lang = ls.Attributes?["xml:lang"]?.Value ?? "?";
                        var term = ls.SelectSingleNode(".//term");
                        if (term != null)
                            parts.Add($"[{lang}] {term.InnerText.Trim()}");
                    }

                    if (parts.Count > 0)
                        sb.AppendLine(string.Join(" → ", parts));
                }
            }
            catch
            {
                return ReadAndStripXmlTags(path);
            }

            return sb.ToString();
        }

        // ─── DOCX ─────────────────────────────────────────────────────

        private static string ExtractDocx(string path)
        {
            var sb = new StringBuilder();
            try
            {
                using (var zip = ZipFile.OpenRead(path))
                {
                    var entry = zip.GetEntry("word/document.xml");
                    if (entry == null) return "";

                    using (var stream = entry.Open())
                    {
                        var doc = new XmlDocument();
                        doc.Load(stream);

                        var nsMgr = new XmlNamespaceManager(doc.NameTable);
                        nsMgr.AddNamespace("w", "http://schemas.openxmlformats.org/wordprocessingml/2006/main");

                        var paragraphs = doc.SelectNodes("//w:p", nsMgr);
                        if (paragraphs == null) return "";

                        foreach (XmlNode para in paragraphs)
                        {
                            var texts = para.SelectNodes(".//w:t", nsMgr);
                            if (texts == null || texts.Count == 0)
                            {
                                sb.AppendLine(); // Empty paragraph = line break
                                continue;
                            }

                            var line = new StringBuilder();
                            foreach (XmlNode t in texts)
                                line.Append(t.InnerText);

                            sb.AppendLine(line.ToString());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not read DOCX file: {ex.Message}", ex);
            }

            return sb.ToString();
        }

        // ─── PPTX ─────────────────────────────────────────────────────

        private static string ExtractPptx(string path)
        {
            var sb = new StringBuilder();
            try
            {
                using (var zip = ZipFile.OpenRead(path))
                {
                    // Slides are in ppt/slides/slide1.xml, slide2.xml, etc.
                    var slideEntries = zip.Entries
                        .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
                                 && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(e => e.FullName)
                        .ToList();

                    int slideNum = 0;
                    foreach (var entry in slideEntries)
                    {
                        slideNum++;
                        using (var stream = entry.Open())
                        {
                            var doc = new XmlDocument();
                            doc.Load(stream);

                            var nsMgr = new XmlNamespaceManager(doc.NameTable);
                            nsMgr.AddNamespace("a", "http://schemas.openxmlformats.org/drawingml/2006/main");

                            var texts = doc.SelectNodes("//a:t", nsMgr);
                            if (texts == null || texts.Count == 0) continue;

                            sb.AppendLine($"--- Slide {slideNum} ---");
                            foreach (XmlNode t in texts)
                            {
                                var txt = t.InnerText?.Trim();
                                if (!string.IsNullOrEmpty(txt))
                                    sb.AppendLine(txt);
                            }
                            sb.AppendLine();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not read PPTX file: {ex.Message}", ex);
            }

            return sb.ToString();
        }

        // ─── XLSX ─────────────────────────────────────────────────────

        private static string ExtractXlsx(string path)
        {
            var sb = new StringBuilder();
            try
            {
                using (var zip = ZipFile.OpenRead(path))
                {
                    // Read shared strings table
                    var sharedStrings = new List<string>();
                    var ssEntry = zip.GetEntry("xl/sharedStrings.xml");
                    if (ssEntry != null)
                    {
                        using (var stream = ssEntry.Open())
                        {
                            var doc = new XmlDocument();
                            doc.Load(stream);
                            var nsMgr = new XmlNamespaceManager(doc.NameTable);
                            nsMgr.AddNamespace("s", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");

                            var sis = doc.SelectNodes("//s:si", nsMgr);
                            if (sis != null)
                            {
                                foreach (XmlNode si in sis)
                                    sharedStrings.Add(si.InnerText);
                            }
                        }
                    }

                    // Read each sheet
                    var sheetEntries = zip.Entries
                        .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
                                 && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(e => e.FullName)
                        .ToList();

                    int sheetNum = 0;
                    foreach (var entry in sheetEntries)
                    {
                        sheetNum++;
                        using (var stream = entry.Open())
                        {
                            var doc = new XmlDocument();
                            doc.Load(stream);
                            var nsMgr = new XmlNamespaceManager(doc.NameTable);
                            nsMgr.AddNamespace("s", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");

                            if (sheetEntries.Count > 1)
                                sb.AppendLine($"--- Sheet {sheetNum} ---");

                            var rows = doc.SelectNodes("//s:row", nsMgr);
                            if (rows == null) continue;

                            foreach (XmlNode row in rows)
                            {
                                var cells = row.SelectNodes("s:c", nsMgr);
                                if (cells == null) continue;

                                var cellValues = new List<string>();
                                foreach (XmlNode cell in cells)
                                {
                                    var type = cell.Attributes?["t"]?.Value;
                                    var valueNode = cell.SelectSingleNode("s:v", nsMgr);
                                    var value = valueNode?.InnerText ?? "";

                                    // Shared string reference
                                    if (type == "s" && int.TryParse(value, out int ssIdx)
                                        && ssIdx >= 0 && ssIdx < sharedStrings.Count)
                                    {
                                        value = sharedStrings[ssIdx];
                                    }

                                    cellValues.Add(value);
                                }

                                if (cellValues.Any(v => !string.IsNullOrEmpty(v)))
                                    sb.AppendLine(string.Join("\t", cellValues));
                            }
                            sb.AppendLine();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not read XLSX file: {ex.Message}", ex);
            }

            return sb.ToString();
        }

        // ─── RTF ──────────────────────────────────────────────────────

        private static string ExtractRtf(string path)
        {
            var raw = File.ReadAllText(path, Encoding.Default);
            var sb = new StringBuilder();

            int depth = 0;
            bool inControl = false;
            var controlWord = new StringBuilder();

            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];

                if (c == '{')
                {
                    depth++;
                    continue;
                }
                if (c == '}')
                {
                    depth--;
                    continue;
                }
                if (c == '\\')
                {
                    inControl = true;
                    controlWord.Clear();
                    continue;
                }

                if (inControl)
                {
                    if (char.IsLetter(c))
                    {
                        controlWord.Append(c);
                        continue;
                    }

                    // End of control word
                    var cw = controlWord.ToString();
                    if (cw == "par" || cw == "line")
                        sb.AppendLine();
                    else if (cw == "tab")
                        sb.Append('\t');

                    inControl = false;
                    controlWord.Clear();

                    if (c == ' ') continue; // Space delimiter after control word

                    // Process this character normally
                    if (depth <= 1 || c == '\n' || c == '\r')
                    {
                        if (c != '\n' && c != '\r')
                            sb.Append(c);
                    }
                    continue;
                }

                if (c != '\n' && c != '\r')
                    sb.Append(c);
            }

            return sb.ToString().Trim();
        }

        // ─── PDF ──────────────────────────────────────────────────────

        private static string ExtractPdf(string path)
        {
            var sb = new StringBuilder();
            try
            {
                using (var reader = new PdfReader(path))
                {
                    for (int page = 1; page <= reader.NumberOfPages; page++)
                    {
                        var strategy = new iTextSharp.text.pdf.parser.SimpleTextExtractionStrategy();
                        var pageText = iTextSharp.text.pdf.parser.PdfTextExtractor.GetTextFromPage(reader, page, strategy);
                        if (!string.IsNullOrWhiteSpace(pageText))
                        {
                            sb.AppendLine(pageText);
                            sb.AppendLine();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not read PDF file: {ex.Message}", ex);
            }

            if (sb.Length == 0)
                throw new InvalidOperationException(
                    "Could not extract any text from this PDF. It may be image-based (scanned).");

            return sb.ToString().Trim();
        }

        // ─── Legacy Office ────────────────────────────────────────────

        private static string ExtractLegacyOffice(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var formatName = ext == ".doc" ? "Word .doc"
                           : ext == ".xls" ? "Excel .xls"
                           : "PowerPoint .ppt";

            // Best-effort: try to extract readable strings from the binary
            var bytes = File.ReadAllBytes(path);
            var sb = new StringBuilder();

            // Look for runs of printable text (at least 8 chars)
            var current = new StringBuilder();
            foreach (var b in bytes)
            {
                if (b >= 32 && b < 127)
                {
                    current.Append((char)b);
                }
                else
                {
                    if (current.Length >= 8)
                    {
                        var s = current.ToString().Trim();
                        if (s.Length >= 8 && !IsLikelyBinaryJunk(s))
                            sb.AppendLine(s);
                    }
                    current.Clear();
                }
            }

            if (sb.Length == 0)
                throw new InvalidOperationException(
                    $"This is a legacy {formatName} file. For best results, save it as " +
                    $"{(ext == ".doc" ? ".docx" : ext == ".xls" ? ".xlsx" : ".pptx")} first, " +
                    "then attach the converted file.");

            return $"[Note: Legacy {formatName} format — text extraction is approximate. " +
                   $"For best results, save as {(ext == ".doc" ? ".docx" : ext == ".xls" ? ".xlsx" : ".pptx")} first.]\n\n" +
                   sb.ToString();
        }

        private static bool IsLikelyBinaryJunk(string s)
        {
            // Filter out strings that look like binary data or internal markers
            int nonAlpha = 0;
            foreach (var c in s)
            {
                if (!char.IsLetterOrDigit(c) && c != ' ' && c != '.' && c != ',' && c != '-')
                    nonAlpha++;
            }
            return (double)nonAlpha / s.Length > 0.4;
        }

        /// <summary>
        /// Formats a file size in human-readable form.
        /// </summary>
        public static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        }
    }
}
