using System.Drawing;
using System.Windows.Forms;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// Simple dialog that asks for a prompt name when saving a generated prompt.
    /// </summary>
    internal class SavePromptDialog : Form
    {
        private TextBox _txtName;

        /// <summary>The prompt name entered by the user.</summary>
        public string PromptName => _txtName?.Text?.Trim() ?? "";

        public SavePromptDialog(string defaultName = "Custom Translation Prompt")
        {
            Text = "Save as Prompt";
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(400, 120);
            BackColor = Color.White;

            Controls.Add(new Label
            {
                Text = "Prompt name:",
                Location = new Point(16, 16),
                AutoSize = true,
                ForeColor = Color.FromArgb(80, 80, 80)
            });

            _txtName = new TextBox
            {
                Location = new Point(16, 38),
                Width = ClientSize.Width - 32,
                Text = defaultName ?? "Custom Translation Prompt",
                BackColor = Color.FromArgb(250, 250, 250)
            };
            _txtName.SelectAll();
            Controls.Add(_txtName);

            // Separator
            Controls.Add(new Label
            {
                Location = new Point(16, ClientSize.Height - 50),
                Width = ClientSize.Width - 32,
                Height = 1,
                BorderStyle = BorderStyle.Fixed3D
            });

            // Buttons — positioned relative to ClientSize like other working dialogs
            var btnOk = new Button
            {
                Text = "Save",
                DialogResult = DialogResult.OK,
                Location = new Point(ClientSize.Width - 170, ClientSize.Height - 38),
                Width = 75,
                FlatStyle = FlatStyle.System
            };
            Controls.Add(btnOk);
            AcceptButton = btnOk;

            var btnCancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(ClientSize.Width - 88, ClientSize.Height - 38),
                Width = 75,
                FlatStyle = FlatStyle.System
            };
            Controls.Add(btnCancel);
            CancelButton = btnCancel;
        }
    }
}
