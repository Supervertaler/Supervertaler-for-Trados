using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Supervertaler.Trados.Licensing;

namespace Supervertaler.Trados
{
    /// <summary>
    /// Base class for Alt+digit term insertion shortcuts.
    /// Each subclass maps a single digit (0-9) to a keyboard shortcut.
    /// No ActionLayout — these are keyboard-only actions (no context menu).
    /// </summary>
    public abstract class TermInsertDigitActionBase : AbstractAction
    {
        protected abstract int Digit { get; }

        protected override void Execute()
        {
            if (!LicenseManager.Instance.HasTier1Access)
            {
                LicenseManager.ShowLicenseRequiredMessage();
                return;
            }

            TermLensEditorViewPart.HandleDigitPress(Digit);
        }
    }

    [Action("TermLens_InsertDigit0", typeof(EditorController),
        Name = "TermLens: Insert term digit 0",
        Description = "Insert term 10 (or second digit of two-digit chord)")]
    [Shortcut(Keys.Alt | Keys.D0)]
    public class TermInsertDigit0Action : TermInsertDigitActionBase
    {
        protected override int Digit => 0;
    }

    [Action("TermLens_InsertDigit1", typeof(EditorController),
        Name = "TermLens: Insert term 1",
        Description = "Insert term 1 or start two-digit chord")]
    [Shortcut(Keys.Alt | Keys.D1)]
    public class TermInsertDigit1Action : TermInsertDigitActionBase
    {
        protected override int Digit => 1;
    }

    [Action("TermLens_InsertDigit2", typeof(EditorController),
        Name = "TermLens: Insert term 2",
        Description = "Insert term 2 or start two-digit chord")]
    [Shortcut(Keys.Alt | Keys.D2)]
    public class TermInsertDigit2Action : TermInsertDigitActionBase
    {
        protected override int Digit => 2;
    }

    [Action("TermLens_InsertDigit3", typeof(EditorController),
        Name = "TermLens: Insert term 3",
        Description = "Insert term 3 or start two-digit chord")]
    [Shortcut(Keys.Alt | Keys.D3)]
    public class TermInsertDigit3Action : TermInsertDigitActionBase
    {
        protected override int Digit => 3;
    }

    [Action("TermLens_InsertDigit4", typeof(EditorController),
        Name = "TermLens: Insert term 4",
        Description = "Insert term 4 or start two-digit chord")]
    [Shortcut(Keys.Alt | Keys.D4)]
    public class TermInsertDigit4Action : TermInsertDigitActionBase
    {
        protected override int Digit => 4;
    }

    [Action("TermLens_InsertDigit5", typeof(EditorController),
        Name = "TermLens: Insert term 5",
        Description = "Insert term 5 or start two-digit chord")]
    [Shortcut(Keys.Alt | Keys.D5)]
    public class TermInsertDigit5Action : TermInsertDigitActionBase
    {
        protected override int Digit => 5;
    }

    [Action("TermLens_InsertDigit6", typeof(EditorController),
        Name = "TermLens: Insert term 6",
        Description = "Insert term 6 or start two-digit chord")]
    [Shortcut(Keys.Alt | Keys.D6)]
    public class TermInsertDigit6Action : TermInsertDigitActionBase
    {
        protected override int Digit => 6;
    }

    [Action("TermLens_InsertDigit7", typeof(EditorController),
        Name = "TermLens: Insert term 7",
        Description = "Insert term 7 or start two-digit chord")]
    [Shortcut(Keys.Alt | Keys.D7)]
    public class TermInsertDigit7Action : TermInsertDigitActionBase
    {
        protected override int Digit => 7;
    }

    [Action("TermLens_InsertDigit8", typeof(EditorController),
        Name = "TermLens: Insert term 8",
        Description = "Insert term 8 or start two-digit chord")]
    [Shortcut(Keys.Alt | Keys.D8)]
    public class TermInsertDigit8Action : TermInsertDigitActionBase
    {
        protected override int Digit => 8;
    }

    [Action("TermLens_InsertDigit9", typeof(EditorController),
        Name = "TermLens: Insert term 9",
        Description = "Insert term 9 or start two-digit chord")]
    [Shortcut(Keys.Alt | Keys.D9)]
    public class TermInsertDigit9Action : TermInsertDigitActionBase
    {
        protected override int Digit => 9;
    }
}
