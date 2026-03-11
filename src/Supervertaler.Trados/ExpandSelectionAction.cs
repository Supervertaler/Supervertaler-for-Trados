using System;
using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Licensing;

namespace Supervertaler.Trados
{
    /// <summary>
    /// Keyboard action: F2 expands the current partial text selection to full
    /// word boundaries. Works in whichever editor pane (source or target)
    /// currently has focus.
    ///
    /// Example: user selects "et recht" → F2 → "het rechtstreeks" is selected.
    ///
    /// Uses SendKeys to manipulate the selection:
    ///   1. Collapse selection to its left end (Left arrow)
    ///   2. Move cursor left by the expansion delta
    ///   3. Shift+Right to select the full expanded text
    /// </summary>
    [Action("TermLens_ExpandSelection", typeof(EditorController),
        Name = "TermLens: Expand selection to word boundaries",
        Description = "Expand the current partial text selection to encompass complete words")]
    [Shortcut(Keys.F2)]
    public class ExpandSelectionAction : AbstractAction
    {
        protected override void Execute()
        {
            if (!LicenseManager.Instance.HasTier1Access)
            {
                LicenseManager.ShowLicenseRequiredMessage();
                return;
            }

            try
            {
                var editorController = SdlTradosStudio.Application.GetController<EditorController>();
                var doc = editorController?.ActiveDocument;
                if (doc == null) return;

                var selection = doc.Selection;
                if (selection == null) return;

                string fullSource = doc.ActiveSegmentPair?.Source?.ToString() ?? "";
                string fullTarget = doc.ActiveSegmentPair?.Target?.ToString() ?? "";

                string srcSel = null;
                string tgtSel = null;
                try { srcSel = selection.Source?.ToString(); } catch { }
                try { tgtSel = selection.Target?.ToString(); } catch { }

                // Determine which side to expand.
                // A partial selection is one that is non-empty, differs from the
                // full segment text, and still has room to expand (not already at
                // word boundaries). Skip sides that are already expanded so we
                // correctly fall through to the other side.
                string fullText = null;
                string partialSel = null;

                if (!string.IsNullOrWhiteSpace(srcSel) && srcSel != fullSource)
                {
                    var srcExpanded = SelectionExpander.ExpandToWordBoundaries(fullSource, srcSel);
                    if (srcExpanded != srcSel && srcExpanded != srcSel.Trim())
                    {
                        fullText = fullSource;
                        partialSel = srcSel;
                    }
                }
                if (fullText == null && !string.IsNullOrWhiteSpace(tgtSel) && tgtSel != fullTarget)
                {
                    fullText = fullTarget;
                    partialSel = tgtSel;
                }

                if (fullText == null || partialSel == null) return;

                string expanded = SelectionExpander.ExpandToWordBoundaries(fullText, partialSel);
                if (expanded == partialSel || expanded == partialSel.Trim())
                    return; // already at word boundaries

                // Find positions in full text
                int selIdx = fullText.IndexOf(partialSel, StringComparison.Ordinal);
                if (selIdx < 0)
                    selIdx = fullText.IndexOf(partialSel, StringComparison.OrdinalIgnoreCase);
                if (selIdx < 0) return;

                int expIdx = fullText.IndexOf(expanded, StringComparison.Ordinal);
                if (expIdx < 0)
                    expIdx = fullText.IndexOf(expanded, StringComparison.OrdinalIgnoreCase);
                if (expIdx < 0) return;

                int leftDelta = selIdx - expIdx;        // chars to extend left
                int expandedLength = expanded.Length;    // total selection length

                // Send keystrokes to re-select the expanded range.
                // Step 1: collapse current selection to its LEFT end
                SendKeys.SendWait("{LEFT}");

                // Step 2: move cursor further left by the expansion delta
                if (leftDelta > 0)
                    SendKeys.SendWait("{LEFT " + leftDelta + "}");

                // Step 3: select the full expanded text
                SendKeys.SendWait("+{RIGHT " + expandedLength + "}");
            }
            catch
            {
                // Silently handle — selection manipulation may fail
            }
        }
    }
}
