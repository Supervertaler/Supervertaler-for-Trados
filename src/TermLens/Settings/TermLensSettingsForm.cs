using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using TermLens.Controls;
using TermLens.Core;
using TermLens.Models;

namespace TermLens.Settings
{
    /// <summary>
    /// Settings dialog for the TermLens plugin.
    /// Allows the user to select a Supervertaler termbase (.db) file,
    /// choose which termbases to search (Read) and which ones receive new terms (Write).
    /// </summary>
    public class TermLensSettingsForm : Form
    {
        private readonly TermLensSettings _settings;

        // Controls
        private TextBox _txtTermbasePath;
        private Button _btnBrowse;
        private Button _btnCreateNew;
        private Label _lblTermbaseInfo;
        private DataGridView _dgvTermbases;
        private Label _lblTermbasesHeader;
        private Button _btnAddGlossary;
        private Button _btnRemoveGlossary;
        private Button _btnImport;
        private Button _btnExport;
        private Button _btnOpenGlossary;
        private CheckBox _chkAutoLoad;
        private NumericUpDown _nudFontSize;
        private Button _btnOK;
        private Button _btnCancel;

        // Cached termbase list from the DB, aligned with DataGridView row indices
        private List<TermbaseInfo> _termbases = new List<TermbaseInfo>();

        public TermLensSettingsForm(TermLensSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            BuildUI();
            PopulateFromSettings();

            // Restore persisted form size
            if (_settings.SettingsFormWidth > 0 && _settings.SettingsFormHeight > 0)
                Size = new Size(_settings.SettingsFormWidth, _settings.SettingsFormHeight);
        }

        private void BuildUI()
        {
            Text = "TermLens Settings";
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 440);
            MinimumSize = new Size(480, 360);
            BackColor = Color.White;

            // === Termbase section ===
            var lblSection = new Label
            {
                Text = "Termbase",
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(50, 50, 50),
                Location = new Point(16, 16),
                AutoSize = true
            };

            var lblPath = new Label
            {
                Text = "Termbase file (.db):",
                Location = new Point(16, 42),
                AutoSize = true,
                ForeColor = Color.FromArgb(80, 80, 80)
            };

