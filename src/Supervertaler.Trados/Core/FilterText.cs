using System;
using System.Collections.Generic;
using System.Globalization;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// The one rule every type-to-filter box in the plugin uses: the prompt and
    /// memory-bank pickers and the termbase list in Settings.
    ///
    /// <para>The needle is split on spaces and every word must appear somewhere
    /// in the text, in any order, ignoring case and accents. So "xyz mach" finds
    /// "CONTOSO_XYZ Machines", "dutch" finds every termbase with Dutch on either
    /// side, and "buro" finds "EXAMPLE BURÓ". An empty needle matches
    /// everything.</para>
    ///
    /// <para>Cost: one culture-aware substring search per word per entry, with no
    /// allocation per entry. A thousand entries is well under a millisecond, so
    /// filtering on every keystroke needs no debounce.</para>
    /// </summary>
    public static class FilterText
    {
        private const CompareOptions Loose =
            CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace |
            CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth;

        private static readonly char[] Spaces = { ' ', '\t', ' ' };

        /// <summary>The needle's words, or an empty array when there are none.</summary>
        public static string[] Words(string needle)
        {
            if (string.IsNullOrWhiteSpace(needle)) return new string[0];
            return needle.Split(Spaces, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>True when every word of <paramref name="needle"/> occurs in <paramref name="text"/>.</summary>
        public static bool Matches(string text, string needle) => Matches(text, Words(needle));

        /// <summary>As <see cref="Matches(string,string)"/>, with the needle already split.</summary>
        public static bool Matches(string text, string[] words)
        {
            if (words == null || words.Length == 0) return true;
            if (string.IsNullOrEmpty(text)) return false;
            var compare = CultureInfo.InvariantCulture.CompareInfo;
            foreach (var w in words)
                if (compare.IndexOf(text, w, Loose) < 0) return false;
            return true;
        }

        /// <summary>
        /// Positions in <paramref name="texts"/> that match, in their original
        /// order. Positions where <paramref name="pinned"/> is true are always
        /// kept – the "New memory bank…" row, say, which must stay reachable
        /// precisely when nothing else matches.
        /// </summary>
        public static List<int> MatchingIndices(IList<string> texts, string needle, Func<int, bool> pinned = null)
        {
            var result = new List<int>();
            if (texts == null) return result;
            var words = Words(needle);
            for (int i = 0; i < texts.Count; i++)
            {
                if ((pinned != null && pinned(i)) || Matches(texts[i], words))
                    result.Add(i);
            }
            return result;
        }
    }
}
