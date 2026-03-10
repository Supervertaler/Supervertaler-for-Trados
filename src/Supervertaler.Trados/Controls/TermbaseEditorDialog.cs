using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Windows.Forms;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Models;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// Dialog for browsing, editing, adding, and deleting terms in a single termbase.
    /// Opened from the Settings dialog by clicking "Open" or double-clicking a termbase row.
    /// </summary>
    public class TermbaseEditorDialog : Form
    {
        private readonly string _dbPath;
        private readonly TermbaseInfo _termbase;
        private readonly TermLensSettings _settings;

        private DataTable _dataTable;
        private BindingSource _bindingSource;
        private DataGridView _dgvTerms;
        private TextBox _txtSearch;
        private Label _lblTermCount;
        private Button _btnDelete;
        private Button _btnMerge;
        private Button _btnBulkNt;
        private CheckBox _chkNtOnly;
        private Button _btnClose;
        private ContextMenuStrip _rowContextMenu;

        // Synonym counts keyed by term ID, loaded once
        private Dictionary<long, int> _synonymCounts;

        // Track whether we're loading data (to suppress CellValueChanged during population)
        private bool _isLoading;

        public TermbaseEditorDialog(string dbPath, TermbaseInfo termbase, TermLensSettings settings)
        {
            _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
            _termbase = termbase ?? throw new ArgumentNullException(nameof(termbase));
            _settings = settings;

            BuildUI();
            LoadTerms();

            // Restore persisted form size
            if (_settings != null && _settings.TermbaseEditorWidth > 0 && _settings.TermbaseEditorHeight > 0)
                Size = new Size(_settings.TermbaseEditorWidth, _settings.TermbaseEditorHeight);
        }

        private void BuildUI()
        {
            Text = $"Termbase Editor \u2014 {_termbase.Name} ({LanguageUtils.ShortenLanguageName(_termbase.SourceLang)} \u2192 {LanguageUtils.ShortenLanguageName(_termbase.TargetLang)})";
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            HelpButton = true;
            HelpButtonClicked += OnHelpButtonClicked;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(800, 500);
            MinimumSize = new Size(600, 350);
            BackColor = Color.White;

            // === Toolbar area ===
            var toolbarPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 36,
                Padding = new Padding(8, 6, 8, 4),
                BackColor = Color.White
            };

            var lblSearch = new Label
            {
                Text = "Search:",
                AutoSize = true,
                Location = new Point(10, 10),
                ForeColor = Color.FromArgb(80, 80, 80)
            };
            toolbarPanel.Controls.Add(lblSearch);

            _txtSearch = new TextBox
            {
                Location = new Point(62, 7),
                Width = 220,
                BackColor = Color.FromArgb(250, 250, 250)
            };
            _txtSearch.TextChanged += OnSearchTextChanged;
            toolbarPanel.Controls.Add(_txtSearch);

            _chkNtOnly = new CheckBox
            {
                Text = "NT only",
                AutoSize = true,
                Location = new Point(_txtSearch.Right + 12, 9),
                ForeColor = Color.FromArgb(80, 80, 80),
                Font = new Font("Segoe UI", 8f)
            };
            _chkNtOnly.CheckedChanged += OnNtFilterChanged;
            toolbarPanel.Controls.Add(_chkNtOnly);

            _btnBulkNt = new Button
            {
                Text = "Bulk Add NT",
                Width = 90,
                Height = 24,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(80, 80, 80),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnBulkNt.FlatAppearance.BorderSize = 0;
            _btnBulkNt.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 220, 220);
            _btnBulkNt.Click += OnBulkAddNtClick;
            toolbarPanel.Controls.Add(_btnBulkNt);

            _btnMerge = new Button
            {
                Text = "Merge Selected",
                Width = 105,
                Height = 24,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(80, 80, 80),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnMerge.FlatAppearance.BorderSize = 0;
            _btnMerge.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 220, 220);
            _btnMerge.Click += OnMergeSelectedClick;
            toolbarPanel.Controls.Add(_btnMerge);

            _btnDelete = new Button
            {
                Text = "Delete Selected",
                Width = 105,
                Height = 24,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(80, 80, 80),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnDelete.FlatAppearance.BorderSize = 0;
            _btnDelete.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 220, 220);
            _btnDelete.Click += OnDeleteSelectedClick;
            toolbarPanel.Controls.Add(_btnDelete);

            // Position buttons at right edge
            _btnDelete.Location = new Point(ClientSize.Width - 16 - _btnDelete.Width, 7);
            _btnMerge.Location = new Point(_btnDelete.Left - _btnMerge.Width - 4, 7);
            _btnBulkNt.Location = new Point(_btnMerge.Left - _btnBulkNt.Width - 4, 7);

            _lblTermCount = new Label
            {
                AutoSize = true,
                ForeColor = Color.FromArgb(120, 120, 120),
                Font = new Font("Segoe UI", 8f),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _lblTermCount.Location = new Point(_btnBulkNt.Left - 120, 10);
            toolbarPanel.Controls.Add(_lblTermCount);

            // === DataGridView ===
            _dgvTerms = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = true,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = true,
                RowHeadersWidth = 30,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true,
                ReadOnly = false,
                BorderStyle = BorderStyle.None,
                BackgroundColor = Color.FromArgb(250, 250, 250),
                Font = new Font("Segoe UI", 8.5f),
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                EnableHeadersVisualStyles = false,
                EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2
            };

            _dgvTerms.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(240, 240, 240),
                ForeColor = Color.FromArgb(50, 50, 50),
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                SelectionBackColor = Color.FromArgb(240, 240, 240),
                SelectionForeColor = Color.FromArgb(50, 50, 50)
            };
            _dgvTerms.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(250, 250, 250),
                ForeColor = Color.FromArgb(40, 40, 40),
                SelectionBackColor = Color.FromArgb(220, 235, 252),
                SelectionForeColor = Color.FromArgb(40, 40, 40)
            };

            _dgvTerms.CellValueChanged += OnCellValueChanged;
            _dgvTerms.RowValidating += OnRowValidating;
            _dgvTerms.UserAddedRow += OnUserAddedRow;
            _dgvTerms.DataError += OnDataError;
            _dgvTerms.CellDoubleClick += OnCellDoubleClick;

            // Row context menu
            _rowContextMenu = new ContextMenuStrip();

            var copyCellItem = new ToolStripMenuItem("Copy cell");
            copyCellItem.Click += OnContextCopyCellClick;
            _rowContextMenu.Items.Add(copyCellItem);

            _rowContextMenu.Items.Add(new ToolStripSeparator());

            var editItem = new ToolStripMenuItem("Edit term\u2026");
            editItem.Click += OnContextEditClick;
            _rowContextMenu.Items.Add(editItem);

            var deleteItem = new ToolStripMenuItem("Delete term");
            deleteItem.Click += OnContextDeleteClick;
            _rowContextMenu.Items.Add(deleteItem);

            _dgvTerms.CellMouseClick += OnCellMouseClick;
            _dgvTerms.ClipboardCopyMode = DataGridViewClipboardCopyMode.Disable;

            // === Bottom bar ===
            var bottomPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                Padding = new Padding(8, 4, 8, 4),
                BackColor = Color.White
            };

            // Separator line
            var sep = new Label
            {
                Dock = DockStyle.Top,
                Height = 1,
                BorderStyle = BorderStyle.Fixed3D
            };
            bottomPanel.Controls.Add(sep);

            _btnClose = new Button
            {
                Text = "Close",
                DialogResult = DialogResult.Cancel,
                Width = 75,
                FlatStyle = FlatStyle.System,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            _btnClose.Location = new Point(
                ClientSize.Width - 16 - _btnClose.Width,
                bottomPanel.Height - _btnClose.Height - 6);
            bottomPanel.Controls.Add(_btnClose);

            CancelButton = _btnClose;

            // Add controls in order: bottom bar first, then toolbar, then grid (Fill)
            Controls.Add(_dgvTerms);
            Controls.Add(toolbarPanel);
            Controls.Add(bottomPanel);
        }

        private void LoadTerms()
        {
            _isLoading = true;

            try
            {
                var terms = TermbaseReader.GetAllTermsByTermbaseId(_dbPath, _termbase.Id);

                // Load synonym counts for this termbase
                try { _synonymCounts = TermbaseReader.GetSynonymCounts(_dbPath, _termbase.Id); }
                catch { _synonymCounts = new Dictionary<long, int>(); }

                _dataTable = new DataTable();
                _dataTable.Columns.Add("Id", typeof(long));
                _dataTable.Columns.Add("SourceTerm", typeof(string));
                _dataTable.Columns.Add("TargetTerm", typeof(string));
                _dataTable.Columns.Add("Synonyms", typeof(string));
                _dataTable.Columns.Add("Definition", typeof(string));
                _dataTable.Columns.Add("Domain", typeof(string));
                _dataTable.Columns.Add("Notes", typeof(string));
                _dataTable.Columns.Add("NT", typeof(bool));

                foreach (var term in terms)
                {
                    int synCount;
                    _synonymCounts.TryGetValue(term.Id, out synCount);
                    _dataTable.Rows.Add(
                        term.Id,
                        term.SourceTerm ?? "",
                        term.TargetTerm ?? "",
                        synCount > 0 ? $"{synCount} syn." : "",
                        term.Definition ?? "",
                        term.Domain ?? "",
                        term.Notes ?? "",
                        term.IsNonTranslatable);
                }

                _bindingSource = new BindingSource { DataSource = _dataTable };
                _dgvTerms.DataSource = _bindingSource;

                // Configure columns
                if (_dgvTerms.Columns.Contains("Id"))
                {
                    _dgvTerms.Columns["Id"].Visible = false;
                }
                if (_dgvTerms.Columns.Contains("SourceTerm"))
                {
                    _dgvTerms.Columns["SourceTerm"].HeaderText =
                        !string.IsNullOrEmpty(_termbase.SourceLang) ? LanguageUtils.ShortenLanguageName(_termbase.SourceLang) : "Source Term";
                    _dgvTerms.Columns["SourceTerm"].FillWeight = 30;
                }
                if (_dgvTerms.Columns.Contains("TargetTerm"))
                {
                    _dgvTerms.Columns["TargetTerm"].HeaderText =
                        !string.IsNullOrEmpty(_termbase.TargetLang) ? LanguageUtils.ShortenLanguageName(_termbase.TargetLang) : "Target Term";
                    _dgvTerms.Columns["TargetTerm"].FillWeight = 28;
                }
                if (_dgvTerms.Columns.Contains("Synonyms"))
                {
                    _dgvTerms.Columns["Synonyms"].HeaderText = "Syn.";
                    _dgvTerms.Columns["Synonyms"].ToolTipText = "Synonym count";
                    _dgvTerms.Columns["Synonyms"].ReadOnly = true;
                    _dgvTerms.Columns["Synonyms"].AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
                    _dgvTerms.Columns["Synonyms"].Width = 52;
                    _dgvTerms.Columns["Synonyms"].FillWeight = 1;
                    _dgvTerms.Columns["Synonyms"].DefaultCellStyle.ForeColor = Color.FromArgb(120, 120, 120);
                    _dgvTerms.Columns["Synonyms"].DefaultCellStyle.Font = new Font("Segoe UI", 7.5f);
                }
                if (_dgvTerms.Columns.Contains("Definition"))
                {
                    _dgvTerms.Columns["Definition"].HeaderText = "Definition";
                    _dgvTerms.Columns["Definition"].FillWeight = 25;
                }
                if (_dgvTerms.Columns.Contains("Domain"))
                {
                    _dgvTerms.Columns["Domain"].HeaderText = "Domain";
                    _dgvTerms.Columns["Domain"].FillWeight = 10;
                    _dgvTerms.Columns["Domain"].MinimumWidth = 60;
                }
                if (_dgvTerms.Columns.Contains("Notes"))
                {
                    _dgvTerms.Columns["Notes"].HeaderText = "Notes";
                    _dgvTerms.Columns["Notes"].FillWeight = 15;
                }
                if (_dgvTerms.Columns.Contains("NT"))
                {
                    _dgvTerms.Columns["NT"].HeaderText = "NT";
                    _dgvTerms.Columns["NT"].ToolTipText = "Non-translatable";
                    _dgvTerms.Columns["NT"].AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
                    _dgvTerms.Columns["NT"].Width = 36;
                    _dgvTerms.Columns["NT"].FillWeight = 1;
                }

                UpdateTermCountLabel();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load terms:\n{ex.Message}",
                    "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _isLoading = false;
            }
        }

        private void UpdateTermCountLabel()
        {
            int total = _dataTable?.Rows.Count ?? 0;
            int visible = _bindingSource?.Count ?? 0;

            // Subtract 1 for the "new row" if it's being shown
            if (_dgvTerms.AllowUserToAddRows && visible > 0)
                visible = Math.Max(0, visible);

            _lblTermCount.Text = _bindingSource != null && !string.IsNullOrEmpty(_bindingSource.Filter)
                ? $"{visible} of {total} terms"
                : $"{total} terms";
        }

        // ─── Search / filter ──────────────────────────────────────────

        private void OnSearchTextChanged(object sender, EventArgs e)
        {
            ApplyFilters();
        }

        private void OnNtFilterChanged(object sender, EventArgs e)
        {
            ApplyFilters();
        }

        /// <summary>
        /// Builds a composite filter from search text and NT-only checkbox,
        /// then applies it to the BindingSource.
        /// </summary>
        private void ApplyFilters()
        {
            if (_bindingSource == null) return;

            var parts = new List<string>();

            // Search filter
            var text = _txtSearch.Text.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                var escaped = text.Replace("'", "''");
                parts.Add($"(SourceTerm LIKE '%{escaped}%' OR TargetTerm LIKE '%{escaped}%' " +
                          $"OR Definition LIKE '%{escaped}%')");
            }

            // NT filter
            if (_chkNtOnly != null && _chkNtOnly.Checked)
            {
                parts.Add("NT = True");
            }

            if (parts.Count > 0)
                _bindingSource.Filter = string.Join(" AND ", parts);
            else
                _bindingSource.RemoveFilter();

            UpdateTermCountLabel();
        }

        // ─── Bulk Add Non-Translatables ─────────────────────────────────

        private void OnBulkAddNtClick(object sender, EventArgs e)
        {
            using (var dlg = new BulkAddNTDialog())
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Terms.Count == 0)
                    return;

                _isLoading = true;
                try
                {
                    var results = TermbaseReader.InsertNonTranslatableBatch(
                        _dbPath, _termbase.Id,
                        _termbase.SourceLang, _termbase.TargetLang,
                        dlg.Terms);

                    foreach (var (term, newId) in results)
                    {
                        _dataTable.Rows.Add(newId, term, term, "", "", "", "", true);
                    }

                    UpdateTermCountLabel();
                    TermLensEditorViewPart.NotifyTermAdded();

                    int skipped = dlg.Terms.Count - results.Count;
                    var msg = $"Added {results.Count} non-translatable term(s).";
                    if (skipped > 0)
                        msg += $"\n{skipped} duplicate(s) skipped.";
                    MessageBox.Show(msg, "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to add terms:\n{ex.Message}",
                        "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    _isLoading = false;
                }
            }
        }

        // ─── Inline editing / saving ──────────────────────────────────

        private void OnCellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (_isLoading || e.RowIndex < 0) return;

            // Skip if this is the new-row template
            if (_dgvTerms.Rows[e.RowIndex].IsNewRow) return;

            var row = ((DataRowView)_bindingSource[e.RowIndex]).Row;
            var id = row["Id"] as long? ?? 0;

            // Only update existing rows (id > 0)
            if (id <= 0) return;

            var source = (row["SourceTerm"] as string ?? "").Trim();
            var target = (row["TargetTerm"] as string ?? "").Trim();

            // Don't save if source or target is empty
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
                return;

            try
            {
                var isNt = row["NT"] as bool? ?? false;
                TermbaseReader.UpdateTerm(_dbPath, id,
                    source, target,
                    row["Definition"] as string ?? "",
                    row["Domain"] as string ?? "",
                    row["Notes"] as string ?? "",
                    isNonTranslatable: isNt);

                // Refresh the in-memory term index so TermLens reflects
                // the edit immediately (source/target text may have changed)
                TermLensEditorViewPart.NotifyTermAdded();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save change:\n{ex.Message}",
                    "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ─── Adding new terms via the new row ─────────────────────────

        private bool _newRowPending;

        private void OnUserAddedRow(object sender, DataGridViewRowEventArgs e)
        {
            _newRowPending = true;
        }

        private void OnRowValidating(object sender, DataGridViewCellCancelEventArgs e)
        {
            if (_isLoading) return;

            // Check if this is a new row that was just committed
            if (!_newRowPending) return;
            if (e.RowIndex < 0 || e.RowIndex >= _bindingSource.Count) return;

            var rowView = _bindingSource[e.RowIndex] as DataRowView;
            if (rowView == null) return;

            var row = rowView.Row;
            var id = row["Id"] as long? ?? 0;
            if (id > 0) return; // Already saved

            var source = (row["SourceTerm"] as string ?? "").Trim();
            var target = (row["TargetTerm"] as string ?? "").Trim();

            // Both source and target are required
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
                return;

            _newRowPending = false;

            try
            {
                _isLoading = true;

                var newId = TermbaseReader.InsertTerm(_dbPath, _termbase.Id,
                    source, target,
                    _termbase.SourceLang, _termbase.TargetLang,
                    row["Definition"] as string ?? "",
                    row["Domain"] as string ?? "",
                    row["Notes"] as string ?? "");

                if (newId > 0)
                {
                    row["Id"] = newId;
                    UpdateTermCountLabel();
                    TermLensEditorViewPart.NotifyTermAdded();
                }
                else
                {
                    MessageBox.Show("This term already exists in the termbase.",
                        "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to add term:\n{ex.Message}",
                    "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _isLoading = false;
            }
        }

        // ─── Deletion ─────────────────────────────────────────────────

        private void OnDeleteSelectedClick(object sender, EventArgs e)
        {
            DeleteSelectedRows();
        }

        private void DeleteSelectedRows()
        {
            if (_dgvTerms.SelectedRows.Count == 0)
            {
                MessageBox.Show("Select one or more rows to delete.",
                    "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Collect term IDs to delete (skip the "new row")
            var toDelete = new List<(long id, DataRow row)>();
            foreach (DataGridViewRow dgvRow in _dgvTerms.SelectedRows)
            {
                if (dgvRow.IsNewRow) continue;
                var rowView = dgvRow.DataBoundItem as DataRowView;
                if (rowView == null) continue;

                var id = rowView.Row["Id"] as long? ?? 0;
                if (id > 0)
                    toDelete.Add((id, rowView.Row));
            }

            if (toDelete.Count == 0) return;

            var msg = toDelete.Count == 1
                ? $"Delete this term?\n\nThis cannot be undone."
                : $"Delete {toDelete.Count} terms?\n\nThis cannot be undone.";

            var result = MessageBox.Show(msg,
                "TermLens \u2014 Delete Terms",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (result != DialogResult.Yes) return;

            _isLoading = true;
            try
            {
                foreach (var (id, row) in toDelete)
                {
                    try
                    {
                        TermbaseReader.DeleteTerm(_dbPath, id);
                        _dataTable.Rows.Remove(row);
                        TermLensEditorViewPart.NotifyTermDeleted(id);
                    }
                    catch
                    {
                        // Continue with other deletions
                    }
                }

                UpdateTermCountLabel();
            }
            finally
            {
                _isLoading = false;
            }
        }

        // ─── Double-click → open editor ────────────────────────────────

        private void OnCellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (_dgvTerms.Rows[e.RowIndex].IsNewRow) return;

            OpenTermEditor(e.RowIndex);
        }

        // ─── Context menu ─────────────────────────────────────────────

        private int _contextRowIndex = -1;
        private int _contextColIndex = -1;

        private void OnCellMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;
            if (_dgvTerms.Rows[e.RowIndex].IsNewRow) return;

            _contextRowIndex = e.RowIndex;
            _contextColIndex = e.ColumnIndex;

            // Select the right-clicked row
            _dgvTerms.ClearSelection();
            _dgvTerms.Rows[e.RowIndex].Selected = true;

            // Show context menu at cursor position
            var rect = _dgvTerms.GetCellDisplayRectangle(e.ColumnIndex, e.RowIndex, true);
            _rowContextMenu.Show(_dgvTerms, rect.Left + e.X, rect.Top + e.Y);
        }

        private void OnContextCopyCellClick(object sender, EventArgs e)
        {
            if (_contextRowIndex < 0 || _contextColIndex < 0) return;
            if (_contextRowIndex >= _dgvTerms.Rows.Count) return;

            var cell = _dgvTerms.Rows[_contextRowIndex].Cells[_contextColIndex];
            var value = cell.Value?.ToString() ?? "";
            if (!string.IsNullOrEmpty(value))
                Clipboard.SetText(value);
        }

        private void OnContextEditClick(object sender, EventArgs e)
        {
            if (_contextRowIndex < 0 || _contextRowIndex >= _bindingSource.Count) return;
            OpenTermEditor(_contextRowIndex);
        }

        /// <summary>
        /// Opens the TermEntryEditorDialog for an existing row and handles save/delete results.
        /// </summary>
        private void OpenTermEditor(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= _bindingSource.Count) return;

            var rowView = _bindingSource[rowIndex] as DataRowView;
            if (rowView == null) return;

            var row = rowView.Row;
            var id = row["Id"] as long? ?? 0;
            if (id <= 0) return;

            // Build a TermEntry from the row data for the editor
            var entry = new TermEntry
            {
                Id = id,
                SourceTerm = row["SourceTerm"] as string ?? "",
                TargetTerm = row["TargetTerm"] as string ?? "",
                Definition = row["Definition"] as string ?? "",
                Domain = row["Domain"] as string ?? "",
                Notes = row["Notes"] as string ?? "",
                IsNonTranslatable = row["NT"] as bool? ?? false,
                TermbaseId = _termbase.Id
            };

            using (var dlg = new TermEntryEditorDialog(entry, _dbPath, _termbase))
            {
                var result = dlg.ShowDialog(this);

                if (result == DialogResult.OK)
                {
                    // Term was saved — update the row
                    _isLoading = true;
                    try
                    {
                        row["SourceTerm"] = dlg.SourceTerm;
                        row["TargetTerm"] = dlg.TargetTerm;
                        row["Definition"] = dlg.Definition;
                        row["Domain"] = dlg.Domain;
                        row["Notes"] = dlg.Notes;
                        row["NT"] = dlg.IsNonTranslatable;

                        // Update synonym count
                        int synCount = dlg.SourceSynonymsList.Count + dlg.TargetSynonymsList.Count;
                        row["Synonyms"] = synCount > 0 ? $"{synCount} syn." : "";

                        // Refresh the in-memory term index so TermLens
                        // reflects the edit immediately
                        TermLensEditorViewPart.NotifyTermAdded();
                    }
                    finally
                    {
                        _isLoading = false;
                    }
                }
                else if (result == DialogResult.Abort)
                {
                    // Term was deleted from the editor
                    _isLoading = true;
                    try
                    {
                        _dataTable.Rows.Remove(row);
                        UpdateTermCountLabel();
                        TermLensEditorViewPart.NotifyTermDeleted(id);
                    }
                    finally
                    {
                        _isLoading = false;
                    }
                }
            }
        }

        private void OnContextDeleteClick(object sender, EventArgs e)
        {
            if (_contextRowIndex < 0 || _contextRowIndex >= _bindingSource.Count) return;

            var rowView = _bindingSource[_contextRowIndex] as DataRowView;
            if (rowView == null) return;

            var row = rowView.Row;
            var id = row["Id"] as long? ?? 0;
            if (id <= 0) return;

            var source = row["SourceTerm"] as string ?? "";
            var target = row["TargetTerm"] as string ?? "";

            var result = MessageBox.Show(
                $"Delete the term \u201c{source} \u2192 {target}\u201d?\n\nThis cannot be undone.",
                "TermLens \u2014 Delete Term",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (result != DialogResult.Yes) return;

            _isLoading = true;
            try
            {
                TermbaseReader.DeleteTerm(_dbPath, id);
                _dataTable.Rows.Remove(row);
                UpdateTermCountLabel();
                TermLensEditorViewPart.NotifyTermDeleted(id);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to delete term:\n{ex.Message}",
                    "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _isLoading = false;
            }
        }

        // ─── Merge ───────────────────────────────────────────────────

        private void OnMergeSelectedClick(object sender, EventArgs e)
        {
            if (_dgvTerms.SelectedRows.Count < 2)
            {
                MessageBox.Show("Select two or more rows to merge.",
                    "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Collect selected term data (skip the new row)
            var selected = new List<(long id, DataRow row, string source, string target)>();
            foreach (DataGridViewRow dgvRow in _dgvTerms.SelectedRows)
            {
                if (dgvRow.IsNewRow) continue;
                var rowView = dgvRow.DataBoundItem as DataRowView;
                if (rowView == null) continue;

                var id = rowView.Row["Id"] as long? ?? 0;
                if (id <= 0) continue;

                selected.Add((
                    id,
                    rowView.Row,
                    (rowView.Row["SourceTerm"] as string ?? "").Trim(),
                    (rowView.Row["TargetTerm"] as string ?? "").Trim()));
            }

            if (selected.Count < 2) return;

            // Validate: all selected entries must have the same source term
            var firstSource = selected[0].source;
            foreach (var item in selected)
            {
                if (!string.Equals(item.source, firstSource, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(
                        "All selected entries must have the same source term to merge.\n\n" +
                        $"Found: \u201c{firstSource}\u201d and \u201c{item.source}\u201d",
                        "TermLens \u2014 Merge",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            // Build confirmation message
            var targetList = new List<string>();
            foreach (var item in selected) targetList.Add(item.target);
            var targetsDisplay = string.Join(", ", targetList);

            var result = MessageBox.Show(
                $"Merge {selected.Count} entries for \u201c{firstSource}\u201d?\n\n" +
                $"The first selected entry\u2019s target term will remain primary.\n" +
                $"Other target terms ({targetsDisplay}) will become synonyms.\n\n" +
                "This cannot be undone.",
                "TermLens \u2014 Merge Entries",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);

            if (result != DialogResult.Yes) return;

            _isLoading = true;
            try
            {
                // First entry = primary, rest = merge into it
                var primaryId = selected[0].id;
                var mergeIds = new List<long>();
                for (int i = 1; i < selected.Count; i++)
                    mergeIds.Add(selected[i].id);

                TermbaseReader.MergeTerms(_dbPath, primaryId, mergeIds);

                // Remove merged rows from the DataTable (all except primary)
                for (int i = 1; i < selected.Count; i++)
                    _dataTable.Rows.Remove(selected[i].row);

                // Refresh synonym count for the primary row
                try
                {
                    var newCounts = TermbaseReader.GetSynonymCounts(_dbPath, _termbase.Id);
                    int synCount;
                    newCounts.TryGetValue(primaryId, out synCount);
                    selected[0].row["Synonyms"] = synCount > 0 ? $"{synCount} syn." : "";
                }
                catch { }

                UpdateTermCountLabel();

                // Refresh in-memory index (merge changes term structure)
                TermLensEditorViewPart.NotifyTermAdded();

                MessageBox.Show(
                    $"Merged {selected.Count} entries into one. " +
                    $"{mergeIds.Count} duplicate(s) removed, their target terms are now synonyms.",
                    "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to merge terms:\n{ex.Message}",
                    "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _isLoading = false;
            }
        }

        // ─── Error handling ───────────────────────────────────────────

        private void OnDataError(object sender, DataGridViewDataErrorEventArgs e)
        {
            // Suppress DataGridView data errors (e.g., type conversion during editing)
            e.ThrowException = false;
        }

        // ─── Form lifecycle ───────────────────────────────────────────

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Persist form size
            if (_settings != null)
            {
                _settings.TermbaseEditorWidth = Width;
                _settings.TermbaseEditorHeight = Height;
                _settings.Save();
            }

            base.OnFormClosing(e);
        }

        private void OnHelpButtonClicked(object sender, System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            HelpSystem.OpenHelp(HelpSystem.Topics.TermbaseEditor);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F1)
            {
                HelpSystem.OpenHelp(HelpSystem.Topics.TermbaseEditor);
                return true;
            }

            // Ctrl+C copies the current cell value (not the whole row)
            if (keyData == (Keys.Control | Keys.C) && !_dgvTerms.IsCurrentCellInEditMode)
            {
                CopyCurrentCell();
                return true;
            }

            // Delete key deletes selected rows (when not editing a cell)
            if (keyData == Keys.Delete && !_dgvTerms.IsCurrentCellInEditMode)
            {
                DeleteSelectedRows();
                return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void CopyCurrentCell()
        {
            var cell = _dgvTerms.CurrentCell;
            if (cell == null) return;

            var value = cell.Value?.ToString() ?? "";
            if (!string.IsNullOrEmpty(value))
                Clipboard.SetText(value);
            else
                Clipboard.Clear();
        }
    }
}
