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
    /// <para><b>Why it must not take focus.</b> Selection works by driving Studio's
    /// own search and shrinking the result with Shift+Left, both of which need the
    /// editor to keep keyboard focus. A window that activated would break the very
    /// selection it exists to make. Same construction as <see cref="TermPopup"/>:
    /// WS_EX_NOACTIVATE, ShowWithoutActivation, TopMost, placed near the cursor
    /// and clamped to the screen. Escape reaches it through the application
    /// message filter; the close button works because mouse clicks arrive
    /// regardless of activation.</para>
    ///
    /// <para><b>Two layouts.</b> <c>sentence</c> - the segment as continuous text,
    /// punctuation and all, each word followed by a small blue superscript number
    /// like a footnote, so it reads as the sentence it is and the numbers come
    /// second. <c>chips</c> - number then word, on a grid, the first version.
    /// The switch is on the popup itself, where the opinion forms; the choice is
    /// saved. Michael's ask (2026-09-17) after two hours' use: "the text part
    /// more continuous, so that it looks like an actual sentence, and then you
    /// read the sentence, pick the words, and then the numbers come at you
    /// secondarily" - TermLens's lower row being the reference.</para>
    ///
    /// <para><b>Deliberately transient.</b> Opens on a command or on a failed
    /// selection; closes on a number, a spoken word that selects, "cancel",
    /// Escape, the close button, or a segment change. While it is open the number
    /// words are in the recogniser's grammar; the moment it closes they leave.</para>
    /// </summary>
    internal sealed class NumberPopupForm : Form
    {
        private static readonly Color BorderColor = Color.FromArgb(190, 190, 190);
        private static readonly Color NumberColor = Color.FromArgb(30, 90, 158);
        /// <summary>
        /// The footnote numbers in the sentence layout: muted, so they sit behind
        /// the words rather than beside them. The link blue in bold was "too
        /// distracting - a hard time focusing on the words and seeing the numbers
        /// at the same time" (2026-09-17). The eye should land on the word first.
        /// </summary>
        private static readonly Color FootnoteColor = Color.FromArgb(120, 140, 170);
        private static readonly Color UnhearableBack = Color.FromArgb(255, 243, 208);   // the NT chip yellow
        private static readonly Color HintColor = Color.FromArgb(110, 110, 110);

        internal const string StyleSentence = "sentence";
        internal const string StyleChips = "chips";

        private static NumberPopupForm _instance;

        /// <summary>One numbered word: its text and offset in the plain segment text.</summary>
        internal sealed class Word
        {
            public string Text;
            public int Start;
            public bool Unhearable;
        }

        private List<Word> _words = new List<Word>();
        private string _plain = "";
        private string _title_ = "";
        private bool _inSource;

        private readonly Label _title;
        private readonly Label _hint;
        private readonly LinkLabel _layout;
        private readonly Panel _body;

        public static bool IsOpen => _instance != null && !_instance.IsDisposed && _instance.Visible;
        public static bool InSource => IsOpen && _instance._inSource;
        public static int WordCount => IsOpen ? _instance._words.Count : 0;

        private static string Style
        {
            get
            {
                var s = Settings.SettingsService.Current?.NumberPopupStyle;
                return string.Equals(s, StyleChips, StringComparison.OrdinalIgnoreCase) ? StyleChips : StyleSentence;
            }
        }

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

            var footer = new Panel { Dock = DockStyle.Bottom, Height = UiScale.Pixels(20) };
            _hint = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                ForeColor = HintColor,
                Font = new Font("Segoe UI", UiScale.FontSize(8f)),
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "say \"select 12\" or \"select 12 to 14\"  ·  \"cancel\" closes"
            };
            // The layout switch lives here, where the opinion forms, not in a
            // settings dialog. One click, saved.
            _layout = new LinkLabel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                Font = new Font("Segoe UI", UiScale.FontSize(8f)),
                LinkColor = HintColor,
                ActiveLinkColor = NumberColor,
                VisitedLinkColor = HintColor,
                LinkBehavior = LinkBehavior.HoverUnderline,
                TextAlign = ContentAlignment.MiddleRight,
                Padding = new Padding(0, UiScale.Pixels(2), 0, 0)
            };
            _layout.Click += (s, e) => ToggleStyle();
            footer.Controls.Add(_hint);
            footer.Controls.Add(_layout);

            _body = new Panel { Dock = DockStyle.Fill };
            Controls.Add(_body);
            Controls.Add(_title);
            Controls.Add(footer);
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
        /// Shows the popup for one side of the segment. <paramref name="plain"/> is
        /// the side's plain text, which the sentence layout reproduces verbatim -
        /// the words carry offsets into it.
        /// </summary>
        public static void ShowFor(bool inSource, string title, List<Word> words, string plain)
        {
            if (_instance == null || _instance.IsDisposed) _instance = new NumberPopupForm();
            _instance.Populate(inSource, title, words ?? new List<Word>(), plain ?? "");
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
        /// (1-based, inclusive), or null when out of range. The span is the segment
        /// text between the first word's start and the last word's end, so a range
        /// keeps the punctuation and spaces between them.
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

        private void ToggleStyle()
        {
            try
            {
                var s = Settings.SettingsService.Current;
                if (s == null) return;
                s.NumberPopupStyle = Style == StyleSentence ? StyleChips : StyleSentence;
                s.Save();
            }
            catch { }
            Populate(_inSource, _title_, _words, _plain);
        }

        private void Populate(bool inSource, string title, List<Word> words, string plain)
        {
            _inSource = inSource;
            _words = words;
            _plain = plain;
            _title_ = title;
            _title.Text = title ?? (inSource ? "Source words" : "Target words");
            _layout.Text = Style == StyleSentence ? "layout: sentence · chips" : "layout: sentence · chips";
            _layout.Links.Clear();
            // Underline only the OTHER layout - the one a click switches to.
            var other = Style == StyleSentence ? "chips" : "sentence";
            var at = _layout.Text.IndexOf(other, StringComparison.Ordinal);
            if (at >= 0) _layout.Links.Add(at, other.Length);

            var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
            var maxW = Math.Min(UiScale.Pixels(720), screen.Width * 6 / 10);
            var maxH = screen.Height / 2;
            Width = maxW;

            _body.SuspendLayout();
            foreach (Control c in _body.Controls.Cast<Control>().ToList()) { _body.Controls.Remove(c); c.Dispose(); }
            int contentH = Style == StyleChips ? PopulateChips(words) : PopulateSentence(words, plain);
            _body.ResumeLayout();

            // Size to content, capped to a share of the screen; past the cap the
            // body scrolls. A patent claim can run past 200 words and a popup
            // taller than the screen helps nobody.
            Height = Math.Min(maxH, contentH + _title.Height + UiScale.Pixels(20) + Padding.Vertical + UiScale.Pixels(12));

            _hint.Text = words.Count > 99
                ? "say \"select 12\" or \"select 12 to 14\"  ·  words past 99 cannot be said  ·  \"cancel\" closes"
                : "say \"select 12\" or \"select 12 to 14\"  ·  \"cancel\" closes";
        }

        /// <summary>
        /// The segment as it reads, each word followed by its number as a small blue
        /// superscript. Built from the plain text and the words' offsets, so the
        /// punctuation and spacing between words are the segment's own. Returns the
        /// content height.
        /// </summary>
        private int PopulateSentence(List<Word> words, string plain)
        {
            var rtb = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = Color.White,
                TabStop = false,
                Cursor = Cursors.Arrow,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                WordWrap = true,
                DetectUrls = false,
                HideSelection = true,
                ShortcutsEnabled = false
            };
            var textFont = new Font("Segoe UI", UiScale.FontSize(10.5f));
            var numFont = new Font("Segoe UI", UiScale.FontSize(7f));
            var raise = UiScale.Pixels(5);
            _body.Controls.Add(rtb);

            // The text's real height, reported by the control as it lays the text
            // out. Asking GetPositionFromCharIndex before layout gave a nonsense
            // answer and the popup sized itself to the cap - a paragraph of text
            // above a screen's worth of white (2026-09-17). The handle must exist
            // for the event to fire, so it is created first, at the final width.
            int textHeight = 0;
            rtb.ContentsResized += (s, e) => textHeight = e.NewRectangle.Height;
            rtb.Width = _body.ClientSize.Width;
            var handle = rtb.Handle;   // forces creation

            int pos = 0;
            for (int n = 0; n < words.Count; n++)
            {
                var w = words[n];
                if (w.Start > pos && w.Start <= plain.Length)
                    Append(rtb, plain.Substring(pos, w.Start - pos), textFont, Color.Black, Color.White, 0);
                Append(rtb, w.Text, textFont, Color.Black, w.Unhearable ? UnhearableBack : Color.White, 0);
                Append(rtb, (n + 1).ToString(), numFont, FootnoteColor, Color.White, raise);
                pos = Math.Min(plain.Length, w.Start + w.Text.Length);
            }
            if (pos < plain.Length)
                Append(rtb, plain.Substring(pos), textFont, Color.Black, Color.White, 0);
            rtb.Select(0, 0);

            if (textHeight <= 0)
            {
                // No event (should not happen with a handle): fall back to a measure
                // of the last character, which is at least right once laid out.
                var last = Math.Max(0, rtb.TextLength - 1);
                textHeight = rtb.GetPositionFromCharIndex(last).Y + textFont.Height;
            }
            return Math.Max(UiScale.Pixels(30), textHeight + textFont.Height / 2);
        }

        private static void Append(RichTextBox rtb, string text, Font font, Color fore, Color back, int offset)
        {
            if (string.IsNullOrEmpty(text)) return;
            var start = rtb.TextLength;
            rtb.AppendText(text);
            rtb.Select(start, text.Length);
            rtb.SelectionFont = font;
            rtb.SelectionColor = fore;
            rtb.SelectionBackColor = back;
            rtb.SelectionCharOffset = offset;
            rtb.Select(rtb.TextLength, 0);
            rtb.SelectionCharOffset = 0;
        }

        /// <summary>Number then word, on a wrapping grid - the first layout. Returns the content height.</summary>
        private int PopulateChips(List<Word> words)
        {
            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                WrapContents = true,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, UiScale.Pixels(4), 0, UiScale.Pixels(4))
            };
            _body.Controls.Add(flow);
            var numFont = new Font("Segoe UI", UiScale.FontSize(7.5f), FontStyle.Bold);
            var wordFont = new Font("Segoe UI", UiScale.FontSize(10f));
            flow.SuspendLayout();
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
                var num = new Label { Text = (n + 1).ToString(), AutoSize = true, Font = numFont, ForeColor = NumberColor, Location = new Point(0, 0), Margin = Padding.Empty };
                var txt = new Label { Text = w.Text, AutoSize = true, Font = wordFont, Location = new Point(num.PreferredWidth + UiScale.Pixels(2), 0), Margin = Padding.Empty };
                chip.Controls.Add(num);
                chip.Controls.Add(txt);
                if (w.Unhearable) new ToolTip().SetToolTip(txt, "The voice model cannot hear this word - say its number");
                flow.Controls.Add(chip);
            }
            flow.ResumeLayout();
            flow.PerformLayout();
            return flow.Controls.Count == 0 ? UiScale.Pixels(30)
                 : flow.Controls[flow.Controls.Count - 1].Bottom + UiScale.Pixels(8);
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
