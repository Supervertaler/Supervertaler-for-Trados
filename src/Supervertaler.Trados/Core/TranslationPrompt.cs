using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Builds system and user prompts for AI translation, and parses batch responses.
    /// Ported from Python Supervertaler's UnifiedPromptLibrary / build_final_prompt().
    /// </summary>
    public static class TranslationPrompt
    {
        /// <summary>
        /// Builds the system prompt for translation, including glossary injection.
        /// </summary>
        public static string BuildSystemPrompt(string sourceLang, string targetLang,
            List<TermEntry> glossaryTerms = null)
        {
            var sb = new StringBuilder(4096);

            sb.AppendLine("You are an expert " + sourceLang + " to " + targetLang +
                " translator with deep understanding of context and nuance.");
            sb.AppendLine();
            sb.AppendLine("**YOUR TASK**: Translate the text segments provided below.");
            sb.AppendLine();

            // Core instructions
            sb.AppendLine("**IMPORTANT INSTRUCTIONS**:");
            sb.AppendLine("- Provide ONLY the translated text");
            sb.AppendLine("- Do NOT include commentary, explanations, or the original text");
            sb.AppendLine("- Maintain accuracy and natural fluency");
            sb.AppendLine();

            // Tag preservation — critical for CAT tools
            sb.AppendLine("**CRITICAL: INLINE FORMATTING TAG PRESERVATION**:");
            sb.AppendLine("- Source text may contain formatting tags: <b>bold</b>, <i>italic</i>, <u>underline</u>");
            sb.AppendLine("- These tags MUST be preserved in the translation");
            sb.AppendLine("- Place tags around the CORRESPONDING translated words");
            sb.AppendLine("- Example: \"Click the <b>Save</b> button\" -> \"Klik op de knop <b>Opslaan</b>\"");
            sb.AppendLine("- Ensure every opening tag has a matching closing tag");
            sb.AppendLine();

            // CAT tool tags (Trados, memoQ, CafeTran, etc.)
            sb.AppendLine("**CRITICAL: CAT TOOL TAG PRESERVATION**:");
            sb.AppendLine("- Source may contain CAT tool formatting tags in various formats:");
            sb.AppendLine("  - Trados Studio: <cf bold=True>text</cf>, <field name=\"Page\" value=\"2\"/>");
            sb.AppendLine("  - memoQ: [1}, {2], [3}, {4] (asymmetric bracket-brace pairs)");
            sb.AppendLine("  - Numbered tags: <410>text</410>, <434>text</434>");
            sb.AppendLine("  - CafeTran: |formatted text| (pipe symbols)");
            sb.AppendLine("- PRESERVE ALL tags exactly as they appear");
            sb.AppendLine("- If source has N tags, target must have exactly N tags");
            sb.AppendLine("- Reposition tags for natural target language word order");
            sb.AppendLine("- Never translate, omit, or modify the tags themselves");
            sb.AppendLine();

            // Number formatting
            sb.AppendLine("**LANGUAGE-SPECIFIC NUMBER FORMATTING**:");
            sb.AppendLine("- If the target language is Dutch, French, German, Italian, Spanish, or another " +
                "continental European language, use a comma as the decimal separator (e.g., 17,1 cm).");
            sb.AppendLine("- If the target language is English or Irish, use a period as the decimal " +
                "separator (e.g., 17.1 cm).");
            sb.AppendLine("- Follow the number formatting conventions of the target language.");
            sb.AppendLine();

            // Glossary injection
            if (glossaryTerms != null && glossaryTerms.Count > 0)
            {
                sb.AppendLine("**GLOSSARY** \u2014 Use these approved terms consistently in your translation:");
                sb.AppendLine();
                foreach (var term in glossaryTerms)
                {
                    if (string.IsNullOrEmpty(term.SourceTerm) || string.IsNullOrEmpty(term.TargetTerm))
                        continue;

                    if (term.Forbidden)
                        sb.AppendLine("- " + term.SourceTerm + " \u2192 \u26A0\uFE0F DO NOT USE: " + term.TargetTerm);
                    else if (term.IsNonTranslatable)
                        sb.AppendLine("- " + term.SourceTerm + " \u2192 " + term.TargetTerm + " (do not translate)");
                    else
                        sb.AppendLine("- " + term.SourceTerm + " \u2192 " + term.TargetTerm);
                }
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Builds the user prompt with numbered segments for batch translation.
        /// </summary>
        public static string BuildBatchUserPrompt(List<BatchSegmentInput> segments)
        {
            var sb = new StringBuilder(segments.Count * 200);

            sb.AppendLine("**SEGMENTS TO TRANSLATE (" + segments.Count + " segments):**");
            sb.AppendLine();
            sb.AppendLine("\u26A0\uFE0F CRITICAL INSTRUCTIONS:");
            sb.AppendLine("1. You must provide EXACTLY one translation per segment.");
            sb.AppendLine("2. You MUST translate ALL " + segments.Count + " segments.");
            sb.AppendLine("3. Format: Each translation MUST start with its segment number, a period, " +
                "then a space, then the translation.");
            sb.AppendLine("4. If the source segment contains line breaks, preserve them in your translation. " +
                "The number label (e.g., '1.') appears only ONCE at the start.");
            sb.AppendLine("5. NO explanations, NO commentary, ONLY the numbered translations.");
            sb.AppendLine();

            foreach (var seg in segments)
            {
                sb.AppendLine(seg.Number + ". " + seg.SourceText);
            }

            sb.AppendLine();
            sb.AppendLine("**YOUR TRANSLATIONS (numbered list):**");
            sb.Append("Begin your translations now:");

            return sb.ToString();
        }

        /// <summary>
        /// Parses a batch response with numbered translations.
        /// Tolerant: returns what it can parse even if count mismatches.
        /// </summary>
        public static List<ParsedTranslation> ParseBatchResponse(string response, int expectedCount)
        {
            var results = new List<ParsedTranslation>();
            if (string.IsNullOrWhiteSpace(response))
                return results;

            // Map: number -> translation text
            var map = new Dictionary<int, StringBuilder>();
            int currentNumber = -1;

            var lines = response.Split(new[] { '\n' }, StringSplitOptions.None);
            var numberPattern = new Regex(@"^\s*(\d+)\.\s*(.*)");

            foreach (var line in lines)
            {
                var match = numberPattern.Match(line);
                if (match.Success)
                {
                    currentNumber = int.Parse(match.Groups[1].Value);
                    var text = match.Groups[2].Value;

                    if (!map.ContainsKey(currentNumber))
                        map[currentNumber] = new StringBuilder();
                    else
                        map[currentNumber].AppendLine(); // multiple blocks with same number

                    map[currentNumber].Append(text);
                }
                else if (currentNumber >= 0)
                {
                    // Continuation line — append to current translation
                    map[currentNumber].AppendLine();
                    map[currentNumber].Append(line);
                }
            }

            // Build result list
            foreach (var kvp in map)
            {
                var translation = kvp.Value.ToString().Trim();
                if (!string.IsNullOrEmpty(translation))
                {
                    results.Add(new ParsedTranslation
                    {
                        Number = kvp.Key,
                        Translation = translation
                    });
                }
            }

            return results;
        }
    }

    /// <summary>
    /// Input segment for building batch user prompts.
    /// </summary>
    public class BatchSegmentInput
    {
        public int Number { get; set; }
        public string SourceText { get; set; }
    }

    /// <summary>
    /// A parsed translation from a batch LLM response.
    /// </summary>
    public class ParsedTranslation
    {
        public int Number { get; set; }
        public string Translation { get; set; }
    }
}
