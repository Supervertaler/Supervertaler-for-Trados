using System;
using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Sdl.TranslationStudioAutomation.IntegrationApi.Presentation.DefaultLocations;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados
{
    /// <summary>
    /// Editor context-menu action: "Search the web".
    /// Takes the selected source or target text and opens it in every enabled
    /// SuperSearch web resource — IATE, Linguee, Reverso and the rest — using the
    /// project's own language pair, so there is nothing to type or configure.
    ///
    /// <para>Alt+W pairs with SuperSearch's own Alt+S — S searches your
    /// material, W searches the web.</para>
    ///
    /// <para>NOT Ctrl+Alt+L, which was the first choice and was wrong: that combo
    /// belongs to Supervertaler Workbench, which claims it as a GLOBAL hotkey for
    /// its own SuperLookup. A global hook fires wherever the user is typing, so a
    /// translator running Workbench alongside Trados — the normal case — would
    /// have triggered both at once. The standalone SuperLookup app's comment about
    /// "leaving Ctrl+Alt+L free" means reserved FOR Workbench, not available.</para>
    /// </summary>
    [Action("Supervertaler_WebSearch", typeof(EditorController),
        Name = "Search the web",
        Description = "Open the selected text in the enabled SuperSearch web resources")]
    [ActionLayout(
        typeof(TranslationStudioDefaultContextMenus.EditorDocumentContextMenuLocation), 11,
        DisplayType.Default, "", true)]
    [Shortcut(Keys.Alt | Keys.W)]
    public class WebSearchAction : AbstractAction
    {
        protected override void Execute()
        {
            try
            {
                var editorController = Core.StudioEditor.Controller;
                var doc = editorController?.ActiveDocument;
                if (doc == null) return;

                // Studio fills exactly one side of doc.Selection — whichever the
                // caret is in. Which side matters: a term taken from the target
                // is looked up in the target language, so the controller flips the
                // pair. Searching a Dutch word as if it were English finds nothing.
                string selectedText = null;
                bool fromTarget = false;
                try
                {
                    var selection = doc.Selection;
                    if (selection != null)
                    {
                        var sourceSelection = selection.Source?.ToString();
                        var targetSelection = selection.Target?.ToString();

                        fromTarget = !string.IsNullOrWhiteSpace(targetSelection);
                        selectedText = fromTarget
                            ? targetSelection.Trim()
                            : sourceSelection?.Trim();

                        // Studio often reports a PARTIAL selection — double-clicking
                        // "UITVINDING" can come back as "VINDING" — so expand to word
                        // boundaries against the segment, exactly as the term-add
                        // actions do. Looking up "vinding" instead of "uitvinding"
                        // silently returns the wrong results rather than none, which
                        // is the worse failure.
                        selectedText = ExpandSelection(doc, selectedText, fromTarget);
                    }
                }
                catch { /* selection API may not be available in all contexts */ }

                // Deliberately does NOT bring the SuperSearch panel up. Alt+W's
                // output is the browser window, which raises itself and carries
                // its own status line ("8 resource(s) · <term> · WebView2 …") plus
                // a tab per resource — so the panel had nothing to add, and
                // popping it open stole editor height for a result the user was
                // already reading somewhere else, often on another monitor.
                //
                // Alt+S still activates the panel: its results genuinely appear
                // there. GetControl() below returns the shared controller's
                // control, created on demand, so nothing here depends on the
                // panel having been shown.
                var control = SuperSearchViewPart.GetControl();
                if (control == null) return;

                // A null term leaves whatever is already in the boxes, so the
                // shortcut still works as "search again" with no selection.
                control.RunWebSearch(
                    string.IsNullOrWhiteSpace(selectedText) ? null : selectedText,
                    fromTarget);
            }
            catch { /* silently ignore errors */ }
        }

        /// <summary>
        /// Grows a partial selection out to whole words against the segment it
        /// came from.
        ///
        /// <para>Auto-expansion is suppressed for no-space scripts, honouring the
        /// side the term came from: expanding to the whitespace token in Korean
        /// or Japanese swallows an attached particle, and in Chinese it can
        /// swallow the whole segment.</para>
        /// </summary>
        private static string ExpandSelection(IStudioDocument doc, string selection, bool fromTarget)
        {
            if (string.IsNullOrWhiteSpace(selection)) return selection;

            try
            {
                var pair = doc.ActiveSegmentPair;
                if (pair == null) return selection;

                var segment = fromTarget ? pair.Target : pair.Source;
                if (segment == null) return selection;

                var fullText = SegmentTagHandler.GetFinalText(segment);
                if (string.IsNullOrEmpty(fullText)) return selection;

                var language = fromTarget
                    ? TermLensEditorViewPart.GetCurrentProjectTargetLanguage()
                    : TermLensEditorViewPart.GetCurrentProjectSourceLanguage();
                var autoExpand = !SettingsService.Current.ResolveSuffixTolerant(language);

                return SelectionExpander.ExpandToWordBoundaries(fullText, selection, autoExpand);
            }
            catch
            {
                return selection;
            }
        }
    }
}
