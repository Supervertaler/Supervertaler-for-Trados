using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Result of a cross-file SDLXLIFF search.
    /// </summary>
    public class SearchResult
    {
        public string FilePath;
        public string FileName;
        public string ParagraphUnitId;
        public string SegmentId;
        public int SegmentNumber;
        public string SourceText;
        public string TargetText;
        public string Status;
    }

    /// <summary>
    /// Specifies which segment fields to search.
    /// </summary>
    public enum SearchScope
    {
        SourceAndTarget,
        SourceOnly,
        TargetOnly
    }

    /// <summary>
    /// Searches across all SDLXLIFF files in a Trados project for matching segments.
    /// Parses trans-units to extract source/target text, segment IDs, and confirmation status.
    /// </summary>
    public static class XliffSearcher
    {
        /// <summary>
        /// Maps sdl:conf attribute values to human-readable status names.
        /// </summary>
        private static readonly Dictionary<string, string> ConfirmationMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Unspecified", "Not Translated" },
                { "Draft", "Draft" },
                { "Translated", "Translated" },
                { "RejectedTranslation", "Translation Rejected" },
                { "ApprovedTranslation", "Translation Approved" },
                { "RejectedSignOff", "Sign-off Rejected" },
                { "ApprovedSignOff", "Signed Off" }
            };

        /// <summary>
        /// Finds all SDLXLIFF files in the target language folder of the project.
        /// Walks up from the active file to find the project root (directory containing .sdlproj),
        /// then searches only in the target language subfolder (the one containing the active file).
        /// This avoids returning duplicate results from both source and target language folders.
        /// </summary>
        public static List<string> FindProjectXliffFiles(string anyProjectFilePath)
        {
            if (string.IsNullOrEmpty(anyProjectFilePath))
                return new List<string>();

            // Walk up to find the project root (directory with .sdlproj)
            var dir = Path.GetDirectoryName(anyProjectFilePath);
            string projectRoot = null;

            while (!string.IsNullOrEmpty(dir))
            {
                try
                {
                    if (Directory.GetFiles(dir, "*.sdlproj", SearchOption.TopDirectoryOnly).Length > 0)
                    {
                        projectRoot = dir;
                        break;
                    }
                }
                catch { /* permission denied */ }

                var parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }

            if (projectRoot == null)
                return new List<string>();

            // Determine the target language folder.
            // Trados project structure: ProjectRoot/en-GB/file.sdlxliff
            // The active file is always in the target language folder.
            // Find the first directory below projectRoot in the active file's path.
            string targetLangFolder = null;
            var fileDir = Path.GetDirectoryName(anyProjectFilePath);
            while (!string.IsNullOrEmpty(fileDir))
            {
                var parent = Path.GetDirectoryName(fileDir);
                if (string.Equals(parent, projectRoot, StringComparison.OrdinalIgnoreCase))
                {
                    targetLangFolder = fileDir;
                    break;
                }
                if (parent == fileDir) break;
                fileDir = parent;
            }

            // Search only in the target language folder (not all project folders)
            var searchDir = targetLangFolder ?? projectRoot;

            try
            {
                return Directory.EnumerateFiles(searchDir, "*.sdlxliff", SearchOption.AllDirectories)
                    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        /// <summary>
        /// Searches all SDLXLIFF files for segments matching the query.
        /// </summary>
        /// <param name="xliffFiles">SDLXLIFF file paths to search.</param>
        /// <param name="query">Search text or regex pattern.</param>
        /// <param name="scope">Which fields to search (source, target, or both).</param>
        /// <param name="caseSensitive">Whether the search is case-sensitive.</param>
        /// <param name="useRegex">Whether to treat the query as a regex pattern.</param>
        /// <param name="progress">Optional callback: (filesSearched, totalFiles).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>List of matching segments.</returns>
        public static List<SearchResult> Search(
            List<string> xliffFiles,
            string query,
            SearchScope scope,
            bool caseSensitive,
            bool useRegex,
            Action<int, int> progress,
            CancellationToken ct)
        {
            if (string.IsNullOrEmpty(query) || xliffFiles == null || xliffFiles.Count == 0)
                return new List<SearchResult>();

            Regex regex = null;
            string queryLower = null;

            if (useRegex)
            {
                var options = RegexOptions.Compiled;
                if (!caseSensitive) options |= RegexOptions.IgnoreCase;
                try { regex = new Regex(query, options); }
                catch { return new List<SearchResult>(); }
            }
            else if (!caseSensitive)
            {
                queryLower = query.ToLowerInvariant();
            }

            var results = new List<SearchResult>();
            int total = xliffFiles.Count;

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke(i, total);

                var filePath = xliffFiles[i];
                var fileName = Path.GetFileName(filePath);

                try
                {
                    var fileResults = SearchFile(filePath, fileName, query, queryLower, regex, scope, caseSensitive, ct);
                    results.AddRange(fileResults);
                }
                catch (OperationCanceledException) { throw; }
                catch { /* skip unreadable files */ }
            }

            progress?.Invoke(total, total);
            return results;
        }

        private static List<SearchResult> SearchFile(
            string filePath, string fileName,
            string query, string queryLower, Regex regex,
            SearchScope scope, bool caseSensitive,
            CancellationToken ct)
        {
            var results = new List<SearchResult>();
            var doc = new XmlDocument();
            doc.Load(filePath);

            var nsMgr = new XmlNamespaceManager(doc.NameTable);
            var root = doc.DocumentElement;
            var xliffNs = root?.NamespaceURI ?? "";
            if (!string.IsNullOrEmpty(xliffNs))
                nsMgr.AddNamespace("x", xliffNs);

            // SDL namespace for confirmation status
            nsMgr.AddNamespace("sdl", "http://sdl.com/FileTypes/SdlXliff/1.0");

            var prefix = string.IsNullOrEmpty(xliffNs) ? "" : "x:";
            var units = doc.SelectNodes($"//{prefix}trans-unit", nsMgr);
            if (units == null) return results;

            int segNum = 0;

            foreach (XmlNode unit in units)
            {
                ct.ThrowIfCancellationRequested();

                var tuElement = unit as XmlElement;
                var tuId = tuElement?.GetAttribute("id") ?? "";

                // Skip non-translatable trans-units (structure paragraphs, locked content)
                var translateAttr = tuElement?.GetAttribute("translate");
                if (string.Equals(translateAttr, "no", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Each trans-unit may contain multiple <seg-defs>/<seg> and
                // corresponding <mrk mtype="seg"> in source/target.
                // Extract segment-level pairs.
                var segments = ExtractSegments(unit, prefix, nsMgr);

                foreach (var seg in segments)
                {
                    var sourceText = seg.Item1;
                    var targetText = seg.Item2;
                    var segId = seg.Item3;
                    var status = seg.Item4;

                    // Only count segments with actual content (skip empty structure segments)
                    if (string.IsNullOrWhiteSpace(sourceText) && string.IsNullOrWhiteSpace(targetText))
                        continue;

                    segNum++;

                    bool matches = false;

                    if (scope == SearchScope.SourceOnly || scope == SearchScope.SourceAndTarget)
                        matches = IsMatch(sourceText, query, queryLower, regex, caseSensitive);

                    if (!matches && (scope == SearchScope.TargetOnly || scope == SearchScope.SourceAndTarget))
                        matches = IsMatch(targetText, query, queryLower, regex, caseSensitive);

                    if (matches)
                    {
                        results.Add(new SearchResult
                        {
                            FilePath = filePath,
                            FileName = fileName,
                            ParagraphUnitId = tuId,
                            SegmentId = segId,
                            SegmentNumber = segNum,
                            SourceText = sourceText,
                            TargetText = targetText,
                            Status = status
                        });
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Extracts (source, target, segmentId, status) tuples from a trans-unit.
        /// Handles both single-segment and multi-segment (mrk mtype="seg") trans-units.
        /// </summary>
        private static List<Tuple<string, string, string, string>> ExtractSegments(
            XmlNode transUnit, string prefix, XmlNamespaceManager nsMgr)
        {
            var segments = new List<Tuple<string, string, string, string>>();

            var sourceNode = transUnit.SelectSingleNode($"{prefix}source", nsMgr);
            var targetNode = transUnit.SelectSingleNode($"{prefix}target", nsMgr);

            if (sourceNode == null) return segments;

            // SDLXLIFF puts segment markers in <seg-source>, not <source>.
            // <source> has plain unsegmented text; <seg-source> has <mrk mtype="seg" mid="N">.
            var segSourceNode = transUnit.SelectSingleNode($"{prefix}seg-source", nsMgr);
            var sourceMarkers = segSourceNode?.SelectNodes($".//{prefix}mrk[@mtype='seg']", nsMgr);

            // Fall back to checking <source> for markers (older formats)
            if (sourceMarkers == null || sourceMarkers.Count == 0)
                sourceMarkers = sourceNode.SelectNodes($".//{prefix}mrk[@mtype='seg']", nsMgr);

            // Also check <target> for markers (in case seg-source is missing)
            if (sourceMarkers == null || sourceMarkers.Count == 0)
            {
                var targetMarkers = targetNode?.SelectNodes($".//{prefix}mrk[@mtype='seg']", nsMgr);
                if (targetMarkers != null && targetMarkers.Count > 0)
                {
                    // Use target markers to discover segment IDs, get source from <source>
                    foreach (XmlNode tgtMrk in targetMarkers)
                    {
                        var mid = (tgtMrk as XmlElement)?.GetAttribute("mid") ?? "";
                        var tgtText = GetInnerText(tgtMrk);
                        var srcText = GetInnerText(sourceNode);
                        var status = GetSegmentStatus(transUnit, mid, nsMgr);
                        segments.Add(Tuple.Create(srcText, tgtText, mid, status));
                    }
                    return segments;
                }
            }

            if (sourceMarkers != null && sourceMarkers.Count > 0)
            {
                // Segmented trans-unit (one or more <mrk mtype="seg"> markers)
                foreach (XmlNode srcMrk in sourceMarkers)
                {
                    var mid = (srcMrk as XmlElement)?.GetAttribute("mid") ?? "";
                    var srcText = GetInnerText(srcMrk);

                    // Find matching target marker
                    var tgtText = "";
                    if (targetNode != null && !string.IsNullOrEmpty(mid))
                    {
                        var tgtMrk = targetNode.SelectSingleNode(
                            $".//{prefix}mrk[@mtype='seg'][@mid='{mid}']", nsMgr);
                        if (tgtMrk != null)
                            tgtText = GetInnerText(tgtMrk);
                    }

                    // Get confirmation status from seg-defs
                    var status = GetSegmentStatus(transUnit, mid, nsMgr);

                    segments.Add(Tuple.Create(srcText, tgtText, mid, status));
                }
            }
            else
            {
                // Truly unsegmented trans-unit (no markers anywhere)
                var srcText = GetInnerText(sourceNode);
                var tgtText = targetNode != null ? GetInnerText(targetNode) : "";

                // Try to get the segment ID from seg-defs (the id attribute on the first sdl:seg)
                var segDefs = transUnit.SelectSingleNode(".//sdl:seg-defs/sdl:seg", nsMgr);
                var segId = (segDefs as XmlElement)?.GetAttribute("id") ?? "";

                // Try to get status from seg-defs
                var status = GetSegmentStatus(transUnit, null, nsMgr);

                segments.Add(Tuple.Create(srcText, tgtText, segId, status));
            }

            return segments;
        }

        /// <summary>
        /// Gets the confirmation status for a segment from sdl:seg-defs/sdl:seg.
        /// </summary>
        private static string GetSegmentStatus(XmlNode transUnit, string segId, XmlNamespaceManager nsMgr)
        {
            // Look in sdl:seg-defs which lives inside the trans-unit
            // Structure: <sdl:seg-defs><sdl:seg id="1" conf="Translated"/></sdl:seg-defs>
            var segDefs = transUnit.SelectSingleNode(".//sdl:seg-defs", nsMgr);
            if (segDefs == null) return "Not Translated";

            XmlNode seg = null;
            if (!string.IsNullOrEmpty(segId))
                seg = segDefs.SelectSingleNode($"sdl:seg[@id='{segId}']", nsMgr);

            // Fall back to first seg element
            if (seg == null)
                seg = segDefs.SelectSingleNode("sdl:seg", nsMgr);

            if (seg == null) return "Not Translated";

            var conf = (seg as XmlElement)?.GetAttribute("conf") ?? "";
            if (string.IsNullOrEmpty(conf)) return "Not Translated";

            return ConfirmationMap.TryGetValue(conf, out var friendly) ? friendly : conf;
        }

        /// <summary>
        /// Gets the plain text content of an XML node, stripping inline tags.
        /// </summary>
        private static string GetInnerText(XmlNode node)
        {
            return node?.InnerText?.Trim() ?? "";
        }

        private static bool IsMatch(string text, string query, string queryLower, Regex regex, bool caseSensitive)
        {
            if (string.IsNullOrEmpty(text)) return false;

            if (regex != null)
                return regex.IsMatch(text);

            if (caseSensitive)
                return text.IndexOf(query, StringComparison.Ordinal) >= 0;

            return text.ToLowerInvariant().IndexOf(queryLower, StringComparison.Ordinal) >= 0;
        }
    }
}
