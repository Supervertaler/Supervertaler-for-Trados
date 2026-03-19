using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Static helpers for gathering document/project context from the active Trados document.
    /// Used by QuickLauncherAction to populate {{PROJECT}}, {{PROJECT_NAME}},
    /// {{DOCUMENT_NAME}}, and {{SURROUNDING_SEGMENTS}} prompt variables.
    ///
    /// Segment numbering logic mirrors the proofreading system: segment IDs are tracked
    /// per file and reset at file boundaries (detected by the ID counter going backwards),
    /// so the numbers shown here match the per-file numbers displayed in the Trados editor.
    /// </summary>
    internal static class DocumentContextHelper
    {
        // ── Segment number map ───────────────────────────────────────────────────

        /// <summary>
        /// Builds a map of (ParagraphUnitId|SegmentId) → per-file 1-based segment number.
        /// File boundaries are detected when a numeric segment ID resets to a lower value.
        /// This is the same heuristic used by the proofreading system and produces numbers
        /// that match the Trados editor grid.
        /// </summary>
        internal static Dictionary<string, int> BuildSegmentNumberMap(IStudioDocument doc)
        {
            var map = new Dictionary<string, int>();
            if (doc == null) return map;

            int fileSegIdx = 0;
            foreach (var pair in doc.SegmentPairs)
            {
                try
                {
                    var sid = pair.Properties?.Id.Id;
                    int segIdNum;
                    if (int.TryParse(sid, out segIdNum) && segIdNum <= fileSegIdx && fileSegIdx > 0)
                        fileSegIdx = 0; // file boundary detected — restart counter

                    fileSegIdx++;

                    if (!string.IsNullOrEmpty(sid))
                    {
                        var parentPu = doc.GetParentParagraphUnit(pair);
                        var puId = parentPu?.Properties?.ParagraphUnitId.Id ?? "";
                        map[puId + "|" + sid] = fileSegIdx;
                    }
                }
                catch { fileSegIdx++; }
            }

            return map;
        }

        // ── Project / document metadata ──────────────────────────────────────────

        /// <summary>
        /// Gets the Trados project name for the active document.
        /// Derived from the folder structure: the project folder is the grandparent of the
        /// source file, which is the standard Trados project layout.
        /// </summary>
        internal static string GetProjectName(IStudioDocument doc)
        {
            try
            {
                var filePath = doc?.ActiveFile?.SourceFile?.LocalFilePath;
                if (!string.IsNullOrEmpty(filePath))
                {
                    var fileDir = Path.GetDirectoryName(filePath);       // e.g. …/ProjectName/en-US/
                    var projectDir = Path.GetDirectoryName(fileDir);    // e.g. …/ProjectName/
                    if (!string.IsNullOrEmpty(projectDir))
                        return Path.GetFileName(projectDir);
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// Gets the file name of the active document (e.g. "EP3456789A1.docx").
        /// </summary>
        internal static string GetDocumentName(IStudioDocument doc)
        {
            try { return doc?.ActiveFile?.Name ?? ""; }
            catch { return ""; }
        }

        // ── {{PROJECT}} — full document source text ──────────────────────────────

        /// <summary>
        /// Formats all source segments from the document as a numbered list for use in the
        /// {{PROJECT}} prompt variable. Each segment is prefixed with its actual Trados
        /// per-file segment number in square brackets.
        ///
        /// In multi-file projects a "=== File N ===" header is inserted at each file
        /// boundary so the AI can reference "segment 4 in File 2" unambiguously.
        /// </summary>
        internal static string FormatProjectText(IStudioDocument doc)
        {
            if (doc == null) return "";

            var sb = new StringBuilder();
            int fileSegIdx = 0;
            int fileNumber = 1;
            bool firstSegment = true;

            foreach (var pair in doc.SegmentPairs)
            {
                try
                {
                    var sid = pair.Properties?.Id.Id;
                    int segIdNum;
                    bool isFileBoundary = !firstSegment
                        && int.TryParse(sid, out segIdNum)
                        && segIdNum <= fileSegIdx
                        && fileSegIdx > 0;

                    if (isFileBoundary)
                    {
                        fileSegIdx = 0;
                        fileNumber++;
                        sb.AppendLine();
                        sb.AppendLine($"=== File {fileNumber} ===");
                    }
                    else if (firstSegment)
                    {
                        firstSegment = false;
                    }

                    fileSegIdx++;
                    var src = pair.Source?.ToString() ?? "";
                    sb.Append('[').Append(fileSegIdx).Append("] ").AppendLine(src);
                }
                catch { }
            }

            return sb.ToString().TrimEnd();
        }

        // ── {{SURROUNDING_SEGMENTS}} — context window ────────────────────────────

        /// <summary>
        /// Formats <paramref name="count"/> source segments before and after the active
        /// segment (inclusive of the active segment, which is marked with "← ACTIVE").
        /// Actual per-file Trados segment numbers are used so references are unambiguous.
        /// </summary>
        internal static string FormatSurroundingSegments(IStudioDocument doc, int count)
        {
            if (doc == null || count <= 0) return "";

            try
            {
                var activePair = doc.ActiveSegmentPair;
                if (activePair == null) return "";

                string activeSegId = null;
                string activePuId = null;
                try
                {
                    activeSegId = activePair.Properties.Id.Id;
                    var parentPU = doc.GetParentParagraphUnit(activePair);
                    activePuId = parentPU.Properties.ParagraphUnitId.Id;
                }
                catch { return ""; }

                if (activePuId == null || activeSegId == null) return "";

                // Single pass: collect all pairs, track segment numbers, find active index
                var entries = new List<(string source, int segNum)>();
                int activeIdx = -1;
                int fileSegIdx = 0;
                int idx = 0;

                foreach (var pair in doc.SegmentPairs)
                {
                    try
                    {
                        var sid = pair.Properties?.Id.Id;
                        var parentPU = doc.GetParentParagraphUnit(pair);
                        var puId = parentPU?.Properties?.ParagraphUnitId.Id ?? "";

                        int segIdNum;
                        if (int.TryParse(sid, out segIdNum) && segIdNum <= fileSegIdx && fileSegIdx > 0)
                            fileSegIdx = 0;

                        fileSegIdx++;

                        var src = pair.Source?.ToString() ?? "";
                        entries.Add((src, fileSegIdx));

                        if (activeIdx < 0 && puId == activePuId && sid == activeSegId)
                            activeIdx = idx;
                    }
                    catch { entries.Add(("", 0)); }

                    idx++;
                }

                if (activeIdx < 0) return "";

                var sb = new StringBuilder();
                int start = Math.Max(0, activeIdx - count);
                int end = Math.Min(entries.Count - 1, activeIdx + count);

                for (int i = start; i <= end; i++)
                {
                    var (src, segNum) = entries[i];
                    if (i == activeIdx)
                        sb.Append('[').Append(segNum).Append(" \u2190 ACTIVE] ").AppendLine(src);
                    else
                        sb.Append('[').Append(segNum).Append("] ").AppendLine(src);
                }

                return sb.ToString().TrimEnd();
            }
            catch { return ""; }
        }

        // ── TM match extraction ────────────────────────────────────────────────

        /// <summary>
        /// Extracts TM match information from the active segment's translation origin.
        /// Returns matches that originated from a translation memory (TM or auto-propagated).
        /// Reusable from both AiAssistantViewPart and QuickLauncherAction.
        /// </summary>
        internal static List<TmMatch> GetTmMatches(IStudioDocument doc)
        {
            var matches = new List<TmMatch>();
            try
            {
                var pair = doc?.ActiveSegmentPair;
                if (pair == null) return matches;

                var origin = pair.Properties?.TranslationOrigin;
                if (origin == null) return matches;

                var originType = origin.OriginType;
                if (string.IsNullOrEmpty(originType)) return matches;

                // Include TM matches and auto-propagated segments (which originate from TM)
                if (originType == "tm" || originType == "auto-propagated")
                {
                    var sourceText = pair.Source?.ToString();
                    var targetText = pair.Target?.ToString();

                    if (!string.IsNullOrEmpty(sourceText) && !string.IsNullOrEmpty(targetText))
                    {
                        matches.Add(new TmMatch
                        {
                            SourceText = sourceText,
                            TargetText = targetText,
                            MatchPercentage = origin.MatchPercent,
                            TmName = origin.OriginSystem ?? ""
                        });
                    }
                }
            }
            catch
            {
                // Segment properties may not be accessible during transitions
            }
            return matches;
        }
    }
}
