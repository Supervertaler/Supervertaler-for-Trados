using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using TermLens.Models;

namespace TermLens.Controls
{
    /// <summary>
    /// Confirmation dialog for adding a new term to one or more write termbases.
    /// Pre-populated with selected source/target text from the Trados editor.
    /// </summary>
    public class AddTermDialog : Form
    {
        private TextBox _txtSource;
        private TextBox _txtTarget;
        private TextBox _txtDefinition;
        private Button _btnAdd;
        private long _termId = -1;

        /// <summary>The (possibly edited) source term.</summary>
        public string SourceTerm => _txtSource.Text.Trim();

        /// <summary>The (possibly edited) target term.</summary>
        public string TargetTerm => _txtTarget.Text.Trim();

        /// <summary>Optional definition entered by the user.</summary>
        public string Definition => _txtDefinition.Text.Trim();

        /// <summary>Database row ID of the term being edited, or -1 for add mode.</summary>
        public long TermId => _termId;

        /// <summary>True when editing an existing term, false when adding a new one.</summary>
        public bool IsEditMode => _termId >= 0;

        public AddTermDialog(string sourceTerm, string targetTerm, List<TermbaseInfo> writeTermbases)
        {
            Text = "Add Term to Termbase";
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(420, 260);
            BackColor = Color.White;

            int y = 16;
            int inputWidth = ClientSize.Width - 32;

            // Source term
            Controls.Add(new Label
            {
                Text = "Source term:",
                Location = new Point(16, y),
                AutoSize = true,
                ForeColor = Color.FromArgb(80, 80, 80)
            });
            y += 20;

            _txtSource = new TextBox
            {
                Text = sourceTerm ?? "",
                Location = new Point(16, y),
                Width = inputWidth,
                BackColor = Color.FromArgb(250, 250, 250)
            };
            Controls.Add(_txtSource);
            y += 30;

            // Target term
            Controls.Add(new Label
            {
                Text = "Target term:",
                Location = new Point(16, y),
                AutoSize = true,
                ForeColor = Color.FromArgb(80, 80, 80)
            });
            y += 20;

            _txtTarget = new TextBox
            {
                Text = targetTerm ?? "",
                Location = new Point(16, y),
                Width = inputWidth,
                BackColor = Color.FromArgb(250, 250, 250)
            };
            Controls.Add(_txtTarget);
            y += 30;

            // Definition (optional)
            Controls.Add(new Label
            {
                Text = "Definition (optional):",
                Location = new Point(16, y),
                AutoSize = true,
                ForeColor = Color.FromArgb(80, 80, 80)
            });
            y += 20;

            _txtDefinition = new TextBox
            {
                Location = new Point(16, y),
                Width = inputWidth,
                BackColor = Color.FromArgb(250, 250, 250)
            };
            Controls.Add(_txtDefinition);
            y += 34;

            // Termbase info label
            string tbText;
            if (writeTermbases != null && writeTermbases.Count > 0)
            {
                if (writeTermbases.Count == 1)
                    tbText = $"Will be added to: {writeTermbases[0].Name} ({writeTermbases[0].SourceLang} \u2192 {writeTermbases[0].TargetLang})";
                else
                {
                    var names = new List<string>();
                    foreach (var tb in writeTermbases) names.Add(tb.Name);
                    tbText = "Will be added to: " + string.Join(", ", names);
                }
            }
            else
            {
                tbText = "No write termbase configured.";
            }
            Controls.Add(new Label
            {
                Text = tbText,
                Location = new Point(16, y),
                AutoSize = true,
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(100, 100, 100)
            });

            // Separator
            Controls.Add(new Label
            {
                Location = new Point(16, ClientSize.Height - 50),
                Width = ClientSize.Width - 32,
                Height = 1,
                BorderStyle = BorderStyle.Fixed3D
            });

            // Buttons
            _btnAdd = new Button
            {
                Text = "Add",
                DialogResult = DialogResult.OK,
                Location = new Point(ClientSize.Width - 170, ClientSize.Height - 38),
                Width = 75,
                FlatStyle = FlatStyle.System,
                Enabled = writeTermbases != null && writeTermbases.Count > 0
            };
            Controls.Add(_btnAdd);

            var btnCancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(ClientSize.Width - 88, ClientSize.Height - 38),
                Width = 75,
                FlatStyle = FlatStyle.System
            };
            Controls.Add(btnCancel);

            AcceptButton = _btnAdd;
            CancelButton = btnCancel;
        }

        /// <summary>
        /// Edit-mode constructor. Pre-fills the dialog with an existing term's data
        /// and changes the title/button text to reflect editing rather than adding.
        /// </summary>
        public AddTermDialog(TermEntry existingEntry, TermbaseInfo termbase)
            : this(existingEntry.SourceTerm, existingEntry.TargetTerm,
                  termbase != null ? new List<TermbaseInfo> { termbase } : new List<TermbaseInfo>())
        {
            _termId = existingEntry.Id;

            // Override title and button text for edit mode
            Text = "Edit Term";
            _btnAdd.Text = "Save";
            _btnAdd.Enabled = true;

            // Pre-fill definition if present
            _txtDefinition.Text = existingEntry.Definition ?? "";
        }
    }
}
