using System;
using System.Collections.Generic;
using System.Linq;

namespace Supervertaler.Trados.VoiceControl
{
    /// <summary>
    /// English number words for selecting by number (issue #128): what goes into
    /// the recogniser's grammar while the number popup is open, and how an
    /// utterance heard against that grammar is read back as a word index or range.
    ///
    /// <para><b>Why words, and why only while the popup is open.</b> Vosk returns
    /// words, not digits, so "12" has to be in the grammar as "twelve". And the
    /// grammar is closed: every phrase in it competes for every sound in every
    /// utterance. Sixty number words resident permanently would be a recognition
    /// disaster - "term eight" alone was enough to make the article "a" unsayable
    /// (see the homophone table in PhraseMatcher). So the numbers join the grammar
    /// when the popup opens and leave when it closes.</para>
    /// </summary>
    internal static class NumberWords
    {
        private static readonly string[] Ones =
        {
            "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
            "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen",
            "seventeen", "eighteen", "nineteen"
        };
        private static readonly string[] Tens =
        {
            null, null, "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"
        };

        /// <summary>Range keywords. "two" is deliberately NOT here: see Parse.</summary>
        private static readonly HashSet<string> RangeWords =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "to", "through", "till", "until", "thru" };

        /// <summary>
        /// What closes the popup without selecting. "escape" and "close window" are
        /// the existing command's phrases: while the popup is open, closing the
        /// popup is what pressing Escape would mean, and the popup cannot receive
        /// the key itself - it never has focus.
        /// </summary>
        private static readonly HashSet<string> CancelWords =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "cancel", "close", "never mind", "dismiss", "escape", "close window" };

        /// <summary>The spoken form of 1..99: "seven", "twenty three".</summary>
        public static string ToWords(int n)
        {
            if (n < 0 || n > 99) return n.ToString();
            if (n < 20) return Ones[n];
            return n % 10 == 0 ? Tens[n / 10] : Tens[n / 10] + " " + Ones[n % 10];
        }

        /// <summary>
        /// Grammar phrases for a popup numbering <paramref name="count"/> words: the
        /// number words up to that count, the range and cancel words, and the
        /// selection prefixes so "select twelve" is one phrase the recogniser knows.
        /// Capped at 99 - a segment with more words than that is numbered anyway,
        /// but the words past 99 cannot be said and the popup says so.
        /// </summary>
        public static List<string> GrammarPhrases(int count, IEnumerable<string> selectPrefixes)
        {
            var phrases = new List<string>();
            var max = Math.Min(Math.Max(count, 1), 99);
            for (int i = 1; i <= max; i++) phrases.Add(ToWords(i));
            phrases.AddRange(RangeWords);
            phrases.AddRange(CancelWords);
            if (selectPrefixes != null) phrases.AddRange(selectPrefixes);
            return phrases.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static bool IsCancel(string utterance)
        {
            var t = (utterance ?? "").Trim();
            return t.Length > 0 && CancelWords.Contains(t);
        }

        /// <summary>
        /// Reads an utterance as a word number or a range of them. Accepts "seven",
        /// "select seven", "source select seven", "seven to nine", "select seven
        /// through nine", and - because the recogniser cannot tell "to" from "two" -
        /// "seven two nine", read as 7..9: when three or more numbers arrive, the
        /// first and last are the range. Leading words that are not numbers are
        /// skipped (a prefix, a stray word). False when no number was said.
        /// </summary>
        public static bool TryParse(string utterance, out int from, out int to)
        {
            from = to = 0;
            var tokens = (utterance ?? "").ToLowerInvariant()
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var numbers = new List<int>();
            int? pendingTens = null;
            foreach (var tok in tokens)
            {
                var tens = Array.IndexOf(Tens, tok);
                var ones = Array.IndexOf(Ones, tok);
                if (tens >= 2)
                {
                    if (pendingTens != null) numbers.Add(pendingTens.Value * 10);
                    pendingTens = tens;
                }
                else if (ones >= 0)
                {
                    if (pendingTens != null && ones < 10) { numbers.Add(pendingTens.Value * 10 + ones); pendingTens = null; }
                    else { if (pendingTens != null) { numbers.Add(pendingTens.Value * 10); pendingTens = null; } numbers.Add(ones); }
                }
                else
                {
                    int digits;
                    if (int.TryParse(tok, out digits)) { if (pendingTens != null) { numbers.Add(pendingTens.Value * 10); pendingTens = null; } numbers.Add(digits); }
                    else if (pendingTens != null) { numbers.Add(pendingTens.Value * 10); pendingTens = null; }
                    // range and prefix words carry no value; anything else is skipped
                }
            }
            if (pendingTens != null) numbers.Add(pendingTens.Value * 10);
            numbers.RemoveAll(n => n <= 0);
            if (numbers.Count == 0) return false;

            // "forty two" came back from the recogniser as "four two" (2026-09-17),
            // and two numbers read as a range selected words 2 to 4 - and reported
            // success. Two single digits with no range word between them are the
            // digits of one number, as anyone saying "four two" would mean; a range
            // needs "to" or a number of ten or more.
            var sawRangeWord = tokens.Any(t => RangeWords.Contains(t));
            if (numbers.Count == 2 && !sawRangeWord && numbers[0] < 10 && numbers[1] < 10)
            {
                from = to = numbers[0] * 10 + numbers[1];
                return true;
            }
            from = numbers[0];
            to = numbers[numbers.Count - 1];
            if (to < from) { var t = from; from = to; to = t; }
            return true;
        }
    }
}
