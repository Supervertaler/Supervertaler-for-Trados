using System;
using System.Collections.Generic;
using System.Linq;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// The text-only half of Apply on the Reports tab: what a proofreader's
    /// <c>Corrected:</c> line becomes before it is offered, and whether it can be
    /// written into its segment at all. Judged on the strings the proofreader was
    /// shown (<see cref="SegmentTagHandler.ToProofreadText"/>), so it needs no
    /// document and can be checked offline. Whether the segment still reads as
    /// it did is the other half, asked of the live segment when Apply is pressed.
    /// </summary>
    internal static class ProofreadCorrection
    {
        /// <summary>
        /// The corrected target as it would be written, or null when there is
        /// nothing to write: no line, a blank one, or one that reads exactly as
        /// the target already does. A model drops the spaces at either end of a
        /// line it writes out, so the target's own are put back; the merge would
        /// otherwise show a space nobody asked to remove as a deletion.
        /// </summary>
        public static string Normalise(string target, string corrected)
        {
            if (string.IsNullOrWhiteSpace(corrected)) return null;
            target = target ?? "";

            var core = Unquote(corrected.Trim(), target.Trim());
            var lead = target.Substring(0, target.Length - target.TrimStart().Length);
            var trail = target.Substring(target.TrimEnd().Length);
            var result = KeepTargetCharacters(target, lead + core + trail);

            return string.Equals(result, target, StringComparison.Ordinal) ? null : result;
        }

        /// <summary>
        /// <paramref name="corrected"/> with the target's own non-breaking spaces
        /// and hyphens wherever the text around them is unchanged. A model writes
        /// an ordinary space for a non-breaking one, so a correction of "80 °C" came
        /// back with "60 °C" free to break across lines - and so did every "120 mm"
        /// in the segment it did not touch (live test, 16 Oct 2026). Compared word
        /// by word with such characters folded to their ordinary forms - one
        /// character for one, so positions line up - and every unchanged stretch is
        /// taken from the target as it was. Text the correction changes or adds is
        /// its own. Idempotent.
        /// </summary>
        public static string KeepTargetCharacters(string target, string corrected)
        {
            if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(corrected)) return corrected;
            var folded = FoldSpecial(target);
            if (string.Equals(folded, target, StringComparison.Ordinal)) return corrected;   // nothing to keep

            var sb = new System.Text.StringBuilder(corrected.Length);
            int t = 0, c = 0;
            foreach (var piece in TokenDiff.Diff(folded, FoldSpecial(corrected)))
            {
                int len = piece.Text.Length;
                switch (piece.Op)
                {
                    case TokenDiff.Op.Equal:
                        sb.Append(target, t, len);
                        t += len; c += len;
                        break;
                    case TokenDiff.Op.Delete:
                        t += len;
                        break;
                    case TokenDiff.Op.Insert:
                        sb.Append(corrected, c, len);
                        c += len;
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>Non-breaking and fixed-width spaces as a space, a non-breaking
        /// hyphen as a hyphen: one character for one.</summary>
        private static string FoldSpecial(string s)
        {
            var chars = s.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                switch ((int)chars[i])
                {
                    case 0x00A0: case 0x202F: case 0x2007: case 0x2009: case 0x200A:
                        chars[i] = ' ';
                        break;
                    case 0x2011:
                        chars[i] = '-';
                        break;
                }
            }
            return new string(chars);
        }

        /// <summary>
        /// Why <paramref name="corrected"/> cannot be written over
        /// <paramref name="target"/>, or null when it can. Both are in the
        /// placeholders the proofreader was shown. The correction must carry
        /// the tags of one of them: the source's, or the target's as it stands.
        /// </summary>
        public static string Problem(string source, string target, string corrected)
        {
            if (string.IsNullOrWhiteSpace(corrected))
                return "the AI gave no corrected translation for it.";

            var tags = TagProblem(source, target, corrected);
            if (tags != null) return tags;

            // A soft return (LF), a hard one (CR) or an InDesign forced line break
            // (U+2028) is formatting the translator put there. The contract asks for
            // them to be kept; a correction that loses one would take it out of the
            // segment. (The reply is read line by line with its CRs dropped, so a
            // target holding a CR is refused here rather than silently changed.)
            if (Count(corrected, '\n') != Count(target, '\n')
                || Count(corrected, '\r') != Count(target, '\r')
                || Count(corrected, '\u2028') != Count(target, '\u2028')
                || Count(corrected, '\u2029') != Count(target, '\u2029'))
                return "the correction does not keep the target's line breaks.";

            return null;
        }

        /// <summary>
        /// Null when <paramref name="corrected"/> carries exactly the source's
        /// tags, or exactly the tags <paramref name="target"/> has now: every
        /// paired tag opened once and closed once, properly nested, every
        /// standalone tag once, and no other tag. Otherwise what is wrong, in
        /// words for the card. Stricter than the rebuild, which makes the best of
        /// a malformed reply; a write the translator asked for must not quietly
        /// drop a tag.
        ///
        /// <para>Why the target's tags count (#141, 18 Nov 2026): until 208 only
        /// the source's did. A target whose tags already differed from the
        /// source's - bold added by hand, a tag left over from a fuzzy match,
        /// the source's tags never copied - could then not be corrected at all:
        /// a correction that changed one word and left the tags alone was
        /// refused for tags it had not touched. The proofreader corrects the
        /// wording; a correction that leaves the tags as the translator had them
        /// makes nothing worse. The target is in the source's numbering
        /// (<see cref="SegmentTagHandler.SerializeAlignedTo"/>), so a tag of its
        /// own never shares a number with one of the source's.</para>
        /// </summary>
        public static string TagProblem(string source, string target, string corrected)
        {
            var againstSource = TagProblemAgainst(source, corrected, "source");
            if (againstSource == null) return null;

            var sourceTags = TagsOf(source);
            var targetTags = TagsOf(target);
            bool targetDiffers = sourceTags.Count != targetTags.Count
                || sourceTags.Any(kv => !targetTags.TryGetValue(kv.Key, out var paired) || paired != kv.Value);
            if (!targetDiffers) return againstSource;

            // Neither: said against the target, whose tags the correction was
            // made from and should have kept.
            return TagProblemAgainst(target, corrected, "target");
        }

        /// <summary>The tags of <paramref name="text"/>: number -> paired?</summary>
        private static Dictionary<int, bool> TagsOf(string text)
        {
            var tags = new Dictionary<int, bool>();
            foreach (var t in SegmentTagHandler.Tokenize(text ?? ""))
            {
                if (t is SegmentTagHandler.TokenOpenTag o) tags[o.TagNumber] = true;
                else if (t is SegmentTagHandler.TokenCloseTag c) tags[c.TagNumber] = true;
                else if (t is SegmentTagHandler.TokenStandaloneTag s) tags[s.TagNumber] = false;
            }
            return tags;
        }

        /// <summary>Null when <paramref name="corrected"/> carries exactly the
        /// tags of <paramref name="reference"/>; <paramref name="whose"/> names
        /// it in the message ("source", "target").</summary>
        private static string TagProblemAgainst(string reference, string corrected, string whose)
        {
            var expected = TagsOf(reference);

            var seen = new HashSet<int>();
            var open = new Stack<int>();
            foreach (var t in SegmentTagHandler.Tokenize(corrected ?? ""))
            {
                if (t is SegmentTagHandler.TokenOpenTag o)
                {
                    if (!expected.TryGetValue(o.TagNumber, out var paired)) return NotIn(whose, "<t" + o.TagNumber + ">");
                    if (!paired) return "the correction has <t" + o.TagNumber + "> where the " + whose + " has the standalone tag <t" + o.TagNumber + "/>.";
                    if (!seen.Add(o.TagNumber)) return "the correction has <t" + o.TagNumber + "> twice.";
                    open.Push(o.TagNumber);
                }
                else if (t is SegmentTagHandler.TokenCloseTag c)
                {
                    if (!expected.ContainsKey(c.TagNumber)) return NotIn(whose, "</t" + c.TagNumber + ">");
                    if (open.Count == 0 || open.Peek() != c.TagNumber)
                        return "the correction's tags are not closed in the order they open (</t" + c.TagNumber + ">).";
                    open.Pop();
                }
                else if (t is SegmentTagHandler.TokenStandaloneTag s)
                {
                    if (!expected.TryGetValue(s.TagNumber, out var paired)) return NotIn(whose, "<t" + s.TagNumber + "/>");
                    if (paired) return "the correction has <t" + s.TagNumber + "/> where the " + whose + " has the tag pair <t" + s.TagNumber + ">.";
                    if (!seen.Add(s.TagNumber)) return "the correction has <t" + s.TagNumber + "/> twice.";
                }
            }

            if (open.Count > 0)
                return "the correction opens <t" + open.Peek() + "> and never closes it.";

            var missing = expected.Keys.Where(n => !seen.Contains(n)).OrderBy(n => n)
                .Select(n => expected[n] ? "<t" + n + ">" : "<t" + n + "/>").ToList();
            if (missing.Count > 0)
                return "the correction is missing the " + whose + "'s " + string.Join(", ", missing)
                       + (missing.Count == 1 ? " tag." : " tags.");

            return null;
        }

        // Quote marks a model may put round a line it was told to give as it stands.
        private static readonly string[][] WrappingQuotes =
        {
            new[] { "\"", "\"" }, new[] { "'", "'" }, new[] { "`", "`" },
            new[] { ((char)0x201C).ToString(), ((char)0x201D).ToString() },   // curly double quotes
            new[] { ((char)0x2018).ToString(), ((char)0x2019).ToString() },   // curly single quotes
        };

        /// <summary>
        /// <paramref name="corrected"/> without a pair of quote marks round the
        /// whole of it, when the target has none there: written into the segment,
        /// they would be text the translation never had. A target that is itself
        /// quoted keeps its quotes, and so does a line with the same mark inside
        /// it: "'Start' or 'Stop'" is two quoted words, not one quoted line.
        /// </summary>
        private static string Unquote(string corrected, string target)
        {
            foreach (var q in WrappingQuotes)
            {
                if (corrected.Length < q[0].Length + q[1].Length + 1
                    || !corrected.StartsWith(q[0], StringComparison.Ordinal)
                    || !corrected.EndsWith(q[1], StringComparison.Ordinal)
                    || (target.StartsWith(q[0], StringComparison.Ordinal) && target.EndsWith(q[1], StringComparison.Ordinal)))
                    continue;
                var inner = corrected.Substring(q[0].Length, corrected.Length - q[0].Length - q[1].Length);
                if (inner.IndexOf(q[0], StringComparison.Ordinal) >= 0 || inner.IndexOf(q[1], StringComparison.Ordinal) >= 0)
                    continue;
                return inner.Trim();
            }
            return corrected;
        }

        private static string NotIn(string whose, string tag) =>
            "the correction has " + tag + ", which the " + whose + " does not have.";

        private static int Count(string s, char c)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int n = 0;
            foreach (var ch in s) if (ch == c) n++;
            return n;
        }
    }
}
