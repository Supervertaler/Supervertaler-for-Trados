using System;
using System.Collections.Generic;
using System.Linq;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// The checks behind the MCP merge_segments and split_segment tools, on plain
    /// data so they can be tested without Studio. Everything here decides whether
    /// to go ahead; <see cref="StudioSegmentCommands"/> then drives Studio's own
    /// Merge/Split commands.
    ///
    /// <para>Studio's commands stop and ask (a modal Yes/No, or a warning) when a
    /// segment is confirmed, when a "Group" tag is involved, or when two segments
    /// share a tag id. A modal on the UI thread would hold the MCP call until
    /// someone clicks, so each of those is refused here first, with the reason.
    /// The conditions are copied from Studio 2026's MergeSegmentsAction and
    /// SplitSegmentsAction (decompiled 26 Nov 2026; the 2026 beta is identical).</para>
    /// </summary>
    internal static class SegmentStructurePlanner
    {
        /// <summary>Statuses Studio counts as confirmed, or later: merging or
        /// splitting them makes Studio ask, and undoes work someone signed off.</summary>
        private static readonly HashSet<string> ConfirmedStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Translated", "ApprovedTranslation", "ApprovedSignOff"
        };

        internal sealed class MergeCandidate
        {
            public string Id;
            public string ParagraphId;
            public string Status;
            public bool Locked;
            /// <summary>Ids of the tags in the segment's TARGET (what Studio checks).</summary>
            public IList<string> TargetTagIds = new List<string>();
            /// <summary>The target holds a tag whose TagType metadata is "Group".</summary>
            public bool HasGroupTag;
        }

        /// <summary>Null when these segments may be merged (still subject to Studio's
        /// own check that they are next to each other), otherwise why not.</summary>
        internal static string CheckMerge(IList<MergeCandidate> segments)
        {
            if (segments == null || segments.Count < 2)
                return "give at least two segment ids to merge";

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in segments)
                if (!seen.Add(s.Id))
                    return $"segment {s.Id} is listed twice";

            var paragraph = segments[0].ParagraphId;
            if (segments.Any(s => !string.Equals(s.ParagraphId, paragraph, StringComparison.Ordinal)))
                return "these segments are in different paragraphs. Only segments of one paragraph can be merged: " +
                       "a paragraph break in the source is part of the document's structure";

            foreach (var s in segments)
            {
                if (s.Locked)
                    return $"segment {s.Id} is locked. Unlock it first if it really should be merged";
                if (s.Status != null && ConfirmedStatuses.Contains(s.Status))
                    return $"segment {s.Id} is confirmed ({s.Status}). Studio would stop to ask before merging it. " +
                           "If it should be merged anyway, set it back to Draft first (update_segments, status Draft)";
            }

            if (segments[0].HasGroupTag)
                return "the first segment contains a group tag, which Studio does not allow in a merge";

            var earlier = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in segments)
            {
                var ids = (s.TargetTagIds ?? new List<string>()).Where(t => t != null).ToList();
                if (ids.Any(earlier.Contains))
                    return "two of these segments carry the same tag id in their targets. Studio would stop to ask " +
                           "before merging them; merge them in Studio by hand, or fix the target tags first";
                foreach (var t in ids) earlier.Add(t);
            }
            return null;
        }

        /// <summary>A stretch of source text as the editor holds it, in document order.</summary>
        internal sealed class TextRun
        {
            public string Text = "";
            /// <summary>Identities of the tag pairs around this text, outermost first.
            /// Two runs share a tag pair when these lists share an entry.</summary>
            public IList<int> TagPairs = new List<int>();
            public bool Locked;
            public bool InRevision;
        }

        internal sealed class SplitPoint
        {
            public int RunIndex;
            public int Offset;
            public string Error;
            /// <summary>A heads-up for the result (not a refusal), e.g. a tag next to the split.</summary>
            public string Note;
            public string FirstPart;
            public string SecondPart;
        }

        /// <summary>
        /// Where the source must be split so the second segment starts with
        /// <paramref name="splitBefore"/>. The text must occur exactly once in the
        /// source's plain text (tags removed). The split lands on the first
        /// non-space character of the second part, which is where Studio's own
        /// Split expects the cursor; Studio then trims the spaces between the parts.
        /// </summary>
        internal static SplitPoint LocateSplit(IList<TextRun> runs, string splitBefore, bool allowInsideTags)
        {
            var result = new SplitPoint();
            if (string.IsNullOrWhiteSpace(splitBefore))
            {
                result.Error = "say where to split: 'splitBefore' is the text the second segment starts with";
                return result;
            }
            runs = runs ?? new List<TextRun>();
            var plain = string.Concat(runs.Select(r => r.Text ?? ""));
            var needle = splitBefore.Trim();

            int at = plain.IndexOf(needle, StringComparison.Ordinal);
            if (at < 0)
            {
                result.Error = $"'{needle}' does not occur in the source text. Give plain text from the source, without tag " +
                               $"markers. The source reads: \"{plain}\"";
                return result;
            }
            if (plain.IndexOf(needle, at + 1, StringComparison.Ordinal) >= 0)
            {
                result.Error = $"'{needle}' occurs more than once in the source. Give a longer piece of text, so the " +
                               "split point is unambiguous";
                return result;
            }

            int last = at - 1;
            while (last >= 0 && char.IsWhiteSpace(plain[last])) last--;
            if (last < 0)
            {
                result.Error = "that is where the segment starts already: splitting there would leave the first part empty";
                return result;
            }

            int prevRun, prevOffset, nextRun, nextOffset;
            Locate(runs, last, out prevRun, out prevOffset);
            Locate(runs, at, out nextRun, out nextOffset);
            var prev = runs[prevRun];
            var next = runs[nextRun];

            if (prev.Locked || next.Locked)
            {
                result.Error = "the split point is in locked content, which cannot be split";
                return result;
            }
            if (prev.InRevision || next.InRevision)
            {
                result.Error = "the split point is inside a tracked change. Accept or reject the change first";
                return result;
            }

            var shared = (prev.TagPairs ?? new List<int>()).Intersect(next.TagPairs ?? new List<int>()).Any();
            if (shared && !allowInsideTags)
            {
                result.Error = "the split point is inside a tag pair (formatting or a link that runs across it). " +
                               "Splitting there cuts the tag pair in two. Split elsewhere, or send allowInsideTags: true " +
                               "if the user wants it split there anyway";
                return result;
            }

            result.RunIndex = nextRun;
            result.Offset = nextOffset;
            result.FirstPart = plain.Substring(0, last + 1);
            result.SecondPart = plain.Substring(at);
            if (shared)
                result.Note = "The split cuts a tag pair in two, as you asked: Studio closes it in the first segment " +
                              "and opens it again in the second. Check the tags of both.";
            else if ((prev.TagPairs?.Count ?? 0) > 0 || (next.TagPairs?.Count ?? 0) > 0)
                result.Note = "A tag pair ends or starts at the split. Check that the tags landed in the right segment.";
            return result;
        }

        /// <summary>
        /// The index of the row that holds a segment: <paramref name="start"/> when
        /// it matches, else the nearest match within a few rows either side (the
        /// editor's guess can be the hidden structure row next to the segment), else
        /// the first match anywhere; -1 when none does. <paramref name="start"/> may
        /// be -1 (no guess) or out of range.
        /// </summary>
        internal static int NearestMatch(int count, int start, Func<int, bool> isMatch)
        {
            const int Near = 4;
            if (start >= 0 && start < count)
            {
                if (isMatch(start)) return start;
                for (int d = 1; d <= Near; d++)
                {
                    if (start + d < count && isMatch(start + d)) return start + d;
                    if (start - d >= 0 && isMatch(start - d)) return start - d;
                }
            }
            for (int i = 0; i < count; i++)
                if (isMatch(i)) return i;
            return -1;
        }

        private static void Locate(IList<TextRun> runs, int charIndex, out int run, out int offset)
        {
            int start = 0;
            for (int i = 0; i < runs.Count; i++)
            {
                var len = (runs[i].Text ?? "").Length;
                if (charIndex < start + len)
                {
                    run = i;
                    offset = charIndex - start;
                    return;
                }
                start += len;
            }
            // Not reachable for an index inside the concatenated text.
            run = runs.Count - 1;
            offset = Math.Max(0, (runs[run].Text ?? "").Length - 1);
        }
    }
}
