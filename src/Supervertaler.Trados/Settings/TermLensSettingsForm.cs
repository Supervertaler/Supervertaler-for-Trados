using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Supervertaler.Trados.Controls;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Licensing;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados.Settings
{
    /// <summary>
    /// Settings dialog for Supervertaler for Trados.
    /// Two tabs: TermLens (termbase settings) and AI Settings (provider/model/keys).
    /// </summary>
    public class TermLensSettingsForm : Form
    {
        private readonly TermLensSettings _settings;
        private readonly Core.PromptLibrary _promptLibrary;

        // Tab control
        private TabControl _tabControl;
        private AiSettingsPanel _aiSettingsPanel;
        private PromptManagerPanel _promptManagerPanel;

        // TermLens tab controls
        private TextBox _txtTermbasePath;
        private Button _btnBrowse;
        private Button _btnCreateNew;
        private Label _lblTermbaseInfo;
        private DataGridView _dgvTermbases;
        private Label _lblTermbasesHeader;
        private Button _btnAddTermbase;
        private Button _btnRemoveTermbase;
        private Button _btnImport;
        private Button _btnExport;
        private Button _btnOpenTermbase;
        private CheckBox _chkAutoLoad;
        private NumericUpDown _nudFontSize;
        private ComboBox _cboShortcutStyle;

        // Form buttons (outside tabs)
        private Button _btnOK;
        private Button _btnCancel;

        // Cached termbase list from the DB, aligned with DataGridView row indices
        private List<TermbaseInfo> _termbases = new List<TermbaseInfo>();

        // MultiTerm termbases from the active Trados project (rows after _termbases in the grid)
        private List<MultiTermTermbaseInfo> _multiTermInfos = new List<MultiTermTermbaseInfo>();

        public TermLensSettingsForm(TermLensSettings settings,
            Core.PromptLibrary promptLibrary = null, int defaultTab = 0)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _promptLibrary = promptLibrary ?? new Core.PromptLibrary();
            BuildUI();
            PopulateFromSettings();

            // Select the requested default tab
            if (defaultTab >= 0 && defaultTab < _tabControl.TabPages.Count)
                _tabControl.SelectedIndex = defaultTab;

            // Restore persisted form size (capped to reasonable bounds)
            if (_settings.SettingsFormWidth > 0 && _settings.SettingsFormHeight > 0)
            {
                var maxW = Screen.PrimaryScreen.WorkingArea.Width;
                var maxH = Screen.PrimaryScreen.WorkingArea.Height;
                var w = Math.Max(MinimumSize.Width, Math.Min(_settings.SettingsFormWidth, maxW));
                var h = Math.Max(MinimumSize.Height, Math.Min(_settings.SettingsFormHeight, maxH));
                Size = new Size(w, h);
            }
        }

        private void BuildUI()
        {
            Text = "Supervertaler Settings";
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            HelpButton = true;
            HelpButtonClicked += OnHelpButtonClicked;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 480);
            MinimumSize = new Size(480, 440);
            MaximumSize = new Size(Screen.PrimaryScreen.WorkingArea.Width, Screen.PrimaryScreen.WorkingArea.Height);
            BackColor = Color.White;

            // === OK / Cancel — anchored to bottom of form, outside tabs ===
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

            // === Tab Control ===
            _tabControl = new TabControl
            {
                Location = new Point(8, 8),
                Size = new Size(ClientSize.Width - 16, ClientSize.Height - 56),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                Font = new Font("Segoe UI", 8.5f),
                Padding = new Point(6, 3)
            };

            // --- TermLens tab ---
            var termLensPage = new TabPage("TermLens") { BackColor = Color.White };
            BuildTermLensTab(termLensPage);
            _tabControl.TabPages.Add(termLensPage);

            // --- AI Settings tab ---
            var aiPage = new TabPage("AI Settings") { BackColor = Color.White };
            _aiSettingsPanel = new AiSettingsPanel
            {
                Dock = DockStyle.Fill
            };
            aiPage.Controls.Add(_aiSettingsPanel);
            _tabControl.TabPages.Add(aiPage);

            // --- Prompts tab ---
            var promptsPage = new TabPage("Prompts") { BackColor = Color.White };
            _promptManagerPanel = new PromptManagerPanel
            {
                Dock = DockStyle.Fill
            };
            promptsPage.Controls.Add(_promptManagerPanel);
            _tabControl.TabPages.Add(promptsPage);

            // --- License tab ---
            var licensePage = new TabPage("License") { BackColor = Color.White };
            var licensePanel = new LicensePanel { Dock = DockStyle.Fill };
            licensePage.Controls.Add(licensePanel);
            _tabControl.TabPages.Add(licensePage);

            Controls.AddRange(new Control[] { _tabControl, _btnOK, _btnCancel });
        }

        /// <summary>
        /// Builds all TermLens controls inside the given TabPage.
        /// Layout is the same as the original flat form, but relative to the tab page.
        /// </summary>
        private void BuildTermLensTab(TabPage page)
        {
            // Reference width for initial control positioning; Dock handles actual sizing.
            var w = page.ClientSize.Width > 0 ? page.ClientSize.Width : 530;

            // Use Dock-based panels for robust layout across DPI scales and resolutions.
            // Top: termbase path, browse, info, termbase buttons (fixed height)
            // Bottom: separator, auto-load, font size (fixed height)
            // Middle: DataGridView fills remaining space
            var topPanel = new Panel { Dock = DockStyle.Top, Height = 138, Width = w, BackColor = Color.White };
            var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 96, BackColor = Color.White };
            var gridPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10, 0, 10, 0),
                BackColor = Color.White
            };

            // === Database section ===
            var lblSection = new Label
            {
                Text = "Database",
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(50, 50, 50),
                Location = new Point(10, 10),
                AutoSize = true
            };

            var lblPath = new Label
            {
                Text = "Database file (.db):",
                Location = new Point(10, 36),
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
            _btnBrowse.Location = new Point(w - 10 - _btnBrowse.Width, 52);
            _btnBrowse.Click += OnBrowseClick;

            _btnCreateNew = new Button
            {
                Text = "Create New...",
                Width = 120,
                Height = 23,
                FlatStyle = FlatStyle.System,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnCreateNew.Location = new Point(_btnBrowse.Left - 6 - _btnCreateNew.Width, 52);
            _btnCreateNew.Click += OnCreateNewClick;

            _txtTermbasePath = new TextBox
            {
                Location = new Point(10, 54),
                ReadOnly = true,
                BackColor = Color.FromArgb(250, 250, 250),
                ForeColor = Color.FromArgb(40, 40, 40),
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            _txtTermbasePath.Width = _btnCreateNew.Left - 10 - 10;

            // Keep TextBox width in sync with button positions when the panel resizes
            topPanel.Layout += (s, e) =>
            {
                _txtTermbasePath.Width = _btnCreateNew.Left - 10 - 10;
            };

            _lblTermbaseInfo = new Label
            {
                Location = new Point(10, 80),
                AutoSize = false,
                Height = 32,
                ForeColor = Color.FromArgb(100, 100, 100),
                Font = new Font("Segoe UI", 8f),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _lblTermbaseInfo.Width = w - 20;

            // === Termbase grid (Read / Write / Project columns) ===
            _lblTermbasesHeader = new Label
            {
                Text = "Termbases:",
                Location = new Point(10, 114),
                AutoSize = true,
                ForeColor = Color.FromArgb(80, 80, 80)
            };

            // Termbase management buttons (right-aligned on the Termbases row)
            _btnAddTermbase = new Button
            {
                Text = "+",
                Width = 26,
                Height = 26,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(80, 80, 80),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnAddTermbase.FlatAppearance.BorderSize = 0;
            _btnAddTermbase.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 220, 220);
            _btnAddTermbase.Location = new Point(w - 10 - 26, 110);
            _btnAddTermbase.Click += OnAddTermbaseClick;

            _btnRemoveTermbase = new Button
            {
                Text = "\u2212",
                Width = 26,
                Height = 26,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(80, 80, 80),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnRemoveTermbase.FlatAppearance.BorderSize = 0;
            _btnRemoveTermbase.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 220, 220);
            _btnRemoveTermbase.Location = new Point(_btnAddTermbase.Left - 28, 110);
            _btnRemoveTermbase.Click += OnRemoveTermbaseClick;

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
            _btnImport.Location = new Point(_btnRemoveTermbase.Left - _btnImport.Width - 2, 110);
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
            _btnExport.Location = new Point(_btnImport.Left - _btnExport.Width - 2, 110);
            _btnExport.Click += OnExportClick;

            _btnOpenTermbase = new Button
            {
                Text = "Open",
                Width = 55,
                Height = 26,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(80, 80, 80),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnOpenTermbase.FlatAppearance.BorderSize = 0;
            _btnOpenTermbase.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 220, 220);
            _btnOpenTermbase.Location = new Point(_btnExport.Left - _btnOpenTermbase.Width - 2, 110);
            _btnOpenTermbase.Click += OnOpenTermbaseClick;

            _dgvTermbases = new DataGridView
            {
                Dock = DockStyle.Fill,
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
            // Columns
            var colRead = new DataGridViewCheckBoxColumn
            {
                Name = "colRead",
                HeaderText = "Read",
                Width = 54,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                FillWeight = 1,
                ToolTipText = "Click header to select/deselect all"
            };
            var colWrite = new DataGridViewCheckBoxColumn
            {
                Name = "colWrite",
                HeaderText = "Write",
                Width = 54,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                FillWeight = 1,
                ToolTipText = "Click header to select/deselect all"
            };
            var colProject = new DataGridViewCheckBoxColumn
            {
                Name = "colProject",
                HeaderText = "Project",
                Width = 72,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                FillWeight = 1,
                ToolTipText = "Mark as project termbase (shown in pink). Click header to clear."
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

            // Click column header to select/deselect all checkboxes in that column
            _dgvTermbases.ColumnHeaderMouseClick += OnColumnHeaderMouseClick;

            // Double-click a termbase row to open the Termbase Editor
            _dgvTermbases.CellDoubleClick += OnGridCellDoubleClick;

            // === Options section (inside bottomPanel, positions relative to panel) ===
            var sep = new Label
            {
                Location = new Point(10, 0),
                Height = 1,
                BorderStyle = BorderStyle.Fixed3D,
                Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right
            };
            sep.Width = w - 20;

            _chkAutoLoad = new CheckBox
            {
                Text = "Automatically load database when Trados Studio starts",
                Location = new Point(10, 8),
                AutoSize = true,
                ForeColor = Color.FromArgb(60, 60, 60)
            };

            var lblFontSize = new Label
            {
                Text = "Panel font size:",
                Location = new Point(10, 36),
                AutoSize = true,
                ForeColor = Color.FromArgb(60, 60, 60)
            };

            _nudFontSize = new NumericUpDown
            {
                Location = new Point(114, 34),
                Width = 60,
                Minimum = 7,
                Maximum = 16,
                DecimalPlaces = 1,
                Increment = 0.5m,
                Value = (decimal)_settings.PanelFontSize
            };

            var lblFontPt = new Label
            {
                Text = "pt",
                Location = new Point(178, 36),
                AutoSize = true,
                ForeColor = Color.FromArgb(100, 100, 100)
            };

            var lblShortcutStyle = new Label
            {
                Text = "Term shortcuts:",
                Location = new Point(10, 64),
                AutoSize = true,
                ForeColor = Color.FromArgb(60, 60, 60)
            };

            _cboShortcutStyle = new ComboBox
            {
                Location = new Point(114, 62),
                Width = 210,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cboShortcutStyle.Items.Add("Sequential (Alt+4, Alt+5 = term 45)");
            _cboShortcutStyle.Items.Add("Repeated digit (Alt+5, Alt+5 = term 14)");
            _cboShortcutStyle.SelectedIndex = _settings.TermShortcutStyle == "repeated" ? 1 : 0;

            var tips = new ToolTip();
            tips.SetToolTip(_cboShortcutStyle,
                "Sequential: type the term number digit by digit (short delay).\n" +
                "Repeated digit: press the same key multiple times (no delay).");

            // Add controls to their respective panels
            topPanel.Controls.AddRange(new Control[]
            {
                lblSection, lblPath, _txtTermbasePath, _btnCreateNew, _btnBrowse,
                _lblTermbaseInfo, _lblTermbasesHeader,
                _btnOpenTermbase, _btnExport, _btnImport, _btnRemoveTermbase, _btnAddTermbase
            });

            bottomPanel.Controls.AddRange(new Control[]
            {
                sep, _chkAutoLoad, lblFontSize, _nudFontSize, lblFontPt,
                lblShortcutStyle, _cboShortcutStyle
            });

            gridPanel.Controls.Add(_dgvTermbases);

            // Add panels to page — order matters for Dock layout
            // (last added has highest z-order and docks first)
            page.Controls.Add(gridPanel);     // Fill — docks last, fills remaining space
            page.Controls.Add(bottomPanel);   // Bottom
            page.Controls.Add(topPanel);      // Top
        }

        private void OnGridCellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.ColumnIndex < 0 || e.RowIndex < 0) return;

            var colName = _dgvTermbases.Columns[e.ColumnIndex].Name;

            // Prevent toggling Write/Project for MultiTerm rows (read-only)
            if (e.RowIndex >= _termbases.Count && (colName == "colWrite" || colName == "colProject"))
            {
                _dgvTermbases.CommitEdit(DataGridViewDataErrorContexts.Commit);
                _dgvTermbases.Rows[e.RowIndex].Cells[colName].Value = false;
                return;
            }

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

        private void OnColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            var col = _dgvTermbases.Columns[e.ColumnIndex];
            if (col.Name != "colRead" && col.Name != "colWrite" && col.Name != "colProject")
                return;

            if (_dgvTermbases.Rows.Count == 0) return;

            // For Write/Project, skip MultiTerm rows (they're read-only)
            bool skipMultiTerm = col.Name == "colWrite" || col.Name == "colProject";

            if (col.Name == "colProject")
            {
                // Project is radio-button style — header click clears the selection
                foreach (DataGridViewRow row in _dgvTermbases.Rows)
                {
                    if (skipMultiTerm && row.Index >= _termbases.Count) continue;
                    row.Cells[col.Name].Value = false;
                }
            }
            else
            {
                // Toggle: if all are checked → uncheck all, otherwise check all
                bool allChecked = true;
                foreach (DataGridViewRow row in _dgvTermbases.Rows)
                {
                    if (skipMultiTerm && row.Index >= _termbases.Count) continue;
                    if (!(row.Cells[col.Name].Value as bool? ?? false))
                    {
                        allChecked = false;
                        break;
                    }
                }

                bool newValue = !allChecked;
                foreach (DataGridViewRow row in _dgvTermbases.Rows)
                {
                    if (skipMultiTerm && row.Index >= _termbases.Count) continue;
                    row.Cells[col.Name].Value = newValue;
                }
            }

            _dgvTermbases.RefreshEdit();
        }

        private void OnGridCellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;

            // Don't open editor for MultiTerm rows (read-only)
            if (e.RowIndex >= _termbases.Count) return;

            // Don't open editor when double-clicking checkbox columns
            var colName = _dgvTermbases.Columns[e.ColumnIndex].Name;
            if (colName == "colRead" || colName == "colWrite" || colName == "colProject")
                return;

            OpenTermbaseEditor(e.RowIndex);
        }

        private void OnOpenTermbaseClick(object sender, EventArgs e)
        {
            if (_dgvTermbases.SelectedRows.Count == 0)
            {
                MessageBox.Show("Select a termbase to open.",
                    "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            OpenTermbaseEditor(_dgvTermbases.SelectedRows[0].Index);
        }

        private void OpenTermbaseEditor(int rowIndex)
        {
            var dbPath = _txtTermbasePath.Text.Trim();
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
            {
                MessageBox.Show("Please select or create a database file first.",
                    "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (rowIndex < 0 || rowIndex >= _termbases.Count)
                return;

            var selected = _termbases[rowIndex];

            using (var editor = new TermbaseEditorDialog(dbPath, selected, _settings))
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

            // AI settings
            _aiSettingsPanel.PopulateFromSettings(_settings.AiSettings);
            _aiSettingsPanel.SetAvailableTermbases(_termbases,
                _settings.AiSettings?.DisabledAiTermbaseIds);

            // Prompts
            _promptManagerPanel.PopulateFromSettings(_settings.AiSettings, _promptLibrary);
        }

        private void OnBrowseClick(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Select Supervertaler database";
                dlg.Filter = "Supervertaler database (*.db)|*.db|All files (*.*)|*.*";
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
                    ? "No database selected."
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
                        ? $"\u2713  {termbases[0].Name}  \u2014  {total:N0} terms  ({LanguageUtils.ShortenLanguageName(termbases[0].SourceLang)} \u2192 {LanguageUtils.ShortenLanguageName(termbases[0].TargetLang)})"
                        : $"\u2713  {termbases.Count} termbases, {total:N0} terms total";

                    _lblTermbaseInfo.ForeColor = Color.FromArgb(30, 130, 60);
                }
            }
            catch
            {
                _lblTermbaseInfo.Text = "Error reading database.";
                _lblTermbaseInfo.ForeColor = Color.FromArgb(180, 60, 60);
            }
        }

        private void PopulateTermbaseList(string path)
        {
            _dgvTermbases.Rows.Clear();
            _termbases.Clear();
            _multiTermInfos.Clear();

            // Load Supervertaler termbases from the .db file
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    using (var reader = new TermbaseReader(path))
                    {
                        if (reader.Open())
                        {
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
                                    $"{LanguageUtils.ShortenLanguageName(tb.SourceLang)} \u2192 {LanguageUtils.ShortenLanguageName(tb.TargetLang)}");
                            }
                        }
                    }
                }
                catch
                {
                    // If we can't read the DB, just leave the Supervertaler rows empty
                }
            }

            // Add MultiTerm termbases from the active Trados project
            var mtInfos = TermLensEditorViewPart.GetMultiTermInfos();
            if (mtInfos != null)
                _multiTermInfos = mtInfos;

            if (_multiTermInfos.Count > 0)
            {
                var disabledMtIds = new HashSet<long>(_settings.DisabledMultiTermIds ?? new List<long>());

                foreach (var info in _multiTermInfos)
                {
                    bool isRead = !disabledMtIds.Contains(info.SyntheticId);
                    var langText = !string.IsNullOrEmpty(info.SourceIndexName)
                        && !string.IsNullOrEmpty(info.TargetIndexName)
                        ? $"{info.SourceIndexName} \u2192 {info.TargetIndexName}"
                        : info.LoadMode == MultiTermLoadMode.Failed ? "Failed to load" : "";

                    int rowIdx = _dgvTermbases.Rows.Add(
                        isRead,     // Read
                        false,      // Write (always disabled for MultiTerm)
                        false,      // Project (always disabled for MultiTerm)
                        $"{info.Name} [MultiTerm]",
                        info.TermCount.ToString("N0"),
                        langText);

                    // Style MultiTerm rows with a light green tint
                    var row = _dgvTermbases.Rows[rowIdx];
                    row.Cells["colWrite"].ReadOnly = true;
                    row.Cells["colProject"].ReadOnly = true;
                    row.DefaultCellStyle.BackColor = Color.FromArgb(232, 245, 233);
                    row.DefaultCellStyle.SelectionBackColor = Color.FromArgb(200, 230, 201);
                }
            }
        }

        private void OnCreateNewClick(object sender, EventArgs e)
        {
            using (var dlg = new SaveFileDialog())
            {
                dlg.Title = "Create new database";
                dlg.Filter = "Supervertaler database (*.db)|*.db";
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
                        // Show full exception chain for diagnostics
                        var msg = ex.Message;
                        if (ex.InnerException != null)
                            msg += "\n\nInner: " + ex.InnerException.Message;
                        if (ex.InnerException?.InnerException != null)
                            msg += "\n\nRoot: " + ex.InnerException.InnerException.Message;
                        MessageBox.Show($"Failed to create database:\n{msg}",
                            "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void OnAddTermbaseClick(object sender, EventArgs e)
        {
            var dbPath = _txtTermbasePath.Text.Trim();
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
            {
                MessageBox.Show("Please select or create a database file first.",
                    "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new NewTermbaseDialog())
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        TermbaseReader.CreateTermbase(dbPath, dlg.TermbaseName,
                            dlg.SourceLang, dlg.TargetLang);
                        UpdateTermbaseInfo(dbPath);
                        PopulateTermbaseList(dbPath);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to create termbase:\n{ex.Message}",
                            "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void OnRemoveTermbaseClick(object sender, EventArgs e)
        {
            var dbPath = _txtTermbasePath.Text.Trim();
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
                return;

            if (_dgvTermbases.SelectedRows.Count == 0)
            {
                MessageBox.Show("Select a termbase first.",
                    "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int idx = _dgvTermbases.SelectedRows[0].Index;
            if (idx < 0 || idx >= _termbases.Count)
                return;

            var selected = _termbases[idx];
            var result = MessageBox.Show(
                $"Delete termbase \"{selected.Name}\" and all its {selected.TermCount:N0} terms?\n\nThis cannot be undone.",
                "TermLens \u2014 Delete Termbase",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

            if (result == DialogResult.Yes)
            {
                try
                {
                    TermbaseReader.DeleteTermbase(dbPath, selected.Id);

                    // Clear write/project references if the deleted termbase was selected
                    if (_settings.WriteTermbaseIds != null)
                        _settings.WriteTermbaseIds.Remove(selected.Id);
                    if (_settings.ProjectTermbaseId == selected.Id)
                        _settings.ProjectTermbaseId = -1;

                    UpdateTermbaseInfo(dbPath);
                    PopulateTermbaseList(dbPath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to delete termbase:\n{ex.Message}",
                        "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void OnImportClick(object sender, EventArgs e)
        {
            var dbPath = _txtTermbasePath.Text.Trim();
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
            {
                MessageBox.Show("Please select or create a database file first.",
                    "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (_dgvTermbases.SelectedRows.Count == 0)
            {
                MessageBox.Show("Select a termbase to import into.",
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
                MessageBox.Show("Please select or create a database file first.",
                    "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (_dgvTermbases.SelectedRows.Count == 0)
            {
                MessageBox.Show("Select a termbase to export.",
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
            // TermLens settings
            _settings.TermbasePath = _txtTermbasePath.Text.Trim();
            _settings.AutoLoadOnStartup = _chkAutoLoad.Checked;
            _settings.PanelFontSize = (float)_nudFontSize.Value;
            _settings.TermShortcutStyle = _cboShortcutStyle.SelectedIndex == 1 ? "repeated" : "sequential";

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

            // Save MultiTerm disabled IDs
            _settings.DisabledMultiTermIds = new List<long>();
            for (int i = 0; i < _multiTermInfos.Count; i++)
            {
                int rowIdx = _termbases.Count + i;
                if (rowIdx < _dgvTermbases.Rows.Count)
                {
                    var readChecked = _dgvTermbases.Rows[rowIdx].Cells["colRead"].Value as bool? ?? false;
                    if (!readChecked)
                        _settings.DisabledMultiTermIds.Add(_multiTermInfos[i].SyntheticId);
                }
            }

            // AI settings
            _aiSettingsPanel.ApplyToSettings(_settings.AiSettings);

            // Prompts
            _promptManagerPanel.ApplyToSettings(_settings.AiSettings);

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

        private string GetCurrentHelpTopic()
        {
            switch (_tabControl?.SelectedIndex)
            {
                case 0:  return HelpSystem.Topics.SettingsTermLens;
                case 1:  return HelpSystem.Topics.SettingsAi;
                case 2:  return HelpSystem.Topics.SettingsPrompts;
                default: return HelpSystem.Topics.SettingsTermLens;
            }
        }

        private void OnHelpButtonClicked(object sender, System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true; // prevent the "What's This?" cursor
            HelpSystem.OpenHelp(GetCurrentHelpTopic());
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F1)
            {
                HelpSystem.OpenHelp(GetCurrentHelpTopic());
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