            _btnBrowse = new Button
            {
                Text = "Browse...",
                Width = 75,
                Height = 23,
                FlatStyle = FlatStyle.System,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnBrowse.Location = new Point(ClientSize.Width - 16 - _btnBrowse.Width, 58);
            _btnBrowse.Click += OnBrowseClick;

            _btnCreateNew = new Button
            {
                Text = "Create New...",
                Width = 120,
                Height = 23,
                FlatStyle = FlatStyle.System,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnCreateNew.Location = new Point(_btnBrowse.Left - 6 - _btnCreateNew.Width, 58);
            _btnCreateNew.Click += OnCreateNewClick;

            _txtTermbasePath = new TextBox
            {
                Location = new Point(16, 60),
                ReadOnly = true,
                BackColor = Color.FromArgb(250, 250, 250),
                ForeColor = Color.FromArgb(40, 40, 40),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _txtTermbasePath.Width = _btnCreateNew.Left - 16 - 6;

            _lblTermbaseInfo = new Label
            {
                Location = new Point(16, 86),
                AutoSize = false,
                Height = 32,
                ForeColor = Color.FromArgb(100, 100, 100),
                Font = new Font("Segoe UI", 8f),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _lblTermbaseInfo.Width = ClientSize.Width - 32;

            // === Glossary grid (Read / Write / Project columns) ===
            _lblTermbasesHeader = new Label
            {
                Text = "Glossaries:",
                Location = new Point(16, 122),
                AutoSize = true,
                ForeColor = Color.FromArgb(80, 80, 80)
            };

            // Glossary management buttons (right-aligned on the Glossaries row)
            _btnAddGlossary = new Button
            {
                Text = "+",
                Width = 26,
                Height = 26,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(80, 80, 80),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnAddGlossary.FlatAppearance.BorderSize = 0;
            _btnAddGlossary.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 220, 220);
            _btnAddGlossary.Location = new Point(ClientSize.Width - 16 - 26, 118);
            _btnAddGlossary.Click += OnAddGlossaryClick;

            _btnRemoveGlossary = new Button
            {
                Text = "\u2212",
                Width = 26,
                Height = 26,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(80, 80, 80),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnRemoveGlossary.FlatAppearance.BorderSize = 0;
            _btnRemoveGlossary.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 220, 220);
            _btnRemoveGlossary.Location = new Point(_btnAddGlossary.Left - 28, 118);
            _btnRemoveGlossary.Click += OnRemoveGlossaryClick;

            _btnImport = new Button
            {
                Text = "Import",
                Width = 65,
                Height = 26,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(80, 80, 80),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnImport.FlatAppearance.BorderSize = 0;
            _btnImport.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 220, 220);
            _btnImport.Location = new Point(_btnRemoveGlossary.Left - _btnImport.Width - 2, 118);
            _btnImport.Click += OnImportClick;

            _btnExport = new Button
            {
                Text = "Export",
                Width = 65,
                Height = 26,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(80, 80, 80),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnExport.FlatAppearance.BorderSize = 0;
            _btnExport.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 220, 220);
            _btnExport.Location = new Point(_btnImport.Left - _btnExport.Width - 2, 118);
            _btnExport.Click += OnExportClick;

            _btnOpenGlossary = new Button
            {
                Text = "Open",
                Width = 55,
                Height = 26,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(80, 80, 80),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnOpenGlossary.FlatAppearance.BorderSize = 0;
            _btnOpenGlossary.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 220, 220);
            _btnOpenGlossary.Location = new Point(_btnExport.Left - _btnOpenGlossary.Width - 2, 118);
            _btnOpenGlossary.Click += OnOpenGlossaryClick;

            _dgvTermbases = new DataGridView
            {
                Location = new Point(16, 144),
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                ReadOnly = false,
                BorderStyle = BorderStyle.FixedSingle,
                BackgroundColor = Color.FromArgb(250, 250, 250),
                Font = new Font("Segoe UI", 8.5f),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                EnableHeadersVisualStyles = false
            };
            _dgvTermbases.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(240, 240, 240),
                ForeColor = Color.FromArgb(50, 50, 50),
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                SelectionBackColor = Color.FromArgb(240, 240, 240),
                SelectionForeColor = Color.FromArgb(50, 50, 50)
            };
            _dgvTermbases.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(250, 250, 250),
                ForeColor = Color.FromArgb(40, 40, 40),
                SelectionBackColor = Color.FromArgb(220, 235, 252),
                SelectionForeColor = Color.FromArgb(40, 40, 40)
            };
            _dgvTermbases.Width = ClientSize.Width - 32;
            _dgvTermbases.Height = ClientSize.Height - 140 - 120;

            // Columns
            var colRead = new DataGridViewCheckBoxColumn
            {
                Name = "colRead",
                HeaderText = "Read",
                Width = 54,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                FillWeight = 1
            };
            var colWrite = new DataGridViewCheckBoxColumn
            {
                Name = "colWrite",
                HeaderText = "Write",
                Width = 54,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                FillWeight = 1
            };
            var colProject = new DataGridViewCheckBoxColumn
            {
                Name = "colProject",
                HeaderText = "Project",
                Width = 72,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                FillWeight = 1,
                ToolTipText = "Mark as project glossary (shown in pink)"
            };
            var colName = new DataGridViewTextBoxColumn
            {
                Name = "colName",
                HeaderText = "Termbase",
                ReadOnly = true,
                FillWeight = 40
            };
            var colTermCount = new DataGridViewTextBoxColumn
            {
                Name = "colTermCount",
                HeaderText = "Terms",
                ReadOnly = true,
                Width = 60,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                FillWeight = 1,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Alignment = DataGridViewContentAlignment.MiddleRight
                }
            };
            var colLanguages = new DataGridViewTextBoxColumn
            {
                Name = "colLanguages",
                HeaderText = "Languages",
                ReadOnly = true,
                FillWeight = 20
            };
            _dgvTermbases.Columns.AddRange(new DataGridViewColumn[]
            {
                colRead, colWrite, colProject, colName, colTermCount, colLanguages
            });

            // Enforce radio-button behaviour on the Project column (only one can be project)
            _dgvTermbases.CellContentClick += OnGridCellContentClick;

            // Double-click a glossary row to open the Glossary Editor
            _dgvTermbases.CellDoubleClick += OnGridCellDoubleClick;

            // === Options section ===
            var sep = new Label
            {
                Location = new Point(16, ClientSize.Height - 110),
                Height = 1,
                BorderStyle = BorderStyle.Fixed3D,
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom | AnchorStyles.Right
            };
            sep.Width = ClientSize.Width - 32;

            _chkAutoLoad = new CheckBox
            {
                Text = "Automatically load termbase when Trados Studio starts",
                Location = new Point(16, ClientSize.Height - 98),
                AutoSize = true,
                ForeColor = Color.FromArgb(60, 60, 60),
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom
            };

            var lblFontSize = new Label
            {
                Text = "Panel font size:",
                Location = new Point(16, ClientSize.Height - 70),
                AutoSize = true,
                ForeColor = Color.FromArgb(60, 60, 60),
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom
            };

            _nudFontSize = new NumericUpDown
            {
                Location = new Point(120, ClientSize.Height - 72),
                Width = 60,
                Minimum = 7,
                Maximum = 16,
                DecimalPlaces = 1,
                Increment = 0.5m,
                Value = (decimal)_settings.PanelFontSize,
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom
            };

            var lblFontPt = new Label
            {
                Text = "pt",
                Location = new Point(184, ClientSize.Height - 70),
                AutoSize = true,
                ForeColor = Color.FromArgb(100, 100, 100),
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom
            };

            // === OK / Cancel ===
            _btnOK = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Location = new Point(ClientSize.Width - 170, ClientSize.Height - 40),
                Width = 75,
                FlatStyle = FlatStyle.System,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            _btnOK.Click += OnOKClick;

            _btnCancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(ClientSize.Width - 88, ClientSize.Height - 40),
                Width = 75,
                FlatStyle = FlatStyle.System,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };

