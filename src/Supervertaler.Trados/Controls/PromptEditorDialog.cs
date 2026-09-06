using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// Creates or edits a prompt. Four things about the task and nothing else (#92):
    /// name, description, what happens when it is run, content.
    ///
    /// Membership of the QuickLauncher menu means one thing: the prompt lives in the
    /// QuickLauncher folder. The dialog no longer asks about it - the old
    /// "Show in QuickLauncher menu" box duplicated the folder, and the old Mode row
    /// (two tick boxes and a Default dropdown, one of them Workbench residue) was
    /// three controls for one decision. That decision is now one dropdown: send to
    /// the Assistant, copy to the clipboard, or ask each time. Hiding a built-in
    /// menu entry moved to the Library tree's right-click menu, beside the entry.
    ///
    /// Laid out with a TableLayoutPanel, not pixel coordinates, so the layout
    /// probe can check it and DPI cannot break it.
    /// </summary>
    public class PromptEditorDialog : Form
    {
        private const string RunAssistant = "Send to the AI Assistant";
        private const string RunClipboard = "Copy to the clipboard";
        private const string RunAsk = "Ask me each time";

        private TableLayoutPanel _layout;
        private TextBox _txtName;
        private TextBox _txtDescription;
        private Label _lblFolder;
        private TextBox _txtFolder;
        private Label _lblWhenRun;
        private ComboBox _cboWhenRun;
        private Label _lblContent;
        private TextBox _txtContent;
        private Label _lblNote;
        private Button _btnOK;
        private Button _btnCancel;
        private ContextMenuStrip _varMenu;

        private readonly PromptTemplate _prompt;
        private readonly bool _isNew;

        /// <summary>For the layout probe: a new, empty prompt.</summary>
        public PromptEditorDialog() : this(null) { }

        /// <param name="prompt">The prompt to edit, or null to create a new one.</param>
        public PromptEditorDialog(PromptTemplate prompt)
        {
            Icon = IconHelper.AppIcon;
            _isNew = prompt == null;
            _prompt = prompt ?? new PromptTemplate();
            BuildUI();
            PopulateFromPrompt();
        }

        /// <summary>The edited prompt template (valid after DialogResult.OK).</summary>
        public PromptTemplate Result => _prompt;

        private bool IsQuickLauncherFolder(string category)
        {
            var c = (category ?? "").Trim().Replace('\\', '/');
            return c.Equals("QuickLauncher", StringComparison.OrdinalIgnoreCase)
                || c.StartsWith("QuickLauncher/", StringComparison.OrdinalIgnoreCase);
        }

        private void BuildUI()
        {
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = _isNew ? "New Prompt" : "Edit Prompt";
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(640, 560);
            MinimumSize = new Size(480, 420);
            BackColor = Color.White;

            var labelColor = Color.FromArgb(80, 80, 80);
            Label L(string text) => new Label
            {
                Text = text, AutoSize = true, ForeColor = labelColor,
                Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0),
            };
            TextBox Box() => new TextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 3) };

            _layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 8, Padding = new Padding(12),
            };
            _layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            for (int i = 0; i < 5; i++) _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // name, description, folder, when run, content label
            _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));                            // content
            _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));                                 // note
            _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));                                 // buttons

            int row = 0;
            _txtName = Box();
            _layout.Controls.Add(L("Name:"), 0, row); _layout.Controls.Add(_txtName, 1, row++);

            _txtDescription = Box();
            var ttDesc = new ToolTip();
            ttDesc.SetToolTip(_txtDescription, "One line, shown in the Library tab and as the menu entry's tooltip. Never sent to the AI.");
            _layout.Controls.Add(L("Description:"), 0, row); _layout.Controls.Add(_txtDescription, 1, row++);

            // Folder: only for prompts outside the QuickLauncher menu (Translate,
            // Proofread...). A menu entry's folder is where it was created.
            _lblFolder = L("Folder:");
            _txtFolder = Box();
            var ttFolder = new ToolTip();
            ttFolder.SetToolTip(_txtFolder, "Where the prompt is filed, e.g. Translate or Proofread. Translate prompts appear in the Batch Operations dropdown.");
            _layout.Controls.Add(_lblFolder, 0, row); _layout.Controls.Add(_txtFolder, 1, row++);

            // When run: one decision, one dropdown (#92).
            _lblWhenRun = L("When run:");
            _cboWhenRun = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left,
                Width = 260, Margin = new Padding(0, 3, 0, 3),
            };
            _cboWhenRun.Items.AddRange(new object[] { RunAssistant, RunClipboard, RunAsk });
            _cboWhenRun.SelectedIndex = 0;
            var ttRun = new ToolTip();
            ttRun.SetToolTip(_cboWhenRun,
                "What the menu entry does. Send: the expanded prompt goes to the AI Assistant.\r\n" +
                "Copy: it goes to the clipboard, for pasting into claude.ai, ChatGPT or Gemini.\r\n" +
                "Ask: the entry becomes a submenu offering both.");
            _layout.Controls.Add(_lblWhenRun, 0, row); _layout.Controls.Add(_cboWhenRun, 1, row++);

            _lblContent = new Label
            {
                Text = "Prompt content   (Ctrl+, inserts a variable)",
                AutoSize = true, ForeColor = labelColor, Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Margin = new Padding(0, 10, 0, 2),
            };
            _layout.Controls.Add(_lblContent, 0, row); _layout.SetColumnSpan(_lblContent, 2); row++;

            _txtContent = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9f),
                BackColor = Color.FromArgb(252, 252, 252),
                ForeColor = Color.FromArgb(40, 40, 40),
                WordWrap = true,
                AcceptsReturn = true,
                AcceptsTab = true,
                // TextBox.MaxLength defaults to Int16.MaxValue (32767) and silently
                // truncates pastes past that - patent-sized prompts hit it instantly.
                MaxLength = int.MaxValue,
                Margin = new Padding(0),
            };
            _layout.Controls.Add(_txtContent, 0, row); _layout.SetColumnSpan(_txtContent, 2); row++;

            _lblNote = new Label
            {
                AutoSize = true, ForeColor = Color.FromArgb(150, 90, 0), Margin = new Padding(0, 8, 0, 0),
                Visible = false, MaximumSize = new Size(ClientSize.Width - 24, 0),
            };
            _layout.Controls.Add(_lblNote, 0, row); _layout.SetColumnSpan(_lblNote, 2); row++;

            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 10, 0, 0), WrapContents = false,
            };
            _btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(80, 26), FlatStyle = FlatStyle.System };
            _btnOK = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(80, 26), FlatStyle = FlatStyle.System };
            _btnOK.Click += OnOKClick;
            buttons.Controls.Add(_btnCancel);
            buttons.Controls.Add(_btnOK);
            _layout.Controls.Add(buttons, 0, row); _layout.SetColumnSpan(buttons, 2); row++;

            Controls.Add(_layout);
            AcceptButton = _btnOK;
            CancelButton = _btnCancel;
            Resize += (s, e) => _lblNote.MaximumSize = new Size(Math.Max(200, ClientSize.Width - 24), 0);

            // Variable picker menu (Ctrl+,)
            _varMenu = new ContextMenuStrip { Font = new Font("Segoe UI", 9f) };
            void AddVar(string variable, string description)
            {
                var item = new ToolStripMenuItem($"{variable}  —  {description}");
                item.Click += (s, e) => InsertVariable(variable);
                _varMenu.Items.Add(item);
            }
            AddVar("{{SOURCE_LANGUAGE}}", "Source language name (e.g. \"Dutch\")");
            AddVar("{{TARGET_LANGUAGE}}", "Target language name (e.g. \"English\")");
            AddVar("{{SOURCE_SEGMENT}}", "Source text of the active segment");
            AddVar("{{TARGET_SEGMENT}}", "Target text of the active segment");
            AddVar("{{SELECTION}}", "Currently selected text in the editor");
            _varMenu.Items.Add(new ToolStripSeparator());
            AddVar("{{PROJECT_NAME}}", "Name of the active Trados project");
            AddVar("{{DOCUMENT_NAME}}", "Name of the active file");
            AddVar("{{SURROUNDING_SEGMENTS}}", "Context segments around the active segment");
            AddVar("{{PROJECT}}", "All source segments in the document");
            AddVar("{{TM_MATCHES}}", "Translation memory fuzzy matches (≥70%)");
        }

        private void PopulateFromPrompt()
        {
            _txtName.Text = _prompt.Name ?? "";
            _txtDescription.Text = _prompt.Description ?? "";
            _txtFolder.Text = _prompt.Category ?? "";
            _txtContent.Text = _prompt.Content ?? "";

            bool inMenu = _prompt.IsQuickLauncher || IsQuickLauncherFolder(_prompt.Category);
            // A menu entry's folder is implied; every other prompt says where it is filed.
            _lblFolder.Visible = !inMenu;
            _txtFolder.Visible = !inMenu;
            _lblWhenRun.Visible = inMenu;
            _cboWhenRun.Visible = inMenu;

            var modes = _prompt.QuickLauncherModes ?? new List<string>();
            bool assistant = modes.Count == 0 || modes.Contains("assistant");
            bool clipboard = modes.Contains("clipboard");
            _cboWhenRun.SelectedItem = assistant && clipboard ? RunAsk : clipboard ? RunClipboard : RunAssistant;

            if (_prompt.IsReadOnly)
            {
                _txtName.ReadOnly = true;
                _txtDescription.ReadOnly = true;
                _txtFolder.ReadOnly = true;
                _cboWhenRun.Enabled = false;
                _txtContent.ReadOnly = true;
                _btnOK.Enabled = false;
                Text += " (read-only)";
            }
            else if (_prompt.IsDefault)
            {
                // Built-in: the text is immutable (use Clone to change it), but what
                // happens when it is run is the user's to choose.
                _txtName.ReadOnly = true;
                _txtDescription.ReadOnly = true;
                _txtFolder.ReadOnly = true;
                _txtContent.ReadOnly = true;
                _lblNote.Text = "This is a built-in prompt: its text cannot be changed here. Use Clone in the Library tab to make " +
                                "your own copy" + (inMenu ? ", or right-click it there to hide it from the menu." : ".");
                _lblNote.Visible = true;
                Text += " (built-in)";
            }
        }

        private void OnOKClick(object sender, EventArgs e)
        {
            if (_prompt.IsDefault)
            {
                ApplyWhenRun();
                return;
            }

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
            if (_txtFolder.Visible) _prompt.Category = _txtFolder.Text.Trim();
            _prompt.Content = _txtContent.Text;
            // _prompt.App and HiddenFromMenu are left as loaded: the editor no longer
            // offers them, and rewriting them here would silently rewrite every file it saved.
            ApplyWhenRun();
        }

        /// <summary>
        /// One dropdown to <see cref="PromptTemplate.QuickLauncherModes"/> +
        /// <see cref="PromptTemplate.DefaultMode"/>. "Ask me" is both modes with
        /// Assistant first - the only case in which the menu shows a submenu.
        /// </summary>
        private void ApplyWhenRun()
        {
            if (!_cboWhenRun.Visible) return;   // not a menu entry: leave modes untouched
            var choice = _cboWhenRun.SelectedItem as string;
            if (choice == RunClipboard)
            {
                _prompt.QuickLauncherModes = new List<string> { "clipboard" };
                _prompt.DefaultMode = "clipboard";
            }
            else if (choice == RunAsk)
            {
                _prompt.QuickLauncherModes = new List<string> { "assistant", "clipboard" };
                _prompt.DefaultMode = "assistant";
            }
            else
            {
                _prompt.QuickLauncherModes = new List<string> { "assistant" };
                _prompt.DefaultMode = "assistant";
            }
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
