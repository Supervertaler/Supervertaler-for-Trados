using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Licensing;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// About/Help dialog showing plugin version, keyboard shortcuts, and links.
    /// Opened from the "?" button in the TermLens panel header.
    /// </summary>
    public class AboutDialog : Form
    {
        public AboutDialog()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            var versionStr = $"{version.Major}.{version.Minor}.{version.Build}";

            Text = "About Supervertaler for Trados";
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(460, 530);
            BackColor = Color.White;

            int y = 16;
            int leftPad = 20;
            int contentWidth = ClientSize.Width - 40;

            // Plugin name
            Controls.Add(new Label
            {
                Text = "Supervertaler for Trados",
                Location = new Point(leftPad, y),
                AutoSize = true,
                Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                ForeColor = Color.FromArgb(50, 50, 50)
            });
            y += 34;

            // Version
            Controls.Add(new Label
            {
                Text = $"Version {versionStr}",
                Location = new Point(leftPad, y),
                AutoSize = true,
                ForeColor = Color.FromArgb(100, 100, 100)
            });
            y += 22;

            // Copyright
            // Author
            Controls.Add(new Label
            {
                Text = "by Michael Beijer",
                Location = new Point(leftPad, y),
                AutoSize = true,
                ForeColor = Color.FromArgb(80, 80, 80)
            });
            y += 20;

            // Email
            var emailLink = new NoFocusCuesLinkLabel
            {
                Text = "info@michaelbeijer.co.uk",
                Location = new Point(leftPad, y),
                AutoSize = true,
                Font = new Font("Segoe UI", 8.5f),
                LinkColor = Color.FromArgb(40, 100, 180),
                ActiveLinkColor = Color.FromArgb(30, 80, 160)
            };
            emailLink.LinkClicked += (s, e) =>
            {
                try
                {
                    Clipboard.SetText("info@michaelbeijer.co.uk");
                    var original = emailLink.Text;
                    emailLink.Text = "Copied!";
                    emailLink.LinkColor = Color.FromArgb(60, 160, 60);
                    var timer = new Timer { Interval = 1500 };
                    timer.Tick += (t, _) =>
                    {
                        emailLink.Text = original;
                        emailLink.LinkColor = Color.FromArgb(40, 100, 180);
                        timer.Stop();
                        timer.Dispose();
                    };
                    timer.Start();
                }
                catch { }
            };
            Controls.Add(emailLink);
            y += 22;

            // Copyright
            Controls.Add(new Label
            {
                Text = $"\u00a9 {DateTime.Now.Year} Supervertaler. All rights reserved.",
                Location = new Point(leftPad, y),
                AutoSize = true,
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(120, 120, 120)
            });
            y += 28;

            // Separator
            Controls.Add(new Label
            {
                Location = new Point(leftPad, y),
                Width = contentWidth,
                Height = 1,
                BorderStyle = BorderStyle.Fixed3D
            });
            y += 12;

            // License status
            var mgr = LicenseManager.Instance;
            var tier = mgr.CurrentTier;
            string licenseText;
            Color licenseColor;
            switch (tier)
            {
                case LicenseTier.Trial:
                    licenseText = $"License: Trial ({mgr.TrialDaysRemaining} days remaining)";
                    licenseColor = Color.FromArgb(30, 80, 160);
                    break;
                case LicenseTier.Tier1:
                    licenseText = "License: TermLens (active)";
                    licenseColor = Color.FromArgb(40, 120, 40);
                    break;
                case LicenseTier.Tier2:
                    licenseText = "License: TermLens + Supervertaler Assistant (active)";
                    licenseColor = Color.FromArgb(40, 120, 40);
                    break;
                default:
                    licenseText = "License: No active license";
                    licenseColor = Color.FromArgb(180, 40, 40);
                    break;
            }

            var licenseLink = new NoFocusCuesLinkLabel
            {
                Text = licenseText,
                Location = new Point(leftPad, y),
                AutoSize = true,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                LinkColor = licenseColor,
                ActiveLinkColor = licenseColor,
                LinkBehavior = LinkBehavior.HoverUnderline
            };
            var licenseTip = new ToolTip();
            licenseTip.SetToolTip(licenseLink, "Open license settings");
            licenseLink.LinkClicked += (s, e) =>
            {
                Close();
                try
                {
                    using (var form = new TermLensSettingsForm(TermLensSettings.Load(), defaultTab: 3))
                        form.ShowDialog();
                }
                catch { }
            };
            Controls.Add(licenseLink);
            y += 24;

            // Separator
            Controls.Add(new Label
            {
                Location = new Point(leftPad, y),
                Width = contentWidth,
                Height = 1,
                BorderStyle = BorderStyle.Fixed3D
            });
            y += 12;

            // Keyboard shortcuts header
            Controls.Add(new Label
            {
                Text = "Keyboard shortcuts",
                Location = new Point(leftPad, y),
                AutoSize = true,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(60, 60, 60)
            });
            y += 26;

            // Shortcuts table
            var shortcuts = new[]
            {
                ("Alt+Down",      "Quick-add term to write termbases"),
                ("Alt+Up",        "Quick-add term to project termbase"),
                ("Ctrl+Alt+T",    "Add term (dialog)"),
                ("Ctrl+Alt+N",    "Quick-add non-translatable"),
                ("Ctrl+Alt+A",    "AI translate segment"),
                ("Ctrl+Shift+G",  "Term Picker"),
                ("Alt+1\u20269",  "Insert term from TermLens panel"),
                ("F1",            "Context-sensitive help"),
            };

            var keyFont = new Font("Consolas", 8.5f);
            var descFont = new Font("Segoe UI", 8.5f);
            var keyColor = Color.FromArgb(50, 50, 50);
            var descColor = Color.FromArgb(80, 80, 80);

            foreach (var (key, desc) in shortcuts)
            {
                Controls.Add(new Label
                {
                    Text = key,
                    Location = new Point(leftPad + 4, y),
                    AutoSize = true,
                    Font = keyFont,
                    ForeColor = keyColor
                });
                Controls.Add(new Label
                {
                    Text = desc,
                    Location = new Point(leftPad + 130, y),
                    AutoSize = true,
                    Font = descFont,
                    ForeColor = descColor
                });
                y += 20;
            }

            y += 10;

            // Separator
            Controls.Add(new Label
            {
                Location = new Point(leftPad, y),
                Width = contentWidth,
                Height = 1,
                BorderStyle = BorderStyle.Fixed3D
            });
            y += 12;

            // Links
            AddLink("Website", "https://supervertaler.com", leftPad, ref y);
            AddLink("Documentation", null, leftPad, ref y, () => HelpSystem.OpenDocsHome());
            AddLink("Support", "https://github.com/Supervertaler/Supervertaler-for-Trados/issues", leftPad, ref y);

            y += 4;

            // Security / open-source note
            Controls.Add(new Label
            {
                Location = new Point(leftPad, y),
                Width = contentWidth,
                Height = 1,
                BorderStyle = BorderStyle.Fixed3D
            });
            y += 10;

            var shieldLabel = new Label
            {
                Text = "\U0001f6e1\ufe0f",
                Location = new Point(leftPad, y),
                AutoSize = true,
                Font = new Font("Segoe UI", 9f)
            };
            Controls.Add(shieldLabel);

            var securityLink = new NoFocusCuesLinkLabel
            {
                Text = "Source code available for security audit",
                Location = new Point(leftPad + 22, y),
                AutoSize = true,
                Font = new Font("Segoe UI", 8.5f),
                LinkColor = Color.FromArgb(80, 120, 80),
                ActiveLinkColor = Color.FromArgb(60, 100, 60)
            };
            var securityTip = new ToolTip();
            securityTip.SetToolTip(securityLink,
                "This plugin makes no network calls except to your chosen AI provider and the license server.\n" +
                "No telemetry, no tracking, no data collection. Verify it yourself on GitHub.");
            securityLink.LinkClicked += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo("https://github.com/Supervertaler/Supervertaler-for-Trados") { UseShellExecute = true }); }
                catch { }
            };
            Controls.Add(securityLink);
            y += 20;

            // Close button
            var btnClose = new Button
            {
                Text = "Close",
                DialogResult = DialogResult.Cancel,
                Width = 75,
                FlatStyle = FlatStyle.System,
                Location = new Point(ClientSize.Width - 95, ClientSize.Height - 44)
            };
            Controls.Add(btnClose);
            CancelButton = btnClose;
        }

        private void AddLink(string text, string url, int x, ref int y, Action customAction = null)
        {
            var link = new NoFocusCuesLinkLabel
            {
                Text = text,
                Location = new Point(x, y),
                AutoSize = true,
                Font = new Font("Segoe UI", 9f),
                LinkColor = Color.FromArgb(40, 100, 180),
                ActiveLinkColor = Color.FromArgb(30, 80, 160)
            };
            link.LinkClicked += (s, e) =>
            {
                try
                {
                    if (customAction != null)
                        customAction();
                    else
                        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch { /* No default browser configured */ }
            };
            Controls.Add(link);
            y += 22;
        }

        /// <summary>LinkLabel that hides the dotted focus rectangle.</summary>
        private class NoFocusCuesLinkLabel : LinkLabel
        {
            protected override bool ShowFocusCues => false;
        }
    }
}
