using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Clipboard Mode: formats segments for manual copy/paste to external web-based LLMs
    /// (ChatGPT, Claude, Gemini, etc.) and parses responses back.
    /// Reuses existing prompt building, tag serialisation, and response parsing infrastructure.
    /// </summary>
    public static class ClipboardRelay
    {
        // ─── Translate: format for clipboard ─────────────────────────

        /// <summary>
        /// Builds the full clipboard text for translation: system prompt + numbered
        /// bilingual segment blocks. The user pastes this into any web-based LLM.
        /// </summary>
        public static string FormatForTranslation(
            List<BatchSegment> segments,
            string sourceLang,
            string targetLang,
            string customPromptContent = null,
            List<TermEntry> termbaseTerms = null,
            string customSystemPrompt = null,
            List<string> documentSegments = null,
            int maxDocumentSegments = 500,
            bool includeTermMetadata = true,
            Supervertaler.Core.StructureContextMode structureContext = Supervertaler.Core.StructureContextMode.Off)
        {
            var sb = new StringBuilder(segments.Count * 300 + 4096);

            // Build system prompt (reuses existing infrastructure)
            var systemPrompt = TranslationPrompt.BuildSystemPrompt(
                sourceLang, targetLang,
                customPromptContent, termbaseTerms, customSystemPrompt,
                documentSegments, maxDocumentSegments, includeTermMetadata,
                kbContext: null, structureContext: structureContext);

            sb.AppendLine(systemPrompt);
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();

            // Use short language labels for per-segment lines to save tokens.
            // The full names (with region) are already stated in the system prompt.
            var srcLabel = LanguageUtils.GetBaseLanguageName(sourceLang);
            var tgtLabel = LanguageUtils.GetBaseLanguageName(targetLang);

            // Instructions for the bilingual format. These are deliberately
            // emphatic: some web LLMs (notably DeepSeek's web chat) otherwise
            // reformat the reply into a bare list and drop the "Segment N"
            // headers, which makes the result impossible to re-import.
            sb.Append("Translate the following segments from ").Append(sourceLang)
              .Append(" into ").Append(targetLang).AppendLine(".");
            sb.AppendLine();
            sb.AppendLine("OUTPUT FORMAT — follow EXACTLY; this is critical:");
            sb.AppendLine("- Reproduce every block in the SAME structure shown below, in the SAME order.");
            sb.AppendLine("- Keep the literal \"Segment <n>\" header line for EVERY segment, with the SAME "
                + "number. Do NOT renumber, merge, split, omit, or reorder segments.");
            sb.AppendLine("- Keep the \"" + srcLabel + ":\" line unchanged and put your translation on the \""
                + tgtLabel + ":\" line.");
            sb.AppendLine("- Do NOT reformat the output into a plain list, a table, or prose, and do NOT drop "
                + "the segment numbers. The result is parsed by its \"Segment <n>\" headers to import it back "
                + "into the CAT tool, so losing them breaks re-import.");
            sb.AppendLine("- Do NOT add commentary, explanations, or notes.");
            sb.AppendLine("- Preserve ALL tag placeholders (<t1>, </t1>, <t2/>, etc.) exactly as they appear.");
            sb.AppendLine();

            // Numbered bilingual segments
            for (int i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                var status = GetSegmentStatus(seg);

                sb.Append("Segment ").Append(i + 1);
                if (!string.IsNullOrEmpty(status))
                    sb.Append(" [").Append(status).Append("]");
                sb.AppendLine(":");

                // #109: the list marker in its sentinel, exactly as the API path sends it.
                var sourceForPrompt = structureContext == Supervertaler.Core.StructureContextMode.Markers
                    ? Supervertaler.Core.StructureContext.Prefix(seg.StructureMarker, seg.SourceText)
                    : seg.SourceText;
                sb.Append(srcLabel).Append(": ").AppendLine(sourceForPrompt);
                sb.Append(tgtLabel).Append(": ");

                // Include existing target for fuzzy/translated segments
                if (!string.IsNullOrWhiteSpace(seg.ExistingTarget))
                    sb.AppendLine(seg.ExistingTarget);
                else
                    sb.AppendLine();

                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }

        // ─── Proofread: format for clipboard ─────────────────────────

        /// <summary>
        /// Builds the full clipboard text for proofreading: system prompt + numbered
        /// bilingual segment blocks with both source and target filled in.
        /// </summary>
        public static string FormatForProofreading(
            List<BatchSegment> segments,
            string sourceLang,
            string targetLang,
            string customPromptContent = null,
            List<TermEntry> termbaseTerms = null,
            string customSystemPrompt = null,
            List<(string source, string target)> documentSegments = null,
            bool includeTermMetadata = true)
        {
            var sb = new StringBuilder(segments.Count * 400 + 4096);

            // Build system prompt using proofreading base. Document context is
            // bilingual (source + target) and untruncated — matches the API path so
            // a "what gets sent to the AI" preview is faithful for both modes.
            var systemPrompt = ProofreadingPrompt.BuildSystemPrompt(
                sourceLang, targetLang,
                termbaseTerms, customPromptContent,
                documentSegments, includeTermMetadata);

            sb.AppendLine(systemPrompt);
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();

            // The batch below deliberately reuses the SAME format and the SAME
            // numbering as the API path (ProofreadingPrompt.BuildBatchUserPrompt)
            // and as # DOCUMENT CONTENT: [SEGMENT NNNN] with the document-absolute
            // number.
            //
            // It previously emitted its own "Segment N:" list numbered 1..N over
            // the FILTERED batch, plus a second, contradictory output-format
            // block. Two bugs came out of that:
            //   * the numbers drifted from the document-absolute ones as soon as
            //     any segment was filtered out (a tag-only segment, say), so the
            //     model's verdicts landed on the wrong segments – silently, since
            //     the output still parsed;
            //   * the system prompt above already specifies the output format
            //     ([SEGMENT NNNN] / Issue: / Evidence: / Suggestion:), so a
            //     second spec here just told the model something different, and
            //     a model obeying the last thing it read dropped Evidence:.
            // Both are gone: the numbering comes from seg.Index (which counts
            // every document segment, filtered or not) and the format is defined
            // exactly once, in the system prompt.
            sb.AppendLine("Review the translated segments below.");
            sb.AppendLine();
            sb.AppendLine("Segment numbers are document-absolute and are NOT contiguous: segments with no");
            sb.AppendLine("translatable content are omitted from this batch but still occupy their number.");
            sb.AppendLine("Use the OUTPUT FORMAT defined above, and cite these same numbers in Evidence lines.");
            sb.AppendLine();
            sb.AppendLine("**SEGMENTS TO REVIEW (" + segments.Count + " segments):**");
            sb.AppendLine();

            foreach (var seg in segments)
            {
                sb.Append("[SEGMENT ").Append((seg.Index + 1).ToString("D4")).AppendLine("]");
                sb.Append("Source: ").AppendLine(seg.SourceText);
                sb.Append("Target: ").AppendLine(seg.ExistingTarget ?? "");
                sb.AppendLine();
            }

            sb.Append("**YOUR REVIEW (one verdict per segment):**");

            return sb.ToString().TrimEnd();
        }

        // ─── Translate: parse clipboard response ─────────────────────

        /// <summary>
        /// Parses the bilingual response from the LLM. Expects the format:
        ///   Segment 1:
        ///   {sourceLang}: ...
        ///   {targetLang}: translated text
        ///
        /// Falls back to the existing numbered-list parser if the bilingual
        /// format is not detected.
        /// </summary>
        public static List<ParsedTranslation> ParseTranslationResponse(
            string response, int expectedCount, string targetLang, string sourceLang = null)
        {
            if (string.IsNullOrWhiteSpace(response))
                return new List<ParsedTranslation>();

            // Try bilingual format first
            var results = ParseBilingualResponse(response, targetLang, sourceLang);

            // Fall back to simple numbered list (1. translation)
            if (results.Count == 0)
                results = TranslationPrompt.ParseBatchResponse(response, expectedCount);

            return results;
        }

        /// <summary>
        /// Parses the bilingual segment format:
        ///   Segment N [status]:
        ///   {lang}: source text
        ///   {lang}: target text
        /// </summary>
        private static List<ParsedTranslation> ParseBilingualResponse(
            string response, string targetLang, string sourceLang = null)
        {
            var results = new List<ParsedTranslation>();

            // Match "Segment N" headers (with optional status annotation)
            var segmentPattern = new Regex(
                @"^Segment\s+(\d+)\s*(?:\[.*?\])?\s*:",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);

            var matches = segmentPattern.Matches(response);
            if (matches.Count == 0)
                return results;

            // Build a target language prefix pattern that matches both the short
            // label ("English:") and the full label ("English (United Kingdom):").
            var baseLang = LanguageUtils.GetBaseLanguageName(targetLang);
            var targetPrefix = new Regex(
                @"^\s*" + Regex.Escape(baseLang) + @"(?:\s*\([^)]*\))?\s*:\s*(.*)",
                RegexOptions.IgnoreCase);

            // The ONLY line that legitimately ends a translation inside a block:
            // the source-language label, in case a model emits the pair the other
            // way round. Everything else in the block belongs to the translation.
            Regex sourcePrefix = null;
            if (!string.IsNullOrWhiteSpace(sourceLang))
            {
                var baseSource = LanguageUtils.GetBaseLanguageName(sourceLang);
                if (!string.IsNullOrWhiteSpace(baseSource) &&
                    !baseSource.Equals(baseLang, StringComparison.OrdinalIgnoreCase))
                {
                    sourcePrefix = new Regex(
                        @"^\s*" + Regex.Escape(baseSource) + @"(?:\s*\([^)]*\))?\s*:",
                        RegexOptions.IgnoreCase);
                }
            }

            for (int i = 0; i < matches.Count; i++)
            {
                var segNum = int.Parse(matches[i].Groups[1].Value);

                // Extract the block between this segment header and the next
                int blockStart = matches[i].Index + matches[i].Length;
                int blockEnd = (i + 1 < matches.Count)
                    ? matches[i + 1].Index
                    : response.Length;

                var block = response.Substring(blockStart, blockEnd - blockStart);
                var lines = block.Split(new[] { '\n' }, StringSplitOptions.None);

                // Find the target language line and collect continuation lines
                var targetText = new StringBuilder();
                bool foundTarget = false;

                foreach (var line in lines)
                {
                    if (!foundTarget)
                    {
                        var targetMatch = targetPrefix.Match(line);
                        if (targetMatch.Success)
                        {
                            foundTarget = true;
                            targetText.Append(targetMatch.Groups[1].Value.Trim());
                        }
                        continue;
                    }

                    // Everything from here to the end of the block is part of the
                    // translation. The block is already bounded by the next
                    // "Segment N" header, so no other terminator is needed — and
                    // the two that used to be here both destroyed valid output:
                    //
                    //   * breaking on the first blank line truncated any segment
                    //     whose translation has more than one paragraph, keeping
                    //     only the first (reported by a user, 2026-07-29);
                    //   * breaking on /^\s*\w[\w\s]*:\s/ as a "language label"
                    //     matched any paragraph opening with a word and a colon.
                    //     \w matches Unicode letters in .NET, so a Chinese target
                    //     beginning "注意：" or an English one beginning "Note: "
                    //     was silently cut off at that point.
                    if (sourcePrefix != null && sourcePrefix.IsMatch(line))
                        break;

                    targetText.Append('\n').Append(line.TrimEnd('\r'));
                }

                if (foundTarget)
                {
                    var translation = targetText.ToString().Trim();
                    if (!string.IsNullOrEmpty(translation))
                    {
                        results.Add(new ParsedTranslation
                        {
                            Number = segNum,
                            Translation = translation
                        });
                    }
                }
            }

            return results;
        }

        // ─── Helpers ──────────────────────────────────────────────────

        /// <summary>
        /// Determines the status annotation for a segment (new, fuzzy, draft, translated).
        /// </summary>
        private static string GetSegmentStatus(BatchSegment seg)
        {
            if (string.IsNullOrWhiteSpace(seg.ExistingTarget))
                return "new";

            var pair = seg.SegmentPairRef as ISegmentPair;
            if (pair == null)
                return "draft";

            try
            {
                var origin = pair.Properties?.TranslationOrigin;
                if (origin == null)
                    return "draft";

                var originType = origin.OriginType ?? "";
                var matchPct = origin.MatchPercent;

                // TM fuzzy or auto-propagated
                if (originType.Equals("tm", StringComparison.OrdinalIgnoreCase)
                    || originType.Equals("auto-propagated", StringComparison.OrdinalIgnoreCase))
                {
                    if (matchPct >= 100)
                        return "translated, 100%";
                    return "fuzzy, " + matchPct + "%";
                }

                // Machine translation
                if (originType.Equals("mt", StringComparison.OrdinalIgnoreCase)
                    || originType.Equals("nmt", StringComparison.OrdinalIgnoreCase)
                    || originType.Equals("adaptive-mt", StringComparison.OrdinalIgnoreCase))
                    return "machine translated";

                // Interactive (human-edited)
                if (originType.Equals("interactive", StringComparison.OrdinalIgnoreCase))
                    return "translated";

                return "draft";
            }
            catch
            {
                return "draft";
            }
        }
    }
}
