using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// AI-based document context detection for AutoPrompt. Sends a sample of the
    /// source to the LLM and asks it to classify the domain plus describe the text
    /// type. This replaces the keyword-only <see cref="DocumentAnalyzer"/> heuristic
    /// as the authoritative domain source (DocumentAnalyzer is still used for
    /// word/segment statistics and as an offline fallback). The model reads the
    /// actual text instead of counting keywords, so it does not get fooled by
    /// superficial cues (e.g. a creative text with a stray "Fig. 1" read as a
    /// patent). Mirrors the Supervertaler Workbench _classify_document_context flow.
    ///
    /// This class only builds the prompt and parses the reply; the LLM call itself
    /// is made by the caller (AiAssistantViewPart) via its LlmClient.
    /// </summary>
    internal static class DocumentContextClassifier
    {
        /// <summary>
        /// Domains the PromptGenerator templates understand. Kept in sync with
        /// PromptGenerator.DomainTemplates so a returned domain always maps to a
        /// template; anything else falls back to "general".
        /// </summary>
        public static readonly string[] Domains =
            { "general", "patent", "legal", "medical", "technical", "financial", "marketing" };

        public const string SystemPrompt =
            "You are a classifier for a professional translation tool. You read a " +
            "document sample and identify its domain and text type. Reply with ONLY " +
            "a JSON object, no prose, no code fences.";

        /// <summary>
        /// Builds the source sample sent to the classifier. A few thousand
        /// characters is plenty to establish text type without a large call.
        /// </summary>
        public static string BuildSample(List<string> sourceSegments, int maxChars = 4000)
        {
            if (sourceSegments == null || sourceSegments.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (var s in sourceSegments)
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                sb.AppendLine(s);
                if (sb.Length >= maxChars) break;
            }
            var text = sb.ToString().Trim();
            return text.Length > maxChars ? text.Substring(0, maxChars) : text;
        }

        /// <summary>Builds the classification user prompt for a source sample.</summary>
        public static string BuildUserPrompt(string sample)
        {
            var domains = string.Join(", ", Domains);
            var sb = new StringBuilder();
            sb.AppendLine("Classify the following document that is about to be translated.");
            sb.AppendLine();
            sb.AppendLine($"Choose the single best-fitting domain from this list: {domains}.");
            sb.AppendLine("Use 'general' only if none of the specific domains clearly fit.");
            sb.AppendLine("Also give a short (max ~12 words) description of the text type and register");
            sb.AppendLine("(e.g. 'creative marketing copy, playful tone' or 'mechanical patent, formal').");
            sb.AppendLine();
            sb.AppendLine("Return exactly: {\"domain\": \"<one of the list>\", \"description\": \"<short phrase>\"}");
            sb.AppendLine();
            sb.AppendLine("=== DOCUMENT SAMPLE ===");
            sb.Append(sample);
            return sb.ToString();
        }

        /// <summary>
        /// Parses the classifier's JSON reply into a domain + description. Tolerant
        /// of surrounding prose / code fences; unknown or missing domains fall back
        /// to "general". Never throws.
        /// </summary>
        public static void Parse(string response, out string domain, out string description)
        {
            domain = "general";
            description = "";
            if (string.IsNullOrWhiteSpace(response)) return;

            bool parsedOk = false;
            try
            {
                var obj = Regex.Match(response, @"\{.*\}", RegexOptions.Singleline);
                if (obj.Success)
                {
                    var json = obj.Value;
                    var dm = Regex.Match(json, "\"domain\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
                    var de = Regex.Match(json, "\"description\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
                    if (dm.Success)
                    {
                        var raw = dm.Groups[1].Value.Trim().ToLowerInvariant();
                        if (Array.IndexOf(Domains, raw) >= 0) { domain = raw; parsedOk = true; }
                    }
                    if (de.Success)
                    {
                        description = de.Groups[1].Value.Trim();
                        if (description.Length > 120) description = description.Substring(0, 120);
                    }
                }
            }
            catch { /* fall through to bare-word scan */ }

            if (!parsedOk)
            {
                // No clean JSON domain: look for a bare domain word in the reply.
                var low = response.ToLowerInvariant();
                foreach (var d in Domains)
                    if (d != "general" && low.Contains(d)) { domain = d; break; }
            }
        }
    }
}
