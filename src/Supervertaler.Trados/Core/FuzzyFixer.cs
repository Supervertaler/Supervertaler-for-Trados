using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// FuzzyFixer: the AI repairs a fuzzy translation memory match so that it fits
    /// the active segment, changing only what the difference between the two
    /// source sentences requires (Workbench has had it since its "Fuzzy Fix
    /// Current Segment").
    ///
    /// <para>This class is the part with no Studio in it: which match can be
    /// repaired, the instruction the request carries, the neighbouring segments
    /// it is given, whether the reply is usable, and what changed. The request
    /// itself is an ordinary one-segment translation (the Alt+T pipeline), with
    /// the match in the user prompt's memory block and
    /// <see cref="RepairInstruction"/> at the start of the user prompt - so the
    /// prompt, the termbase terms and SuperMemory reach the model exactly as they
    /// do for Alt+T, and Plugin Core is not changed.</para>
    ///
    /// <para><b>Cost.</b> Everything that differs from one repair to the next
    /// (the segments around the active one, the match, the segment) is in the
    /// user prompt. The system prompt - the translator's prompt and the memory
    /// bank, by far the largest part - is then the same bytes for every repair,
    /// and the same as Alt+T's when Alt+T sends no document, so the provider's
    /// prompt cache serves it. With the neighbours in the system prompt, as it
    /// was first built, every repair of another segment paid for the whole bank
    /// again (about 10,000 tokens, written to the cache at twice the price).</para>
    ///
    /// <para><b>Document context.</b> Unlike Alt+T it never sends the whole
    /// document, whatever "Include document context" says: a repair keeps the
    /// match's wording, so what it needs is the sentences around the segment,
    /// not the document's type. It sends <see cref="NeighbourCount"/> segments
    /// before and after (<see cref="NeighboursBlock"/>).</para>
    ///
    /// <para><b>Scale.</b> One segment, one memory search and one AI request per
    /// use; the Editor waits behind the preview window meanwhile, so requests
    /// cannot pile up. Nothing here is kept between uses.</para>
    /// </summary>
    internal static class FuzzyFixer
    {
        public const string Title = "FuzzyFixer";

        /// <summary>How many segments before, and how many after, the active one go with a repair.</summary>
        public const int NeighbourCount = 3;

        /// <summary>
        /// Opens the user prompt (see <see cref="RepairLead"/>). The same text for
        /// every repair; the match itself travels with the segment. It
        /// has to say that it replaces the memory block's "reference only" note,
        /// which is written for Batch Translate, where a match is advice and not
        /// the starting point.
        /// </summary>
        public const string RepairInstruction =
            "## Fuzzy match repair (this request)\n"
            + "This request is a repair of a translation memory match, not a fresh translation. It contains one "
            + "segment and, under TRANSLATION MEMORY MATCHES, an earlier translation of a similar sentence "
            + "(\"translation in memory\") with the sentence it was made for (\"source in memory\").\n"
            + "- Start from the translation in memory. Compare \"this segment\" with \"source in memory\" and find "
            + "every difference between them.\n"
            + "- Change the translation only where those differences require it, and then make whatever further "
            + "changes the sentence needs to be a correct, grammatical translation of this segment (agreement, "
            + "word order, articles).\n"
            + "- Keep everything else exactly as it is in the translation in memory: wording, terminology, "
            + "spelling and punctuation. Do not improve or restyle parts the differences do not touch, even if "
            + "you would have translated them differently.\n"
            + "- Where the termbase or your instructions require a particular term for something the "
            + "differences touch, follow them.\n"
            + "- The translation in memory carries no formatting tags. Put this segment's tags into the repaired "
            + "translation as you would in any translation.\n"
            + "- For this request, this replaces the note that memory matches are reference only. Return the "
            + "repaired translation in the usual output format.";

        /// <summary>One segment near the active one: its number in Studio, its source, and its translation if it has one.</summary>
        public sealed class Neighbour
        {
            public string Number, Source, Target;
        }

        /// <summary>
        /// The segments around the active one, as a section of the user prompt;
        /// null when there are none (a one-segment document). Plain text, no tag
        /// markers: the model must not carry a neighbour's tags into the repair.
        /// A neighbour's translation is shown where it has one, so that what the
        /// repair changes agrees with the sentences beside it.
        /// </summary>
        public static string NeighboursBlock(IList<Neighbour> before, IList<Neighbour> after)
        {
            bool any = (before != null && before.Count > 0) || (after != null && after.Count > 0);
            if (!any) return null;

            var sb = new StringBuilder();
            sb.Append("## Surrounding segments (context only)\n");
            sb.Append("The segments just before and after this segment in the document, with their current "
                + "translations where they have one (these may be unrevised drafts). They are context for the "
                + "repair only: do not translate, repair or return them, and do not copy text from them that this "
                + "segment's source does not call for.\n");
            Append(sb, "Before this segment:", before);
            Append(sb, "After this segment:", after);
            return sb.ToString().TrimEnd('\n');
        }

        /// <summary>
        /// The same segments without the heading and the sentence written for a
        /// repair, for a request that introduces them in its own words (Proofread
        /// active segment). Null when there are none.
        /// </summary>
        public static string NeighbourLines(IList<Neighbour> before, IList<Neighbour> after)
        {
            var sb = new StringBuilder();
            Append(sb, "Before this segment:", before);
            Append(sb, "After this segment:", after);
            return sb.Length == 0 ? null : sb.ToString().TrimEnd('\n');
        }

        private static void Append(StringBuilder sb, string heading, IList<Neighbour> list)
        {
            if (list == null || list.Count == 0) return;
            sb.Append(heading).Append('\n');
            foreach (var n in list)
            {
                var source = Plain(n?.Source);
                if (source.Length == 0) continue;
                sb.Append("- segment ").Append(string.IsNullOrWhiteSpace(n.Number) ? "?" : n.Number.Trim())
                    .Append(" source: ").Append(source).Append('\n');
                var target = Plain(n.Target);
                if (target.Length > 0) sb.Append("  translation: ").Append(target).Append('\n');
            }
        }

        /// <summary>
        /// What opens the user prompt of a repair: the repair instruction, then
        /// the surrounding segments. The translator's own prompt is not part of
        /// it: that stays where Alt+T puts it, in the system prompt.
        /// </summary>
        public static string RepairLead(string neighboursBlock = null) =>
            string.IsNullOrWhiteSpace(neighboursBlock)
                ? RepairInstruction
                : RepairInstruction + "\n\n" + neighboursBlock.Trim();

        /// <summary>The lowest match the setting allows, as Batch Translate reads it: 70 when the stored value is unusable.</summary>
        public static int MinScore(int stored) => stored < 1 || stored > 100 ? 70 : stored;

        public enum MatchVerdict { Repairable, NoMatch, Exact }

        /// <summary>
        /// Whether a memory match can be repaired. A match without both its source
        /// and its target is no match: the model must be able to see what differs.
        /// Anything below 100 is repairable - also the 99 the search gives a
        /// sentence that differs from the segment only in its numbers.
        /// </summary>
        public static MatchVerdict JudgeMatch(string matchSource, string matchTarget, int score, int minScore)
        {
            if (string.IsNullOrWhiteSpace(matchSource) || string.IsNullOrWhiteSpace(matchTarget) || score < minScore)
                return MatchVerdict.NoMatch;
            return score >= BatchTranslator.ExactMatch ? MatchVerdict.Exact : MatchVerdict.Repairable;
        }

        /// <summary>What to tell the translator when there is nothing to repair; null when there is.</summary>
        public static string MatchProblem(MatchVerdict verdict, int minScore, string tmName)
        {
            switch (verdict)
            {
                case MatchVerdict.NoMatch:
                    return "No translation memory that this project looks things up in has a match of " + minScore
                        + "% or higher for this segment, so there is nothing to repair.\n\n"
                        + "The lowest match is set under Settings → AI Settings (\"Lowest TM match to send\").";
                case MatchVerdict.Exact:
                    return "The best match for this segment is a 100% match"
                        + (string.IsNullOrWhiteSpace(tmName) ? "" : " (" + tmName + ")")
                        + ", so there is nothing to repair.";
                default:
                    return null;
            }
        }

        /// <summary>
        /// What to tell the translator when the project has no memory to search:
        /// none at all, or none that is switched on with Lookup ticked.
        /// </summary>
        public static string NoMemoryProblem(int total)
        {
            if (total <= 0)
                return "Supervertaler found no translation memory in this project to take a fuzzy match from.";
            return (total == 1
                    ? "This project's translation memory is switched off or has Lookup unticked"
                    : "All " + total + " of this project's translation memories are switched off or have Lookup unticked")
                + " in Project Settings, so there is nothing to take a fuzzy match from.";
        }

        /// <summary>
        /// What to tell the translator when the repair reads exactly like the
        /// translation already in the segment (the segment held the repaired text
        /// already): nothing is written, and the status is left as it was. Before
        /// 20.209 this was silent - with Track Changes on the write was skipped and
        /// nothing said; with it off the same text was written and the segment
        /// became a draft.
        /// </summary>
        public static string AlreadyInTarget(string segmentLabel, string comment) =>
            "The repaired translation reads exactly like the one already in " + segmentLabel
            + ", so nothing was written and its status was left as it was."
            + (string.IsNullOrWhiteSpace(comment) ? "" : "\n\nThe AI's note: " + comment.Trim());

        public enum ReplyVerdict { Repaired, Empty, Unchanged, TagsDiffer }

        /// <summary>
        /// Whether the AI's reply can be offered. <paramref name="reply"/> and
        /// <paramref name="segmentSource"/> are in the plugin's tag notation; the
        /// match's target is plain text. Tags are compared as a set: a repair may
        /// move a tag, but must carry exactly the segment's tags.
        /// </summary>
        public static ReplyVerdict JudgeReply(string reply, string matchTarget, string segmentSource, out string detail)
        {
            detail = null;
            var plain = Plain(reply);
            if (plain.Length == 0) return ReplyVerdict.Empty;

            var want = AutoTagger.ExtractTags(segmentSource ?? "");
            var have = AutoTagger.ExtractTags(reply ?? "");
            want.Sort(StringComparer.Ordinal);
            have.Sort(StringComparer.Ordinal);
            if (!want.SequenceEqual(have, StringComparer.Ordinal))
            {
                detail = "the segment has " + TagCount(want.Count) + " and the repaired translation has "
                    + TagCount(have.Count) + (want.Count == have.Count ? ", but not the same ones" : "");
                return ReplyVerdict.TagsDiffer;
            }

            return plain == Plain(matchTarget) ? ReplyVerdict.Unchanged : ReplyVerdict.Repaired;
        }

        private static string TagCount(int n) => n == 1 ? "1 tag marker" : n + " tag markers";

        private static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.Compiled);

        /// <summary>Text without tag markers, spaces folded: what the translator reads, and what is compared.</summary>
        public static string Plain(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return Spaces.Replace(SegmentTagHandler.StripTagPlaceholders(text), " ").Trim();
        }

        /// <summary>What the repair changed in the match's translation, word by word, for the preview.</summary>
        public static List<TokenDiff.Piece> Changes(string matchTarget, string reply)
        {
            return TokenDiff.Diff(Plain(matchTarget), Plain(reply));
        }
    }
}
