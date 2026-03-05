using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Windows.Forms;
using TermLens.Core;
using TermLens.Models;
using TermLens.Settings;

namespace TermLens.Controls
{
    /// <summary>
    /// Dialog for browsing, editing, adding, and deleting terms in a single glossary.
    /// Opened from the Settings dialog by clicking "Open" or double-clicking a glossary row.
    /// </summary>
    public class GlossaryEditorDialog : Form
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
        private Button _btnClose;
        private ContextMenuStrip _rowContextMenu;

        // Track whether we're loading data (to suppress CellValueChanged during population)
        private bool _isLoading;

        public GlossaryEditorDialog(string dbPath, TermbaseInfo termbase, TermLensSettings settings)
        {
            _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
            _termbase = termbase ?? throw new ArgumentNullException(nameof(termbase));
            _settings = settings;

            BuildUI();
            LoadTerms();

            // Restore persisted form size
            if (_settings != null && _settings.GlossaryEditorWidth > 0 && _settings.GlossaryEditorHeight > 0)
                Size = new Size(_settings.GlossaryEditorWidth, _settings.GlossaryEditorHeight);
        }

        private void BuildUI()
        {
            Text = $"Glossary Editor \u2014 {_termbase.Name} ({_termbase.SourceLang} \u2192 {_termbase.TargetLang})";
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
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
            _btnDelete.Location = new Point(ClientSize.Width - 16 - _btnDelete.Width, 7);
            _btnDelete.Click += OnDeleteSelectedClick;
            toolbarPanel.Controls.Add(_btnDelete);

            _lblTermCount = new Label
            {
                AutoSize = true,
                ForeColor = Color.FromArgb(120, 120, 120),
                Font = new Font("Segoe UI", 8f),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _lblTermCount.Location = new Point(_btnDelete.Left - 120, 10);
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

            // Row context menu
            _rowContextMenu = new ContextMenuStrip();

            var editItem = new ToolStripMenuItem("Edit Term\u2026");
            editItem.Click += OnContextEditClick;
            _rowContextMenu.Items.Add(editItem);

            var deleteItem = new ToolStripMenuItem("Delete Term");
            deleteItem.Click += OnContextDeleteClick;
            _rowContextMenu.Items.Add(deleteItem);

            _dgvTerms.CellMouseClick += OnCellMouseClick;

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

                _dataTable = new DataTable();
                _dataTable.Columns.Add("Id", typeof(long));
                _dataTable.Columns.Add("SourceTerm", typeof(string));
                _dataTable.Columns.Add("TargetTerm", typeof(string));
                _dataTable.Columns.Add("Definition", typeof(string));
                _dataTable.Columns.Add("Domain", typeof(string));
                _dataTable.Columns.Add("Notes", typeof(string));

                foreach (var term in terms)
                {
                    _dataTable.Rows.Add(
                        term.Id,
                        term.SourceTerm ?? "",
                        term.TargetTerm ?? "",
                        term.Definition ?? "",
                        term.Domain ?? "",
                        term.Notes ?? "");
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
                    _dgvTerms.Columns["SourceTerm"].HeaderText = "Source Term";
                    _dgvTerms.Columns["SourceTerm"].FillWeight = 30;
                }
                if (_dgvTerms.Columns.Contains("TargetTerm"))
                {
                    _dgvTerms.Columns["TargetTerm"].HeaderText = "Target Term";
                    _dgvTerms.Columns["TargetTerm"].FillWeight = 30;
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
            if (_bindingSource == null) return;

            var text = _txtSearch.Text.Trim();
            if (string.IsNullOrEmpty(text))
            {
                _bindingSource.RemoveFilter();
            }
            else
            {
                // Escape single quotes for DataTable filter expression
                var escaped = text.Replace("'", "''");
                _bindingSource.Filter =
                    $"SourceTerm LIKE '%{escaped}%' OR TargetTerm LIKE '%{escaped}%' " +
                    $"OR Definition LIKE '%{escaped}%'";
            }

            UpdateTermCountLabel();
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
                TermbaseReader.UpdateTerm(_dbPath, id,
                    source, target,
                    row["Definition"] as string ?? "",
                    row["Domain"] as string ?? "",
                    row["Notes"] as string ?? "");
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

        // ─── Context menu ─────────────────────────────────────────────

        private int _contextRowIndex = -1;

        private void OnCellMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;
            if (_dgvTerms.Rows[e.RowIndex].IsNewRow) return;

            _contextRowIndex = e.RowIndex;

            // Select the right-clicked row
            _dgvTerms.ClearSelection();
            _dgvTerms.Rows[e.RowIndex].Selected = true;

            // Show context menu at cursor position
            var rect = _dgvTerms.GetCellDisplayRectangle(e.ColumnIndex, e.RowIndex, true);
            _rowContextMenu.Show(_dgvTerms, rect.Left + e.X, rect.Top + e.Y);
        }

        private void OnContextEditClick(object sender, EventArgs e)
        {
            if (_contextRowIndex < 0 || _contextRowIndex >= _bindingSource.Count) return;

            var rowView = _bindingSource[_contextRowIndex] as DataRowView;
            if (rowView == null) return;

            var row = rowView.Row;
            var id = row["Id"] as long? ?? 0;
            if (id <= 0) return;

            // Build a TermEntry from the row data for the edit dialog
            var entry = new TermEntry
            {
                Id = id,
                SourceTerm = row["SourceTerm"] as string ?? "",
                TargetTerm = row["TargetTerm"] as string ?? "",
                Definition = row["Definition"] as string ?? "",
                Domain = row["Domain"] as string ?? "",
                Notes = row["Notes"] as string ?? "",
                TermbaseId = _termbase.Id
            };

            using (var dlg = new AddTermDialog(entry, _termbase))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _isLoading = true;
                    try
                    {
                        TermbaseReader.UpdateTerm(_dbPath, id,
                            dlg.SourceTerm, dlg.TargetTerm, dlg.Definition,
                            entry.Domain, entry.Notes);

                        // Update the DataTable row
                        row["SourceTerm"] = dlg.SourceTerm;
                        row["TargetTerm"] = dlg.TargetTerm;
                        row["Definition"] = dlg.Definition;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to update term:\n{ex.Message}",
                            "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                _settings.GlossaryEditorWidth = Width;
                _settings.GlossaryEditorHeight = Height;
                _settings.Save();
            }

            base.OnFormClosing(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // Delete key deletes selected rows (when not editing a cell)
            if (keyData == Keys.Delete && !_dgvTerms.IsCurrentCellInEditMode)
            {
                DeleteSelectedRows();
                return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
