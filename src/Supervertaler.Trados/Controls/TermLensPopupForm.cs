using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// Borderless top-most popup that mirrors the docked TermLens panel for the
    /// active segment. Designed for keyboard-only term selection on small screens
    /// where keeping the docked panel always-visible costs too much vertical space.
    ///
    /// Lifecycle:
    ///   - Opened via TermLensEditorViewPart.HandleTermLensPopup() — bound to
    ///     Ctrl-tap (primary) and Ctrl+Alt+G (fallback). Re-pressing the open
    ///     shortcut closes the popup (toggle); cycling is keyboard-only inside.
    ///   - Right / Down / Tab → next; Left / Up / Shift+Tab → previous.
    ///   - Enter inserts the currently-highlighted match into the target segment
    ///     and closes the popup.
    ///   - Escape, click-outside, or focus loss closes without inserting.
    ///   - Mouse-clicking a chip inserts via the same docked-panel insertion flow
    ///     and also closes the popup.
    /// </summary>
    public class TermLensPopupForm : Form
    {
        private readonly FlowLayoutPanel _flowPanel;
        private readonly Label _hintLabel;
        private readonly List<TermBlock> _blocks = new List<TermBlock>();
        private readonly Action<int> _insertByOneBasedIndex;
        private int _currentIndex = -1;

        // Guards against any second invocation of RequestInsert on the same
        // popup — keeps the insertion exactly-once even if the click path
        // and the keyboard path race, or if a block fires its event twice.
        private int? _pendingInsertOneBased;

        public TermLensPopupForm(
            TermLensControl dockedControl,
            string sourceText,
            Action<int> insertByOneBasedIndex)
        {
            if (dockedControl == null) throw new ArgumentNullException(nameof(dockedControl));
            _insertByOneBasedIndex = insertByOneBasedIndex
                ?? throw new ArgumentNullException(nameof(insertByOneBasedIndex));

            SuspendLayout();

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            BackColor = Color.White;
            // Subtle 1px border so the borderless form is visible against any backdrop.
            Padding = new Padding(UiScale.Pixels(1));

            // Inner panel forms the visible card surface (with a 1px border via the
            // form's BackColor showing through Padding).
            var card = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                Padding = new Padding(UiScale.Pixels(8), UiScale.Pixels(6), UiScale.Pixels(8), UiScale.Pixels(4))
            };
            Controls.Add(card);
            BackColor = Color.FromArgb(180, 180, 180); // border tone

            // Keyboard-only popup — no visible help button, no mouse affordances
            // beyond clicking a chip to insert. Help shortcut keys removed too:
            // F1 is owned by Trados Studio (its handler beats application-level
            // message filters) so a working in-popup help binding would need a
            // Win32 low-level keyboard hook, which isn't worth the complexity.
            // Users can read the help page directly at supervertaler.gitbook.io.
            _hintLabel = new Label
            {
                Dock = DockStyle.Bottom,
                AutoSize = false,
                Height = UiScale.Pixels(18),
                Text = "← → cycle  ·  Enter insert  ·  E edit  ·  Esc close",
                ForeColor = Color.FromArgb(120, 120, 120),
                Font = new Font(Font.FontFamily, Font.Size - 1f, FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleLeft
            };

            _flowPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                AutoScroll = true,
                WrapContents = true,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor = Color.White
            };

            card.Controls.Add(_flowPanel);
            card.Controls.Add(_hintLabel);

            // Dimensions: cap width so a long sentence wraps; the form will
            // auto-resize height once the flow panel measures.
            int maxPopupWidth = UiScale.Pixels(560);
            ClientSize = new Size(maxPopupWidth, UiScale.Pixels(120));

            int maxBlockWidth = maxPopupWidth - card.Padding.Horizontal - UiScale.Pixels(20);
            if (maxBlockWidth < 60) maxBlockWidth = 60;

            // Reuse the docked panel's matcher/index — no termbase reload.
            // Pass wireInsertBubble: false so chip clicks DON'T bubble through
            // to the docked panel's existing OnTermInsertRequested handler;
            // we wire our own handler that routes click and keyboard Enter
            // through the same RequestInsert path. Otherwise the bubble fires
            // alongside our own flow and the term gets inserted twice.
            var built = dockedControl.BuildSegmentBlocks(sourceText, maxBlockWidth, wireInsertBubble: false);
            foreach (var ctrl in built)
            {
                _flowPanel.Controls.Add(ctrl);
                if (ctrl is TermBlock tb)
                {
                    _blocks.Add(tb);
                    var captured = tb; // capture so the lambda picks up THIS block's index
                    tb.TermInsertRequested += (s, e) => RequestInsert(captured.ShortcutIndex + 1);
                }
            }

            // Size the form to the flow panel's natural height (capped).
            ResizeToContent(maxPopupWidth);

            if (_blocks.Count > 0)
                SetCurrent(0);

            ResumeLayout(false);
        }

        private void ResizeToContent(int desiredWidth)
        {
            // Measure the flow panel's preferred size at the target width, then
            // size the form to fit. Cap at half the working area height so we
            // don't overflow on very long segments — AutoScroll will scroll.
            var pref = _flowPanel.GetPreferredSize(new Size(
                desiredWidth - Padding.Horizontal - UiScale.Pixels(20),
                int.MaxValue));

            int chrome = Padding.Vertical
                + _hintLabel.Height
                + UiScale.Pixels(12); // card padding top+bottom

            int desiredHeight = Math.Min(
                pref.Height + chrome + UiScale.Pixels(8),
                Screen.PrimaryScreen.WorkingArea.Height / 2);

            ClientSize = new Size(desiredWidth, Math.Max(desiredHeight, UiScale.Pixels(80)));
        }

        /// <summary>
        /// Position the popup near the supplied screen point (typically the
        /// caret / cursor location), clamped to the working area of the screen
        /// it would otherwise spill off.
        /// </summary>
        public void PositionNear(Point screenAnchor)
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

        /// <summary>
        /// Move the highlighted "current match" to the next block, wrapping
        /// from last back to first.
        /// </summary>
        public void MoveCurrentNext() => SetCurrent(_currentIndex + 1);

        public void MoveCurrentPrevious() => SetCurrent(_currentIndex - 1);

        private void SetCurrent(int newIdx)
        {
            if (_blocks.Count == 0) return;

            int n = _blocks.Count;
            // Modulo that handles negative indexes correctly.
            int wrapped = ((newIdx % n) + n) % n;

            if (_currentIndex >= 0 && _currentIndex < n)
                _blocks[_currentIndex].IsCurrent = false;
            _currentIndex = wrapped;
            _blocks[_currentIndex].IsCurrent = true;

            // Make sure the highlighted block is visible inside the scroll area.
            _flowPanel.ScrollControlIntoView(_blocks[_currentIndex]);
        }

        /// <summary>
        /// Single insertion entry point for both mouse-click and keyboard Enter.
        /// Defers the actual insert until FormClosed so focus has returned to
        /// the Trados editor before Selection.Target.Replace runs, and uses
        /// _pendingInsertOneBased as a guard so a second call (from a stray
        /// re-fire of a block event, or the click+Enter racing each other)
        /// is a no-op.
        /// </summary>
        private void RequestInsert(int oneBased)
        {
            if (_pendingInsertOneBased.HasValue) return;
            _pendingInsertOneBased = oneBased;
            FormClosed += DoPendingInsert;
            Close();
        }

        private void DoPendingInsert(object sender, FormClosedEventArgs e)
        {
            if (!_pendingInsertOneBased.HasValue) return;
            int idx = _pendingInsertOneBased.Value;
            _pendingInsertOneBased = null;
            _insertByOneBasedIndex(idx);
        }

        // Note: no OnDeactivate-Close. The chip-hover tooltip (TermPopup)
        // briefly steals focus, which would close the popup mid-hover and
        // strand the user. The popup is keyboard-only anyway — Esc closes,
        // Ctrl-tap toggles. Mouse users can still click a chip to insert
        // (which closes), or Esc, or click outside (which leaves it open
        // until they press a key — a small papercut traded for not losing
        // the popup to spurious focus events).

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Right:
                case Keys.Down:
                case Keys.Tab:
                    MoveCurrentNext();
                    return true;

                case Keys.Left:
                case Keys.Up:
                case Keys.Shift | Keys.Tab:
                    MoveCurrentPrevious();
                    return true;

                case Keys.Enter:
                    if (_currentIndex >= 0 && _currentIndex < _blocks.Count)
                        RequestInsert(_currentIndex + 1);
                    return true;

                case Keys.E:
                    EditCurrentMatch();
                    return true;

                case Keys.Escape:
                    Close();
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>
        /// Open the term-entry editor on the current match. Captures the
        /// entry data before closing the popup so the editor doesn't
        /// reference a disposed TermBlock; routes through the same
        /// OnTermEditRequested handler the docked panel's right-click
        /// "Edit Term…" menu uses.
        /// </summary>
        private void EditCurrentMatch()
        {
            if (_currentIndex < 0 || _currentIndex >= _blocks.Count) return;
            var block = _blocks[_currentIndex];
            var entry = block.PrimaryEntry;
            if (entry == null || entry.IsMultiTerm) return; // MultiTerm is read-only

            // Snapshot the entry list — block.Entries is a live read-only
            // view that's invalid after the popup disposes.
            var allEntries = new List<TermEntry>(block.Entries);

            FormClosed += (s, e) =>
                TermLensEditorViewPart.HandleEditCurrentTerm(entry, allEntries);
            Close();
        }
    }
}
