using System;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Sdl.TranslationStudioAutomation.IntegrationApi;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// #138: a write made with Track Changes on, recorded as word-level
    /// revisions instead of the whole-segment change Studio records for any
    /// <c>ProcessSegmentPair</c> edit. The one implementation behind
    /// update_segments and Apply on the Reports tab (through
    /// AiAssistantViewPart.WriteSegmentTarget), and behind the MCP server's
    /// find_and_replace and SuperSearch's Replace (through
    /// <see cref="SegmentWriteCheck.ProcessChecked"/>). Until 201 it lived in
    /// AiAssistantViewPart, so the two replace features edited the text nodes in
    /// place and let Studio track that: a one-word replace came out as the whole
    /// sentence struck through and retyped (seen live on 19 Oct 2026).
    ///
    /// <para>From 208 a target that already carries tracked changes is recorded
    /// word by word as well (<see cref="TrackedTargetMerge"/>): a second write
    /// to a segment adds revisions only for the words that differ from the text
    /// as it reads now. Seen on a post-editing job on 16 Nov 2026, where every
    /// segment written twice came out struck through whole.</para>
    /// </summary>
    internal sealed class TrackedWrite
    {
        /// <summary>"word" (word-level revisions, possibly none), "tag-only"
        /// (only tags changed, so no revision at all) or "whole-segment".</summary>
        public string RevisionMode;

        /// <summary>Why the write was recorded as one whole-segment change;
        /// null when it was not.</summary>
        public string Note;

        /// <summary>Tags replaced without a revision to line the two targets up.</summary>
        public int TagsReplaced;

        /// <summary>
        /// Replaces the freshly built <paramref name="target"/> with the same
        /// text expressed as word-level revisions against
        /// <paramref name="targetBefore"/>, and switches Track Changes off so
        /// Studio applies them as they are instead of wrapping them in its own
        /// whole-segment revision. Call inside the <c>ProcessSegmentPair</c>
        /// callback, once the new target is built.
        ///
        /// <para>Returns the suspension the caller must dispose the moment the
        /// write has been applied. Returns null, with a reason in
        /// <paramref name="note"/>, whenever it declines - and in that case it has
        /// changed nothing: the target is still exactly what the ordinary write
        /// built, and tracking is still on.</para>
        ///
        /// <para>The order is deliberate. The merge is built and self-checked on a
        /// detached copy first; tracking is switched off only once there is a
        /// verified result to write; and the target is replaced only once tracking
        /// is known to be off. No failure along the way can leave a text change
        /// untracked. Tags the merge had to replace to line the two targets up
        /// are the one deliberate exception, counted in
        /// <paramref name="tagsReplaced"/>.</para>
        ///
        /// <para><paramref name="revisionMode"/> says how the write was recorded:
        /// "word" (word-level revisions, possibly none), "tag-only" (only tags
        /// changed, so no revision at all) or "whole-segment".</para>
        /// </summary>
        /// <param name="targetBefore">A clone of the target as it was, taken
        /// before the write changed it. It becomes the merged target.</param>
        /// <param name="tagRevisionsBefore"><see cref="TrackedTargetMerge.HasTagRevisions"/>
        /// of the LIVE target before the write: the clone has lost those. Revisions
        /// around TEXT survive the clone, and the merge works around them.</param>
        public static TrackingSuspension Rewrite(IStudioDocument doc,
            ISegment target, ISegment targetBefore, bool tagRevisionsBefore,
            out string note, out string revisionMode, out int tagsReplaced)
        {
            note = null;
            revisionMode = "whole-segment";
            tagsReplaced = 0;
            if (tagRevisionsBefore)
            {
                // A revision on a tag itself, which Clone() has already dropped from
                // targetBefore: the merge could not see it, let alone keep it.
                note = "tracked as a whole-segment change (a tag in the segment carries a tracked change of its own).";
                return null;
            }

            var factory = SegmentTagHandler.CreateDocumentItemFactory();
            var props = factory?.PropertiesFactory;
            if (props == null)
            {
                note = "tracked as a whole-segment change (revision markers could not be created).";
                return null;
            }

            // One insertion and one deletion per segment, shared by every marker
            // in it - the shape Studio documents for one edit spanning several
            // markers, and the shape the issue's reference output used.
            var author = TrackingSuspension.EditingUser(doc);
            var now = DateTime.Now;
            var ins = props.CreateRevisionProperties(RevisionType.Insert);
            ins.Author = author;
            ins.Date = now;
            var del = props.CreateRevisionProperties(RevisionType.Delete);
            del.Author = author;
            del.Date = now;

            var refusal = TrackedTargetMerge.TryMerge(targetBefore, target, factory, ins, del,
                out var replaced, out var textRevised);
            if (refusal != null)
            {
                note = "tracked as a whole-segment change (" + refusal + ").";
                return null;
            }

            var suspension = TrackingSuspension.TurnOff(doc);
            if (suspension == null)
            {
                note = "tracked as a whole-segment change (Track Changes could not be switched for the write).";
                return null;
            }

            // From here the suspension exists but the caller does not have it yet,
            // so a throw would lose it and leave Track Changes off for the rest of
            // the session. Restore it here, then rethrow rather than return: if the
            // clear succeeded and the move did not, the target is half-replaced,
            // and throwing out of the callback is what stops Studio writing it.
            try
            {
                target.Clear();
                targetBefore.MoveAllItemsTo(target);
            }
            catch
            {
                suspension.Dispose();
                throw;
            }
            revisionMode = !textRevised && replaced > 0 ? "tag-only" : "word";
            tagsReplaced = replaced;
            return suspension;
        }
    }
}
