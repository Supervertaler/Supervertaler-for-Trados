using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// All context data needed to build the AI chat system prompt.
    /// Created fresh on each message send so the LLM always sees the latest state.
    /// </summary>
    public class ChatContext
    {
        public string SourceLang;
        public string TargetLang;
        public string SourceText;              // current segment source
        public string TargetText;              // current segment target
        public List<TermPickerMatch> MatchedTerms;
        public List<TmMatch> TmMatches;

        // Project & document context
        public string ProjectName;
        public string FileName;
        public List<string> DocumentSegments;  // all source segments for document analysis
        public int ActiveSegmentIndex;         // 0-based index of current segment in document
        public int TotalSegmentCount;          // total segments in document
        public int MaxDocumentSegments;        // truncation cap from settings

        // Surrounding segments: each entry is [source, target]
        public List<string[]> SurroundingSegments;

        // Enhanced terminology
        public bool IncludeTermMetadata;       // include definitions, domains, notes

        // SuperMemory KB context (pre-formatted prompt section, or null)
        public string KbContext;
    }

    /// <summary>
    /// Builds the system prompt for the AI Chat Assistant.
    /// Separate from TranslationPrompt because the chat assistant is conversational
    /// rather than a pure translator — it needs different persona and guidelines.
    /// </summary>
    public static class ChatPrompt
    {
        /// <summary>
        /// Builds a context-aware system prompt for the AI chat assistant.
        /// Uses the full ChatContext for rich project/document awareness.
        /// </summary>
        public static string BuildSystemPrompt(ChatContext ctx)
        {
            if (ctx == null)
                return BuildSystemPrompt(null, null, null, null, null, null);

            var sb = new StringBuilder(8192);

            // ── Persona ──────────────────────────────────────────────
            sb.AppendLine("You are a professional translation assistant integrated into Trados Studio.");
            sb.AppendLine("You help translators with their work by answering questions about translations,");
            sb.AppendLine("suggesting improvements, explaining terminology, and providing context.");
            sb.AppendLine();
            sb.AppendLine("# FORMATTING");
            sb.AppendLine("Use proper Markdown formatting in your responses. When presenting tabular data,");
            sb.AppendLine("always use valid Markdown table syntax with pipe delimiters and a separator row:");
            sb.AppendLine("| Header 1 | Header 2 |");
            sb.AppendLine("|----------|----------|");
            sb.AppendLine("| Cell 1   | Cell 2   |");
            sb.AppendLine("Use bullet lists (- item), bold (**text**), and headings (##) where appropriate.");

            // ── Project context ──────────────────────────────────────
            var hasLangPair = !string.IsNullOrEmpty(ctx.SourceLang) && !string.IsNullOrEmpty(ctx.TargetLang);
            if (hasLangPair || !string.IsNullOrEmpty(ctx.ProjectName) || !string.IsNullOrEmpty(ctx.FileName))
            {
                sb.AppendLine();
                sb.AppendLine("# PROJECT CONTEXT");

                if (!string.IsNullOrEmpty(ctx.ProjectName))
                    sb.Append("- Project: ").AppendLine(ctx.ProjectName);

                if (!string.IsNullOrEmpty(ctx.FileName))
                    sb.Append("- File: ").AppendLine(ctx.FileName);

                if (hasLangPair)
                    sb.Append("- Language pair: ").Append(ctx.SourceLang).Append(" \u2192 ").AppendLine(ctx.TargetLang);

                if (ctx.TotalSegmentCount > 0)
                {
                    if (ctx.ActiveSegmentIndex >= 0)
                        sb.Append("- Position: Segment ").Append(ctx.ActiveSegmentIndex + 1)
                          .Append(" of ").AppendLine(ctx.TotalSegmentCount.ToString());
                    else
                        sb.Append("- Total segments: ").AppendLine(ctx.TotalSegmentCount.ToString());
                }
            }

            // ── SuperMemory knowledge base context ─────────────────
            if (!string.IsNullOrWhiteSpace(ctx.KbContext))
            {
                sb.AppendLine();
                sb.AppendLine(ctx.KbContext);
            }

            // ── Document content (all source segments) ───────────────
            if (ctx.DocumentSegments != null && ctx.DocumentSegments.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("# DOCUMENT CONTENT");
                sb.AppendLine("The following is the source document content. Analyze it to determine the document type");
                sb.AppendLine("(legal, medical, technical, marketing, financial, patent, scientific, etc.) and use that");
                sb.AppendLine("assessment to inform all your responses about terminology, style, and translation choices.");
                sb.AppendLine();

                var segments = ctx.DocumentSegments;
                var max = ctx.MaxDocumentSegments > 0 ? ctx.MaxDocumentSegments : 500;

                if (segments.Count <= max)
                {
                    // Include all segments
                    for (int i = 0; i < segments.Count; i++)
                    {
                        sb.Append(i + 1).Append(". ").AppendLine(segments[i]);
                    }
                }
                else
                {
                    // Truncate: first 80% of max + last 20% of max
                    int firstCount = (int)(max * 0.8);
                    int lastCount = max - firstCount;
                    int omitted = segments.Count - max;

                    for (int i = 0; i < firstCount; i++)
                    {
                        sb.Append(i + 1).Append(". ").AppendLine(segments[i]);
                    }

                    sb.AppendLine();
                    sb.Append("[... ").Append(omitted).AppendLine(" segments omitted ...]");
                    sb.AppendLine();

                    int startLast = segments.Count - lastCount;
                    for (int i = startLast; i < segments.Count; i++)
                    {
                        sb.Append(i + 1).Append(". ").AppendLine(segments[i]);
                    }
                }
            }

            // ── Surrounding segments ─────────────────────────────────
            if (ctx.SurroundingSegments != null && ctx.SurroundingSegments.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Nearby Segments");
                sb.AppendLine("Segments surrounding the active segment (for additional context):");
                sb.AppendLine();

                foreach (var pair in ctx.SurroundingSegments)
                {
                    if (pair == null || pair.Length < 2) continue;

                    var src = pair[0] ?? "";
                    var tgt = pair[1] ?? "";

                    sb.Append("- Source: ").AppendLine(src);
                    if (!string.IsNullOrEmpty(tgt))
                        sb.Append("  Target: ").AppendLine(tgt);
                }
            }

            // ── Current segment ──────────────────────────────────────
            if (!string.IsNullOrEmpty(ctx.SourceText))
            {
                sb.AppendLine();
                sb.AppendLine("## Current Source Segment");
                sb.AppendLine(ctx.SourceText);
            }

            if (!string.IsNullOrEmpty(ctx.TargetText))
            {
                sb.AppendLine();
                sb.AppendLine("## Current Target Segment");
                sb.AppendLine(ctx.TargetText);
            }

            // ── TM matches ──────────────────────────────────────────
            if (ctx.TmMatches != null && ctx.TmMatches.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Translation Memory Matches");
                foreach (var tm in ctx.TmMatches)
                {
                    sb.Append("- ").Append(tm.MatchPercentage).Append("% match");
                    if (!string.IsNullOrEmpty(tm.TmName))
                        sb.Append(" (").Append(tm.TmName).Append(")");
                    sb.AppendLine(":");
                    sb.Append("  Source: ").AppendLine(tm.SourceText);
                    sb.Append("  Target: ").AppendLine(tm.TargetText);
                }
            }

            // ── Matched terminology ──────────────────────────────────
            if (ctx.MatchedTerms != null && ctx.MatchedTerms.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Matched Terminology");
                foreach (var match in ctx.MatchedTerms)
                {
                    var targets = match.GetAllTargets();
                    if (targets.Count > 0)
                    {
                        var primary = targets[0];
                        var entry = match.PrimaryEntry;
                        sb.Append("- ").Append(match.SourceText).Append(" \u2192 ").Append(primary.TargetTerm);

                        if (entry != null && entry.IsNonTranslatable)
                            sb.Append(" (do not translate)");
                        else if (entry != null && entry.Forbidden)
                            sb.Append(" (\u26a0\ufe0f forbidden)");

                        // Show synonyms if any
                        if (targets.Count > 1)
                        {
                            var synonyms = targets.Skip(1).Select(t => t.TargetTerm);
                            sb.Append(" (also: ").Append(string.Join(", ", synonyms)).Append(")");
                        }

                        sb.AppendLine();

                        // Enhanced metadata (definition, domain, notes)
                        if (ctx.IncludeTermMetadata && entry != null)
                        {
                            if (!string.IsNullOrWhiteSpace(entry.Domain))
                                sb.Append("  Domain: ").AppendLine(entry.Domain);
                            if (!string.IsNullOrWhiteSpace(entry.Definition))
                                sb.Append("  Definition: ").AppendLine(entry.Definition);
                            if (!string.IsNullOrWhiteSpace(entry.Notes))
                                sb.Append("  Notes: ").AppendLine(entry.Notes);
                        }
                    }
                }
            }

            // ── Guidelines ───────────────────────────────────────────
            sb.AppendLine();
            sb.AppendLine("# GUIDELINES");
            sb.AppendLine("- Answer in the language the user writes in");
            sb.AppendLine("- When suggesting translations, be specific and explain your reasoning");
            sb.AppendLine("- Reference the terminology list and TM matches when relevant");
            sb.AppendLine("- Keep answers concise unless the user asks for detail");
            sb.AppendLine("- If asked to translate or improve text, provide the translation/improvement clearly marked on its own line");

            if (ctx.DocumentSegments != null && ctx.DocumentSegments.Count > 0)
                sb.AppendLine("- Use your analysis of the document type to inform terminology and style recommendations");

            return sb.ToString();
        }

        /// <summary>
        /// Legacy overload — builds a basic system prompt without document/project context.
        /// Delegates to the ChatContext-based overload.
        /// </summary>
        public static string BuildSystemPrompt(
            string sourceLang,
            string targetLang,
            string sourceText,
            string targetText,
            List<TermPickerMatch> matchedTerms,
            List<TmMatch> tmMatches = null)
        {
            var ctx = new ChatContext
            {
                SourceLang = sourceLang,
                TargetLang = targetLang,
                SourceText = sourceText,
                TargetText = targetText,
                MatchedTerms = matchedTerms,
                TmMatches = tmMatches
            };
            return BuildSystemPrompt(ctx);
        }
    }
}
