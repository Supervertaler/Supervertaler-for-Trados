using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// The numbered-word popup for selecting by number (issue #128).
    ///
    /// <para><b>Why it exists.</b> A word the voice model does not know cannot be
    /// selected by saying it - measured on a real patent, that is a third of the
    /// technical vocabulary in the target and nearly half in a Dutch source. This
    /// popup numbers the words of the segment so the translator says the number
    /// instead: "select twelve", "select twelve to fourteen". It cannot mishear a
    /// word it was never asked to hear, and it is language-independent.</para>
    ///
    /// <para><b>Why a popup, and why it must not take focus.</b> Selection works by
    /// driving Studio's own search and shrinking the result with Shift+Left, both
    /// of which need the editor to keep keyboard focus. A window that activated
    /// would break the very selection it exists to make. Same construction as
    /// <see cref="TermPopup"/>: WS_EX_NOACTIVATE, ShowWithoutActivation, TopMost,
    /// placed near the cursor and clamped to the screen.</para>
    ///
    /// <para><b>Deliberately transient.</b> It opens on a command or on a failed
    /// selection, and closes on a number, "cancel", or a segment change. While it
    /// is open the number words are in the recogniser's grammar; the moment it
    /// closes they leave, because sixty extra words competing for every sound is
    /// how "term eight" made the article "a" unsayable.</para>
    /// </summary>
    internal sealed class NumberPopupForm : Form
    {
        private static readonly Color BorderColor = Color.FromArgb(190, 190, 190);
        private static readonly Color NumberColor = Color.FromArgb(30, 90, 158);
        private static readonly Color UnhearableBack = Color.FromArgb(255, 243, 208);   // the NT chip yellow
        private static readonly Color HintColor = Color.FromArgb(110, 110, 110);

        private static NumberPopupForm _instance;

        /// <summary>One numbered word: its text and offset in the plain segment text.</summary>
        internal sealed class Word
        {
            public string Text;
            public int Start;
            public bool Unhearable;
        }

        private List<Word> _words = new List<Word>();
        private bool _inSource;

        private readonly Label _title;
        private readonly Label _hint;
        private readonly FlowLayoutPanel _flow;

        /// <summary>Whether the popup is showing - the grammar and the intercept key off this.</summary>
        public static bool IsOpen => _instance != null && !_instance.IsDisposed && _instance.Visible;
        public static bool InSource => IsOpen && _instance._inSource;
        public static int WordCount => IsOpen ? _instance._words.Count : 0;

        private NumberPopupForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.White;
            AutoScaleMode = AutoScaleMode.Dpi;
            TopMost = true;
            DoubleBuffered = true;
            SetStyle(ControlStyles.Selectable, false);
            Padding = new Padding(UiScale.Pixels(10));

            _title = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = UiScale.Pixels(22),
                Font = new Font("Segoe UI", UiScale.FontSize(9f), FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(0, 0, UiScale.Pixels(24), 0)
            };
            // A close button. The window never takes keyboard focus, so Escape
            // reaches it only through the application's message filter - which
            // covers Studio's own windows, but a translator in the middle of a
            // screencast needs something to click as well. Mouse clicks arrive
            // regardless of activation.
            var close = new Label
            {
                Text = "✕",
                AutoSize = false,
                Width = UiScale.Pixels(22),
                Height = UiScale.Pixels(22),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = HintColor,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", UiScale.FontSize(9f))
            };
            new ToolTip().SetToolTip(close, "Close (Escape, or say \"cancel\")");
            close.Click += (s, e) => Supervertaler.Trados.TermLensEditorViewPart.VoiceHideNumbers();
            _title.Controls.Add(close);
            _title.Resize += (s, e) => close.Location = new Point(_title.Width - close.Width, 0);
            _hint = new Label
            {
                Dock = DockStyle.Bottom,
                AutoSize = false,
                Height = UiScale.Pixels(20),
                ForeColor = HintColor,
                Font = new Font("Segoe UI", UiScale.FontSize(8f)),
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "say \"select 12\" or \"select 12 to 14\"  ·  \"cancel\" closes"
            };
            _flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                WrapContents = true,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, UiScale.Pixels(4), 0, UiScale.Pixels(4))
            };
            Controls.Add(_flow);
            Controls.Add(_title);
            Controls.Add(_hint);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x08000000;   // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(BorderColor))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        /// <summary>
        /// Shows the popup for one side of the segment. <paramref name="title"/> is
        /// why it opened - the failure message, or "Target words" for the command.
        /// </summary>
        public static void ShowFor(bool inSource, string title, List<Word> words)
        {
            if (_instance == null || _instance.IsDisposed) _instance = new NumberPopupForm();
            _instance.Populate(inSource, title, words ?? new List<Word>());
            _instance.PositionNear(Cursor.Position);
            if (!_instance.Visible) _instance.Show();
            else _instance.Invalidate();
        }

        public static void CloseIt()
        {
            var i = _instance;
            if (i == null || i.IsDisposed) return;
            if (i.InvokeRequired) { try { i.BeginInvoke((Action)i.Hide); } catch { } }
            else i.Hide();
        }

        /// <summary>
        /// The text span for words <paramref name="from"/>..<paramref name="to"/>
        /// (1-based, inclusive) - one word when equal - or null when out of range.
        /// The span is the segment text between the first word's start and the last
        /// word's end, so a range keeps the punctuation and spaces between them.
        /// </summary>
        public static Word Span(int from, int to, string plain)
        {
            var i = _instance;
            if (i == null || i.IsDisposed) return null;
            var words = i._words;
            if (from < 1 || to < from || to > words.Count) return null;
            var first = words[from - 1];
            var last = words[to - 1];
            var end = last.Start + last.Text.Length;
            if (plain == null || end > plain.Length) return null;
            return new Word { Start = first.Start, Text = plain.Substring(first.Start, end - first.Start) };
        }

        private void Populate(bool inSource, string title, List<Word> words)
        {
            _inSource = inSource;
            _words = words;
            _title.Text = title ?? (inSource ? "Source words" : "Target words");

            _flow.SuspendLayout();
            _flow.Controls.Clear();
            var numFont = new Font("Segoe UI", UiScale.FontSize(7.5f), FontStyle.Bold);
            var wordFont = new Font("Segoe UI", UiScale.FontSize(10f));
            for (int n = 0; n < words.Count; n++)
            {
                var w = words[n];
                var chip = new Panel
                {
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Margin = new Padding(UiScale.Pixels(2), UiScale.Pixels(2), UiScale.Pixels(6), UiScale.Pixels(2)),
                    Padding = new Padding(UiScale.Pixels(3), UiScale.Pixels(1), UiScale.Pixels(4), UiScale.Pixels(1)),
                    BackColor = w.Unhearable ? UnhearableBack : Color.Transparent
                };
                var num = new Label
                {
                    Text = (n + 1).ToString(),
                    AutoSize = true,
                    Font = numFont,
                    ForeColor = NumberColor,
                    Location = new Point(0, 0),
                    Margin = Padding.Empty
                };
                var txt = new Label
                {
                    Text = w.Text,
                    AutoSize = true,
                    Font = wordFont,
                    Location = new Point(num.PreferredWidth + UiScale.Pixels(2), 0),
                    Margin = Padding.Empty
                };
                chip.Controls.Add(num);
                chip.Controls.Add(txt);
                if (w.Unhearable)
                    new ToolTip().SetToolTip(txt, "The voice model cannot hear this word - say its number");
                _flow.Controls.Add(chip);
            }
            _flow.ResumeLayout();

            // Size to content, capped to a share of the screen; past the cap the
            // flow panel scrolls. A patent claim can run past 200 words and a
            // popup taller than the screen helps nobody.
            var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
            var maxW = Math.Min(UiScale.Pixels(720), screen.Width * 6 / 10);
            var maxH = screen.Height / 2;
            Width = maxW;
            _flow.PerformLayout();
            var contentH = _flow.Controls.Count == 0 ? UiScale.Pixels(30)
                         : _flow.Controls[_flow.Controls.Count - 1].Bottom + UiScale.Pixels(8);
            Height = Math.Min(maxH, contentH + _title.Height + _hint.Height + Padding.Vertical + UiScale.Pixels(8));

            if (words.Count > 99)
                _hint.Text = "say \"select 12\" or \"select 12 to 14\"  ·  words past 99 cannot be said  ·  \"cancel\" closes";
        }

        /// <summary>Same placement rule as the TermLens popup: near the point, on screen.</summary>
        private void PositionNear(Point screenAnchor)
        {
            var screen = Screen.FromPoint(screenAnchor).WorkingArea;
            int x = screenAnchor.X + UiScale.Pixels(8);
            int y = screenAnchor.Y + UiScale.Pixels(20);
            if (x + Width > screen.Right) x = screen.Right - Width - UiScale.Pixels(4);
            if (y + Height > screen.Bottom) y = screenAnchor.Y - Height - UiScale.Pixels(8);
            if (x < screen.Left) x = screen.Left + UiScale.Pixels(4);
            if (y < screen.Top) y = screen.Top + UiScale.Pixels(4);
            Location = new Point(x, y);
        }
    }
}
