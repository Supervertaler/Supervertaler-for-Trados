using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;

namespace Supervertaler.Trados.Core
{
    /// <summary>One file of GET /v1/unsegmented-text (MCP check_unsegmented_text).</summary>
    [DataContract]
    public class BridgeUnsegmentedFile
    {
        [DataMember(Name = "file", Order = 0)] public string File { get; set; }
        [DataMember(Name = "checked", Order = 1)] public bool Checked { get; set; }
        [DataMember(Name = "segments", Order = 2)] public int Segments { get; set; }
        [DataMember(Name = "stretches", Order = 3)] public int Stretches { get; set; }
        [DataMember(Name = "paragraphsNotSegmented", Order = 4, EmitDefaultValue = false)] public int ParagraphsNotSegmented { get; set; }
        [DataMember(Name = "error", Order = 5, EmitDefaultValue = false)] public string Error { get; set; }
    }

    /// <summary>One stretch of source text outside every segment.</summary>
    [DataContract]
    public class BridgeUnsegmentedHit
    {
        [DataMember(Name = "file", Order = 0)] public string File { get; set; }
        [DataMember(Name = "location", Order = 1)] public string Location { get; set; }
        [DataMember(Name = "segmentBefore", Order = 2, EmitDefaultValue = false)] public string SegmentBefore { get; set; }
        [DataMember(Name = "segmentAfter", Order = 3, EmitDefaultValue = false)] public string SegmentAfter { get; set; }
        /// <summary>The nearest segment's id ("paragraphUnitId:segmentId"), for go_to_segment.</summary>
        [DataMember(Name = "nearestSegmentId", Order = 4, EmitDefaultValue = false)] public string NearestSegmentId { get; set; }
        [DataMember(Name = "text", Order = 5)] public string Text { get; set; }
    }

    [DataContract]
    public class BridgeUnsegmentedResponse
    {
        [DataMember(Name = "available", Order = 0)] public bool Available { get; set; }
        [DataMember(Name = "total", Order = 1)] public int Total { get; set; }
        [DataMember(Name = "files", Order = 2, EmitDefaultValue = false)] public List<BridgeUnsegmentedFile> Files { get; set; }
        [DataMember(Name = "stretches", Order = 3, EmitDefaultValue = false)] public List<BridgeUnsegmentedHit> Stretches { get; set; }
        [DataMember(Name = "truncated", Order = 4, EmitDefaultValue = false)] public bool Truncated { get; set; }
        /// <summary>Paragraphs with text but no segment: listed apart, because they are usually deliberate.</summary>
        [DataMember(Name = "paragraphsNotSegmentedTotal", Order = 5, EmitDefaultValue = false)] public int ParagraphsNotSegmentedTotal { get; set; }
        [DataMember(Name = "paragraphsNotSegmented", Order = 6, EmitDefaultValue = false)] public List<BridgeUnsegmentedHit> ParagraphsNotSegmented { get; set; }
        [DataMember(Name = "note", Order = 7, EmitDefaultValue = false)] public string Note { get; set; }
    }

    public sealed partial class SupervertalerBridge
    {
        /// <summary>
        /// GET /v1/unsegmented-text (MCP check_unsegmented_text): every stretch of
        /// source text outside any segment in the files of the open document.
        /// Runs on this listener thread, not the UI thread - it only reads files
        /// on disk, re-checking those saved since their last check.
        /// </summary>
        private void HandleUnsegmentedText(HttpListenerContext context)
        {
            int limit = 100;
            int asked;
            if (int.TryParse(QueryUtf8(context.Request)["limit"], out asked) && asked > 0)
                limit = Math.Min(asked, 500);

            BridgeUnsegmentedResponse response;
            try
            {
                bool attached;
                var results = UnsegmentedTextMonitor.CheckActiveDocumentNow(out attached);
                response = BuildUnsegmentedResponse(results, attached, limit);
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"[SupervertalerBridge] unsegmented-text threw: {ex.Message}");
                response = new BridgeUnsegmentedResponse { Available = false, Note = "check failed: " + ex.Message };
            }
            WriteJson(context, 200, response);
        }

        internal static BridgeUnsegmentedResponse BuildUnsegmentedResponse(
            List<UnsegmentedFileResult> results, bool attached, int limit)
        {
            if (!attached)
                return new BridgeUnsegmentedResponse
                {
                    Available = false,
                    Note = "The check is not running in this Studio (the TermLens panel has not started)."
                };
            if (results.Count == 0)
                return new BridgeUnsegmentedResponse
                {
                    Available = false,
                    Note = "No document is open in the Trados editor."
                };

            var response = new BridgeUnsegmentedResponse
            {
                Available = true,
                Total = results.Sum(r => r.HitCount),
                Files = results.Select(r => new BridgeUnsegmentedFile
                {
                    File = UnsegmentedTextMonitor.DisplayName(r.FilePath),
                    Checked = r.Ok,
                    Segments = r.Segments,
                    Stretches = r.HitCount,
                    ParagraphsNotSegmented = r.ParagraphCount,
                    Error = r.Ok ? null : r.Error
                }).ToList(),
                Stretches = results.SelectMany(r => r.Hits).Take(limit).Select(ToBridge).ToList(),
                ParagraphsNotSegmentedTotal = results.Sum(r => r.ParagraphCount)
            };
            response.Truncated = response.Stretches.Count < response.Total;
            if (response.ParagraphsNotSegmentedTotal > 0)
                response.ParagraphsNotSegmented = results.SelectMany(r => r.Paragraphs).Take(limit).Select(ToBridge).ToList();

            var notes = new List<string>();
            if (response.Total == 0)
                notes.Add("No source text outside segments" +
                          (results.All(r => r.Ok) ? "." : " in the files that could be read – see 'files' for the others."));
            else
                notes.Add("Trados left the text in 'stretches' outside every segment: it is not in the editor, QA " +
                          "checks and the word count skip it, and the exported file keeps it untranslated. It " +
                          "cannot be fixed from the editor – tell the user, who can translate it in the exported " +
                          "file or fix the source file and prepare it again. go_to_segment with nearestSegmentId " +
                          "shows the spot." +
                          (response.Truncated ? $" Only {response.Stretches.Count} of the {response.Total} are listed." : ""));
            if (response.ParagraphsNotSegmentedTotal > 0)
                notes.Add($"{response.ParagraphsNotSegmentedTotal} paragraph(s) have text but no segment at all. " +
                          "These are usually deliberate – typically a file made from another tool's bilingual " +
                          "format (memoQ, XLIFF), whose locked, hidden or excluded rows arrive this way – so " +
                          "mention them without alarm and let the user judge whether any should have been translated.");
            if (results.Any(r => r.Ok && r.Segments == 0))
                notes.Add("A file with no segments at all has not been prepared, so nothing is reported for it.");
            notes.Add("Checked the last saved state of each file.");
            response.Note = string.Join(" ", notes);
            return response;
        }

        private static BridgeUnsegmentedHit ToBridge(UnsegmentedHit h)
        {
            return new BridgeUnsegmentedHit
            {
                File = UnsegmentedTextMonitor.DisplayName(h.FilePath),
                Location = h.Location,
                SegmentBefore = h.SegmentBefore,
                SegmentAfter = h.SegmentAfter,
                NearestSegmentId = h.JumpSegmentId == null ? null : h.JumpParagraphUnitId + ":" + h.JumpSegmentId,
                Text = h.Text
            };
        }
    }
}
