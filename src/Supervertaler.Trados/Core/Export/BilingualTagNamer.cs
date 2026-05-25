using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Sdl.FileTypeSupport.Framework.BilingualApi;

namespace Supervertaler.Trados.Core.Export
{
    /// <summary>
    /// Two-way conversion between the numbered tag placeholders that
    /// <see cref="SegmentTagHandler"/> emits / consumes (<c>&lt;t1&gt;</c>,
    /// <c>&lt;/t1&gt;</c>, <c>&lt;t2/&gt;</c>) and the semantic tag names
    /// that the Supervertaler Workbench's "With Tags" Bilingual Table
    /// export uses (<c>&lt;b&gt;</c>, <c>&lt;i&gt;</c>, <c>&lt;u&gt;</c>,
    /// <c>&lt;bi&gt;</c>).
    ///
    /// Why this layer exists:
    /// - <c>SegmentTagHandler</c> uses numbered placeholders because they're
    ///   unambiguous (multiple bold ranges in one segment are still
    ///   distinguishable) and they cover every Trados tag type including
    ///   field codes / page numbers / custom format pairs.
    /// - But proofreaders find <c>&lt;b&gt;</c> much more readable than
    ///   <c>&lt;t1&gt;</c>, and the Workbench uses the semantic form, so
    ///   for cross-product consistency we present the semantic form in the
    ///   bilingual file when the underlying tag is a recognised
    ///   character-formatting cf pair.
    ///
    /// Conversion strategy:
    /// - Export: walk the <see cref="TagInfo"/> map; if the original
    ///   markup is a cf pair that maps to a known semantic (<c>b</c>,
    ///   <c>i</c>, <c>u</c>, <c>bi</c>), replace the matching
    ///   <c>&lt;tN&gt;</c> / <c>&lt;/tN&gt;</c> pair with the semantic
    ///   marker. Standalone tags (<c>&lt;tN/&gt;</c>) and unrecognised
    ///   tags keep their numbered form.
    /// - Import: re-serialise the live source to regenerate the TagMap
    ///   with deterministic numbering, build per-name queues of tag
    ///   numbers, then walk the proofreader's edit replacing
    ///   <c>&lt;b&gt;</c> with the next available <c>&lt;tN&gt;</c> for
    ///   "b" (positional matching — the Nth <c>&lt;b&gt;</c> binds to
    ///   the Nth bold tag in source order).
    /// </summary>
    public static class BilingualTagNamer
    {
        // Recognised semantic names. Order matters for combined formats:
        // "bi" must come before "b" / "i" so combination-tag detection
        // wins over single-format detection.
        private static readonly string[] KnownSemanticNames = { "bi", "b", "i", "u" };

        // Pattern that matches <tN>, </tN>, <tN/> in serialised text.
        private static readonly Regex NumberedTagRe =
            new Regex(@"<(/?)t(\d+)(\s*/?)>", RegexOptions.Compiled);

