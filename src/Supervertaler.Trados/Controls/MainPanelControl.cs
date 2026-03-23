using System;
using System.Drawing;
using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi.Interfaces;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// Top-level container for the TermLens ViewPart.
    /// Hosts the TermLensControl directly (no tabs).
    /// The settings gear button is at the top-right.
    /// </summary>
    public class MainPanelControl : UserControl, IUIControl
    {
        private readonly Button _btnSettings;
        private readonly Button _btnHelp;

        /// <summary>
        /// Fired when the user clicks the gear/settings button.
        /// </summary>
        public event EventHandler SettingsRequested;

        public MainPanelControl(TermLensControl termLensControl)
        {
            SuspendLayout();

            BackColor = Color.White;

            // Host TermLensControl directly — no TabControl
            termLensControl.Dock = DockStyle.Fill;
            Controls.Add(termLensControl);

            // Settings gear button — floats at top-right
            _btnSettings = new Button
            {
                Text = "\u2699\uFE0E",  // gear character + text presentation selector
                Size = new Size(UiScale.Pixels(26), UiScale.Pixels(22)),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Symbol", UiScale.FontSize(10f)),
                ForeColor = Color.FromArgb(100, 100, 100),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                TabStop = false,
                UseCompatibleTextRendering = true,
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = Padding.Empty,
                Margin = Padding.Empty
            };
            _btnSettings.FlatAppearance.BorderSize = 0;
            _btnSettings.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 220, 220);
            _btnSettings.Click += (s, e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

            // Help/about button (?) — floats to the left of the gear button
            _btnHelp = new Button
            {
                Text = "?",
                Size = new Size(UiScale.Pixels(26), UiScale.Pixels(22)),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", UiScale.FontSize(8f), FontStyle.Bold),
                ForeColor = Color.FromArgb(100, 100, 100),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                TabStop = false,
                UseCompatibleTextRendering = true,
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(0, UiScale.Pixels(1), 0, 0),
                Margin = Padding.Empty
            };
            _btnHelp.FlatAppearance.BorderSize = 0;
            _btnHelp.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 220, 220);
            _btnHelp.Click += OnHelpDropdown;

            Controls.Add(_btnSettings);
            Controls.Add(_btnHelp);
            _btnSettings.BringToFront(); // render on top of the content
            _btnHelp.BringToFront();

            ResumeLayout(false);

            // Position buttons at top-right, and keep them there on resize
            Resize += (s, e) => PositionTopButtons();
            PositionTopButtons();
        }

        private void PositionTopButtons()
        {
            if (_btnHelp == null || _btnSettings == null) return;
            // Help "?" at far right, gear to its left
            _btnHelp.Location = new Point(Width - _btnHelp.Width - 2, 1);
            _btnSettings.Location = new Point(_btnHelp.Left - _btnSettings.Width, 1);
        }

        private void OnHelpDropdown(object sender, EventArgs e)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("TermLens Help", null, (s, ev) =>
                HelpSystem.OpenHelp(HelpSystem.Topics.TermLensPanel));
            menu.Items.Add("MultiTerm Help", null, (s, ev) =>
                HelpSystem.OpenHelp(HelpSystem.Topics.MultiTermSupport));
            menu.Items.Add("Example Project", null, (s, ev) =>
                HelpSystem.OpenHelp(HelpSystem.Topics.ExampleProject));
            menu.Items.Add("-");  // separator
            menu.Items.Add("About Supervertaler for Trados", null, (s, ev) =>
            {
                using (var dlg = new AboutDialog())
                    dlg.ShowDialog(FindForm());
            });
            menu.Show(_btnHelp, new Point(0, _btnHelp.Height));
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F1)
            {
                HelpSystem.OpenHelp(HelpSystem.Topics.TermLensPanel);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

    }
}
