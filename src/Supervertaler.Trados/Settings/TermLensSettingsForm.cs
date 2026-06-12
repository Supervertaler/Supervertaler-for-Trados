using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
        /// <summary>
        /// Raised when the user requests "Distill to SuperMemory" for a termbase.
        /// The event args carry the termbase name and formatted term text.
        /// </summary>
        public event EventHandler<DistillTermbaseEventArgs> DistillTermbaseRequested;

        /// <summary>
        /// Bubbled from <see cref="PromptManagerPanel.ActivePromptChanged"/>. Fires
        /// while the dialog is open so subscribers (e.g. the Batch Translate panel)
        /// can live-refresh. The payload is the new active prompt's relative path,
        /// or empty if the active prompt was cleared.
        /// </summary>
        public event EventHandler<string> ActivePromptChanged;

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
        private CheckBox _chkCaseSensitive;
        private CheckBox _chkUsageStats;
        private CheckBox _chkSuperSearchInTab;
        private NumericUpDown _nudFontSize;
        private ComboBox _cboUiScale;
        private ComboBox _cboShortcutStyle;
        private ComboBox _cboSuffixTolerant;
        private NumericUpDown _nudChordDelay;

        // Form buttons (outside tabs)
        private Button _btnOK;
        private Button _btnCancel;

        /// <summary>
        /// True if the user imported settings from a file.
        /// The caller should reload settings from disk when this is set.
        /// </summary>
        public bool SettingsImported { get; private set; }

        // Cached termbase list from the DB, aligned with DataGridView row indices
        private List<TermbaseInfo> _termbases = new List<TermbaseInfo>();

        // MultiTerm termbases from the active Trados project (rows after _termbases in the grid)
        private List<MultiTermTermbaseInfo> _multiTermInfos = new List<MultiTermTermbaseInfo>();

        // Active project source language captured at form open. Null when no
        // project is loaded – in that case the language-mismatch warning is
        // suppressed because we have nothing to compare against.
        private string _projectSourceLanguage;

        public TermLensSettingsForm(TermLensSettings settings,
            Core.PromptLibrary promptLibrary = null, int defaultTab = 0)
        {
            Icon = Supervertaler.Trados.Core.IconHelper.AppIcon;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _promptLibrary = promptLibrary ?? new Core.PromptLibrary();
            _projectSourceLanguage = TermLensEditorViewPart.GetCurrentProjectSourceLanguage();
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
            // Let WinForms scale this dialog by system DPI so it doesn't squish
            // at >100% Windows display scaling. Cheap fallback; for surfaces
            // with their own UiScale-driven layout, set AutoScaleMode = None
            // instead and let UiScale own scaling.
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Supervertaler Settings";
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            HelpButton = true;
            HelpButtonClicked += OnHelpButtonClicked;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 480);
            MinimumSize = new Size(480, 460);
            MaximumSize = new Size(Screen.PrimaryScreen.WorkingArea.Width, Screen.PrimaryScreen.WorkingArea.Height);
            BackColor = Color.White;

            // === OK / Cancel – anchored to bottom of form, outside tabs ===
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

            // --- General tab ---
            var generalPage = new TabPage("General") { BackColor = Color.White };
            BuildGeneralTab(generalPage);
            _tabControl.TabPages.Add(generalPage);

            // --- Termbases tab ---
            var termLensPage = new TabPage("Termbases") { BackColor = Color.White };
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
            _promptManagerPanel.ActivePromptChanged += (s, newPath) =>
                ActivePromptChanged?.Invoke(this, newPath);
            promptsPage.Controls.Add(_promptManagerPanel);
            _tabControl.TabPages.Add(promptsPage);

            // --- Licence tab ---
            var licensePage = new TabPage("Licence") { BackColor = Color.White };
            var licensePanel = new LicensePanel { Dock = DockStyle.Fill };
            licensePage.Controls.Add(licensePanel);
            _tabControl.TabPages.Add(licensePage);

            // --- Backup tab ---
            var backupPage = new TabPage("Backup") { BackColor = Color.White };
            BuildBackupTab(backupPage);
            _tabControl.TabPages.Add(backupPage);

            Controls.AddRange(new Control[] { _tabControl, _btnOK, _btnCancel });
        }

        /// <summary>
        /// Builds the General tab – plugin-wide settings that are not specific to TermLens or AI.
        /// </summary>
        private void BuildGeneralTab(TabPage page)
        {
            // ─── Appearance section ─────────────────────────────────
            var lblAppearance = new Label
            {
                Text = "Appearance",
                Location = new Point(10, 12),
                AutoSize = true,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(50, 50, 50)
            };

            var lblUiScale = new Label
            {
                Text = "UI scale:",
                Location = new Point(10, 40),
                AutoSize = true,
                ForeColor = Color.FromArgb(60, 60, 60)
            };

            _cboUiScale = new ComboBox
            {
                Location = new Point(80, 37),
                Width = 70,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            // 70-90% lets users dial the plugin smaller than Windows' display
            // scaling on hi-DPI machines where the global scaling is too
            // aggressive but they don't want to change Windows-wide settings.
            // Below 70% the system-rendered control elements (NUD spinners,
            // checkbox boxes) become disproportionately large vs the text.
            _cboUiScale.Items.AddRange(new object[] { "70%", "80%", "90%", "100%", "110%", "125%", "150%" });
            var scalePercent = (int)Math.Round(_settings.UiScaleFactor * 100);
            var scaleText = scalePercent + "%";
            var idx = _cboUiScale.Items.IndexOf(scaleText);
            _cboUiScale.SelectedIndex = idx >= 0 ? idx : 0;

            var lblScaleNote = new Label
            {
                Text = "(restart required)",
                Location = new Point(156, 40),
                AutoSize = true,
                ForeColor = Color.FromArgb(140, 140, 140),
                Font = new Font("Segoe UI", 7.5f, FontStyle.Italic)
            };

            // ─── Privacy section ────────────────────────────────────
            var sepPrivacy = new Label
            {
                Location = new Point(10, 72),
                Height = 1,
                Width = 500,
                BorderStyle = BorderStyle.Fixed3D,
                Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right
            };

            var lblPrivacy = new Label
            {
                Text = "Privacy",
                Location = new Point(10, 82),
                AutoSize = true,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(50, 50, 50)
            };

            _chkUsageStats = new CheckBox
            {
                Text = "Share anonymous usage statistics (no personal data)",
                Location = new Point(10, 108),
                AutoSize = true,
                ForeColor = Color.FromArgb(60, 60, 60)
            };

            var tips = new ToolTip();
            tips.SetToolTip(_chkUsageStats,
                "Sends a single anonymous ping on startup (plugin version, OS, Trados version, locale).\n" +
                "No personal data, translation content, or termbase info is ever collected.");

            // ─── Panels section ─────────────────────────────────────
            var sepPanels = new Label
            {
                Location = new Point(10, 140),
                Height = 1,
                Width = 500,
                BorderStyle = BorderStyle.Fixed3D,
                Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right
            };

            var lblPanels = new Label
            {
                Text = "Panels",
                Location = new Point(10, 150),
                AutoSize = true,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(50, 50, 50)
            };

            _chkSuperSearchInTab = new CheckBox
            {
                Text = "Show SuperSearch as a tab in the Supervertaler Assistant panel",
                Location = new Point(10, 176),
                AutoSize = true,
                ForeColor = Color.FromArgb(60, 60, 60)
            };

            var lblSuperSearchNote = new Label
            {
                Text = "(restart required)",
                Location = new Point(10, 198),
                AutoSize = true,
                ForeColor = Color.FromArgb(140, 140, 140),
                Font = new Font("Segoe UI", 7.5f, FontStyle.Italic)
            };

            tips.SetToolTip(_chkSuperSearchInTab,
                "When on, SuperSearch is hosted as a 4th tab inside the Supervertaler Assistant\n" +
                "panel instead of its own dockable panel. Requires a Trados restart, and an\n" +
                "Assistant licence (without one, SuperSearch stays in its own panel).");

            page.Controls.AddRange(new Control[]
            {
                lblAppearance, lblUiScale, _cboUiScale, lblScaleNote,
                sepPrivacy, lblPrivacy, _chkUsageStats,
                sepPanels, lblPanels, _chkSuperSearchInTab, lblSuperSearchNote
            });
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
            var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 175, BackColor = Color.White };
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

            // Termbase management buttons (right-aligned on the Termbases row).
            // All five use AutoSize + MinimumSize so labels never clip at high
            // DPI \u2013 at 150% Windows scaling the previous fixed widths cropped
            // "Open" -> "Ope" and dropped the "Remove" / "Add" text entirely,
            // leaving just the +/\u2212 symbol. The chain positions each button to
            // the LEFT of the previous one using its measured Width, so the
            // row still right-aligns correctly regardless of label length.
            Button MakeFlatButton(string text, int minWidth)
            {
                var b = new Button
                {
                    Text = text,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    MinimumSize = new Size(minWidth, 26),
                    Padding = new Padding(8, 0, 8, 0),
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 8f),
                    ForeColor = Color.FromArgb(80, 80, 80),
                    Anchor = AnchorStyles.Top | AnchorStyles.Right
                };
                b.FlatAppearance.BorderSize = 0;
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 220, 220);
                return b;
            }

            _btnAddTermbase = MakeFlatButton("+ Add", 50);
            _btnAddTermbase.Location = new Point(w - 10 - _btnAddTermbase.PreferredSize.Width, 110);
            _btnAddTermbase.Click += OnAddTermbaseClick;

            _btnRemoveTermbase = MakeFlatButton("\u2212 Remove", 68);
            _btnRemoveTermbase.Location = new Point(_btnAddTermbase.Left - _btnRemoveTermbase.PreferredSize.Width - 2, 110);
            _btnRemoveTermbase.Click += OnRemoveTermbaseClick;

            _btnImport = MakeFlatButton("Import", 65);
            _btnImport.Location = new Point(_btnRemoveTermbase.Left - _btnImport.PreferredSize.Width - 2, 110);
            _btnImport.Click += OnImportClick;

            _btnExport = MakeFlatButton("Export", 65);
            _btnExport.Location = new Point(_btnImport.Left - _btnExport.PreferredSize.Width - 2, 110);
            _btnExport.Click += OnExportClick;

            _btnOpenTermbase = MakeFlatButton("Open", 55);
            _btnOpenTermbase.Location = new Point(_btnExport.Left - _btnOpenTermbase.PreferredSize.Width - 2, 110);
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
            // DataGridView column widths are fixed pixels and do NOT participate
            // in AutoScaleMode.Dpi scaling (the grid scales row heights and
            // fonts but not explicit column widths). At 150% Windows display
            // scaling the bold "Write" / "Read" / "Terms" headers in their
            // 54/60-px columns clipped to "Wr..." / "Ter..." – widths bumped
            // here to give comfortable headroom at any DPI.
            var colRead = new DataGridViewCheckBoxColumn
            {
                Name = "colRead",
                HeaderText = "Read",
                Width = 80,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                FillWeight = 1,
                ToolTipText = "Click header to select/deselect all"
            };
            var colWrite = new DataGridViewCheckBoxColumn
            {
                Name = "colWrite",
                HeaderText = "Write",
                Width = 80,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                FillWeight = 1,
                ToolTipText = "Click header to select/deselect all"
            };
            var colProject = new DataGridViewCheckBoxColumn
            {
                Name = "colProject",
                HeaderText = "Project",
                Width = 90,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                FillWeight = 1,
                ToolTipText = "Mark as project termbase (shown in pink). Click header to clear."
            };
            var colCS = new DataGridViewCheckBoxColumn
            {
                Name = "colCS",
                HeaderText = "CS",
                Width = 56,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                FillWeight = 1,
                ToolTipText = "Case-sensitive matching for this termbase."
            };
            var colAi = new DataGridViewCheckBoxColumn
            {
                Name = "colAi",
                HeaderText = "AI",
                Width = 56,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                FillWeight = 1,
                ToolTipText = "Send this termbase's terms to the AI (Chat, AutoPrompt, batch operations). Click header to select/deselect all."
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
                Width = 80,
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
                colRead, colWrite, colProject, colCS, colAi, colName, colTermCount, colLanguages
            });

            // Enforce radio-button behaviour on the Project column (only one can be project)
            _dgvTermbases.CellContentClick += OnGridCellContentClick;

            // Click column header to select/deselect all checkboxes in that column
            _dgvTermbases.ColumnHeaderMouseClick += OnColumnHeaderMouseClick;

            // Double-click a termbase row to open the Termbase Editor
            _dgvTermbases.CellDoubleClick += OnGridCellDoubleClick;

            // Right-click context menu for termbase rows
            var termbaseContextMenu = new ContextMenuStrip();
            var renameMenuItem = new ToolStripMenuItem("Rename");
            renameMenuItem.ShortcutKeyDisplayString = "F2";
            renameMenuItem.Click += (s, ev) =>
            {
                if (_dgvTermbases.SelectedRows.Count > 0)
                {
                    var rowIndex = _dgvTermbases.SelectedRows[0].Index;
                    if (rowIndex >= 0 && rowIndex < _termbases.Count)
                        RenameTermbase(rowIndex);
                }
            };
            var openMenuItem = new ToolStripMenuItem("Open in Editor");
            openMenuItem.Click += (s, ev) => OnOpenTermbaseClick(s, ev);
            termbaseContextMenu.Items.Add(openMenuItem);
            termbaseContextMenu.Items.Add(renameMenuItem);
            termbaseContextMenu.Items.Add(new ToolStripSeparator());
            var distillMenuItem = new ToolStripMenuItem("\u2697 Distill to SuperMemory");
            distillMenuItem.Click += (s, ev) => OnDistillTermbaseClick();
            termbaseContextMenu.Items.Add(distillMenuItem);
            _dgvTermbases.ContextMenuStrip = termbaseContextMenu;
            _dgvTermbases.CellMouseDown += (s, ev) =>
            {
                if (ev.Button == MouseButtons.Right && ev.RowIndex >= 0 && ev.RowIndex < _termbases.Count)
                {
                    _dgvTermbases.ClearSelection();
                    _dgvTermbases.Rows[ev.RowIndex].Selected = true;
                }
            };

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

            _chkCaseSensitive = new CheckBox
            {
                Text = "Enable case-sensitive matching globally",
                Location = new Point(10, 32),
                AutoSize = true,
                ForeColor = Color.FromArgb(60, 60, 60)
            };

            // Bottom rows: a label, an input, and a trailing unit label. The
            // input column was previously hard-coded to x=130, but at 150%
            // Windows display scaling the AutoSize labels grow past 120px
            // and overlap the input. Compute a shared input-column x from the
            // widest of the three labels' actual widths, then chain the
            // trailing units off each input's right edge.
            var lblFontSize = new Label
            {
                Text = "Panel font size:",
                Location = new Point(10, 58),
                AutoSize = true,
                ForeColor = Color.FromArgb(60, 60, 60)
            };
            var lblShortcutStyle = new Label
            {
                Text = "Term shortcuts:",
                Location = new Point(10, 86),
                AutoSize = true,
                ForeColor = Color.FromArgb(60, 60, 60)
            };
            var lblChordDelay = new Label
            {
                Text = "Shortcut delay:",
                Location = new Point(10, 114),
                AutoSize = true,
                ForeColor = Color.FromArgb(60, 60, 60)
            };
            int inputX = Math.Max(130,
                Math.Max(lblFontSize.PreferredSize.Width,
                    Math.Max(lblShortcutStyle.PreferredSize.Width,
                             lblChordDelay.PreferredSize.Width)) + 16);

            // Bumped widths so the digit area still feels comfortable after
            // AutoScaleMode.Dpi scales the control body at >100% scaling – at
            // 60/70px the system spinner buttons swallowed most of the width.
            _nudFontSize = new NumericUpDown
            {
                Location = new Point(inputX, 56),
                Width = 80,
                Minimum = 7,
                Maximum = 16,
                DecimalPlaces = 1,
                Increment = 0.5m,
                Value = (decimal)_settings.PanelFontSize
            };

            var lblFontPt = new Label
            {
                Text = "pt",
                Location = new Point(_nudFontSize.Right + 4, 58),
                AutoSize = true,
                ForeColor = Color.FromArgb(100, 100, 100)
            };

            _cboShortcutStyle = new ComboBox
            {
                Location = new Point(inputX, 84),
                Width = 280,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cboShortcutStyle.Items.Add("Sequential (Alt+45 = term 45)");
            _cboShortcutStyle.Items.Add("Repeated digit (Alt+55, Alt+66, etc.)");
            _cboShortcutStyle.SelectedIndex = _settings.TermShortcutStyle == "repeated" ? 1 : 0;

            _nudChordDelay = new NumericUpDown
            {
                Location = new Point(inputX, 112),
                Width = 90,
                Minimum = 300,
                Maximum = 3000,
                Increment = 100,
                Value = Math.Max(300, Math.Min(3000, _settings.ChordDelayMs))
            };

            var lblChordMs = new Label
            {
                Text = "ms",
                Location = new Point(_nudChordDelay.Right + 4, 114),
                AutoSize = true,
                ForeColor = Color.FromArgb(100, 100, 100)
            };

            var lblSuffixTolerant = new Label
            {
                Text = "Particle matching:",
                Location = new Point(10, 142),
                AutoSize = true,
                ForeColor = Color.FromArgb(60, 60, 60)
            };
            _cboSuffixTolerant = new ComboBox
            {
                Location = new Point(inputX, 140),
                Width = 280,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cboSuffixTolerant.Items.Add("Auto (on for Korean / Japanese)");
            _cboSuffixTolerant.Items.Add("Always on");
            _cboSuffixTolerant.Items.Add("Always off");
            switch ((_settings.SuffixTolerantMatching ?? "auto").Trim().ToLowerInvariant())
            {
                case "on": _cboSuffixTolerant.SelectedIndex = 1; break;
                case "off": _cboSuffixTolerant.SelectedIndex = 2; break;
                default: _cboSuffixTolerant.SelectedIndex = 0; break;
            }

            var tips = new ToolTip();
            tips.SetToolTip(_btnOpenTermbase,
                "Open the selected termbase in the built-in termbase editor.");
            tips.SetToolTip(_btnExport,
                "Export all terms from the selected termbase to a CSV file.");
            tips.SetToolTip(_btnImport,
                "Import terms from a CSV file into the selected termbase.");
            tips.SetToolTip(_btnRemoveTermbase,
                "Remove the selected termbase from the database.\nThe termbase and its terms will be permanently deleted.");
            tips.SetToolTip(_btnAddTermbase,
                "Add a new termbase to the database.");
            tips.SetToolTip(_cboShortcutStyle,
                "Sequential: type the term number digit by digit (short delay).\n" +
                "Repeated digit: press the same key multiple times (no delay).");
            tips.SetToolTip(_nudChordDelay,
                "How long the system waits for the next digit in Sequential mode (in milliseconds).\n" +
                "Increase if you need more time between keystrokes. Only applies to Sequential mode.");
            tips.SetToolTip(_chkCaseSensitive,
                "When checked, terms only match if the case matches exactly.\n" +
                "Individual termbases can override this using the Case column above.");
            tips.SetToolTip(_cboSuffixTolerant,
                "For Korean / Japanese, grammatical particles attach to nouns with no space.\n" +
                "When active, a term still matches when the segment word has a trailing particle\n" +
                "(값 matches 값으로), and adding a term keeps your exact selection instead of\n" +
                "expanding to the whole word.\n" +
                "Auto: on when the source language is Korean or Japanese.");

            // Add controls to their respective panels
            topPanel.Controls.AddRange(new Control[]
            {
                lblSection, lblPath, _txtTermbasePath, _btnCreateNew, _btnBrowse,
                _lblTermbaseInfo, _lblTermbasesHeader,
                _btnOpenTermbase, _btnExport, _btnImport, _btnRemoveTermbase, _btnAddTermbase
            });

            bottomPanel.Controls.AddRange(new Control[]
            {
                sep, _chkAutoLoad, _chkCaseSensitive,
                lblFontSize, _nudFontSize, lblFontPt,
                lblShortcutStyle, _cboShortcutStyle,
                lblChordDelay, _nudChordDelay, lblChordMs,
                lblSuffixTolerant, _cboSuffixTolerant
            });

            gridPanel.Controls.Add(_dgvTermbases);

            // Add panels to page – order matters for Dock layout
            // (last added has highest z-order and docks first)
            page.Controls.Add(gridPanel);     // Fill – docks last, fills remaining space
            page.Controls.Add(bottomPanel);   // Bottom
            page.Controls.Add(topPanel);      // Top
        }

        private void BuildBackupTab(TabPage page)
        {
            var lblHeader = new Label
            {
                Text = "Back up and restore your Supervertaler settings",
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(50, 50, 50),
                Location = new Point(16, 16),
                AutoSize = true
            };

            var lblDesc = new Label
            {
                Text = "Export your settings to a file so you can restore them later – for example " +
                       "before upgrading the plugin, or to transfer your setup to another machine.\n\n" +
                       "The settings file contains all your plugin configuration: termbase paths, " +
                       "toggle states, font size, shortcut preferences, AI provider keys, model " +
                       "selections, and prompt configuration.",
                Location = new Point(16, 44),
                Size = new Size(500, 72),
                ForeColor = Color.FromArgb(80, 80, 80),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            var btnExport = new Button
            {
                Text = "Export Settings...",
                Location = new Point(16, 130),
                Width = 140,
                Height = 30,
                FlatStyle = FlatStyle.System
            };
            btnExport.Click += OnExportSettingsClick;

            var lblExport = new Label
            {
                Text = "Save a copy of your current settings to a JSON file.",
                Location = new Point(164, 136),
                AutoSize = true,
                ForeColor = Color.FromArgb(100, 100, 100)
            };

            var btnImport = new Button
            {
                Text = "Import Settings...",
                Location = new Point(16, 172),
                Width = 140,
                Height = 30,
                FlatStyle = FlatStyle.System
            };
            btnImport.Click += OnImportSettingsClick;

            var lblImport = new Label
            {
                Text = "Restore settings from a previously exported file.",
                Location = new Point(164, 178),
                AutoSize = true,
                ForeColor = Color.FromArgb(100, 100, 100)
            };

            var lblLocation = new Label
            {
                Text = "Settings file location:",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(80, 80, 80),
                Location = new Point(16, 224),
                AutoSize = true
            };

            var txtPath = new TextBox
            {
                Text = TermLensSettings.SettingsFilePath,
                Location = new Point(16, 244),
                Width = 500,
                ReadOnly = true,
                BackColor = Color.FromArgb(245, 245, 245),
                ForeColor = Color.FromArgb(60, 60, 60),
                Font = new Font("Consolas", 8.5f),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            var lnkOpenFolder = new LinkLabel
            {
                Text = "Open settings folder",
                Location = new Point(16, 274),
                AutoSize = true,
                Font = new Font("Segoe UI", 8.5f)
            };
            lnkOpenFolder.LinkClicked += (s, e) =>
            {
                var folder = Path.GetDirectoryName(TermLensSettings.SettingsFilePath);
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                    System.Diagnostics.Process.Start("explorer.exe", folder);
            };

            page.Controls.AddRange(new Control[]
            {
                lblHeader, lblDesc, btnExport, lblExport, btnImport, lblImport,
                lblLocation, txtPath, lnkOpenFolder
            });
        }

        private void OnGridCellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.ColumnIndex < 0 || e.RowIndex < 0) return;

            var colName = _dgvTermbases.Columns[e.ColumnIndex].Name;

            // Prevent toggling Write/Project/Case for MultiTerm rows (read-only)
            if (e.RowIndex >= _termbases.Count && (colName == "colWrite" || colName == "colProject" || colName == "colCS"))
            {
                _dgvTermbases.CommitEdit(DataGridViewDataErrorContexts.Commit);
                _dgvTermbases.Rows[e.RowIndex].Cells[colName].Value = false;
                return;
            }

            // Warn-once when a non-matching termbase is being assigned as Write
            // or Project. We only check the database termbases (rows < _termbases.Count);
            // MultiTerm rows already short-circuit above. Untick removes the
            // confirmation so a re-tick re-asks. Read column is exempt –
            // there's no harm in *reading* a non-matching termbase, only in
            // writing into it.
            if ((colName == "colWrite" || colName == "colProject") && e.RowIndex < _termbases.Count)
            {
                _dgvTermbases.CommitEdit(DataGridViewDataErrorContexts.Commit);
                var clicked = _dgvTermbases.Rows[e.RowIndex].Cells[colName].Value as bool? ?? false;
                var tb = _termbases[e.RowIndex];

                if (clicked)
                {
                    if (!ConfirmWriteToNonMatchingTermbase(tb, colName))
                    {
                        // User cancelled – revert the tick.
                        _dgvTermbases.Rows[e.RowIndex].Cells[colName].Value = false;
                        _dgvTermbases.RefreshEdit();
                        return;
                    }
                }
                else
                {
                    // Unticking removes the override so a future re-tick re-asks.
                    if (_settings.ConfirmedNonMatchingWriteTermbaseNames != null && tb?.Name != null)
                    {
                        _settings.ConfirmedNonMatchingWriteTermbaseNames.RemoveAll(
                            n => string.Equals(n, tb.Name, StringComparison.Ordinal));
                    }
                }
            }

            // Radio-button enforcement for Project column only (only one can be project)
            // Write column allows multiple selections – terms are inserted into all write targets.
            if (colName == "colProject")
            {
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

        /// <summary>
        /// Whether the termbase is eligible to be auto-ticked by a bulk
        /// "tick-all Write" header click. Non-matching termbases that the
        /// user hasn't already confirmed are skipped – the user has to tick
        /// them individually so the per-termbase confirm dialog can ask.
        /// </summary>
        private bool IsTermbaseEligibleForBulkWriteTick(TermbaseInfo tb)
        {
            if (tb == null) return false;
            if (string.IsNullOrEmpty(_projectSourceLanguage)) return true;

            var direction = LanguageUtils.CompareTermbaseDirection(
                _projectSourceLanguage, tb.SourceLang, tb.TargetLang);
            if (direction != LanguageUtils.TermbaseDirection.Unrelated) return true;

            return !string.IsNullOrEmpty(tb.Name)
                && _settings.ConfirmedNonMatchingWriteTermbaseNames != null
                && _settings.ConfirmedNonMatchingWriteTermbaseNames.Contains(tb.Name, StringComparer.Ordinal);
        }

        /// <summary>
        /// Shows a confirm dialog when the user ticks Write or Project on a
        /// termbase whose declared language pair doesn't match the active
        /// project (the <see cref="LanguageUtils.TermbaseDirection.Unrelated"/>
        /// case from <see cref="LanguageUtils.CompareTermbaseDirection"/>).
        /// Returns <c>true</c> if the tick should proceed (no project loaded,
        /// language pair matches, or the user already confirmed for this
        /// termbase, or the user clicks Yes), <c>false</c> if it should be
        /// reverted.
        ///
        /// Pre-v4.19.58 the user could silently tick Write or Project on a
        /// non-matching termbase and the AI / Quick-Add paths would happily
        /// write terms into it without questioning – v4.19.56 stopped the
        /// per-write *inversion* of unrelated termbases, but writing the
        /// wrong-language terms into them was still possible. This dialog
        /// catches the mistake at tick-time. Confirmations persist across
        /// sessions in <see cref="TermLensSettings.ConfirmedNonMatchingWriteTermbaseNames"/>.
        /// </summary>
        private bool ConfirmWriteToNonMatchingTermbase(TermbaseInfo tb, string colName)
        {
            // No project loaded → can't compare → don't warn.
            if (string.IsNullOrEmpty(_projectSourceLanguage)) return true;
            if (tb == null) return true;

            var direction = LanguageUtils.CompareTermbaseDirection(
                _projectSourceLanguage, tb.SourceLang, tb.TargetLang);
            if (direction != LanguageUtils.TermbaseDirection.Unrelated) return true;

            // Already confirmed for this termbase – don't nag.
            if (!string.IsNullOrEmpty(tb.Name)
                && _settings.ConfirmedNonMatchingWriteTermbaseNames != null
                && _settings.ConfirmedNonMatchingWriteTermbaseNames.Contains(tb.Name, StringComparer.Ordinal))
            {
                return true;
            }

            var role = colName == "colProject" ? "the Project termbase" : "a Write termbase";
            var projShort = LanguageUtils.ShortenLanguageName(_projectSourceLanguage) ?? _projectSourceLanguage;
            var tbDir = $"{LanguageUtils.ShortenLanguageName(tb.SourceLang) ?? tb.SourceLang} → " +
                        $"{LanguageUtils.ShortenLanguageName(tb.TargetLang) ?? tb.TargetLang}";

            var msg =
                $"“{tb.Name}” is a {tbDir} termbase, but the active project's source " +
                $"language is {projShort}.\n\n" +
                $"Setting it as {role} means new terms added during this project will be written " +
                "into a termbase whose language pair doesn't match.\n\n" +
                "This is occasionally intentional (multilingual or global termbases, bootstrapping " +
                "a new direction) – tick “Yes” to continue. The plugin will remember this " +
                "choice for this termbase and won't ask again until you untick the box.";

            var result = MessageBox.Show(this, msg,
                "Supervertaler — Termbase language pair doesn't match project",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);

            if (result != DialogResult.Yes) return false;

            if (_settings.ConfirmedNonMatchingWriteTermbaseNames == null)
                _settings.ConfirmedNonMatchingWriteTermbaseNames = new List<string>();
            if (!string.IsNullOrEmpty(tb.Name)
                && !_settings.ConfirmedNonMatchingWriteTermbaseNames.Contains(tb.Name, StringComparer.Ordinal))
            {
                _settings.ConfirmedNonMatchingWriteTermbaseNames.Add(tb.Name);
            }
            return true;
        }

        private void OnColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            var col = _dgvTermbases.Columns[e.ColumnIndex];
            if (col.Name != "colRead" && col.Name != "colWrite" && col.Name != "colProject" && col.Name != "colAi")
                return;

            if (_dgvTermbases.Rows.Count == 0) return;

            // For Write/Project, skip MultiTerm rows (they're read-only)
            bool skipMultiTerm = col.Name == "colWrite" || col.Name == "colProject";

            if (col.Name == "colProject")
            {
                // Project is radio-button style – header click clears the selection
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

                    // Skip non-matching termbases when bulk-ticking Write –
                    // each one needs explicit per-termbase confirmation, not a
                    // hidden bulk override. Already-confirmed ones flow
                    // through normally. Unticking is unconditional.
                    if (newValue && col.Name == "colWrite" && row.Index < _termbases.Count)
                    {
                        var tb = _termbases[row.Index];
                        if (!IsTermbaseEligibleForBulkWriteTick(tb))
                            continue;
                    }

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
            if (colName == "colRead" || colName == "colWrite" || colName == "colProject" || colName == "colCS" || colName == "colAi")
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

            var selected = _dgvTermbases.Rows[rowIndex].Tag as TermbaseInfo;
            if (selected == null) return;

            using (var editor = new TermbaseEditorDialog(dbPath, selected, _settings))
            {
                editor.ShowDialog(this);
            }

            // Refresh the list – term counts may have changed
            UpdateTermbaseInfo(dbPath);
            PopulateTermbaseList(dbPath);
        }

        private void OnDistillTermbaseClick()
        {
            if (_dgvTermbases.SelectedRows.Count == 0) return;
            var termbase = _dgvTermbases.SelectedRows[0].Tag as TermbaseInfo;
            if (termbase == null) return;

            var dbPath = _txtTermbasePath.Text.Trim();
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
            {
                MessageBox.Show("Please select or create a database file first.",
                    "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            List<TermEntry> terms;
            try
            {
                terms = TermbaseReader.GetAllTermsByTermbaseId(dbPath, termbase.Id);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not read termbase: {ex.Message}",
                    "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (terms.Count == 0)
            {
                MessageBox.Show("This termbase is empty – nothing to distill.",
                    "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Format terms as structured text for the AI
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Termbase: {termbase.Name}");
            sb.AppendLine($"Languages: {termbase.SourceLang} → {termbase.TargetLang}");
            sb.AppendLine($"Term count: {terms.Count}");
            sb.AppendLine();
            sb.AppendLine("| Source | Target | Definition | Notes | Domain |");
            sb.AppendLine("|--------|--------|------------|-------|--------|");
            foreach (var t in terms)
            {
                var def = (t.Definition ?? "").Replace("|", "\\|").Replace("\n", " ").Trim();
                var notes = (t.Notes ?? "").Replace("|", "\\|").Replace("\n", " ").Trim();
                var domain = (t.Domain ?? "").Replace("|", "\\|").Replace("\n", " ").Trim();
                sb.AppendLine($"| {t.SourceTerm} | {t.TargetTerm} | {def} | {notes} | {domain} |");
            }

            DistillTermbaseRequested?.Invoke(this,
                new DistillTermbaseEventArgs(termbase.Name, sb.ToString()));
        }

        private void PopulateFromSettings()
        {
            _txtTermbasePath.Text = _settings.TermbasePath ?? "";
            _chkAutoLoad.Checked = _settings.AutoLoadOnStartup;
            _chkCaseSensitive.Checked = _settings.CaseSensitiveMatching;
            _chkUsageStats.Checked = _settings.UsageStatisticsEnabled;
            _chkSuperSearchInTab.Checked = _settings.SuperSearchInAssistantTab;
            _nudFontSize.Value = Math.Max(_nudFontSize.Minimum, Math.Min(_nudFontSize.Maximum, (decimal)_settings.PanelFontSize));
            var curScaleText = ((int)Math.Round(_settings.UiScaleFactor * 100)) + "%";
            var scaleIdx = _cboUiScale.Items.IndexOf(curScaleText);
            _cboUiScale.SelectedIndex = scaleIdx >= 0 ? scaleIdx : 0;
            UpdateTermbaseInfo(_settings.TermbasePath);
            PopulateTermbaseList(_settings.TermbasePath);

            // AI settings
            _aiSettingsPanel.PopulateFromSettings(_settings.AiSettings);

            // Prompts – pass per-project active prompt if available
            string projectActivePrompt = null;
            var projPath = TermLensEditorViewPart.GetCurrentProjectPath();
            if (!string.IsNullOrEmpty(projPath))
            {
                var ps = ProjectSettings.Load(projPath);
                if (ps != null && !string.IsNullOrEmpty(ps.ActivePromptPath))
                    projectActivePrompt = ps.ActivePromptPath;
            }
            _promptManagerPanel.PopulateFromSettings(_settings.AiSettings, _promptLibrary, projectActivePrompt);
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

            // AI inclusion ("AI" column) is stored as an opt-in disable list, shared by
            // Supervertaler termbases (by Id) and MultiTerm termbases (by SyntheticId).
            var disabledAi = new HashSet<long>(
                _settings.AiSettings?.DisabledAiTermbaseIds ?? new List<long>());

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
                                bool isCaseSensitive = tb.CaseSensitive == 1;
                                bool isAi = !disabledAi.Contains(tb.Id);
                                int rowIdx = _dgvTermbases.Rows.Add(
                                    isRead,
                                    isWrite,
                                    isProject,
                                    isCaseSensitive,
                                    isAi,
                                    tb.Name,
                                    tb.TermCount.ToString("N0"),
                                    $"{LanguageUtils.ShortenLanguageName(tb.SourceLang)} \u2192 {LanguageUtils.ShortenLanguageName(tb.TargetLang)}");
                                _dgvTermbases.Rows[rowIdx].Tag = tb;
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
                    bool isAiMt = !disabledAi.Contains(info.SyntheticId);
                    var langText = !string.IsNullOrEmpty(info.SourceIndexName)
                        && !string.IsNullOrEmpty(info.TargetIndexName)
                        ? $"{info.SourceIndexName} \u2192 {info.TargetIndexName}"
                        : info.LoadMode == MultiTermLoadMode.Failed ? "Failed to load" : "";

                    int rowIdx = _dgvTermbases.Rows.Add(
                        isRead,     // Read
                        false,      // Write (always disabled for MultiTerm)
                        false,      // Project (always disabled for MultiTerm)
                        false,      // CS (always disabled for MultiTerm)
                        isAiMt,     // AI (MultiTerm CAN be sent to the AI)
                        $"{info.Name} [MultiTerm]",
                        info.TermCount.ToString("N0"),
                        langText);

                    // Style MultiTerm rows with a light green tint
                    var row = _dgvTermbases.Rows[rowIdx];
                    row.Tag = info;
                    row.Cells["colWrite"].ReadOnly = true;
                    row.Cells["colProject"].ReadOnly = true;
                    row.Cells["colCS"].ReadOnly = true;
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

        private void RenameTermbase(int rowIndex)
        {
            var dbPath = _txtTermbasePath.Text.Trim();
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath)) return;
            if (rowIndex < 0 || rowIndex >= _termbases.Count) return;

            var tb = _termbases[rowIndex];

            using (var dlg = new Form())
            {
                dlg.Text = "Rename Termbase";
                dlg.Width = 400;
                dlg.Height = 150;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.Font = new Font("Segoe UI", 9f);

                var lbl = new Label { Text = "New name:", Left = 15, Top = 15, AutoSize = true };
                var txt = new TextBox { Text = tb.Name, Left = 15, Top = 38, Width = 350 };
                txt.SelectAll();
                var btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 210, Top = 72, Width = 75 };
                var btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 290, Top = 72, Width = 75 };
                dlg.AcceptButton = btnOk;
                dlg.CancelButton = btnCancel;
                dlg.Controls.AddRange(new Control[] { lbl, txt, btnOk, btnCancel });

                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                var newName = txt.Text.Trim();
                if (string.IsNullOrWhiteSpace(newName) || newName == tb.Name) return;

                try
                {
                    TermbaseReader.RenameTermbase(dbPath, tb.Id, newName);
                    UpdateTermbaseInfo(dbPath);
                    PopulateTermbaseList(dbPath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to rename termbase: {ex.Message}",
                        "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

            var selected = _dgvTermbases.SelectedRows[0].Tag as TermbaseInfo;
            if (selected == null) return;

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

            var selected = _dgvTermbases.SelectedRows[0].Tag as TermbaseInfo;
            if (selected == null) return;

            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = $"Import TSV into \"{selected.Name}\"";
                dlg.Filter = "Tab-separated files (*.tsv;*.txt)|*.tsv;*.txt|All files (*.*)|*.*";

                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                // Count data rows and show confirmation before importing.
                int rowCount;
                try
                {
                    var allLines = File.ReadAllLines(dlg.FileName);
                    rowCount = allLines.Length > 1 ? allLines.Length - 1 : 0;
                }
                catch
                {
                    rowCount = 0;
                }

                var langPair = $"{LanguageUtils.ShortenLanguageName(selected.SourceLang)} \u2192 " +
                               $"{LanguageUtils.ShortenLanguageName(selected.TargetLang)}";
                var fileName = Path.GetFileName(dlg.FileName);
                var confirmMsg = rowCount > 0
                    ? $"Import {rowCount:N0} row{(rowCount == 1 ? "" : "s")} from \"{fileName}\" " +
                      $"into \"{selected.Name}\" ({langPair})?"
                    : $"Import \"{fileName}\" into \"{selected.Name}\" ({langPair})?";

                if (MessageBox.Show(confirmMsg, "TermLens",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                    return;

                // Run import on a background thread with a progress dialog.
                var tsvFilePath = dlg.FileName;
                var progressDlg = new Form
                {
                    Text = "Importing…",
                    Font = new Font("Segoe UI", 9f),
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    MaximizeBox = false,
                    MinimizeBox = false,
                    StartPosition = FormStartPosition.CenterParent,
                    ClientSize = new Size(400, 90),
                    BackColor = Color.White,
                    ControlBox = false
                };
                var lblProgress = new Label
                {
                    Text = $"Importing into \"{selected.Name}\"…",
                    Location = new Point(16, 12),
                    AutoSize = true,
                    ForeColor = Color.FromArgb(80, 80, 80)
                };
                var progressBar = new ProgressBar
                {
                    Location = new Point(16, 38),
                    Width = 368,
                    Height = 22,
                    Minimum = 0,
                    Maximum = Math.Max(rowCount, 1),
                    Style = ProgressBarStyle.Continuous
                };
                progressDlg.Controls.Add(lblProgress);
                progressDlg.Controls.Add(progressBar);

                int importedCount = 0;
                Exception importError = null;

                var progress = new Progress<int>(imported =>
                {
                    if (progressDlg.IsDisposed) return;
                    progressBar.Value = Math.Min(imported, progressBar.Maximum);
                    lblProgress.Text = $"Importing into \"{selected.Name}\"… {imported:N0} / {rowCount:N0} terms";
                });

                progressDlg.Shown += async (s2, e2) =>
                {
                    try
                    {
                        importedCount = await Task.Run(() =>
                            TermbaseReader.ImportTsv(dbPath, selected.Id, tsvFilePath,
                                selected.SourceLang, selected.TargetLang, progress));
                    }
                    catch (Exception ex)
                    {
                        importError = ex;
                    }
                    progressDlg.Close();
                };

                progressDlg.ShowDialog(this);

                if (importError != null)
                {
                    MessageBox.Show($"Import failed:\n{importError.Message}",
                        "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else
                {
                    MessageBox.Show($"Imported {importedCount:N0} terms into \"{selected.Name}\".",
                        "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }

                UpdateTermbaseInfo(dbPath);
                PopulateTermbaseList(dbPath);
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

            var selected = _dgvTermbases.SelectedRows[0].Tag as TermbaseInfo;
            if (selected == null) return;

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
            _settings.CaseSensitiveMatching = _chkCaseSensitive.Checked;
            _settings.UsageStatisticsEnabled = _chkUsageStats.Checked;
            _settings.SuperSearchInAssistantTab = _chkSuperSearchInTab.Checked;
            // Mark as asked (both v1 and v2 flags) so the opt-in dialog won't show again
            _settings.UsageStatisticsAsked = true;
            _settings.UsageStatisticsAskedV2 = true;
            // Generate anonymous ID on first opt-in
            if (_settings.UsageStatisticsEnabled && string.IsNullOrEmpty(_settings.UsageStatisticsId))
                _settings.UsageStatisticsId = System.Guid.NewGuid().ToString("D");
            _settings.PanelFontSize = (float)_nudFontSize.Value;

            // Parse UI scale from dropdown (e.g. "125%" → 1.25f)
            if (_cboUiScale.SelectedItem is string scaleStr)
            {
                scaleStr = scaleStr.TrimEnd('%');
                if (int.TryParse(scaleStr, out var pct) && pct > 0)
                    _settings.UiScaleFactor = pct / 100f;
            }

            _settings.TermShortcutStyle = _cboShortcutStyle.SelectedIndex == 1 ? "repeated" : "sequential";
            _settings.ChordDelayMs = (int)_nudChordDelay.Value;
            switch (_cboSuffixTolerant.SelectedIndex)
            {
                case 1: _settings.SuffixTolerantMatching = "on"; break;
                case 2: _settings.SuffixTolerantMatching = "off"; break;
                default: _settings.SuffixTolerantMatching = "auto"; break;
            }

            // Build disabled list, write IDs, and project ID from grid cells
            _settings.DisabledTermbaseIds = new List<long>();
            _settings.WriteTermbaseIds = new List<long>();
            _settings.WriteTermbaseId = -1; // deprecated single-ID field
            _settings.ProjectTermbaseId = -1;

            var dbPath = _txtTermbasePath.Text;

            // Iterate by Tag so the mapping survives column sorts
            _settings.DisabledMultiTermIds = new List<long>();
            var disabledAiIds = new List<long>();
            foreach (DataGridViewRow row in _dgvTermbases.Rows)
            {
                if (row.Tag is TermbaseInfo tb)
                {
                    var readChecked = row.Cells["colRead"].Value as bool? ?? false;
                    var writeChecked = row.Cells["colWrite"].Value as bool? ?? false;
                    var projectChecked = row.Cells["colProject"].Value as bool? ?? false;
                    bool csChecked = row.Cells["colCS"].Value as bool? ?? false;
                    int caseSetting = csChecked ? 1 : 0;
                    if (caseSetting != tb.CaseSensitive && !string.IsNullOrEmpty(dbPath) && File.Exists(dbPath))
                    {
                        try { TermbaseReader.SetTermbaseCaseSensitive(dbPath, tb.Id, caseSetting); }
                        catch { /* ignore write failures */ }
                        tb.CaseSensitive = caseSetting;
                    }
                    if (!readChecked) _settings.DisabledTermbaseIds.Add(tb.Id);
                    if (writeChecked) _settings.WriteTermbaseIds.Add(tb.Id);
                    if (projectChecked) _settings.ProjectTermbaseId = tb.Id;

                    var aiChecked = row.Cells["colAi"].Value as bool? ?? false;
                    if (!aiChecked) disabledAiIds.Add(tb.Id);
                }
                else if (row.Tag is MultiTermTermbaseInfo mtInfo)
                {
                    var readChecked = row.Cells["colRead"].Value as bool? ?? false;
                    if (!readChecked)
                        _settings.DisabledMultiTermIds.Add(mtInfo.SyntheticId);

                    var aiChecked = row.Cells["colAi"].Value as bool? ?? false;
                    if (!aiChecked) disabledAiIds.Add(mtInfo.SyntheticId);
                }
            }

            // The "AI" column on the Termbases tab is now the single source of truth for
            // which termbases the AI may see (Chat, AutoPrompt, batch). Persist it as the
            // opt-in disable list and mark it initialized so the opt-in auto-migration in
            // TermLensEditorViewPart doesn't reset the user's choices on next startup.
            if (_settings.AiSettings != null)
            {
                _settings.AiSettings.DisabledAiTermbaseIds = disabledAiIds;
                _settings.AiSettings.AiTermbaseIdsInitialized = true;
            }

            // AI settings
            _aiSettingsPanel.ApplyToSettings(_settings.AiSettings);

            // Prompts
            _promptManagerPanel.ApplyToSettings(_settings.AiSettings);

            _settings.Save();

            // Also save per-project settings if a Trados project is active
            var projectPath = TermLensEditorViewPart.GetCurrentProjectPath();
            var projectName = TermLensEditorViewPart.GetCurrentProjectName();
            if (!string.IsNullOrEmpty(projectPath))
            {
                ProjectSettings.Save(projectPath,
                    _settings.ExtractProjectSettings(projectPath, projectName));
            }
        }

        private void OnExportSettingsClick(object sender, EventArgs e)
        {
            using (var dlg = new SaveFileDialog
            {
                Title = "Export Settings",
                Filter = "JSON files (*.json)|*.json",
                FileName = "supervertaler-settings.json",
                OverwritePrompt = true
            })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        var src = TermLensSettings.SettingsFilePath;
                        if (!File.Exists(src))
                        {
                            // Save current in-memory settings first so there is something to export
                            _settings.Save();
                        }
                        File.Copy(src, dlg.FileName, overwrite: true);
                        MessageBox.Show(this,
                            "Settings exported successfully.",
                            "Export Settings",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this,
                            "Failed to export settings:\n" + ex.Message,
                            "Export Settings",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void OnImportSettingsClick(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "Import Settings",
                Filter = "JSON files (*.json)|*.json"
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK)
                    return;

                // Validate the file is a readable settings JSON
                TermLensSettings imported;
                try
                {
                    var json = File.ReadAllText(dlg.FileName, System.Text.Encoding.UTF8);
                    using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
                    {
                        var serializer = new System.Runtime.Serialization.Json.DataContractJsonSerializer(
                            typeof(TermLensSettings));
                        imported = (TermLensSettings)serializer.ReadObject(stream);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this,
                        "The selected file is not a valid Supervertaler settings file.\n\n" + ex.Message,
                        "Import Settings",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                var result = MessageBox.Show(this,
                    "Importing settings will replace all your current settings and close this dialog.\n\n" +
                    "Your current settings will be backed up as settings.backup.json.\n\n" +
                    "Continue?",
                    "Import Settings",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (result != DialogResult.Yes)
                    return;

                try
                {
                    // Back up current settings
                    var currentFile = TermLensSettings.SettingsFilePath;
                    if (File.Exists(currentFile))
                    {
                        var backupFile = Path.Combine(
                            Path.GetDirectoryName(currentFile),
                            "settings.backup.json");
                        File.Copy(currentFile, backupFile, overwrite: true);
                    }

                    // Copy imported file over current settings
                    File.Copy(dlg.FileName, currentFile, overwrite: true);

                    MessageBox.Show(this,
                        "Settings imported successfully. The dialog will now close and the new settings will take effect.",
                        "Import Settings",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);

                    // Signal import and close – caller checks SettingsImported flag
                    SettingsImported = true;
                    DialogResult = DialogResult.Cancel;
                    Close();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this,
                        "Failed to import settings:\n" + ex.Message,
                        "Import Settings",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
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
                case 0:  return HelpSystem.Topics.SettingsGeneral;
                case 1:  return HelpSystem.Topics.SettingsTermLens;
                case 2:  return HelpSystem.Topics.SettingsAi;
                case 3:  return HelpSystem.Topics.SettingsPrompts;
                case 4:  return HelpSystem.Topics.Licensing;
                case 5:  return HelpSystem.Topics.SettingsBackup;
                default: return HelpSystem.Topics.SettingsGeneral;
            }
        }

        private string GetCurrentHelpLabel()
        {
            switch (_tabControl?.SelectedIndex)
            {
                case 0:  return "General Settings Help";
                case 1:  return "TermLens Settings Help";
                case 2:  return "AI Settings Help";
                case 3:  return "Prompts Help";
                case 4:  return "Licensing Help";
                case 5:  return "Backup Help";
                default: return "Settings Help";
            }
        }

        private void OnHelpButtonClicked(object sender, System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true; // prevent the "What's This?" cursor

            var topic = GetCurrentHelpTopic();
            var label = GetCurrentHelpLabel();

            var menu = new ContextMenuStrip();
            menu.Items.Add(label, null, (s, ev) =>
                HelpSystem.OpenHelp(topic));
            menu.Items.Add("-");
            menu.Items.Add("About Supervertaler for Trados", null, (s, ev) =>
            {
                using (var dlg = new AboutDialog())
                    dlg.ShowDialog(this);
            });

            // Show near the title bar help button (top-right area)
            var btnLocation = new Point(ClientSize.Width - 60, 0);
            menu.Show(this, btnLocation);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F1)
            {
                HelpSystem.OpenHelp(GetCurrentHelpTopic());
                return true;
            }
            if (keyData == Keys.F2 && _dgvTermbases.Focused && _dgvTermbases.SelectedRows.Count > 0)
            {
                var rowIndex = _dgvTermbases.SelectedRows[0].Index;
                if (rowIndex >= 0 && rowIndex < _termbases.Count)
                    RenameTermbase(rowIndex);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    /// <summary>
    /// Event arguments for the "Distill to SuperMemory" termbase context menu action.
    /// </summary>
    public class DistillTermbaseEventArgs : EventArgs
    {
        public string TermbaseName { get; }
        public string FormattedTerms { get; }

        public DistillTermbaseEventArgs(string termbaseName, string formattedTerms)
        {
            TermbaseName = termbaseName;
            FormattedTerms = formattedTerms;
        }
    }
}
