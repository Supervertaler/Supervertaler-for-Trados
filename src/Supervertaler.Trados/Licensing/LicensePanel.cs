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
        private const string PurchaseUrl = "https://supervertaler-for-trados.lemonsqueezy.com/checkout/buy/86e8dcb3-2a38-4396-aa38-9a67e5c72204";
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
            // Let WinForms scale this dialog by system DPI so it doesn't squish
            // at >100% Windows display scaling. Cheap fallback; for surfaces
            // with their own UiScale-driven layout, set AutoScaleMode = None
            // instead and let UiScale own scaling.
            AutoScaleMode = AutoScaleMode.Dpi;
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
                // Taller so the bold "Licence active" text isn't clipped at the
                // bottom under high DPI + Windows text scaling.
                Size = new Size(contentWidth, 48),
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
                Size = new Size(contentWidth, 170),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Visible = false
            };

            var lblEnterKey = new Label
            {
                Text = "Licence key",
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
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(12, 4, 12, 4),
                Location = new Point(0, 56),
                FlatStyle = FlatStyle.System
            };
            _btnActivate.Click += async (s, e) => await ActivateAsync();

            _lnkBuy = new LinkLabel
            {
                Text = "Buy a licence \u2192",
                Location = new Point(100, 62),
                AutoSize = true,
                Font = font,
                LinkColor = Color.FromArgb(40, 100, 180),
                ActiveLinkColor = Color.FromArgb(30, 80, 160)
            };
            _lnkBuy.LinkClicked += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo(PurchaseUrl) { UseShellExecute = true }); }
                catch { }
            };

            // The open door: nobody should feel they have to crack the trial.
            var lnkHardship = new LinkLabel
            {
                Text = "Cost shouldn’t be a barrier – if the price is a problem for you, get in touch and we’ll work something out.",
                Location = new Point(0, 92),
                MaximumSize = new Size(contentWidth, 0),
                AutoSize = true,
                Font = font,
                LinkColor = Color.FromArgb(40, 100, 180),
                ActiveLinkColor = Color.FromArgb(30, 80, 160)
            };
            lnkHardship.LinkArea = new LinkArea(
                lnkHardship.Text.IndexOf("get in touch", StringComparison.Ordinal), "get in touch".Length);
            lnkHardship.LinkClicked += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo("https://beijer.uk/contact") { UseShellExecute = true }); }
                catch { }
            };

            _activationPanel.Controls.AddRange(new Control[]
            {
                lblEnterKey, _txtLicenseKey, _btnActivate, _lnkBuy, lnkHardship
            });
            Controls.Add(_activationPanel);

            // ─── Licensed panel (shown when key is active) ──────────
            _licensedPanel = new Panel
            {
                Location = new Point(leftPad, y),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(contentWidth, 0),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Visible = false
            };

            // AutoSize TableLayoutPanel: rows can't overlap and values can't clip
            // at high DPI (the old absolute ly increments + fixed valueX=110 did
            // both, especially with Windows text scaling on top).
            var licensedGrid = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                GrowStyle = TableLayoutPanelGrowStyle.AddRows,
                Margin = Padding.Empty
            };
            licensedGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            licensedGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            Label LicLabel(string text, Font f) => new Label
            {
                Text = text, AutoSize = true, Font = f, ForeColor = labelColor,
                Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 14, 4)
            };
            Label LicValue(Font f, Color c) => new Label
            {
                AutoSize = true, Font = f, ForeColor = c,
                Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 0, 4)
            };

            _lblTierLabel = LicLabel("Plan:", headingFont);
            _lblTierValue = LicValue(headingFont, valueColor);
            licensedGrid.Controls.Add(_lblTierLabel, 0, 0);
            licensedGrid.Controls.Add(_lblTierValue, 1, 0);

            _lblKeyLabel = LicLabel("Licence key:", smallFont);
            _lblKeyValue = LicValue(new Font("Consolas", 8.5f), valueColor);
            licensedGrid.Controls.Add(_lblKeyLabel, 0, 1);
            licensedGrid.Controls.Add(_lblKeyValue, 1, 1);

            _lblStatusLabel = LicLabel("Status:", smallFont);
            _lblStatusValue = LicValue(smallFont, valueColor);
            licensedGrid.Controls.Add(_lblStatusLabel, 0, 2);
            licensedGrid.Controls.Add(_lblStatusValue, 1, 2);

            _lblValidatedLabel = LicLabel("Last verified:", smallFont);
            _lblValidatedValue = LicValue(smallFont, labelColor);
            licensedGrid.Controls.Add(_lblValidatedLabel, 0, 3);
            licensedGrid.Controls.Add(_lblValidatedValue, 1, 3);

            _btnRefresh = new Button
            {
                Text = "Verify Now",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(12, 4, 12, 4),
                Margin = new Padding(0, 10, 8, 0),
                FlatStyle = FlatStyle.System
            };
            _btnRefresh.Click += async (s, e) => await RefreshAsync();

            _btnDeactivate = new Button
            {
                Text = "Deactivate",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(12, 4, 12, 4),
                Margin = new Padding(0, 10, 0, 0),
                FlatStyle = FlatStyle.System
            };
            _btnDeactivate.Click += async (s, e) => await DeactivateAsync();

            var licenseButtonFlow = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = Padding.Empty
            };
            licenseButtonFlow.Controls.Add(_btnRefresh);
            licenseButtonFlow.Controls.Add(_btnDeactivate);
            licensedGrid.Controls.Add(licenseButtonFlow, 0, 4);
            licensedGrid.SetColumnSpan(licenseButtonFlow, 2);

            _lnkManage = new LinkLabel
            {
                Text = "Manage subscription \u2192",
                AutoSize = true,
                Font = font,
                LinkColor = Color.FromArgb(40, 100, 180),
                ActiveLinkColor = Color.FromArgb(30, 80, 160),
                Margin = new Padding(0, 12, 0, 0)
            };
            _lnkManage.LinkClicked += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo(ManageUrl) { UseShellExecute = true }); }
                catch { }
            };
            licensedGrid.Controls.Add(_lnkManage, 0, 5);
            licensedGrid.SetColumnSpan(_lnkManage, 2);

            _licensedPanel.Controls.Add(licensedGrid);
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

                case LicenseTier.Licensed:
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
            _statusText.Text = "\u2705  Licence active";
            _statusText.ForeColor = Color.FromArgb(40, 120, 40);

            // Show the Lemon Squeezy variant name if available, otherwise generic label
            var displayName = !string.IsNullOrWhiteSpace(mgr.VariantName)
                ? mgr.VariantName
                : "Supervertaler for Trados";
            _lblTierValue.Text = displayName;
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

            _activationPanel.Visible = false;
            _licensedPanel.Visible = true;
        }

        private void ShowExpiredState()
        {
            _statusBanner.BackColor = Color.FromArgb(255, 230, 230);
            _statusBanner.Invalidate();
            _statusText.Text = "\u274c  Licence expired";
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
                "Are you sure you want to deactivate this licence?\n\n" +
                "This will free up one of your activation slots.",
                "Deactivate Licence",
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
