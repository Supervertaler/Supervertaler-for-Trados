using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.TranslationStudioAutomation.IntegrationApi;

namespace TermLens
{
    /// <summary>
    /// Keyboard action: Ctrl+Shift+G opens the Term Picker dialog.
    /// Lists all matched terms for the current segment and lets the user
    /// select one to insert into the target segment.
    /// No context menu entry — keyboard-only.
    /// </summary>
    [Action("TermLens_TermPicker", typeof(EditorController),
        Name = "TermLens: Pick term to insert",
        Description = "Open a dialog to browse and insert matched terms")]
    [Shortcut(Keys.Control | Keys.Shift | Keys.G)]
    public class TermPickerAction : AbstractAction
    {
        protected override void Execute()
        {
            TermLensEditorViewPart.HandleTermPicker();
        }
    }
}
