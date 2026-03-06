using System;
using System.Drawing;
using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi.Interfaces;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// Top-level container for the Supervertaler ViewPart.
    /// Hosts a TabControl with tabs for each feature: TermLens (glossary),
    /// AI Assistant, Batch Translate, etc.
    /// The settings gear button is at the top-right, visible on all tabs.
    /// </summary>
    public class MainPanelControl : UserControl, IUIControl
    {
        private readonly TabControl _tabControl;
        private readonly Button _btnSettings;

        /// <summary>
        /// Fired when the user clicks the gear/settings button.
        /// </summary>
        public event EventHandler SettingsRequested;

        public MainPanelControl(TermLensControl termLensControl,
            BatchTranslateControl batchTranslateControl)
        {
            SuspendLayout();

            BackColor = Color.White;

            _tabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8.5f),
                Padding = new Point(6, 3),
            };

            // TermLens tab (glossary)
            var termLensPage = new TabPage("TermLens");
            termLensControl.Dock = DockStyle.Fill;
            termLensPage.Controls.Add(termLensControl);
            _tabControl.TabPages.Add(termLensPage);

            // Placeholder tabs for upcoming features
            var aiAssistantPage = new TabPage("AI Assistant");
            aiAssistantPage.Controls.Add(CreatePlaceholderLabel("AI Assistant \u2014 coming soon"));
            _tabControl.TabPages.Add(aiAssistantPage);

            var batchPage = new TabPage("Batch Translate");
            batchTranslateControl.Dock = DockStyle.Fill;
            batchPage.Controls.Add(batchTranslateControl);
            _tabControl.TabPages.Add(batchPage);

            Controls.Add(_tabControl);

            // Settings gear button — floats at top-right over the tab strip,
            // visible regardless of which tab is active
            _btnSettings = new Button
            {
                Text = "\u2699\uFE0E",  // gear character + text presentation selector
                Size = new Size(26, 22),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Symbol", 10f),
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

            Controls.Add(_btnSettings);
            _btnSettings.BringToFront(); // render on top of the tab control

            ResumeLayout(false);

            // Position gear button at top-right, and keep it there on resize
            Resize += (s, e) => PositionSettingsButton();
            PositionSettingsButton();
        }

        private void PositionSettingsButton()
        {
            if (_btnSettings == null) return;
            _btnSettings.Location = new Point(Width - _btnSettings.Width - 2, 1);
        }

        private static Label CreatePlaceholderLabel(string text)
        {
            return new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(150, 150, 150),
                Font = new Font("Segoe UI", 10f, FontStyle.Italic),
            };
        }
    }
}
