using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// The pop-up shown when a document opens with source text outside any
    /// segment (see <see cref="UnsegmentedTextAlert"/>, which decides when).
    /// It is modeless and is shown without taking the focus, so typing in the
    /// editor carries on. Owned by Studio's main window, so it cannot fall
    /// behind Studio. "Show list" opens the same list as the TermLens panel's
    /// yellow line, so it works whether or not that panel is shown.
    ///
    /// <para>Laid out with a TableLayoutPanel and scaled once by
    /// <see cref="DialogScale"/>, like the other code-built dialogs.</para>
    /// </summary>
    internal sealed class UnsegmentedTextAlertForm : Form
    {
        private const int ContentWidth = 380;
        private const int MaxFilesNamed = 3;
        private const string ShowListText = "Show list";
        private const string HintText =
            "Click " + ShowListText + " to see them, then click one to go to the nearest segment. " +
            "The text itself cannot be translated in the editor.";

        private readonly List<UnsegmentedHit> _hits;
        private readonly int _total;
        private readonly bool _severalFiles;
        private readonly Button _showList;
        private readonly RichTextBox _hint;
        private IntPtr _owner;

        public string DocumentKey { get; }
        public string ResultsKey { get; }

        /// <summary>The user did something with it: a button, the link or the close box.</summary>
        public bool Seen { get; private set; }

        protected override bool ShowWithoutActivation => true;

        public UnsegmentedTextAlertForm(string documentKey, string resultsKey, IList<UnsegmentedFileResult> withHits)
        {
            DocumentKey = documentKey;
            ResultsKey = resultsKey;
            _hits = withHits.SelectMany(r => r.Hits).ToList();
            _total = withHits.Sum(r => r.HitCount);
            _severalFiles = withHits.Count > 1;

            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None;   // DialogScale.Apply, below
            Text = "Supervertaler for Trados";
            Icon = IconHelper.AppIcon;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.Manual;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9F);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var root = new TableLayoutPanel
            {
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Padding = new Padding(16, 14, 16, 12),
                GrowStyle = TableLayoutPanelGrowStyle.AddRows
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ContentWidth));

            var icon = new PictureBox
            {
                Image = SystemIcons.Warning.ToBitmap(),
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(32, 32),
                Margin = new Padding(0, 2, 12, 0)
            };

            var heading = new Label
            {
                Text = "Source text outside any segment",
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                AutoSize = true,
                MaximumSize = new Size(ContentWidth, 0),
                Margin = new Padding(0, 0, 0, 6)
            };
            var message = new Label
            {
                Text = Describe(withHits, _total),
                AutoSize = true,
                MaximumSize = new Size(ContentWidth, 0),
                UseMnemonic = false,
                Margin = new Padding(0, 0, 0, 6)
            };
            // Not a Label: a Label cannot make "Show list" bold. Filled and
            // sized in OnLoad, once its handle exists at its scaled width.
            _hint = new ReadOnlyText
            {
                Width = ContentWidth,
                Height = 1,
                Margin = new Padding(0, 0, 0, 8)
            };
            var help = new LinkLabel
            {
                Text = "Why this matters and what to do",
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 12)
            };
            help.LinkClicked += (s, e) =>
            {
                Seen = true;
                HelpSystem.OpenHelp(HelpSystem.Topics.UnsegmentedText);
            };

            var ok = new Button
            {
                Text = "OK",
                AutoSize = true,
                MinimumSize = new Size(88, 28),
                FlatStyle = FlatStyle.System,
                Margin = new Padding(8, 0, 0, 0)
            };
            ok.Click += (s, e) =>
            {
                Seen = true;
                Close();
            };
            _showList = new Button
            {
                Text = ShowListText,
                AutoSize = true,
                MinimumSize = new Size(88, 28),
                FlatStyle = FlatStyle.System,
                Margin = new Padding(0)
            };
            _showList.Click += (s, e) =>
            {
                Seen = true;
                UnsegmentedTextMenu.Show(_showList, _hits, _total, _severalFiles);
            };
            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                WrapContents = false,
                Margin = new Padding(0)
            };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(_showList);

            root.Controls.Add(icon, 0, 0);
            root.SetRowSpan(icon, 4);
            root.Controls.Add(heading, 1, 0);
            root.Controls.Add(message, 1, 1);
            root.Controls.Add(_hint, 1, 2);
            root.Controls.Add(help, 1, 3);
            root.Controls.Add(buttons, 0, 4);
            root.SetColumnSpan(buttons, 2);
            Controls.Add(root);

            AcceptButton = ok;
            CancelButton = ok;

            DialogScale.Apply(this);
            ResumeLayout(false);
            PerformLayout();
        }

        /// <summary>
        /// "Trados left 2 stretches of source text in X outside any segment. …",
        /// naming up to three files.
        /// </summary>
        internal static string Describe(IList<UnsegmentedFileResult> withHits, int total)
        {
            string what = total == 1 ? "1 stretch of source text" : total + " stretches of source text";
            string where;
            if (withHits.Count == 1)
                where = " in " + UnsegmentedTextMonitor.DisplayName(withHits[0].FilePath);
            else
            {
                var named = withHits.Take(MaxFilesNamed)
                    .Select(r => r.HitCount + " in " + UnsegmentedTextMonitor.DisplayName(r.FilePath))
                    .ToList();
                int rest = withHits.Count - named.Count;
                if (rest > 0) named.Add(rest == 1 ? "more in 1 other file" : $"more in {rest} other files");
                where = " (" + (named.Count == 1
                    ? named[0]
                    : string.Join(", ", named.Take(named.Count - 1)) + " and " + named.Last()) + ")";
            }
            string consequence = total == 1
                ? "It is not in the editor, QA and the word count skip it, and it will be exported untranslated."
                : "They are not in the editor, QA and the word count skip them, and they will be exported untranslated.";
            return "Trados left " + what + where + " outside any segment. " + consequence;
        }

        /// <summary>Shows it over <paramref name="owner"/>, Studio's main window, without taking the focus.</summary>
        public void ShowOver(IntPtr owner)
        {
            _owner = owner;
            if (owner != IntPtr.Zero)
                Show(new OwnerWindow(owner));
            else
            {
                // No owner to keep it above Studio: give it a taskbar button so it
                // cannot be lost behind Studio.
                ShowInTaskbar = true;
                Show();
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            FillHint();
            PerformLayout();

            // Centred on Studio's window, kept on the screen it is on.
            RECT r;
            var area = _owner != IntPtr.Zero && GetWindowRect(_owner, out r) && r.Right > r.Left && r.Bottom > r.Top
                ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)
                : Screen.PrimaryScreen.WorkingArea;
            var screen = Screen.FromRectangle(area).WorkingArea;
            int x = area.Left + (area.Width - Width) / 2;
            int y = area.Top + (area.Height - Height) / 2;
            x = Math.Max(screen.Left, Math.Min(x, screen.Right - Width));
            y = Math.Max(screen.Top, Math.Min(y, screen.Bottom - Height));
            Location = new Point(x, y);
        }

        /// <summary>
        /// Puts the hint in, with "Show list" bold, and makes the box exactly as
        /// tall as the text: the control reports that height as it lays the
        /// text out, at the width and font size the screen's DPI gave it (as in
        /// NumberPopupForm).
        /// </summary>
        private void FillHint()
        {
            if (!_hint.IsHandleCreated) { var unused = _hint.Handle; }
            int textHeight = 0;
            ContentsResizedEventHandler measure = (s, e) => textHeight = e.NewRectangle.Height;
            _hint.ContentsResized += measure;
            try
            {
                _hint.Text = HintText;
                _hint.Select(HintText.IndexOf(ShowListText, StringComparison.Ordinal), ShowListText.Length);
                using (var bold = new Font(_hint.Font, FontStyle.Bold))
                    _hint.SelectionFont = bold;
                _hint.Select(0, 0);
            }
            finally
            {
                _hint.ContentsResized -= measure;
            }
            if (textHeight <= 0)
                textHeight = _hint.GetPositionFromCharIndex(_hint.TextLength - 1).Y + _hint.Font.Height;
            _hint.Height = textHeight;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // The close box, Esc or OK. A close by UnsegmentedTextAlert has
            // already decided whether this counted as seen.
            Seen = true;
            base.OnFormClosing(e);
        }

        /// <summary>
        /// Text to read, not to edit: no border or scroll bars, never in the tab
        /// order, and the mouse can neither select in it nor scroll it, so no
        /// caret appears and nothing moves when it is clicked.
        /// </summary>
        private sealed class ReadOnlyText : RichTextBox
        {
            private const int WM_LBUTTONDOWN = 0x0201, WM_MOUSEHWHEEL = 0x020E;

            public ReadOnlyText()
            {
                ReadOnly = true;
                BorderStyle = BorderStyle.None;
                BackColor = SystemColors.Control;
                ScrollBars = RichTextBoxScrollBars.None;
                WordWrap = true;
                DetectUrls = false;
                ShortcutsEnabled = false;
                TabStop = false;
                Cursor = Cursors.Arrow;
            }

            protected override void WndProc(ref Message m)
            {
                // Button presses, double clicks and the wheel. A click still
                // brings the pop-up forward: WM_MOUSEACTIVATE comes before these.
                if (m.Msg >= WM_LBUTTONDOWN && m.Msg <= WM_MOUSEHWHEEL) return;
                base.WndProc(ref m);
            }
        }

        private sealed class OwnerWindow : IWin32Window
        {
            public OwnerWindow(IntPtr handle) { Handle = handle; }
            public IntPtr Handle { get; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    }
}
