using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// A text box for typing a filter into.
    ///
    /// <para>Escape empties it and is swallowed, so in a dialog it clears the
    /// filter instead of cancelling the dialog; only an Escape on an empty box
    /// goes on to the dialog. Enter and Down raise <see cref="MoveToResults"/>
    /// rather than reaching the dialog's OK button: in a settings window, Enter
    /// in a filter box closing the window would throw away whatever the user had
    /// just ticked.</para>
    ///
    /// <para>Works by key handling inside the box itself (ProcessCmdKey), which
    /// WinForms runs before the form's own Escape/Enter buttons.</para>
    /// </summary>
    public class FilterTextBox : TextBox
    {
        private string _cue;

        /// <summary>Raised on Enter or Down: the caller moves focus to the list it filters.</summary>
        public event EventHandler MoveToResults;

        /// <summary>Grey hint shown while the box is empty (.NET 4.8 TextBox has no PlaceholderText).</summary>
        public string Cue
        {
            get => _cue;
            set { _cue = value; ApplyCue(); }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyCue();
        }

        private const int EM_SETCUEBANNER = 0x1501;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private void ApplyCue()
        {
            // wParam 1: keep the hint while the box has focus, until something is typed.
            if (IsHandleCreated) SendMessage(Handle, EM_SETCUEBANNER, (IntPtr)1, _cue ?? "");
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape && TextLength > 0)
            {
                Clear();
                return true;
            }
            if ((keyData == Keys.Enter || keyData == Keys.Down) && MoveToResults != null)
            {
                MoveToResults(this, EventArgs.Empty);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