        // Pattern that matches semantic <b>, </b>, <i>, </i>, etc. in
        // proofreader-edited text. Case-insensitive to be forgiving.
        private static readonly Regex SemanticTagRe =
            new Regex(@"<(/?)(bi|b|i|u)>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // ─── Export: <tN> → <b> ───────────────────────────────────────

        /// <summary>Replace numbered placeholder pairs with semantic names
        /// where the underlying Trados tag is a recognised character-
        /// formatting cf pair. Standalone tags and unrecognised tags
        /// keep their numbered form.</summary>
        public static string ApplySemanticNames(string serializedText, Dictionary<int, TagInfo> tagMap)
        {
            if (string.IsNullOrEmpty(serializedText) || tagMap == null) return serializedText;

            var sb = new StringBuilder(serializedText.Length);
            int lastEnd = 0;
            foreach (Match m in NumberedTagRe.Matches(serializedText))
            {
                if (m.Index > lastEnd)
                    sb.Append(serializedText, lastEnd, m.Index - lastEnd);

                string slash = m.Groups[1].Value;     // "/" if closing tag, "" if opening
                string numStr = m.Groups[2].Value;
                string standalone = m.Groups[3].Value; // "/" if standalone, "" if paired

                bool isStandalone = standalone.Trim() == "/";
                bool isClosing = slash == "/";

                // Standalone tags can't be semantic-named (no end pair).
                if (isStandalone)
                {
                    sb.Append(m.Value);
                    lastEnd = m.Index + m.Length;
                    continue;
                }

                int tagNum;
                if (!int.TryParse(numStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out tagNum) ||
                    !tagMap.TryGetValue(tagNum, out var info))
                {
                    sb.Append(m.Value);
                    lastEnd = m.Index + m.Length;
                    continue;
                }

                var semantic = DetectSemantic(info);
                if (semantic == null)
                {
                    sb.Append(m.Value);
                }
                else
                {
                    sb.Append(isClosing ? "</" : "<").Append(semantic).Append('>');
                }
                lastEnd = m.Index + m.Length;
            }
            if (lastEnd < serializedText.Length)
                sb.Append(serializedText, lastEnd, serializedText.Length - lastEnd);

            return sb.ToString();
        }

        // ─── Import: <b> → <tN> ───────────────────────────────────────

        /// <summary>Reverse of <see cref="ApplySemanticNames"/>: walks the
        /// proofreader's edit replacing <c>&lt;b&gt;</c>, <c>&lt;i&gt;</c>,
        /// <c>&lt;u&gt;</c>, <c>&lt;bi&gt;</c> with the matching
        /// <c>&lt;tN&gt;</c> / <c>&lt;/tN&gt;</c> markers, using positional
        /// matching against the live source's <paramref name="tagMap"/>.
        /// Unknown semantic names and stray markers are left as-is —
        /// SegmentTagHandler.ReconstructTarget will fall back to plain
        /// text if reconstruction fails.</summary>
        public static string ResolveSemanticNames(string editedText, Dictionary<int, TagInfo> tagMap)
        {
            if (string.IsNullOrEmpty(editedText) || tagMap == null) return editedText;

            // Build per-semantic-name queues of tag numbers, in TagMap
            // ascending order (= source document order).
            var queues = new Dictionary<string, Queue<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in tagMap.OrderBy(k => k.Key))
            {
                var name = DetectSemantic(kv.Value);
                if (name == null) continue;
                if (!queues.TryGetValue(name, out var q))
                {
                    q = new Queue<int>();
                    queues[name] = q;
                }
                q.Enqueue(kv.Key);
            }

            // Per-name stack tracking currently-open tags so closing
            // markers map back to the matching opening's tag number.
            var openStacks = new Dictionary<string, Stack<int>>(StringComparer.OrdinalIgnoreCase);

            var sb = new StringBuilder(editedText.Length);
            int lastEnd = 0;
            foreach (Match m in SemanticTagRe.Matches(editedText))
            {
                if (m.Index > lastEnd)
                    sb.Append(editedText, lastEnd, m.Index - lastEnd);

                bool isClosing = m.Groups[1].Value == "/";
                var name = m.Groups[2].Value.ToLowerInvariant();

                int tagNum = -1;
                if (isClosing)
                {
                    if (openStacks.TryGetValue(name, out var stack) && stack.Count > 0)
                        tagNum = stack.Pop();
                }
                else
                {
                    if (queues.TryGetValue(name, out var q) && q.Count > 0)
                    {
                        tagNum = q.Dequeue();
                        if (!openStacks.TryGetValue(name, out var stack))
                        {
                            stack = new Stack<int>();
                            openStacks[name] = stack;
                        }
                        stack.Push(tagNum);
                    }
                }

                if (tagNum > 0)
                {
                    sb.Append(isClosing ? "</t" : "<t")
                      .Append(tagNum.ToString(CultureInfo.InvariantCulture))
                      .Append('>');
                }
                else
                {
                    // Couldn't resolve — leave the literal marker in
                    // place. ReconstructTarget will then return false and
                    // we fall back to plain-text writeback.
                    sb.Append(m.Value);
                }
                lastEnd = m.Index + m.Length;
            }
            if (lastEnd < editedText.Length)
                sb.Append(editedText, lastEnd, editedText.Length - lastEnd);

            return sb.ToString();
        }

