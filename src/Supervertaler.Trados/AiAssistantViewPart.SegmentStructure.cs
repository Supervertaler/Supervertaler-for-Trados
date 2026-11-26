using System;
using System.Collections.Generic;
using System.Linq;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Sdl.FileTypeSupport.Framework.NativeApi;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados
{
    // MCP merge_segments / split_segment. The checks are in
    // Core/SegmentStructurePlanner, the Studio side in Core/StudioSegmentCommands.
    public partial class AiAssistantViewPart
    {
        /// <summary>Bridge delegate for POST /v1/merge-segments. Marshals to the UI thread.</summary>
        private BridgeSegmentStructureResponse BridgeMergeSegments(BridgeMergeSegmentsRequest req)
        {
            var marshalled = OnUiThread(() => BridgeMergeSegments(req));
            if (marshalled != null) return marshalled;

            if (_activeDocument == null)
                return Refuse("no document is open in the Trados editor");

            var ids = (req?.Ids ?? new List<string>()).Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).ToList();
            if (ids.Count < 2)
                return Refuse("give at least two segment ids (from get_segments) to merge, in 'ids'");

            try
            {
                var pairs = new List<ISegmentPair>();
                var candidates = new List<SegmentStructurePlanner.MergeCandidate>();
                var addresses = new List<KeyValuePair<ParagraphUnitId, SegmentId>>();
                foreach (var id in ids)
                {
                    var pair = ResolveBridgeSegment(id, null, null, out var error);
                    if (pair == null) return Refuse($"segment {id}: {error}");
                    var pu = _activeDocument.GetParentParagraphUnit(pair);
                    pairs.Add(pair);
                    addresses.Add(new KeyValuePair<ParagraphUnitId, SegmentId>(pu.Properties.ParagraphUnitId, pair.Properties.Id));
                    candidates.Add(new SegmentStructurePlanner.MergeCandidate
                    {
                        Id = id,
                        ParagraphId = pu?.Properties?.ParagraphUnitId.Id ?? "",
                        Status = pair.Properties?.ConfirmationLevel.ToString(),
                        Locked = pair.Properties?.IsLocked == true,
                        TargetTagIds = TagIdsOf(pair.Target),
                        HasGroupTag = HasGroupTag(pair.Target),
                    });
                }

                var refusal = SegmentStructurePlanner.CheckMerge(candidates);
                if (refusal != null) return Refuse(refusal);

                var editor = StudioSegmentCommands.Open(_activeDocument, out var openError);
                if (editor == null) return Refuse(openError);
                var notShown = StudioSegmentCommands.CheckShown(_activeDocument, StudioEditor.Controller?.ActiveDocument);
                if (notShown != null) return Refuse(notShown);

                var paragraphId = candidates[0].ParagraphId;
                var before = SegmentIdsInParagraph(paragraphId);

                // Puts the editor on the first segment (the user sees the merge) and
                // leaves any segment being edited, so its edit is kept.
                _activeDocument.SetActiveSegmentPair(paragraphId, addresses[0].Value.Id, true);

                refusal = StudioSegmentCommands.Merge(editor, addresses, ids);
                if (refusal != null) return Refuse(refusal);

                var after = SegmentIdsInParagraph(paragraphId);
                var requested = new HashSet<string>(addresses.Select(a => a.Value.Id), StringComparer.Ordinal);
                var survivors = after.Where(requested.Contains).ToList();
                var added = after.Except(before).ToList();
                if (survivors.Count == requested.Count)
                    return Refuse("Studio did not merge the segments (nothing changed). Merge them in Studio by hand " +
                                  "(select the rows, Ctrl+Alt+S) to see why");

                _bridgeUnsavedWritesDoc = _activeDocument;
                var mergedId = (added.Count > 0 ? added[0] : survivors.FirstOrDefault()) ?? addresses[0].Value.Id;
                var fullId = paragraphId + ":" + mergedId;
                var merged = ResolveBridgeSegment(fullId, null, null, out _);
                return new BridgeSegmentStructureResponse
                {
                    Ok = true,
                    Ids = new List<string> { fullId },
                    Source = merged == null ? null : PlainTextOf(merged.Source),
                    Target = merged == null ? null : PlainTextOf(merged.Target),
                    Note = $"Merged {ids.Count} segments into segment {mergedId}. Its target is the old targets run " +
                           "together: write the translation of the whole sentence with update_segments (read the " +
                           "segment with get_segments first for its fp and tags). Studio cannot undo a merge with " +
                           "Ctrl+Z; to go back, split it with split_segment. The document is not saved.",
                };
            }
            catch (Exception ex)
            {
                return Refuse("merge failed: " + StudioSegmentCommands.Inner(ex).Message);
            }
        }

        /// <summary>Bridge delegate for POST /v1/split-segment. Marshals to the UI thread.</summary>
        private BridgeSegmentStructureResponse BridgeSplitSegment(BridgeSplitSegmentRequest req)
        {
            var marshalled = OnUiThread(() => BridgeSplitSegment(req));
            if (marshalled != null) return marshalled;

            if (_activeDocument == null)
                return Refuse("no document is open in the Trados editor");
            if (string.IsNullOrWhiteSpace(req?.Id))
                return Refuse("give the id (from get_segments) of the segment to split, in 'id'");

            try
            {
                var pair = ResolveBridgeSegment(req.Id.Trim(), null, null, out var error);
                if (pair == null) return Refuse(error);
                var pu = _activeDocument.GetParentParagraphUnit(pair);
                var paragraphId = pu?.Properties?.ParagraphUnitId.Id ?? "";
                var segId = pair.Properties.Id;

                if (pair.Properties.IsLocked)
                    return Refuse($"segment {req.Id} is locked. Unlock it first if it really should be split");
                var status = pair.Properties.ConfirmationLevel.ToString();
                if (status == "Translated" || status == "ApprovedTranslation" || status == "ApprovedSignOff")
                    return Refuse($"segment {req.Id} is confirmed ({status}). Studio would stop to ask before splitting it. " +
                                  "If it should be split anyway, set it back to Draft first (update_segments, status Draft)");

                var editor = StudioSegmentCommands.Open(_activeDocument, out var openError);
                if (editor == null) return Refuse(openError);
                var notShown = StudioSegmentCommands.CheckShown(_activeDocument, StudioEditor.Controller?.ActiveDocument);
                if (notShown != null) return Refuse(notShown);

                var runs = StudioSegmentCommands.SourceRuns(editor, pu.Properties.ParagraphUnitId, segId, out var textNodes);
                if (runs == null) return Refuse($"segment {req.Id} is not in Studio's editor");

                var point = SegmentStructurePlanner.LocateSplit(runs, req.SplitBefore, req.AllowInsideTags == true);
                if (point.Error != null) return Refuse(point.Error);

                var before = SegmentIdsInParagraph(paragraphId);
                _activeDocument.SetActiveSegmentPair(paragraphId, segId.Id, true);

                var refusal = StudioSegmentCommands.Split(editor, textNodes[point.RunIndex], point.Offset);
                if (refusal != null) return Refuse(refusal);

                var after = SegmentIdsInParagraph(paragraphId);
                var added = after.Except(before).ToList();
                if (after.Contains(segId.Id) && added.Count == 0)
                    return Refuse("Studio did not split the segment (nothing changed). Split it in Studio by hand " +
                                  "(cursor in the source, Alt+Shift+T) to see why");

                _bridgeUnsavedWritesDoc = _activeDocument;
                // The two halves, in document order: whatever is new, plus the old id
                // if Studio kept it for the first half.
                var halves = after.Where(i => added.Contains(i) || i == segId.Id).ToList();
                var response = new BridgeSegmentStructureResponse
                {
                    Ok = true,
                    Ids = halves.Select(i => paragraphId + ":" + i).ToList(),
                    Note = $"Split segment {segId.Id} into {string.Join(" and ", halves)}: \"{point.FirstPart}\" | " +
                           $"\"{point.SecondPart}\". The existing target stays with the first part; translate both " +
                           "with update_segments (read them with get_segments first for their fp and tags). Studio's " +
                           "Undo (Ctrl+Z) reverses a split. The document is not saved.",
                };
                if (point.Note != null) response.Note += " " + point.Note;
                return response;
            }
            catch (Exception ex)
            {
                return Refuse("split failed: " + StudioSegmentCommands.Inner(ex).Message);
            }
        }

        private static BridgeSegmentStructureResponse Refuse(string why) =>
            new BridgeSegmentStructureResponse { Ok = false, Error = why };

        /// <summary>Runs <paramref name="call"/> on the UI thread when this is not it,
        /// and returns its answer; null when already on the UI thread.</summary>
        private BridgeSegmentStructureResponse OnUiThread(Func<BridgeSegmentStructureResponse> call)
        {
            var ctrl = _control?.Value;
            if (ctrl == null || ctrl.IsDisposed)
                return Refuse("ai assistant disposed");
            if (ctrl.InvokeRequired)
                return (BridgeSegmentStructureResponse)ctrl.Invoke(call);
            // The panel may have no handle yet, in which case InvokeRequired lies.
            // See Core/UiThread.
            if (UiThread.InvokeRequired && UiThread.IsAvailable)
                return UiThread.Invoke(call);
            return null;
        }

        /// <summary>The segment ids of one paragraph, in document order. One pass over
        /// the document's segment pairs (about as costly as get_segments' index).</summary>
        private List<string> SegmentIdsInParagraph(string paragraphId)
        {
            var ids = new List<string>();
            foreach (var pair in _activeDocument.SegmentPairs)
            {
                var pu = _activeDocument.GetParentParagraphUnit(pair)?.Properties?.ParagraphUnitId.Id;
                if (string.Equals(pu, paragraphId, StringComparison.Ordinal))
                    ids.Add(pair.Properties?.Id.Id ?? "");
                else if (ids.Count > 0)
                    break; // A paragraph's segments are contiguous.
            }
            return ids;
        }

        private static List<string> TagIdsOf(ISegment segment)
        {
            var ids = new List<string>();
            if (segment == null) return ids;
            foreach (var item in segment.AllSubItems)
            {
                var id = (item as IAbstractTag)?.TagProperties?.TagId.Id;
                if (id != null) ids.Add(id);
            }
            return ids;
        }

        private static bool HasGroupTag(ISegment segment)
        {
            if (segment == null) return false;
            foreach (var item in segment.AllSubItems)
            {
                var props = (item as IAbstractTag)?.TagProperties;
                if (props != null && string.Equals(props.GetMetaData("TagType"), "Group", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }
}
