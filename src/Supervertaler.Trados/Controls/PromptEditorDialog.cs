using System;
using System.Drawing;
using System.Windows.Forms;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// Modal dialog for creating or editing a prompt template.
    /// Shows name, description, domain, and content fields with variable reference.
    /// </summary>
    public class PromptEditorDialog : Form
    {
        private TextBox _txtName;
        private TextBox _txtDescription;
        private TextBox _txtDomain;
        private TextBox _txtContent;
        private Button _btnOK;
        private Button _btnCancel;
        private ContextMenuStrip _varMenu;

        private readonly PromptTemplate _prompt;
        private readonly bool _isNew;

        /// <summary>
        /// Creates a prompt editor dialog.
        /// </summary>
        /// <param name="prompt">The prompt to edit, or null to create a new one.</param>
        public PromptEditorDialog(PromptTemplate prompt = null)
        {
            _isNew = prompt == null;
            _prompt = prompt ?? new PromptTemplate();
            BuildUI();
            PopulateFromPrompt();
        }

        /// <summary>The edited prompt template (valid after DialogResult.OK).</summary>
        public PromptTemplate Result => _prompt;

        private void BuildUI()
        {
            Text = _isNew ? "New Prompt" : "Edit Prompt";
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(600, 520);
            MinimumSize = new Size(450, 400);
            BackColor = Color.White;

            var labelColor = Color.FromArgb(80, 80, 80);
            var y = 12;

            // ─── Name ─────────────────────────────────────
            var lblName = new Label
            {
                Text = "Name:",
                Location = new Point(12, y + 3),
                AutoSize = true,
                ForeColor = labelColor
            };
            _txtName = new TextBox
            {
                Location = new Point(100, y),
                Width = ClientSize.Width - 112,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(lblName);
            Controls.Add(_txtName);
            y += 30;

            // ─── Description ──────────────────────────────
            var lblDesc = new Label
            {
                Text = "Description:",
                Location = new Point(12, y + 3),
                AutoSize = true,
                ForeColor = labelColor
            };
            _txtDescription = new TextBox
            {
                Location = new Point(100, y),
                Width = ClientSize.Width - 112,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(lblDesc);
            Controls.Add(_txtDescription);
            y += 30;

            // ─── Domain/Category ──────────────────────────
            var lblDomain = new Label
            {
                Text = "Category:",
                Location = new Point(12, y + 3),
                AutoSize = true,
                ForeColor = labelColor
            };
            _txtDomain = new TextBox
            {
                Location = new Point(100, y),
                Width = 200,
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            Controls.Add(lblDomain);
            Controls.Add(_txtDomain);
            y += 34;

            // ─── Content label + variable hint ────────────
            var lblContent = new Label
            {
                Text = "Prompt content:",
                Location = new Point(12, y),
                AutoSize = true,
                ForeColor = labelColor,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            Controls.Add(lblContent);
            y += 20;

            var lblVars = new Label
            {
                Text = "Press Ctrl+, to insert a variable",
                Location = new Point(12, y),
                AutoSize = false,
                Height = 16,
                Width = ClientSize.Width - 24,
                ForeColor = Color.FromArgb(130, 130, 130),
                Font = new Font("Segoe UI", 7.5f, FontStyle.Italic),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(lblVars);
            y += 20;

            // ─── Content TextBox ──────────────────────────
            _txtContent = new TextBox
            {
                Location = new Point(12, y),
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9f),
                BackColor = Color.FromArgb(252, 252, 252),
                ForeColor = Color.FromArgb(40, 40, 40),
                WordWrap = true,
                AcceptsReturn = true,
                AcceptsTab = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };
            _txtContent.Width = ClientSize.Width - 24;
            _txtContent.Height = ClientSize.Height - y - 50;
            Controls.Add(_txtContent);

            // ─── Variable picker menu (Ctrl+,) ────────────
            _varMenu = new ContextMenuStrip { Font = new Font("Segoe UI", 9f) };
            void AddVar(string variable, string description)
            {
                var item = new ToolStripMenuItem($"{variable}  \u2014  {description}");
                item.Click += (s, e) => InsertVariable(variable);
                _varMenu.Items.Add(item);
            }
            AddVar("{{SOURCE_LANGUAGE}}", "Source language name (e.g. \"Dutch\")");
            AddVar("{{TARGET_LANGUAGE}}", "Target language name (e.g. \"English\")");
            _varMenu.Items.Add(new ToolStripSeparator());
            AddVar("{{SOURCE_SEGMENT}}", "Source text of the active segment");
            AddVar("{{TARGET_SEGMENT}}", "Target text of the active segment");
            AddVar("{{SELECTION}}", "Currently selected text in the editor");
            _varMenu.Items.Add(new ToolStripSeparator());
            AddVar("{{PROJECT_NAME}}", "Name of the active Trados project");
            AddVar("{{DOCUMENT_NAME}}", "Name of the active file");
            AddVar("{{SURROUNDING_SEGMENTS}}", "Context segments before and after the active segment");
            AddVar("{{PROJECT}}", "All source segments in the document");
            _varMenu.Items.Add(new ToolStripSeparator());
            AddVar("{{TM_MATCHES}}", "Translation memory fuzzy matches (\u226570%)");

            // ─── OK / Cancel ──────────────────────────────
            _btnOK = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Location = new Point(ClientSize.Width - 170, ClientSize.Height - 40),
                Width = 75,
                FlatStyle = FlatStyle.System,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            _btnOK.Click += OnOKClick;

            _btnCancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(ClientSize.Width - 88, ClientSize.Height - 40),
                Width = 75,
                FlatStyle = FlatStyle.System,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };

            AcceptButton = _btnOK;
            CancelButton = _btnCancel;
            Controls.Add(_btnOK);
            Controls.Add(_btnCancel);
        }

        private void PopulateFromPrompt()
        {
            _txtName.Text = _prompt.Name ?? "";
            _txtDescription.Text = _prompt.Description ?? "";
            _txtDomain.Text = _prompt.Domain ?? "";
            _txtContent.Text = _prompt.Content ?? "";

            if (_prompt.IsReadOnly)
            {
                _txtName.ReadOnly = true;
                _txtDescription.ReadOnly = true;
                _txtDomain.ReadOnly = true;
                _txtContent.ReadOnly = true;
                _btnOK.Enabled = false;
                Text += " (read-only)";
            }
        }

        private void OnOKClick(object sender, EventArgs e)
        {
            var name = _txtName.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show("Please enter a name for the prompt.",
                    "Prompt Editor", MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.None;
                return;
            }

            _prompt.Name = name;
            _prompt.Description = _txtDescription.Text.Trim();
            _prompt.Domain = _txtDomain.Text.Trim();
            _prompt.Content = _txtContent.Text;
        }

        private void ShowVarMenu()
        {
            var pt = _txtContent.GetPositionFromCharIndex(_txtContent.SelectionStart);
            pt.Y += _txtContent.Font.Height + 2;
            _varMenu.Show(_txtContent, pt);
        }

        private void InsertVariable(string variable)
        {
            var start = _txtContent.SelectionStart;
            _txtContent.Text = _txtContent.Text
                .Remove(start, _txtContent.SelectionLength)
                .Insert(start, variable);
            _txtContent.SelectionStart = start + variable.Length;
            _txtContent.SelectionLength = 0;
            _txtContent.Focus();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F1)
            {
                HelpSystem.OpenHelp(HelpSystem.Topics.SettingsPrompts);
                return true;
            }
            if (keyData == (Keys.Control | Keys.Oemcomma) && _txtContent.Focused)
            {
                ShowVarMenu();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
