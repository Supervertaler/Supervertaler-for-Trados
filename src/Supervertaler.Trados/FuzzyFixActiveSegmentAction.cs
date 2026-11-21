using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Sdl.TranslationStudioAutomation.IntegrationApi.Presentation.DefaultLocations;
using Supervertaler.Trados.Licensing;

namespace Supervertaler.Trados
{
    /// <summary>
    /// Editor action: Ctrl+Alt+F (and the editor's right-click menu) asks the AI
    /// to repair the best fuzzy translation memory match for the active segment.
    /// See Core/FuzzyFixer and HandleFuzzyFixActiveSegment.
    ///
    /// Ctrl+Alt+F is Workbench's shortcut for the same thing. Among Studio 2022,
    /// 2024 and 2026's own defaults only the Alignment view claims it ("Confirm
    /// all alignments"), nothing in the Editor - read from the [Shortcut]
    /// attributes of each install's Sdl*.dll on 11 Nov 2026, not tried in Studio.
    /// Ctrl+Alt+R, the other obvious key, types the registered sign in the Editor
    /// of Studio 2022 and 2024.
    ///
    /// The action, its menu place and its key are also in
    /// Supervertaler.Trados.plugin.xml: Studio registers them from the manifest,
    /// not from these attributes. Change both together.
    /// </summary>
    [Action("Supervertaler_FuzzyFixActiveSegment", typeof(EditorController),
        Name = "FuzzyFixer: repair fuzzy match",
        Description = "Ask the AI to repair the best fuzzy TM match for the active segment, changing only what the source change requires")]
    // Right-click menu group 11, "AI on the active segment": see docs/design/context-menu-order.md
    [ActionLayout(
        typeof(TranslationStudioDefaultContextMenus.EditorDocumentContextMenuLocation), 11,
        DisplayType.Default, "", true)]
    [Shortcut(Keys.Control | Keys.Alt | Keys.F)]
    public class FuzzyFixActiveSegmentAction : AbstractAction
    {
        protected override void Execute()
        {
            if (!LicenseManager.Instance.HasAssistantAccess)
            {
                LicenseManager.ShowUpgradeMessage();
                return;
            }

            AiAssistantViewPart.HandleFuzzyFixActiveSegment();
        }
    }
}
