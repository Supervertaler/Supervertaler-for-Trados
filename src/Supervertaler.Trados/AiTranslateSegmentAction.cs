using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Sdl.TranslationStudioAutomation.IntegrationApi.Presentation.DefaultLocations;

namespace Supervertaler.Trados
{
    /// <summary>
    /// Editor action: Ctrl+Alt+A translates the active segment using the configured AI provider.
    /// Also appears in the right-click context menu in the editor.
    /// </summary>
    [Action("Supervertaler_AiTranslateSegment", typeof(EditorController),
        Name = "AI Translate Current Segment",
        Description = "Translate the active segment using the configured AI provider")]
    [ActionLayout(
        typeof(TranslationStudioDefaultContextMenus.EditorDocumentContextMenuLocation), 9,
        DisplayType.Default, "", true)]
    [Shortcut(Keys.Control | Keys.Alt | Keys.A)]
    public class AiTranslateSegmentAction : AbstractAction
    {
        protected override void Execute()
        {
            TermLensEditorViewPart.HandleAiTranslateSegment();
        }
    }
}
