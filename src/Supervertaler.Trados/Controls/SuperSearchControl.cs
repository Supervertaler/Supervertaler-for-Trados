using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi.Interfaces;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// WinForms UserControl for the SuperSearch dockable ViewPart.
    /// Provides cross-file search, results grid, and replace bar.
    /// All layout is programmatic (no designer file).
    /// </summary>
    public class SuperSearchControl : UserControl, IUIControl
    {
        // ─── Search bar row 1 ────────────────────────────────────
        private Panel _searchPanel;
        private TextBox _txtSearch;
        private Button _btnSearch;
        private Button _btnStop;
        private ComboBox _cboScope;
        private CheckBox _chkCaseSensitive;
        private CheckBox _chkRegex;
        private CheckBox _chkShowReplace;
        private Button _btnFiles;
        private Button _btnHelp;

        // ─── Replace bar row 2 (hidden by default) ──────────────
        private Panel _replacePanel;
        private TextBox _txtReplace;
        private Button _btnReplace;
        private Button _btnReplaceAll;

        // ─── Results grid ────────────────────────────────────────
        private DataGridView _grid;

        // ─── Preview pane (below grid) ──────────────────────────
        private Panel _previewPanel;
        private RichTextBox _rtbPreviewSource;
        private RichTextBox _rtbPreviewTarget;
        private Label _lblPreviewSource;
        private Label _lblPreviewTarget;

        // ─── Status bar ──────────────────────────────────────────
        private Label _lblStatus;

        // ─── File selection state ────────────────────────────────
        private List<string> _allProjectFiles = new List<string>();
        private HashSet<string> _excludedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ─── Highlight state ─────────────────────────────────────
        private string _highlightQuery;
        private bool _highlightCaseSensitive;

        // ─── Styling ─────────────────────────────────────────────
        private static readonly Color HeaderBg = Color.FromArgb(245, 245, 245);
        private static readonly Color BorderColor = Color.FromArgb(200, 200, 200);
        private static readonly Color TextColor = Color.FromArgb(50, 50, 50);
        private static readonly Color SubtleColor = Color.FromArgb(120, 120, 120);
        private static readonly Color AltRowColor = Color.FromArgb(250, 250, 250);

        // ─── Events ──────────────────────────────────────────────

        /// <summary>Fired when user clicks Search or presses Enter.</summary>
        public event EventHandler<SearchRequestEventArgs> SearchRequested;

        /// <summary>Fired when user clicks Stop.</summary>
        public event EventHandler StopRequested;

        /// <summary>Fired when user double-clicks a result row to navigate.</summary>
        public event EventHandler<NavigateToSegmentEventArgs> NavigateRequested;

        /// <summary>Fired when user clicks Replace (single).</summary>
        public event EventHandler<ReplaceRequestEventArgs> ReplaceRequested;

        /// <summary>Fired when user clicks Replace All.</summary>
        public event EventHandler<ReplaceRequestEventArgs> ReplaceAllRequested;

        /// <summary>Fired when user clicks the help button.</summary>
        public new event EventHandler HelpRequested;

        public SuperSearchControl()
        {
            BuildUI();
        }

        private void BuildUI()
        {
            SuspendLayout();
            BackColor = Color.White;
            Font = new Font("Segoe UI", 9f);

            var bodyFont = new Font("Segoe UI", 8.5f);
            var smallFont = new Font("Segoe UI", 8f);

            // ═══════════════════════════════════════════════════════
            // Search bar panel (row 1)
            // ═══════════════════════════════════════════════════════
            _searchPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 40,
                BackColor = HeaderBg,
                Padding = new Padding(4, 4, 4, 2)
            };

            _txtSearch = new TextBox
            {
                Font = bodyFont,
                Location = new Point(4, 8),
                Width = 200  // will be auto-sized on resize
            };
            _txtSearch.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    FireSearch();
                }
            };
            _searchPanel.Controls.Add(_txtSearch);

            _btnSearch = CreateButton("Search", bodyFont, 72, 26);
            _btnSearch.Click += (s, e) => FireSearch();
            _searchPanel.Controls.Add(_btnSearch);

            _btnStop = CreateButton("Stop", bodyFont, 46, 26);
            _btnStop.Visible = false;
            _btnStop.Click += (s, e) => StopRequested?.Invoke(this, EventArgs.Empty);
            _searchPanel.Controls.Add(_btnStop);

            _cboScope = new ComboBox
            {
                Font = bodyFont,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 145
            };
            _cboScope.Items.AddRange(new object[] { "Source & Target", "Source only", "Target only" });
            _cboScope.SelectedIndex = 0;
            _searchPanel.Controls.Add(_cboScope);

            _chkCaseSensitive = new CheckBox
            {
                Text = "Aa",
                Font = smallFont,
                AutoSize = true,
                ForeColor = SubtleColor
            };
            var ttCaseSensitive = new ToolTip();
            ttCaseSensitive.SetToolTip(_chkCaseSensitive, "Case sensitive");
            _searchPanel.Controls.Add(_chkCaseSensitive);

            _chkRegex = new CheckBox
            {
                Text = ".*",
                Font = smallFont,
                AutoSize = true,
                ForeColor = SubtleColor
            };
            var ttRegex = new ToolTip();
            ttRegex.SetToolTip(_chkRegex, "Use regular expressions");
            _searchPanel.Controls.Add(_chkRegex);

            _chkShowReplace = new CheckBox
            {
                Text = "Replace",
                Font = smallFont,
                AutoSize = true,
                ForeColor = SubtleColor
            };
            _chkShowReplace.CheckedChanged += (s, e) =>
            {
                _replacePanel.Visible = _chkShowReplace.Checked;
            };
            _searchPanel.Controls.Add(_chkShowReplace);

            _btnFiles = CreateButton("Files", bodyFont, 56, 26);
            var ttFiles = new ToolTip();
            ttFiles.SetToolTip(_btnFiles, "Select which files to include in the search");
            _btnFiles.Click += (s, e) => ShowFileSelectionDialog();
            _searchPanel.Controls.Add(_btnFiles);

            _btnHelp = new Button
            {
                Text = "?",
                Font = bodyFont,
                Size = new Size(26, 26),
                FlatStyle = FlatStyle.Flat,
                ForeColor = SubtleColor,
                BackColor = HeaderBg,
                Cursor = Cursors.Hand
            };
            _btnHelp.FlatAppearance.BorderColor = BorderColor;
            _btnHelp.Click += (s, e) => HelpRequested?.Invoke(this, EventArgs.Empty);
            _searchPanel.Controls.Add(_btnHelp);

            _searchPanel.Resize += (s, e) => LayoutSearchBar();
            Controls.Add(_searchPanel);

            // ═══════════════════════════════════════════════════════
            // Replace bar panel (row 2, hidden by default)
            // ═══════════════════════════════════════════════════════
            _replacePanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 36,
                BackColor = HeaderBg,
                Padding = new Padding(4, 2, 4, 2),
                Visible = false
            };

            _txtReplace = new TextBox
            {
                Font = bodyFont,
                Location = new Point(4, 3),
                Width = 200  // will be auto-sized on resize
            };
            _txtReplace.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    FireReplace();
                }
            };
            _replacePanel.Controls.Add(_txtReplace);

            _btnReplace = CreateButton("Replace", bodyFont, 76, 26);
            _btnReplace.Click += (s, e) => FireReplace();
            _replacePanel.Controls.Add(_btnReplace);

            _btnReplaceAll = CreateButton("Replace All", bodyFont, 94, 26);
            _btnReplaceAll.Click += (s, e) => FireReplaceAll();
            _replacePanel.Controls.Add(_btnReplaceAll);

            var lblReplaceHint = new Label
            {
                Text = "(target only)",
                Font = smallFont,
                ForeColor = SubtleColor,
                AutoSize = true
            };
            _replacePanel.Controls.Add(lblReplaceHint);

            _replacePanel.Resize += (s, e) => LayoutReplaceBar();
            Controls.Add(_replacePanel);

            // ═══════════════════════════════════════════════════════
            // Status bar (bottom)
            // ═══════════════════════════════════════════════════════
            _lblStatus = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 22,
                Font = smallFont,
                ForeColor = SubtleColor,
                Text = "Ready",
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 0, 0),
                BackColor = HeaderBg
            };
            Controls.Add(_lblStatus);

            // ═══════════════════════════════════════════════════════
            // Results DataGridView (fills remaining space)
            // ═══════════════════════════════════════════════════════
            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                RowHeadersVisible = false,
                BorderStyle = BorderStyle.None,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                GridColor = Color.FromArgb(230, 230, 230),
                BackgroundColor = Color.White,
                Font = bodyFont,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None
            };

            // Column header style
            _grid.EnableHeadersVisualStyles = false;
            _grid.ColumnHeadersDefaultCellStyle.BackColor = HeaderBg;
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = TextColor;
            _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = HeaderBg;
            _grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = TextColor;
            _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            _grid.ColumnHeadersHeight = 26;
            _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;

            // Row style
            _grid.RowTemplate.Height = 24;
            _grid.DefaultCellStyle.ForeColor = TextColor;
            _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(210, 230, 255);
            _grid.DefaultCellStyle.SelectionForeColor = TextColor;
            _grid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
            _grid.AlternatingRowsDefaultCellStyle.BackColor = AltRowColor;

            // Columns
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "colFile",
                HeaderText = "File",
                Width = 100,
                MinimumWidth = 50
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "colSegNum",
                HeaderText = "#",
                Width = 40,
                MinimumWidth = 30,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight }
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "colSource",
                HeaderText = "Source",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 50,
                MinimumWidth = 100
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "colTarget",
                HeaderText = "Target",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 50,
                MinimumWidth = 100
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "colStatus",
                HeaderText = "Status",
                Width = 130,
                MinimumWidth = 80
            });

            _grid.CellDoubleClick += OnGridDoubleClick;
            _grid.KeyDown += OnGridKeyDown;
            _grid.CellPainting += OnCellPainting;
            _grid.SelectionChanged += OnGridSelectionChanged;

            // ═══════════════════════════════════════════════════════
            // Preview pane (below grid, shows full source+target)
            // ═══════════════════════════════════════════════════════
            _previewPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 100,
                BackColor = Color.White,
                Padding = new Padding(0)
            };

            // Thin top border line
            var previewBorder = new Panel
            {
                Dock = DockStyle.Top,
                Height = 1,
                BackColor = BorderColor
            };

            // Use a TableLayoutPanel for reliable side-by-side layout
            var previewTable = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                BackColor = Color.White,
                Margin = new Padding(0),
                Padding = new Padding(0),
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None
            };
            previewTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            previewTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            previewTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 18f));
            previewTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            // Source header + text
            _lblPreviewSource = new Label
            {
                Text = "Source",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                ForeColor = SubtleColor,
                Padding = new Padding(4, 2, 0, 0),
                BackColor = Color.FromArgb(252, 252, 252)
            };
            _rtbPreviewSource = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 11f),
                ForeColor = TextColor,
                BackColor = Color.White,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                WordWrap = true,
                DetectUrls = false
            };

            // Target header + text
            _lblPreviewTarget = new Label
            {
                Text = "Target",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                ForeColor = SubtleColor,
                Padding = new Padding(4, 2, 0, 0),
                BackColor = Color.FromArgb(252, 252, 252)
            };
            _rtbPreviewTarget = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 11f),
                ForeColor = TextColor,
                BackColor = Color.White,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                WordWrap = true,
                DetectUrls = false
            };

            previewTable.Controls.Add(_lblPreviewSource, 0, 0);
            previewTable.Controls.Add(_lblPreviewTarget, 1, 0);
            previewTable.Controls.Add(_rtbPreviewSource, 0, 1);
            previewTable.Controls.Add(_rtbPreviewTarget, 1, 1);

            _previewPanel.Controls.Add(previewTable);
            _previewPanel.Controls.Add(previewBorder);

            // Add controls in correct order for WinForms docking z-order:
            // Grid (Fill) must be added LAST / be in FRONT of z-order
            Controls.Add(_grid);
            Controls.Add(_previewPanel);

            // Z-order for WinForms docking: back = docks first.
            // Top panels: search (top), replace (below search)
            // Bottom panels: status (bottom), preview (above status)
            // Fill: grid fills remaining space (must be in front)
            _replacePanel.SendToBack();   // second from back → docks below search
            _searchPanel.SendToBack();    // very back → docks at top
            _grid.BringToFront();         // front → fills remaining space

            ResumeLayout(false);
        }

        // ─── Dynamic Layout ──────────────────────────────────────

        private void LayoutSearchBar()
        {
            if (_searchPanel == null || _txtSearch == null) return;

            int w = _searchPanel.ClientSize.Width;
            int h = _searchPanel.ClientSize.Height;
            int btnY = (h - _btnSearch.Height) / 2;         // vertically center buttons
            int txtY = (h - _txtSearch.Height) / 2;          // vertically center text box
            int chkY = (h - _chkCaseSensitive.Height) / 2;   // vertically center checkboxes
            int cboY = (h - _cboScope.Height) / 2;            // vertically center combo

            // Right-anchored controls first (right to left)
            _btnHelp.Location = new Point(w - _btnHelp.Width - 4, btnY);
            _btnFiles.Location = new Point(_btnHelp.Left - _btnFiles.Width - 4, btnY);

            // Fixed controls from left after the search box
            int fixedLeft = _btnFiles.Left;

            // Position from right: chkShowReplace, chkRegex, chkCaseSensitive, cboScope, btnStop, btnSearch
            _chkShowReplace.Location = new Point(fixedLeft - _chkShowReplace.Width - 4, chkY);
            _chkRegex.Location = new Point(_chkShowReplace.Left - _chkRegex.Width - 2, chkY);
            _chkCaseSensitive.Location = new Point(_chkRegex.Left - _chkCaseSensitive.Width - 2, chkY);
            _cboScope.Location = new Point(_chkCaseSensitive.Left - _cboScope.Width - 6, cboY);
            _btnStop.Location = new Point(_cboScope.Left - _btnStop.Width - 4, btnY);
            _btnSearch.Location = new Point(_btnStop.Left - _btnSearch.Width - 2, btnY);

            // Search text box fills available space
            _txtSearch.Location = new Point(4, txtY);
            int searchRight = _btnSearch.Left - 4;
            _txtSearch.Width = Math.Max(80, searchRight - _txtSearch.Left);
        }

        private void LayoutReplaceBar()
        {
            if (_replacePanel == null || _txtReplace == null) return;

            int h = _replacePanel.ClientSize.Height;
            int txtY = (h - _txtReplace.Height) / 2;
            int btnY = (h - _btnReplace.Height) / 2;

            _txtReplace.Location = new Point(4, txtY);
            _txtReplace.Width = _txtSearch.Width;

            _btnReplace.Location = new Point(_txtReplace.Right + 4, btnY);
            _btnReplaceAll.Location = new Point(_btnReplace.Right + 2, btnY);

            // The hint label is the last child
            var hint = _replacePanel.Controls[_replacePanel.Controls.Count - 1] as Label;
            if (hint != null)
                hint.Location = new Point(_btnReplaceAll.Right + 6, btnY + 4);
        }

        // ─── File Selection ──────────────────────────────────────

        /// <summary>
        /// Updates the list of available project files (called by the ViewPart).
        /// </summary>
        public void SetProjectFiles(List<string> files)
        {
            _allProjectFiles = files ?? new List<string>();
            // Remove any excluded files that no longer exist in the project
            _excludedFiles.IntersectWith(_allProjectFiles);
            UpdateFilesButton();
        }

        /// <summary>
        /// Gets the list of files to search (all project files minus excluded ones).
        /// </summary>
        public List<string> GetSelectedFiles()
        {
            if (_excludedFiles.Count == 0)
                return _allProjectFiles;

            return _allProjectFiles.Where(f => !_excludedFiles.Contains(f)).ToList();
        }

        private void UpdateFilesButton()
        {
            int included = _allProjectFiles.Count - _excludedFiles.Count;
            int total = _allProjectFiles.Count;
            _btnFiles.Text = included == total
                ? $"Files ({total})"
                : $"Files ({included}/{total})";
        }

        private void ShowFileSelectionDialog()
        {
            if (_allProjectFiles.Count == 0)
            {
                MessageBox.Show("No project files found. Open a file in the editor first.",
                    "SuperSearch", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new Form())
            {
                dlg.Text = "SuperSearch \u2014 Select Files";
                dlg.Size = new Size(600, 450);
                dlg.MinimumSize = new Size(400, 250);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.FormBorderStyle = FormBorderStyle.Sizable;
                dlg.ShowIcon = false;
                dlg.ShowInTaskbar = false;
                dlg.Font = new Font("Segoe UI", 9f);

                var lblInfo = new Label
                {
                    Text = "Select which files to include in the search:",
                    Dock = DockStyle.Top,
                    Height = 28,
                    Padding = new Padding(8, 8, 8, 0),
                    ForeColor = TextColor
                };
                dlg.Controls.Add(lblInfo);

                var clb = new CheckedListBox
                {
                    Dock = DockStyle.Fill,
                    CheckOnClick = true,
                    Font = new Font("Segoe UI", 8.5f),
                    IntegralHeight = false,
                    BorderStyle = BorderStyle.FixedSingle
                };

                foreach (var file in _allProjectFiles)
                {
                    var shortName = Path.GetFileName(file);
                    bool isChecked = !_excludedFiles.Contains(file);
                    clb.Items.Add(shortName, isChecked);
                }
                dlg.Controls.Add(clb);

                // Bottom panel with Select All / None / OK
                var bottomPanel = new Panel
                {
                    Dock = DockStyle.Bottom,
                    Height = 40,
                    BackColor = HeaderBg,
                    Padding = new Padding(8, 6, 8, 6)
                };

                var btnSelectAll = CreateButton("Select All", dlg.Font, 90, 28);
                btnSelectAll.Location = new Point(8, 6);
                btnSelectAll.Click += (s, e) =>
                {
                    for (int i = 0; i < clb.Items.Count; i++)
                        clb.SetItemChecked(i, true);
                };
                bottomPanel.Controls.Add(btnSelectAll);

                var btnSelectNone = CreateButton("Select None", dlg.Font, 100, 28);
                btnSelectNone.Location = new Point(btnSelectAll.Right + 4, 6);
                btnSelectNone.Click += (s, e) =>
                {
                    for (int i = 0; i < clb.Items.Count; i++)
                        clb.SetItemChecked(i, false);
                };
                bottomPanel.Controls.Add(btnSelectNone);

                var btnOk = CreateButton("OK", dlg.Font, 70, 28);
                btnOk.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                btnOk.Location = new Point(bottomPanel.Width - btnOk.Width - 8, 6);
                btnOk.Click += (s, e) =>
                {
                    _excludedFiles.Clear();
                    for (int i = 0; i < clb.Items.Count; i++)
                    {
                        if (!clb.GetItemChecked(i))
                            _excludedFiles.Add(_allProjectFiles[i]);
                    }
                    UpdateFilesButton();
                    dlg.DialogResult = DialogResult.OK;
                };
                bottomPanel.Controls.Add(btnOk);

                dlg.Controls.Add(bottomPanel);
                dlg.AcceptButton = btnOk;

                // Fix z-order
                clb.BringToFront();

                dlg.ShowDialog(this);
            }
        }

        // ─── Public Methods ──────────────────────────────────────

        /// <summary>
        /// Populates the results grid with search results.
        /// Must be called on the UI thread.
        /// </summary>
        public void SetResults(List<SearchResult> results)
        {
            SetResults(results, _txtSearch.Text, _chkCaseSensitive.Checked);
        }

        /// <summary>
        /// Populates the results grid with search results and stores the query for highlighting.
        /// </summary>
        public void SetResults(List<SearchResult> results, string highlightQuery, bool caseSensitive)
        {
            _highlightQuery = highlightQuery;
            _highlightCaseSensitive = caseSensitive;

            _grid.SuspendLayout();
            _grid.Rows.Clear();

            foreach (var r in results)
            {
                var idx = _grid.Rows.Add(r.FileName, r.SegmentNumber, r.SourceText, r.TargetText, r.Status);
                var row = _grid.Rows[idx];
                row.Tag = r;
                row.Cells["colFile"].ToolTipText = r.FilePath;
                row.Cells["colSource"].ToolTipText = r.SourceText;
                row.Cells["colTarget"].ToolTipText = r.TargetText;
            }

            _grid.ResumeLayout();
        }

        /// <summary>
        /// Updates the status bar text.
        /// </summary>
        public void SetStatus(string text)
        {
            _lblStatus.Text = text;
        }

        /// <summary>
        /// Shows/hides the Stop button and toggles the Search button.
        /// </summary>
        public void SetSearching(bool searching)
        {
            _btnSearch.Enabled = !searching;
            _btnStop.Visible = searching;
            _btnReplace.Enabled = !searching;
            _btnReplaceAll.Enabled = !searching;
        }

        /// <summary>
        /// Gets the currently selected search result, or null.
        /// </summary>
        public SearchResult GetSelectedResult()
        {
            if (_grid.SelectedRows.Count == 0) return null;
            return _grid.SelectedRows[0].Tag as SearchResult;
        }

        /// <summary>
        /// Gets the current search query text.
        /// </summary>
        public string SearchQuery => _txtSearch.Text;

        /// <summary>
        /// Gets the current replace text.
        /// </summary>
        public string ReplaceText => _txtReplace.Text;

        /// <summary>
        /// Focuses the search text box.
        /// </summary>
        public void FocusSearch()
        {
            _txtSearch.Focus();
            _txtSearch.SelectAll();
        }

        /// <summary>
        /// Sets the search text and optionally triggers a search.
        /// Used by the context menu "SuperSearch" action.
        /// </summary>
        public void SetSearchText(string text, bool autoSearch = false)
        {
            _txtSearch.Text = text ?? "";
            _txtSearch.Focus();
            if (autoSearch && !string.IsNullOrWhiteSpace(text))
                FireSearch();
        }

        // ─── Cell Painting (search term highlighting) ────────────

        private void OnCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            // Only highlight Source and Target columns
            if (e.RowIndex < 0) return;
            if (e.ColumnIndex != 2 && e.ColumnIndex != 3) return; // colSource=2, colTarget=3
            if (string.IsNullOrEmpty(_highlightQuery)) return;

            var cellText = e.Value?.ToString();
            if (string.IsNullOrEmpty(cellText)) return;

            var comparison = _highlightCaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            // Check if the query appears in this cell
            int firstIdx = cellText.IndexOf(_highlightQuery, comparison);
            if (firstIdx < 0) return;

            // Let the grid paint the background and borders
            e.PaintBackground(e.ClipBounds, true);

            // Measure and paint text with highlights
            var font = e.CellStyle.Font ?? _grid.Font;
            var cellBounds = e.CellBounds;
            var textColor = e.CellStyle.SelectionForeColor;
            var isSelected = (e.State & DataGridViewElementStates.Selected) != 0;
            var fgColor = isSelected ? e.CellStyle.SelectionForeColor : e.CellStyle.ForeColor;
            var highlightBrush = new SolidBrush(Color.FromArgb(255, 235, 120)); // warm yellow
            var textBrush = new SolidBrush(fgColor);

            // Text area (account for padding)
            var textRect = new Rectangle(
                cellBounds.X + 3, cellBounds.Y + 2,
                cellBounds.Width - 6, cellBounds.Height - 4);

            var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                        TextFormatFlags.SingleLine;

            // First, draw the full text
            TextRenderer.DrawText(e.Graphics, cellText, font, textRect, fgColor, flags);

            // Now overlay highlights on each match
            int pos = 0;
            while (pos < cellText.Length)
            {
                int idx = cellText.IndexOf(_highlightQuery, pos, comparison);
                if (idx < 0) break;

                // Measure the text before the match to get X offset
                var before = cellText.Substring(0, idx);
                var matchText = cellText.Substring(idx, _highlightQuery.Length);

                var beforeSize = TextRenderer.MeasureText(e.Graphics, before, font,
                    new Size(int.MaxValue, textRect.Height), flags);
                var matchSize = TextRenderer.MeasureText(e.Graphics, matchText, font,
                    new Size(int.MaxValue, textRect.Height), flags);

                // Adjust for TextRenderer's internal padding
                int xOffset = beforeSize.Width - 4; // TextRenderer adds ~4px padding
                if (idx == 0) xOffset = 0;

                var highlightRect = new Rectangle(
                    textRect.X + xOffset, textRect.Y,
                    matchSize.Width - 4, textRect.Height);

                // Clip to cell bounds
                highlightRect.Intersect(textRect);

                if (highlightRect.Width > 0)
                {
                    e.Graphics.FillRectangle(highlightBrush, highlightRect);
                    TextRenderer.DrawText(e.Graphics, matchText, font, highlightRect,
                        Color.FromArgb(50, 50, 50), flags);
                }

                pos = idx + _highlightQuery.Length;
            }

            highlightBrush.Dispose();
            textBrush.Dispose();

            e.Handled = true;
        }

        // ─── Preview Pane ────────────────────────────────────────

        private void OnGridSelectionChanged(object sender, EventArgs e)
        {
            var result = GetSelectedResult();
            if (result == null)
            {
                _rtbPreviewSource.Text = "";
                _rtbPreviewTarget.Text = "";
                return;
            }

            PopulatePreview(_rtbPreviewSource, result.SourceText);
            PopulatePreview(_rtbPreviewTarget, result.TargetText);
        }

        /// <summary>
        /// Sets the preview text and highlights the search term in yellow.
        /// </summary>
        private void PopulatePreview(RichTextBox rtb, string text)
        {
            rtb.Text = text ?? "";

            if (string.IsNullOrEmpty(_highlightQuery) || string.IsNullOrEmpty(text))
                return;

            var comparison = _highlightCaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            int pos = 0;
            while (pos < text.Length)
            {
                int idx = text.IndexOf(_highlightQuery, pos, comparison);
                if (idx < 0) break;

                rtb.Select(idx, _highlightQuery.Length);
                rtb.SelectionBackColor = Color.FromArgb(255, 235, 120);
                pos = idx + _highlightQuery.Length;
            }

            rtb.Select(0, 0);
        }

        // ─── Private Helpers ─────────────────────────────────────

        private void FireSearch()
        {
            if (string.IsNullOrWhiteSpace(_txtSearch.Text)) return;

            SearchScope scope;
            switch (_cboScope.SelectedIndex)
            {
                case 1: scope = SearchScope.SourceOnly; break;
                case 2: scope = SearchScope.TargetOnly; break;
                default: scope = SearchScope.SourceAndTarget; break;
            }

            SearchRequested?.Invoke(this, new SearchRequestEventArgs
            {
                Query = _txtSearch.Text,
                Scope = scope,
                CaseSensitive = _chkCaseSensitive.Checked,
                UseRegex = _chkRegex.Checked
            });
        }

        private void FireReplace()
        {
            var result = GetSelectedResult();
            if (result == null) return;

            ReplaceRequested?.Invoke(this, new ReplaceRequestEventArgs
            {
                SearchText = _txtSearch.Text,
                ReplaceText = _txtReplace.Text,
                CaseSensitive = _chkCaseSensitive.Checked,
                UseRegex = _chkRegex.Checked,
                SelectedResult = result
            });
        }

        private void FireReplaceAll()
        {
            ReplaceAllRequested?.Invoke(this, new ReplaceRequestEventArgs
            {
                SearchText = _txtSearch.Text,
                ReplaceText = _txtReplace.Text,
                CaseSensitive = _chkCaseSensitive.Checked,
                UseRegex = _chkRegex.Checked
            });
        }

        private void OnGridDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            var result = _grid.Rows[e.RowIndex].Tag as SearchResult;
            if (result == null) return;

            NavigateRequested?.Invoke(this, new NavigateToSegmentEventArgs
            {
                ParagraphUnitId = result.ParagraphUnitId,
                SegmentId = result.SegmentId
            });
        }

        private void OnGridKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                var result = GetSelectedResult();
                if (result == null) return;

                NavigateRequested?.Invoke(this, new NavigateToSegmentEventArgs
                {
                    ParagraphUnitId = result.ParagraphUnitId,
                    SegmentId = result.SegmentId
                });
            }
        }

        private static Button CreateButton(string text, Font font, int width, int height)
        {
            var btn = new Button
            {
                Text = text,
                Font = font,
                Size = new Size(width, height),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.FromArgb(80, 80, 80),
                BackColor = Color.FromArgb(245, 245, 245),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
            btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(230, 230, 230);
            return btn;
        }
    }

    // ─── Event Argument Classes ──────────────────────────────

    public class SearchRequestEventArgs : EventArgs
    {
        public string Query { get; set; }
        public SearchScope Scope { get; set; }
        public bool CaseSensitive { get; set; }
        public bool UseRegex { get; set; }
    }

    public class ReplaceRequestEventArgs : EventArgs
    {
        public string SearchText { get; set; }
        public string ReplaceText { get; set; }
        public bool CaseSensitive { get; set; }
        public bool UseRegex { get; set; }
        public SearchResult SelectedResult { get; set; }
    }
}
