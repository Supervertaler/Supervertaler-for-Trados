using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Supervertaler.Trados.Licensing;

namespace Supervertaler.Trados
{
    /// <summary>
    /// Keyboard action: Ctrl+Alt+Down opens the Term Picker dialog.
    /// Lists all matched terms for the current segment and lets the user
    /// select one to insert into the target segment.
    /// No context menu entry — keyboard-only.
    /// </summary>
    [Action("TermLens_TermPicker", typeof(EditorController),
        Name = "TermLens: Pick term to insert",
        Description = "Open a dialog to browse and insert matched terms")]
    [Shortcut(Keys.Control | Keys.Alt | Keys.Down)]
    public class TermPickerAction : AbstractAction
    {
        protected override void Execute()
        {
            if (!LicenseManager.Instance.HasTier1Access)
            {
                LicenseManager.ShowLicenseRequiredMessage();
                return;
            }

            TermLensEditorViewPart.HandleTermPicker();
        }
    }
}
