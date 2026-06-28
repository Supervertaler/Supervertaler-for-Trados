using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Supervertaler.Trados.Licensing;

namespace Supervertaler.Trados
{
    /// <summary>
    /// Keyboard action: Ctrl-tap (press and release Ctrl alone) opens the
    /// floating TermLens popup – a borderless version of the docked TermLens
    /// panel for the active segment, designed for keyboard-only term selection.
    /// (Ctrl-tap is wired in TermLensEditorViewPart.Initialize via CtrlTapFilter,
    /// not via this attribute.)
    ///
    /// No default [Shortcut] – Ctrl+Alt+G now belongs to AutoTagger. This action
    /// has no default key but remains assignable via Trados' keyboard settings.
    /// </summary>
    [Action("TermLens_TermLensPopup", typeof(EditorController),
        Name = "TermLens: Show TermLens popup",
        Description = "Open a floating TermLens popup; cycle matches with arrow keys; Enter inserts")]
    public class TermLensPopupAction : AbstractAction
    {
        protected override void Execute()
        {
            if (!LicenseManager.Instance.HasTier1Access)
            {
                LicenseManager.ShowLicenseRequiredMessage();
                return;
            }

            TermLensEditorViewPart.HandleTermLensPopup();
        }
    }
}
