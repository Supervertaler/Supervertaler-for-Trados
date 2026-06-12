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
                trimStart++;

            int trimEnd = text.Length - 1;
            while (trimEnd >= trimStart && !IsWordChar(text[trimEnd]))
                trimEnd--;

            if (trimStart > trimEnd)
                return text.Trim(); // degenerate case

            return text.Substring(trimStart, trimEnd - trimStart + 1);
        }

        /// <summary>
        /// Returns true if the character is part of a "word" for term purposes:
        /// letters, digits, hyphens (compound words), and apostrophes (contractions).
        /// </summary>
        private static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '-' || c == '\'' || c == '\u2019'; // right single quote
        }
    }
}
