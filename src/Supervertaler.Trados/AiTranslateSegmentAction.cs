using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Supervertaler.Trados.Licensing;

namespace Supervertaler.Trados
{
    /// <summary>
    /// Legacy duplicate of <see cref="TranslateActiveSegmentAction"/> (Ctrl+T).
    /// It is kept REGISTERED — but deliberately given no <c>[ActionLayout]</c> — so
    /// that it no longer appears in the editor right-click menu while its action id
    /// still resolves. Studio caches the editor command bar by action id and
    /// instantiates every cached action on startup; deleting this type entirely
    /// (as 4.20.57 did) makes that cached reference fail and crashes Studio before
    /// the editor loads. Registered-but-unplaced is the safe way to retire it.
    /// (It still shows in the keyboard-shortcuts editor — that list includes every
    /// registered action, and there is no SDK flag to hide it from there.)
    /// No default shortcut; redirects to the same batch-translate pipeline as Ctrl+T.
    /// </summary>
    [Action("Supervertaler_AiTranslateSegment", typeof(EditorController),
        Name = "AI translate current segment",
        Description = "Translate the active segment (same as Ctrl+T)")]
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
