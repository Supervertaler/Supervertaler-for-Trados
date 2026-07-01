using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Supervertaler.Trados.Core.Export
{
    /// <summary>
    /// Orchestrates a bilingual export: picks the right <see cref="IExportRenderer"/>
    /// for the chosen format, writes both the export file and the sidecar
    /// manifest JSON, and returns the manifest so the caller can register it
    /// in the export-history list.
    ///
    /// Pure: no Trados SDK calls here. The caller (the ViewPart) builds the
    /// <see cref="ExportSegment"/> list by walking the active document and
    /// hands it in.
    /// </summary>
    public class BilingualExporter
    {
        public ExportManifest Export(List<ExportSegment> segments, ExportOptions options, string outputPath)
        {
            // Fill in source hashes so re-import can detect source tampering.
            foreach (var seg in segments)
            {
                if (string.IsNullOrEmpty(seg.SourceHash))
                    seg.SourceHash = HashPrefix(seg.SourceText ?? "");
            }

            IExportRenderer renderer = SelectRenderer(options.Format);
            renderer.Render(segments, options, outputPath);

            var manifest = BuildManifest(segments, options, outputPath);
            manifest.Save(ExportManifest.SidecarPathFor(outputPath));
            return manifest;
        }

        private static IExportRenderer SelectRenderer(ExportFormat format)
        {
            switch (format)
            {
                case ExportFormat.Docx: return new DocxRenderer();
                case ExportFormat.Text: return new BilingualTextRenderer();
                case ExportFormat.Html: return new HtmlRenderer();
                default:                return new DocxRenderer();
            }
        }

        private static ExportManifest BuildManifest(List<ExportSegment> segments, ExportOptions opts, string filePath)
        {
            var m = new ExportManifest
            {
                ProjectName = opts.ProjectName,
                SourceFileName = opts.SourceFileName,
                SourceLanguage = opts.SourceLanguageDisplay,
                TargetLanguage = opts.TargetLanguageDisplay,
                ExportTimestampUtc = DateTime.UtcNow,
                Format = opts.Format.ToString().ToLowerInvariant(),
                Layout = opts.Layout.ToString(),
                ToolVersion = opts.ToolVersion,
                ExportFilePath = filePath
            };
            foreach (var seg in segments)
            {
                m.Segments.Add(new ExportManifestSegment
                {
                    Number = seg.Number,
                    ParagraphUnitId = seg.ParagraphUnitId ?? "",
                    SegmentId = seg.SegmentId ?? "",
                    SourceHash = seg.SourceHash ?? "",
                    Status = seg.Status ?? "",
                    SourceFileId = seg.SourceFileId ?? "",
                    SourceFileName = seg.SourceFileName ?? "",
                    IsLocked = seg.IsLocked
                });
            }
            return m;
        }

        /// <summary>SHA-256, hex-encoded, truncated to 16 chars. Plenty for
        /// detecting "the proofreader changed the source line by accident"
        /// without bloating the manifest.</summary>
        public static string HashPrefix(string text)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""));
                var sb = new StringBuilder();
                for (int i = 0; i < 8 && i < bytes.Length; i++)
                    sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>Suggest a default filename + extension for the chosen format
        /// + layout. Caller can override.</summary>
        public static string DefaultFileName(ExportOptions opts)
        {
            var safe = SanitiseForFileName(opts.ProjectName);
            var layoutSuffix = opts.Format == ExportFormat.Text ? "_bilingual_text" : "_bilingual";
            var ext = opts.Format == ExportFormat.Docx ? ".docx" :
                      opts.Format == ExportFormat.Text ? ".txt" : ".html";
            return safe + layoutSuffix + ext;
        }

        private static string SanitiseForFileName(string input)
        {
            if (string.IsNullOrEmpty(input)) return "project";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(input.Length);
            foreach (var c in input)
            {
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }
            return sb.ToString();
        }
    }
}
