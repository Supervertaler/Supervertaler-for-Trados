using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Builds system and user prompts for AI translation, and parses batch responses.
    /// Supports composable prompt assembly: Base → Custom → Termbase.
    /// Ported from Python Supervertaler's UnifiedPromptLibrary / build_final_prompt().
    /// </summary>
    public static class TranslationPrompt
    {
        /// <summary>
        /// Builds the base system prompt with critical translation instructions.
        /// This includes persona, core rules, tag preservation, and number formatting.
        /// Always included — custom prompts are appended after this.
        /// </summary>
        public static string BuildBaseSystemPrompt(string sourceLang, string targetLang)
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

            // Numbered tag placeholder preservation (Trados inline tags)
            sb.AppendLine("**CRITICAL: NUMBERED TAG PLACEHOLDER PRESERVATION**:");
            sb.AppendLine("- Source segments may contain numbered tag placeholders representing inline formatting:");
            sb.AppendLine("  - Paired tags: <t1>formatted text</t1>, <t2>another format</t2>");
            sb.AppendLine("  - Standalone tags: <t3/> (e.g., page breaks, field codes)");
            sb.AppendLine("- You MUST preserve ALL tag placeholders exactly as they appear");
            sb.AppendLine("- Place paired tags around the CORRESPONDING translated words");
            sb.AppendLine("- Keep standalone tags (<tN/>) in the correct position relative to the translated text");
            sb.AppendLine("- If source has N tags, target must have exactly N tags with the same numbers");
            sb.AppendLine("- Never translate, remove, or modify the tag placeholders themselves");
            sb.AppendLine("- Tags may be reordered to fit natural target language word order");
            sb.AppendLine("- Example: \"Click the <t1>Save</t1> button\" → \"Klik op de knop <t1>Opslaan</t1>\"");
            sb.AppendLine("- Example: \"Page <t2/> of <t3/>\" → \"Pagina <t2/> van <t3/>\"");
            sb.AppendLine();

            // Generic formatting tag preservation (for non-Trados sources)
            sb.AppendLine("**INLINE FORMATTING TAG PRESERVATION**:");
            sb.AppendLine("- Source text may also contain formatting tags: <b>bold</b>, <i>italic</i>, <u>underline</u>");
            sb.AppendLine("- These tags MUST be preserved in the translation");
            sb.AppendLine("- Place tags around the CORRESPONDING translated words");
            sb.AppendLine("- Ensure every opening tag has a matching closing tag");
            sb.AppendLine();

            // Number formatting
            sb.AppendLine("**LANGUAGE-SPECIFIC NUMBER FORMATTING**:");
            sb.AppendLine("- If the target language is Dutch, French, German, Italian, Spanish, or another " +
                "continental European language, use a comma as the decimal separator (e.g., 17,1 cm).");
            sb.AppendLine("- If the target language is English or Irish, use a period as the decimal " +
                "separator (e.g., 17.1 cm).");
            sb.AppendLine("- Follow the number formatting conventions of the target language.");

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Returns the default base system prompt text (for display in settings UI).
        /// </summary>
        public static string GetDefaultBaseSystemPrompt()
        {
            return BuildBaseSystemPrompt("{{SOURCE_LANGUAGE}}", "{{TARGET_LANGUAGE}}");
        }

        /// <summary>
        /// Builds the full system prompt by composing: Base → Custom Prompt → Termbase.
        /// This is the main entry point used by both batch and single-segment translate.
        /// </summary>
        /// <param name="sourceLang">Source language display name</param>
        /// <param name="targetLang">Target language display name</param>
        /// <param name="customPromptContent">Optional custom prompt content (from library); already variable-substituted</param>
        /// <param name="termbaseTerms">Optional termbase terms to inject</param>
        /// <param name="customSystemPrompt">Optional system prompt override (replaces base prompt entirely)</param>
        public static string BuildSystemPrompt(string sourceLang, string targetLang,
            string customPromptContent = null,
            List<TermEntry> termbaseTerms = null,
            string customSystemPrompt = null,
            List<string> documentSegments = null,
            int maxDocumentSegments = 500,
            bool includeTermMetadata = true,
            string kbContext = null)
        {
            var sb = new StringBuilder(4096);

            // Layer 1: Base system prompt (or user's custom override)
            if (!string.IsNullOrWhiteSpace(customSystemPrompt))
            {
                // User has overridden the system prompt — apply variable substitution
                var resolved = PromptLibrary.ApplyVariables(customSystemPrompt, sourceLang, targetLang);
                sb.Append(resolved);
            }
            else
            {
                sb.Append(BuildBaseSystemPrompt(sourceLang, targetLang));
            }

            // Layer 2: Custom prompt from library (appended as additional instructions)
            if (!string.IsNullOrWhiteSpace(customPromptContent))
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine("# CUSTOM INSTRUCTIONS");
                sb.AppendLine();
                sb.Append(customPromptContent);
            }

            // Layer 3: SuperMemory knowledge base context
            if (!string.IsNullOrWhiteSpace(kbContext))
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.Append(kbContext);
            }

            // Layer 4: Termbase injection
            if (termbaseTerms != null && termbaseTerms.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine("# TERMBASE");
                sb.AppendLine();
                sb.AppendLine("Use these approved terms consistently in your translation:");
                sb.AppendLine();
                foreach (var term in termbaseTerms)
                {
                    if (string.IsNullOrEmpty(term.SourceTerm) || string.IsNullOrEmpty(term.TargetTerm))
                        continue;

                    if (term.Forbidden)
                        sb.AppendLine("- " + term.SourceTerm + " \u2192 \u26A0\uFE0F DO NOT USE: " + term.TargetTerm);
                    else if (term.IsNonTranslatable)
                        sb.AppendLine("- " + term.SourceTerm + " \u2192 " + term.TargetTerm + " (do not translate)");
                    else
                        sb.AppendLine("- " + term.SourceTerm + " \u2192 " + term.TargetTerm);

                    // Include term metadata (domain, definition, notes, client)
                    if (includeTermMetadata)
                    {
                        if (!string.IsNullOrWhiteSpace(term.Client))
                            sb.Append("  Client: ").AppendLine(term.Client);
                        if (!string.IsNullOrWhiteSpace(term.Domain))
                            sb.Append("  Domain: ").AppendLine(term.Domain);
                        if (!string.IsNullOrWhiteSpace(term.Definition))
                            sb.Append("  Definition: ").AppendLine(term.Definition);
                        if (!string.IsNullOrWhiteSpace(term.Notes))
                            sb.Append("  Notes: ").AppendLine(term.Notes);
                    }

                    // Include abbreviation pair if available
                    if (!string.IsNullOrEmpty(term.SourceAbbreviation) && !string.IsNullOrEmpty(term.TargetAbbreviation))
                        sb.AppendLine("- " + term.SourceAbbreviation + " \u2192 " + term.TargetAbbreviation + " (abbreviation of: " + term.SourceTerm + ")");
                }
            }

            // Document context (all source segments for document type analysis)
            if (documentSegments != null && documentSegments.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine("# DOCUMENT CONTENT");
                sb.AppendLine("The following is the source document content. Analyze it to determine the document type");
                sb.AppendLine("(legal, medical, technical, marketing, financial, patent, scientific, etc.) and use that");
                sb.AppendLine("assessment to inform your translation style, terminology choices, and register.");
                sb.AppendLine();

                var max = maxDocumentSegments > 0 ? maxDocumentSegments : 500;

                if (documentSegments.Count <= max)
                {
                    for (int i = 0; i < documentSegments.Count; i++)
                    {
                        sb.Append(i + 1).Append(". ").AppendLine(documentSegments[i]);
                    }
                }
                else
                {
                    // Truncate: first 80% of max + last 20% of max
                    int firstCount = (int)(max * 0.8);
                    int lastCount = max - firstCount;
                    int omitted = documentSegments.Count - max;

                    for (int i = 0; i < firstCount; i++)
                    {
                        sb.Append(i + 1).Append(". ").AppendLine(documentSegments[i]);
                    }

                    sb.AppendLine();
                    sb.Append("[... ").Append(omitted).AppendLine(" segments omitted ...]");
                    sb.AppendLine();

                    int startLast = documentSegments.Count - lastCount;
                    for (int i = startLast; i < documentSegments.Count; i++)
                    {
                        sb.Append(i + 1).Append(". ").AppendLine(documentSegments[i]);
                    }
                }
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
