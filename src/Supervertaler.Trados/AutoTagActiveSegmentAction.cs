using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Sdl.TranslationStudioAutomation.IntegrationApi.Presentation.DefaultLocations;
using Supervertaler.Trados.Licensing;

namespace Supervertaler.Trados
{
    /// <summary>
    /// Editor action: Ctrl+Alt+G asks the AI to place the active source segment's
    /// inline tags into the existing (tag-free) target translation, without
    /// changing any words. See AutoTagger / HandleAutoTagActiveSegment.
    /// </summary>
    [Action("Supervertaler_AutoTagActiveSegment", typeof(EditorController),
        Name = "Auto-tag active segment",
        Description = "Place the source segment's inline tags into the current translation using AI")]
    [ActionLayout(
        typeof(TranslationStudioDefaultContextMenus.EditorDocumentContextMenuLocation), 9,
        DisplayType.Default, "", true)]
    [Shortcut(Keys.Control | Keys.Alt | Keys.G)]
    public class AutoTagActiveSegmentAction : AbstractAction
    {
        protected override void Execute()
        {
            if (!LicenseManager.Instance.HasAssistantAccess)
            {
                LicenseManager.ShowUpgradeMessage();
                return;
            }

            AiAssistantViewPart.HandleAutoTagActiveSegment();
        }
    }
}
