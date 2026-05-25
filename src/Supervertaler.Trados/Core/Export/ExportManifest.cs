using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Supervertaler.Trados.Core.Export
{
    /// <summary>
    /// Sidecar JSON written next to every exported file. Records the project /
    /// file / language context and the (Number → Pu/Seg id) mapping so the
    /// importer can write changes back to the exact Trados segments they came
    /// from, even if the user-visible numbering is later perturbed.
    ///
    /// Hand-rolled JSON serialisation (no Newtonsoft / System.Text.Json
    /// dependency) — the schema is tiny and stable, so it's not worth
    /// pulling in another NuGet for it.
    /// </summary>
    public class ExportManifest
    {
        public string Version { get; set; } = "1.0";
        public string ProjectName { get; set; } = "";
        public string SourceFileName { get; set; } = "";
        public string SourceLanguage { get; set; } = "";
        public string TargetLanguage { get; set; } = "";
        public DateTime ExportTimestampUtc { get; set; } = DateTime.UtcNow;
        public string Format { get; set; } = "";   // "docx" / "markdown" / "html"
        public string Layout { get; set; } = "";   // "table" / "stacked_source_top" / "stacked_target_top"
        public string ToolVersion { get; set; } = "";
        public string ExportFilePath { get; set; } = "";

        public List<ExportManifestSegment> Segments { get; set; } = new List<ExportManifestSegment>();

        // ─── Serialisation ──────────────────────────────────────────

        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append('{').Append('\n');
            AppendField(sb, "version", Version, true);
            AppendField(sb, "project_name", ProjectName, true);
            AppendField(sb, "source_file_name", SourceFileName, true);
            AppendField(sb, "source_language", SourceLanguage, true);
            AppendField(sb, "target_language", TargetLanguage, true);
            AppendField(sb, "export_timestamp_utc",
                ExportTimestampUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), true);
            AppendField(sb, "format", Format, true);
            AppendField(sb, "layout", Layout, true);
            AppendField(sb, "tool_version", ToolVersion, true);
            AppendField(sb, "export_file_path", ExportFilePath, true);

            sb.Append("  \"segments\": [\n");
            for (int i = 0; i < Segments.Count; i++)
            {
                var s = Segments[i];
                sb.Append("    {");
                sb.Append("\"number\": ").Append(s.Number).Append(", ");
                sb.Append("\"paragraph_unit_id\": ").Append(JsonString(s.ParagraphUnitId)).Append(", ");
                sb.Append("\"segment_id\": ").Append(JsonString(s.SegmentId)).Append(", ");
                sb.Append("\"source_hash\": ").Append(JsonString(s.SourceHash)).Append(", ");
                sb.Append("\"status\": ").Append(JsonString(s.Status)).Append(", ");
                sb.Append("\"source_file_id\": ").Append(JsonString(s.SourceFileId)).Append(", ");
                sb.Append("\"source_file_name\": ").Append(JsonString(s.SourceFileName)).Append(", ");
                sb.Append("\"is_locked\": ").Append(s.IsLocked ? "true" : "false");
                sb.Append("}");
                if (i < Segments.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            sb.Append("  ]\n");
            sb.Append('}').Append('\n');
            return sb.ToString();
        }

        public void Save(string path)
        {
            File.WriteAllText(path, ToJson(), new UTF8Encoding(false));
        }

        public static ExportManifest Load(string path)
        {
            var text = File.ReadAllText(path);
            return ParseJson(text);
        }

        /// <summary>Returns the conventional sidecar path for a given exported file path.</summary>
        public static string SidecarPathFor(string exportFilePath)
        {
            // e.g. "MyExport.docx" → "MyExport.docx.svexport.json"
            return exportFilePath + ".svexport.json";
        }

        // ─── Hand-rolled JSON helpers ───────────────────────────────

        private static void AppendField(StringBuilder sb, string key, string value, bool trailingComma)
        {
            sb.Append("  \"").Append(key).Append("\": ").Append(JsonString(value));
            sb.Append(trailingComma ? "," : "").Append('\n');
        }

        private static string JsonString(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>Minimalist parser for the exact shape ToJson() emits.
        /// Not a general-purpose JSON parser — but tolerant enough for the
        /// hand-rolled structure plus reasonable whitespace.</summary>
        private static ExportManifest ParseJson(string text)
        {
            var m = new ExportManifest();
            // Strip outer braces; we'll process top-level fields as a flat scan.
            int i = 0;
            SkipWhitespace(text, ref i);
            Expect(text, ref i, '{');

            while (true)
            {
                SkipWhitespace(text, ref i);
                if (i >= text.Length) break;
                if (text[i] == '}') { i++; break; }
                if (text[i] == ',') { i++; continue; }

                string key = ReadJsonString(text, ref i);
                SkipWhitespace(text, ref i);
                Expect(text, ref i, ':');
                SkipWhitespace(text, ref i);

                if (key == "segments")
                {
                    Expect(text, ref i, '[');
                    while (true)
                    {
                        SkipWhitespace(text, ref i);
                        if (text[i] == ']') { i++; break; }
                        if (text[i] == ',') { i++; continue; }
                        var seg = ReadSegmentObject(text, ref i);
                        if (seg != null) m.Segments.Add(seg);
                    }
                }
                else
                {
                    string value = ReadAnyValue(text, ref i);
                    AssignField(m, key, value);
                }
            }
            return m;
        }

        private static ExportManifestSegment ReadSegmentObject(string text, ref int i)
        {
            SkipWhitespace(text, ref i);
            if (i >= text.Length || text[i] != '{') return null;
            i++; // consume '{'

            var seg = new ExportManifestSegment();
            while (true)
            {
                SkipWhitespace(text, ref i);
                if (i >= text.Length) break;
                if (text[i] == '}') { i++; break; }
                if (text[i] == ',') { i++; continue; }

                string key = ReadJsonString(text, ref i);
                SkipWhitespace(text, ref i);
                Expect(text, ref i, ':');
                SkipWhitespace(text, ref i);
                string value = ReadAnyValue(text, ref i);

                switch (key)
                {
                    case "number":
                        int n;
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                            seg.Number = n;
                        break;
                    case "paragraph_unit_id": seg.ParagraphUnitId = StripQuotes(value); break;
                    case "segment_id": seg.SegmentId = StripQuotes(value); break;
                    case "source_hash": seg.SourceHash = StripQuotes(value); break;
                    case "status": seg.Status = StripQuotes(value); break;
                    case "source_file_id": seg.SourceFileId = StripQuotes(value); break;
                    case "source_file_name": seg.SourceFileName = StripQuotes(value); break;
                    case "is_locked":
                        {
                            var v = StripQuotes(value).Trim().ToLowerInvariant();
                            seg.IsLocked = (v == "true" || v == "1");
                        }
                        break;
                }
            }
            return seg;
        }

        private static void AssignField(ExportManifest m, string key, string value)
        {
            switch (key)
            {
                case "version": m.Version = StripQuotes(value); break;
                case "project_name": m.ProjectName = StripQuotes(value); break;
                case "source_file_name": m.SourceFileName = StripQuotes(value); break;
                case "source_language": m.SourceLanguage = StripQuotes(value); break;
                case "target_language": m.TargetLanguage = StripQuotes(value); break;
                case "export_timestamp_utc":
                    var iso = StripQuotes(value);
                    DateTime dt;
                    if (DateTime.TryParse(iso, CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dt))
                        m.ExportTimestampUtc = dt;
                    break;
                case "format": m.Format = StripQuotes(value); break;
                case "layout": m.Layout = StripQuotes(value); break;
                case "tool_version": m.ToolVersion = StripQuotes(value); break;
                case "export_file_path": m.ExportFilePath = StripQuotes(value); break;
            }
        }

        private static void SkipWhitespace(string text, ref int i)
        {
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t' || text[i] == '\n' || text[i] == '\r'))
                i++;
        }

        private static void Expect(string text, ref int i, char c)
        {
            if (i >= text.Length || text[i] != c)
                throw new FormatException("Manifest JSON parse error at offset " + i + ": expected '" + c + "'");
            i++;
        }

        private static string ReadJsonString(string text, ref int i)
        {
            Expect(text, ref i, '"');
            var sb = new StringBuilder();
            while (i < text.Length)
            {
                char c = text[i++];
                if (c == '"') return sb.ToString();
                if (c == '\\' && i < text.Length)
                {
                    char esc = text[i++];
                    switch (esc)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'u':
                            if (i + 4 <= text.Length)
                            {
                                int code = int.Parse(text.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                                sb.Append((char)code);
                                i += 4;
                            }
                            break;
                        default: sb.Append(esc); break;
                    }
                }
                else sb.Append(c);
            }
            throw new FormatException("Unterminated string in manifest JSON");
        }

        private static string ReadAnyValue(string text, ref int i)
        {
            SkipWhitespace(text, ref i);
            if (i >= text.Length) return "";
            // String?
            if (text[i] == '"')
            {
                // Capture the raw string including quotes so callers can decide
                // whether to strip them.
                int start = i;
                ReadJsonString(text, ref i);
                return text.Substring(start, i - start);
            }
            // Number / literal — read until comma/brace/bracket/whitespace.
            int s = i;
            while (i < text.Length && text[i] != ',' && text[i] != '}' && text[i] != ']'
                   && text[i] != '\n' && text[i] != '\r')
                i++;
            return text.Substring(s, i - s).Trim();
        }

        private static string StripQuotes(string s)
        {
            if (s == null) return null;
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
            {
                // Re-decode via ReadJsonString to handle escapes uniformly.
                int j = 0;
                return ReadJsonString(s, ref j);
            }
            return s;
        }
    }

    public class ExportManifestSegment
    {
        public int Number { get; set; }
        public string ParagraphUnitId { get; set; } = "";
        public string SegmentId { get; set; } = "";
        public string SourceHash { get; set; } = "";
        public string Status { get; set; } = "";

        /// <summary>Trados file id (string GUID) the segment came from.
        /// Empty for single-file exports; populated in multi-file mode
        /// so re-import can route each diff to the correct source file
        /// in a merged document.</summary>
        public string SourceFileId { get; set; } = "";

        /// <summary>Human-readable source filename (e.g. "Chapter2.docx")
        /// the segment came from. Mirrors SourceFileId for UX —
        /// surfaced in re-import warnings and log lines.</summary>
        public string SourceFileName { get; set; } = "";

        /// <summary>v4.20.18: snapshot of the segment's IsLocked flag at
        /// export time. Mostly informational — the re-import path queries
        /// the LIVE Trados document for the current locked state via
        /// SnapshotLockedSegments(), since the lock may have been
        /// toggled between export and re-import.</summary>
        public bool IsLocked { get; set; }
    }
}
