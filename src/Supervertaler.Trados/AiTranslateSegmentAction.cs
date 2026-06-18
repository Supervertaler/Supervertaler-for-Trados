using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Supervertaler.Trados.Licensing;

namespace Supervertaler.Trados
{
    /// <summary>
    /// Legacy duplicate of <see cref="TranslateActiveSegmentAction"/> (Ctrl+T).
    /// Kept REGISTERED (it must never be deleted — Studio instantiates every cached
    /// command-bar action on startup, and a missing type crashes it) but given no
    /// <c>[ActionLayout]</c>, so it is not placed in the editor right-click menu.
    ///
    /// IMPORTANT: Studio reads action layouts from the static plugin.xml manifest,
    /// NOT from these attributes. The manifest is hand-maintained, so its matching
    /// &lt;extension&gt; must ALSO keep this action's &lt;extensionAttribute&gt;
    /// (ActionAttribute) and carry an EMPTY, self-closing
    /// &lt;auxiliaryExtensionAttributes /&gt; element — never the element removed
    /// entirely, or the shortcut-cache loader NPEs on startup.
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
