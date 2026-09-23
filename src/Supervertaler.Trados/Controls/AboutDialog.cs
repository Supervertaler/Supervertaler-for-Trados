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
            Icon = Supervertaler.Trados.Core.IconHelper.AppIcon;
            // Let WinForms scale this dialog by system DPI so it doesn't squish
            // at >100% Windows display scaling. Cheap fallback; for surfaces
            // with their own UiScale-driven layout, set AutoScaleMode = None
            // instead and let UiScale own scaling.
            AutoScaleMode = AutoScaleMode.Dpi;
            var asm = Assembly.GetExecutingAssembly();
            var version = asm.GetName().Version;
            var versionStr = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                is System.Reflection.AssemblyInformationalVersionAttribute[] attrs && attrs.Length > 0
                ? attrs[0].InformationalVersion
                : $"{version.Major}.{version.Minor}.{version.Build}";

            Text = "About Supervertaler for Trados";
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(460, 555);
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
            // 22, not 20: an AutoSize label at this font renders 21px tall, so a
            // 20px step left the email link below overlapping it by a pixel.
            // Invisible on screen, but the layout probe measures it. Matches the
            // step used after the version label above.
            y += 22;

            // Email
            var emailLink = new NoFocusCuesLinkLabel
            {
                Text = "support@supervertaler.com",
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
                    Clipboard.SetText("support@supervertaler.com");
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
                    licenseText = $"Licence: Trial ({mgr.TrialDaysRemaining} days remaining)";
                    licenseColor = Color.FromArgb(30, 80, 160);
                    break;
                case LicenseTier.Licensed:
                    licenseText = "Licence: Active";
                    licenseColor = Color.FromArgb(40, 120, 40);
                    break;
                case LicenseTier.Unknown:
                    licenseText = "Licence: Could not be read";
                    licenseColor = Color.FromArgb(140, 100, 0);
                    break;
                default:
                    licenseText = "Licence: No active licence";
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
            licenseTip.SetToolTip(licenseLink, "Open licence settings");
            licenseLink.LinkClicked += (s, e) =>
            {
                Close();
                try
                {
                    // Was: a fresh TermLensSettings.Load(), i.e. a fourth private
                    // copy. Saving it reverted whatever either panel had changed
                    // since — and nothing was refreshed afterwards either, so
                    // this was the one gear icon whose changes did not show up
                    // until Studio restarted.
                    SettingsDialog.Show(null, defaultTab: 4);
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
                ("Ctrl+Alt+A",    "Add term with abbreviation (AI)"),
                ("Ctrl+Alt+N",    "Quick-add non-translatable"),
                // Alt+T, not Ctrl+T: the default moved in 18/19.20.119 because
                // Ctrl+T collides with Trados's own "Apply Translation Result".
                ("Alt+T",         "Translate active segment"),
                ("Ctrl+Alt+G",     "AutoTagger (place inline tags)"),
                // Was Ctrl (tap) until the tap was retired: a bare Ctrl press
                // is indistinguishable from any other program's Ctrl chord
                // once the middle key is consumed elsewhere, so the popup
                // opened by itself.
                ("Alt+L",          "Show TermLens popup"),
                ("Alt+P",          "Show TermPicker (list of matches)"),
                // Alt+Q, not Ctrl+Q: Studio's "View Internally Source" holds
                // Ctrl+Q and wins, so QuickLauncher did nothing until the user
                // cleared it. Moved in 20.184 — and listed here, because its
                // absence from this dialog is how that went unnoticed.
                ("Alt+Q",          "QuickLauncher (prompt menu)"),
                ("Ctrl+Alt+1\u20260", "Run QuickLauncher slot 1\u201310"),
                ("Ctrl+Alt+D",     "Toggle voice commands"),
                // Alt+0 is registered too ("insert term digit 0"), so the range
                // is 0-9, not the 1-9 this said until 20.184.
                ("Alt+0\u20269",  "Insert term from TermLens panel"),
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

            y += 4;

            // The list above is a hand-picked dozen; the plugin registers about
            // seventeen families. More to the point, several of these keys are
            // Trados factory defaults — Ctrl+Q is Studio's "View Internally
            // Source", and STUDIO WINS, so QuickLauncher does nothing until the
            // user clears that binding. A shortcut list that omits the reason a
            // shortcut appears dead is worse than none, and this dialog is where
            // people look first. The docs page carries the full list and the
            // conflict table; this points at it rather than duplicating it here,
            // where it would drift.
            AddLink("All shortcuts, and Trados conflicts", null, leftPad + 4, ref y,
                () => HelpSystem.OpenHelp(HelpSystem.Topics.KeyboardShortcuts),
                tooltip: "The full list, plus the Trados defaults that override "
                       + "some of these and how to clear them");

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
            AddLink("Documentation", null, leftPad, ref y, () => HelpSystem.OpenDocsHome(),
                tooltip: "Open the plugin documentation");
            AddLink("Support & Community", "https://supervertaler.com/trados/#support", leftPad, ref y,
                tooltip: "Supervertaler forum and GitHub Issues");
            AddLink("Privacy Policy", "https://supervertaler.com/privacy/", leftPad, ref y,
                tooltip: "How Supervertaler handles your data");

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
                // Position dynamically against the shield emoji's actual right edge.
                // At 150% Windows scaling the AutoSize emoji label was wider than
                // the fixed 30px gap, clipping the leading "S" of "Source".
                Location = new Point(shieldLabel.Right + 6, y),
                AutoSize = true,
                Font = new Font("Segoe UI", 8.5f),
                LinkColor = Color.FromArgb(80, 120, 80),
                ActiveLinkColor = Color.FromArgb(60, 100, 60)
            };
            var securityTip = new ToolTip();
            securityTip.SetToolTip(securityLink,
                "This plugin makes no network calls except to your chosen AI provider and the licence server.\n" +
                "No telemetry, no tracking, no data collection. Verify it yourself on GitHub.");
            securityLink.LinkClicked += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo("https://github.com/Supervertaler/Supervertaler-for-Trados") { UseShellExecute = true }); }
                catch { }
            };
            Controls.Add(securityLink);
            y += 20;

            // Grow the dialog to fit whatever the layout above actually produced,
            // instead of trusting the constant ClientSize set at the top.
            //
            // That constant was a running total that nobody recomputed: every
            // shortcut row and link added over the years pushed the last link
            // closer to the bottom edge, and adding Ctrl+Alt+A in 20.157 finally
            // pushed "Privacy Policy" off the dialog altogether. Deriving the
            // height from y means the next row added cannot do that again.
            const int closeButtonArea = 56;   // room for the button plus margins
            ClientSize = new Size(ClientSize.Width, y + closeButtonArea);

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

        private void AddLink(string text, string url, int x, ref int y, Action customAction = null, string tooltip = null)
        {
            var link = new NoFocusCuesLinkLabel
            {
                Text = text,
                Location = new Point(x, y),
                AutoSize = true,
                Font = new Font("Segoe UI", 9f),
                LinkColor = Color.FromArgb(40, 100, 180),
                ActiveLinkColor = Color.FromArgb(30, 80, 160),
                UseMnemonic = false
            };
            if (tooltip != null)
            {
                var tip = new ToolTip();
                tip.SetToolTip(link, tooltip);
            }
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
