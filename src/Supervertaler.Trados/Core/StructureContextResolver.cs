using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Supervertaler.Core;

namespace Supervertaler.Trados.Core
{
    /// <summary>The list markers of one open document, by paragraph unit (#109).</summary>
    internal sealed class StructureMap
    {
        /// <summary>True when the document is a Word file whose original could be read and that has at least one list.</summary>
        public bool Available;
        public Dictionary<string, string> MarkerByParagraphUnit = new Dictionary<string, string>(StringComparer.Ordinal);
        public int Paragraphs, Numbered, Matched, Unmatched;
        /// <summary>One line for the batch log saying what was found, or why nothing was.</summary>
        public string Note;
    }

    /// <summary>
    /// Computes each paragraph unit's list marker for the open document (#109).
    ///
    /// <para>Studio's Word filter gives every paragraph unit a location context whose
    /// <c>StartsAt</c> metadata is the character offset of the paragraph's element
    /// in the original <c>word/document.xml</c> - and the original .docx is embedded,
    /// base64, in the sdlxliff header. So: decode the original, let the shared reader
    /// (<see cref="DocxStructure"/>) walk every paragraph and render every marker
    /// with the whole document's restarts and continuations, then join Studio's
    /// paragraph units to those paragraphs by offset. No text matching, no guessing.</para>
    ///
    /// <para>One map is cached, for the document last resolved, keyed by the sdlxliff's
    /// path and write time; a batch of 40 segments and a Ctrl+T on one segment cost
    /// the same one decode. Scale: the embedded original is read into memory once
    /// (a 30 MB Word file is a 40 MB base64 string for the duration of one call, on
    /// 32-bit Studio 2024 too); the map itself is a few bytes per numbered paragraph.
    /// Every failure path returns an unavailable map with a note - never an exception
    /// into the batch.</para>
    /// </summary>
    internal static class StructureContextResolver
    {
        private const string LogCategory = "Structure";
        private static readonly object Gate = new object();
        private static string _cachedPath;
        private static DateTime _cachedWriteTime;
        private static StructureMap _cached;

        /// <summary>Forget the cached map (document changed).</summary>
        public static void Invalidate()
        {
            lock (Gate) { _cachedPath = null; _cached = null; }
        }

        public static StructureMap Resolve(IStudioDocument doc)
        {
            string path = null;
            try { path = doc?.ActiveFile?.LocalFilePath; } catch { }
            if (string.IsNullOrEmpty(path))
                return new StructureMap { Note = "Structure context: no file path for the open document." };

            DateTime writeTime = DateTime.MinValue;
            try { writeTime = File.GetLastWriteTimeUtc(path); } catch { }

            lock (Gate)
            {
                if (_cached != null && string.Equals(_cachedPath, path, StringComparison.OrdinalIgnoreCase) && _cachedWriteTime == writeTime)
                    return _cached;
            }

            var map = Build(doc, path);
            lock (Gate) { _cachedPath = path; _cachedWriteTime = writeTime; _cached = map; }
            return map;
        }

        private static StructureMap Build(IStudioDocument doc, string path)
        {
            var map = new StructureMap();
            try
            {
                List<DocxParagraph> paragraphs;
                using (var original = ReadEmbeddedOriginal(path))
                {
                    if (original == null)
                    {
                        map.Note = "Structure context: not available for this file (no embedded Word original).";
                        DiagnosticLog.Log(LogCategory, map.Note + " " + Path.GetFileName(path));
                        return map;
                    }
                    paragraphs = DocxStructure.ReadParagraphs(original);
                }
                if (paragraphs == null)
                {
                    map.Note = "Structure context: not available for this file (the embedded original is not a Word document).";
                    DiagnosticLog.Log(LogCategory, map.Note + " " + Path.GetFileName(path));
                    return map;
                }

                map.Paragraphs = paragraphs.Count;
                var byOffset = new Dictionary<int, DocxParagraph>(paragraphs.Count);
                foreach (var p in paragraphs)
                {
                    if (p.Marker != null) map.Numbered++;
                    byOffset[p.StartOffset] = p;
                }
                if (map.Numbered == 0)
                {
                    map.Note = "Structure context: the document has no numbered or bulleted paragraphs.";
                    DiagnosticLog.Log(LogCategory, map.Note + " " + Path.GetFileName(path));
                    return map;
                }

                // Join Studio's paragraph units to the paragraphs by offset.
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var pair in doc.SegmentPairs)
                {
                    IParagraphUnit pu = null;
                    try { pu = doc.GetParentParagraphUnit(pair); } catch { }
                    var id = pu?.Properties?.ParagraphUnitId.Id;
                    if (id == null || !seen.Add(id)) continue;

                    int offset = StartsAt(pu);
                    if (offset < 0) { map.Unmatched++; continue; }
                    if (!byOffset.TryGetValue(offset, out var para)) { map.Unmatched++; continue; }
                    map.Matched++;
                    if (para.Marker != null) map.MarkerByParagraphUnit[id] = para.Marker;
                }

                map.Available = map.MarkerByParagraphUnit.Count > 0;
                map.Note = map.Available
                    ? $"Structure context: {map.MarkerByParagraphUnit.Count} list marker(s) found ({map.Numbered} numbered of {map.Paragraphs} paragraphs" +
                      (map.Unmatched > 0 ? $", {map.Unmatched} paragraph unit(s) could not be matched" : "") + ")."
                    : $"Structure context: {map.Numbered} numbered paragraph(s) in the original, but none could be matched to the document's paragraph units.";
                DiagnosticLog.Log(LogCategory, map.Note + " " + Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                map.Available = false;
                map.MarkerByParagraphUnit.Clear();
                map.Note = "Structure context: not available (" + ex.Message + ").";
                DiagnosticLog.Log(LogCategory, "Resolve failed for " + Path.GetFileName(path ?? "") + ": " + ex);
            }
            return map;
        }

