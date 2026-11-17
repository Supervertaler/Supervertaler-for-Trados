using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using Sdl.Core.Globalization;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Sdl.TranslationStudioAutomation.IntegrationApi;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Whether an edit made through <see cref="IStudioDocument.ProcessSegmentPair"/>
    /// actually landed. Studio's call says nothing when it does not: with Track
    /// Changes and TQA mode both off, the editor protects tracked changes nobody
    /// has accepted or rejected (FrameworkEditControl's revision protection) and
    /// quietly declines an edit that would replace them, from a plugin as from
    /// the keyboard. Found 18 Oct 2026, when update_segments answered ok:true for
    /// four segments that stayed exactly as they were. Michael chose to respect
    /// the protection and say why, rather than lift it and drop changes nobody
    /// has reviewed, and to give every feature that writes a segment the same
    /// check.
    ///
    /// <para>The check reads the segment back from the document and compares
    /// its accepted view - the text as Accept All would leave it, tags included
    /// (<see cref="TrackedTargetMerge.Project"/>) - with the target the edit
    /// built. An edit that changed no text is not checked; nor is a segment that
    /// cannot be read back. One paragraph's pairs are copied per check.</para>
    /// </summary>
    internal static class SegmentWriteCheck
    {
        /// <summary>
        /// <see cref="IStudioDocument.ProcessSegmentPair"/>, then the check: throws
        /// <see cref="SegmentWriteDeclinedException"/> with the reason when Studio
        /// did not apply the edit, so each caller's own failure handling reports
        /// it. An edit that changed the text leaves the segment labelled as edited
        /// by hand (<see cref="SegmentOrigin"/>), and <paramref name="pair"/>
        /// carrying that label. UI thread.
        /// </summary>
        /// <param name="comments">Compare the target's comments too: for an edit
        /// that changes a comment and not the text, which the text alone would
        /// show as nothing to check.</param>
        /// <param name="wordLevel">With Track Changes on, record the edit as
        /// word-level revisions against the target it replaces
        /// (<see cref="TrackedWrite.Rewrite"/>, as update_segments does) rather
        /// than as the whole-segment change Studio records for any edit made
        /// here. Track Changes off: no difference.</param>
        /// <param name="overTextOnly">With <paramref name="wordLevel"/>: record word
        /// by word only when the target holds text before the edit. A write into
        /// an empty target is one insertion whichever way it is recorded, so it is
        /// left to Studio and Track Changes is not switched off and on around it:
        /// what a translation run of thousands of empty segments needs.</param>
        /// <param name="faultAfterWrite">Told when Studio threw out of
        /// <c>ProcessSegmentPair</c> although the edit is in the document (see
        /// below), so the caller can say so to the user. The write then counts
        /// as made and nothing is thrown.</param>
        /// <returns>With <paramref name="wordLevel"/> and Track Changes on, how
        /// the edit was recorded; otherwise null.</returns>
        /// <remarks>
        /// Studio can throw AFTER it has taken an edit: seen 14 Nov 2026 as
        /// "Attempted to read or write protected memory" on long segments in
        /// Batch Translate, in Studio 2024 and 2026, with the target complete in
        /// the document each time. (Studio runs with
        /// legacyCorruptedStateExceptionsPolicy, so an access violation in its
        /// native code reaches a plugin as an ordinary exception.) Such a write
        /// is not reported as failed: when the edit ran to its end, changed the
        /// text, and the document reads back as the edit built it, the fault is
        /// logged with its stack trace and the write stands. When the document
        /// does not read back that way, or cannot be read, the exception goes on
        /// to the caller as before.
        /// </remarks>
        public static TrackedWrite ProcessChecked(IStudioDocument doc, ISegmentPair pair, string undoName,
            Action<ISegmentPair, CancelEventArgs> edit, bool comments = false, bool wordLevel = false,
            Action<Exception> faultAfterWrite = null, bool overTextOnly = false)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            long sincePrevious = MsSincePreviousWrite();
            ExceptionDispatchInfo fault = null;
            Func<IAbstractMarkupDataContainer, string> view = comments
                ? (Func<IAbstractMarkupDataContainer, string>)AcceptedWithComments
                : c => TrackedTargetMerge.Project(c, accept: true);
            bool tracked = TrackingSuspension.IsTracking(doc) == true;
            string before = null, after = null, shapeBefore = null, shapeAfter = null;
            // The text alone, as Accept All would leave it: what decides whether the
            // segment is labelled as edited. The view above also holds the comments.
            string textBefore = null, textAfter = null;
            var levelBefore = ConfirmationLevel.Unspecified;
            TrackedWrite record = null;
            TrackingSuspension suspension = null;

            try
            {
                doc.ProcessSegmentPair(pair, undoName, (sp, cancel) =>
                {
                    before = view(sp.Target);
                    textBefore = comments ? TrackedTargetMerge.Project(sp.Target, accept: true) : before;
                    levelBefore = sp.Properties?.ConfirmationLevel ?? ConfirmationLevel.Unspecified;
                    shapeBefore = TrackedTargetMerge.Shape(sp.Target);
                    string rejectedBefore = comments && tracked ? TrackedTargetMerge.Project(sp.Target, accept: false) : null;
                    // Both asked of the live target before the edit: Clone() drops
                    // a revision recorded on a tag itself.
                    var targetBefore = wordLevel && tracked && !(overTextOnly && string.IsNullOrEmpty(textBefore))
                        ? sp.Target.Clone() as ISegment : null;
                    bool tagRevisionsBefore = targetBefore != null && TrackedTargetMerge.HasTagRevisions(sp.Target);
                    edit(sp, cancel);
                    // An edit that leaves the target reading as it did (the AI sent a
                    // match back unchanged) is not applied at all: Studio would track
                    // the rewrite as the whole segment deleted and typed again.
                    // Translation writes only: other callers' edits may also change
                    // what this view does not show (a status).
                    if (overTextOnly && targetBefore != null && !cancel.Cancel && view(sp.Target) == before)
                        cancel.Cancel = true;
                    // Nothing to record for an edit that changed nothing or was
                    // cancelled. On a refusal the target stays as the edit left it
                    // and Studio records its whole-segment change, as before.
                    if (targetBefore != null && !cancel.Cancel && view(sp.Target) != before)
                    {
                        suspension = TrackedWrite.Rewrite(doc, sp.Target, targetBefore, tagRevisionsBefore,
                            out var note, out var mode, out var replaced);
                        record = new TrackedWrite { RevisionMode = mode, Note = note, TagsReplaced = replaced };
                    }
                    // A comment added, changed or removed is not a change to the translation.
                    // With Track Changes on, Studio records any edit made here as the whole
                    // segment replaced, so a comment edit that leaves the text as it was is
                    // applied with tracking suspended - as Studio's own comment commands are.
                    // If it cannot be suspended, the edit goes in tracked, as before.
                    if (comments && tracked && suspension == null && !cancel.Cancel && view(sp.Target) != before
                        && TrackedTargetMerge.Project(sp.Target, accept: true) == textBefore
                        && TrackedTargetMerge.Project(sp.Target, accept: false) == rejectedBefore)
                        suspension = TrackingSuspension.TurnOff(doc);
                    after = view(sp.Target);
                    textAfter = comments ? TrackedTargetMerge.Project(sp.Target, accept: true) : after;
                    shapeAfter = TrackedTargetMerge.Shape(sp.Target);
                });
            }
            // Only once the edit has run to its end and changed the text: an
            // exception from the edit itself leaves 'after' unset and is not
            // caught here, and with no change there is nothing to read back.
            catch (Exception ex) when (after != null && after != before)
            {
                fault = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                // Studio has applied the write, or the edit threw: Track Changes
                // goes back on now, before the read-back and before the caller
                // pumps messages, so nothing the translator types goes untracked.
                suspension?.Dispose();
            }

            if (after == null || after == before) return record;
            var live = ReadLivePair(doc, pair)?.Target;
            if (fault != null)
            {
                // Unlike the check below, a segment that cannot be read back is
                // NOT given the benefit of the doubt: Studio said the write failed.
                bool landed = live != null && view(live) == after;
                LogFault(doc, pair, fault.SourceException, landed, after.Length, sincePrevious);
                if (!landed) fault.Throw();
                faultAfterWrite?.Invoke(fault.SourceException);
            }
            else if (live != null && view(live) != after)
                throw new SegmentWriteDeclinedException(Reason(doc, live, shapeBefore, shapeAfter, tracked));
            // Landed, or cannot be read back (which is not held against it). A
            // comment edit leaves the text as it was, and the label with it.
            if (textAfter != textBefore) SegmentOrigin.MarkEdited(doc, pair, levelBefore);
            return record;
        }

        /// <summary>When the previous <see cref="ProcessChecked"/> began
        /// (Stopwatch ticks; 0 = none yet). For the fault line only: whether
        /// faults follow writes made in quick succession. UI thread, like every
        /// write, so no lock.</summary>
        private static long _previousWriteAt;

        private static long MsSincePreviousWrite()
        {
            long now = Stopwatch.GetTimestamp(), previous = _previousWriteAt;
            _previousWriteAt = now;
            return previous == 0 ? -1 : (now - previous) * 1000 / Stopwatch.Frequency;
        }

        /// <summary>
        /// One log entry for an exception Studio threw out of a write: what is
        /// needed to find where in Studio it comes from. Lengths, ids and the
        /// stack trace; never the text.
        /// </summary>
        private static void LogFault(IStudioDocument doc, ISegmentPair pair, Exception ex, bool landed,
            int builtLength, long sincePrevious)
        {
            string active;
            try
            {
                var a = doc.ActiveSegmentPair;
                active = a?.Properties == null ? "none"
                    : a.Properties.Id.Id == pair?.Properties?.Id.Id
                      && Equals(doc.GetParentParagraphUnit(a)?.Properties?.ParagraphUnitId,
                                doc.GetParentParagraphUnit(pair)?.Properties?.ParagraphUnitId) ? "yes" : "no";
            }
            catch (Exception readEx) { active = "unknown (" + readEx.GetType().Name + ")"; }

            DiagnosticLog.Log("Write", "Studio threw " + ex.GetType().FullName + " from ProcessSegmentPair; the edit "
                + (landed ? "IS in the document, so the write stands" : "is NOT in the document (or cannot be read back), so it is reported as failed")
                + ". Built target: " + builtLength + " chars; segment id " + (pair?.Properties?.Id.Id ?? "?")
                + "; is the editor's active segment: " + active
                + "; ms since the previous write began: " + (sincePrevious < 0 ? "first write" : sincePrevious.ToString())
                + "; thread " + Thread.CurrentThread.ManagedThreadId
                + (Environment.Is64BitProcess ? "; 64-bit" : "; 32-bit")
                + Environment.NewLine + ex);
        }

        /// <summary>Whether <paramref name="container"/> holds a comment with this
        /// text: for a comment add that threw, to see whether Studio added it all
        /// the same.</summary>
        internal static bool HasComment(IAbstractMarkupDataContainer container, string text)
        {
            return CountComments(container, text) > 0;
        }

        /// <summary>How many comments with this text <paramref name="container"/> holds.</summary>
        internal static int CountComments(IAbstractMarkupDataContainer container, string text)
        {
            if (container == null) return 0;
            int n = 0;
            foreach (var item in container)
            {
                if (item is ICommentMarker m)
                {
                    var cs = m.Comments;
                    for (int i = 0; cs != null && i < cs.Count; i++)
                        if (cs.GetItem(i)?.Text == text) n++;
                }
                if (item is IAbstractMarkupDataContainer inner) n += CountComments(inner, text);
            }
            return n;
        }

        private static int CountComments(ISegmentPair pair, string text)
        {
            return pair == null ? 0 : CountComments(pair.Source, text) + CountComments(pair.Target, text);
        }

        /// <summary>
        /// <see cref="IStudioDocument.AddCommentOnSegment"/>, then a look when Studio
        /// throws: it can throw AFTER it has added the comment, as it can after a
        /// write (see <see cref="ProcessChecked"/>). When the segment, read back,
        /// holds one more comment with this text than <paramref name="pair"/> did,
        /// the add stands: the fault is logged with its stack trace (never the
        /// text) and nothing is thrown. When it does not, or the segment cannot be
        /// read back, Studio's exception goes on to the caller as before.
        /// Counted, not just looked for, so that a comment with the same text that
        /// was already there is not taken for the new one. UI thread.
        /// </summary>
        /// <param name="pair">The segment as the document holds it now (just read).</param>
        internal static void AddCommentChecked(IStudioDocument doc, ISegmentPair pair, string text,
            Sdl.FileTypeSupport.Framework.NativeApi.Severity severity)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            int before = CountComments(pair, text);
            try
            {
                doc.AddCommentOnSegment(pair, text, severity);
            }
            catch (Exception ex)
            {
                var live = ReadLivePair(doc, pair);
                bool landed = live != null && CountComments(live, text) > before;
                DiagnosticLog.Log("Write", "Studio threw " + ex.GetType().FullName + " from AddCommentOnSegment; the comment "
                    + (landed ? "IS on the segment, so the add stands" : "is NOT on the segment (or it cannot be read back), so it is reported as failed")
                    + ". Comment: " + (text ?? "").Length + " chars; segment id " + (pair?.Properties?.Id.Id ?? "?")
                    + "; thread " + Thread.CurrentThread.ManagedThreadId
                    + (Environment.Is64BitProcess ? "; 64-bit" : "; 32-bit")
                    + Environment.NewLine + ex);
                if (!landed) throw;
            }
        }

        /// <summary>
        /// Why the edit did not land, or null when <paramref name="live"/> reads as
        /// <paramref name="acceptedAfter"/> (or cannot be compared). Logs the
        /// markup's shape when it did not: kinds and lengths, never the text.
        /// </summary>
        public static string Declined(IStudioDocument doc, ISegment live, string acceptedAfter,
            string shapeBefore, string shapeAfter, bool tracked)
        {
            if (live == null || acceptedAfter == null) return null;
            if (TrackedTargetMerge.Project(live, accept: true) == acceptedAfter) return null;
            return Reason(doc, live, shapeBefore, shapeAfter, tracked);
        }

        /// <summary>The reason for the user, once a check has found that Studio
        /// did not apply an edit; and the log line.</summary>
        private static string Reason(IStudioDocument doc, ISegment live, string shapeBefore, string shapeAfter, bool tracked)
        {
            bool hadRevisions = shapeBefore != null && (shapeBefore.Contains("Ins[") || shapeBefore.Contains("Del["));
            DiagnosticLog.Log("Write", "NOT APPLIED by Studio (tracked write: " + tracked
                + ", tracking now: " + (TrackingSuspension.IsTracking(doc)?.ToString() ?? "unknown")
                + "). Before: " + shapeBefore + " | built: " + shapeAfter
                + " | live after: " + TrackedTargetMerge.Shape(live));
            return hadRevisions && !tracked
                ? "the segment holds tracked changes that have not been accepted or rejected, and Trados Studio "
                  + "protects them while Track Changes is off. Accept or reject them, or switch Track Changes on, "
                  + "and try again. Nothing was written."
                : "Trados Studio did not apply the change: the segment still reads as it did. Nothing was written.";
        }

        /// <summary>
        /// The accepted view with every comment in it - text, author and severity,
        /// where its marker sits: what a comment edit changes. Deleted text and its
        /// comments are left out, as Accept All would, so the copies Studio's own
        /// tracked changes keep do not count.
        /// </summary>
        internal static string AcceptedWithComments(IAbstractMarkupDataContainer container)
        {
            var sb = new StringBuilder();
            WalkAccepted(container, sb);
            return sb.ToString();
        }

        private static void WalkAccepted(IAbstractMarkupDataContainer container, StringBuilder sb)
        {
            if (container == null) return;
            foreach (var item in container)
            {
                if (item is IText t) sb.Append(t.Properties?.Text);
                else if (item is IRevisionMarker r)
                {
                    if (r.Properties?.RevisionType != RevisionType.Delete) WalkAccepted(r, sb);
                }
                else if (item is ICommentMarker m)
                {
                    sb.Append("[c:");
                    var cs = m.Comments;
                    for (int i = 0; cs != null && i < cs.Count; i++)
                    {
                        var c = cs.GetItem(i);
                        if (c != null) sb.Append(c.Text).Append('\u0001').Append(c.Author).Append('\u0001').Append(c.Severity).Append('\u0002');
                    }
                    sb.Append(']');
                    WalkAccepted(m, sb);
                    sb.Append("[/c]");
                }
                else if (item is ITagPair tp) { sb.Append('<'); WalkAccepted(tp, sb); sb.Append('>'); }
                else if (item is IPlaceholderTag) sb.Append("<ph/>");
                else if (item is IAbstractMarkupDataContainer other) WalkAccepted(other, sb);
            }
        }

        /// <summary>The segment pair as the document holds it now, read afresh
        /// (the pair an edit was given is a copy, and keeps the old target); null
        /// when it cannot be found. One paragraph's pairs are copied, not the
        /// document's.</summary>
        public static ISegmentPair ReadLivePair(IStudioDocument doc, ISegmentPair pair)
        {
            try
            {
                var puId = doc?.GetParentParagraphUnit(pair)?.Properties?.ParagraphUnitId;
                var segId = pair?.Properties?.Id.Id;
                if (puId == null || segId == null) return null;
                foreach (var p in doc.GetSegmentPairsFromParagraph(puId.Value))
                    if (p?.Properties != null && p.Properties.Id.Id == segId) return p;
                DiagnosticLog.Log("Write", "could not find the segment again to check the write");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Log("Write", "could not read the segment back to check the write: " + ex.Message);
            }
            return null;
        }
    }

    /// <summary>Studio did not apply an edit to a segment (see
    /// <see cref="SegmentWriteCheck"/>); the message says why, for the user.</summary>
    internal sealed class SegmentWriteDeclinedException : InvalidOperationException
    {
        public SegmentWriteDeclinedException(string message) : base(message) { }
    }
}
