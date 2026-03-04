using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using TermLens.Models;

namespace TermLens.Controls
{
    /// <summary>
    /// Modal dialog that lists all matched terms for the current segment.
    /// The user can select a term by clicking, pressing Enter, or typing its number.
    /// Rows with multiple target synonyms show a ► indicator and can be expanded
    /// with the Right arrow key to reveal all alternative translations.
    /// Triggered by Ctrl+Shift+G.
    /// </summary>
    public class TermPickerDialog : Form
    {
        private readonly ListView _listView;
        private readonly List<TermPickerMatch> _matches;

        // Track which parent indices (1-based) are currently expanded
        private readonly HashSet<int> _expandedParents = new HashSet<int>();

        // Colors
        private static readonly Color HighPriorityBg = ColorTranslator.FromHtml("#FFE5F0");
        private static readonly Color RegularBg = ColorTranslator.FromHtml("#D6EBFF");
        private static readonly Color SubItemBg = Color.FromArgb(245, 245, 250);

        /// <summary>
        /// The target term string selected by the user, or null if cancelled.
        /// </summary>
        public string SelectedTargetTerm { get; private set; }

        public TermPickerDialog(List<TermPickerMatch> matches)
        {
            _matches = matches ?? new List<TermPickerMatch>();

            Text = "TermLens \u2014 Pick Term to Insert";
            Size = new Size(580, 400);
            MinimumSize = new Size(400, 250);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            KeyPreview = true;

            _listView = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                GridLines = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                Font = new Font("Segoe UI", 9.5f)
            };

            _listView.Columns.Add("#", 40, HorizontalAlignment.Right);
            _listView.Columns.Add("Source", 160, HorizontalAlignment.Left);
            _listView.Columns.Add("Target", 210, HorizontalAlignment.Left);
            _listView.Columns.Add("Termbase", 130, HorizontalAlignment.Left);

            PopulateMainRows();

            // Select first item
            if (_listView.Items.Count > 0)
            {
                _listView.Items[0].Selected = true;
                _listView.Items[0].Focused = true;
            }

            // Bottom panel
            var bottomPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                Padding = new Padding(8, 6, 8, 6)
            };

            var hintLabel = new Label
            {
                Text = "Type a number or Enter to insert \u2022 \u25B6 Right arrow expands synonyms",
                Dock = DockStyle.Left,
                AutoSize = true,
                ForeColor = Color.FromArgb(120, 120, 120),
                Font = new Font("Segoe UI", 8.5f),
                TextAlign = ContentAlignment.MiddleLeft
            };

            var btnCancel = new Button
            {
                Text = "Cancel",
                Dock = DockStyle.Right,
                Width = 80,
                DialogResult = DialogResult.Cancel
            };

            var btnInsert = new Button
            {
                Text = "Insert",
                Dock = DockStyle.Right,
                Width = 80
            };
            btnInsert.Click += (s, e) => AcceptSelection();

            bottomPanel.Controls.Add(hintLabel);
            bottomPanel.Controls.Add(btnCancel);
            bottomPanel.Controls.Add(btnInsert);

            Controls.Add(_listView);
            Controls.Add(bottomPanel);

            _listView.DoubleClick += (s, e) => AcceptSelection();
            _listView.KeyDown += OnListViewKeyDown;
            KeyDown += OnFormKeyDown;

