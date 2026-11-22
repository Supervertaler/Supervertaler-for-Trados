using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Supervertaler.Trados.Licensing;

namespace Supervertaler.Trados
{
    /// <summary>
    /// "Insert EverTM match 1" to 9, from the editor without leaving it: Alt+Shift+1–9 (Michael, 9 Nov 2026, kept on
    /// 22 Nov). Keyboard-only Studio actions, so they can be given other keys in Studio's own Keyboard Shortcuts.
    ///
    /// <para>Studio has its own actions on these keys in 2022, 2024 and 2026: "insert special character 1–9" from the
    /// Editor's Insert Symbol list (Sdl.TranslationStudio.Editor.Symbols). That list is empty until Insert Symbol is
    /// used, and Studio's actions are unavailable while it is. Someone who uses that list can move either set of
    /// actions to other keys in Studio's Keyboard Shortcuts. (The shortcut values are also in
    /// Supervertaler.Trados.plugin.xml, which is what Studio reads.)</para>
    /// </summary>
    public abstract class EverTmInsertMatchActionBase : AbstractAction
    {
        protected abstract int Number { get; }

        protected override void Execute()
        {
            if (!LicenseManager.Instance.HasTier1Access)
            {
                LicenseManager.ShowLicenseRequiredMessage();
                return;
            }
            EverTmMatchesViewPart.InsertNumber(Number);
        }
    }

    [Action("EverTM_InsertMatch1", typeof(EditorController), Name = "EverTM: Insert match 1", Description = "Insert EverTM match 1 from the EverTM Matches pane")]
    [Shortcut(Keys.Alt | Keys.Shift | Keys.D1)]
    public class EverTmInsertMatch1Action : EverTmInsertMatchActionBase { protected override int Number => 1; }

    [Action("EverTM_InsertMatch2", typeof(EditorController), Name = "EverTM: Insert match 2", Description = "Insert EverTM match 2 from the EverTM Matches pane")]
    [Shortcut(Keys.Alt | Keys.Shift | Keys.D2)]
    public class EverTmInsertMatch2Action : EverTmInsertMatchActionBase { protected override int Number => 2; }

    [Action("EverTM_InsertMatch3", typeof(EditorController), Name = "EverTM: Insert match 3", Description = "Insert EverTM match 3 from the EverTM Matches pane")]
    [Shortcut(Keys.Alt | Keys.Shift | Keys.D3)]
    public class EverTmInsertMatch3Action : EverTmInsertMatchActionBase { protected override int Number => 3; }

    [Action("EverTM_InsertMatch4", typeof(EditorController), Name = "EverTM: Insert match 4", Description = "Insert EverTM match 4 from the EverTM Matches pane")]
    [Shortcut(Keys.Alt | Keys.Shift | Keys.D4)]
    public class EverTmInsertMatch4Action : EverTmInsertMatchActionBase { protected override int Number => 4; }

    [Action("EverTM_InsertMatch5", typeof(EditorController), Name = "EverTM: Insert match 5", Description = "Insert EverTM match 5 from the EverTM Matches pane")]
    [Shortcut(Keys.Alt | Keys.Shift | Keys.D5)]
    public class EverTmInsertMatch5Action : EverTmInsertMatchActionBase { protected override int Number => 5; }

    [Action("EverTM_InsertMatch6", typeof(EditorController), Name = "EverTM: Insert match 6", Description = "Insert EverTM match 6 from the EverTM Matches pane")]
    [Shortcut(Keys.Alt | Keys.Shift | Keys.D6)]
    public class EverTmInsertMatch6Action : EverTmInsertMatchActionBase { protected override int Number => 6; }

    [Action("EverTM_InsertMatch7", typeof(EditorController), Name = "EverTM: Insert match 7", Description = "Insert EverTM match 7 from the EverTM Matches pane")]
    [Shortcut(Keys.Alt | Keys.Shift | Keys.D7)]
    public class EverTmInsertMatch7Action : EverTmInsertMatchActionBase { protected override int Number => 7; }

    [Action("EverTM_InsertMatch8", typeof(EditorController), Name = "EverTM: Insert match 8", Description = "Insert EverTM match 8 from the EverTM Matches pane")]
    [Shortcut(Keys.Alt | Keys.Shift | Keys.D8)]
    public class EverTmInsertMatch8Action : EverTmInsertMatchActionBase { protected override int Number => 8; }

    [Action("EverTM_InsertMatch9", typeof(EditorController), Name = "EverTM: Insert match 9", Description = "Insert EverTM match 9 from the EverTM Matches pane")]
    [Shortcut(Keys.Alt | Keys.Shift | Keys.D9)]
    public class EverTmInsertMatch9Action : EverTmInsertMatchActionBase { protected override int Number => 9; }
}
