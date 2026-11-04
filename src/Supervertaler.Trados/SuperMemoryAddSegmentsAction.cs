using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Sdl.Core.Globalization;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Sdl.TranslationStudioAutomation.IntegrationApi.Presentation.DefaultLocations;
using Supervertaler.Trados.Controls;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Licensing;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados
{
    /// <summary>
    /// Editor context menu action: "Add segments to SuperMemory".
    /// Copies a range of reviewed segments - source and translation - into a
    /// memory bank's brief.md as reference translations, so that a later part of
    /// the job follows them: translate and review the claims, then translate the
    /// description with the reviewed claims in every prompt.
    ///
    /// <para>Quick Add (<see cref="SuperMemoryQuickAddAction"/>) takes one term
    /// or one note; this takes a range. Until it existed the only way to put a
    /// range into a bank was an outside AI app through the MCP server.</para>
    ///
    /// <para><b>Ctrl+Alt+Shift+A</b>, which is how a multi-row selection reaches
    /// the dialog. Right-clicking in a segment's text moves the cursor there and
    /// Studio drops the selection, so from the context menu the range can only
    /// start at the active segment; the segment-number column's own menu, where
    /// the selection survives, is not public API (its location type is
    /// internal). Planned without a shortcut, then given one after the live test
    /// (Michael, 21 Oct 2026). Ctrl+Alt+Shift+M was the first choice, but that
    /// is Studio's Insert left-to-right mark. Keep this and the ShortcutAttribute
    /// in Supervertaler.Trados.plugin.xml in step: Studio binds keys from the
    /// manifest.</para>
    /// </summary>
    [Action("Supervertaler_SuperMemoryAddSegments", typeof(EditorController),
        Name = "Add segments to SuperMemory",
        Description = "Copy a range of reviewed segments into a memory bank as reference translations")]
    [ActionLayout(
        typeof(TranslationStudioDefaultContextMenus.EditorDocumentContextMenuLocation), 8,
        DisplayType.Default, "", false)]
    [Shortcut(Keys.Control | Keys.Alt | Keys.Shift | Keys.A)]
    public class SuperMemoryAddSegmentsAction : AbstractAction
    {
        protected override void Execute()
        {
            if (!LicenseManager.Instance.HasAssistantAccess)
            {
                LicenseManager.ShowUpgradeMessage();
                return;
            }

            var doc = Core.StudioEditor.Controller?.ActiveDocument;
            if (doc == null)
            {
                MessageBox.Show("Open a document in the editor first.", "Supervertaler – SuperMemory",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            List<ReferenceSegment> segments;
            Dictionary<string, int> keys;
            int fileCount;
            try
            {
                segments = Gather(doc, out keys, out fileCount);
            }
            catch (Exception ex)
            {
                // The document can be mid-way through opening or closing.
                MessageBox.Show("Could not read this document's segments: " + ex.Message + "\n\nTry again in a moment.",
                    "Supervertaler – SuperMemory", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var fileNote = ReferenceTranslations.NumberFiles(segments, fileCount);
            if (segments.Count == 0)
            {
                MessageBox.Show("This document has no segments to add.", "Supervertaler – SuperMemory",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // The range starts as the rows selected in the editor - click the
            // first row's number, Shift+click the last, then Ctrl+Alt+Shift+A -
            // or as the active segment. From the right-click menu it is always
            // the active segment: the right-click has dropped the selection.
            var first = -1;
            var last = -1;
            try
            {
                foreach (var pair in doc.GetSelectedSegmentPairs() ?? Enumerable.Empty<ISegmentPair>())
                {
                    if (!keys.TryGetValue(KeyOf(doc, pair), out var i)) continue;
                    if (first < 0 || i < first) first = i;
                    if (i > last) last = i;
                }
            }
            catch { /* the selection API can throw in some editor states */ }
            if (first < 0)
            {
                try
                {
                    var active = doc.ActiveSegmentPair;
                    if (active != null && keys.TryGetValue(KeyOf(doc, active), out var i)) first = last = i;
                }
                catch { }
            }
            if (first < 0) first = last = 0;

            // A start or end on a segment with no number (rare) borrows the nearest numbered one.
            var from = NumberedNear(segments, first, +1);
            var to = NumberedNear(segments, last, -1);

            var activeBank = SettingsService.Current?.AiSettings?.ActiveMemoryBankName;
            if (string.IsNullOrWhiteSpace(activeBank))
                activeBank = UserDataPath.DefaultMemoryBankName;

            Languages(doc, out var sourceLabel, out var targetLabel);

            using (var dlg = new SuperMemoryAddSegmentsDialog(segments, fileNote,
                from.File, Math.Max(1, from.Value), to.File, Math.Max(1, to.Value),
                sourceLabel, targetLabel, activeBank))
            {
                if (dlg.ShowDialog() != DialogResult.OK || dlg.Written == null) return;
                Confirm(dlg, activeBank);
            }
        }

        /// <summary>
        /// Every segment of the document, in document order: its number, file,
        /// status, and its text read only if it ends up in the range. One pass,
        /// on the UI thread, which is where the document may be read.
        /// </summary>
        private static List<ReferenceSegment> Gather(IStudioDocument doc, out Dictionary<string, int> keys,
            out int fileCount)
        {
            var segments = new List<ReferenceSegment>();
            keys = new Dictionary<string, int>(StringComparer.Ordinal);

            // The map get_segments uses; null in a one-file document, or when the
            // files of a merged one cannot be told apart.
            var fileOf = AiAssistantViewPart.ParagraphFileNames(doc, out fileCount);
            string file = null;

            foreach (var pair in doc.SegmentPairs)
            {
                if (pair == null) continue;
                string puId = null, number = null;
                var confirmed = false;
                try
                {
                    puId = doc.GetParentParagraphUnit(pair)?.Properties?.ParagraphUnitId.Id;
                    number = pair.Properties?.Id.Id;
                    var level = pair.Properties?.ConfirmationLevel ?? ConfirmationLevel.Unspecified;
                    confirmed = level == ConfirmationLevel.Translated
                        || level == ConfirmationLevel.ApprovedTranslation
                        || level == ConfirmationLevel.ApprovedSignOff;
                }
                catch { /* unreadable properties: listed without a number, so no range starts or ends on it */ }

                // A paragraph the map lacks (added since it was built) keeps the
                // previous segment's file: a document lists each file's segments
                // together. The same rule as the prompts' numbering.
                if (fileOf != null)
                {
                    var f = fileOf(puId);
                    if (f != null) file = f;
                }

                var p = pair;
                if (!string.IsNullOrEmpty(puId) && !string.IsNullOrEmpty(number))
                    keys[puId + ":" + number] = segments.Count;
                segments.Add(new ReferenceSegment
                {
                    File = fileOf != null ? file : null,
                    Number = string.IsNullOrWhiteSpace(number) ? (segments.Count + 1).ToString() : number,
                    Value = ReferenceTranslations.LeadingNumber(number),
                    Confirmed = confirmed,
                    Source = new Lazy<string>(() => SafeText(p.Source)),
                    Target = new Lazy<string>(() => SafeText(p.Target)),
                });
            }

            // A merged document whose first segments came before any mapped
            // paragraph: give them the first file seen, rather than none.
            if (fileOf != null)
            {
                var firstFile = segments.Select(s => s.File).FirstOrDefault(f => f != null);
                foreach (var s in segments)
                {
                    if (s.File != null) break;
                    s.File = firstFile;
                }
            }
            return segments;
        }

        /// <summary>Plain text, tracked changes accepted, tags left out.</summary>
        private static string SafeText(ISegment segment)
        {
            try { return SegmentTagHandler.GetFinalText(segment) ?? ""; }
            catch { return ""; }
        }

        private static string KeyOf(IStudioDocument doc, ISegmentPair pair)
        {
            try
            {
                var puId = doc.GetParentParagraphUnit(pair)?.Properties?.ParagraphUnitId.Id;
                return puId + ":" + pair.Properties?.Id.Id;
            }
            catch { return ""; }
        }

        /// <summary>The segment at <paramref name="index"/>, or the nearest one with a number in the given direction.</summary>
        private static ReferenceSegment NumberedNear(List<ReferenceSegment> segments, int index, int step)
        {
            for (var i = index; i >= 0 && i < segments.Count; i += step)
                if (segments[i].Value >= 1) return segments[i];
            for (var i = index; i >= 0 && i < segments.Count; i -= step)
                if (segments[i].Value >= 1) return segments[i];
            return segments[Math.Max(0, Math.Min(index, segments.Count - 1))];
        }

        private static void Languages(IStudioDocument doc, out string source, out string target)
        {
            source = null;
            target = null;
            try
            {
                var lang = doc.ActiveFile?.SourceFile?.Language;
                if (lang != null) source = LanguageUtils.ShortenLanguageName(lang.DisplayName);
            }
            catch { }
            try
            {
                var lang = doc.ActiveFile?.Language;
                if (lang != null) target = LanguageUtils.ShortenLanguageName(lang.DisplayName);
            }
            catch { }
        }

        private static void Confirm(SuperMemoryAddSegmentsDialog dlg, string activeBank)
        {
            var r = dlg.Written;
            var bank = r.Bank;
            var msg = new StringBuilder();
            msg.AppendLine("✓  Added " + dlg.WrittenSegments.ToString("N0", CultureInfo.InvariantCulture) + " segment"
                + (dlg.WrittenSegments == 1 ? "" : "s") + " (about " + dlg.WrittenTokens.ToString("N0", CultureInfo.InvariantCulture)
                + " tokens) to brief.md in memory bank “" + bank + "”, under “## "
                + ReferenceTranslations.Heading + "”.");
            if (dlg.ReplacedExisting)
                msg.AppendLine("   They replace the reference translations that were there.");
            msg.AppendLine();

            if (MemoryBanks.IsSharedName(bank))
                msg.AppendLine("_shared is loaded with every memory bank, so they now go with every translation, for every client.");
            else if (string.Equals(bank, activeBank, StringComparison.OrdinalIgnoreCase))
                msg.AppendLine("They now go with every translation from this bank, until you add another range or delete the section.");
            else
                msg.AppendLine("This is not the active memory bank: they go with translations once you switch to it on the SuperMemory bar.");

            msg.AppendLine();
            msg.AppendLine("To read or edit them: Settings → Library → SuperMemory → " + bank + " → brief.md.");
            if (!string.IsNullOrEmpty(r.Backup))
            {
                msg.AppendLine();
                msg.AppendLine("The previous brief.md is saved as:");
                msg.AppendLine(r.Backup);
            }

            MessageBox.Show(msg.ToString().TrimEnd(), "Supervertaler – SuperMemory",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