        /// <summary>The <c>StartsAt</c> of the paragraph unit's location context, or -1.</summary>
        private static int StartsAt(IParagraphUnit pu)
        {
            try
            {
                var contexts = pu.Properties?.Contexts?.Contexts;
                if (contexts == null) return -1;
                foreach (var ctx in contexts)
                {
                    if (ctx == null) continue;
                    var s = AiAssistantViewPart.TryGetContextMetaData(ctx, "StartsAt");
                    if (!string.IsNullOrEmpty(s) && int.TryParse(s, out var v)) return v;
                }
            }
            catch { }
            return -1;
        }

        /// <summary>
        /// The original .docx of an sdlxliff as a file on disk, for readers that
        /// want a path (the image extractor opens with the OpenXml SDK). Studio
        /// usually keeps its own copy beside the sdlxliff; when it does not, the
        /// embedded original is written to <paramref name="cacheDir"/> once per
        /// version of the sdlxliff. Null when the file is not a Word document or
        /// has no embedded original.
        /// </summary>
        internal static string MaterialiseOriginal(string sdlxliffPath, string cacheDir)
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(sdlxliffPath);   // "X.docx" for "X.docx.sdlxliff"
                if (!name.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)) return null;
                var target = Path.Combine(cacheDir, name);
                var srcTime = File.GetLastWriteTimeUtc(sdlxliffPath);
                if (File.Exists(target) && File.GetLastWriteTimeUtc(target) >= srcTime) return target;

                using (var s = ReadEmbeddedOriginal(sdlxliffPath))
                {
                    if (s == null) return null;
                    Directory.CreateDirectory(cacheDir);
                    using (var zip = new ZipArchive(s, ZipArchiveMode.Read, leaveOpen: true))
                    {
                        if (zip.GetEntry("word/document.xml") != null)
                        {
                            s.Position = 0;
                            using (var f = File.Create(target)) s.CopyTo(f);
                        }
                        else
                        {
                            ZipArchiveEntry inner = null;
                            foreach (var e in zip.Entries) if (e.Length > 0) { inner = e; break; }
                            if (inner == null) return null;
                            using (var es = inner.Open())
                            using (var f = File.Create(target)) es.CopyTo(f);
                        }
                    }
                }
                File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
                return target;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Log(LogCategory, "MaterialiseOriginal failed for " + Path.GetFileName(sdlxliffPath ?? "") + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// The original document embedded in the sdlxliff header
        /// (<c>&lt;internal-file form="base64"&gt;</c>), as a stream, or null when the
        /// file has none. Read line by line up to the closing tag: the blob sits in
        /// the header, so a large document's body is never read.
        /// </summary>
        internal static Stream ReadEmbeddedOriginal(string sdlxliffPath)
        {
            const string open = "<internal-file";
            const string close = "</internal-file>";
            var b64 = new StringBuilder();
            bool inside = false;
            using (var reader = new StreamReader(sdlxliffPath, Encoding.UTF8, true))
            {
                string line;
                int lines = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!inside)
                    {
                        int i = line.IndexOf(open, StringComparison.Ordinal);
                        if (i < 0)
                        {
                            // The header is small; a body without a reference block is not worth reading.
                            if (++lines > 200 && line.IndexOf("<trans-unit", StringComparison.Ordinal) >= 0) return null;
                            continue;
                        }
                        int tagEnd = line.IndexOf('>', i);
                        if (tagEnd < 0) return null;
                        var tag = line.Substring(i, tagEnd - i);
                        if (tag.IndexOf("base64", StringComparison.OrdinalIgnoreCase) < 0) return null;
                        inside = true;
                        line = line.Substring(tagEnd + 1);
                    }
                    int end = line.IndexOf(close, StringComparison.Ordinal);
                    if (end >= 0)
                    {
                        b64.Append(line, 0, end);
                        break;
                    }
                    b64.Append(line);
                }
            }
            if (!inside || b64.Length == 0) return null;
            var bytes = Convert.FromBase64String(b64.ToString());
            return new MemoryStream(bytes, writable: false);
        }
    }
}
