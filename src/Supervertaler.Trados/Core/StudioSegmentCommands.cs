using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Sdl.FileTypeSupport.Framework.NativeApi;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Runs Studio's own Merge Segments and Split Segment commands for the MCP
    /// merge_segments and split_segment tools.
    ///
    /// <para><b>Why this way.</b> Studio's public API has no merge or split, only
    /// the events that report one. Its own commands (MergeSegmentsAction, Ctrl+Alt+S;
    /// SplitSegmentsAction, Alt+Shift+T, both internal to
    /// Sdl.TranslationStudio.Editor) act on what the editor shows: Merge on the
    /// SELECTED ROWS, Split at the CURSOR in the source. So this selects the rows,
    /// or puts the cursor, through the editor's public types
    /// (AbstractFrameworkEditControl.SelectRowRange, IContentSelection.MoveTo),
    /// then runs the command by its id through Studio's action service. No
    /// keystrokes, so Studio need not be in front.</para>
    ///
    /// <para><b>By reflection.</b> The editor's assemblies are not on Studio's list
    /// of assemblies a plugin may reference (see <see cref="StudioEditor"/>), so
    /// every type is found by name in the copy Studio has loaded. If Studio renames
    /// one, <see cref="FindParts"/> names the missing part and the tools refuse,
    /// rather than doing something else. tools/mcp_merge_split_test.ps1 runs
    /// FindParts against each installed Studio's DLLs.</para>
    ///
    /// <para>Decompiled from Studio 2026 (19) and the 2026 beta on 26 Nov 2026:
    /// identical. Studio 2022 builds refuse: not checked there.</para>
    ///
    /// <para>Everything here must run on Studio's UI thread: the editor control
    /// throws InvokeRequiredException otherwise.</para>
    /// </summary>
    internal static class StudioSegmentCommands
    {
        internal const string MergeActionId = "MergeSegmentsAction";
        internal const string SplitActionId = "SplitSegmentsAction";

        private const string EditorApi = "Sdl.DesktopEditor.EditorApi";
        private const string ViewTypeName = "Sdl.TranslationStudio.Editor.TranslationEditor.SideBySideDocumentEditingView";
        internal const string ViewFieldName = "_sideBySideView";

        private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public;

        /// <summary>The Studio members this needs, found by name. <see cref="Missing"/>
        /// names the first one that is not there; the others are then unusable.</summary>
        internal sealed class Parts
        {
            public string Missing;
            public PropertyInfo ViewEditor, EditorSource, EditorTarget;
            public PropertyInfo ControlDocument, ControlRows, ControlSelection;
            public MethodInfo FindRow, GetSegment, SelectRowRange, ClearRowSelections, MoveTo;
            public PropertyInfo RowIsSegment, RowHidden, RowNumber, RowSegmentContainer, ContainerSegment;
            public PropertyInfo ContainerStartNode, TextNodeText;
            public ConstructorInfo PositionCtor;
            public PropertyInfo PositionTextOffset;
            public Type TextNodeType, ContainerType, TagPairType, LockedType, RevisionType;
            public MethodInfo GetAction;
            public PropertyInfo ActionEnabled;
            public MethodInfo ActionExecute;
            public object ActionService;
        }

        /// <summary>
        /// Finds every member in the assemblies loaded in this AppDomain. With
        /// <paramref name="resolveService"/> false it does not ask Studio's container
        /// for the action service, so a test can run it against Studio's DLLs
        /// without a running Studio.
        /// </summary>
        internal static Parts FindParts(bool resolveService = true)
        {
            var p = new Parts();
            try
            {
                var view = TypeOf("Sdl.TranslationStudio.Editor", ViewTypeName);
                var fec = TypeOf(EditorApi, EditorApi + ".IFrameworkEditControl");
                var edit = TypeOf(EditorApi, EditorApi + ".IEditControl");
                var fdoc = TypeOf(EditorApi, EditorApi + ".IFrameworkDocument");
                var row = TypeOf(EditorApi, EditorApi + ".IRowInfo");
                var sel = TypeOf(EditorApi, EditorApi + ".IContentSelection");
                var pos = TypeOf(EditorApi, EditorApi + ".Position");
                var content = TypeOf(EditorApi, EditorApi + ".IAbstractContentNode");
                // A C++/CLI class in the global namespace.
                var control = TypeOf("Sdl.DesktopEditor.Control", "AbstractFrameworkEditControl");
                p.ContainerType = TypeOf(EditorApi, EditorApi + ".IAbstractContainerNode");
                p.TextNodeType = TypeOf(EditorApi, EditorApi + ".ITextNode");
                p.TagPairType = TypeOf(EditorApi, EditorApi + ".ITagPairContainerNode");
                p.LockedType = TypeOf(EditorApi, EditorApi + ".ILockedContentContainerNode");
                p.RevisionType = TypeOf(EditorApi, EditorApi + ".IRevisionMarkerContainerNode");

                string missing(string what) => p.Missing = what;

                if (view == null) { missing(ViewTypeName); return p; }
                if (fec == null || edit == null || fdoc == null || row == null || sel == null || pos == null
                    || content == null || p.ContainerType == null || p.TextNodeType == null || p.TagPairType == null
                    || p.LockedType == null || p.RevisionType == null)
                { missing(EditorApi + " editor interfaces"); return p; }
                if (control == null) { missing("AbstractFrameworkEditControl"); return p; }

                p.ViewEditor = view.GetProperty("SideBySideEditor", Inst);
                if (p.ViewEditor == null) { missing("SideBySideDocumentEditingView.SideBySideEditor"); return p; }
                p.EditorSource = p.ViewEditor.PropertyType.GetProperty("Source", Inst);
                p.EditorTarget = p.ViewEditor.PropertyType.GetProperty("Target", Inst);
                if (p.EditorSource == null || p.EditorTarget == null) { missing("SideBySideEditor.Source/Target"); return p; }

                p.ControlDocument = fec.GetProperty("Document", Inst | BindingFlags.DeclaredOnly);
                p.ControlRows = edit.GetProperty("Rows", Inst);
                p.ControlSelection = edit.GetProperty("Selection", Inst);
                p.FindRow = edit.GetMethod("FindRow", new[] { pos });
                p.GetSegment = fdoc.GetMethod("GetSegment", new[] { typeof(ParagraphUnitId), typeof(SegmentId) });
                p.SelectRowRange = control.GetMethod("SelectRowRange", Inst, null, new[] { row, row }, null);
                p.ClearRowSelections = control.GetMethod("ClearAllRowSelections", Inst, null, Type.EmptyTypes, null);
                p.MoveTo = sel.GetMethod("MoveTo", new[] { pos, typeof(bool) });
                p.RowIsSegment = row.GetProperty("IsSegment");
                p.RowHidden = row.GetProperty("Hidden");
                p.RowNumber = row.GetProperty("RowNumber");
                p.RowSegmentContainer = row.GetProperty("SegmentContainer");
                var segNode = TypeOf(EditorApi, EditorApi + ".ISegmentContainerNode");
                p.ContainerSegment = segNode?.GetProperty("Segment");
                p.ContainerStartNode = p.ContainerType.GetProperty("StartNode");
                p.TextNodeText = p.TextNodeType.GetProperty("Text");
                p.PositionCtor = pos.GetConstructor(new[] { content });
                p.PositionTextOffset = pos.GetProperty("TextOffset", Inst);

                var named = new (object member, string name)[]
                {
                    (p.ControlDocument, "IFrameworkEditControl.Document"),
                    (p.ControlRows, "IEditControl.Rows"),
                    (p.ControlSelection, "IEditControl.Selection"),
                    (p.FindRow, "IEditControl.FindRow(Position)"),
                    (p.GetSegment, "IFrameworkDocument.GetSegment(ParagraphUnitId, SegmentId)"),
                    (p.SelectRowRange, "AbstractFrameworkEditControl.SelectRowRange(IRowInfo, IRowInfo)"),
                    (p.ClearRowSelections, "AbstractFrameworkEditControl.ClearAllRowSelections()"),
                    (p.MoveTo, "IContentSelection.MoveTo(Position, bool)"),
                    (p.RowIsSegment, "IRowInfo.IsSegment"),
                    (p.RowHidden, "IRowInfo.Hidden"),
                    (p.RowNumber, "IRowInfo.RowNumber"),
                    (p.RowSegmentContainer, "IRowInfo.SegmentContainer"),
                    (p.ContainerSegment, "ISegmentContainerNode.Segment"),
                    (p.ContainerStartNode, "IAbstractContainerNode.StartNode"),
                    (p.TextNodeText, "ITextNode.Text"),
                    (p.PositionCtor, "Position(IAbstractContentNode)"),
                    (p.PositionTextOffset, "Position.TextOffset"),
                };
                foreach (var (member, name) in named)
                    if (member == null) { missing(name); return p; }

                var service = TypeOf("Sdl.Desktop.Platform", "Sdl.Desktop.Platform.CommandBars.IActionService");
                var action = TypeOf("Sdl.Desktop.Platform", "Sdl.Desktop.Platform.CommandBars.IAction");
                if (service == null || action == null) { missing("Sdl.Desktop.Platform action service"); return p; }
                p.GetAction = service.GetMethod("GetAction", new[] { typeof(string) });
                p.ActionEnabled = action.GetProperty("Enabled");
                p.ActionExecute = action.GetMethod("Execute", Type.EmptyTypes);
                if (p.GetAction == null || p.ActionEnabled == null || p.ActionExecute == null)
                { missing("IActionService.GetAction / IAction.Enabled / IAction.Execute"); return p; }

                if (resolveService)
                {
#if STUDIO17
                    missing("Studio's action service (not checked in Studio 2022)");
                    return p;
#else
                    var resolve = StudioEditor.FindResolve();
                    if (resolve == null) { missing("StudioPluginManager.Resolve<T>()"); return p; }
                    p.ActionService = resolve.MakeGenericMethod(service).Invoke(null, null);
                    if (p.ActionService == null) { missing("Studio's action service"); return p; }
#endif
                }
            }
            catch (Exception ex)
            {
                p.Missing = "lookup failed: " + Inner(ex).Message;
            }
            return p;
        }

        private static Type TypeOf(string assembly, string fullName) =>
            AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => string.Equals(a.GetName().Name, assembly, StringComparison.Ordinal))
                .Select(a => a.GetType(fullName, false))
                .FirstOrDefault(t => t != null);

        /// <summary>The field on the integration API's document that holds its editor
        /// view, searched up the type's bases. Null if Studio renamed it.</summary>
        internal static FieldInfo FindViewField(Type documentType)
        {
            for (var t = documentType; t != null; t = t.BaseType)
            {
                var f = t.GetField(ViewFieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        /// <summary>The open document's side-by-side editor, ready to use.</summary>
        internal sealed class Editor
        {
            public Parts P;
            public object Source, Target;
        }

        /// <summary>Null and an error when the parts are missing or the editor view
        /// is not the side-by-side editor (e.g. a preview is showing).</summary>
        internal static Editor Open(object studioDocument, out string error)
        {
            error = null;
            var p = FindParts();
            if (p.Missing != null)
            {
                error = NotFound(p.Missing);
                return null;
            }
            var field = studioDocument == null ? null : FindViewField(studioDocument.GetType());
            if (field == null)
            {
                error = NotFound("the document's editor view (" + ViewFieldName + ")");
                return null;
            }
            var view = field.GetValue(studioDocument);
            if (view == null || !p.ViewEditor.DeclaringType.IsInstanceOfType(view))
            {
                error = "the document is not showing in Studio's side-by-side editor. Switch the editor back to the " +
                        "normal grid view and try again";
                return null;
            }
            var sbs = p.ViewEditor.GetValue(view);
            var e = new Editor
            {
                P = p,
                Source = sbs == null ? null : p.EditorSource.GetValue(sbs),
                Target = sbs == null ? null : p.EditorTarget.GetValue(sbs),
            };
            if (e.Source == null || e.Target == null)
            {
                error = "Studio's editor is not ready (no source or target grid)";
                return null;
            }
            return e;
        }

        internal static string NotFound(string part) =>
            "this Studio version does not have the part of Studio's editor that merge/split uses (" + part +
            "), so nothing was changed. Merge or split in Studio by hand (Ctrl+Alt+S / Alt+Shift+T), and tell " +
            "the Supervertaler developer which Studio version this is";

        /// <summary>The segment's container node in one side's editor document, or null.</summary>
        private static object SegmentNode(Editor e, object control, ParagraphUnitId pu, SegmentId seg)
        {
            var doc = e.P.ControlDocument.GetValue(control);
            return doc == null ? null : e.P.GetSegment.Invoke(doc, new object[] { pu, seg });
        }

        /// <summary>
        /// The grid row that shows this segment. FindRow at the segment's start
        /// node can answer the row BEFORE it: the structure row between two
        /// segments of a paragraph, which Studio keeps hidden (found live on
        /// 26 Nov 2026: merging 12+13 was refused as "hidden by a display
        /// filter"). So the answer is checked - the row must be a segment row
        /// holding this very segment - and the rows around it are searched when
        /// it is not; the whole grid only as a last resort.
        /// </summary>
        private static object RowOf(Editor e, object control, object segmentNode, SegmentId seg, ParagraphUnitId pu)
        {
            var p = e.P;
            var rows = p.ControlRows.GetValue(control) as IList;
            if (rows == null) return null;
            int start = -1;
            var startNode = p.ContainerStartNode.GetValue(segmentNode);
            if (startNode != null)
            {
                var position = p.PositionCtor.Invoke(new[] { startNode });
                var found = p.FindRow.Invoke(control, new[] { position });
                if (found != null) start = (int)p.RowNumber.GetValue(found);
            }
            int index = SegmentStructurePlanner.NearestMatch(rows.Count, start,
                i => RowHolds(p, rows[i], seg.Id, pu.Id));
            // So the live test shows whether Studio's guess was off, and by how much.
            if (index != start)
            {
                try { DiagnosticLog.WriteAlways("SegmentStructure", $"FindRow gave row {start} for segment {seg.Id}; it is on row {index}"); }
                catch { }
            }
            return index < 0 ? null : rows[index];
        }

        private static bool RowHolds(Parts p, object row, string segmentId, string paragraphId)
        {
            if (row == null || !(bool)p.RowIsSegment.GetValue(row)) return false;
            var container = p.RowSegmentContainer.GetValue(row);
            var segment = container == null ? null : p.ContainerSegment.GetValue(container) as ISegment;
            return segment != null
                   && string.Equals(segment.Properties?.Id.Id, segmentId, StringComparison.Ordinal)
                   && string.Equals(segment.ParentParagraphUnit?.Properties?.ParagraphUnitId.Id, paragraphId, StringComparison.Ordinal);
        }

        /// <summary>
        /// Selects the segments' rows in the target grid and runs Studio's Merge.
        /// Returns null when Studio ran it, otherwise why not. The caller has done
        /// <see cref="SegmentStructurePlanner.CheckMerge"/>; this adds what only the
        /// editor knows: that the rows are shown and next to each other, and Studio's
        /// own verdict (the command's Enabled state, which covers its other rules).
        /// The row selection is cleared again on every path.
        /// </summary>
        internal static string Merge(Editor e, IList<KeyValuePair<ParagraphUnitId, SegmentId>> segments, IList<string> ids)
        {
            var p = e.P;
            var rows = new List<object>();
            for (int i = 0; i < segments.Count; i++)
            {
                var node = SegmentNode(e, e.Target, segments[i].Key, segments[i].Value);
                var row = node == null ? null : RowOf(e, e.Target, node, segments[i].Value, segments[i].Key);
                if (row == null) return $"segment {ids[i]} is not in Studio's editor grid";
                if ((bool)p.RowHidden.GetValue(row))
                    return $"segment {ids[i]} is hidden by a display filter. Clear the filter in Studio first";
                rows.Add(row);
            }

            var numbers = rows.Select(r => (int)p.RowNumber.GetValue(r)).ToList();
            var order = Enumerable.Range(0, rows.Count).OrderBy(i => numbers[i]).ToList();
            var allRows = p.ControlRows.GetValue(e.Target) as IList;
            if (allRows == null) return "Studio's editor grid has no rows";
            for (int k = 1; k < order.Count; k++)
            {
                int from = numbers[order[k - 1]], to = numbers[order[k]];
                for (int n = from + 1; n < to; n++)
                    if (n >= 0 && n < allRows.Count && (bool)p.RowIsSegment.GetValue(allRows[n]))
                        return $"segments {ids[order[k - 1]]} and {ids[order[k]]} are not next to each other. " +
                               "Only neighbouring segments can be merged; list every segment in between too";
            }

            var action = p.GetAction.Invoke(p.ActionService, new object[] { MergeActionId });
            if (action == null) return NotFound("the " + MergeActionId + " command");

            try
            {
                p.ClearRowSelections.Invoke(e.Target, null);
                p.SelectRowRange.Invoke(e.Target, new[] { rows[order[0]], rows[order[order.Count - 1]] });
                if (!(bool)p.ActionEnabled.GetValue(action))
                    return "Studio does not allow merging these segments (its Merge Segments command is unavailable " +
                           "for them: for example a segment has QA marks, or a document boundary is in between)";
                p.ActionExecute.Invoke(action, null);
                return null;
            }
            finally
            {
                try { p.ClearRowSelections.Invoke(e.Target, null); } catch { }
            }
        }

        /// <summary>The segment's source as text runs in document order, with the
        /// text nodes they came from (same index).</summary>
        internal static List<SegmentStructurePlanner.TextRun> SourceRuns(
            Editor e, ParagraphUnitId pu, SegmentId seg, out List<object> textNodes)
        {
            textNodes = new List<object>();
            var runs = new List<SegmentStructurePlanner.TextRun>();
            var node = SegmentNode(e, e.Source, pu, seg);
            if (node == null) return null;
            int pairCounter = 0;
            Walk(e.P, node, new List<int>(), false, false, runs, textNodes, ref pairCounter, 0);
            return runs;
        }

        private static void Walk(Parts p, object container, List<int> pairs, bool locked, bool revision,
            List<SegmentStructurePlanner.TextRun> runs, List<object> textNodes, ref int pairCounter, int depth)
        {
            // Segments are shallow; the limit only guards against a cycle.
            if (depth > 64 || !(container is IEnumerable children)) return;
            foreach (var child in children)
            {
                if (child == null || ReferenceEquals(child, container)) continue;
                if (p.TextNodeType.IsInstanceOfType(child))
                {
                    var text = (p.TextNodeText.GetValue(child) as IText)?.Properties?.Text ?? "";
                    if (text.Length == 0) continue;
                    runs.Add(new SegmentStructurePlanner.TextRun
                    {
                        Text = text, TagPairs = new List<int>(pairs), Locked = locked, InRevision = revision
                    });
                    textNodes.Add(child);
                }
                else if (p.ContainerType.IsInstanceOfType(child))
                {
                    var inner = pairs;
                    if (p.TagPairType.IsInstanceOfType(child))
                        inner = new List<int>(pairs) { ++pairCounter };
                    Walk(p, child, inner,
                        locked || p.LockedType.IsInstanceOfType(child),
                        revision || p.RevisionType.IsInstanceOfType(child),
                        runs, textNodes, ref pairCounter, depth + 1);
                }
            }
        }

        /// <summary>
        /// Puts the cursor in the source at the given text node and offset and runs
        /// Studio's Split. Null when Studio ran it, otherwise why not.
        /// </summary>
        internal static string Split(Editor e, object textNode, int offset)
        {
            var p = e.P;
            var action = p.GetAction.Invoke(p.ActionService, new object[] { SplitActionId });
            if (action == null) return NotFound("the " + SplitActionId + " command");

            var position = p.PositionCtor.Invoke(new[] { textNode });
            p.PositionTextOffset.SetValue(position, (long)offset);
            var selection = p.ControlSelection.GetValue(e.Source);
            if (selection == null) return "Studio's source grid has no cursor";
            var moved = p.MoveTo.Invoke(selection, new[] { position, (object)false });
            if (moved is bool ok && !ok) return "Studio did not accept the cursor position for the split";

            if (!(bool)p.ActionEnabled.GetValue(action))
                return "Studio does not allow a split there (its Split Segment command is unavailable at that point: " +
                       "for example the segment or that text is locked, or the segment has QA marks)";
            p.ActionExecute.Invoke(action, null);
            return null;
        }

        internal static Exception Inner(Exception ex) =>
            ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
    }
}
