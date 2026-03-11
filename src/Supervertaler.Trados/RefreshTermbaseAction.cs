using System;
using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Supervertaler.Trados.Licensing;

namespace Supervertaler.Trados
{
    /// <summary>
    /// Keyboard action: F5 reloads the termbase from disk and refreshes the
    /// TermLens display for the current segment. Useful after external edits
    /// or as a manual fallback if the panel ever shows stale data.
    /// </summary>
    [Action("TermLens_RefreshTermbase", typeof(EditorController),
        Name = "TermLens: Refresh termbases",
        Description = "Reload all termbases from disk and refresh the TermLens display")]
    [Shortcut(Keys.F5)]
    public class RefreshTermbaseAction : AbstractAction
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
                TermLensEditorViewPart.NotifyTermAdded();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to refresh termbases: {ex.Message}",
                    "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
