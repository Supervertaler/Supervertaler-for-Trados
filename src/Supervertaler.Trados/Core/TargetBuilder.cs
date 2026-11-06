using System;
using System.Collections.Generic;
using Sdl.FileTypeSupport.Framework.BilingualApi;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Builds a segment's new target, for the writes that replace a whole
    /// target (update_segments, Apply on the Reports tab). Pure functions over
    /// segments, kept out of the view part so that update_segments can build a
    /// write on a detached copy first - to refuse it before anything in the
    /// document changes - and so they can be checked offline
    /// (.dev/mcp-tag-only-test.ps1).
    ///
    /// <para>Two ways in: <see cref="FromText"/> rebuilds the target from a
    /// translation in tag markers, cloning the SOURCE's tags; <see cref="CopySource"/>
    /// clones the source's content as it is, which is what Studio's Copy Source
    /// to Target (Ctrl+Insert) does. Both keep the target's comments, as every
    /// write path does (<see cref="SegmentTagHandler.CaptureCommentMarkers"/>).</para>
    ///
    /// <para>Why CopySource exists (4 Nov 2026, a real job): a segment holding
    /// only tags - email links, a field, an image - could not be filled through
    /// update_segments at all. get_segments did not list it; a write of its
    /// markers built nothing, because new text is modelled on a text node of the
    /// source and it has none; and the write was still reported as done, with a
    /// tag warning. The agent's tags were blamed. Copying the source needs no
    /// markers, so it cannot get them wrong.</para>
    /// </summary>
    internal static class TargetBuilder
    {
        /// <summary>
        /// Rebuilds <paramref name="target"/> from <paramref name="targetText"/>
        /// - a translation in &lt;tN&gt; markers (and, with
        /// <paramref name="semanticTagNames"/>, &lt;b&gt;/&lt;i&gt;/&lt;u&gt;) -
        /// cloning each marker's tag from the SOURCE. A marker the source does
        /// not have, or one used twice, loses its tag and keeps its text. When
        /// the markers cannot be placed, the text is written plain.
        ///
        /// <para>Returns false, with the target left exactly as it was, when
        /// nothing can be built: the source has no text node to model new text on
        /// (a segment of tags only) and the translation needs text, or holds no
        /// tag the source has. The caller must then not report a write.</para>
        /// </summary>
        /// <param name="removedMarkers">When given, receives every marker taken
        /// out of <paramref name="targetText"/> rather than written (see
        /// <see cref="RemoveUnknownMarkers"/>), as written, so the caller can
        /// refuse or warn. Only with <paramref name="semanticTagNames"/>: without
        /// it the text can only hold &lt;tN&gt;, and a "&lt;b&gt;" in it is text
        /// (Proofread apply) and stays text.</param>
        public static bool FromText(ISegment target, ISegment source, string targetText, bool semanticTagNames,
            List<string> removedMarkers = null)
        {
            if (target == null || source == null || targetText == null) return false;

            if (targetText.Length == 0)
            {
                // A deliberate blank (update_segments' allowEmpty): what the
                // plain-text branch below leaves, without needing a text node to
                // model on - so a segment of tags only can be blanked too.
                var kept = SegmentTagHandler.CaptureCommentMarkers(target);
                target.Clear();
                SegmentTagHandler.OpenCommentMarkers(target, kept);
                return true;
            }

            // Source-authoritative, always: Studio's Tag Verifier compares the
            // target's tag ids with the SOURCE's. See WriteSegmentTarget.
            var sourceSer = SegmentTagHandler.Serialize(source);
            var tagMap = sourceSer.TagMap ?? new Dictionary<int, TagInfo>();

            var resolved = targetText;
            if (semanticTagNames)
            {
                resolved = Export.BilingualTagNamer.ResolveSemanticNames(targetText, tagMap, removedMarkers);
                // Any marker still left that is not one of ours is taken out, so
                // it never reaches the target as text. See RemoveUnknownMarkers.
                resolved = RemoveUnknownMarkers(resolved,
                    SegmentTagHandler.StripTagPlaceholders(sourceSer.SerializedText ?? ""), removedMarkers);
            }

            bool hasAnyMarker = tagMap.Count > 0
                || resolved.IndexOf("<t", StringComparison.Ordinal) >= 0;
            if (hasAnyMarker && SegmentTagHandler.ReconstructTarget(target, source, resolved, tagMap))
                return true;

            // Semantic markers are already resolved to <tN> or removed above;
            // without semanticTagNames there are none to remove.
            var plain = SegmentTagHandler.StripTagPlaceholders(resolved);

            var textTpl = SegmentTagHandler.FindFirstText(source);
            if (textTpl == null) return false;

            var keptComments = SegmentTagHandler.CaptureCommentMarkers(target);
            target.Clear();
            var dest = SegmentTagHandler.OpenCommentMarkers(target, keptComments);
            SegmentTagHandler.AppendText(dest, plain, source, tagMap, textTpl);
            return true;
        }

        // A tag-like marker: <name>, </name>, <name/>, optionally with
        // name="value" attributes (<cf bold="True">). Deliberately strict, so
        // ordinary text is never taken for one: '<' must be followed straight
        // away by a letter (or '/' and a letter), and anything between the name
        // and '>' must be attributes with a value. "a < b", "a<b", "x < y > z" and
        // "if a<b and c>d" do not match.
        private static readonly System.Text.RegularExpressions.Regex MarkerRe =
            new System.Text.RegularExpressions.Regex(
                @"</?[A-Za-z][A-Za-z0-9_.:-]*(?:\s+[A-Za-z_:][A-Za-z0-9_.:-]*\s*=\s*(?:""[^""<>]*""|'[^'<>]*'|[^\s""'<>/=]+))*\s*(?:/\s*)?>",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // Our own numbered markers, in the tolerant forms SegmentTagHandler
        // reads (<t1>, </t1>, <t2/>, <T1 />).
        private static readonly System.Text.RegularExpressions.Regex NumberedMarkerRe =
            new System.Text.RegularExpressions.Regex(@"^<\s*/?\s*t\s*\d+\s*/?\s*>$",
                System.Text.RegularExpressions.RegexOptions.Compiled
                | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>
        /// Removes every tag-like marker from <paramref name="text"/> that is not
        /// one of our numbered &lt;tN&gt; markers - after
        /// <see cref="Export.BilingualTagNamer.ResolveSemanticNames"/> has turned
        /// the &lt;b&gt;/&lt;i&gt;/&lt;u&gt;/&lt;bi&gt; it could bind into those.
        /// What is left is a marker this tool does not understand: a &lt;u&gt;
        /// with no underline tag in the source to bind to, &lt;br/&gt;, &lt;x&gt;,
        /// Studio's own &lt;cf bold="True"&gt;. Until 206 such a marker went into
        /// the target as literal text (6 Nov 2026: "&lt;u/&gt;" in a target).
        ///
        /// <para>Removed the way an invented &lt;t99&gt; is: the marker goes, the
        /// text around and inside it stays - but never silently: each one goes into
        /// <paramref name="removed"/>, and update_segments refuses the write
        /// without an fp and warns with one (<see cref="Preflight"/>,
        /// <see cref="DescribeRemovedMarkers"/>). When it stood for one of the source's
        /// tags, that tag is then missing from the target, which update_segments
        /// treats as the tag mismatch it is (refused without an fp, written with
        /// the tag-id warning with one).</para>
        ///
        /// <para>Kept: a marker that appears as TEXT in the source
        /// (<paramref name="sourceText"/>, the source with our markers removed) -
        /// a software string "Press &lt;Enter&gt;" or a page about HTML translates
        /// with that text in it.</para>
        /// </summary>
        internal static string RemoveUnknownMarkers(string text, string sourceText, List<string> removed = null)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('<') < 0) return text;
            return MarkerRe.Replace(text, m =>
            {
                if (NumberedMarkerRe.IsMatch(m.Value)
                    || (!string.IsNullOrEmpty(sourceText) && sourceText.IndexOf(m.Value, StringComparison.Ordinal) >= 0))
                    return m.Value;
                removed?.Add(m.Value);
                return "";
            });
        }

        /// <summary>
        /// The removed markers as the caller is told about them, each once, in
        /// the order written: "removed '&lt;Invoer&gt;', '&lt;u/&gt;': not a tag
        /// in this segment's source". Null when nothing was removed.
        /// </summary>
        internal static string DescribeRemovedMarkers(List<string> removed)
        {
            var list = QuoteRemovedMarkers(removed);
            if (list == null) return null;
            return "removed " + list + ": " + (list.IndexOf("', '", StringComparison.Ordinal) < 0 ? "not a tag" : "not tags") +
                   " in this segment's source";
        }

        /// <summary>"'&lt;Invoer&gt;', '&lt;u/&gt;'": each removed marker once, in
        /// the order written. Null when there are none.</summary>
        private static string QuoteRemovedMarkers(List<string> removed)
        {
            if (removed == null || removed.Count == 0) return null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var shown = new List<string>();
            foreach (var r in removed) if (seen.Add(r)) shown.Add("'" + r + "'");
            return string.Join(", ", shown);
        }

        /// <summary>
        /// Replaces <paramref name="target"/>'s content with a copy of
        /// <paramref name="source"/>'s, as Studio's Copy Source to Target does:
        /// every text, tag and locked item cloned, so each tag keeps the source's
        /// own tag id and the Tag Verifier has nothing to report. The source as
        /// it reads is copied: text a tracked change deleted from the source is
        /// left out, and a source comment is not carried into the target (its
        /// text is). The target's own comments are kept.
        ///
        /// <para>Returns false, with the target left as it was, when the source
        /// holds nothing to copy - copying it would only blank the target.</para>
        /// </summary>
        public static bool CopySource(ISegment target, ISegment source)
        {
            if (target == null || source == null || !HasContent(source)) return false;

            var keptComments = SegmentTagHandler.CaptureCommentMarkers(target);
            target.Clear();
            var dest = SegmentTagHandler.OpenCommentMarkers(target, keptComments);
            CopyInto(source, dest);
            return true;
        }

        /// <summary>
        /// update_segments' check before it touches a segment: builds the write
        /// on a detached copy of <paramref name="target"/> and returns why it must
        /// be refused, or null to go ahead. Nothing in the document changes.
        ///
        /// <para>Refused: a write that cannot be built at all, and - with
        /// <paramref name="requireMatchingTags"/> (a target sent with no fp and no
        /// force) - a text write whose tags would not match the source's, so a
        /// guessed tag never lands. <paramref name="tagMismatch"/> says which.
        /// A copy of the source always matches it, and a deliberate blank guesses
        /// no tags, so neither is checked.</para>
        /// </summary>
        public static string Preflight(ISegment source, ISegment target, string targetText,
            bool copySource, bool requireMatchingTags, out bool tagMismatch)
        {
            tagMismatch = false;
            var probe = target?.Clone() as ISegment;
            if (probe == null || source == null)
                return "could not read this segment to prepare the write – nothing written";

            if (copySource)
                return CopySource(probe, source)
                    ? null
                    : "this segment's source is empty (no text and no tags), so there is nothing to copy – " +
                      "nothing written";

            var removed = new List<string>();
            if (!FromText(probe, source, targetText, semanticTagNames: true, removedMarkers: removed))
                return SegmentTagHandler.Serialize(source).TagMap.Count == 0
                    ? "this segment's source is empty (no text and no tags): there is nothing to translate – " +
                      "nothing written"
                    : "this segment's source holds only tags and no text, so the target you sent cannot be " +
                      "built: it needs text the source has nothing to model on, or uses none of the source's " +
                      "tags. Nothing was written. Send copySource: true instead – a copy of the source, tags and " +
                      "all, as Ctrl+Insert does in Studio";

            // A deliberate blank (allowEmpty) is not a guess at the tags: it is
            // written, and its missing tags reported, as before.
            if (!requireMatchingTags || targetText.Length == 0) return null;
            var mismatch = TagIdAudit.DescribeListMismatch(
                TagIdAudit.CollectTagIds(source), TagIdAudit.CollectTagIds(probe));
            // A marker that would be removed is refused like a tag mismatch, even
            // when every source tag is still there: "<Invoer>" for a key name is
            // text the caller meant, and removing it unannounced loses it.
            var removedList = QuoteRemovedMarkers(removed);
            if (mismatch == null && removedList == null) return null;

            tagMismatch = true;
            var problems = new List<string>();
            if (removedList != null)
                problems.Add("holds " + (removedList.IndexOf("', '", StringComparison.Ordinal) < 0 ? "a marker that is not a tag" : "markers that are not tags") +
                             " in this segment's source (" + removedList + "), which would be removed, not written");
            if (mismatch != null)
                problems.Add("would leave this segment's tags not matching its source (" + mismatch + ")");
            return "refused, nothing written: the target you sent " + string.Join(", and ", problems) +
                   "; it came without an fp, so there is no sign its source was read. Read the segment with " +
                   "get_segments and copy the tag markers from its SOURCE field, sending its fp; for a segment of " +
                   "tags only (tagOnly: true) send copySource: true instead of a target. Only if you mean the " +
                   "target to differ from the source's tags, re-send with force: true (a marker that is not a tag " +
                   "in the source is then still left out, and the result says so)";
        }

        /// <summary>True when <paramref name="container"/> holds anything that
        /// would be copied: text (even a space), a tag, locked content.</summary>
        private static bool HasContent(IAbstractMarkupDataContainer container)
        {
            foreach (var item in container)
            {
                if (item is IRevisionMarker rev)
                {
                    if (rev.Properties?.RevisionType != RevisionType.Delete && HasContent(rev)) return true;
                }
                else if (item is ICommentMarker comment)
                {
                    if (HasContent(comment)) return true;
                }
                else if (item is IText text)
                {
                    if (!string.IsNullOrEmpty(text.Properties?.Text)) return true;
                }
                else return true;
            }
            return false;
        }

        private static void CopyInto(IAbstractMarkupDataContainer from, IAbstractMarkupDataContainer to)
        {
            foreach (var item in from)
            {
                if (item is IRevisionMarker rev)
                {
                    // The source as it reads: a deletion is gone, an insertion is
                    // simply text. A revision marker is not something to copy.
                    if (rev.Properties?.RevisionType != RevisionType.Delete) CopyInto(rev, to);
                }
                else if (item is ICommentMarker comment)
                {
                    CopyInto(comment, to);
                }
                else if (item is ITagPair pair)
                {
                    // Rebuilt rather than cloned whole, so a revision or comment
                    // inside the pair is treated as above.
                    var clone = (ITagPair)pair.Clone();
                    clone.Clear();
                    CopyInto(pair, clone);
                    to.Add(clone);
                }
                else
                {
                    to.Add((IAbstractMarkupData)item.Clone());
                }
            }
        }
    }
}
