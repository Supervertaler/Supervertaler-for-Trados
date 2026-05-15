using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// One-time anonymous-usage-statistics notice. Shown once after install or
    /// update under the v2 framing: informational, default-on, opt-out.
    ///
    ///   - Yes button / Enter / Esc / X-close all return DialogResult.Yes
    ///     (user keeps stats on - the default).
    ///   - Only an explicit click on "Turn it off" returns DialogResult.No.
    ///
    /// Copy is deliberately written as a personal note from the developer
    /// rather than a corporate disclaimer, since the data collected is
    /// genuinely anonymous and minimal.
    /// </summary>
    internal sealed class UsageStatisticsDialog : Form
    {
        private const string HelpUrl =
            "https://supervertaler.gitbook.io/trados/settings/usage-statistics";

        public UsageStatisticsDialog()
        {
            Icon = Supervertaler.Trados.Core.IconHelper.AppIcon;
            // Let WinForms scale this dialog by system DPI so it doesn't squish
            // at >100% Windows display scaling. Cheap fallback; for surfaces
            // with their own UiScale-driven layout, set AutoScaleMode = None
            // instead and let UiScale own scaling.
            AutoScaleMode = AutoScaleMode.Dpi;
            SuspendLayout();

            Text = "Supervertaler for Trados";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            HelpButton = true;
            ClientSize = new Size(460, 240);
            Font = new Font("Segoe UI", 9F);

            HelpButtonClicked += (s, e) =>
            {
                e.Cancel = true; // prevent the cursor from changing to ?
                try { Process.Start(new ProcessStartInfo(HelpUrl) { UseShellExecute = true }); }
                catch { }
            };

            var lblTitle = new Label
            {
                Text = "Anonymous usage statistics",
                Location = new Point(20, 16),
                AutoSize = true,
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                ForeColor = Color.FromArgb(30, 30, 30)
            };

            var lblBody = new Label
            {
                Text = "Supervertaler for Trados sends one anonymous ping at startup so I " +
                       "can see how many people use the plugin. No personal data, no " +
                       "translation content, no termbase info — just plugin version, OS, " +
                       "Trados version, and system locale.\n\n" +
                       "If you'd rather not, switch it off below or any time in Settings.\n\n" +
                       "— Michael",
                Location = new Point(20, 46),
                Size = new Size(420, 130),
                ForeColor = Color.FromArgb(50, 50, 50)
            };

            var lnkLearnMore = new LinkLabel
            {
                Text = "Learn more about what is collected",
                Location = new Point(20, 184),
                AutoSize = true,
                LinkColor = Color.FromArgb(37, 99, 235),
                ForeColor = Color.FromArgb(37, 99, 235)
            };
            lnkLearnMore.LinkClicked += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo(HelpUrl) { UseShellExecute = true }); }
                catch { }
            };

            var btnYes = new Button
            {
                Text = "Keep it on",
                DialogResult = DialogResult.Yes,
                Location = new Point(190, 200),
                Size = new Size(120, 32),
                FlatStyle = FlatStyle.System
            };

            var btnNo = new Button
            {
                Text = "Turn it off",
                DialogResult = DialogResult.No,
                Location = new Point(320, 200),
                Size = new Size(120, 32),
                FlatStyle = FlatStyle.System
            };

            // Both Enter (AcceptButton) and Esc (CancelButton) are wired to
            // "Keep it on" so any non-explicit close defaults to keeping stats
            // enabled. The X-close button isn't routed through CancelButton -
            // it returns DialogResult.Cancel - and the calling code treats
            // anything that isn't an explicit DialogResult.No as "keep on".
            AcceptButton = btnYes;
            CancelButton = btnYes;

            Controls.AddRange(new Control[] { lblTitle, lblBody, lnkLearnMore, btnYes, btnNo });

            ResumeLayout(false);
        }
    }
}
