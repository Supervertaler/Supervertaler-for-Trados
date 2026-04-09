using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// One-off migration dialog shown the first time a multi-bank-aware build of
    /// Supervertaler for Trados starts up on an installation that still has a
    /// legacy single-bank folder (either <c>&lt;Root&gt;/memory-bank/</c> or
    /// <c>&lt;Root&gt;/supermemory/</c>). The user types a short name for their
    /// existing bank and on OK the folder is renamed into the new layout at
    /// <c>&lt;Root&gt;/memory-banks/&lt;name&gt;/</c>.
    ///
    /// Mirrors the Python Supervertaler Assistant's first-run naming dialog so a
    /// translator who uses both products sees the same flow on both sides.
    /// </summary>
    internal class LegacyMemoryBankMigrationDialog : Form
    {
        private Label _headerLabel;
        private Label _bodyLabel;
        private Label _sourceLabel;
        private Label _sourceValue;
        private Label _destLabel;
        private Label _destValue;
        private Label _nameLabel;
        private TextBox _nameBox;
        private Label _rulesLabel;
        private Label _statusLabel;
        private Button _okButton;
        private Button _cancelButton;

        /// <summary>The name chosen by the user; only valid when DialogResult == OK.</summary>
        internal string ChosenBankName { get; private set; }

        internal LegacyMemoryBankMigrationDialog()
        {
            InitializeComponent();

            _sourceValue.Text = UserDataPath.LegacySingleBankPath ?? "(none detected)";
            UpdateDestinationPreview();
            _nameBox.TextChanged += (s, e) => UpdateDestinationPreview();
        }

        // ── Destination preview ─────────────────────────────────────

        private void UpdateDestinationPreview()
        {
            var safe = UserDataPath.SanitizeBankName(_nameBox.Text);
            if (string.IsNullOrEmpty(safe))
            {
                _destValue.Text = Path.Combine(UserDataPath.MemoryBanksRoot, "<your-name>");
                _destValue.ForeColor = Color.FromArgb(140, 140, 140);
                _statusLabel.Text = string.Empty;
                _okButton.Enabled = false;
                return;
            }

            _destValue.Text = Path.Combine(UserDataPath.MemoryBanksRoot, safe);
            _destValue.ForeColor = Color.FromArgb(40, 40, 40);

            // Warn if the sanitized name differs from what the user typed, so they
            // understand why the destination preview doesn't match their input.
            var typed = (_nameBox.Text ?? "").Trim();
            if (!string.Equals(typed, safe, StringComparison.Ordinal))
            {
                _statusLabel.Text = "Will be saved as: " + safe;
                _statusLabel.ForeColor = Color.FromArgb(120, 80, 0);
            }
            else
            {
                _statusLabel.Text = string.Empty;
            }

            _okButton.Enabled = true;
        }

        // ── Event handlers ──────────────────────────────────────────

        private void OkButton_Click(object sender, EventArgs e)
        {
            var safe = UserDataPath.SanitizeBankName(_nameBox.Text);
            if (string.IsNullOrEmpty(safe))
            {
                _statusLabel.Text = "Please enter at least one letter, digit, hyphen or underscore.";
                _statusLabel.ForeColor = Color.FromArgb(180, 0, 0);
                return;
            }

            string error;
            if (!UserDataPath.TryMigrateLegacySingleBank(safe, out error))
            {
                MessageBox.Show(this,
                    error,
                    "Supervertaler — Memory bank migration",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            ChosenBankName = safe;
            DialogResult = DialogResult.OK;
            Close();
        }

        // ── UI construction ─────────────────────────────────────────

        private void InitializeComponent()
        {
            SuspendLayout();

            Text            = "Supervertaler — Name your existing memory bank";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition   = FormStartPosition.CenterScreen;
            MaximizeBox     = false;
            MinimizeBox     = false;
            ShowInTaskbar   = false;
            Font            = new Font("Segoe UI", 9f);

            _headerLabel = new Label
            {
                Text      = "Supervertaler Assistant now supports several memory banks",
                Font      = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = Color.FromArgb(30, 30, 120),
                Location  = new Point(16, 16),
                Size      = new Size(544, 24),
                AutoSize  = false
            };

            _bodyLabel = new Label
            {
                Text =
                    "Supervertaler for Trados just found an existing memory bank from an earlier " +
                    "version. Give it a short name so it can join the new multi-bank layout. " +
                    "You can add more banks later from the Memory banks toolbar.",
                Location  = new Point(16, 48),
                Size      = new Size(544, 48),
                AutoSize  = false
            };

            _sourceLabel = new Label
            {
                Text      = "Found at:",
                Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
                Location  = new Point(16, 106),
                Size      = new Size(100, 20),
                AutoSize  = false,
                TextAlign = ContentAlignment.MiddleLeft
            };

            _sourceValue = new Label
            {
                Location  = new Point(120, 106),
                Size      = new Size(440, 20),
                AutoSize  = false,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(40, 40, 40)
            };

            _destLabel = new Label
            {
                Text      = "Will become:",
                Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
                Location  = new Point(16, 130),
                Size      = new Size(100, 20),
                AutoSize  = false,
                TextAlign = ContentAlignment.MiddleLeft
            };

            _destValue = new Label
            {
                Location  = new Point(120, 130),
                Size      = new Size(440, 20),
                AutoSize  = false,
                TextAlign = ContentAlignment.MiddleLeft
            };

            _nameLabel = new Label
            {
                Text      = "Name:",
                Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
                Location  = new Point(16, 170),
                Size      = new Size(100, 22),
                AutoSize  = false,
                TextAlign = ContentAlignment.MiddleLeft
            };

            _nameBox = new TextBox
            {
                Location = new Point(120, 168),
                Size     = new Size(440, 22),
                Text     = "main"
            };

            _rulesLabel = new Label
            {
                Text =
                    "Use lowercase letters, digits, hyphens or underscores only. " +
                    "Spaces are replaced with hyphens and any other characters are dropped.",
                Location  = new Point(120, 196),
                Size      = new Size(440, 32),
                AutoSize  = false,
                ForeColor = Color.FromArgb(100, 100, 100),
                Font      = new Font("Segoe UI", 8.25f)
            };

            _statusLabel = new Label
            {
                Location  = new Point(120, 232),
                Size      = new Size(440, 20),
                AutoSize  = false,
                Font      = new Font("Segoe UI", 8.25f, FontStyle.Italic)
            };

            // Separator
            var separator = new Panel
            {
                Location  = new Point(0, 266),
                Size      = new Size(576, 1),
                BackColor = Color.FromArgb(200, 200, 200)
            };

            _okButton = new Button
            {
                Text         = "Migrate",
                DialogResult = DialogResult.None, // handled manually
                Location     = new Point(384, 278),
                Size         = new Size(88, 26),
                Enabled      = false
            };
            _okButton.Click += OkButton_Click;
            AcceptButton = _okButton;

            _cancelButton = new Button
            {
                Text         = "Skip for now",
                DialogResult = DialogResult.Cancel,
                Location     = new Point(480, 278),
                Size         = new Size(88, 26)
            };
            CancelButton = _cancelButton;

            ClientSize = new Size(576, 318);

            Controls.AddRange(new Control[]
            {
                _headerLabel, _bodyLabel,
                _sourceLabel, _sourceValue,
                _destLabel, _destValue,
                _nameLabel, _nameBox, _rulesLabel, _statusLabel,
                separator,
                _okButton, _cancelButton
            });

            ResumeLayout(false);
        }
    }
}
