using System;
using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Sdl.TranslationStudioAutomation.IntegrationApi.Presentation.DefaultLocations;

namespace Supervertaler.Trados
{
    /// <summary>
    /// Editor context menu action: "SuperSearch".
    /// Takes the selected source or target text and searches for it across all project files.
    /// Activates the SuperSearch ViewPart and triggers a search.
    /// </summary>
    [Action("Supervertaler_SuperSearch", typeof(EditorController),
        Name = "SuperSearch",
        Description = "Search for the selected text across all project files")]
    [ActionLayout(
        typeof(TranslationStudioDefaultContextMenus.EditorDocumentContextMenuLocation), 10,
        DisplayType.Default, "", true)]
    [Shortcut(Keys.Alt | Keys.S)]
    public class SuperSearchAction : AbstractAction
    {
        protected override void Execute()
        {
            try
            {
                var editorController = Core.StudioEditor.Controller;
                var doc = editorController?.ActiveDocument;
                if (doc == null) return;

                // Try to get selected text from the active segment.
                //
                // Studio fills exactly one side of doc.Selection - whichever the
                // caret is in - so a non-empty target string IS the signal that
                // the user selected in the target, and the term belongs in the
                // Tgt box. It used to be searched as source text, which finds
                // nothing: a target term does not appear on the source side
                // (issue #57).
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
                    }
                }
                catch { /* selection API may not be available in all contexts */ }

                // Activate the SuperSearch host (makes it visible even when
                // auto-hidden/unpinned) — either its own ViewPart or the
                // SuperSearch tab in the Supervertaler Assistant panel,
                // depending on the SuperSearchInAssistantTab setting.
                try
                {
                    if (SuperSearchViewPart.IsHostedInAssistantTab())
                    {
                        AiAssistantViewPart.ActivateSuperSearchTab();
                    }
                    else
                    {
                        var viewPart = SdlTradosStudio.Application.GetController<SuperSearchViewPart>();
                        viewPart?.Activate();
                    }
                }
                catch { /* Activate may not be available in all Trados versions */ }

                // Set the search text and trigger search
                var control = SuperSearchViewPart.GetControl();
                if (control != null)
                {
                    control.SetSearchText(selectedText ?? "",
                        autoSearch: !string.IsNullOrWhiteSpace(selectedText),
                        intoTargetBox: fromTarget);
                }
            }
            catch { /* silently ignore errors */ }
        }
    }
}