            AcceptButton = _btnOK;
            CancelButton = _btnCancel;

            Controls.AddRange(new Control[]
            {
                lblSection, lblPath, _txtTermbasePath, _btnCreateNew, _btnBrowse,
                _lblTermbaseInfo, _lblTermbasesHeader,
                _btnOpenGlossary, _btnExport, _btnImport, _btnRemoveGlossary, _btnAddGlossary,
                _dgvTermbases,
                sep, _chkAutoLoad, lblFontSize, _nudFontSize, lblFontPt,
                _btnOK, _btnCancel
            });
        }

        private void OnGridCellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.ColumnIndex < 0 || e.RowIndex < 0) return;

            var colName = _dgvTermbases.Columns[e.ColumnIndex].Name;

            // Radio-button enforcement for Project column only (only one can be project)
            // Write column allows multiple selections — terms are inserted into all write targets.
            if (colName == "colProject")
            {
                // Commit the edit so .Value is up-to-date
                _dgvTermbases.CommitEdit(DataGridViewDataErrorContexts.Commit);

                var clicked = _dgvTermbases.Rows[e.RowIndex].Cells[colName].Value as bool? ?? false;

                if (clicked)
                {
                    // Radio-button: uncheck all other rows in this column
                    foreach (DataGridViewRow row in _dgvTermbases.Rows)
                    {
                        if (row.Index != e.RowIndex)
                            row.Cells[colName].Value = false;
                    }
                }
            }
        }

        private void OnGridCellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;

            // Don't open editor when double-clicking checkbox columns
            var colName = _dgvTermbases.Columns[e.ColumnIndex].Name;
            if (colName == "colRead" || colName == "colWrite" || colName == "colProject")
                return;

            OpenGlossaryEditor(e.RowIndex);
        }

        private void OnOpenGlossaryClick(object sender, EventArgs e)
        {
            if (_dgvTermbases.SelectedRows.Count == 0)
            {
                MessageBox.Show("Select a glossary to open.",
                    "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            OpenGlossaryEditor(_dgvTermbases.SelectedRows[0].Index);
        }

        private void OpenGlossaryEditor(int rowIndex)
        {
            var dbPath = _txtTermbasePath.Text.Trim();
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
            {
                MessageBox.Show("Please select or create a termbase file first.",
                    "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (rowIndex < 0 || rowIndex >= _termbases.Count)
                return;

            var selected = _termbases[rowIndex];

            using (var editor = new GlossaryEditorDialog(dbPath, selected, _settings))
            {
                editor.ShowDialog(this);
            }

            // Refresh the list — term counts may have changed
            UpdateTermbaseInfo(dbPath);
            PopulateTermbaseList(dbPath);
        }

        private void PopulateFromSettings()
        {
            _txtTermbasePath.Text = _settings.TermbasePath ?? "";
            _chkAutoLoad.Checked = _settings.AutoLoadOnStartup;
            _nudFontSize.Value = Math.Max(_nudFontSize.Minimum, Math.Min(_nudFontSize.Maximum, (decimal)_settings.PanelFontSize));
            UpdateTermbaseInfo(_settings.TermbasePath);
            PopulateTermbaseList(_settings.TermbasePath);
        }

        private void OnBrowseClick(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Select Supervertaler Termbase";
                dlg.Filter = "Supervertaler Termbase (*.db)|*.db|All files (*.*)|*.*";
                dlg.FilterIndex = 1;

                var current = _txtTermbasePath.Text;
                if (!string.IsNullOrEmpty(current) && File.Exists(current))
                    dlg.InitialDirectory = Path.GetDirectoryName(current);

                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _txtTermbasePath.Text = dlg.FileName;
                    UpdateTermbaseInfo(dlg.FileName);
                    PopulateTermbaseList(dlg.FileName);
                }
            }
        }

        private void UpdateTermbaseInfo(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                _lblTermbaseInfo.Text = string.IsNullOrEmpty(path)
                    ? "No termbase selected."
                    : "File not found.";
                _lblTermbaseInfo.ForeColor = Color.FromArgb(160, 160, 160);
                return;
            }

            try
            {
                using (var reader = new TermbaseReader(path))
                {
                    if (!reader.Open())
                    {
                        _lblTermbaseInfo.Text = $"Could not open: {reader.LastError}";
                        _lblTermbaseInfo.ForeColor = Color.FromArgb(180, 60, 60);
                        return;
                    }

                    var termbases = reader.GetTermbases();
                    int total = 0;
                    foreach (var tb in termbases) total += tb.TermCount;

                    _lblTermbaseInfo.Text = termbases.Count == 1
                        ? $"\u2713  {termbases[0].Name}  \u2014  {total:N0} terms  ({termbases[0].SourceLang} \u2192 {termbases[0].TargetLang})"
                        : $"\u2713  {termbases.Count} termbases, {total:N0} terms total";

                    _lblTermbaseInfo.ForeColor = Color.FromArgb(30, 130, 60);
                }
            }
            catch
            {
                _lblTermbaseInfo.Text = "Error reading termbase.";
                _lblTermbaseInfo.ForeColor = Color.FromArgb(180, 60, 60);
            }
        }

        private void PopulateTermbaseList(string path)
        {
            _dgvTermbases.Rows.Clear();
            _termbases.Clear();

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;

            try
            {
                using (var reader = new TermbaseReader(path))
                {
                    if (!reader.Open())
                        return;

                    _termbases = reader.GetTermbases();
                    var disabled = new HashSet<long>(_settings.DisabledTermbaseIds ?? new List<long>());
                    var writeIds = new HashSet<long>(_settings.WriteTermbaseIds ?? new List<long>());

                    foreach (var tb in _termbases)
                    {
                        bool isRead = !disabled.Contains(tb.Id);
                        bool isWrite = writeIds.Contains(tb.Id);
                        bool isProject = tb.Id == _settings.ProjectTermbaseId;
                        _dgvTermbases.Rows.Add(
                            isRead,
                            isWrite,
                            isProject,
                            tb.Name,
                            tb.TermCount.ToString("N0"),
                            $"{tb.SourceLang} \u2192 {tb.TargetLang}");
                    }
                }
            }
            catch
            {
                // If we can't read the DB, just leave the grid empty
            }
        }

        private void OnCreateNewClick(object sender, EventArgs e)
        {
            using (var dlg = new SaveFileDialog())
            {
                dlg.Title = "Create New Termbase";
                dlg.Filter = "Supervertaler Termbase (*.db)|*.db";
                dlg.FileName = "supervertaler.db";

                var current = _txtTermbasePath.Text;
                if (!string.IsNullOrEmpty(current) && File.Exists(current))
                    dlg.InitialDirectory = Path.GetDirectoryName(current);

                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        TermbaseReader.CreateDatabase(dlg.FileName);
                        _txtTermbasePath.Text = dlg.FileName;
                        UpdateTermbaseInfo(dlg.FileName);
                        PopulateTermbaseList(dlg.FileName);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to create database:\n{ex.Message}",
                            "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void OnAddGlossaryClick(object sender, EventArgs e)
        {
            var dbPath = _txtTermbasePath.Text.Trim();
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
            {
                MessageBox.Show("Please select or create a termbase file first.",
                    "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new NewGlossaryDialog())
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        TermbaseReader.CreateTermbase(dbPath, dlg.GlossaryName,
                            dlg.SourceLang, dlg.TargetLang);
                        UpdateTermbaseInfo(dbPath);
                        PopulateTermbaseList(dbPath);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to create glossary:\n{ex.Message}",
                            "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void OnRemoveGlossaryClick(object sender, EventArgs e)
        {
            var dbPath = _txtTermbasePath.Text.Trim();
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
                return;

            if (_dgvTermbases.SelectedRows.Count == 0)
            {
                MessageBox.Show("Select a glossary first.",
                    "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int idx = _dgvTermbases.SelectedRows[0].Index;
            if (idx < 0 || idx >= _termbases.Count)
                return;

            var selected = _termbases[idx];
            var result = MessageBox.Show(
                $"Delete glossary \"{selected.Name}\" and all its {selected.TermCount:N0} terms?\n\nThis cannot be undone.",
                "TermLens \u2014 Delete Glossary",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

            if (result == DialogResult.Yes)
            {
                try
                {
                    TermbaseReader.DeleteTermbase(dbPath, selected.Id);

                    // Clear write/project references if the deleted glossary was selected
                    if (_settings.WriteTermbaseIds != null)
                        _settings.WriteTermbaseIds.Remove(selected.Id);
                    if (_settings.ProjectTermbaseId == selected.Id)
                        _settings.ProjectTermbaseId = -1;

                    UpdateTermbaseInfo(dbPath);
                    PopulateTermbaseList(dbPath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to delete glossary:\n{ex.Message}",
                        "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void OnImportClick(object sender, EventArgs e)
        {
            var dbPath = _txtTermbasePath.Text.Trim();
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
            {
                MessageBox.Show("Please select or create a termbase file first.",
                    "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (_dgvTermbases.SelectedRows.Count == 0)
            {
                MessageBox.Show("Select a glossary to import into.",
                    "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int idx = _dgvTermbases.SelectedRows[0].Index;
            if (idx < 0 || idx >= _termbases.Count)
                return;

            var selected = _termbases[idx];

            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = $"Import TSV into \"{selected.Name}\"";
                dlg.Filter = "Tab-separated files (*.tsv;*.txt)|*.tsv;*.txt|All files (*.*)|*.*";

                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    Cursor = Cursors.WaitCursor;
                    int count = TermbaseReader.ImportTsv(dbPath, selected.Id, dlg.FileName,
                        selected.SourceLang, selected.TargetLang);
                    Cursor = Cursors.Default;

                    MessageBox.Show($"Imported {count:N0} terms into \"{selected.Name}\".",
                        "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Information);

                    UpdateTermbaseInfo(dbPath);
                    PopulateTermbaseList(dbPath);
                }
                catch (Exception ex)
                {
                    Cursor = Cursors.Default;
                    MessageBox.Show($"Import failed:\n{ex.Message}",
                        "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void OnExportClick(object sender, EventArgs e)
        {
            var dbPath = _txtTermbasePath.Text.Trim();
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
            {
                MessageBox.Show("Please select or create a termbase file first.",
                    "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (_dgvTermbases.SelectedRows.Count == 0)
            {
                MessageBox.Show("Select a glossary to export.",
                    "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int idx = _dgvTermbases.SelectedRows[0].Index;
            if (idx < 0 || idx >= _termbases.Count)
                return;

            var selected = _termbases[idx];

            using (var dlg = new SaveFileDialog())
            {
                dlg.Title = $"Export \"{selected.Name}\" as TSV";
                dlg.Filter = "Tab-separated files (*.tsv)|*.tsv|All files (*.*)|*.*";
                dlg.FileName = $"{selected.Name}.tsv";

                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    Cursor = Cursors.WaitCursor;
                    int count = TermbaseReader.ExportTsv(dbPath, selected.Id, dlg.FileName);
                    Cursor = Cursors.Default;

                    MessageBox.Show($"Exported {count:N0} terms from \"{selected.Name}\".",
                        "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    Cursor = Cursors.Default;
                    MessageBox.Show($"Export failed:\n{ex.Message}",
                        "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void OnOKClick(object sender, EventArgs e)
        {
            _settings.TermbasePath = _txtTermbasePath.Text.Trim();
            _settings.AutoLoadOnStartup = _chkAutoLoad.Checked;
            _settings.PanelFontSize = (float)_nudFontSize.Value;

            // Build disabled list, write IDs, and project ID from grid cells
            _settings.DisabledTermbaseIds = new List<long>();
            _settings.WriteTermbaseIds = new List<long>();
            _settings.WriteTermbaseId = -1; // deprecated single-ID field
            _settings.ProjectTermbaseId = -1;

            for (int i = 0; i < _termbases.Count; i++)
            {
                var readChecked = _dgvTermbases.Rows[i].Cells["colRead"].Value as bool? ?? false;
                var writeChecked = _dgvTermbases.Rows[i].Cells["colWrite"].Value as bool? ?? false;
                var projectChecked = _dgvTermbases.Rows[i].Cells["colProject"].Value as bool? ?? false;

                if (!readChecked)
                    _settings.DisabledTermbaseIds.Add(_termbases[i].Id);
                if (writeChecked)
                    _settings.WriteTermbaseIds.Add(_termbases[i].Id);
                if (projectChecked)
                    _settings.ProjectTermbaseId = _termbases[i].Id;
            }

            _settings.Save();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Always persist form size (even on Cancel)
            _settings.SettingsFormWidth = Width;
            _settings.SettingsFormHeight = Height;
            _settings.Save();

            base.OnFormClosing(e);
        }
    }
}
