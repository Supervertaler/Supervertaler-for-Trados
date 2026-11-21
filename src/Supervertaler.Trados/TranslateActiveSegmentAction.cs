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
    /// freeze Studio. (Existing users keep whatever they have already bound —
    /// Studio stores per-user shortcuts — so this default only changes fresh
    /// installs.)
    ///
    /// Alt+T is NOT free either, in Studio 2024 or 2026: Studio's own
    /// TranslationResultsToggleSourceTargetFocusAction ("Toggle between Source and
    /// Target in Translation Results View") is on it by default, and Studio's
    /// binding wins - pressing Alt+T just toggles the Translation Results window and
    /// this action never runs, silently (found 2026-09-26). Kept anyway, as with
    /// Alt+Q: that toggle is obscure, and the help's first-time setup table tells
    /// users to clear it. .dev/studio-alt-t-probe.ps1 lists Studio's Alt+letter
    /// defaults (Alt+C, Alt+G, Alt+T in both versions).
    /// </summary>
    [Action("Supervertaler_TranslateActiveSegment", typeof(EditorController),
        Name = "Translate active segment",
        Description = "Translate the active segment using the batch translate settings")]
    // Right-click menu group 11, "AI on the active segment": see docs/design/context-menu-order.md
    [ActionLayout(
        typeof(TranslationStudioDefaultContextMenus.EditorDocumentContextMenuLocation), 11,
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
