using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Sdl.TranslationStudioAutomation.IntegrationApi.Presentation.DefaultLocations;
using Supervertaler.Trados.Licensing;

namespace Supervertaler.Trados
{
    /// <summary>
    /// Editor action: Ctrl+Alt+P (and the editor's right-click menu) has the AI
    /// proofread the active segment and offers its correction in a small window.
    /// See Core/SegmentProofread and HandleProofreadActiveSegment.
    ///
    /// Ctrl+Alt+P: P for proofread, beside Ctrl+Alt+F (FuzzyFixer) and Ctrl+Alt+G
    /// (AutoTagger). No other Supervertaler action has it; Alt+P is TermPicker.
    /// Not checked against Studio's own defaults, and not tried in Studio. On a
    /// keyboard layout where AltGr+P types a character (US-International: o with
    /// a diaeresis), Ctrl+Alt+P is that character's key, as Ctrl+Alt+A, N and T
    /// already are for other actions here.
    ///
    /// The action, its menu place and its key are also in
    /// Supervertaler.Trados.plugin.xml: Studio registers them from the manifest,
    /// not from these attributes. Change both together.
    /// </summary>
    [Action("Supervertaler_ProofreadActiveSegment", typeof(EditorController),
        Name = "Proofread active segment",
        Description = "Ask the AI to proofread the active segment and offer its correction, without leaving the grid")]
    [ActionLayout(
        typeof(TranslationStudioDefaultContextMenus.EditorDocumentContextMenuLocation), 10,
        DisplayType.Default, "", true)]
    [Shortcut(Keys.Control | Keys.Alt | Keys.P)]
    public class ProofreadActiveSegmentAction : AbstractAction
    {
        protected override void Execute()
        {
            if (!LicenseManager.Instance.HasAssistantAccess)
            {
                LicenseManager.ShowUpgradeMessage();
                return;
            }

            AiAssistantViewPart.HandleProofreadActiveSegment();
        }
    }
}
