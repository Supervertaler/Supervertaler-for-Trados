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

            // 1. Literal, on WORD boundaries. Without the boundary check, "the"
            // matches inside "further" - which comes first in "In a further
            // embodiment, the ..." - and the translator watches a command select
            // three letters in the middle of a different word.
            var at = IndexOfWord(plainTarget, phrase, 0);
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

            // 2. A stray leading function word, dropped. The recogniser inserts these:
            // "select the" came back as "select to the", and the subsequence tier
            // below then matched "to ... the" and widened across the unspoken
            // "adjusting" to select three words instead of one (measured 2026-09-12).
            //
            // Only when the whole phrase has no literal match, so a genuine "to the"
            // that IS in the segment is matched as itself by tier 1 and never reaches
            // here. And only when what remains matches literally - trading one
            // guessed reading for another would be no improvement.
            var firstSpace = phrase.IndexOf(' ');
            if (firstSpace > 0 && IsStrayCandidate(phrase.Substring(0, firstSpace)))
            {
                var rest = phrase.Substring(firstSpace + 1).Trim();
                var restAt = rest.Length > 0 ? IndexOfWord(plainTarget, rest, 0) : -1;
                if (restAt >= 0)
                {
                    var restText = plainTarget.Substring(restAt, rest.Length);
                    return new Match
                    {
                        Text = restText,
                        Start = restAt,
                        Exact = true,
                        Occurrences = CountOccurrences(plainTarget, restText)
                    };
                }
            }

            // 3. Ordered subsequence, for the words the recogniser swallowed.
            var targetWords = Tokenise(plainTarget);
            var spokenWords = Tokenise(phrase).Select(w => w.Text).ToList();
            if (targetWords.Count == 0 || spokenWords.Count == 0) return null;

            // A single word that is not literally present is not a match. Widening a
            // one-word utterance into a span would be guesswork with nothing to
            // anchor it.
            if (spokenWords.Count < 2) return null;

            // Exact words first, then near ones. Tried in that order so a sentence
            // containing both "for" and "four" resolves to whichever was actually
            // said, and only falls back to sounding-alike when nothing else fits.
            return Subsequence(plainTarget, targetWords, spokenWords, false)
                ?? Subsequence(plainTarget, targetWords, spokenWords, true);
        }

        private static Match Subsequence(string plainTarget, List<Token> targetWords,
                                         List<string> spokenWords, bool allowNear)
        {
            for (int start = 0; start < targetWords.Count; start++)
            {
                if (!Same(targetWords[start].Text, spokenWords[0], allowNear)) continue;

                int ti = start, si = 1, last = start;
                while (si < spokenWords.Count)
                {
                    int gap = 0, probe = ti + 1;
                    while (probe < targetWords.Count && gap <= MaxGap)
                    {
                        if (Same(targetWords[probe].Text, spokenWords[si], allowNear)) break;
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

        /// <summary>
        /// How many times a piece of text occurs as whole words. Counting substring
        /// hits would report "the" three times in a sentence holding one "the" and
        /// two words that merely contain it, and the ambiguity warning would be
        /// nonsense.
        /// </summary>
        public static int CountOccurrences(string haystack, string needle)
        {
            return Occurrences(haystack, needle).Count;
        }

        /// <summary>
        /// Where a piece of text occurs as whole words, in order. Saying the same
        /// phrase twice steps to the next of these, so the translator can reach an
        /// occurrence other than the first without naming more words.
        /// </summary>
        public static List<int> Occurrences(string haystack, string needle)
        {
            var list = new List<int>();
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return list;
            int i = 0;
            while (true)
            {
                var at = IndexOfWord(haystack, needle, i);
                if (at < 0) break;
                list.Add(at); i = at + 1;
            }
            return list;
        }

        /// <summary>
        /// The first occurrence at or after <paramref name="from"/> that is not
        /// buried inside a longer word. A needle that begins or ends with
        /// punctuation is only checked on the side where it begins or ends with a
        /// letter, so a span like "cycle, be" still matches.
        /// </summary>
        private static int IndexOfWord(string haystack, string needle, int from)
        {
            if (string.IsNullOrEmpty(needle)) return -1;
            for (int i = Math.Max(0, from); ; )
            {
                var at = haystack.IndexOf(needle, i, StringComparison.OrdinalIgnoreCase);
                if (at < 0) return -1;

                var startOk = !char.IsLetterOrDigit(needle[0])
                              || at == 0
                              || !char.IsLetterOrDigit(haystack[at - 1]);

                var end = at + needle.Length;
                var endOk = !char.IsLetterOrDigit(needle[needle.Length - 1])
                            || end >= haystack.Length
                            || !char.IsLetterOrDigit(haystack[end]);

                if (startOk && endOk) return at;
                i = at + 1;
            }
        }

        /// <summary>
        /// Short, unstressed words the recogniser inserts in front of what was
        /// actually said. Deliberately a short closed list: every word here is one a
        /// translator would rarely open a selection with on purpose, and the phrase
        /// still has to match literally without it.
        /// </summary>
        private static readonly HashSet<string> StrayWords =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "to", "a", "an", "of", "in", "and", "at", "it", "is", "that", "for", "on" };

        private static bool IsStrayCandidate(string word)
        {
            return StrayWords.Contains(word.Trim());
        }

        private static bool Same(string a, string b, bool allowNear = false)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            if (!allowNear) return false;
            return Near(a, b);
        }

        /// <summary>
        /// Two words close enough to be the same one misheard.
        ///
        /// <para>The grammar is closed, so the recogniser cannot invent a word - but
        /// it can pick the wrong one FROM the grammar, and the words it confuses are
        /// the ones that sound alike. Measured: "for" came back as "four" (from the
        /// "term four" command) and as "further" (from the segment itself). A closed
        /// vocabulary removes open-ended mishearing; it does not remove this.</para>
        ///
        /// <para>One edit for a short word, two for a long one, and the first letter
        /// must agree. It runs only after exact matching has failed EVERYWHERE, which
        /// is what keeps it safe: a segment holding both "for" and "four" resolves
        /// each to itself, and only a word with no exact counterpart anywhere is
        /// allowed to fall back to sounding alike.</para>
        ///
        /// <para>It does permit "the"/"then" - but only in a segment containing
        /// neither exactly, and the recogniser cannot return a word the grammar does
        /// not hold. What it will not do is confuse two words that are both really
        /// there: "addition" and "adjusting" share a first letter and nothing
        /// else.</para>
        /// </summary>
        private static bool Near(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            if (char.ToLowerInvariant(a[0]) != char.ToLowerInvariant(b[0])) return false;
            if (Math.Abs(a.Length - b.Length) > 2) return false;

            var budget = Math.Max(a.Length, b.Length) >= 6 ? 2 : 1;
            return Distance(a, b) <= budget;
        }

        /// <summary>Levenshtein, case-insensitive.</summary>
        private static int Distance(string a, string b)
        {
            a = a.ToLowerInvariant(); b = b.ToLowerInvariant();
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                Array.Copy(cur, prev, cur.Length);
            }
            return prev[b.Length];
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
