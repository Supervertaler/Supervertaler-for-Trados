using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Sdl.TranslationStudioAutomation.IntegrationApi.Presentation.DefaultLocations;
using Supervertaler.Trados.Licensing;

namespace Supervertaler.Trados
{
    /// <summary>
    /// Editor action: Alt+T translates the active segment using the batch translate
    /// settings (same provider, prompt, and termbase configuration).
    ///
    /// Default shortcut is Alt+T, NOT Ctrl+T. Ctrl+T is a Trados factory default
    /// ("Apply Translation Result"); binding this action there too made a single
    /// keypress fire both commands, which raced on the same segment and could
    /// freeze Studio. Alt+T is collision-free. (Existing users keep whatever they
    /// have already bound — Studio stores per-user shortcuts — so this default
    /// only changes fresh installs.)
    /// </summary>
    [Action("Supervertaler_TranslateActiveSegment", typeof(EditorController),
        Name = "Translate active segment",
        Description = "Translate the active segment using the batch translate settings")]
    [ActionLayout(
        typeof(TranslationStudioDefaultContextMenus.EditorDocumentContextMenuLocation), 8,
        DisplayType.Default, "", true)]
    [Shortcut(Keys.Alt | Keys.T)]
    public class TranslateActiveSegmentAction : AbstractAction
    {
        protected override void Execute()
        {
            if (!LicenseManager.Instance.HasAssistantAccess)
            {
                LicenseManager.ShowUpgradeMessage();
                return;
            }

            AiAssistantViewPart.HandleTranslateActiveSegment();
        }
    }
}
