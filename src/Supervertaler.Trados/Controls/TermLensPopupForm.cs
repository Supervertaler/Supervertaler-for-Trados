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
    ///   - Opened via TermLensEditorViewPart.HandleTermLensPopup() – bound to
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
        // popup – keeps the insertion exactly-once even if the click path
        // and the keyboard path race, or if a block fires its event twice.
        private int? _pendingInsertOneBased;

        // Transient hint state – used to flash a short message in place of
        // the keyboard-shortcut hint label (e.g. when the user presses E on
        // a read-only MultiTerm match). Auto-restores after a few seconds.
        private string _originalHintText;
        private Color _originalHintColor;
        private System.Windows.Forms.Timer _hintRestoreTimer;

        // Auto-close: poll the cursor position so any mouse movement closes
        // the popup. Captured once in OnShown so the cursor's position at
        // the moment the popup appears is the baseline.
        private System.Windows.Forms.Timer _autoCloseTimer;
        private Point _initialCursorPos;
        private bool _cursorBaselineCaptured;

        // Set to true while we're deliberately showing a companion form
        // (the metadata TermPopup, or the term editor dialog). Suppresses
        // the OnDeactivate-Close so showing those doesn't tear down the
        // parent popup.
        private bool _suppressDeactivateClose;

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

            // Keyboard-only popup – no visible help button, no mouse affordances
            // beyond clicking a chip to insert. Help shortcut keys removed too:
            // F1 is owned by Trados Studio (its handler beats application-level
            // message filters) so a working in-popup help binding would need a
            // Win32 low-level keyboard hook, which isn't worth the complexity.
            // Users can read the help page directly at docs.supervertaler.com.
            _hintLabel = new Label
            {
                Dock = DockStyle.Bottom,
                AutoSize = false,
                Height = UiScale.Pixels(18),
                Text = "← → cycle  ·  Enter insert  ·  E edit  ·  I info  ·  Esc close",
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

            // Sizing strategy:
            //   - maxAvailableWidth caps how wide the popup can ever be – a
            //     fraction of the screen the popup will appear on, with an
            //     absolute upper bound so it doesn't span 4K monitors.
            //   - Individual chips get the same cap (less the form's chrome)
            //     as their MaxWidth, so a single very long target term will
            //     still ellipsis-truncate rather than blow past the screen.
            //   - After the chips are added, ResizeToContent measures the
            //     flow panel's preferred size at maxAvailableWidth and
            //     SHRINKS the popup to fit in BOTH dimensions – short
            //     segments don't get a giant empty popup, long segments
            //     grow to show every chip.
            //
            // Earlier versions hard-capped width at 560 px regardless of
            // screen, which truncated chips on long sentences (reported on
            // patent-style segments running past 100 chars).
            var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
            int maxAvailableWidth = Math.Min(
                UiScale.Pixels(1200),
                screen.Width - UiScale.Pixels(60));
            ClientSize = new Size(maxAvailableWidth, UiScale.Pixels(120));

            int maxBlockWidth = maxAvailableWidth - card.Padding.Horizontal - UiScale.Pixels(40);
            if (maxBlockWidth < 60) maxBlockWidth = 60;

            // Reuse the docked panel's matcher/index – no termbase reload.
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

            // Shrink-to-fit in both dimensions, capped at the screen.
            ResizeToContent(maxAvailableWidth, screen);

            if (_blocks.Count > 0)
                SetCurrent(0);

            ResumeLayout(false);
        }

        private void ResizeToContent(int maxAvailableWidth, Rectangle screen)
        {
            int chromeH = Padding.Horizontal + UiScale.Pixels(20);  // card horizontal padding
            int chromeV = Padding.Vertical + _hintLabel.Height + UiScale.Pixels(12); // card vertical padding

            // Measure the flow panel's preferred size at the maximum allowed
            // width – chips wrap inside that constraint.
            var pref = _flowPanel.GetPreferredSize(new Size(
                maxAvailableWidth - chromeH,
                int.MaxValue));

            // Width: shrink to actual content width (so short segments get a
            // small popup), capped at maxAvailableWidth.
            int desiredWidth = Math.Min(pref.Width + chromeH, maxAvailableWidth);
            int minWidth = UiScale.Pixels(280);
            desiredWidth = Math.Max(desiredWidth, minWidth);

            // Height: shrink to fit, cap at 80 % of the screen height (vs the
            // earlier 50 % cap which truncated multi-line chip rows on long
            // sentences). AutoScroll handles anything beyond that.
            int maxHeight = screen.Height * 4 / 5;
            int desiredHeight = Math.Min(pref.Height + chromeV + UiScale.Pixels(8), maxHeight);
            desiredHeight = Math.Max(desiredHeight, UiScale.Pixels(80));

            ClientSize = new Size(desiredWidth, desiredHeight);
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

        // Auto-close behaviour:
        //   - Any mouse movement (>4 px from the baseline captured in OnShown)
        //     closes the popup. Polled via a 75 ms timer so we don't need a
        //     low-level Win32 mouse hook.
        //   - Focus loss (switching apps, clicking outside) closes the popup
        //     via OnDeactivate. The companion metadata TermPopup uses
        //     WS_EX_NOACTIVATE so showing it does NOT trigger our deactivate.
        //     The editor dialog flow sets _suppressDeactivateClose to keep the
        //     two close paths from racing.
        //   - Any key not handled by ProcessCmdKey closes the popup, except
        //     pure-modifier presses (Ctrl/Shift/Alt alone) which would
        //     otherwise close the popup the moment the user releases the
        //     opening Ctrl-tap.

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _initialCursorPos = Cursor.Position;
            _cursorBaselineCaptured = true;

            _autoCloseTimer = new System.Windows.Forms.Timer { Interval = 75 };
            _autoCloseTimer.Tick += OnAutoCloseTick;
            _autoCloseTimer.Start();
        }

        private void OnAutoCloseTick(object sender, EventArgs e)
        {
            if (!_cursorBaselineCaptured) return;
            if (IsDisposed || !Visible) return;

            var pos = Cursor.Position;
            int dx = Math.Abs(pos.X - _initialCursorPos.X);
            int dy = Math.Abs(pos.Y - _initialCursorPos.Y);
            // 4 px tolerance: the cursor sometimes shifts by 1 px when a new
            // top-most window appears. Anything past that is the user moving
            // the mouse, which signals "I'm doing something else now".
            if (dx > 4 || dy > 4)
            {
                _autoCloseTimer.Stop();
                Close();
            }
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (_suppressDeactivateClose) return;
            if (IsDisposed) return;
            Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // Tear down auxiliary state regardless of how we got here.
            _autoCloseTimer?.Stop();
            _autoCloseTimer?.Dispose();
            _autoCloseTimer = null;
            _hintRestoreTimer?.Stop();
            _hintRestoreTimer?.Dispose();
            _hintRestoreTimer = null;
            // If the metadata popup is still showing, hide it too.
            try { TermPopup.GetInstance().HidePopup(); } catch { /* ignore */ }
            base.OnFormClosed(e);
        }

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

                case Keys.I:
                    ToggleMetadataForCurrent();
                    return true;

                case Keys.Escape:
                    Close();
                    return true;
            }

            // Any other keypress closes the popup, except pure modifier
            // presses (Ctrl/Shift/Alt on their own). Without this carve-out,
            // the keyup that follows the opening Ctrl-tap would close the
            // popup the moment it appeared.
            if (!IsPureModifier(keyData))
            {
                Close();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private static bool IsPureModifier(Keys keyData)
        {
            var key = keyData & Keys.KeyCode;
            return key == Keys.None
                || key == Keys.ControlKey || key == Keys.LControlKey || key == Keys.RControlKey
                || key == Keys.ShiftKey   || key == Keys.LShiftKey   || key == Keys.RShiftKey
                || key == Keys.Menu       || key == Keys.LMenu       || key == Keys.RMenu
                || key == Keys.LWin       || key == Keys.RWin
                || key == Keys.Capital    || key == Keys.NumLock     || key == Keys.Scroll;
        }

        /// <summary>
        /// Toggles the companion metadata popup (TermPopup) for the
        /// currently-highlighted block. If the popup is already showing,
        /// hide it; otherwise build the metadata lines for the current block
        /// and show it below the chip. TermPopup uses WS_EX_NOACTIVATE so
        /// showing it does not steal focus from this form.
        /// </summary>
        private void ToggleMetadataForCurrent()
        {
            if (_currentIndex < 0 || _currentIndex >= _blocks.Count) return;
            var block = _blocks[_currentIndex];
            var popup = TermPopup.GetInstance();

            if (popup.Visible)
            {
                popup.HidePopup();
                return;
            }

            var lines = block.BuildMetadataLines();
            // Show the metadata popup. WS_EX_NOACTIVATE prevents it from
            // stealing focus, so OnDeactivate won't fire and tear us down.
            // The suppress flag is a defence-in-depth in case the platform
            // briefly raises a deactivate anyway on some Windows versions.
            _suppressDeactivateClose = true;
            try
            {
                popup.ShowBelow(block, lines);
            }
            finally
            {
                _suppressDeactivateClose = false;
            }
        }

        /// <summary>
        /// Open the term-entry editor on the current match. Captures the
        /// entry data before closing the popup so the editor doesn't
        /// reference a disposed TermBlock; routes through the same
        /// OnTermEditRequested handler the docked panel's right-click
        /// "Edit Term…" menu uses. MultiTerm matches are read-only – we
        /// flash a hint instead of opening anything (Trados Studio 2026
        /// is expected to replace MultiTerm with a SQLite-backed
        /// terminology system, so we're not investing in MultiTerm
        /// write support here).
        ///
        /// Why Hide() + owner.BeginInvoke instead of "FormClosed += …; Close()":
        /// The editor dialog opens via ShowDialog, which is modal and blocks
        /// the message pump. When invoked from inside FormClosed, the pump is
        /// blocked before the area beneath the popup has been repainted – the
        /// popup's pixels stay visible behind the modal until it closes.
        /// Hiding synchronously and deferring Close() + editor-open to the
        /// owner's message loop lets WM_PAINT for the freshly-uncovered area
        /// run first, so the editor opens onto a clean screen.
        /// </summary>
        private void EditCurrentMatch()
        {
            if (_currentIndex < 0 || _currentIndex >= _blocks.Count) return;
            var block = _blocks[_currentIndex];
            var entry = block.PrimaryEntry;
            if (entry == null) return;

            if (entry.IsMultiTerm)
            {
                ShowTransientHint(
                    "MultiTerm entries are read-only – edit them in Trados → Termbase Viewer.");
                return;
            }

            // Snapshot the entry list – block.Entries is a live read-only
            // view that's invalid after the popup disposes.
            var allEntries = new List<TermEntry>(block.Entries);

            // We deliberately Hide() before Close()-ing in BeginInvoke. Without
            // this flag, the Hide() would trigger OnDeactivate → Close(), and
            // then BeginInvoke's Close() would race with the editor open. The
            // flag tells OnDeactivate to stand down for this transition; the
            // BeginInvoke handler is the authoritative closer.
            _suppressDeactivateClose = true;

            // 1. Hide immediately so the popup is visually gone right away.
            Hide();

            // 2. Defer Close() + editor open to the owner's message loop so
            //    the area underneath the popup gets repainted before the
            //    modal editor dialog blocks the pump.
            var owner = Owner;
            if (owner != null && owner.IsHandleCreated && !owner.IsDisposed)
            {
                owner.BeginInvoke(new Action(() =>
                {
                    Close();
                    TermLensEditorViewPart.HandleEditCurrentTerm(entry, allEntries);
                }));
            }
            else
            {
                // No live owner – fall back to the original FormClosed pattern.
                FormClosed += (s, e) =>
                    TermLensEditorViewPart.HandleEditCurrentTerm(entry, allEntries);
                Close();
            }
        }

        /// <summary>
        /// Briefly replace the keyboard-shortcut hint with a status message
        /// in muted red, then restore the original after a few seconds. Used
        /// for non-fatal feedback (e.g. "MultiTerm is read-only") without
        /// breaking the user's keyboard flow with a modal dialogue.
        /// </summary>
        private void ShowTransientHint(string text, int durationMs = 3500)
        {
            if (_originalHintText == null)
            {
                _originalHintText = _hintLabel.Text;
                _originalHintColor = _hintLabel.ForeColor;
            }

            _hintLabel.Text = text;
            _hintLabel.ForeColor = Color.FromArgb(180, 70, 70); // muted red

            if (_hintRestoreTimer == null)
            {
                _hintRestoreTimer = new System.Windows.Forms.Timer();
                _hintRestoreTimer.Tick += (s, e) =>
                {
                    _hintRestoreTimer.Stop();
                    if (!IsDisposed)
                    {
                        _hintLabel.Text = _originalHintText;
                        _hintLabel.ForeColor = _originalHintColor;
                    }
                };
            }

            _hintRestoreTimer.Stop();
            _hintRestoreTimer.Interval = durationMs;
            _hintRestoreTimer.Start();
        }
    }
}
