using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// First-run setup dialog that lets the user choose the shared Supervertaler
    /// user-data folder.  Shown once when no %APPDATA%\Supervertaler\config.json
    /// exists yet.  On OK it calls UserDataPath.SetRoot() to persist the choice.
    /// </summary>
    internal class SetupDialog : Form
    {
        private Label  _headerLabel;
        private Label  _bodyLabel;
        private Label  _pathLabel;
        private TextBox _pathBox;
        private Button  _browseButton;
        private Label  _noteLabel;
        private Panel  _parallelsWarningPanel;
        private Button  _okButton;
        private Button  _cancelButton;

        internal SetupDialog()
        {
            Icon = Supervertaler.Trados.Core.IconHelper.AppIcon;
            // Let WinForms scale this dialog by system DPI so it doesn't squish
            // at >100% Windows display scaling. Cheap fallback; for surfaces
            // with their own UiScale-driven layout, set AutoScaleMode = None
            // instead and let UiScale own scaling.
            AutoScaleMode = AutoScaleMode.Dpi;
            InitializeComponent();
            PopulateDefaults();
        }

        // ── Defaults ─────────────────────────────────────────────────

        private void PopulateDefaults()
        {
            var detected = UserDataPath.DetectSharedRoot();
            if (!string.IsNullOrEmpty(detected))
            {
                _pathBox.Text = detected;
                _noteLabel.Text =
                    "An existing Supervertaler data folder was detected at the path above.\r\n" +
                    "Using it means prompts, termbases and API keys are shared with Supervertaler for memoQ and Sidekick automatically.";
                _noteLabel.ForeColor = Color.FromArgb(0, 120, 60);
            }
            else
            {
                _pathBox.Text = UserDataPath.DefaultRoot;
                _noteLabel.Text =
                    "No existing Supervertaler data folder was detected.\r\n" +
                    "The folder will be created automatically when Supervertaler for Trados first saves data.";
                _noteLabel.ForeColor = Color.FromArgb(80, 80, 80);
            }
        }

        // ── Event handlers ────────────────────────────────────────────

        private void BrowseButton_Click(object sender, EventArgs e)
        {
            var chosen = FolderPicker.Show(
                this, "Choose the Supervertaler user-data folder", _pathBox.Text);
            if (!string.IsNullOrEmpty(chosen))
                _pathBox.Text = chosen;
        }

        private void OkButton_Click(object sender, EventArgs e)
        {
            var path = _pathBox.Text.Trim();
            if (string.IsNullOrEmpty(path))
            {
                MessageBox.Show(this,
                    "Please enter or browse to a folder path.",
                    "Supervertaler Setup",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Warn if the path points to a Mac-side Parallels share
            if (IsMacSidePath(path))
            {
                var result = MessageBox.Show(this,
                    "The folder you selected is on the Mac side of Parallels (a shared network folder). " +
                    "Supervertaler uses SQLite databases for termbases, and SQLite does not work " +
                    "reliably on network-mounted filesystems – you may experience database errors or data loss.\r\n\r\n" +
                    "We strongly recommend using a Windows-side path instead (e.g., C:\\Users\\<username>\\Supervertaler).\r\n\r\n" +
                    "Use this Mac-side path anyway?",
                    "Supervertaler Setup – Mac Path Warning",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (result != DialogResult.Yes) return;
            }

            // Attempt to create the folder now so we can validate it's writable
            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "Could not create the folder:\r\n" + ex.Message,
                    "Supervertaler Setup",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            UserDataPath.SetRoot(path);
            DialogResult = DialogResult.OK;
            Close();
        }

        // ── Parallels / Mac detection ────────────────────────────────

        private static bool IsRunningInParallels()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))
                {
                    if (key != null)
                    {
                        var combined = ((key.GetValue("SystemManufacturer") as string ?? "") + " " +
                                        (key.GetValue("SystemProductName") as string ?? "")).ToLowerInvariant();
                        if (combined.Contains("parallels")) return true;
                    }
                }
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Parallels\Parallels Tools"))
                {
                    if (key != null) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Returns true if the path points to a Mac-side shared folder (e.g. \\Mac\Home\...).</summary>
        private static bool IsMacSidePath(string path)
        {
            return !string.IsNullOrEmpty(path) &&
                   path.TrimStart().StartsWith(@"\\Mac\", StringComparison.OrdinalIgnoreCase);
        }

        // ── UI construction ───────────────────────────────────────────

        private void InitializeComponent()
        {
            SuspendLayout();

            // ── Form ──────────────────────────────────────────────────
            Text            = "Supervertaler for Trados – First-Run Setup";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition   = FormStartPosition.CenterScreen;
            MaximizeBox     = false;
            MinimizeBox     = false;
            ShowInTaskbar   = false;
            Font            = new Font("Segoe UI", 9f);

            // ── Header ────────────────────────────────────────────────
            _headerLabel = new Label
            {
                Text      = "Welcome to Supervertaler for Trados",
                Font      = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = Color.FromArgb(30, 30, 120),
                Location  = new Point(16, 16),
                Size      = new Size(508, 24),
                AutoSize  = false
            };

            // ── Body ──────────────────────────────────────────────────
            _bodyLabel = new Label
            {
                Text =
                    "Choose a folder to store your data (prompts, settings, and licence information). " +
                    "Supervertaler for memoQ and Supervertaler Sidekick use the same folder, so prompts, " +
                    "termbases and API keys are shared between them automatically.",
                Location  = new Point(16, 48),
                Size      = new Size(508, 52),
                AutoSize  = false
            };

            // ── Path row ──────────────────────────────────────────────
            _pathLabel = new Label
            {
                Text     = "Data folder:",
                Location = new Point(16, 112),
                Size     = new Size(80, 22),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft
            };

            _pathBox = new TextBox
            {
                Location = new Point(100, 110),
                Size     = new Size(336, 22),
                Anchor   = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
            };

            _browseButton = new Button
            {
                Text     = "Browse…",
                Location = new Point(444, 109),
                Size     = new Size(80, 24)
            };
            _browseButton.Click += BrowseButton_Click;

            // ── Note ──────────────────────────────────────────────────
            _noteLabel = new Label
            {
                Location  = new Point(16, 146),
                Size      = new Size(508, 40),
                AutoSize  = false
            };

            // ── Parallels / Mac warning (shown only when VM detected) ─
            bool inParallels = IsRunningInParallels();
            int parallelsHeight = inParallels ? 64 : 0;

            _parallelsWarningPanel = new Panel
            {
                Location  = new Point(16, 194),
                Size      = new Size(508, 56),
                BackColor = Color.FromArgb(255, 248, 220),  // light yellow
                Visible   = inParallels,
                Padding   = new Padding(8)
            };
            var parallelsIcon = new Label
            {
                Text      = "\u26A0",  // ⚠ warning sign
                Font      = new Font("Segoe UI", 14f),
                Location  = new Point(8, 8),
                Size      = new Size(28, 28),
                AutoSize  = false
            };
            var parallelsText = new Label
            {
                Text      = "Parallels detected – you're running on a Mac. Keep the data folder on " +
                            "the Windows side (C:\\ drive). Do not use a Mac-side path like \\\\Mac\\Home\\... – " +
                            "SQLite databases do not work reliably on shared network folders.",
                Font      = new Font("Segoe UI", 8.25f),
                ForeColor = Color.FromArgb(120, 80, 0),
                Location  = new Point(36, 6),
                Size      = new Size(460, 44),
                AutoSize  = false
            };
            _parallelsWarningPanel.Controls.Add(parallelsIcon);
            _parallelsWarningPanel.Controls.Add(parallelsText);

            // ── Separator ─────────────────────────────────────────────
            int sepY = 244 + parallelsHeight;
            var separator = new Panel
            {
                Location  = new Point(0, sepY),
                Size      = new Size(540, 1),
                BackColor = Color.FromArgb(200, 200, 200)
            };

            // ── Buttons ───────────────────────────────────────────────
            _okButton = new Button
            {
                Text         = "OK",
                DialogResult = DialogResult.None, // handled manually
                Location     = new Point(348, sepY + 10),
                Size         = new Size(80, 26)
            };
            _okButton.Click += OkButton_Click;
            AcceptButton = _okButton;

            _cancelButton = new Button
            {
                Text         = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location     = new Point(444, sepY + 10),
                Size         = new Size(80, 26)
            };
            CancelButton = _cancelButton;

            // ── Form height ──────────────────────────────────────────
            ClientSize = new Size(540, 290 + parallelsHeight);

            Controls.AddRange(new Control[]
            {
                _headerLabel, _bodyLabel,
                _pathLabel, _pathBox, _browseButton,
                _noteLabel,
                _parallelsWarningPanel,
                separator,
                _okButton, _cancelButton
            });

            ResumeLayout(false);
        }
    }
}
