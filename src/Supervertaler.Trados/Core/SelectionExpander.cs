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
    /// When the selection already matches a complete word (i.e. it sits at word
    /// boundaries), no expansion is performed. This prevents "hechting" from
    /// being incorrectly expanded to "hechtingsbevorderaars" when both the
    /// compound and the standalone word appear in the same segment.
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
        /// </summary>
        /// <param name="fullText">The complete segment text.</param>
        /// <param name="partialSelection">The user's (possibly partial) selection.</param>
        /// <returns>The expanded text, or the original selection if it can't be found.</returns>
        public static string ExpandToWordBoundaries(string fullText, string partialSelection)
        {
            if (string.IsNullOrEmpty(fullText) || string.IsNullOrEmpty(partialSelection))
                return (partialSelection ?? "").Trim();

            // Search for all occurrences of the selection in the full text,
            // preferring matches that sit at word boundaries over matches
            // embedded inside longer words.
            int bestIdx = -1;
            bool bestAtBoundary = false;

            bestIdx = FindBest(fullText, partialSelection, StringComparison.Ordinal, out bestAtBoundary);

            // Case-insensitive fallback
            if (bestIdx < 0)
                bestIdx = FindBest(fullText, partialSelection, StringComparison.OrdinalIgnoreCase, out bestAtBoundary);

            if (bestIdx < 0)
                return partialSelection.Trim(); // not found — return trimmed as-is

            // If the selection sits at word boundaries, return it without expansion
            if (bestAtBoundary)
                return TrimNonWordEdges(partialSelection);

            // Otherwise expand outward to full word boundaries (original behavior
            // for genuine cross-boundary selections like "ing pr" → "warning profiles")
            int start = bestIdx;
            while (start > 0 && !char.IsWhiteSpace(fullText[start - 1]))
                start--;

            int end = bestIdx + partialSelection.Length;
            while (end < fullText.Length && !char.IsWhiteSpace(fullText[end]))
                end++;

            return TrimNonWordEdges(fullText.Substring(start, end - start));
        }

        /// <summary>
        /// Scans all occurrences of <paramref name="needle"/> inside
        /// <paramref name="haystack"/> and returns the index of the best match.
        /// A match at word boundaries is always preferred; if none exists the
        /// first embedded match is returned as a fallback.
        /// </summary>
        private static int FindBest(string haystack, string needle,
            StringComparison comparison, out bool atBoundary)
        {
            atBoundary = false;
            int bestIdx = -1;
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
                    // Perfect word-boundary match — no need to search further
                    atBoundary = true;
                    return idx;
                }

                if (bestIdx < 0)
                    bestIdx = idx; // Remember first occurrence as fallback

                pos = idx + 1;
            }

            return bestIdx;
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