            AcceptButton = null;
            CancelButton = btnCancel;
        }

        private void PopulateMainRows()
        {
            _listView.Items.Clear();

            foreach (var match in _matches)
            {
                var allTargets = match.GetAllTargets();
                bool hasExpansion = allTargets.Count > 1;

                // Source text with ► indicator if expandable
                string sourceDisplay = match.SourceText;
                if (hasExpansion)
                    sourceDisplay += "  \u25B6"; // ►

                var item = new ListViewItem(match.Index.ToString());
                item.SubItems.Add(sourceDisplay);
                item.SubItems.Add(match.PrimaryEntry.TargetTerm ?? "");
                item.SubItems.Add(match.PrimaryEntry.TermbaseName ?? "");

                // Tag stores: ("parent", parentIndex, targetTerm)
                item.Tag = new RowTag
                {
                    IsSubItem = false,
                    ParentIndex = match.Index,
                    TargetTerm = match.PrimaryEntry.TargetTerm
                };

                // Color: project glossary = pink, rest = blue
                item.BackColor = match.IsProjectGlossary ? HighPriorityBg : RegularBg;

                _listView.Items.Add(item);

                // If this parent is expanded, add sub-items immediately
                if (_expandedParents.Contains(match.Index))
                {
                    AddSubItems(match);
                }
            }
        }

        private void AddSubItems(TermPickerMatch match)
        {
            // Find the parent row's position in the ListView
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

            // Change ► to ▼ on the parent row
            string sourceText = match.SourceText + "  \u25BC"; // ▼
            _listView.Items[parentPos].SubItems[1].Text = sourceText;

            // Get all targets, skip the primary (already shown on parent row)
            var allTargets = match.GetAllTargets();
            int insertPos = parentPos + 1;

            for (int t = 1; t < allTargets.Count; t++)
            {
                var option = allTargets[t];

                var subItem = new ListViewItem(""); // empty # column
                subItem.SubItems.Add("    \u2514 " + match.SourceText); // indented with └
                subItem.SubItems.Add(option.TargetTerm);
                subItem.SubItems.Add(option.TermbaseName ?? "");

                subItem.Tag = new RowTag
                {
                    IsSubItem = true,
                    ParentIndex = match.Index,
                    TargetTerm = option.TargetTerm
                };

                subItem.BackColor = SubItemBg;
                subItem.ForeColor = Color.FromArgb(60, 60, 60);

                _listView.Items.Insert(insertPos, subItem);
                insertPos++;
            }
        }

        private void RemoveSubItems(int parentIndex)
        {
            // Remove all sub-items for this parent and reset ► indicator
            for (int i = _listView.Items.Count - 1; i >= 0; i--)
            {
                var tag = _listView.Items[i].Tag as RowTag;
                if (tag != null && tag.IsSubItem && tag.ParentIndex == parentIndex)
                {
                    _listView.Items.RemoveAt(i);
                }
            }

            // Find the parent row and change ▼ back to ►
            for (int i = 0; i < _listView.Items.Count; i++)
            {
                var tag = _listView.Items[i].Tag as RowTag;
                if (tag != null && !tag.IsSubItem && tag.ParentIndex == parentIndex)
                {
                    var match = FindMatch(parentIndex);
                    if (match != null)
                        _listView.Items[i].SubItems[1].Text = match.SourceText + "  \u25B6"; // ►
                    break;
                }
            }
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

            var allTargets = match.GetAllTargets();
            if (allTargets.Count <= 1) return; // nothing to expand

            if (_expandedParents.Contains(parentIndex))
            {
                // Collapse
                _expandedParents.Remove(parentIndex);
                RemoveSubItems(parentIndex);
            }
            else
            {
                // Expand
                _expandedParents.Add(parentIndex);
                AddSubItems(match);
            }
        }

        private void AcceptSelection()
        {
            if (_listView.SelectedItems.Count > 0)
            {
                var tag = _listView.SelectedItems[0].Tag as RowTag;
                if (tag != null)
                {
                    SelectedTargetTerm = tag.TargetTerm;
                    DialogResult = DialogResult.OK;
                    Close();
                }
            }
        }

        private void OnListViewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Return)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                AcceptSelection();
            }
            else if (e.KeyCode == Keys.Right)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;

                if (_listView.SelectedItems.Count > 0)
                {
                    var tag = _listView.SelectedItems[0].Tag as RowTag;
                    if (tag != null && !tag.IsSubItem)
                    {
                        if (!_expandedParents.Contains(tag.ParentIndex))
                            ToggleExpansion(tag.ParentIndex);
                    }
                }
            }
            else if (e.KeyCode == Keys.Left)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;

                if (_listView.SelectedItems.Count > 0)
                {
                    var tag = _listView.SelectedItems[0].Tag as RowTag;
                    if (tag != null)
                    {
                        int parentIdx = tag.ParentIndex;
                        if (_expandedParents.Contains(parentIdx))
                        {
                            ToggleExpansion(parentIdx);

                            // Select the parent row
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
            }
        }

        private void OnFormKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode >= Keys.D0 && e.KeyCode <= Keys.D9 && !e.Alt && !e.Control)
            {
                int digit = e.KeyCode - Keys.D0;
                SelectByNumber(digit);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode >= Keys.NumPad0 && e.KeyCode <= Keys.NumPad9 && !e.Alt && !e.Control)
            {
                int digit = e.KeyCode - Keys.NumPad0;
                SelectByNumber(digit);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void SelectByNumber(int digit)
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
                        AcceptSelection();

                    return;
                }
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _listView.Focus();
        }

        /// <summary>
        /// Tag data stored on each ListViewItem to track parent/sub-item relationships.
        /// </summary>
        private class RowTag
        {
            public bool IsSubItem { get; set; }
            public int ParentIndex { get; set; } // 1-based term index
            public string TargetTerm { get; set; }
        }
    }
}
