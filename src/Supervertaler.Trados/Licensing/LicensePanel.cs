using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Supervertaler.Trados.Licensing
{
    /// <summary>
    /// License management panel, shown as a tab in the Settings dialog.
    /// Handles activation, deactivation, and status display.
    /// </summary>
    public class LicensePanel : UserControl
    {
        private const string PurchaseTier1Url = "https://supervertaler-for-trados.lemonsqueezy.com/checkout/buy/db309794-1214-4b57-a336-653438cb673d";
        private const string PurchaseTier2Url = "https://supervertaler-for-trados.lemonsqueezy.com/checkout/buy/d12f1b20-a4a5-4786-b9a9-47cc970296e0";
        private const string ManageUrl = "https://supervertaler-for-trados.lemonsqueezy.com/billing";

        private Label _lblStatus;
        private Panel _statusBanner;
        private Label _statusText;

        // Trial / no-key state
        private Panel _activationPanel;
        private TextBox _txtLicenseKey;
        private Button _btnActivate;
        private LinkLabel _lnkBuy;

        // Licensed state
        private Panel _licensedPanel;
        private Label _lblKeyLabel;
        private Label _lblKeyValue;
        private Label _lblTierLabel;
        private Label _lblTierValue;
        private Label _lblStatusLabel;
        private Label _lblStatusValue;
        private Label _lblValidatedLabel;
        private Label _lblValidatedValue;
        private Button _btnDeactivate;
        private Button _btnRefresh;
        private LinkLabel _lnkManage;
        private LinkLabel _lnkUpgrade;

        private bool _isProcessing;

        public LicensePanel()
        {
            BackColor = Color.White;
            Dock = DockStyle.Fill;
            BuildUI();
            RefreshDisplay();

            LicenseManager.Instance.LicenseStateChanged += (s, e) =>
            {
                if (InvokeRequired)
                    BeginInvoke(new Action(RefreshDisplay));
                else
                    RefreshDisplay();
            };
        }

        private void BuildUI()
        {
            var font = new Font("Segoe UI", 9f);
            var headingFont = new Font("Segoe UI", 9f, FontStyle.Bold);
            var smallFont = new Font("Segoe UI", 8.5f);
            var labelColor = Color.FromArgb(80, 80, 80);
            var valueColor = Color.FromArgb(40, 40, 40);

            int y = 16;
            int leftPad = 16;
            int contentWidth = 480;

            // ─── Status banner ──────────────────────────────────────
            _statusBanner = new Panel
            {
                Location = new Point(leftPad, y),
                Size = new Size(contentWidth, 36),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = Color.FromArgb(230, 245, 230)
            };
            _statusBanner.Paint += (s, e) =>
            {
                using (var pen = new Pen(Color.FromArgb(200, 220, 200)))
                    e.Graphics.DrawRectangle(pen, 0, 0, _statusBanner.Width - 1, _statusBanner.Height - 1);
            };

            _statusText = new Label
            {
                Location = new Point(12, 8),
                AutoSize = true,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(40, 120, 40),
                BackColor = Color.Transparent
            };
            _statusBanner.Controls.Add(_statusText);
            Controls.Add(_statusBanner);
            y += 50;

            // ─── Activation panel (shown during trial / expired) ────
            _activationPanel = new Panel
            {
                Location = new Point(leftPad, y),
                Size = new Size(contentWidth, 140),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Visible = false
            };

            var lblEnterKey = new Label
            {
                Text = "License key",
                Location = new Point(0, 0),
                AutoSize = true,
                Font = headingFont,
                ForeColor = Color.FromArgb(50, 50, 50)
            };

            _txtLicenseKey = new TextBox
            {
                Location = new Point(0, 24),
                Width = contentWidth,
                Font = new Font("Consolas", 9.5f)
            };
            _txtLicenseKey.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter && !_isProcessing)
                {
                    e.SuppressKeyPress = true;
                    _ = ActivateAsync();
                }
            };

            _btnActivate = new Button
            {
                Text = "Activate",
                Width = 90,
                Height = 28,
                Location = new Point(0, 56),
                FlatStyle = FlatStyle.System
            };
            _btnActivate.Click += async (s, e) => await ActivateAsync();

            _lnkBuy = new LinkLabel
            {
                Text = "Buy a license \u2192",
                Location = new Point(100, 62),
                AutoSize = true,
                Font = font,
                LinkColor = Color.FromArgb(40, 100, 180),
                ActiveLinkColor = Color.FromArgb(30, 80, 160)
            };
            _lnkBuy.LinkClicked += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo(PurchaseTier1Url) { UseShellExecute = true }); }
                catch { }
            };

            _activationPanel.Controls.AddRange(new Control[]
            {
                lblEnterKey, _txtLicenseKey, _btnActivate, _lnkBuy
            });
            Controls.Add(_activationPanel);

            // ─── Licensed panel (shown when key is active) ──────────
            _licensedPanel = new Panel
            {
                Location = new Point(leftPad, y),
                Size = new Size(contentWidth, 200),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Visible = false
            };

            int ly = 0;
            int labelX = 0;
            int valueX = 110;

            _lblTierLabel = new Label
            {
                Text = "Plan:",
                Location = new Point(labelX, ly),
                AutoSize = true,
                Font = headingFont,
                ForeColor = labelColor
            };
            _lblTierValue = new Label
            {
                Location = new Point(valueX, ly),
                AutoSize = true,
                Font = headingFont,
                ForeColor = valueColor
            };
            ly += 24;

            _lblKeyLabel = new Label
            {
                Text = "License key:",
                Location = new Point(labelX, ly),
                AutoSize = true,
                Font = smallFont,
                ForeColor = labelColor
            };
            _lblKeyValue = new Label
            {
                Location = new Point(valueX, ly),
                AutoSize = true,
                Font = new Font("Consolas", 8.5f),
                ForeColor = valueColor
            };
            ly += 22;

            _lblStatusLabel = new Label
            {
                Text = "Status:",
                Location = new Point(labelX, ly),
                AutoSize = true,
                Font = smallFont,
                ForeColor = labelColor
            };
            _lblStatusValue = new Label
            {
                Location = new Point(valueX, ly),
                AutoSize = true,
                Font = smallFont,
                ForeColor = valueColor
            };
            ly += 22;

            _lblValidatedLabel = new Label
            {
                Text = "Last verified:",
                Location = new Point(labelX, ly),
                AutoSize = true,
                Font = smallFont,
                ForeColor = labelColor
            };
            _lblValidatedValue = new Label
            {
                Location = new Point(valueX, ly),
                AutoSize = true,
                Font = smallFont,
                ForeColor = labelColor
            };
            ly += 32;

            _btnRefresh = new Button
            {
                Text = "Verify Now",
                Width = 90,
                Location = new Point(0, ly),
                FlatStyle = FlatStyle.System
            };
            _btnRefresh.Click += async (s, e) => await RefreshAsync();

            _btnDeactivate = new Button
            {
                Text = "Deactivate",
                Width = 90,
                Location = new Point(100, ly),
                FlatStyle = FlatStyle.System
            };
            _btnDeactivate.Click += async (s, e) => await DeactivateAsync();

            ly += 34;

            _lnkManage = new LinkLabel
            {
                Text = "Manage subscription \u2192",
                Location = new Point(0, ly),
                AutoSize = true,
                Font = font,
                LinkColor = Color.FromArgb(40, 100, 180),
                ActiveLinkColor = Color.FromArgb(30, 80, 160)
            };
            _lnkManage.LinkClicked += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo(ManageUrl) { UseShellExecute = true }); }
                catch { }
            };

            _lnkUpgrade = new LinkLabel
            {
                Text = "Upgrade to TermLens + Supervertaler Assistant \u2192",
                Location = new Point(0, ly + 22),
                AutoSize = true,
                Font = font,
                LinkColor = Color.FromArgb(40, 100, 180),
                ActiveLinkColor = Color.FromArgb(30, 80, 160),
                Visible = false
            };
            _lnkUpgrade.LinkClicked += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo(PurchaseTier2Url) { UseShellExecute = true }); }
                catch { }
            };

            _licensedPanel.Controls.AddRange(new Control[]
            {
                _lblTierLabel, _lblTierValue,
                _lblKeyLabel, _lblKeyValue,
                _lblStatusLabel, _lblStatusValue,
                _lblValidatedLabel, _lblValidatedValue,
                _btnRefresh, _btnDeactivate,
                _lnkManage, _lnkUpgrade
            });
            Controls.Add(_licensedPanel);
        }

        // ─── Display Refresh ────────────────────────────────────────

        private void RefreshDisplay()
        {
            var mgr = LicenseManager.Instance;
            var tier = mgr.CurrentTier;

            switch (tier)
            {
                case LicenseTier.Trial:
                    ShowTrialState(mgr.TrialDaysRemaining);
                    break;

                case LicenseTier.Tier1:
                case LicenseTier.Tier2:
                    ShowLicensedState(mgr);
                    break;

                case LicenseTier.None:
                default:
                    if (mgr.HasLicenseKey)
                        ShowExpiredState();
                    else
                        ShowExpiredTrialState();
                    break;
            }
        }

        private void ShowTrialState(int daysRemaining)
        {
            _statusBanner.BackColor = Color.FromArgb(220, 235, 255);
            _statusBanner.Invalidate();
            _statusText.Text = $"\u23f3  Trial: {daysRemaining} day{(daysRemaining == 1 ? "" : "s")} remaining";
            _statusText.ForeColor = Color.FromArgb(30, 80, 160);

            _activationPanel.Visible = true;
            _licensedPanel.Visible = false;
        }

        private void ShowLicensedState(LicenseManager mgr)
        {
            _statusBanner.BackColor = Color.FromArgb(230, 245, 230);
            _statusBanner.Invalidate();
            _statusText.Text = "\u2705  License active";
            _statusText.ForeColor = Color.FromArgb(40, 120, 40);

            _lblTierValue.Text = mgr.VariantName;
            _lblKeyValue.Text = mgr.MaskedLicenseKey;
            _lblStatusValue.Text = "Active";
            _lblStatusValue.ForeColor = Color.FromArgb(40, 120, 40);

            var lastValidated = mgr.LastValidatedAt;
            if (lastValidated != DateTime.MinValue)
            {
                var ago = DateTime.UtcNow - lastValidated;
                if (ago.TotalMinutes < 2)
                    _lblValidatedValue.Text = "Just now";
                else if (ago.TotalHours < 1)
                    _lblValidatedValue.Text = $"{(int)ago.TotalMinutes} minutes ago";
                else if (ago.TotalDays < 1)
                    _lblValidatedValue.Text = $"{(int)ago.TotalHours} hours ago";
                else
                    _lblValidatedValue.Text = $"{(int)ago.TotalDays} days ago";
            }
            else
            {
                _lblValidatedValue.Text = "Never";
            }

            // Show upgrade link for Tier 1 users
            _lnkUpgrade.Visible = mgr.CurrentTier == LicenseTier.Tier1;

            _activationPanel.Visible = false;
            _licensedPanel.Visible = true;
        }

        private void ShowExpiredState()
        {
            _statusBanner.BackColor = Color.FromArgb(255, 230, 230);
            _statusBanner.Invalidate();
            _statusText.Text = "\u274c  License expired";
            _statusText.ForeColor = Color.FromArgb(180, 40, 40);

            _activationPanel.Visible = true;
            _licensedPanel.Visible = false;
        }

        private void ShowExpiredTrialState()
        {
            _statusBanner.BackColor = Color.FromArgb(255, 240, 220);
            _statusBanner.Invalidate();
            _statusText.Text = "\u23f3  Trial expired";
            _statusText.ForeColor = Color.FromArgb(160, 100, 20);

            _activationPanel.Visible = true;
            _licensedPanel.Visible = false;
        }

        // ─── Actions ────────────────────────────────────────────────

        private async Task ActivateAsync()
        {
            if (_isProcessing) return;
            _isProcessing = true;
            _btnActivate.Enabled = false;
            _btnActivate.Text = "...";

            try
            {
                var (success, message) = await LicenseManager.Instance.ActivateAsync(_txtLicenseKey.Text);

                if (success)
                {
                    _txtLicenseKey.Text = "";
                    RefreshDisplay();
                }

                MessageBox.Show(message, success ? "Activation Successful" : "Activation Failed",
                    MessageBoxButtons.OK,
                    success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            finally
            {
                _isProcessing = false;
                _btnActivate.Enabled = true;
                _btnActivate.Text = "Activate";
            }
        }

        private async Task DeactivateAsync()
        {
            if (_isProcessing) return;

            var confirm = MessageBox.Show(
                "Are you sure you want to deactivate this license?\n\n" +
                "This will free up one of your activation slots.",
                "Deactivate License",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (confirm != DialogResult.Yes) return;

            _isProcessing = true;
            _btnDeactivate.Enabled = false;

            try
            {
                var (success, message) = await LicenseManager.Instance.DeactivateAsync();
                RefreshDisplay();
                MessageBox.Show(message, "Deactivation", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            finally
            {
                _isProcessing = false;
                _btnDeactivate.Enabled = true;
            }
        }

        private async Task RefreshAsync()
        {
            if (_isProcessing) return;
            _isProcessing = true;
            _btnRefresh.Enabled = false;
            _btnRefresh.Text = "...";

            try
            {
                var (success, message) = await LicenseManager.Instance.ValidateOnlineAsync();
                RefreshDisplay();

                if (!success)
                    MessageBox.Show(message, "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _isProcessing = false;
                _btnRefresh.Enabled = true;
                _btnRefresh.Text = "Verify Now";
            }
        }
    }
}
