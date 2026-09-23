using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// A word-level diff of two strings, for writing a target as tracked changes
    /// rather than as one deletion of everything and one insertion of everything
    /// (issue #138).
    ///
    /// <para><b>Tokens are words, single punctuation marks and whitespace runs.</b>
    /// Punctuation as its own token is what makes "Belgium; depending" to
    /// "Belgium, depending" a one-character revision rather than a whole-word
    /// swap. Whitespace is a token because in these files a tab is layout, and is
    /// frequently the only thing that changed.</para>
    ///
    /// <para><b>Written here rather than taken from a library</b>, deliberately:
    /// the obvious candidate would be one more DLL shipped into Trados, where
    /// every shipped assembly has been a load-order risk. This is a textbook
    /// longest-common-subsequence and no more.</para>
    ///
    /// <para><b>Bounded.</b> The common prefix and suffix are trimmed first, which
    /// is where an ordinary edit spends almost all its length, and the quadratic
    /// table only ever covers what is left. If even that exceeds
    /// <see cref="MaxCells"/> the middle is reported as one deletion and one
    /// insertion: coarser, still exactly correct, and a pathological cell can
    /// never take the editor's memory with it.</para>
    /// </summary>
    internal static class TokenDiff
    {
        public enum Op { Equal, Delete, Insert }

        public struct Piece
        {
            public Op Op;
            public string Text;
            public Piece(Op op, string text) { Op = op; Text = text; }
            public override string ToString() => Op + "[" + Text + "]";
        }

        /// <summary>
        /// Upper bound on the comparison table, in cells. 2,000,000 is about 8 MB
        /// of ints, transient, and is reached only by a changed middle of over a
        /// thousand tokens on both sides - far beyond any real segment.
        /// </summary>
        internal const int MaxCells = 2000000;

        private static readonly Regex TokenPattern =
            new Regex(@"\w+|[^\w\s]|\s+", RegexOptions.Compiled);

        public static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            if (string.IsNullOrEmpty(text)) return tokens;
            foreach (Match m in TokenPattern.Matches(text)) tokens.Add(m.Value);
            return tokens;
        }

        /// <summary>
        /// The edit script from <paramref name="oldText"/> to
        /// <paramref name="newText"/>. Adjacent pieces of the same kind are always
        /// merged, and every contiguous change is exactly one Delete followed by
        /// one Insert (either may be absent) - one revision per change, which is
        /// what Studio itself records when a person makes the same edit.
        /// </summary>
        public static List<Piece> Diff(string oldText, string newText)
        {
            oldText = oldText ?? "";
            newText = newText ?? "";
            var result = new List<Piece>();
            if (oldText == newText)
            {
                if (oldText.Length > 0) result.Add(new Piece(Op.Equal, oldText));
                return result;
            }

            var a = Tokenize(oldText);
            var b = Tokenize(newText);

            // Common prefix and suffix, in tokens.
            int pre = 0;
            while (pre < a.Count && pre < b.Count && a[pre] == b[pre]) pre++;
            int suf = 0;
            while (suf < a.Count - pre && suf < b.Count - pre
                   && a[a.Count - 1 - suf] == b[b.Count - 1 - suf]) suf++;

            var raw = new List<Piece>();
            for (int i = 0; i < pre; i++) raw.Add(new Piece(Op.Equal, a[i]));

            int n = a.Count - pre - suf, m = b.Count - pre - suf;
            if (n == 0 || m == 0 || (long)(n + 1) * (m + 1) > MaxCells)
            {
                // Nothing on one side, or too big to compare finely: the whole
                // middle is one change. Correct either way, just coarser.
                for (int i = 0; i < n; i++) raw.Add(new Piece(Op.Delete, a[pre + i]));
                for (int j = 0; j < m; j++) raw.Add(new Piece(Op.Insert, b[pre + j]));
            }
            else
            {
                raw.AddRange(Lcs(a, pre, n, b, pre, m));
            }

            for (int i = a.Count - suf; i < a.Count; i++) raw.Add(new Piece(Op.Equal, a[i]));

            return SlideToWordStart(Coalesce(raw));
        }

        /// <summary>
        /// Moves a pure insertion or deletion past leading whitespace that the
        /// following text also begins with, so "a[ named] contact" becomes
        /// "a [named ]contact". The two describe the same edit - rotating the
        /// shared character across the boundary changes neither the old text nor
        /// the new - but a reviewer should not see an underline start on a space.
        ///
        /// <para>Needed because trimming the common suffix is greedy: it takes the
        /// space in front of an unchanged word, leaving the inserted word to carry
        /// the space before it. Only pure edits flanked by unchanged text are
        /// moved; a deletion paired with an insertion is left exactly as found.</para>
        /// </summary>
        private static List<Piece> SlideToWordStart(List<Piece> p)
        {
            for (int i = 1; i + 1 < p.Count; i++)
            {
                if (p[i].Op == Op.Equal || p[i - 1].Op != Op.Equal || p[i + 1].Op != Op.Equal)
                    continue;

                var prev = p[i - 1].Text;
                var x = p[i].Text;
                var next = p[i + 1].Text;
                while (x.Length > 0 && char.IsWhiteSpace(x[0]) && next.Length > 0 && next[0] == x[0])
                {
                    prev += x[0];
                    x = x.Substring(1) + x[0];
                    next = next.Substring(1);
                }
                p[i - 1] = new Piece(Op.Equal, prev);
                p[i] = new Piece(p[i].Op, x);
                p[i + 1] = new Piece(Op.Equal, next);
            }
            p.RemoveAll(piece => piece.Op == Op.Equal && piece.Text.Length == 0);
            return p;
        }

        private static List<Piece> Lcs(List<string> a, int aOff, int n, List<string> b, int bOff, int m)
        {
            // len[i, j] = LCS length of a[i..n) and b[j..m). Built from the end so
            // the walk below reads forwards.
            var len = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
                for (int j = m - 1; j >= 0; j--)
                    len[i, j] = a[aOff + i] == b[bOff + j]
                        ? len[i + 1, j + 1] + 1
                        : Math.Max(len[i + 1, j], len[i, j + 1]);

            var ops = new List<Piece>(n + m);
            int x = 0, y = 0;
            while (x < n && y < m)
            {
                if (a[aOff + x] == b[bOff + y]) { ops.Add(new Piece(Op.Equal, a[aOff + x])); x++; y++; }
                else if (len[x + 1, y] >= len[x, y + 1]) { ops.Add(new Piece(Op.Delete, a[aOff + x])); x++; }
                else { ops.Add(new Piece(Op.Insert, b[bOff + y])); y++; }
            }
            while (x < n) { ops.Add(new Piece(Op.Delete, a[aOff + x])); x++; }
            while (y < m) { ops.Add(new Piece(Op.Insert, b[bOff + y])); y++; }
            return ops;
        }

        /// <summary>
        /// Between two equal stretches, gather every deletion into one piece and
        /// every insertion into one piece, deletion first. The raw walk can
        /// interleave them token by token; a reviewer should see one change.
        /// </summary>
        private static List<Piece> Coalesce(List<Piece> raw)
        {
            var result = new List<Piece>();
            var eq = new StringBuilder();
            var del = new StringBuilder();
            var ins = new StringBuilder();

            void FlushChange()
            {
                if (del.Length > 0) { result.Add(new Piece(Op.Delete, del.ToString())); del.Clear(); }
                if (ins.Length > 0) { result.Add(new Piece(Op.Insert, ins.ToString())); ins.Clear(); }
            }
            void FlushEqual()
            {
                if (eq.Length > 0) { result.Add(new Piece(Op.Equal, eq.ToString())); eq.Clear(); }
            }

            foreach (var p in raw)
            {
                if (p.Op == Op.Equal)
                {
                    FlushChange();
                    eq.Append(p.Text);
                }
                else
                {
                    FlushEqual();
                    (p.Op == Op.Delete ? del : ins).Append(p.Text);
                }
            }
            FlushEqual();
            FlushChange();
            return result;
        }
    }
}
