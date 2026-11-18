using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// FuzzyFixer's window: opens at once, says what is happening while the memory
    /// is searched and the AI is asked, then shows what the repair changed in the
    /// match's translation - removed words struck through in red, added words
    /// underlined in green. Enter writes the repair, Escape discards it; nothing
    /// reaches the segment before Enter.
    ///
    /// <para>Modal on purpose. The translator cannot move to another segment or
    /// press the shortcut again while a repair is under way, so there is no
    /// "which segment was this for" and no second request to keep track of, and
    /// closing the window is the one way to cancel. The work runs on a pool
    /// thread and reports through <see cref="ShowStatus"/>,
    /// <see cref="ShowResult"/> and <see cref="ShowProblem"/>, each safe from any
    /// thread and a no-op once the window has closed.</para>
    ///
    /// <para>Proofread active segment uses the same window under its own title
    /// and help page, and reports through <see cref="ShowReview"/>: what the AI
    /// found, in words, and under it the correction as the same red and green
    /// changes.</para>
    /// </summary>
    internal sealed class FuzzyFixPreviewDialog : Form
    {
        private static readonly Color Removed = Color.FromArgb(192, 0, 0);
        private static readonly Color Added = Color.FromArgb(0, 128, 0);

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Action<FuzzyFixPreviewDialog> _start;
        private readonly string _title;
        private readonly string _helpTopic;
        private Label _header;
        private RichTextBox _text;
        private Label _note;
        private Button _write;
        private Button _discard;

        // Taken once, here: the source is disposed with the window, and asking a
        // disposed source for its token throws. The work on the pool thread may
        // ask after the window has gone (Escape pressed at once).
        private readonly CancellationToken _token;

        /// <summary>Cancelled when the window closes, however it closes. Safe to read after that.</summary>
        public CancellationToken Token => _token;

        /// <param name="start">Called once, on the UI thread, when the window is
        /// on screen: starts the background work.</param>
        public FuzzyFixPreviewDialog(string segmentLabel, string note, Action<FuzzyFixPreviewDialog> start)
            : this(FuzzyFixer.Title, HelpSystem.Topics.FuzzyFixer, segmentLabel, note, start)
        {
        }

        /// <summary>The same window for another feature: its name in the title
        /// bar and in "could not start", and the help page ? and F1 open.</summary>
        public FuzzyFixPreviewDialog(string title, string helpTopic, string segmentLabel, string note,
            Action<FuzzyFixPreviewDialog> start)
        {
            _title = title;
            _helpTopic = helpTopic;
            _start = start;
            _token = _cts.Token;
            Icon =IconHelper.AppIcon;
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = _title + (string.IsNullOrWhiteSpace(segmentLabel) ? "" : " – " + segmentLabel);
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            // The title-bar ? (shown because there is no minimise or maximise box)
            // and F1 open the feature's help page, as the other dialogs do.
            HelpButton = true;
            HelpButtonClicked += (s, e) =>
            {
                e.Cancel = true;   // no ? cursor
                HelpSystem.OpenHelp(_helpTopic);
            };

            // Absolute positions, no Dock panels: those misbehave in Trados's host.
            const int margin = 16, width = 620;
            int y = 14;

            // Three lines, measured in the font: on a scaled display the text grows and a
            // height in fixed pixels cut the second line off (the keys to press).
            int headerHeight = Font.Height * 3 + 4;
            _header = new Label { Location = new Point(margin, y), Size = new Size(width, headerHeight), Text = "Starting…" };
            Controls.Add(_header);
            y += headerHeight + 6;

            _text = new RichTextBox
            {
                Location = new Point(margin, y),
                Size = new Size(width, 190),
                ReadOnly = true,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = SystemColors.Window,
                Font = new Font("Segoe UI", 10f),
                ScrollBars = RichTextBoxScrollBars.Vertical,
                TabStop = false,
                DetectUrls = false
            };
            Controls.Add(_text);
            y += 198;

            _note = new Label
            {
                Location = new Point(margin, y),
                Size = new Size(width, Font.Height + 4),
                Text = note ?? "",
                ForeColor = Removed,
                Visible = !string.IsNullOrWhiteSpace(note)
            };
            Controls.Add(_note);
            // Not _note.Visible: before the window is shown that reads false whatever
            // was set, so the buttons were placed on top of the note and hidden by it.
            if (!string.IsNullOrWhiteSpace(note)) y += Font.Height + 10;

            _discard = new Button { Text = "Cancel", Size = new Size(110, 30), Location = new Point(margin + width - 110, y), DialogResult = DialogResult.Cancel };
            // Alt+W writes, Alt+D discards (Enter and Escape do the same).
            _write = new Button { Text = "&Write to target", Size = new Size(150, 30), Location = new Point(margin + width - 110 - 8 - 150, y), Enabled = false };
            _write.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            Controls.Add(_write);
            Controls.Add(_discard);
            y += 30 + 14;

            ClientSize = new Size(margin + width + margin, y);
            CancelButton = _discard;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _discard.Focus();
            try { _start?.Invoke(this); }
            catch (Exception ex) { ShowProblem(_title + " could not start: " + ex.Message); }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // Every way out cancels: Escape, the X, Discard, and Enter too (by
            // then the work is done and the token has nothing left to stop).
            try { _cts.Cancel(); } catch { }
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Cancelled first, also for a window that was never shown or is
                // disposed without closing: a token cancelled before its source
                // is disposed stays usable, and whoever holds it sees "cancelled".
                try { _cts.Cancel(); } catch { }
                _cts.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // The text box would otherwise take Enter for itself.
            if (keyData == Keys.Enter && _write.Enabled)
            {
                DialogResult = DialogResult.OK;
                Close();
                return true;
            }
            if (keyData == Keys.F1)
            {
                HelpSystem.OpenHelp(_helpTopic);
                return true;
            }
            if (keyData == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>What is being done now; optionally the match's translation, to read meanwhile.</summary>
        public void ShowStatus(string header, string matchTarget = null)
        {
            Post(() =>
            {
                _header.Text = header;
                if (matchTarget != null) _text.Text = matchTarget;
            });
        }

        /// <summary>The repair, as changes to the match's translation. Enter now writes it.</summary>
        public void ShowResult(string header, List<TokenDiff.Piece> pieces)
        {
            Post(() =>
            {
                _header.Text = header;
                Render(pieces);
                _discard.Text = "&Discard";
                _write.Enabled = true;
                AcceptButton = _write;
                _write.Focus();
            });
        }

        /// <summary>
        /// What a check of the segment found (Proofread active segment).
        /// <paramref name="findings"/> is plain text of any length: it goes in
        /// the text box, which scrolls, and not in the header, which holds three
        /// lines. With a <paramref name="correction"/> its changes follow under
        /// the findings and Enter writes it; with none, only closing is left.
        /// </summary>
        public void ShowReview(string header, string findings, List<TokenDiff.Piece> correction)
        {
            Post(() =>
            {
                _header.Text = header;
                _text.Clear();
                if (!string.IsNullOrEmpty(findings))
                {
                    _text.SelectionColor = _text.ForeColor;
                    _text.SelectionFont = _text.Font;
                    _text.AppendText(findings.TrimEnd() + (correction != null ? "\n\n" : ""));
                }
                if (correction != null) Append(correction);
                _text.SelectionStart = 0;
                _text.SelectionLength = 0;
                _text.ScrollToCaret();

                _write.Enabled = correction != null;
                if (correction != null)
                {
                    _discard.Text = "&Discard";
                    AcceptButton = _write;
                    _write.Focus();
                }
                else
                {
                    _discard.Text = "Close";
                    _discard.Focus();
                }
            });
        }

        /// <summary>Nothing to write, and why. Only closing is left.</summary>
        public void ShowProblem(string message)
        {
            Post(() =>
            {
                _header.Text = "Nothing was written.";
                _text.Clear();
                _text.Text = message;
                _write.Enabled = false;
                _discard.Text = "Close";
                _discard.Focus();
            });
        }

        private void Render(List<TokenDiff.Piece> pieces)
        {
            _text.Clear();
            if (pieces == null) return;
            Append(pieces);
            _text.SelectionStart = 0;
            _text.SelectionLength = 0;
        }

        /// <summary>Adds the changes at the end of the text box: removed words struck through in red, added words underlined in green.</summary>
        private void Append(List<TokenDiff.Piece> pieces)
        {
            using (var struck = new Font(_text.Font, FontStyle.Strikeout))
            using (var underlined = new Font(_text.Font, FontStyle.Underline))
            {
                foreach (var p in pieces)
                {
                    _text.SelectionStart = _text.TextLength;
                    _text.SelectionLength = 0;
                    switch (p.Op)
                    {
                        case TokenDiff.Op.Delete:
                            _text.SelectionColor = Removed;
                            _text.SelectionFont = struck;
                            break;
                        case TokenDiff.Op.Insert:
                            _text.SelectionColor = Added;
                            _text.SelectionFont = underlined;
                            break;
                        default:
                            _text.SelectionColor = _text.ForeColor;
                            _text.SelectionFont = _text.Font;
                            break;
                    }
                    _text.AppendText(p.Text);
                }
            }
        }

        /// <summary>Runs <paramref name="action"/> on the UI thread if the window is still open; otherwise drops it.</summary>
        private void Post(Action action)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                if (InvokeRequired) BeginInvoke((Action)(() => { if (!IsDisposed) action(); }));
                else action();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }   // closed between the check and the call
        }
    }
}
