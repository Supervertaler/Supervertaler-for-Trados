using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// A one-line warning across the top of the TermLens panel, shown only
    /// while the open document has source text outside every segment (see
    /// <see cref="UnsegmentedTextDetector"/>). Clicking it lists each stretch;
    /// clicking one puts the editor on the nearest segment. The × hides it for
    /// these results: it comes back only when the results change.
    ///
    /// <para>Updates itself from <see cref="UnsegmentedTextMonitor.Changed"/>, so
    /// the panel that hosts it only has to add it.</para>
    /// </summary>
    internal sealed class UnsegmentedTextStrip : Panel
    {
        /// <summary>Stretches listed in the menu; the rest are counted.</summary>
        private const int MaxListed = 25;

        // A Button rather than a Label: only a focusable control can recover the
        // click Studio 2026 eats when the pane was inactive (ClickThrough).
        private readonly Button _message;
        private readonly Button _close;
        private readonly ToolTip _tip = new ToolTip();
        private List<UnsegmentedHit> _hits = new List<UnsegmentedHit>();
        private int _total;
        private bool _severalFiles;

        // What was showing when the user clicked ×. The same results arriving
        // again (switching back to the document) stay hidden; different ones show.
        private string _shownKey, _dismissedKey;

        public UnsegmentedTextStrip()
        {
            Dock = DockStyle.Top;
            Visible = false;
            Height = UiScale.Pixels(24);
            BackColor = Color.FromArgb(255, 243, 205);   // amber, as Studio's own warnings
            Padding = new Padding(UiScale.Pixels(4), 0, 0, 0);

            _close = new Button
            {
                Text = "×",
                Dock = DockStyle.Right,
                Width = UiScale.Pixels(24),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", UiScale.FontSize(9f)),
                ForeColor = Color.FromArgb(102, 77, 3),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                TabStop = false,
                UseCompatibleTextRendering = true
            };
            _close.FlatAppearance.BorderSize = 0;
            _close.FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 230, 156);
            _close.Click += (s, e) => Dismiss();
            ClickThrough.Attach(_close, Dismiss);
            _tip.SetToolTip(_close, "Hide this warning until the document's results change");

            _message = new Button
            {
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", UiScale.FontSize(8.5f)),
                ForeColor = Color.FromArgb(102, 77, 3),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                TabStop = false,
                UseMnemonic = false,
                // GDI text, which falls back to a font that has the warning sign.
                UseCompatibleTextRendering = false
            };
            _message.FlatAppearance.BorderSize = 0;
            _message.FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 236, 179);
            _message.FlatAppearance.MouseDownBackColor = Color.FromArgb(255, 230, 156);
            _message.Click += (s, e) => ShowList();
            ClickThrough.Attach(_message, ShowList);

            Controls.Add(_message);
            Controls.Add(_close);

            UnsegmentedTextMonitor.Changed += ShowCurrent;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                UnsegmentedTextMonitor.Changed -= ShowCurrent;
                _tip.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>Re-reads the active document's results. UI thread.</summary>
        public void ShowCurrent()
        {
            if (IsDisposed) return;
            int pending;
            var results = UnsegmentedTextMonitor.Current(out pending);
            _hits = results.SelectMany(r => r.Hits).ToList();
            _total = results.Sum(r => r.HitCount);
            _severalFiles = results.Count(r => r.HitCount > 0) > 1;
            _shownKey = string.Join("|", results.Select(r => r.FilePath + "#" + r.LastWriteUtc.Ticks + "#" + r.HitCount));

            if (_total == 0 || _shownKey == _dismissedKey)
            {
                Visible = false;
                return;
            }

            var text = _total == 1
                ? "⚠ 1 stretch of source text is outside any segment and will export untranslated."
                : $"⚠ {_total} stretches of source text are outside any segment and will export untranslated.";
            _message.Text = text + " Show…";
            _tip.SetToolTip(_message, text + "\r\nClick to list them and go to the nearest segment.");
            Visible = true;
        }

        private void Dismiss()
        {
            _dismissedKey = _shownKey;
            Visible = false;
        }

        private void ShowList()
        {
            if (_hits.Count == 0) return;
            var menu = new ContextMenuStrip { ShowImageMargin = false, ShowItemToolTips = true };
            menu.Closed += (s, e) => BeginInvoke(new Action(menu.Dispose));

            foreach (var hit in _hits.Take(MaxListed))
            {
                var h = hit;
                var where = (_severalFiles ? UnsegmentedTextMonitor.DisplayName(h.FilePath) + ", " : "") + h.Location;
                var label = where + ": " + UnsegmentedTextMonitor.Shorten(h.Text, 80);
                var item = new ToolStripMenuItem(label.Replace("&", "&&"))
                {
                    ToolTipText = UnsegmentedTextMonitor.Shorten(h.Text, 300),
                    Enabled = h.JumpSegmentId != null
                };
                item.Click += (s, e) =>
                {
                    var problem = UnsegmentedTextMonitor.GoTo(h);
                    if (problem != null)
                        MessageBox.Show(FindForm(), problem, "Supervertaler for Trados",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                };
                menu.Items.Add(item);
            }
            if (_total > MaxListed)
                menu.Items.Add(new ToolStripMenuItem($"…and {_total - MaxListed} more") { Enabled = false });

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Why this matters and what to do", null,
                (s, e) => HelpSystem.OpenHelp(HelpSystem.Topics.UnsegmentedText));

            menu.Show(_message, new Point(0, _message.Height));
        }
    }
}
