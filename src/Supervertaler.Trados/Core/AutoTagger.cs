using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Pure logic for AutoTagger: builds the prompt that asks the AI to place the
    /// source segment's inline tags into an existing (tag-free) target, validates
    /// the AI's response, and re-inserts the tags into the user's exact target so
    /// the wording/punctuation is preserved verbatim.
    ///
    /// Tags use the same numbered placeholder scheme as <see cref="SegmentTagHandler"/>
    /// (&lt;t1&gt;…&lt;/t1&gt; paired, &lt;t2/&gt; standalone). The result of a
    /// successful run is a marker string suitable for SegmentTagHandler.ReconstructTarget.
    /// </summary>
    public static class AutoTagger
    {
        // Mirrors SegmentTagHandler.TagPlaceholderPattern (whitespace/case tolerant).
        // Group 1 = standalone number, 2 = closing number, 3 = opening number.
        private static readonly Regex TagToken = new Regex(
            @"<\s*t\s*(\d+)\s*/\s*>|<\s*/\s*t\s*(\d+)\s*>|<\s*t\s*(\d+)\s*>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Cosmetic character variants LLMs routinely swap. Folded only for the
        // "words unchanged" comparison; the written target keeps the user's chars.
        private static char FoldChar(char c)
        {
            switch (c)
            {
                case '“': case '”': case '„': case '‟':
                case '«': case '»': case '″': return '"';
                case '‘': case '’': case '‚': case '‛':
                case '′': case '`': return '\'';
                case ' ': case ' ': case ' ': return ' ';
                case '–': case '—': case '‑': return '-';
                default: return c;
            }
        }

        /// <summary>Canonical, normalized list of tag tokens in order of appearance.</summary>
        public static List<string> ExtractTags(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (Match m in TagToken.Matches(text))
            {
                if (m.Groups[1].Success) list.Add($"<t{m.Groups[1].Value}/>");
                else if (m.Groups[2].Success) list.Add($"</t{m.Groups[2].Value}>");
                else if (m.Groups[3].Success) list.Add($"<t{m.Groups[3].Value}>");
            }
            return list;
        }

        public static string StripTags(string text) =>
            string.IsNullOrEmpty(text) ? "" : TagToken.Replace(text, "");

        private static string FoldForCompare(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length);
            foreach (var ch in text) sb.Append(FoldChar(ch));
            // ellipsis → "..." so a folded "…" matches "..."
            var folded = sb.ToString().Replace('…'.ToString(), "...");
            return Regex.Replace(folded, @"\s+", " ").Trim();
        }

        private static bool TagsWellFormed(string text)
        {
            var stack = new Stack<string>();
            foreach (Match m in TagToken.Matches(text))
            {
                if (m.Groups[1].Success) continue;              // standalone
                if (m.Groups[2].Success)                        // closing
                {
                    if (stack.Count == 0 || stack.Peek() != m.Groups[2].Value) return false;
                    stack.Pop();
                }
                else if (m.Groups[3].Success)                   // opening
                {
                    stack.Push(m.Groups[3].Value);
                }
            }
            return stack.Count == 0;
        }

        /// <summary>
        /// Validates an AutoTagger candidate. Returns (true, "") when the candidate
        /// has exactly the source's tag set, leaves the words unchanged versus the
        /// tag-free target, and is well-formed; otherwise (false, reason).
        /// </summary>
        public static bool Validate(string serializedSource, string candidate, string plainTarget, out string reason)
        {
            var srcTags = ExtractTags(serializedSource).OrderBy(t => t, StringComparer.Ordinal).ToList();
            var candTags = ExtractTags(candidate).OrderBy(t => t, StringComparer.Ordinal).ToList();
            if (!srcTags.SequenceEqual(candTags))
            {
                reason = "tag set does not match the source";
                return false;
            }
            if (FoldForCompare(StripTags(candidate)) != FoldForCompare(plainTarget))
            {
                reason = "the target wording changed (AutoTagger must only move tags)";
                return false;
            }
            if (!TagsWellFormed(candidate))
            {
                reason = "tags are not well-formed (unpaired or out of order)";
                return false;
            }
            reason = "";
            return true;
        }

        /// <summary>
        /// Re-inserts the candidate's tags into the EXACT <paramref name="plainTarget"/>
        /// so the user's wording/punctuation is preserved verbatim, when the candidate's
        /// tag-free text differs from the target only by cosmetic single-character swaps
        /// (e.g. straight vs curly quotes). Returns null when they can't be aligned
        /// 1:1 (caller then falls back to the candidate as-is).
        /// </summary>
        public static string ReinsertTagsIntoExactTarget(string candidate, string plainTarget)
        {
            if (candidate == null || plainTarget == null) return null;

            // Collect tags with their offset in the tag-free candidate text.
            var pieces = new List<(int offset, string tag)>();
            var stripped = new StringBuilder();
            int last = 0;
            foreach (Match m in TagToken.Matches(candidate))
            {
                stripped.Append(candidate, last, m.Index - last);
                string tok = m.Groups[1].Success ? $"<t{m.Groups[1].Value}/>"
                           : m.Groups[2].Success ? $"</t{m.Groups[2].Value}>"
                           : $"<t{m.Groups[3].Value}>";
                pieces.Add((stripped.Length, tok));
                last = m.Index + m.Length;
            }
            stripped.Append(candidate, last, candidate.Length - last);
            string candStripped = stripped.ToString();

            // Exact mapping only works when the tag-free texts line up 1:1.
            if (candStripped.Length != plainTarget.Length) return null;
            for (int i = 0; i < candStripped.Length; i++)
            {
                if (candStripped[i] != plainTarget[i]
                    && FoldChar(candStripped[i]) != FoldChar(plainTarget[i]))
                    return null;
            }

            // Offsets in candStripped map directly onto plainTarget (same length).
            var byOffset = new Dictionary<int, List<string>>();
            foreach (var (offset, tag) in pieces)
            {
                if (!byOffset.TryGetValue(offset, out var l)) { l = new List<string>(); byOffset[offset] = l; }
                l.Add(tag);
            }
            var sb = new StringBuilder(plainTarget.Length + 16);
            for (int i = 0; i <= plainTarget.Length; i++)
            {
                if (byOffset.TryGetValue(i, out var tags))
                    foreach (var t in tags) sb.Append(t);
                if (i < plainTarget.Length) sb.Append(plainTarget[i]);
            }
            return sb.ToString();
        }

        /// <summary>Space-joined list of the source's tags for the {{TAG_LIST}} placeholder.</summary>
        public static string TagList(string serializedSource) => string.Join(" ", ExtractTags(serializedSource));

        /// <summary>Renders the editable instruction template into the user prompt.</summary>
        public static string BuildUserPrompt(string instruction, string serializedSource, string plainTarget)
        {
            return (instruction ?? "")
                .Replace("{{SOURCE_TEXT}}", serializedSource ?? "")
                .Replace("{{TARGET_TEXT}}", plainTarget ?? "")
                .Replace("{{TAG_LIST}}", TagList(serializedSource));
        }

        /// <summary>Small fixed system prompt for AutoTagger (tag-only, no translation).</summary>
        public const string SystemPrompt =
            "You are a tag-placement assistant for a CAT tool. You insert inline tags " +
            "into an already-translated target segment. You never translate, add, remove, " +
            "or reword the target text. You output only the tagged target text.";
    }
}
