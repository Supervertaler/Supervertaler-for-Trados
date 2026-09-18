using System;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Expands a partial text selection to full word boundaries.
    ///
    /// In the Trados editor grid, users often select across word boundaries by
    /// grabbing just a few letters at the end of one word and the start of the
    /// next (e.g. selecting "ing pr" to mean "warning profiles"). This class
    /// finds the partial selection within the full segment text and expands it
    /// outward to encompass complete words.
    ///
    /// Selection priority (highest to lowest):
    ///   1. Exact word-boundary match – selection is already a complete word
    ///   2. Shortest expansion – when multiple words contain the selection,
    ///      the shortest enclosing word wins (e.g. "echt" → "hechting" not
    ///      "hechtingsbevorderaars")
    /// </summary>
    public static class SelectionExpander
    {
        /// <summary>
        /// Expands a partial text selection to full word boundaries within the
        /// full segment text.
        ///
        /// Example: fullText = "selecting warning profiles, reading out event logs"
        ///          partialSelection = "ing pr"
        ///          result = "warning profiles"
        ///
        /// If the selection already sits at word boundaries somewhere in the
        /// text, it is returned as-is (no expansion).
        ///
        /// Example: fullText = "hechtingsbevorderaars ... de hechting kunnen"
        ///          partialSelection = "hechting"
        ///          result = "hechting"   (NOT "hechtingsbevorderaars")
        ///
        /// When the selection is embedded inside multiple words, the shortest
        /// enclosing word is preferred.
        ///
        /// Example: fullText = "hechtingsbevorderaars ... de hechting kunnen"
        ///          partialSelection = "echt"
        ///          result = "hechting"   (8 chars, shorter than "hechtingsbevorderaars")
        /// </summary>
        /// <param name="fullText">The complete segment text.</param>
        /// <param name="partialSelection">The user's (possibly partial) selection.</param>
        /// <returns>The expanded text, or the original selection if it can't be found.</returns>
        /// <summary>
        /// Overload that can skip auto-expansion. When <paramref name="autoExpand"/>
        /// is false the exact (trimmed) selection is returned unchanged — used for
        /// Korean/Japanese where expanding to the whitespace token would swallow
        /// an attached particle (saving 장치의 instead of the intended 장치).
        /// </summary>
        public static string ExpandToWordBoundaries(string fullText, string partialSelection, bool autoExpand)
        {
            if (!autoExpand)
                return (partialSelection ?? "").Trim();
            return ExpandToWordBoundaries(fullText, partialSelection);
        }

        public static string ExpandToWordBoundaries(string fullText, string partialSelection)
        {
            if (string.IsNullOrEmpty(fullText) || string.IsNullOrEmpty(partialSelection))
                return (partialSelection ?? "").Trim();

            // Strip leading/trailing whitespace before matching – a selection like
            // "trimethoxysilaan " (trailing space) would otherwise cause endPos to land
            // on the next word, making the expansion loop swallow it ("trimethoxysilaan of").
            partialSelection = partialSelection.Trim();
            if (string.IsNullOrEmpty(partialSelection))
                return "";

            // Try case-sensitive first, then case-insensitive
            string result = FindBestExpansion(fullText, partialSelection, StringComparison.Ordinal);
            if (result == null)
                result = FindBestExpansion(fullText, partialSelection, StringComparison.OrdinalIgnoreCase);

            return result ?? partialSelection.Trim();
        }

        /// <summary>
        /// Scans all occurrences of <paramref name="needle"/> inside
        /// <paramref name="haystack"/>, expands each to word boundaries,
        /// and returns the best result.
        ///
        /// Priority: (1) exact word-boundary match (no expansion needed),
        /// (2) shortest expanded word among all candidates.
        /// </summary>
        private static string FindBestExpansion(string haystack, string needle,
            StringComparison comparison)
        {
            string bestExpansion = null;
            int bestLength = int.MaxValue;
            int pos = 0;

            while (pos <= haystack.Length - needle.Length)
            {
                int idx = haystack.IndexOf(needle, pos, comparison);
                if (idx < 0) break;

                bool atLeft = idx == 0 || !IsWordChar(haystack[idx - 1]);
                int endPos = idx + needle.Length;
                bool atRight = endPos >= haystack.Length || !IsWordChar(haystack[endPos]);

                if (atLeft && atRight)
                {
                    // Perfect word-boundary match – return immediately
                    return TrimNonWordEdges(needle);
                }

                // Expand outward to word boundaries
                int start = idx;
                while (start > 0 && !char.IsWhiteSpace(haystack[start - 1]))
                    start--;

                int end = endPos;
                while (end < haystack.Length && !char.IsWhiteSpace(haystack[end]))
                    end++;

                string expanded = TrimNonWordEdges(haystack.Substring(start, end - start));

                // Prefer the shortest expansion – the user most likely
                // intended the simpler/base word, not a longer compound
                if (expanded.Length < bestLength)
                {
                    bestLength = expanded.Length;
                    bestExpansion = expanded;
                }

                pos = idx + 1;
            }

            return bestExpansion;
        }

        /// <summary>
        /// Trims non-word characters (punctuation, brackets, quotes) from the
        /// edges of a string, keeping hyphens and apostrophes which are valid
        /// inside terms.
        /// </summary>
        private static string TrimNonWordEdges(string text)
        {
            int trimStart = 0;
            while (trimStart < text.Length && !IsWordChar(text[trimStart]))
            {
                // Keep a bracket that is balanced inside the selection:
                // "(her)certificering" is a term, not a stray edge character.
                if (IsBalancedOpener(text, trimStart, text.Length - 1)) break;
                trimStart++;
            }

            int trimEnd = text.Length - 1;
            while (trimEnd >= trimStart && !IsWordChar(text[trimEnd]))
            {
                // Don't strip a closing bracket that is balanced inside what we
                // are keeping – "tekst (met noot)" must not become "tekst (met
                // noot", which is both wrong and unbalanced.
                if (IsBalancedCloser(text, trimStart, trimEnd)) break;
                trimEnd--;
            }

            if (trimStart > trimEnd)
                return text.Trim(); // degenerate case

            // A bracket pair that wraps the WHOLE kept text is not part of the
            // word: "(oxide)" is the word "oxide" in brackets, unlike
            // "(her)certificering", where the pair sits inside the word. The
            // balanced-bracket rule above kept both; strip the wrapping pair
            // and trim again, since the inside may have edges of its own.
            if (trimEnd > trimStart && MatchingCloser(text, trimStart, trimEnd) == trimEnd)
                return TrimNonWordEdges(text.Substring(trimStart + 1, trimEnd - trimStart - 1));

            return text.Substring(trimStart, trimEnd - trimStart + 1);
        }

        /// <summary>True when text[start] opens a bracket closed at or before
        /// <paramref name="end"/> - the pair is intact within the range.</summary>
        /// <summary>Index of the closer matching the opener at <paramref name="start"/>,
        /// searched up to <paramref name="end"/>; -1 when text[start] is no opener or
        /// nothing closes it in range.</summary>
        private static int MatchingCloser(string text, int start, int end)
        {
            const string openers = "([{";
            const string closers = ")]}";
            int o = openers.IndexOf(text[start]);
            if (o < 0) return -1;
            int depth = 0;
            for (int i = start; i <= end && i < text.Length; i++)
            {
                if (text[i] == openers[o]) depth++;
                else if (text[i] == closers[o] && --depth == 0) return i;
            }
            return -1;
        }

        private static bool IsBalancedOpener(string text, int start, int end)
        {
            const string openers = "([{";
            const string closers = ")]}";
            int o = openers.IndexOf(text[start]);
            if (o < 0) return false;

            int depth = 0;
            for (int i = start; i <= end && i < text.Length; i++)
            {
                if (text[i] == openers[o]) depth++;
                else if (text[i] == closers[o] && --depth == 0) return true;
            }
            return false;
        }

        /// <summary>True when text[end] closes a bracket opened at or after
        /// <paramref name="start"/> – i.e. the pair is intact within the range.</summary>
        private static bool IsBalancedCloser(string text, int start, int end)
        {
            const string openers = "([{";
            const string closers = ")]}";
            int c = closers.IndexOf(text[end]);
            if (c < 0) return false;

            int depth = 0;
            for (int i = end; i >= start; i--)
            {
                if (text[i] == closers[c]) depth++;
                else if (text[i] == openers[c] && --depth == 0) return true;
            }
            return false;
        }

        /// <summary>
        /// Returns true if the character is part of a "word" for term purposes:
        /// letters, digits, hyphens (compound words), and apostrophes (contractions).
        /// </summary>
        private static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '-' || c == '\'' || c == '\u2019' // right single quote
                || IsScriptDigit(c) || IsScriptSign(c) || TermMatcher.IsRadicalDot(c);
        }

        /// <summary>Sub- and superscript plus and minus: ⁺ ⁻ ₊ ₋. Part of a formula's charge.</summary>
        private static bool IsScriptSign(char c)
        {
            return c == '\u207A' || c == '\u207B' || c == '\u208A' || c == '\u208B';
        }

        /// <summary>
        /// Sub- and superscript digits: ₀-₉ ⁰ ¹ ² ³ ⁴-⁹. char.IsLetterOrDigit says no
        /// to them (Unicode category No, not Nd), so a selection of "O₃" was trimmed
        /// to "O" and saved as such. The term matcher's word pattern accepts exactly
        /// this set and normalises it to plain digits for matching, so a saved
        /// "O₃" is found again.
        /// </summary>
        private static bool IsScriptDigit(char c)
        {
            return (c >= '₀' && c <= '₉') || c == '⁰'
                || c == '¹' || c == '²' || c == '³'
                || (c >= '⁴' && c <= '⁹');
        }
    }
}
