using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// Type-to-filter for a drop-down list: the prompt pickers and the memory
    /// bank pickers, which grow with the work (dozens of client banks, dozens of
    /// prompts) until scrolling a plain list for the right one is a chore.
    ///
    /// <para>Clicking the combo, F4 / Alt+Down, or simply typing while it has
    /// focus opens a list under it with a filter box on top, instead of the
    /// native list. Typing narrows it (any part of the name, any order, case
    /// and accents ignored – see <see cref="FilterText"/>); Up/Down/PageUp/
    /// PageDown move; Enter or a click picks; Escape clears the filter, and a
    /// second Escape (or clicking elsewhere) closes it with nothing changed.</para>
    ///
    /// <para>The combo itself is never touched until a row is picked: its items
    /// are not filtered, and its selection changes only on that pick, by
    /// <c>SelectedIndex</c>, exactly as the native list would set it. So every
    /// SelectedIndexChanged handler, index mapping and "revert the sentinel"
    /// trick in the callers keeps working unchanged, and nothing is ever chosen
    /// that the user didn't choose.</para>
    ///
    /// <para>The popup is a small modeless window driven by the text box's own
    /// KeyDown, not a ToolStripDropDown: Studio 2026 hosts the panes in WPF,
    /// whose message loop does not run the WinForms message filter that a
    /// ToolStripDropDown needs for its keyboard.</para>
    /// </summary>
    public static class ComboFilter
    {
        /// <summary>
        /// Give <paramref name="combo"/> (a DropDownList) a filterable list.
        /// <paramref name="pinned"/> marks items that stay visible whatever is
        /// typed – "New memory bank…" and the like.
        /// </summary>
        public static void Attach(ComboBox combo, string cue, Func<object, bool> pinned = null)
        {
            if (combo == null) return;
            new ComboHook(combo, cue, pinned);
        }

        /// <summary>
        /// Listens on the combo's own window, ahead of the native control, for
        /// the click, keys and characters that would open the native list.
        /// </summary>
        private sealed class ComboHook : NativeWindow
        {
            private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, WM_CHAR = 0x0102;
            private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONDBLCLK = 0x0203;

            private readonly ComboBox _combo;
            private readonly string _cue;
            private readonly Func<object, bool> _pinned;
            private FilterPopup _open;
            private int _closedAt = int.MinValue;

            public ComboHook(ComboBox combo, string cue, Func<object, bool> pinned)
            {
                _combo = combo; _cue = cue; _pinned = pinned;
                if (combo.IsHandleCreated) AssignHandle(combo.Handle);
                combo.HandleCreated += (s, e) => AssignHandle(_combo.Handle);
                combo.HandleDestroyed += (s, e) => { ClosePopup(); ReleaseHandle(); };
            }

            protected override void WndProc(ref Message m)
            {
                if (_combo.Enabled && _combo.Items.Count > 0)
                {
                    switch (m.Msg)
                    {
                        case WM_LBUTTONDOWN:
                        case WM_LBUTTONDBLCLK:
                            _combo.Focus();
                            // The click that just closed the list (by taking the
                            // focus from it) must not open it again straight away.
                            // Unsigned difference: right across TickCount's wrap, and "long ago" for
                            // the starting value (a signed one made every click do nothing).
                            if (unchecked((uint)(Environment.TickCount - _closedAt)) < 300) return;
                            Toggle(null);
                            return;
                        case WM_KEYDOWN:
                            if ((Keys)(int)m.WParam == Keys.F4) { Toggle(null); return; }
                            break;
                        case WM_SYSKEYDOWN:
                            var k = (Keys)(int)m.WParam;
                            if (k == Keys.Down || k == Keys.Up) { Toggle(null); return; }
                            break;
                        case WM_CHAR:
                            // A printable character would make the native combo jump
                            // to the next item starting with it - a silent change of
                            // selection. It starts the filter instead.
                            var c = (char)(int)m.WParam;
                            if (!char.IsControl(c)) { Toggle(c.ToString()); return; }
                            break;
                    }
                }
                base.WndProc(ref m);
            }

            private void Toggle(string initial)
            {
                if (_open != null && !_open.IsDisposed)
                {
                    // A second click on the combo closes the list, as the native one does.
                    if (initial == null) { ClosePopup(); return; }
                    return;
                }
                var popup = new FilterPopup(_combo, _cue, _pinned, initial);
                _open = popup;
                popup.FormClosed += (s, e) =>
                {
                    _open = null;
                    if (popup.ClosedByClickAway) _closedAt = Environment.TickCount;
                };
                popup.ShowUnder(_combo);
            }

            private void ClosePopup()
            {
                var p = _open;
                _open = null;
                if (p != null && !p.IsDisposed) p.Close();
            }
        }

        /// <summary>One row of the popup's list: the combo index it stands for.</summary>
        internal sealed class Row
        {
            public int Index;
            public string Text;
            public override string ToString() => Text;
        }

        internal sealed class FilterPopup : Form
        {
            private readonly ComboBox _combo;
            private readonly FilterTextBox _box = new FilterTextBox();
            private readonly ListBox _list = new ListBox();
            private readonly Label _empty = new Label();
            private readonly List<string> _texts = new List<string>();
            private readonly bool[] _pinned;
            private bool _done;

            /// <summary>Closed because the user clicked elsewhere - perhaps on the combo itself.</summary>
            public bool ClosedByClickAway { get; private set; }

            public FilterPopup(ComboBox combo, string cue, Func<object, bool> pinned, string initial)
            {
                _combo = combo;

                // Snapshot of the items as text, taken once: typing scans these
                // strings and never goes back to the combo.
                _pinned = new bool[combo.Items.Count];
                for (int i = 0; i < combo.Items.Count; i++)
                {
                    var item = combo.Items[i];
                    _texts.Add(combo.GetItemText(item) ?? "");
                    _pinned[i] = pinned != null && pinned(item);
                }

                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                KeyPreview = false;
                BackColor = SystemColors.ControlDark; // the 1px frame
                Padding = new Padding(1);
                Font = combo.Font;

                var inner = new Panel { Dock = DockStyle.Fill, BackColor = SystemColors.Window, Padding = new Padding(UiScale.Pixels(3)) };

                _box.Dock = DockStyle.Top;
                _box.Cue = cue ?? "Type to filter";
                _box.Font = combo.Font;
                _box.BorderStyle = BorderStyle.FixedSingle;
                _box.PreviewKeyDown += (s, e) =>
                {
                    // Keep arrows and Enter in the box: in a WinForms dialog loop
                    // they would otherwise move focus between the box and the list.
                    if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down || e.KeyCode == Keys.PageUp ||
                        e.KeyCode == Keys.PageDown || e.KeyCode == Keys.Enter || e.KeyCode == Keys.Escape)
                        e.IsInputKey = true;
                };
                _box.KeyDown += OnBoxKeyDown;
                _box.TextChanged += (s, e) => Populate();

                var gap = new Panel { Dock = DockStyle.Top, Height = UiScale.Pixels(3), BackColor = SystemColors.Window };

                _list.Dock = DockStyle.Fill;
                _list.BorderStyle = BorderStyle.None;
                _list.IntegralHeight = false;
                _list.Font = combo.Font;
                _list.TabStop = false;
                _list.MouseClick += (s, e) =>
                {
                    var i = _list.IndexFromPoint(e.Location);
                    if (i >= 0) { _list.SelectedIndex = i; Commit(); }
                };
                // Focus belongs in the box, so typing keeps filtering after a scroll.
                _list.GotFocus += (s, e) => _box.Focus();

                _empty.Text = "No match. Escape clears the filter.";
                _empty.ForeColor = SystemColors.GrayText;
                _empty.Dock = DockStyle.Fill;
                _empty.Visible = false;

                inner.Controls.Add(_list);
                inner.Controls.Add(_empty);
                inner.Controls.Add(gap);
                inner.Controls.Add(_box);
                Controls.Add(inner);

                // Clicking anywhere else closes it, leaving the focus where the click put it.
                Deactivate += (s, e) =>
                {
                    if (_done) return;
                    ClosedByClickAway = true;
                    Cancel(refocus: false);
                };

                if (!string.IsNullOrEmpty(initial)) _box.Text = initial; // fires Populate
                else Populate();
                _box.SelectionStart = _box.TextLength;
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW: no taskbar or Alt+Tab entry
                    cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW, like a native drop-down
                    return cp;
                }
            }

            public void ShowUnder(ComboBox combo)
            {
                var itemHeight = Math.Max(_list.ItemHeight, combo.ItemHeight);
                var rows = Math.Min(16, Math.Max(4, _texts.Count));
                var width = Math.Max(combo.Width, combo.DropDownWidth);
                var height = _box.PreferredHeight + UiScale.Pixels(3) * 3 + 2 + rows * itemHeight + UiScale.Pixels(4);

                var screen = Screen.FromControl(combo).WorkingArea;
                var below = combo.PointToScreen(new Point(0, combo.Height));
                var x = Math.Max(screen.Left, Math.Min(below.X, screen.Right - width));
                var y = below.Y;
                if (y + height > screen.Bottom)
                {
                    var above = combo.PointToScreen(Point.Empty).Y - height;
                    y = above >= screen.Top ? above : Math.Max(screen.Top, screen.Bottom - height);
                }
                Bounds = new Rectangle(x, y, width, height);

                // Owned by the window the combo lives in, so it stays on top of
                // it and Windows hands activation back to it when this closes.
                var root = combo.TopLevelControl;
                if (root != null) Show(root);
                else Show(new RootWindow(combo.Handle));
                Activate();
                _box.Focus();
            }

            /// <summary>The combo's top-level window when it has no WinForms Form above it (a WPF-hosted Studio pane).</summary>
            private sealed class RootWindow : IWin32Window
            {
                [System.Runtime.InteropServices.DllImport("user32.dll")]
                private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
                public RootWindow(IntPtr child) { Handle = GetAncestor(child, 2 /* GA_ROOT */); }
                public IntPtr Handle { get; }
            }

            private void Populate()
            {
                var keep = (_list.SelectedItem as Row)?.Index ?? _combo.SelectedIndex;
                var hits = FilterText.MatchingIndices(_texts, _box.Text, i => _pinned[i]);

                _list.BeginUpdate();
                try
                {
                    _list.Items.Clear();
                    int select = -1;
                    foreach (var i in hits)
                    {
                        var at = _list.Items.Add(new Row { Index = i, Text = _texts[i] });
                        if (i == keep) select = at;
                    }
                    // Keep the highlighted row if it still matches; otherwise the
                    // first real match, so Enter after typing takes the obvious one.
                    if (select < 0)
                    {
                        for (int r = 0; r < _list.Items.Count; r++)
                            if (!_pinned[((Row)_list.Items[r]).Index]) { select = r; break; }
                    }
                    if (select < 0 && _list.Items.Count > 0) select = 0;
                    _list.SelectedIndex = select;
                }
                finally { _list.EndUpdate(); }

                // Only one of the two is laid out, so each gets the whole space.
                _empty.Visible = _list.Items.Count == 0;
                _list.Visible = !_empty.Visible;
            }

            private void OnBoxKeyDown(object sender, KeyEventArgs e)
            {
                int n = _list.Items.Count, i = _list.SelectedIndex;
                int page = Math.Max(1, _list.ClientSize.Height / Math.Max(1, _list.ItemHeight) - 1);
                switch (e.KeyCode)
                {
                    case Keys.Down: if (n > 0) _list.SelectedIndex = Math.Min(n - 1, i + 1); break;
                    case Keys.Up: if (n > 0) _list.SelectedIndex = Math.Max(0, i - 1); break;
                    case Keys.PageDown: if (n > 0) _list.SelectedIndex = Math.Min(n - 1, Math.Max(0, i) + page); break;
                    case Keys.PageUp: if (n > 0) _list.SelectedIndex = Math.Max(0, i - page); break;
                    case Keys.Enter: Commit(); break;
                    case Keys.Escape:
                        if (_box.TextLength > 0) _box.Clear(); else Cancel(refocus: true);
                        break;
                    case Keys.F4: Cancel(refocus: true); break;
                    default: return;
                }
                e.Handled = true;
                e.SuppressKeyPress = true;
            }

            /// <summary>Pick the highlighted row: the one place the combo is changed.</summary>
            private void Commit()
            {
                if (_done) return;
                var row = _list.SelectedItem as Row;
                _done = true;
                Close();
                if (row == null || _combo.IsDisposed) return;
                if (_combo.CanFocus) _combo.Focus();
                if (_combo.SelectedIndex != row.Index && row.Index < _combo.Items.Count)
                    _combo.SelectedIndex = row.Index;
            }

            private void Cancel(bool refocus)
            {
                if (_done) return;
                _done = true;
                Close();
                if (refocus && !_combo.IsDisposed && _combo.CanFocus) _combo.Focus();
            }

            // Test hooks: drive the popup without showing it.
            internal string FilterTextForTest { get => _box.Text; set => _box.Text = value; }
            internal int VisibleCountForTest => _list.Items.Count;
            internal void KeyForTest(Keys k) => OnBoxKeyDown(_box, new KeyEventArgs(k));
            internal string HighlightedForTest => (_list.SelectedItem as Row)?.Text;
        }

        /// <summary>For tests: a popup over <paramref name="combo"/>, built but not shown.</summary>
        internal static Form CreatePopupForTest(ComboBox combo, Func<object, bool> pinned, string initial)
            => new FilterPopup(combo, null, pinned, initial);
    }
}
