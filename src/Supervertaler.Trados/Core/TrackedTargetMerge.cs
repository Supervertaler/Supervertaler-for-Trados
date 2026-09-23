using System;
using System.Collections.Generic;
using System.Text;
using Sdl.FileTypeSupport.Framework.BilingualApi;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Rewrites a target as word-level tracked changes (issue #138): given the
    /// target as it was and the target as it should become, builds content that
    /// shows only what actually changed - a four-character insertion as a
    /// four-character insertion, not the whole sentence struck through and
    /// retyped.
    ///
    /// <para><b>Why this exists at all.</b> Studio's <c>ProcessSegmentPair</c>
    /// replaces the entire segment range with whatever the callback built, so
    /// with Track Changes on, every write is recorded as delete-everything plus
    /// insert-everything however small the change. No amount of care in the
    /// callback survives that. So the caller switches tracking off around the
    /// write, and the revisions a reviewer sees are the ones built here.</para>
    ///
    /// <para><b>Diff each text run separately, never across a tag.</b> Content is
    /// runs of text separated by tags. Each run is diffed on its own, so a
    /// revision can never straddle a tag boundary, tags are never wrapped, moved
    /// or duplicated, and a change that really does span a tag becomes two
    /// adjacent revisions - which is what Studio records when a person makes the
    /// same edit.</para>
    ///
    /// <para><b>Comments are transparent.</b> They wrap text rather than
    /// separating it, and an ordinary write re-anchors every comment over the
    /// whole new target. The merge builds on the OLD tree, so each comment keeps
    /// the span it had.</para>
    ///
    /// <para><b>Refuses rather than guesses.</b> If the tags do not line up, or the
    /// target already carries tracked changes, it returns a reason and changes
    /// nothing the caller will use. The caller then writes the way it always has,
    /// with tracking left on - so the worst case is today's behaviour, never a
    /// change nobody can see.</para>
    ///
    /// <para><b>And it checks itself.</b> Before handing anything back it projects
    /// the result both ways: accepting every change must give exactly the new
    /// target, and rejecting every change exactly the old one. If either fails,
    /// that is reported as a refusal like any other. A bug here therefore costs
    /// a coarse revision, not a corrupted segment.</para>
    /// </summary>
    internal static class TrackedTargetMerge
    {
        private sealed class Run
        {
            public readonly List<IText> Leaves = new List<IText>();
            public readonly StringBuilder Text = new StringBuilder();
            // Where an empty run sits, for inserting into it: before AnchorBefore
            // in AnchorContainer, or at the end when AnchorBefore is null.
            public IAbstractMarkupDataContainer AnchorContainer;
            public IAbstractMarkupData AnchorBefore;
        }

        private sealed class Flat
        {
            public readonly List<Run> Runs = new List<Run>();
            public readonly List<string> Skeleton = new List<string>();
            public string Refusal;
        }

        /// <summary>
        /// Rewrites <paramref name="oldTarget"/> in place so that it carries
        /// <paramref name="newTarget"/>'s text as revisions. Returns null on
        /// success, or the reason it could not - in which case
        /// <paramref name="oldTarget"/> may have been partly modified and must be
        /// discarded. <paramref name="newTarget"/> is only read.
        /// </summary>
        public static string TryMerge(
            IAbstractMarkupDataContainer oldTarget,
            IAbstractMarkupDataContainer newTarget,
            IDocumentItemFactory factory,
            IRevisionProperties insertProps,
            IRevisionProperties deleteProps)
        {
            if (oldTarget == null || newTarget == null) return "no target to compare";
            if (factory == null || insertProps == null || deleteProps == null)
                return "revision markers could not be created";

            var before = Flatten(oldTarget);
            if (before.Refusal != null) return before.Refusal;
            var after = Flatten(newTarget);
            if (after.Refusal != null) return after.Refusal;

            if (!SameSkeleton(before.Skeleton, after.Skeleton))
                return "the inline tags differ between the old and new target";

            // What the result must project to, taken before anything is touched.
            var expectAccept = Project(newTarget, accept: true);
            var expectReject = Project(oldTarget, accept: false);

            try
            {
                for (int i = 0; i < before.Runs.Count; i++)
                {
                    var oldRun = before.Runs[i];
                    var newRun = after.Runs[i];
                    var oldText = oldRun.Text.ToString();
                    var newText = newRun.Text.ToString();
                    if (oldText == newText) continue;

                    var pieces = TokenDiff.Diff(oldText, newText);

                    if (oldRun.Leaves.Count > 0)
                    {
                        RewriteLeaves(oldRun, pieces, factory, insertProps, deleteProps);
                    }
                    else
                    {
                        // Nothing was there: a pure insertion, placed where the
                        // empty run sits. Its text node is modelled on the new
                        // run's, which is where this text came from.
                        var template = newRun.Leaves.Count > 0 ? newRun.Leaves[0] : null;
                        if (template == null) return "no text node to model an insertion on";
                        var marker = factory.CreateRevision(insertProps);
                        marker.Add(TextLike(template, newText));
                        InsertAt(oldRun.AnchorContainer, oldRun.AnchorBefore, marker);
                    }
                }
            }
            catch (Exception ex)
            {
                return "building the revisions failed: " + ex.Message;
            }

            // The round trip, per segment, on every write. Cheap string walks, and
            // the one check that proves nothing was dropped or duplicated.
            if (Project(oldTarget, accept: true) != expectAccept)
                return "self-check failed: accepting the changes would not give the new target";
            if (Project(oldTarget, accept: false) != expectReject)
                return "self-check failed: rejecting the changes would not give the old target";

            return null;
        }

        // ─── Flattening ──────────────────────────────────────────────

        private static Flat Flatten(IAbstractMarkupDataContainer root)
        {
            var flat = new Flat();
            var current = new Run();
            flat.Runs.Add(current);
            Walk(root, flat, ref current);
            if (flat.Refusal == null) Close(current, root, null);
            return flat;
        }

        private static void Close(Run run, IAbstractMarkupDataContainer container, IAbstractMarkupData before)
        {
            run.AnchorContainer = container;
            run.AnchorBefore = before;
        }

        private static void Boundary(Flat flat, ref Run current,
            IAbstractMarkupDataContainer container, IAbstractMarkupData before, string token)
        {
            Close(current, container, before);
            flat.Skeleton.Add(token);
            current = new Run();
            flat.Runs.Add(current);
        }

        private static void Walk(IAbstractMarkupDataContainer container, Flat flat, ref Run current)
        {
            // A snapshot: nothing is mutated during flattening, but a live
            // enumerator over a Studio container is not something to lean on.
            var items = new List<IAbstractMarkupData>(container);
            foreach (var item in items)
            {
                if (flat.Refusal != null) return;

                if (item is IText text)
                {
                    current.Leaves.Add(text);
                    current.Text.Append(text.Properties?.Text ?? "");
                }
                else if (item is IRevisionMarker)
                {
                    // Diffing against a target that already carries revisions
                    // would mean editing inside someone else's marks. Refused
                    // rather than flattened.
                    flat.Refusal = "the segment already carries tracked changes";
                }
                else if (item is ICommentMarker comment)
                {
                    Walk(comment, flat, ref current);   // transparent
                }
                else if (item is ITagPair pair)
                {
                    var id = TagId(pair.StartTagProperties);
                    Boundary(flat, ref current, container, pair, "<" + id + ">");
                    Walk(pair, flat, ref current);
                    if (flat.Refusal != null) return;
                    Close(current, pair, null);
                    flat.Skeleton.Add("</" + id + ">");
                    current = new Run();
                    flat.Runs.Add(current);
                }
                else if (item is IPlaceholderTag ph)
                {
                    Boundary(flat, ref current, container, ph, "<" + TagId(ph.Properties) + "/>");
                }
                else if (item is ILockedContent locked)
                {
                    // Opaque: compared whole, never diffed into.
                    Boundary(flat, ref current, container, locked,
                        "[locked:" + PlainText(locked.Content) + "]");
                }
                else
                {
                    // Anything else - a sub-segment reference, a marker type this
                    // was not written for - is not guessed at.
                    flat.Refusal = "the segment contains content this cannot track ("
                                 + item.GetType().Name + ")";
                }
            }
        }

        private static string TagId(Sdl.FileTypeSupport.Framework.NativeApi.IAbstractTagProperties p)
        {
            // Typed, not reflective, for the same reason as the tag audit: if the
            // SDK moves this, the build should break rather than the check go
            // quiet. A blank id still has to take part in the comparison.
            var id = p?.TagId.Id;
            return string.IsNullOrEmpty(id) ? "?" : id;
        }

        private static bool SameSkeleton(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }

        // ─── Rewriting one run ───────────────────────────────────────

        private struct Fragment
        {
            public TokenDiff.Op Op;
            public string Text;
        }

        /// <summary>
        /// Spreads the edit script over the run's text nodes by position in the old
        /// text. Unchanged and deleted text stays in the node it came from; an
        /// insertion goes to the node ending where it occurs, so text inserted at
        /// a comment's edge lands outside the comment rather than inside it.
        /// </summary>
        private static void RewriteLeaves(Run run, List<TokenDiff.Piece> pieces,
            IDocumentItemFactory factory, IRevisionProperties ins, IRevisionProperties del)
        {
            int n = run.Leaves.Count;
            var starts = new int[n];
            var ends = new int[n];
            int pos = 0;
            for (int i = 0; i < n; i++)
            {
                starts[i] = pos;
                pos += (run.Leaves[i].Properties?.Text ?? "").Length;
                ends[i] = pos;
            }

            var frags = new List<Fragment>[n];
            for (int i = 0; i < n; i++) frags[i] = new List<Fragment>();

            int oldPos = 0;
            foreach (var p in pieces)
            {
                if (p.Op == TokenDiff.Op.Insert)
                {
                    int leaf = 0;
                    for (int i = 0; i < n; i++)
                        if (oldPos > starts[i] && oldPos <= ends[i]) { leaf = i; break; }
                    Append(frags[leaf], p.Op, p.Text);
                    continue;
                }

                int from = oldPos, to = oldPos + p.Text.Length;
                for (int i = 0; i < n; i++)
                {
                    int s = Math.Max(from, starts[i]), e = Math.Min(to, ends[i]);
                    if (e > s) Append(frags[i], p.Op, p.Text.Substring(s - from, e - s));
                }
                oldPos = to;
            }

            for (int i = 0; i < n; i++)
            {
                var leaf = run.Leaves[i];
                var original = leaf.Properties?.Text ?? "";
                var f = frags[i];
                if (f.Count == 0) continue;   // an empty node nothing touched
                if (f.Count == 1 && f[0].Op == TokenDiff.Op.Equal && f[0].Text == original) continue;

                var parent = leaf.Parent;
                int index = leaf.IndexInParent;
                leaf.RemoveFromParent();
                foreach (var frag in f)
                {
                    IAbstractMarkupData node;
                    if (frag.Op == TokenDiff.Op.Equal)
                    {
                        node = TextLike(leaf, frag.Text);
                    }
                    else
                    {
                        var marker = factory.CreateRevision(frag.Op == TokenDiff.Op.Insert ? ins : del);
                        marker.Add(TextLike(leaf, frag.Text));
                        node = marker;
                    }
                    parent.Insert(index++, node);
                }
            }
        }

        private static void Append(List<Fragment> list, TokenDiff.Op op, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (list.Count > 0 && list[list.Count - 1].Op == op)
            {
                var last = list[list.Count - 1];
                last.Text += text;
                list[list.Count - 1] = last;
            }
            else list.Add(new Fragment { Op = op, Text = text });
        }

        /// <summary>A copy of <paramref name="model"/>, carrying its formatting,
        /// with different text.</summary>
        private static IText TextLike(IText model, string text)
        {
            var clone = (IText)model.Clone();
            clone.Properties.Text = text;
            return clone;
        }

        private static void InsertAt(IAbstractMarkupDataContainer container,
            IAbstractMarkupData before, IAbstractMarkupData node)
        {
            if (before != null && before.Parent == container)
                container.Insert(before.IndexInParent, node);
            else
                container.Add(node);
        }

        // ─── Projection: what Accept All / Reject All would produce ──

        /// <summary>
        /// The target as it would read after accepting (or rejecting) every
        /// revision, with tags rendered so that structure is compared too.
        /// Comments are transparent, as everywhere else here.
        /// </summary>
        internal static string Project(IAbstractMarkupDataContainer container, bool accept)
        {
            var sb = new StringBuilder();
            ProjectInto(container, accept, sb);
            return sb.ToString();
        }

        private static void ProjectInto(IAbstractMarkupDataContainer container, bool accept, StringBuilder sb)
        {
            foreach (var item in container)
            {
                if (item is IText t) sb.Append(t.Properties?.Text);
                else if (item is IRevisionMarker r)
                {
                    var type = r.Properties?.RevisionType;
                    bool keep = type == RevisionType.Insert ? accept
                              : type == RevisionType.Delete ? !accept
                              : true;
                    if (keep) ProjectInto(r, accept, sb);
                }
                else if (item is ICommentMarker c) ProjectInto(c, accept, sb);
                else if (item is ITagPair pair)
                {
                    var id = TagId(pair.StartTagProperties);
                    sb.Append('<').Append(id).Append('>');
                    ProjectInto(pair, accept, sb);
                    sb.Append("</").Append(id).Append('>');
                }
                else if (item is IPlaceholderTag ph) sb.Append('<').Append(TagId(ph.Properties)).Append("/>");
                else if (item is ILockedContent locked) sb.Append("[locked:").Append(PlainText(locked.Content)).Append(']');
                else if (item is IAbstractMarkupDataContainer other) ProjectInto(other, accept, sb);
            }
        }

        private static string PlainText(IAbstractMarkupDataContainer container)
        {
            if (container == null) return "";
            var sb = new StringBuilder();
            foreach (var item in container)
            {
                if (item is IText t) sb.Append(t.Properties?.Text);
                else if (item is IAbstractMarkupDataContainer nested) sb.Append(PlainText(nested));
            }
            return sb.ToString();
        }
    }
}
