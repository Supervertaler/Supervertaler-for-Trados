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

            var targetWords = Tokenise(plainTarget);
            var spokenWords = Tokenise(phrase).Select(w => w.Text).ToList();
            if (targetWords.Count == 0 || spokenWords.Count == 0) return null;

            // 2. Spoken parts that reassemble into ONE written word. A Dutch or German
            // compound is said as its parts run together, and the recogniser returns
            // them separately because the compound itself is not in its lexicon -
            // measured: "beschermingsperiode" is unknown to the small Dutch model
            // while "beschermings" and "periode" are both known. Joining the run and
            // looking for it as a whole word turns two heard words back into the one
            // written word the translator named.
            var joined = LongestJoinedRun(plainTarget, spokenWords);
            if (joined != null) return joined;

            // 3. Part of a compound, naming the whole of it. The recogniser often
            // returns only ONE part - measured: saying "beschermingsperiode" came back
            // as "beschermings", three times running - because the rest was swallowed
            // or is not in its lexicon. A part is unambiguous enough to act on: it is
            // long, and it names a word the translator can see.
            var partial = CompoundPart(plainTarget, targetWords, spokenWords);
            if (partial != null) return partial;

            // A single word that is not literally present is not a match. Widening a
            // one-word utterance into a span would be guesswork with nothing to
            // anchor it.
            if (spokenWords.Count < 2) return null;

            // Exact words first, then near ones. Tried in that order so a sentence
            // containing both "for" and "four" resolves to whichever was actually
            // said, and only falls back to sounding-alike when nothing else fits.
            var whole = Subsequence(plainTarget, targetWords, spokenWords, false)
                     ?? Subsequence(plainTarget, targetWords, spokenWords, true);
            if (whole != null) return whole;

            // 5. The same, ignoring stray words at the EDGES. The recogniser adds
            // them: "niet-conform licht" came back as "licht niet conform licht",
            // with a stray "licht" at each end, and requiring every spoken word to
            // line up threw away a three-word run that matched perfectly.
            //
            // Only the edges, and never down to a single word. That floor is what
            // keeps this from re-opening the question settled by reverting the
            // stray-function-word tier: "in further" and "to the" are two words, so
            // trimming either would leave one, and they still widen as before.
            // Trimming an edge discards a word that was heard; doing so to reach a
            // lone word would be a guess with nothing left to corroborate it.
            for (int length = spokenWords.Count - 1; length >= 2; length--)
            {
                for (int start = 0; start + length <= spokenWords.Count; start++)
                {
                    var window = spokenWords.GetRange(start, length);
                    var match = Subsequence(plainTarget, targetWords, window, false)
                             ?? Subsequence(plainTarget, targetWords, window, true);
                    if (match != null) return match;
                }
            }

            return null;
        }

        /// <summary>
        /// The longest run of consecutive spoken words that, joined without spaces, is
        /// a whole word of the target. Null when none is.
        ///
        /// <para>Longest wins so that a three-part compound is not settled by its first
        /// two parts. Runs of two or more only: a single word joins to itself, which
        /// the literal tier has already tried.</para>
        /// </summary>
        private static Match LongestJoinedRun(string plainTarget, List<string> spokenWords)
        {
            Match best = null;
            for (int start = 0; start < spokenWords.Count; start++)
            {
                var sb = new System.Text.StringBuilder(spokenWords[start]);
                for (int end = start + 1; end < spokenWords.Count; end++)
                {
                    sb.Append(spokenWords[end]);
                    var candidate = sb.ToString();
                    var at = IndexOfWord(plainTarget, candidate, 0);
                    if (at < 0) continue;

                    if (best == null || candidate.Length > best.Text.Length)
                    {
                        var text = plainTarget.Substring(at, candidate.Length);
                        best = new Match
                        {
                            Text = text,
                            Start = at,
                            // Not exact: the words were re-joined, not heard as one.
                            Exact = false,
                            Occurrences = CountOccurrences(plainTarget, text)
                        };
                    }
                }
            }
            return best;
        }

        /// <summary>
        /// How much of a compound has to be heard before it names it.
        ///
        /// <para>Four, to agree with the shortest part the grammar offers
        /// (TermLensEditorViewPart.CompoundMinPart). The two have to match: offering
        /// "rest" to the recogniser and then refusing to accept it - which is what a
        /// minimum of five did to "restluminantie" - means the one word it could say
        /// is the one word we throw away. Change either and change both.</para>
        ///
        /// <para>Three would be too few: "the", "and", "for" are words in their own
        /// right and would start claiming longer ones.</para>
        /// </summary>
        private const int MinPartLength = 4;

        /// <summary>
        /// A spoken run that is the START or END of a longer word in the segment,
        /// naming that whole word.
        ///
        /// <para>This is what makes an unknown compound reachable. "beschermingsperiode"
        /// is missing from the small Dutch model's lexicon and can never be returned;
        /// "beschermings" is in it and is what comes back. Requiring a whole-word match
        /// then rejected the only thing the recogniser was able to say.</para>
        ///
        /// <para>Deliberately mean, because a prefix match is a guess about intent.
        /// Five characters at least, so "the" cannot claim "therefore"; and the word
        /// must be meaningfully longer than the part, so an ordinary word is not
        /// swallowed by a neighbour that merely starts the same way.</para>
        /// </summary>
        private static Match CompoundPart(string plainTarget, List<Token> targetWords,
                                          List<string> spokenWords)
        {
            Match best = null;
            var bestPart = 0;   // the PART's length, not the matched word's

            for (int start = 0; start < spokenWords.Count; start++)
            {
                var sb = new System.Text.StringBuilder();
                for (int end = start; end < spokenWords.Count; end++)
                {
                    sb.Append(spokenWords[end]);
                    var part = sb.ToString();
                    if (part.Length < MinPartLength) continue;

                    foreach (var token in targetWords)
                    {
                        // The whole word must be longer than the part by a real margin;
                        // equal lengths are the literal tier's business.
                        if (token.Text.Length < part.Length + 3) continue;
                        if (!token.Text.StartsWith(part, StringComparison.OrdinalIgnoreCase)
                            && !token.Text.EndsWith(part, StringComparison.OrdinalIgnoreCase)) continue;

                        // Longest part wins: it is the most evidence, and on a
                        // three-part compound it picks the more specific reading.
                        if (best != null && part.Length <= bestPart) continue;
                        bestPart = part.Length;
                        best = new Match
                        {
                            Text = token.Text,
                            Start = token.Start,
                            Exact = false,
                            Occurrences = CountOccurrences(plainTarget, token.Text)
                        };
                    }
                }
            }

            return best;
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
        /// The whole hyphenated compound containing a span, or null when the span is
        /// not part of one.
        ///
        /// <para>Hyphens split, so "infrarood-emitters" is two tokens and saying
        /// "infrarood" selects just that half - correctly, because it IS a word there.
        /// But the other half can be unsayable: "emitters" is an English loan the Dutch
        /// model does not know, so no phrasing reaches the whole compound. Repeating
        /// the phrase widens to it, which is the same "say it again" the occurrence
        /// cycle already uses.</para>
        /// </summary>
        public static Match Enclosing(string plainTarget, int start, int length)
        {
            if (string.IsNullOrEmpty(plainTarget)) return null;
            if (start < 0 || length <= 0 || start + length > plainTarget.Length) return null;

            var from = start;
            var to = start + length;

            // A hyphen only JOINS when there is a letter or digit on both sides of it.
            // A dash used as punctuation has spaces around it and must not pull the
            // selection across a clause.
            while (from >= 2 && plainTarget[from - 1] == '-' && char.IsLetterOrDigit(plainTarget[from - 2]))
            {
                from -= 2;
                while (from > 0 && char.IsLetterOrDigit(plainTarget[from - 1])) from--;
            }
            while (to + 1 < plainTarget.Length && plainTarget[to] == '-' && char.IsLetterOrDigit(plainTarget[to + 1]))
            {
                to += 2;
                while (to < plainTarget.Length && char.IsLetterOrDigit(plainTarget[to])) to++;
            }

            if (from == start && to == start + length) return null;

            var text = plainTarget.Substring(from, to - from);
            return new Match
            {
                Text = text,
                Start = from,
                Exact = false,
                Occurrences = CountOccurrences(plainTarget, text)
            };
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

        private static bool Same(string a, string b, bool allowNear = false)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            if (!allowNear) return false;
            return Homophone(a, b) || Near(a, b);
        }

        /// <summary>
        /// Words that sound identical but share no spelling - so <see cref="Near"/>,
        /// which needs a common first letter, cannot see them.
        ///
        /// <para><b>This exists because of our own command list.</b> The grammar is
        /// closed, so the recogniser must return SOMETHING from it, and "term eight"
        /// and "match eight" put "eight" in the vocabulary of every segment. Say the
        /// article "a" - which is /eɪ/ - and "eight" is the nearest thing available.
        /// Measured 2026-09-12: "select a further" came back as "select eight further"
        /// six times running, and "select in addition to" as "in addition two". The
        /// number words we ship are quietly stealing short words out of the
        /// translator's own text.</para>
        ///
        /// <para>Only the number words that have a common English homophone are
        /// listed; three, five, six, seven and nine have none worth the risk. Like
        /// <see cref="Near"/>, this is consulted only after exact matching has failed
        /// everywhere, so a segment that really does contain "eight" resolves it to
        /// itself.</para>
        /// </summary>
        private static readonly string[][] Homophones =
        {
            new[] { "a", "eight", "ate" },
            new[] { "to", "two", "too" },
            new[] { "for", "four", "fore" },
            new[] { "one", "won" },
        };

        private static bool Homophone(string a, string b)
        {
            foreach (var set in Homophones)
            {
                var hasA = false;
                var hasB = false;
                foreach (var w in set)
                {
                    if (string.Equals(w, a, StringComparison.OrdinalIgnoreCase)) hasA = true;
                    if (string.Equals(w, b, StringComparison.OrdinalIgnoreCase)) hasB = true;
                }
                if (hasA && hasB) return true;
            }
            return false;
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
                // A hyphen ENDS a word; an apostrophe does not. The recogniser cannot
                // return a hyphen, and a compound is spoken as its parts - "night
                // vision device" for "night-vision device" - so treating the compound
                // as one token meant the spoken words could never line up with it.
                // "don't" stays whole, because that IS how it is said.
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '\'')) i++;
                list.Add(new Token { Text = text.Substring(start, i - start), Start = start });
            }
            return list;
        }
    }
}
