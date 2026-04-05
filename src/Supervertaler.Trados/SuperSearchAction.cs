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
                var editorController = SdlTradosStudio.Application.GetController<EditorController>();
                var doc = editorController?.ActiveDocument;
                if (doc == null) return;

                // Try to get selected text from the active segment
                string selectedText = null;
                try
                {
                    var selection = doc.Selection;
                    if (selection != null)
                    {
                        // Try source selection first, then target
                        var sourceSelection = selection.Source?.ToString();
                        var targetSelection = selection.Target?.ToString();

                        selectedText = !string.IsNullOrWhiteSpace(targetSelection)
                            ? targetSelection.Trim()
                            : sourceSelection?.Trim();
                    }
                }
                catch { /* selection API may not be available in all contexts */ }

                // Activate the SuperSearch ViewPart panel (makes it visible even when auto-hidden/unpinned)
                try
                {
                    var viewPart = SdlTradosStudio.Application.GetController<SuperSearchViewPart>();
                    viewPart?.Activate();
                }
                catch { /* Activate may not be available in all Trados versions */ }

                // Set the search text and trigger search
                var control = SuperSearchViewPart.GetControl();
                if (control != null)
                {
                    control.SetSearchText(selectedText ?? "", autoSearch: !string.IsNullOrWhiteSpace(selectedText));
                }
            }
            catch { /* silently ignore errors */ }
        }
    }
}
