using System;
using System.Drawing;
using System.Windows.Forms;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.VoiceControl
{
    /// <summary>
    /// Tiny always-on-top status strip – the FALLBACK voice indicator, shown
    /// only when the TermLens panel isn't open to host the integrated header
    /// 🎤 (see VoiceControlManager). Shows the listening state and the last
    /// executed command, plus a stop button and a gear that opens the
    /// Advanced dialog. Draggable (grab anywhere on the strip); the position
    /// is remembered across sessions. Defaults to bottom-right of the primary
    /// screen; never activated on show, so it can't steal editor focus.
    /// </summary>
    internal sealed class VoiceStatusWindow : Form
    {
        private readonly Label _dot;
        private readonly Label _text;
        private readonly Timer _flashTimer;
        private bool _userPositioned;

        public event EventHandler StopRequested;
        public event EventHandler AdvancedRequested;

        protected override bool ShowWithoutActivation => true;

        // Drag-to-move: report the whole client area as the caption so the
        // user can grab the strip anywhere (buttons still receive clicks –
        // they're child controls and hit-test first).
        private const int WM_NCHITTEST = 0x84;
        private const int HTCLIENT = 1;
        private const int HTCAPTION = 2;
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_NCHITTEST && m.Result == (IntPtr)HTCLIENT)
                m.Result = (IntPtr)HTCAPTION;
        }

        private bool _programmaticMove;

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            // Only a drag by the user counts – programmatic auto-placement
            // (and moves before Show) must not flip the flag.
            if (!_programmaticMove && Visible)
                _userPositioned = true;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Persist a user-chosen position for the next session
            try
            {
                if (_userPositioned)
                {
                    Settings.SettingsService.Update(s =>
                    {
                        s.VoiceStripLeft = Location.X;
                        s.VoiceStripTop = Location.Y;
                    });
                }
            }
            catch { }
            base.OnFormClosing(e);
        }

        public VoiceStatusWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(32, 33, 36);
            Padding = new Padding(UiScale.Pixels(8), UiScale.Pixels(5), UiScale.Pixels(6), UiScale.Pixels(5));

            var layout = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = new Padding(0)
            };

            _dot = new Label
            {
                Text = "●",
                ForeColor = Color.FromArgb(120, 200, 120),
                AutoSize = true,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Margin = new Padding(0, UiScale.Pixels(2), UiScale.Pixels(4), 0)
            };

            _text = new Label
            {
                Text = "Listening…",
                ForeColor = Color.WhiteSmoke,
                AutoSize = true,
                Font = new Font("Segoe UI", 9f),
                Margin = new Padding(0, UiScale.Pixels(2), UiScale.Pixels(8), 0)
            };

            var gear = MakeButton("⚙", "SuperVoice settings");
            gear.Click += (s, e) => AdvancedRequested?.Invoke(this, EventArgs.Empty);

            var stop = MakeButton("✕", "Stop voice commands");
            stop.Click += (s, e) => StopRequested?.Invoke(this, EventArgs.Empty);

            layout.Controls.Add(_dot);
            layout.Controls.Add(_text);
            layout.Controls.Add(gear);
            layout.Controls.Add(stop);
            Controls.Add(layout);

            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            _flashTimer = new Timer { Interval = 2500 };
            _flashTimer.Tick += (s, e) =>
            {
                _flashTimer.Stop();
                SetStatus("Listening…", listening: true);
            };

            Load += (s, e) => PositionInitial();
        }

        private Button MakeButton(string glyph, string tip)
        {
            var b = new Button
            {
                Text = glyph,
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.Gainsboro,
                BackColor = Color.FromArgb(48, 49, 52),
                Font = new Font("Segoe UI", 8.5f),
                Margin = new Padding(UiScale.Pixels(2), 0, 0, 0),
                TabStop = false
            };
            b.FlatAppearance.BorderSize = 0;
            new ToolTip().SetToolTip(b, tip);
            return b;
        }

        /// <summary>
        /// First placement: the remembered position when the user dragged the
        /// strip in an earlier session (and it's still on a screen), else
        /// bottom-right of the primary screen.
        /// </summary>
        private void PositionInitial()
        {
            try
            {
                var settings = Settings.SettingsService.Current;
                if (settings.VoiceStripLeft != 0 || settings.VoiceStripTop != 0)
                {
                    var saved = new Point(settings.VoiceStripLeft, settings.VoiceStripTop);
                    foreach (var screen in Screen.AllScreens)
                    {
                        if (screen.WorkingArea.Contains(saved))
                        {
                            _programmaticMove = true;
                            try { Location = saved; }
                            finally { _programmaticMove = false; }
                            _userPositioned = true; // restored user choice
                            return;
                        }
                    }
                }
            }
            catch { }
            PositionBottomRight();
        }

        private void PositionBottomRight()
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            _programmaticMove = true;
            try
            {
                Location = new Point(wa.Right - Width - UiScale.Pixels(24), wa.Bottom - Height - UiScale.Pixels(24));
            }
            finally { _programmaticMove = false; }
        }

        /// <summary>Thread-safe status update.</summary>
        public void SetStatus(string text, bool listening)
        {
            if (InvokeRequired) { BeginInvoke((Action)(() => SetStatus(text, listening))); return; }
            _text.Text = text;
            _dot.ForeColor = listening ? Color.FromArgb(120, 200, 120) : Color.FromArgb(230, 170, 60);
            if (!_userPositioned) PositionBottomRight();
        }

        /// <summary>Shows "heard" feedback briefly, then reverts to Listening.</summary>
        public void FlashCommand(string phrase)
        {
            if (InvokeRequired) { BeginInvoke((Action)(() => FlashCommand(phrase))); return; }
            _text.Text = "“" + phrase + "”";
            _dot.ForeColor = Color.FromArgb(110, 168, 254);
            if (!_userPositioned) PositionBottomRight();
            _flashTimer.Stop();
            _flashTimer.Start();
        }
    }
}
