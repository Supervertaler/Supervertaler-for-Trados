using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi.Interfaces;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// The TermPicker list itself: every matched term for the current segment as
    /// a flat, sortable, keyboard-navigable list, with target synonyms as
    /// expandable child rows.
    ///
    /// Hosted by two surfaces so they can never drift apart:
    ///   • <see cref="TermPickerDialog"/> – the Alt+P popup (modal, at cursor)
    ///   • <see cref="TermPickerViewPart"/> – the dockable pane, refreshed on
    ///     every segment change
    /// The keyboard grammar (arrows to navigate with wrap-around, Right/Left to
    /// expand/collapse, digits to jump, Enter to insert) lives here, so both
    /// surfaces behave identically.
    /// </summary>
    public class TermPickerControl : UserControl, IUIControl
    {
        private readonly BufferedListView _listView;
        private readonly Label _hintLabel;
        private List<TermPickerMatch> _matches = new List<TermPickerMatch>();

        // Parent indices (1-based) currently expanded
        private readonly HashSet<int> _expandedParents = new HashSet<int>();

        private static readonly Color HighPriorityBg = ColorTranslator.FromHtml("#FFE5F0");
        private static readonly Color RegularBg = ColorTranslator.FromHtml("#D6EBFF");
        private static readonly Color NonTranslatableBg = ColorTranslator.FromHtml("#FFF3D0");
        private static readonly Color SubItemBg = Color.FromArgb(245, 245, 250);

        /// <summary>Raised when the user chooses a term (Enter, double-click, digit).</summary>
        public event EventHandler<TermPickerInsertEventArgs> InsertRequested;

        /// <summary>
        /// Raised when the user presses 'e' to edit the selected term. Only the
        /// modal popup subscribes (so it can close before the editor opens); when
        /// nobody subscribes, the control opens the editor itself.
        /// </summary>
        public event EventHandler<TermPickerEditEventArgs> EditRequested;

        /// <summary>The target text of the currently selected row, or null.</summary>
        public string SelectedTargetTerm
        {
            get
            {
                if (_listView.SelectedItems.Count == 0) return null;
                var tag = _listView.SelectedItems[0].Tag as RowTag;
                return tag?.TargetTerm;
            }
        }

        public TermPickerControl()
        {
            _listView = new BufferedListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                GridLines = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                // Keep the selected row visibly selected while focus is
                // elsewhere. With the default (true) the highlight vanishes
                // when the editor has focus and snaps back on Alt+P, which
                // reads as the top row flashing away and returning.
                HideSelection = false,
                Font = new Font("Segoe UI", 9.5f)
            };
            _listView.Columns.Add("#", 48, HorizontalAlignment.Right);
            // Metadata indicator: the amber dot marks rows that have something
            // for 'I' to show, mirroring the dot on TermLens chips. Without it
            // there is no way to tell whether pressing I will do anything.
            _listView.Columns.Add("", 22, HorizontalAlignment.Center);
            _listView.Columns.Add("Source", 160, HorizontalAlignment.Left);
            _listView.Columns.Add("Target", 210, HorizontalAlignment.Left);
            _listView.Columns.Add("Termbase", 130, HorizontalAlignment.Left);

            _listView.DoubleClick += (s, e) => RaiseInsert();
            // A details popup left open while the user arrows to another row
            // would be describing the wrong term.
            _listView.SelectedIndexChanged += (s, e) => TryHideInfoPopup();
            _listView.KeyDown += OnListViewKeyDown;
            BuildContextMenu();

            _hintLabel = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 20,
                Text = "Enter inserts • ←/→ synonyms • I info (●) • E edit • right-click for more",
                ForeColor = Color.FromArgb(120, 120, 120),
                Font = new Font("Segoe UI", 8.5f),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 0, 0)
            };

            Controls.Add(_listView);
            Controls.Add(_hintLabel);
        }

        /// <summary>
        /// Right-click menu matching the TermLens chips: edit, toggle
        /// non-translatable, delete. Built once; the items enable/disable
        /// themselves for the row under the cursor (MultiTerm entries are
        /// read-only, so they get nothing but a disabled menu).
        /// </summary>
        private void BuildContextMenu()
        {
            var menu = new ContextMenuStrip();
            var editItem = new ToolStripMenuItem("Edit Term…");
            var ntItem = new ToolStripMenuItem("Mark as Non-Translatable");
            var deleteItem = new ToolStripMenuItem("Delete Term");

            editItem.Click += (s, e) => EditSelected();
            ntItem.Click += (s, e) => ToggleNonTranslatableSelected();
            deleteItem.Click += (s, e) => DeleteSelected();

            menu.Items.Add(editItem);
            menu.Items.Add(ntItem);
            menu.Items.Add(deleteItem);

            menu.Opening += (s, e) =>
            {
                var entry = SelectedEntry;
                bool editable = entry != null && !entry.IsMultiTerm;
                editItem.Enabled = ntItem.Enabled = deleteItem.Enabled = editable;
                ntItem.Text = entry != null && entry.IsNonTranslatable
                    ? "Mark as Translatable"
                    : "Mark as Non-Translatable";
            };

            // Right-clicking a row should select it first, so the menu always
            // acts on what the user actually pointed at.
            _listView.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                var hit = _listView.HitTest(e.Location);
                if (hit.Item != null)
                {
                    hit.Item.Selected = true;
                    hit.Item.Focused = true;
                }
            };

            _listView.ContextMenuStrip = menu;
        }

        /// <summary>The TermEntry behind the selected row, or null.</summary>
        private TermEntry SelectedEntry
        {
            get
            {
                if (_listView.SelectedItems.Count == 0) return null;
                var tag = _listView.SelectedItems[0].Tag as RowTag;
                return tag?.Match?.PrimaryEntry;
            }
        }

        /// <summary>Toggles the non-translatable flag on the selected term.</summary>
        public void ToggleNonTranslatableSelected()
        {
            var entry = SelectedEntry;
            if (entry == null || entry.IsMultiTerm) return;
            TermLensEditorViewPart.HandleToggleNonTranslatable(entry);
        }

        /// <summary>Deletes the selected term (with the usual confirmation).</summary>
        public void DeleteSelected()
        {
            var entry = SelectedEntry;
            if (entry == null || entry.IsMultiTerm) return;
            TermLensEditorViewPart.HandleDeleteTerm(entry);
        }

        /// <summary>Hides the hint strip (the dialog shows its own with buttons).</summary>
        public bool ShowHint
        {
            get => _hintLabel.Visible;
            set => _hintLabel.Visible = value;
        }

        /// <summary>Gives the list keyboard focus.</summary>
        public void FocusList()
        {
            try { _listView.Focus(); } catch { }
        }

        /// <summary>Current column widths, for the host to persist.</summary>
        public List<int> GetColumnWidths()
        {
            var widths = new List<int>();
            for (int i = 0; i < _listView.Columns.Count; i++)
                widths.Add(_listView.Columns[i].Width);
            return widths;
        }

        /// <summary>Applies persisted column widths (ignored when the count doesn't match).</summary>
        public void ApplyColumnWidths(List<int> widths)
        {
            if (widths == null || widths.Count != _listView.Columns.Count) return;
            for (int i = 0; i < _listView.Columns.Count; i++)
                if (widths[i] > 0) _listView.Columns[i].Width = widths[i];
        }

        /// <summary>
        /// (Re)loads the list for a segment's matches. Synonym groups open
        /// expanded so the full picture is visible at a glance. Safe to call
        /// repeatedly – the docked pane calls it on every segment change.
        /// </summary>
        public void LoadMatches(List<TermPickerMatch> matches)
        {
            _matches = matches ?? new List<TermPickerMatch>();
            _expandedParents.Clear();
            foreach (var match in _matches)
                if (match.GetAllTargets().Count > 1)
                    _expandedParents.Add(match.Index);

            // Column headers follow the termbase's language names when known
            if (_matches.Count > 0 && _matches[0].PrimaryEntry != null)
            {
                var src = _matches[0].PrimaryEntry.SourceLang;
                var tgt = _matches[0].PrimaryEntry.TargetLang;
                if (!string.IsNullOrEmpty(src)) _listView.Columns[2].Text = src;
                if (!string.IsNullOrEmpty(tgt)) _listView.Columns[3].Text = tgt;
            }

            // A metadata popup left over from the previous segment would be
            // describing a row that no longer exists.
            try { TermPopup.GetInstance().HidePopup(); } catch { }

            _listView.BeginUpdate();
            try
            {
                PopulateMainRows();
                if (_listView.Items.Count > 0)
                {
                    _listView.Items[0].Selected = true;
                    _listView.Items[0].Focused = true;
                }
            }
            finally { _listView.EndUpdate(); }
        }

        private void PopulateMainRows()
        {
            _listView.Items.Clear();

            foreach (var match in _matches)
            {
                var allTargets = match.GetAllTargets();
                bool hasExpansion = allTargets.Count > 1;

                string indexDisplay = match.Index.ToString();
                if (hasExpansion)
                    indexDisplay += " ▸"; // ▸

                // Adapt casing to the segment occurrence so the picker shows and
                // inserts the same text as the TermLens chips
                var adaptedTarget = TermCaseAdapter.Adapt(match.MatchedSourceText,
                    match.SourceText, match.PrimaryEntry.TargetTerm ?? "");

                var item = new ListViewItem(indexDisplay);
                item.UseItemStyleForSubItems = false;   // lets the dot be amber
                var metaCell = item.SubItems.Add(HasMetadata(match) ? "●" : "");
                metaCell.ForeColor = Color.FromArgb(230, 160, 40);
                item.SubItems.Add(match.SourceText);
                item.SubItems.Add(adaptedTarget);
                item.SubItems.Add(match.PrimaryEntry.TermbaseName ?? "");
                item.Tag = new RowTag
                {
                    IsSubItem = false,
                    ParentIndex = match.Index,
                    TargetTerm = adaptedTarget,
                    Match = match
                };

                if (match.PrimaryEntry.IsNonTranslatable) item.BackColor = NonTranslatableBg;
                else if (match.IsProjectTermbase) item.BackColor = HighPriorityBg;
                else item.BackColor = RegularBg;

                _listView.Items.Add(item);

                if (_expandedParents.Contains(match.Index))
                    AddSubItems(match);
            }
        }

        private void AddSubItems(TermPickerMatch match)
        {
            int parentPos = -1;
            for (int i = 0; i < _listView.Items.Count; i++)
            {
                var tag = _listView.Items[i].Tag as RowTag;
                if (tag != null && !tag.IsSubItem && tag.ParentIndex == match.Index)
                {
                    parentPos = i;
                    break;
                }
            }
            if (parentPos < 0) return;

            _listView.Items[parentPos].Text = match.Index + " ▾"; // ▾

            var allTargets = match.GetAllTargets();
            int insertPos = parentPos + 1;

            for (int t = 1; t < allTargets.Count; t++)
            {
                var option = allTargets[t];
                var adaptedOption = TermCaseAdapter.Adapt(match.MatchedSourceText,
                    match.SourceText, option.TargetTerm);

                var subItem = new ListViewItem("");
                subItem.SubItems.Add("");                    // indicator column
                subItem.SubItems.Add("    └ " + match.SourceText);
                subItem.SubItems.Add(adaptedOption);
                subItem.SubItems.Add(option.TermbaseName ?? "");
                subItem.Tag = new RowTag
                {
                    IsSubItem = true,
                    ParentIndex = match.Index,
                    TargetTerm = adaptedOption,
                    Match = match
                };
                subItem.BackColor = SubItemBg;
                subItem.ForeColor = Color.FromArgb(60, 60, 60);

                _listView.Items.Insert(insertPos, subItem);
                insertPos++;
            }
        }

        private void RemoveSubItems(int parentIndex)
        {
            for (int i = _listView.Items.Count - 1; i >= 0; i--)
            {
                var tag = _listView.Items[i].Tag as RowTag;
                if (tag != null && tag.IsSubItem && tag.ParentIndex == parentIndex)
                    _listView.Items.RemoveAt(i);
            }

            for (int i = 0; i < _listView.Items.Count; i++)
            {
                var tag = _listView.Items[i].Tag as RowTag;
                if (tag != null && !tag.IsSubItem && tag.ParentIndex == parentIndex)
                {
                    _listView.Items[i].Text = parentIndex + " ▸"; // ▸
                    break;
                }
            }
        }

        /// <summary>
        /// True when any entry behind this row has something for 'I' to show -
        /// a definition, domain, notes or URL. Synonyms deliberately don't
        /// count: they are already visible as sub-rows.
        /// </summary>
        private static bool HasMetadata(TermPickerMatch match)
        {
            var entries = match?.AllEntries;
            if ((entries == null || entries.Count == 0) && match?.PrimaryEntry != null)
                entries = new List<TermEntry> { match.PrimaryEntry };
            if (entries == null) return false;

            foreach (var entry in entries)
            {
                if (entry == null) continue;
                if (!string.IsNullOrWhiteSpace(entry.Definition)) return true;
                if (!string.IsNullOrWhiteSpace(entry.Domain)) return true;
                if (!string.IsNullOrWhiteSpace(entry.Notes)) return true;
                if (!string.IsNullOrWhiteSpace(entry.Url)) return true;
            }
            return false;
        }

        private TermPickerMatch FindMatch(int index)
        {
            foreach (var m in _matches)
                if (m.Index == index) return m;
            return null;
        }

        private void ToggleExpansion(int parentIndex)
        {
            var match = FindMatch(parentIndex);
            if (match == null) return;
            if (match.GetAllTargets().Count <= 1) return;

            if (_expandedParents.Contains(parentIndex))
            {
                _expandedParents.Remove(parentIndex);
                RemoveSubItems(parentIndex);
            }
            else
            {
                _expandedParents.Add(parentIndex);
                AddSubItems(match);
            }
        }

        /// <summary>
        /// True when this control is hosted in the docked pane (set by
        /// TermPickerViewPart). There, Escape hands focus back to the Studio
        /// editor - which also retracts the pane when it is on auto-hide, so
        /// the Alt+P "popup" (really the pane sliding out) is dismissed the
        /// way users expect. Deliberately NOT set for the modal Alt+P dialog:
        /// its own ProcessCmdKey closes the window, and stealing Escape here
        /// would break that.
        /// </summary>
        public bool EscapeReturnsFocusToEditor { get; set; }

        /// <summary>
        /// Escape is pre-processed by Windows as a dialog key, so it never
        /// reaches the ListView's KeyDown. ProcessCmdKey runs on the focused
        /// control before the host form sees the key, which lets the details
        /// popup swallow the first Escape in BOTH surfaces; the second Escape
        /// closes the modal popup (host form) or, in the docked pane, returns
        /// focus to the editor.
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                if (TryHideInfoPopup())
                    return true;
                if (EscapeReturnsFocusToEditor)
                {
                    try
                    {
                        Core.StudioEditor.Controller?.Activate();
                    }
                    catch { /* editor may be mid-transition; Escape just does nothing */ }
                    return true;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>
        /// Hides the details popup if it is showing. Returns true when it was
        /// visible, so callers can tell whether Escape has been "used up".
        /// </summary>
        public static bool TryHideInfoPopup()
        {
            try
            {
                var popup = TermPopup.GetInstance();
                if (!popup.Visible) return false;
                popup.HidePopup();
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Toggles the metadata popup for the selected row – the same popup and
        /// the same content as hovering a TermLens chip or pressing 'i' in the
        /// TermLens popup. Anchored to the row's screen rectangle, since a list
        /// row isn't a control.
        /// </summary>
        public void ToggleInfoForSelected()
        {
            var popup = TermPopup.GetInstance();
            if (popup.Visible)
            {
                popup.HidePopup();
                return;
            }

            if (_listView.SelectedItems.Count == 0) return;
            var row = _listView.SelectedItems[0];
            var tag = row.Tag as RowTag;
            var match = tag?.Match;
            if (match?.PrimaryEntry == null) return;

            var entries = match.AllEntries ?? new List<TermEntry> { match.PrimaryEntry };
            var lines = TermBlock.BuildMetadataLines(
                entries,
                null,                                  // abbreviation matching is a chip concern
                match.PrimaryEntry.Forbidden,
                match.PrimaryEntry.IsMultiTerm,
                match.PrimaryEntry.IsNonTranslatable);

            // Anchor below the row itself
            var rowRect = row.Bounds;
            var screenTopLeft = _listView.PointToScreen(new Point(rowRect.Left, rowRect.Top));
            var anchor = new Rectangle(screenTopLeft, new Size(rowRect.Width, rowRect.Height));

            try { popup.ShowBelow(_listView, anchor, lines); }
            catch { /* popup is a nicety – never break the list */ }
        }

        /// <summary>
        /// Opens the term editor for the selected row, matching the TermLens
        /// popup's 'e' key. MultiTerm entries are read-only, so they are
        /// skipped. Hosts that are modal (the Alt+P popup) close themselves
        /// via <see cref="EditRequested"/> before the editor opens.
        /// </summary>
        public void EditSelected()
        {
            if (_listView.SelectedItems.Count == 0) return;
            var tag = _listView.SelectedItems[0].Tag as RowTag;
            var match = tag?.Match;
            var entry = match?.PrimaryEntry;
            if (entry == null || entry.IsMultiTerm) return;

            var handler = EditRequested;
            if (handler != null)
            {
                handler(this, new TermPickerEditEventArgs
                {
                    Entry = entry,
                    AllEntries = match.AllEntries
                });
                return;
            }

            TermLensEditorViewPart.HandleEditCurrentTerm(entry, match.AllEntries);
        }

        private void RaiseInsert()
        {
            var text = SelectedTargetTerm;
            if (string.IsNullOrEmpty(text)) return;
            InsertRequested?.Invoke(this, new TermPickerInsertEventArgs { TargetTerm = text });
        }

        private void OnListViewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Return)
            {
                e.Handled = true; e.SuppressKeyPress = true;
                RaiseInsert();
            }
            else if (e.KeyCode == Keys.Down)
            {
                if (_listView.Items.Count > 0 && _listView.SelectedIndices.Count > 0
                    && _listView.SelectedIndices[0] == _listView.Items.Count - 1)
                {
                    e.Handled = true; e.SuppressKeyPress = true;
                    _listView.Items[0].Selected = true;
                    _listView.Items[0].Focused = true;
                    _listView.EnsureVisible(0);
                }
            }
            else if (e.KeyCode == Keys.Up)
            {
                if (_listView.Items.Count > 0 && _listView.SelectedIndices.Count > 0
                    && _listView.SelectedIndices[0] == 0)
                {
                    e.Handled = true; e.SuppressKeyPress = true;
                    int last = _listView.Items.Count - 1;
                    _listView.Items[last].Selected = true;
                    _listView.Items[last].Focused = true;
                    _listView.EnsureVisible(last);
                }
            }
            else if (e.KeyCode == Keys.Right)
            {
                e.Handled = true; e.SuppressKeyPress = true;
                if (_listView.SelectedItems.Count > 0)
                {
                    var tag = _listView.SelectedItems[0].Tag as RowTag;
                    if (tag != null && !tag.IsSubItem && !_expandedParents.Contains(tag.ParentIndex))
                        ToggleExpansion(tag.ParentIndex);
                }
            }
            else if (e.KeyCode == Keys.Left)
            {
                e.Handled = true; e.SuppressKeyPress = true;
                if (_listView.SelectedItems.Count > 0)
                {
                    var tag = _listView.SelectedItems[0].Tag as RowTag;
                    if (tag != null && _expandedParents.Contains(tag.ParentIndex))
                    {
                        int parentIdx = tag.ParentIndex;
                        ToggleExpansion(parentIdx);
                        for (int i = 0; i < _listView.Items.Count; i++)
                        {
                            var ptag = _listView.Items[i].Tag as RowTag;
                            if (ptag != null && !ptag.IsSubItem && ptag.ParentIndex == parentIdx)
                            {
                                _listView.Items[i].Selected = true;
                                _listView.Items[i].Focused = true;
                                _listView.EnsureVisible(i);
                                break;
                            }
                        }
                    }
                }
            }
            else if (e.KeyCode == Keys.I && !e.Alt && !e.Control && !e.Shift)
            {
                e.Handled = true; e.SuppressKeyPress = true;
                ToggleInfoForSelected();
            }
            else if (e.KeyCode == Keys.E && !e.Alt && !e.Control && !e.Shift)
            {
                e.Handled = true; e.SuppressKeyPress = true;
                EditSelected();
            }
            else if ((e.KeyCode >= Keys.D0 && e.KeyCode <= Keys.D9
                      || e.KeyCode >= Keys.NumPad0 && e.KeyCode <= Keys.NumPad9)
                     && !e.Alt && !e.Control)
            {
                int digit = e.KeyCode >= Keys.NumPad0
                    ? e.KeyCode - Keys.NumPad0
                    : e.KeyCode - Keys.D0;
                e.Handled = true; e.SuppressKeyPress = true;
                SelectByNumber(digit);
            }
        }

        /// <summary>
        /// Jumps to term N (0 = 10). With nine or fewer matches the number is
        /// unambiguous, so it inserts immediately; with more, it only selects,
        /// leaving room for a second digit.
        /// </summary>
        public void SelectByNumber(int digit)
        {
            int targetIndex = digit == 0 ? 10 : digit;

            for (int i = 0; i < _listView.Items.Count; i++)
            {
                var tag = _listView.Items[i].Tag as RowTag;
                if (tag != null && !tag.IsSubItem && tag.ParentIndex == targetIndex)
                {
                    _listView.Items[i].Selected = true;
                    _listView.Items[i].Focused = true;
                    _listView.EnsureVisible(i);

                    if (_matches.Count <= 9)
                        RaiseInsert();
                    return;
                }
            }
        }

        /// <summary>
        /// ListView with double buffering switched on. The stock control paints
        /// rows one by one, which flickers when the list is rebuilt on every
        /// segment change and when the selection is redrawn on focus.
        /// DoubleBuffered is protected, hence the subclass.
        /// </summary>
        private class BufferedListView : ListView
        {
            public BufferedListView()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            }
        }

        private class RowTag
        {
            public bool IsSubItem { get; set; }
            public int ParentIndex { get; set; }
            public string TargetTerm { get; set; }
            /// <summary>The match this row belongs to – lets 'e' open the term editor.</summary>
            public TermPickerMatch Match { get; set; }
        }
    }

    public class TermPickerInsertEventArgs : EventArgs
    {
        public string TargetTerm { get; set; }
    }

    public class TermPickerEditEventArgs : EventArgs
    {
        public TermEntry Entry { get; set; }
        public List<TermEntry> AllEntries { get; set; }
    }
}
