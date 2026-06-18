using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Sdl.TranslationStudioAutomation.IntegrationApi.Presentation.DefaultLocations;
using Supervertaler.Trados.Licensing;

namespace Supervertaler.Trados
{
    /// <summary>
    /// Legacy action kept for backward compatibility with Trados's cached action registry.
    /// No default shortcut – Ctrl+T (TranslateActiveSegmentAction) is the primary shortcut.
    /// Redirects to the same batch-translate pipeline as Ctrl+T.
    /// </summary>
    [Action("Supervertaler_AiTranslateSegment", typeof(EditorController),
        Name = "AI translate current segment",
        Description = "Translate the active segment (same as Ctrl+T)")]
    [ActionLayout(
        typeof(TranslationStudioDefaultContextMenus.EditorDocumentContextMenuLocation), 9,
        DisplayType.Default, "", true)]
    public class AiTranslateSegmentAction : AbstractAction
    {
        protected override void Execute()
        {
            if (!LicenseManager.Instance.HasAssistantAccess)
            {
                LicenseManager.ShowUpgradeMessage();
                return;
            }

            // Redirect to the unified Ctrl+T pipeline
            AiAssistantViewPart.HandleTranslateActiveSegment();
        }
    }
}
