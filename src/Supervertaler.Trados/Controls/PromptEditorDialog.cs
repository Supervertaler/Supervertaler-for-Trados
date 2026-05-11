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
        private ComboBox _cboApp;
        private CheckBox _chkShowInMenu;
        private TextBox _txtContent;
        private Label _lblDefault;
        private Label _lblMode;
        private CheckBox _chkModeAssistant;
        private CheckBox _chkModeClipboard;
        private Label _lblDefaultMode;
        private ComboBox _cboDefaultMode;
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
            // Let WinForms scale this dialog by system DPI so it doesn't squish
            // at >100% Windows display scaling. Cheap fallback; for surfaces
            // with their own UiScale-driven layout, set AutoScaleMode = None
            // instead and let UiScale own scaling.
            AutoScaleMode = AutoScaleMode.Dpi;
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
            var lblApp = new Label
            {
                Text = "App:",
                Location = new Point(310, y + 3),
                AutoSize = true,
                ForeColor = labelColor
            };
            _cboApp = new ComboBox
            {
                Location = new Point(355, y),
                Width = 130,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            _cboApp.Items.AddRange(new object[] { "Both", "Trados only", "Workbench only" });
            _cboApp.SelectedIndex = 0;
            Controls.Add(lblDomain);
            Controls.Add(_txtDomain);
            Controls.Add(lblApp);
            Controls.Add(_cboApp);
            y += 30;

            // ─── Show in QuickLauncher menu ─────────────
            _chkShowInMenu = new CheckBox
            {
                Text = "Show in QuickLauncher menu",
                Location = new Point(100, y),
                AutoSize = true,
                Checked = true,
                ForeColor = labelColor,
                Visible = false // shown only for QuickLauncher prompts
            };
            Controls.Add(_chkShowInMenu);

            // ─── Default prompt indicator ────────────────
            _lblDefault = new Label
            {
                Text = "(default prompt)",
                Location = new Point(330, y + 2),
                AutoSize = true,
                ForeColor = Color.FromArgb(150, 150, 150),
                Font = new Font("Segoe UI", 8f, FontStyle.Italic),
                Visible = false // shown only for default prompts
            };
            Controls.Add(_lblDefault);
            y += 26;

            // ─── QuickLauncher mode selector ─────────────
            // When two or more modes are checked, the QuickLauncher menu
            // renders a cascading submenu so the user can pick at runtime.
            // Default mode = which submenu item gets the natural first-Enter
            // activation. Whole row is shown only for QuickLauncher prompts
            // (same condition as the "Show in menu" checkbox above).
            _lblMode = new Label
            {
                Text = "Mode:",
                Location = new Point(12, y + 3),
                AutoSize = true,
                ForeColor = labelColor,
                Visible = false
            };
            Controls.Add(_lblMode);

            _chkModeAssistant = new CheckBox
            {
                Text = "Send to Assistant",
                Location = new Point(100, y),
                AutoSize = true,
                Checked = true,
                ForeColor = labelColor,
                Visible = false
            };
            Controls.Add(_chkModeAssistant);

            _chkModeClipboard = new CheckBox
            {
                Text = "Copy to clipboard",
                Location = new Point(245, y),
                AutoSize = true,
                Checked = false,
                ForeColor = labelColor,
                Visible = false
            };
            Controls.Add(_chkModeClipboard);

            // "Default:" + combo. Pushed further right because the autoscaled
            // "Copy to clipboard" checkbox is wider in practice (~155 px) than
            // a naive metric suggests — at x=415 the label was still butting
            // up against the previous checkbox with no visible gap.
            _lblDefaultMode = new Label
            {
                Text = "Default:",
                Location = new Point(435, y + 3),
                AutoSize = true,
                ForeColor = labelColor,
                Visible = false
            };
            Controls.Add(_lblDefaultMode);

            _cboDefaultMode = new ComboBox
            {
                Location = new Point(490, y),
                Width = 100,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Visible = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            _cboDefaultMode.Items.AddRange(new object[] { "Assistant", "Clipboard" });
            _cboDefaultMode.SelectedIndex = 0;
            Controls.Add(_cboDefaultMode);

            // Enable / disable the default-mode combo based on how many
            // mode checkboxes are checked. When only one is checked, there
            // is nothing to default to (single-mode prompts skip the
            // submenu entirely) so the combo is greyed out.
            EventHandler updateDefaultModeEnabled = (s, e) =>
            {
                var modesChecked =
                    (_chkModeAssistant.Checked ? 1 : 0) +
                    (_chkModeClipboard.Checked ? 1 : 0);
                _cboDefaultMode.Enabled = modesChecked >= 2;
                _lblDefaultMode.ForeColor = _cboDefaultMode.Enabled
                    ? labelColor : Color.FromArgb(170, 170, 170);
            };
            _chkModeAssistant.CheckedChanged += updateDefaultModeEnabled;
            _chkModeClipboard.CheckedChanged += updateDefaultModeEnabled;

            y += 26;

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
                // TextBox.MaxLength defaults to Int16.MaxValue (32767) and silently
                // truncates pastes past that – patent-sized prompts hit it instantly.
                MaxLength = int.MaxValue,
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

            // Common variables (shared with Workbench)
            AddVar("{{SOURCE_LANGUAGE}}", "Source language name (e.g. \"Dutch\")");
            AddVar("{{TARGET_LANGUAGE}}", "Target language name (e.g. \"English\")");
            AddVar("{{SOURCE_SEGMENT}}", "Source text of the active segment");
            AddVar("{{TARGET_SEGMENT}}", "Target text of the active segment");
            AddVar("{{SELECTION}}", "Currently selected text in the editor");
            _varMenu.Items.Add(new ToolStripSeparator());

            // Trados-specific variables
            AddVar("{{PROJECT_NAME}}", "Name of the active Trados project");
            AddVar("{{DOCUMENT_NAME}}", "Name of the active file");
            AddVar("{{SURROUNDING_SEGMENTS}}", "Context segments around the active segment");
            AddVar("{{PROJECT}}", "All source segments in the document");
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
            _txtDomain.Text = _prompt.Category ?? "";
            _txtContent.Text = _prompt.Content ?? "";

            // Map App value to ComboBox selection
            var app = (_prompt.App ?? "both").ToLowerInvariant();
            if (app == "trados")
                _cboApp.SelectedIndex = 1;
            else if (app == "workbench")
                _cboApp.SelectedIndex = 2;
            else
                _cboApp.SelectedIndex = 0; // "Both"

            // Show "Show in QuickLauncher menu" checkbox for QuickLauncher prompts
            UpdateShowInMenuVisibility();
            _chkShowInMenu.Checked = !_prompt.HiddenFromMenu;
            _txtDomain.TextChanged += (s, ev) => UpdateShowInMenuVisibility();

            // Pre-tick mode checkboxes from the prompt's QuickLauncherModes
            // list. Single-mode prompts ("assistant" only) show Assistant
            // ticked and Clipboard unticked — the default. Multi-mode
            // prompts tick both and enable the default-mode selector.
            var modes = _prompt.QuickLauncherModes ?? new System.Collections.Generic.List<string>();
            _chkModeAssistant.Checked = modes.Count == 0 || modes.Contains("assistant");
            _chkModeClipboard.Checked = modes.Contains("clipboard");
            _cboDefaultMode.SelectedIndex =
                string.Equals(_prompt.DefaultMode, "clipboard", StringComparison.OrdinalIgnoreCase)
                    ? 1 : 0;
            // Trigger initial enabled/disabled state on the default combo
            _cboDefaultMode.Enabled = _chkModeAssistant.Checked && _chkModeClipboard.Checked;

            if (_prompt.IsReadOnly)
            {
                _txtName.ReadOnly = true;
                _txtDescription.ReadOnly = true;
                _txtDomain.ReadOnly = true;
                _cboApp.Enabled = false;
                _chkShowInMenu.Enabled = false;
                _chkModeAssistant.Enabled = false;
                _chkModeClipboard.Enabled = false;
                _cboDefaultMode.Enabled = false;
                _txtContent.ReadOnly = true;
                _btnOK.Enabled = false;
                Text += " (read-only)";
            }
            else if (_prompt.IsDefault)
            {
                // Default prompts: content is immutable, but visibility +
                // mode toggles can be changed. To modify content, use Clone.
                _txtName.ReadOnly = true;
                _txtDescription.ReadOnly = true;
                _txtDomain.ReadOnly = true;
                _cboApp.Enabled = false;
                _txtContent.ReadOnly = true;
                // _chkShowInMenu, mode checkboxes, default combo stay enabled —
                // users can hide a default prompt and toggle clipboard mode on
                // it without needing to clone first. Those are routing prefs,
                // not content edits.
                _lblDefault.Visible = true;
                Text += " (default — use Clone to modify)";
            }
        }

        private void UpdateShowInMenuVisibility()
        {
            var domain = (_txtDomain.Text ?? "").Trim();
            var isQuickLauncher =
                domain.Equals("QuickLauncher", StringComparison.OrdinalIgnoreCase) ||
                domain.StartsWith("QuickLauncher/", StringComparison.OrdinalIgnoreCase) ||
                domain.StartsWith("QuickLauncher\\", StringComparison.OrdinalIgnoreCase);

            _chkShowInMenu.Visible = isQuickLauncher;
            // The mode-selector row is only meaningful for prompts that
            // appear in the QuickLauncher menu in the first place, so it
            // tracks the same visibility flag.
            _lblMode.Visible = isQuickLauncher;
            _chkModeAssistant.Visible = isQuickLauncher;
            _chkModeClipboard.Visible = isQuickLauncher;
            _lblDefaultMode.Visible = isQuickLauncher;
            _cboDefaultMode.Visible = isQuickLauncher;
        }

        private void OnOKClick(object sender, EventArgs e)
        {
            // Built-in prompts: content / name / category are immutable, but
            // routing prefs (visibility + clipboard mode) can be edited.
            if (_prompt.IsDefault)
            {
                if (_chkShowInMenu.Visible)
                    _prompt.HiddenFromMenu = !_chkShowInMenu.Checked;
                ApplyModesFromUi();
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
            _prompt.Category = _txtDomain.Text.Trim();
            _prompt.Content = _txtContent.Text;

            // Map ComboBox selection back to App value
            switch (_cboApp.SelectedIndex)
            {
                case 1: _prompt.App = "trados"; break;
                case 2: _prompt.App = "workbench"; break;
                default: _prompt.App = "both"; break;
            }

            // Save QuickLauncher menu visibility
            if (_chkShowInMenu.Visible)
                _prompt.HiddenFromMenu = !_chkShowInMenu.Checked;

            ApplyModesFromUi();
        }

        /// <summary>
        /// Build <see cref="PromptTemplate.QuickLauncherModes"/> + <see cref="PromptTemplate.DefaultMode"/>
        /// from the mode-selector checkboxes / combo. Force Assistant on if
        /// the user managed to uncheck everything — silently making a prompt
        /// unreachable from QuickLauncher would be worse than ignoring the
        /// edit.
        /// </summary>
        private void ApplyModesFromUi()
        {
            if (!_lblMode.Visible)
                return; // non-QuickLauncher prompts: leave modes untouched

            var modes = new System.Collections.Generic.List<string>();
            if (_chkModeAssistant.Checked) modes.Add("assistant");
            if (_chkModeClipboard.Checked) modes.Add("clipboard");
            if (modes.Count == 0)
                modes.Add("assistant");

            _prompt.QuickLauncherModes = modes;
            _prompt.DefaultMode = _cboDefaultMode.SelectedIndex == 1 ? "clipboard" : "assistant";
            // If the user picked a default that's no longer in the list
            // (e.g. picked Clipboard then unticked Clipboard), fall back to
            // whatever IS in the list.
            if (!modes.Contains(_prompt.DefaultMode))
                _prompt.DefaultMode = modes[0];
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
