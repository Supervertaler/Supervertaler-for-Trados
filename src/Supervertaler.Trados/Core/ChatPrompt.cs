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
        public List<Supervertaler.Core.DocumentSegment> DocumentSegments;  // all source segments, with Studio's numbers and files
        public int ActiveSegmentIndex;         // 0-based index of current segment in document
        public int TotalSegmentCount;          // total segments in document
        public int MaxDocumentSegments;        // truncation cap from settings

        // Surrounding segments: each entry is [source, target]
        public List<string[]> SurroundingSegments;

        // Enhanced terminology
        public bool IncludeTermMetadata;       // include definitions, domains, notes

        // SuperMemory KB context (pre-formatted prompt section, or null)
        public string KbContext;

        // Demo mode: AI anonymises all personal/project data in responses
        public bool DemoMode;
    }

    /// <summary>
    /// Builds the system prompt for the AI Chat Assistant.
    /// Separate from TranslationPrompt because the chat assistant is conversational
    /// rather than a pure translator – it needs different persona and guidelines.
    /// </summary>
    public static class ChatPrompt
    {
        /// <summary>
        /// Builds a context-aware system prompt for the AI chat assistant.
        /// Uses the full ChatContext for rich project/document awareness.
        /// </summary>
        public static string BuildSystemPrompt(ChatContext ctx) => BuildSystemPrompt(ctx, out _);

        /// <summary>
        /// <see cref="BuildSystemPrompt(ChatContext)"/>, and how many of its
        /// leading characters stay the same from one chat turn to the next.
        ///
        /// <para>The prompt is built in two parts for prompt caching (205). First
        /// what stays the same while the translator works in one document: the
        /// instructions, the project, SuperMemory and the whole source document -
        /// tens of thousands of tokens, the bulk of every turn. Then what follows
        /// the active segment: where it is, its neighbours, its text, its TM
        /// matches and terms. A cache matches a prompt's beginning, so anything
        /// that changes per segment in the first part would make every turn pay
        /// for the whole document again; here moving to another segment changes
        /// only the second part. The segment's position used to sit in PROJECT
        /// CONTEXT, ahead of the document; it is the one thing that moved.</para>
        ///
        /// <para>Byte-identical across turns needs the inputs to be: the document
        /// and the memory bank as read, in the same order. Both are (the bank's
        /// files are read sorted); editing a bank file between turns changes the
        /// first part, once.</para>
        /// </summary>
        public static string BuildSystemPrompt(ChatContext ctx, out int stableLength)
        {
            if (ctx == null)
                return BuildSystemPrompt(new ChatContext(), out stableLength);

            var sb = new StringBuilder(8192);

            // ── Persona ──────────────────────────────────────────────
            sb.AppendLine("You are a professional translation assistant integrated into Trados Studio.");
            sb.AppendLine("You help translators with their work by answering questions about translations,");
            sb.AppendLine("suggesting improvements, explaining terminology, and providing context.");
            sb.AppendLine();
            sb.AppendLine("# TRADOS STUDIO TOOLS");
            sb.AppendLine("You have access to tools that can query the user's Trados Studio installation.");
            sb.AppendLine("Use these tools when the user asks about their projects, translation memories,");
            sb.AppendLine("or project templates. The tools read local Trados data – you do not need to");
            sb.AppendLine("ask the user for file paths or other details. Just call the appropriate tool.");
            sb.AppendLine("Present tool results in a clear, well-formatted table or summary.");
            sb.AppendLine();
            sb.AppendLine("# FORMATTING");
            sb.AppendLine("Use proper Markdown formatting in your responses. When presenting tabular data,");
            sb.AppendLine("always use valid Markdown table syntax with pipe delimiters and a separator row:");
            sb.AppendLine("| Header 1 | Header 2 |");
            sb.AppendLine("|----------|----------|");
            sb.AppendLine("| Cell 1   | Cell 2   |");
            sb.AppendLine("Use bullet lists (- item), bold (**text**), and headings (##) where appropriate.");

            // ── Incognito mode (data anonymisation) ───────────────────
            if (ctx.DemoMode)
            {
                sb.AppendLine();
                sb.AppendLine("# INCOGNITO MODE – DATA ANONYMISATION");
                sb.AppendLine("This is a demo session. All personal and organisational data MUST be anonymised");
                sb.AppendLine("in every response you write. This applies to:");
                sb.AppendLine("- User names, display names, and email addresses");
                sb.AppendLine("- Organisation, client, and customer names");
                sb.AppendLine("- Project names that contain client or personal identifiers");
                sb.AppendLine("- File names and file paths");
                sb.AppendLine("- TM names and termbase names that contain client identifiers");
                sb.AppendLine("- Server URLs, tenant IDs, and credential references");
                sb.AppendLine();
                sb.AppendLine("Replace real values with plausible anonymised equivalents:");
                sb.AppendLine("- \"Acme Corp\" → \"Client Alpha\", \"Jane Smith\" → \"User A\"");
                sb.AppendLine("- \"D:\\Jobs\\ACME\\...\" → \"D:\\Projects\\Client Alpha\\...\"");
                sb.AppendLine("- \"ACME_translation_Q1.docx\" → \"Project_Alpha_document.docx\"");
                sb.AppendLine("Use consistent replacements within the conversation so references remain trackable.");
                sb.AppendLine();
                sb.AppendLine("Do NOT anonymise:");
                sb.AppendLine("- Language codes (en-GB, nl-NL, etc.)");
                sb.AppendLine("- Technical identifiers (status values, tool names)");
                sb.AppendLine("- Counts, statistics, word counts, and structural metadata");
                sb.AppendLine("- The source and target text of the segment being translated");
            }

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

                // Where the active segment is goes in the second part, below.
                if (ctx.TotalSegmentCount > 0 && ctx.ActiveSegmentIndex < 0)
                    sb.Append("- Total segments: ").AppendLine(ctx.TotalSegmentCount.ToString());
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

                // Numbered as Studio numbers them, so a segment the assistant names
                // is one the translator finds: per file in a merged document.
                // Truncated past the cap: first 80% of it + last 20%.
                Supervertaler.Core.DocumentContent.AppendNumbered(sb, ctx.DocumentSegments,
                    ctx.MaxDocumentSegments > 0 ? ctx.MaxDocumentSegments : 500);
            }

            // ══ Everything above stays the same from turn to turn; everything
            //    below follows the active segment. ══════════════════════════
            stableLength = sb.Length;

            // ── Position of the active segment ───────────────────────
            if (ctx.TotalSegmentCount > 0 && ctx.ActiveSegmentIndex >= 0)
            {
                // The active segment by the number Studio shows - and, in a
                // merged document, which numbers each file from 1, its file.
                var active = ctx.DocumentSegments != null
                    && ctx.ActiveSegmentIndex < ctx.DocumentSegments.Count
                    ? ctx.DocumentSegments[ctx.ActiveSegmentIndex] : null;
                var files = Supervertaler.Core.DocumentContent.Files(ctx.DocumentSegments);
                sb.AppendLine();
                sb.AppendLine("# ACTIVE SEGMENT");
                if (active != null && files.Count > 1 && active.File != null)
                    sb.Append("- Position: Segment ").Append(active.Number)
                      .Append(" of ").Append(active.File)
                      .Append(" (file ").Append(files.IndexOf(active.File) + 1).Append(" of ").Append(files.Count)
                      .Append("; ").Append(ctx.TotalSegmentCount).AppendLine(" segments in all)");
                else
                    sb.Append("- Position: Segment ").Append(active?.Number ?? (ctx.ActiveSegmentIndex + 1).ToString())
                      .Append(" of ").AppendLine(ctx.TotalSegmentCount.ToString());
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
        /// How far the start of the history window moves at a time:
        /// two exchanges. Even, so the window keeps starting on the same side of
        /// the user/assistant alternation.
        /// </summary>
        internal const int MessageWindowStep = 4;

        /// <summary>
        /// The messages a chat turn sends: the most recent
        /// <paramref name="maxMessages"/> at least, within a character budget
        /// (~50K tokens ≈ 200K chars) that keeps accumulated large prompts (e.g.
        /// {{PROJECT}} expansions) from running away. Always includes the most
        /// recent message.
        ///
        /// <para>The window's first message moves only every
        /// <see cref="MessageWindowStep"/> messages, so a window holds between
        /// <paramref name="maxMessages"/> and <paramref name="maxMessages"/> + 3.
        /// Until 205 it slid by one with every message once the chat was longer
        /// than <paramref name="maxMessages"/>, so each turn began differently
        /// from the last and prompt caching, which matches a beginning, could
        /// never read the history back. Now it starts the same for two exchanges
        /// at a time.</para>
        /// </summary>
        public static List<Supervertaler.Core.Models.ChatMessage> MessageWindow(
            List<Supervertaler.Core.Models.ChatMessage> history, int maxMessages)
        {
            const int maxChars = 200_000; // ~50K tokens

            var result = new List<Supervertaler.Core.Models.ChatMessage>();
            if (history == null || history.Count == 0)
                return result;

            var startIdx = history.Count > maxMessages
                ? (history.Count - maxMessages) / MessageWindowStep * MessageWindowStep
                : 0;

            // Start from the most recent message and work backwards
            var totalChars = 0;
            for (int i = history.Count - 1; i >= startIdx; i--)
            {
                var msgLen = history[i].Content?.Length ?? 0;

                // Always include the most recent message
                if (i == history.Count - 1)
                {
                    result.Insert(0, history[i]);
                    totalChars += msgLen;
                    continue;
                }

                // Stop adding older messages if we'd exceed the budget
                if (totalChars + msgLen > maxChars)
                    break;

                result.Insert(0, history[i]);
                totalChars += msgLen;
            }

            return result;
        }

        /// <summary>
        /// Legacy overload – builds a basic system prompt without document/project context.
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
