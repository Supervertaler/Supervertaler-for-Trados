using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Sdl.TranslationStudioAutomation.IntegrationApi.Presentation.DefaultLocations;
using Supervertaler.Trados.Licensing;
using Supervertaler.Trados.VoiceControl;

namespace Supervertaler.Trados
{
    /// <summary>
    /// The one-button face of voice commands: Ctrl+Alt+D (also in the editor
    /// context menu) toggles listening. First activation downloads the voice
    /// runtime automatically; after that the default command set is ready
    /// with zero configuration. Everything deeper lives behind the gear on
    /// the status strip (VoiceSettingsDialog).
    ///
    /// NOT Ctrl+Alt+V, which it used until 20.157. Supervertaler Workbench
    /// binds Ctrl+Alt+V to its own voice-command push-to-talk as an OS-level
    /// GLOBAL hotkey, so it fires whichever application is in front - including
    /// Trados. One press started both listeners, and because Workbench's is a
    /// hold and this one is a toggle, releasing the key stopped only Workbench's:
    /// this listener stayed latched on with nothing visible having switched it on.
    /// The Trados side moved because it is the newer binding with far fewer
    /// users' fingers trained on it.
    /// </summary>
    [Action("Supervertaler_VoiceControl", typeof(EditorController),
        Name = "Toggle voice commands",
        Description = "Start/stop hands-free voice commands (confirm, next segment, insert term…)")]
    // Right-click menu group 6, "Voice": see docs/design/context-menu-order.md
    [ActionLayout(
        typeof(TranslationStudioDefaultContextMenus.EditorDocumentContextMenuLocation), 6,
        DisplayType.Default, "", true)]
    [Shortcut(Keys.Control | Keys.Alt | Keys.D)]
    public class VoiceControlAction : AbstractAction
    {
        protected override void Execute()
        {
            if (!LicenseManager.Instance.HasTier1Access)
            {
                LicenseManager.ShowLicenseRequiredMessage();
                return;
            }

            VoiceControlManager.Instance.Toggle();
        }
    }
}
