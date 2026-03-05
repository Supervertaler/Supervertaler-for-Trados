using System;

namespace TermLens.Core
{
    /// <summary>
    /// Expands a partial text selection to full word boundaries.
    ///
    /// In the Trados editor grid, users often select across word boundaries by
    /// grabbing just a few letters at the end of one word and the start of the
    /// next (e.g. selecting "ing pr" to mean "warning profiles"). This class
    /// finds the partial selection within the full segment text and expands it
    /// outward to encompass complete words.
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
        /// </summary>
        /// <param name="fullText">The complete segment text.</param>
        /// <param name="partialSelection">The user's (possibly partial) selection.</param>
        /// <returns>The expanded text, or the original selection if it can't be found.</returns>
        public static string ExpandToWordBoundaries(string fullText, string partialSelection)
        {
            if (string.IsNullOrEmpty(fullText) || string.IsNullOrEmpty(partialSelection))
                return (partialSelection ?? "").Trim();

            // Find the partial selection in the full text
            int idx = fullText.IndexOf(partialSelection, StringComparison.Ordinal);
            if (idx < 0)
            {
                // Try case-insensitive as fallback
                idx = fullText.IndexOf(partialSelection, StringComparison.OrdinalIgnoreCase);
            }

            if (idx < 0)
                return partialSelection.Trim(); // not found — return trimmed as-is

            // Expand left to word boundary (whitespace or start of string)
            int start = idx;
            while (start > 0 && !char.IsWhiteSpace(fullText[start - 1]))
                start--;

            // Expand right to word boundary (whitespace or end of string)
            int end = idx + partialSelection.Length;
            while (end < fullText.Length && !char.IsWhiteSpace(fullText[end]))
                end++;

            string expanded = fullText.Substring(start, end - start);

            // Trim non-word characters from edges (punctuation like commas,
            // periods, parentheses, quotes) but keep hyphens and apostrophes
            // which are valid inside words/terms.
            int trimStart = 0;
            while (trimStart < expanded.Length && !IsWordChar(expanded[trimStart]))
                trimStart++;

            int trimEnd = expanded.Length - 1;
            while (trimEnd >= trimStart && !IsWordChar(expanded[trimEnd]))
                trimEnd--;

            if (trimStart > trimEnd)
                return partialSelection.Trim(); // degenerate case

            return expanded.Substring(trimStart, trimEnd - trimStart + 1);
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
