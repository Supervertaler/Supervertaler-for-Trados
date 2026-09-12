using System;
using System.Collections.Generic;
using System.Linq;

namespace Supervertaler.Trados.VoiceControl
{
    /// <summary>
    /// Turns what the recogniser heard into a run of the segment's own text
    /// (issue #125).
    ///
    /// <para><b>Why a matcher is needed at all, and why not the one the design brief
    /// asked for.</b> The brief expected phonetic matching - Metaphone plus
    /// Levenshtein - because it assumed open-vocabulary recognition that would
    /// mishear "sealing ring" as "seal the ring". Vosk runs on a grammar built from
    /// this segment's words, so that problem does not arise: measured, it returns
    /// exact runs of them.</para>
    ///
    /// <para>What it does do is <b>drop short function words</b>. Saying "comprises
    /// at most" came back three times as "comprises most" - "at" is unstressed and
    /// vanishes. Requiring a literal substring then failed, correctly but uselessly.
    /// So the words are matched as an ordered SUBSEQUENCE and the span between the
    /// first and last of them is what gets selected: "comprises ... most" resolves
    /// to "comprises at most".</para>
    ///
    /// <para>The gap is bounded. Without a limit, "the display" would happily match
    /// "the" in one clause and "display" in the next and select the whole lot -
    /// worse than refusing, because it acts on text the translator did not name.</para>
    /// </summary>
    internal static class PhraseMatcher
    {
        /// <summary>
        /// How many unspoken words may sit between two spoken ones. Two covers the
        /// dropped article or preposition that causes this ("comprises [at] most",
        /// "duty cycle [of the] display" is already too far). Higher would start
        /// selecting across clauses on a two-word utterance.
        /// </summary>
        private const int MaxGap = 2;

        public class Match
        {
            /// <summary>The segment's own text for the span, with its own casing.</summary>
            public string Text;
            /// <summary>Character offset of the span in the plain target.</summary>
            public int Start;
            /// <summary>True when the heard words matched with nothing skipped.</summary>
            public bool Exact;
            /// <summary>
            /// How many times this text occurs in the target. More than one means the
            /// translator named something ambiguous, and FindTextInSegment will take
            /// the first - there is no supported way to reach a later one, so the
            /// honest response is to say so and let them speak more words.
            /// </summary>
            public int Occurrences = 1;
        }

        /// <summary>
        /// Finds the span of <paramref name="plainTarget"/> that the spoken phrase
        /// names, or null when it names nothing found there.
        ///
        /// Prefers a literal substring: that is the common case and needs no
        /// judgement. Falls back to an ordered subsequence for the dropped-word case.
        /// </summary>
        public static Match Find(string plainTarget, string spoken)
        {
            if (string.IsNullOrWhiteSpace(plainTarget) || string.IsNullOrWhiteSpace(spoken))
                return null;

            var phrase = spoken.Trim();

            // 1. Literal. What happens when nothing was dropped.
            var at = plainTarget.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                var text = plainTarget.Substring(at, phrase.Length);
                return new Match
                {
                    Text = text,
                    Start = at,
                    Exact = true,
                    Occurrences = CountOccurrences(plainTarget, text)
                };
            }

            // 2. Ordered subsequence, for the words the recogniser swallowed.
            var targetWords = Tokenise(plainTarget);
            var spokenWords = Tokenise(phrase).Select(w => w.Text).ToList();
            if (targetWords.Count == 0 || spokenWords.Count == 0) return null;

            // A single word that is not literally present is not a match. Widening a
            // one-word utterance into a span would be guesswork with nothing to
            // anchor it.
            if (spokenWords.Count < 2) return null;

            for (int start = 0; start < targetWords.Count; start++)
            {
                if (!Same(targetWords[start].Text, spokenWords[0])) continue;

                int ti = start, si = 1, last = start;
                while (si < spokenWords.Count)
                {
                    int gap = 0, probe = ti + 1;
                    while (probe < targetWords.Count && gap <= MaxGap)
                    {
                        if (Same(targetWords[probe].Text, spokenWords[si])) break;
                        probe++; gap++;
                    }
                    if (probe >= targetWords.Count || gap > MaxGap) break;
                    ti = probe; last = probe; si++;
                }

                if (si == spokenWords.Count)
                {
                    var from = targetWords[start].Start;
                    var to = targetWords[last].Start + targetWords[last].Text.Length;
                    var span = plainTarget.Substring(from, to - from);
                    return new Match
                    {
                        Text = span,
                        Start = from,
                        Exact = false,
                        Occurrences = CountOccurrences(plainTarget, span)
                    };
                }
            }

            return null;
        }

        /// <summary>How many times a piece of text occurs, case-insensitively.</summary>
        public static int CountOccurrences(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return 0;
            int n = 0, i = 0;
            while (true)
            {
                var at = haystack.IndexOf(needle, i, StringComparison.OrdinalIgnoreCase);
                if (at < 0) break;
                n++; i = at + 1;
            }
            return n;
        }

        private static bool Same(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private class Token { public string Text; public int Start; }

        /// <summary>
        /// Words with their offsets. Punctuation is not a word - the recogniser never
        /// says it - but it is left in the underlying string, so a span that runs
        /// across a comma keeps it.
        /// </summary>
        private static List<Token> Tokenise(string text)
        {
            var list = new List<Token>();
            int i = 0;
            while (i < text.Length)
            {
                while (i < text.Length && !char.IsLetterOrDigit(text[i])) i++;
                if (i >= text.Length) break;
                int start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '\'' || text[i] == '-')) i++;
                list.Add(new Token { Text = text.Substring(start, i - start), Start = start });
            }
            return list;
        }
    }
}