        // ─── Tag-introspection helper ─────────────────────────────────

        /// <summary>
        /// Inspect a TagInfo's OriginalMarkup and return the matching
        /// semantic name ("b", "i", "u", "bi") if it's a recognised cf
        /// character-formatting pair. Returns null for everything else
        /// (standalone tags, field codes, page numbers, custom format
        /// pairs, etc.) so those keep their <c>&lt;tN&gt;</c> numbered form.
        ///
        /// Uses two probes layered defensively so SDK quirks fall through:
        /// 1. <c>ITagPair.StartTagProperties.Formatting</c> if available —
        ///    walks the IFormattingGroup looking for known keys.
        /// 2. Raw <c>TagContent</c> string match — looks for
        ///    <c>bold="True"</c> / <c>italic="True"</c> substrings.
        /// </summary>
        public static string DetectSemantic(TagInfo info)
        {
            if (info == null || info.OriginalMarkup == null) return null;

            var pair = info.OriginalMarkup as ITagPair;
            if (pair == null || pair.StartTagProperties == null) return null;

            bool hasBold = false, hasItalic = false, hasUnderline = false;

            // Probe 1: walk the IFormattingGroup.
            try
            {
                var formatting = pair.StartTagProperties.Formatting;
                if (formatting != null)
                {
                    foreach (var key in formatting.Keys)
                    {
                        var lc = (key ?? "").ToLowerInvariant();
                        var value = formatting[key]?.ToString() ?? "";
                        bool isTrue = value.IndexOf("true", StringComparison.OrdinalIgnoreCase) >= 0
                                   || value.IndexOf("single", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!isTrue) continue;

                        if (lc.Contains("bold")) hasBold = true;
                        else if (lc.Contains("italic")) hasItalic = true;
                        else if (lc.Contains("underline")) hasUnderline = true;
                    }
                }
            }
            catch { /* fall through to probe 2 */ }

            // Probe 2: raw TagContent substring match (covers SDK variants
            // where Formatting isn't populated for cf tags).
            if (!hasBold && !hasItalic && !hasUnderline)
            {
                try
                {
                    var content = pair.StartTagProperties.TagContent ?? "";
                    var lc = content.ToLowerInvariant();
                    if (lc.Contains("bold=\"true\"") || lc.Contains("bold='true'")) hasBold = true;
                    if (lc.Contains("italic=\"true\"") || lc.Contains("italic='true'")) hasItalic = true;
                    if (lc.Contains("underline=\"true\"") || lc.Contains("underline=\"single\"")
                        || lc.Contains("underline='true'") || lc.Contains("underline='single'"))
                        hasUnderline = true;
                }
                catch { }
            }

            if (hasBold && hasItalic) return "bi";
            if (hasBold) return "b";
            if (hasItalic) return "i";
            if (hasUnderline) return "u";
            return null;
        }

        // ─── Convenience for the DocxRenderer's tag-aware coloring ────

        /// <summary>Combined regex matching both numbered (&lt;tN&gt;) and
        /// semantic (&lt;b&gt;, etc.) tag markers. Used by the DOCX
        /// renderer to colour markers red regardless of which naming
        /// convention they ended up in.</summary>
        public static readonly Regex AnyTagMarkerRegex =
            new Regex(@"</?(?:t\d+|bi|b|i|u)\s*/?>", RegexOptions.Compiled);
    }
}
