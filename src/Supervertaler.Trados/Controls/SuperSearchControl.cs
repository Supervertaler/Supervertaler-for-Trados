using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
        private Label _lblSearchSource;
        private TextBox _txtSearch;          // source-term box
        private Label _lblSearchTarget;
        private TextBox _txtSearchTarget;    // target-term box
        private Button _btnSearch;
        private Button _btnStop;
        private ComboBox _cboMode;
        private CheckBox _chkCaseSensitive;
        private CheckBox _chkRegex;
        private CheckBox _chkWholeWord;
        private CheckBox _chkLiveFilter;
        private CheckBox _chkShowReplace;
        private Button _btnFiles;
        private Button _btnTms;
        private Button _btnTbs;
        private Button _btnWeb;
        private Button _btnWebGo;
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
        private List<string> _allProjectTms = new List<string>();
        private HashSet<string> _excludedTms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Termbases are identified by the label the controller builds
        // ("Name (Kind)"), so a Supervertaler and a MultiTerm termbase that
        // happen to share a name stay distinguishable.
        private List<string> _allTermbases = new List<string>();
        private HashSet<string> _excludedTermbases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ─── Web resources ───────────────────────────────────────
        // The reconciled resource list, pushed in by the controller from
        // settings. Unlike files/TMs/termbases this is not a project property,
        // so it is stored as the resources themselves rather than as an
        // exclusion set — the user's on/off state lives on WebResource.Enabled.
        private List<WebResource> _webResources = new List<WebResource>();

        // ─── Highlight state ─────────────────────────────────────
        private string _highlightSource;
        private string _highlightTarget;
        private bool _highlightCaseSensitive;

        // ─── Live-filter state ───────────────────────────────────
        // The last full backend result set; live filtering narrows this
        // in-memory rather than re-querying files/TMs on every keystroke.
        private List<SearchResult> _unfilteredResults;
        private System.Windows.Forms.Timer _liveFilterTimer;

        // ─── Styling ─────────────────────────────────────────────
        private static readonly Color HeaderBg = Color.FromArgb(245, 245, 245);
        private static readonly Color BorderColor = Color.FromArgb(200, 200, 200);
        private static readonly Color TextColor = Color.FromArgb(50, 50, 50);
        private static readonly Color SubtleColor = Color.FromArgb(120, 120, 120);
        private static readonly Color AltRowColor = Color.FromArgb(250, 250, 250);
        // Translation-memory rows show their TM name in this blue in the File/TM
        // column, to set them apart from project-file rows at a glance.
        private static readonly Color TmNameColor = Color.FromArgb(30, 110, 195);
        /// <summary>Termbase names in the File/TM column – green, echoing the
        /// MultiTerm chip colour in TermLens.</summary>
        private static readonly Color TermbaseNameColor = Color.FromArgb(46, 125, 50);

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

        /// <summary>Fired when the user changes the search-source mode dropdown
        /// (Project files / Files + TMs / TMs only). The controller persists it.</summary>
        public event EventHandler ModeChanged;

        /// <summary>
        /// Raised when the user changes which web resources are enabled, so the
        /// controller can persist the list. Mirrors <see cref="ModeChanged"/>.
        /// </summary>
        public event EventHandler WebResourcesChanged;

        /// <summary>
        /// Raised when the user asks to run the current query against the
        /// enabled web resources. The controller resolves the project's language
        /// pair and decides whether to open a browser window or (later) render
        /// the results in embedded tabs.
        /// </summary>
        public event EventHandler<WebSearchRequestEventArgs> WebSearchRequested;

        public SuperSearchControl()
        {
            BuildUI();
        }

        private void BuildUI()
        {
            // NOT AutoScaleMode.Dpi. This panel lays its own toolbar out by hand
            // (see LayoutSearchBar) and is hosted inside a Trados ViewPart pane
            // that has already applied the system DPI factor, so making it a
            // second scaling boundary buys nothing and risks double-scaling.
            // The blanket "AutoScaleMode = Dpi on every dialog and panel" sweep
            // (1b80d8e, 2026-05-07) set Dpi here; None is the setting that sweep
            // itself prescribed for surfaces that own their layout.
            //
            // This is NOT a fix for the 2026-08-06 report of the search bar and
            // grid headers going missing. That turned out to be Trados docking
            // geometry, not ours: the pane's own caption bar was clipped too, so
            // the whole pane was sitting too high. A restart cleared it.
            AutoScaleMode = AutoScaleMode.None;
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

            _lblSearchSource = new Label
            {
                Text = "Src:",
                Font = smallFont,
                ForeColor = SubtleColor,
                AutoSize = true
            };
            _searchPanel.Controls.Add(_lblSearchSource);

            _txtSearch = new TextBox
            {
                Font = bodyFont,
                Location = new Point(4, 8),
                Width = 150  // auto-sized on resize
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

            _lblSearchTarget = new Label
            {
                Text = "Tgt:",
                Font = smallFont,
                ForeColor = SubtleColor,
                AutoSize = true
            };
            _searchPanel.Controls.Add(_lblSearchTarget);

            _txtSearchTarget = new TextBox
            {
                Font = bodyFont,
                Location = new Point(160, 8),
                Width = 150  // auto-sized on resize
            };
            _txtSearchTarget.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    FireSearch();
                }
            };
            _searchPanel.Controls.Add(_txtSearchTarget);

            _btnSearch = CreateButton("Search", bodyFont, 72, 26);
            _btnSearch.Click += (s, e) => FireSearch();
            Core.ClickThrough.Attach(_btnSearch, () => FireSearch());
            _searchPanel.Controls.Add(_btnSearch);

            // 46 px clipped "Stop" to "Sto" at the user's display scaling — the
            // label needs the same room "Search" gets, minus a little.
            _btnStop = CreateButton("Stop", bodyFont, 60, 26);
            _btnStop.Visible = false;
            _btnStop.Click += (s, e) => StopRequested?.Invoke(this, EventArgs.Empty);
            Core.ClickThrough.Attach(_btnStop, () => StopRequested?.Invoke(this, EventArgs.Empty));
            _searchPanel.Controls.Add(_btnStop);

            _cboMode = new ComboBox
            {
                Font = bodyFont,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 110
            };
            // "Everything" first so it reads as the recommended/default scope,
            // then one entry per source. Index order here must stay in lockstep
            // with SelectedSourceMode / SetSourceMode below.
            _cboMode.Items.AddRange(new object[] { "Everything", "Project files", "TMs", "Termbases" });
            _cboMode.SelectedIndex = 0;
            _cboMode.SelectedIndexChanged += (s, e) =>
            {
                // Replace only applies to project-file results: neither a TM
                // entry nor a term is a document location.
                _chkShowReplace.Enabled = ModeIncludesFiles(SelectedSourceMode);
                if (!_chkShowReplace.Enabled) _chkShowReplace.Checked = false;
                ModeChanged?.Invoke(this, EventArgs.Empty);
            };
            var ttMode = new ToolTip();
            ttMode.SetToolTip(_cboMode,
                "Where to search: everything at once, the project's SDLXLIFF files, " +
                "the project's translation memories (concordance), or your termbases " +
                "(Supervertaler, MultiTerm and Trados .ttb)");
            _searchPanel.Controls.Add(_cboMode);

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

            _chkWholeWord = new CheckBox
            {
                Text = "Word",
                Font = smallFont,
                AutoSize = true,
                ForeColor = SubtleColor
            };
            var ttWholeWord = new ToolTip();
            ttWholeWord.SetToolTip(_chkWholeWord,
                "Match whole word only (e.g. \"cat\" won't match \"category\")");
            _searchPanel.Controls.Add(_chkWholeWord);

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

            _chkLiveFilter = new CheckBox
            {
                Text = "Live",
                Font = smallFont,
                AutoSize = true,
                ForeColor = SubtleColor
            };
            var ttLive = new ToolTip();
            ttLive.SetToolTip(_chkLiveFilter,
                "Live filter: narrow the current results as you type instead of " +
                "re-searching each time. Press Enter / Search to run a fresh full search.");
            _searchPanel.Controls.Add(_chkLiveFilter);

            // Debounced live filtering of the loaded result set.
            _liveFilterTimer = new System.Windows.Forms.Timer { Interval = 200 };
            _liveFilterTimer.Tick += (s, e) => { _liveFilterTimer.Stop(); ApplyLiveFilter(); };
            _txtSearch.TextChanged += (s, e) => OnLiveFilterTextChanged();
            _txtSearchTarget.TextChanged += (s, e) => OnLiveFilterTextChanged();

            _btnFiles = CreateButton("Files", bodyFont, 56, 26);
            var ttFiles = new ToolTip();
            ttFiles.SetToolTip(_btnFiles, "Select which project files to include in the search");
            _btnFiles.Click += (s, e) => ShowFileSelectionDialog();
            Core.ClickThrough.Attach(_btnFiles, () => ShowFileSelectionDialog());
            _searchPanel.Controls.Add(_btnFiles);

            _btnTbs = CreateButton("TBs", bodyFont, 52, 26);
            var ttTbs = new ToolTip();
            ttTbs.SetToolTip(_btnTbs, "Select which termbases to include in the search");
            _btnTbs.Click += (s, e) => ShowTermbaseSelectionDialog();
            Core.ClickThrough.Attach(_btnTbs, () => ShowTermbaseSelectionDialog());
            _searchPanel.Controls.Add(_btnTbs);

            _btnTms = CreateButton("TMs", bodyFont, 52, 26);
            var ttTms = new ToolTip();
            ttTms.SetToolTip(_btnTms, "Select which translation memories to include in the search");
            _btnTms.Click += (s, e) => ShowTmSelectionDialog();
            Core.ClickThrough.Attach(_btnTms, () => ShowTmSelectionDialog());
            _searchPanel.Controls.Add(_btnTms);

            // Widths here are only a starting point — SizeToText re-fits both to
            // their captions as soon as the counts are known.
            _btnWeb = CreateButton("Web", bodyFont, 62, 26);
            var ttWeb = new ToolTip();
            ttWeb.SetToolTip(_btnWeb, "Choose which web resources to search");
            _btnWeb.Click += (s, e) => ShowWebSelectionDialog();
            Core.ClickThrough.Attach(_btnWeb, () => ShowWebSelectionDialog());
            _searchPanel.Controls.Add(_btnWeb);

            // Separate from the scope button on purpose. Files/TMs/TBs feed the
            // results grid and run on Search; the web opens pages, so it needs
            // its own deliberate trigger rather than firing on every search.
            _btnWebGo = CreateButton("\U0001F310", bodyFont, 34, 26);
            var ttWebGo = new ToolTip();
            ttWebGo.SetToolTip(_btnWebGo,
                "Search the web for the term in the Src box (Ctrl+Alt+L)");
            _btnWebGo.Click += (s, e) => FireWebSearch();
            Core.ClickThrough.Attach(_btnWebGo, () => FireWebSearch());
            _searchPanel.Controls.Add(_btnWebGo);

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
            Core.ClickThrough.Attach(_btnHelp, () => HelpRequested?.Invoke(this, EventArgs.Empty));
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
            Core.ClickThrough.Attach(_btnReplace, () => FireReplace());
            _replacePanel.Controls.Add(_btnReplace);

            _btnReplaceAll = CreateButton("Replace All", bodyFont, 94, 26);
            _btnReplaceAll.Click += (s, e) => FireReplaceAll();
            Core.ClickThrough.Attach(_btnReplaceAll, () => FireReplaceAll());
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

            // ─── Tooltips for the search & replace bars ──────────────
            // (the Aa / .* / Word / Files controls set their own tooltips above)
            var tt = new ToolTip { AutoPopDelay = 10000 };
            tt.SetToolTip(_txtSearch, "Source-text search term. Leave blank to match any source. Press Enter to search.");
            tt.SetToolTip(_txtSearchTarget, "Target-text search term. Leave blank to match any target. Fill both boxes to find segments whose source AND target match. Press Enter to search.");
            tt.SetToolTip(_btnSearch, "Search the selected files/TMs");
            tt.SetToolTip(_btnStop, "Stop the current search");
            tt.SetToolTip(_chkShowReplace, "Show the find & replace bar");
            tt.SetToolTip(_btnHelp, "Open SuperSearch help");
            tt.SetToolTip(_txtReplace, "Replacement text — applied to target text only");
            tt.SetToolTip(_btnReplace, "Replace the match in the selected result (active file only)");
            tt.SetToolTip(_btnReplaceAll, "Replace all target matches across all files");

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
                HeaderText = "Found in",
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
            _grid.CellClick += (s, ev) => { if (ev.RowIndex >= 0) UpdatePreview(); };

            // #104: right-click a termbase hit to open it in the term editor.
            _rowMenu = new ContextMenuStrip();
            _miEditTerm = new ToolStripMenuItem("Edit term\u2026");
            _miEditTerm.Click += OnEditTermClick;
            _rowMenu.Items.Add(_miEditTerm);
            _rowMenu.Opening += OnRowMenuOpening;
            // CellMouseClick, not CellMouseDown: a menu shown on mouse-down is
            // dismissed by the same click's mouse-up. The Termbase Editor's menu
            // uses CellMouseClick and works; this matches it.
            _grid.CellMouseClick += OnGridCellMouseClick;
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

            // ── Resize splitter (drag handle between grid and preview) ──
            var splitter = new Splitter
            {
                Dock = DockStyle.Bottom,
                Height = 4,
                BackColor = Color.FromArgb(230, 230, 230),
                MinExtra = 40,   // minimum grid height
                MinSize = 60     // minimum preview height
            };
            // Subtle hover effect on the drag bar
            splitter.MouseEnter += (s, ev) => splitter.BackColor = Color.FromArgb(180, 200, 220);
            splitter.MouseLeave += (s, ev) => splitter.BackColor = Color.FromArgb(230, 230, 230);

            // Thin top border line
            var previewBorder = new Panel
            {
                Dock = DockStyle.Top,
                Height = 1,
                BackColor = Color.FromArgb(200, 200, 200)
            };

            // Use a TableLayoutPanel for reliable side-by-side layout
            // 3 columns: source | vertical divider | target
            var previewTable = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 2,
                BackColor = Color.White,
                Margin = new Padding(0),
                Padding = new Padding(0),
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None
            };
            previewTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            previewTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1f));
            previewTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            previewTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 22f));
            previewTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            // Vertical divider between source and target (spans both rows)
            var verticalDivider = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(210, 210, 210),
                Margin = new Padding(0)
            };

            // Source header + text
            _lblPreviewSource = new Label
            {
                Text = "Source",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                ForeColor = SubtleColor,
                Padding = new Padding(4, 2, 0, 0),
                BackColor = Color.FromArgb(248, 249, 250)
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
                BackColor = Color.FromArgb(248, 249, 250)
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
            previewTable.Controls.Add(_lblPreviewTarget, 2, 0);
            previewTable.Controls.Add(_rtbPreviewSource, 0, 1);
            previewTable.Controls.Add(_rtbPreviewTarget, 2, 1);
            // Vertical divider spans both rows
            previewTable.Controls.Add(verticalDivider, 1, 0);
            previewTable.SetRowSpan(verticalDivider, 2);

            _previewPanel.Controls.Add(previewTable);
            _previewPanel.Controls.Add(previewBorder);

            BuildPreviewContextMenus();

            // Add controls in correct order for WinForms docking z-order:
            // Grid (Fill) must be added LAST / be in FRONT of z-order
            Controls.Add(_grid);
            Controls.Add(splitter);
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
            int txtY = (h - _txtSearch.Height) / 2;          // vertically center text boxes
            int chkY = (h - _chkCaseSensitive.Height) / 2;   // vertically center checkboxes
            int cboY = (h - _cboMode.Height) / 2;            // vertically center combo
            int lblY = (h - _lblSearchSource.Height) / 2;    // vertically center labels

            // Right-anchored controls first (right to left): ?, 🌐, Web, TBs, TMs, Files
            _btnHelp.Location = new Point(w - _btnHelp.Width - 4, btnY);
            _btnWebGo.Location = new Point(_btnHelp.Left - _btnWebGo.Width - 4, btnY);
            _btnWeb.Location = new Point(_btnWebGo.Left - _btnWeb.Width - 4, btnY);
            _btnTbs.Location = new Point(_btnWeb.Left - _btnTbs.Width - 4, btnY);
            _btnTms.Location = new Point(_btnTbs.Left - _btnTms.Width - 4, btnY);
            _btnFiles.Location = new Point(_btnTms.Left - _btnFiles.Width - 4, btnY);

            // Fixed controls from the right after the search boxes
            int fixedLeft = _btnFiles.Left;

            // Position from right: chkShowReplace, chkWholeWord, chkRegex, chkCaseSensitive, cboMode, btnStop, btnSearch
            _chkLiveFilter.Location = new Point(fixedLeft - _chkLiveFilter.Width - 4, chkY);
            _chkShowReplace.Location = new Point(_chkLiveFilter.Left - _chkShowReplace.Width - 4, chkY);
            _chkWholeWord.Location = new Point(_chkShowReplace.Left - _chkWholeWord.Width - 2, chkY);
            _chkRegex.Location = new Point(_chkWholeWord.Left - _chkRegex.Width - 2, chkY);
            _chkCaseSensitive.Location = new Point(_chkRegex.Left - _chkCaseSensitive.Width - 2, chkY);
            _cboMode.Location = new Point(_chkCaseSensitive.Left - _cboMode.Width - 6, cboY);
            _btnStop.Location = new Point(_cboMode.Left - _btnStop.Width - 4, btnY);
            _btnSearch.Location = new Point(_btnStop.Left - _btnSearch.Width - 2, btnY);

            // Two text boxes (Src: | Tgt:) split the remaining space on the left.
            int areaLeft = 4;
            int areaRight = _btnSearch.Left - 6;
            int available = Math.Max(160, areaRight - areaLeft);
            int boxesWidth = available
                - (_lblSearchSource.Width + 3) - (_lblSearchTarget.Width + 3) - 8;
            int eachBox = Math.Max(60, boxesWidth / 2);

            _lblSearchSource.Location = new Point(areaLeft, lblY);
            _txtSearch.Location = new Point(_lblSearchSource.Right + 3, txtY);
            _txtSearch.Width = eachBox;

            _lblSearchTarget.Location = new Point(_txtSearch.Right + 8, lblY);
            _txtSearchTarget.Location = new Point(_lblSearchTarget.Right + 3, txtY);
            _txtSearchTarget.Width = eachBox;

            // A box that was narrow when its text went in (down to 60 px here) and
            // has just been widened keeps the scroll it needed then: "droge stof"
            // showed as "roge stof" until the user pressed Home. A TextBox never
            // scrolls back by itself, so it is told to here.
            ResetHorizontalScroll(_txtSearch);
            ResetHorizontalScroll(_txtSearchTarget);
        }

        /// <summary>
        /// Scrolls a single-line TextBox back to its first character without
        /// disturbing the caret or selection. Setting the text with the caret at
        /// the end (SetSearchText focuses and selects all) scrolls the box to keep
        /// the caret visible when the text does not fit; once the box is wider the
        /// offset is stale but stays. Moving to 0 and back re-derives it from the
        /// current width: a selection that now fits ends up unscrolled, one that
        /// still does not scrolls only as far as it must.
        /// </summary>
        private static void ResetHorizontalScroll(TextBox box)
        {
            if (box == null || !box.IsHandleCreated || box.TextLength == 0) return;
            int start = box.SelectionStart, length = box.SelectionLength;
            box.Select(0, 0);
            box.ScrollToCaret();
            box.Select(start, length);
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
            SizeToText(_btnFiles);
        }

        /// <summary>
        /// Widens a scope button to fit its own caption.
        ///
        /// <para>The counts make these captions variable-width — "TBs (1)" and
        /// "Web (8/41)" differ by a lot — while the widths were fixed at creation.
        /// At higher DPI, or once a count reaches two digits, the text was
        /// clipped mid-caption. Shrinking is allowed too, so a button does not
        /// stay wide after its count drops.</para>
        /// </summary>
        private void SizeToText(Button button)
        {
            if (button == null) return;
            // Padding covers the flat border plus a little breathing room; measured
            // rather than guessed so it survives font scaling.
            var width = TextRenderer.MeasureText(button.Text, button.Font).Width + 16;
            if (button.Width == width) return;
            button.Width = width;
            LayoutSearchBar();   // right-anchored chain: every neighbour shifts
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
                    AutoSize = true,           // grow to fit the (scaled) text; fixed 28px clipped it on high-DPI
                    Padding = new Padding(8, 8, 8, 4),
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

                // Bottom bar: Select All / Select None / OK (AutoSize, DPI-safe).
                var btnSelectAll = CreateButton("Select All", dlg.Font, 90, 28);
                btnSelectAll.Click += (s, e) =>
                {
                    for (int i = 0; i < clb.Items.Count; i++)
                        clb.SetItemChecked(i, true);
                };

                var btnSelectNone = CreateButton("Select None", dlg.Font, 100, 28);
                btnSelectNone.Click += (s, e) =>
                {
                    for (int i = 0; i < clb.Items.Count; i++)
                        clb.SetItemChecked(i, false);
                };

                var btnOk = CreateButton("OK", dlg.Font, 70, 28);
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

                dlg.Controls.Add(BuildPickerBottomBar(btnSelectAll, btnSelectNone, btnOk, HeaderBg));
                dlg.AcceptButton = btnOk;

                // Fix z-order
                clb.BringToFront();

                dlg.ShowDialog(this);
            }
        }

        // ─── TM Selection ────────────────────────────────────────

        /// <summary>
        /// Updates the list of available project translation memories
        /// (called by the controller after it discovers the project's TMs).
        /// </summary>
        public void SetProjectTms(List<string> tms)
        {
            _allProjectTms = tms ?? new List<string>();
            // Drop any excluded TMs that are no longer attached to the project.
            _excludedTms.IntersectWith(_allProjectTms);
            UpdateTmsButton();
        }

        /// <summary>
        /// Gets the TMs to search (all project TMs minus the excluded ones).
        /// </summary>
        public List<string> GetSelectedTms()
        {
            if (_excludedTms.Count == 0)
                return _allProjectTms;

            return _allProjectTms.Where(t => !_excludedTms.Contains(t)).ToList();
        }

        private void UpdateTmsButton()
        {
            int included = _allProjectTms.Count - _excludedTms.Count;
            int total = _allProjectTms.Count;
            _btnTms.Text = included == total
                ? $"TMs ({total})"
                : $"TMs ({included}/{total})";
            SizeToText(_btnTms);
        }

        /// <summary>
        /// Updates the list of searchable termbases (called by the controller
        /// after discovery). Labels are "Name (Kind)" so a Supervertaler and a
        /// MultiTerm termbase of the same name remain distinguishable.
        /// </summary>
        public void SetProjectTermbases(List<string> termbases)
        {
            _allTermbases = termbases ?? new List<string>();
            // Drop exclusions for termbases that are no longer available (e.g.
            // the user unticked Read, or switched project).
            _excludedTermbases.IntersectWith(_allTermbases);
            UpdateTbsButton();
        }

        /// <summary>Termbases to search (all discovered minus the excluded).</summary>
        public List<string> GetSelectedTermbases()
        {
            if (_excludedTermbases.Count == 0)
                return _allTermbases;

            return _allTermbases.Where(t => !_excludedTermbases.Contains(t)).ToList();
        }

        private void UpdateTbsButton()
        {
            int included = _allTermbases.Count - _excludedTermbases.Count;
            int total = _allTermbases.Count;
            _btnTbs.Text = included == total
                ? $"TBs ({total})"
                : $"TBs ({included}/{total})";
            SizeToText(_btnTbs);
        }

        private void ShowTermbaseSelectionDialog()
        {
            if (_allTermbases.Count == 0)
            {
                MessageBox.Show(
                    "No termbases available to search." + Environment.NewLine + Environment.NewLine +
                    "SuperSearch searches the Supervertaler termbases whose Read tick is on, " +
                    "plus the MultiTerm/.ttb termbases enabled in Trados Project Settings. " +
                    "Run a search in Termbases or Everything mode first so they can be discovered.",
                    "SuperSearch", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new Form())
            {
                dlg.Text = "SuperSearch — Select Termbases";
                dlg.Size = new Size(600, 450);
                dlg.MinimumSize = new Size(400, 250);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.FormBorderStyle = FormBorderStyle.Sizable;
                dlg.ShowIcon = false;
                dlg.ShowInTaskbar = false;
                dlg.Font = new Font("Segoe UI", 9f);

                var lblInfo = new Label
                {
                    Text = "Select which termbases to include in the search:",
                    Dock = DockStyle.Top,
                    AutoSize = true,
                    Padding = new Padding(8, 8, 8, 4),
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

                foreach (var tb in _allTermbases)
                    clb.Items.Add(tb, !_excludedTermbases.Contains(tb));
                dlg.Controls.Add(clb);

                var btnSelectAll = CreateButton("Select All", dlg.Font, 90, 28);
                btnSelectAll.Click += (s, e) =>
                {
                    for (int i = 0; i < clb.Items.Count; i++)
                        clb.SetItemChecked(i, true);
                };

                var btnSelectNone = CreateButton("Select None", dlg.Font, 100, 28);
                btnSelectNone.Click += (s, e) =>
                {
                    for (int i = 0; i < clb.Items.Count; i++)
                        clb.SetItemChecked(i, false);
                };

                var btnOk = CreateButton("OK", dlg.Font, 70, 28);
                btnOk.Click += (s, e) =>
                {
                    _excludedTermbases.Clear();
                    for (int i = 0; i < clb.Items.Count; i++)
                    {
                        if (!clb.GetItemChecked(i))
                            _excludedTermbases.Add(_allTermbases[i]);
                    }
                    UpdateTbsButton();
                    dlg.DialogResult = DialogResult.OK;
                };

                dlg.Controls.Add(BuildPickerBottomBar(btnSelectAll, btnSelectNone, btnOk, HeaderBg));
                dlg.AcceptButton = btnOk;
                clb.BringToFront();
                dlg.ShowDialog(this);
            }
        }

        private void ShowTmSelectionDialog()
        {
            if (_allProjectTms.Count == 0)
            {
                MessageBox.Show(
                    "No translation memories found for this project.\n\n" +
                    "Attach a TM in the project's translation-memory settings, then search again.",
                    "SuperSearch", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new Form())
            {
                dlg.Text = "SuperSearch — Select Translation Memories";
                dlg.Size = new Size(600, 450);
                dlg.MinimumSize = new Size(400, 250);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.FormBorderStyle = FormBorderStyle.Sizable;
                dlg.ShowIcon = false;
                dlg.ShowInTaskbar = false;
                dlg.Font = new Font("Segoe UI", 9f);

                var lblInfo = new Label
                {
                    Text = "Select which translation memories to include in the search:",
                    Dock = DockStyle.Top,
                    AutoSize = true,           // grow to fit the (scaled) text; fixed 28px clipped it on high-DPI
                    Padding = new Padding(8, 8, 8, 4),
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

                foreach (var tm in _allProjectTms)
                {
                    var shortName = Core.TmSearcher.DisplayName(tm);
                    bool isChecked = !_excludedTms.Contains(tm);
                    clb.Items.Add(shortName, isChecked);
                }
                dlg.Controls.Add(clb);

                // Bottom bar: Select All / Select None / OK (AutoSize, DPI-safe).
                var btnSelectAll = CreateButton("Select All", dlg.Font, 90, 28);
                btnSelectAll.Click += (s, e) =>
                {
                    for (int i = 0; i < clb.Items.Count; i++)
                        clb.SetItemChecked(i, true);
                };

                var btnSelectNone = CreateButton("Select None", dlg.Font, 100, 28);
                btnSelectNone.Click += (s, e) =>
                {
                    for (int i = 0; i < clb.Items.Count; i++)
                        clb.SetItemChecked(i, false);
                };

                var btnOk = CreateButton("OK", dlg.Font, 70, 28);
                btnOk.Click += (s, e) =>
                {
                    _excludedTms.Clear();
                    for (int i = 0; i < clb.Items.Count; i++)
                    {
                        if (!clb.GetItemChecked(i))
                            _excludedTms.Add(_allProjectTms[i]);
                    }
                    UpdateTmsButton();
                    dlg.DialogResult = DialogResult.OK;
                };

                dlg.Controls.Add(BuildPickerBottomBar(btnSelectAll, btnSelectNone, btnOk, HeaderBg));
                dlg.AcceptButton = btnOk;

                // Fix z-order
                clb.BringToFront();

                dlg.ShowDialog(this);
            }
        }

        // ─── Web Resource Selection ──────────────────────────────

        /// <summary>
        /// Sets the web resource list (called by the controller from settings,
        /// already reconciled against the built-ins).
        /// </summary>
        public void SetWebResources(List<WebResource> resources)
        {
            _webResources = resources ?? new List<WebResource>();
            UpdateWebButton();
        }

        /// <summary>The full resource list, including disabled ones — the
        /// controller persists this verbatim so ordering and custom entries
        /// survive.</summary>
        public List<WebResource> GetWebResources()
        {
            return _webResources;
        }

        /// <summary>Just the resources that should actually be searched.</summary>
        public List<WebResource> GetEnabledWebResources()
        {
            return _webResources.Where(r => r != null && r.Enabled).ToList();
        }

        /// <summary>
        /// True when web results should open in the user's default browser
        /// rather than the embedded Supervertaler window. Set from the Web
        /// picker dialog; persisted by the controller alongside the resources.
        /// </summary>
        public bool WebResultsInBrowser { get; set; }

        private void UpdateWebButton()
        {
            int enabled = _webResources.Count(r => r != null && r.Enabled);
            int total = _webResources.Count;
            _btnWeb.Text = enabled == total
                ? $"Web ({total})"
                : $"Web ({enabled}/{total})";
            SizeToText(_btnWeb);
            // No point offering the trigger when everything is switched off.
            _btnWebGo.Enabled = enabled > 0;
        }

        private void ShowWebSelectionDialog()
        {
            if (_webResources.Count == 0)
            {
                MessageBox.Show("No web resources are configured.",
                    "SuperSearch", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new Form())
            {
                dlg.Text = "SuperSearch — Select Web Resources";
                dlg.Size = new Size(600, 560);
                dlg.MinimumSize = new Size(400, 300);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.FormBorderStyle = FormBorderStyle.Sizable;
                dlg.ShowIcon = false;
                dlg.ShowInTaskbar = false;
                dlg.Font = new Font("Segoe UI", 9f);

                var chkBrowser = new CheckBox
                {
                    Text = "Open results in my default browser instead of a Supervertaler window",
                    Checked = WebResultsInBrowser,
                    Dock = DockStyle.Top,
                    AutoSize = true,
                    Padding = new Padding(8, 4, 8, 8),
                    ForeColor = TextColor
                };
                var ttBrowser = new ToolTip();
                ttBrowser.SetToolTip(chkBrowser,
                    "Your own browser keeps your ad blocker and your signed-in sessions, "
                    + "but opens a new window each search.\n"
                    + "The Supervertaler window reuses one window and refreshes its tabs in place.");
                dlg.Controls.Add(chkBrowser);

                var lblInfo = new Label
                {
                    Text = "Tick the web resources to search.",
                    Dock = DockStyle.Top,
                    AutoSize = true,
                    Padding = new Padding(8, 8, 8, 4),
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

                foreach (var resource in _webResources)
                    clb.Items.Add(resource.ToString(), resource.Enabled);
                dlg.Controls.Add(clb);

                var btnSelectAll = CreateButton("Select All", dlg.Font, 90, 28);
                btnSelectAll.Click += (s, e) =>
                {
                    for (int i = 0; i < clb.Items.Count; i++)
                        clb.SetItemChecked(i, true);
                };

                var btnSelectNone = CreateButton("Select None", dlg.Font, 100, 28);
                btnSelectNone.Click += (s, e) =>
                {
                    for (int i = 0; i < clb.Items.Count; i++)
                        clb.SetItemChecked(i, false);
                };

                var btnOk = CreateButton("OK", dlg.Font, 70, 28);
                btnOk.Click += (s, e) =>
                {
                    for (int i = 0; i < clb.Items.Count && i < _webResources.Count; i++)
                        _webResources[i].Enabled = clb.GetItemChecked(i);
                    WebResultsInBrowser = chkBrowser.Checked;
                    UpdateWebButton();
                    WebResourcesChanged?.Invoke(this, EventArgs.Empty);
                    dlg.DialogResult = DialogResult.OK;
                };

                dlg.Controls.Add(BuildPickerBottomBar(btnSelectAll, btnSelectNone, btnOk, HeaderBg));
                dlg.AcceptButton = btnOk;

                clb.BringToFront();

                dlg.ShowDialog(this);
            }
        }

        /// <summary>
        /// Runs a web search for the given term, or for whatever is already in
        /// the boxes when <paramref name="term"/> is null. Public so the
        /// Ctrl+Alt+L action can drive it with the editor selection.
        /// </summary>
        /// <param name="fromTarget">True when the term came from the target
        /// side, which flips the language pair — looking up a Dutch word in an
        /// EN→NL project should search nl→en, or every resource returns nothing.</param>
        public void RunWebSearch(string term = null, bool fromTarget = false)
        {
            if (term != null)
            {
                var box = fromTarget ? _txtSearchTarget : _txtSearch;
                var other = fromTarget ? _txtSearch : _txtSearchTarget;
                box.Text = term;
                other.Text = "";
            }
            FireWebSearch();
        }

        private void FireWebSearch()
        {
            // Either box may hold the term. SetSearchText clears the opposite one,
            // so after Alt+S on a target selection the Src box is empty and only
            // Tgt is populated — reading Src alone made target-side lookups
            // silently do nothing. Src wins when both are set.
            var query = _txtSearch.Text?.Trim();
            var fromTarget = false;
            if (string.IsNullOrWhiteSpace(query))
            {
                query = _txtSearchTarget.Text?.Trim();
                fromTarget = !string.IsNullOrWhiteSpace(query);
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                SetStatus("Type a term in the Src or Tgt box to search the web.");
                return;
            }

            var enabled = GetEnabledWebResources();
            if (enabled.Count == 0)
            {
                MessageBox.Show(
                    "No web resources are switched on.\n\n"
                    + "Click the Web button to choose some.",
                    "SuperSearch", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            WebSearchRequested?.Invoke(this, new WebSearchRequestEventArgs
            {
                Query = query,
                Resources = enabled,
                FromTarget = fromTarget
            });
        }

        // ─── Public Methods ──────────────────────────────────────

        /// <summary>
        /// Populates the results grid with search results.
        /// Must be called on the UI thread.
        /// </summary>
        public void SetResults(List<SearchResult> results)
        {
            // Snapshot the full set so the Live filter can narrow it in-memory.
            _unfilteredResults = results;
            // Highlight each box's term in its own column/preview.
            _highlightSource = _txtSearch.Text;
            _highlightTarget = _txtSearchTarget.Text;
            _highlightCaseSensitive = _chkCaseSensitive.Checked;
            PopulateGrid(results);
        }

        /// <summary>
        /// Populates the results grid with search results (highlight terms are
        /// set by the caller via the source/target box state).
        /// </summary>
        private void PopulateGrid(List<SearchResult> results)
        {
            _grid.SuspendLayout();
            _grid.Rows.Clear();

            foreach (var r in results)
            {
                // TM concordance hits have no segment number; show the match
                // score in the # column instead and a "TM" status. Termbase
                // hits have neither a number nor a score – the # column stays
                // empty and the Status column carries the termbase kind.
                var isTm = r.Kind == ResultKind.TmEntry;
                var isTermbase = r.Kind == ResultKind.TermbaseEntry;
                var numCell = isTm
                    ? (r.MatchScore > 0 ? r.MatchScore + "%" : "")
                    : isTermbase
                        ? ""
                        : r.SegmentNumber.ToString();

                var idx = _grid.Rows.Add(r.FileName, numCell, r.SourceText, r.TargetText, r.Status);
                var row = _grid.Rows[idx];
                row.Tag = r;

                var fileCell = row.Cells["colFile"];
                fileCell.ToolTipText = isTm
                    ? "Translation memory: " + r.FilePath
                    : isTermbase
                        ? "Termbase: " + r.FilePath
                        : r.FilePath;
                if (isTm)
                {
                    // Tint the TM name blue so TM hits stand out from file rows.
                    fileCell.Style.ForeColor = TmNameColor;
                    fileCell.Style.SelectionForeColor = TmNameColor;
                }
                else if (isTermbase)
                {
                    // Green, matching the colour TermLens uses for MultiTerm
                    // chips, so terminology reads as terminology at a glance.
                    fileCell.Style.ForeColor = TermbaseNameColor;
                    fileCell.Style.SelectionForeColor = TermbaseNameColor;
                }

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

        /// <summary>True when the mode searches project files, i.e. when results
        /// can be navigated to and replaced in.</summary>
        internal static bool ModeIncludesFiles(SuperSearchSourceMode mode)
            => mode == SuperSearchSourceMode.ProjectFiles
            || mode == SuperSearchSourceMode.Everything;

        /// <summary>
        /// The currently selected search-source mode
        /// (Everything / Project files / TMs / Termbases).
        /// </summary>
        public SuperSearchSourceMode SelectedSourceMode
        {
            get
            {
                // Item order: 0 = Everything, 1 = Project files, 2 = TMs, 3 = Termbases.
                switch (_cboMode.SelectedIndex)
                {
                    case 1: return SuperSearchSourceMode.ProjectFiles;
                    case 2: return SuperSearchSourceMode.Tms;
                    case 3: return SuperSearchSourceMode.Termbases;
                    default: return SuperSearchSourceMode.Everything;
                }
            }
        }

        /// <summary>
        /// Restores the search-source mode dropdown (e.g. from persisted settings
        /// on startup). Does not raise <see cref="ModeChanged"/>.
        /// </summary>
        public void SetSourceMode(SuperSearchSourceMode mode)
        {
            // Item order: 0 = Everything, 1 = Project files, 2 = TMs, 3 = Termbases.
            int idx;
            switch (mode)
            {
                case SuperSearchSourceMode.ProjectFiles: idx = 1; break;
                case SuperSearchSourceMode.Tms: idx = 2; break;
                case SuperSearchSourceMode.Termbases: idx = 3; break;
                default: idx = 0; break; // Everything
            }

            // Suppress the ModeChanged event for a programmatic restore.
            var handler = ModeChanged;
            ModeChanged = null;
            _cboMode.SelectedIndex = idx;
            ModeChanged = handler;

            _chkShowReplace.Enabled = ModeIncludesFiles(mode);
            if (!_chkShowReplace.Enabled) _chkShowReplace.Checked = false;
        }

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
        /// Used by the context menu / Alt+S "SuperSearch" action.
        ///
        /// <paramref name="intoTargetBox"/> puts the text in the Tgt box instead
        /// of Src, for a selection the user made in the target segment: searching
        /// a target term as if it were source text finds nothing, since it does
        /// not appear on the source side at all (issue #57).
        ///
        /// The box that does NOT receive the text is cleared. Src and Tgt are
        /// ANDed, so a term left over from the previous search silently reduces
        /// the new one to zero hits; Alt+S starts a search rather than refining
        /// the last one.
        /// </summary>
        public void SetSearchText(string text, bool autoSearch = false, bool intoTargetBox = false)
        {
            var box   = intoTargetBox ? _txtSearchTarget : _txtSearch;
            var other = intoTargetBox ? _txtSearch : _txtSearchTarget;

            box.Text = text ?? "";
            other.Text = "";
            box.Focus();
            box.SelectAll();
            if (autoSearch && !string.IsNullOrWhiteSpace(text))
                FireSearch();
        }

        // ─── Cell Painting (search term highlighting) ────────────

        private void OnCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            // Only highlight Source and Target columns
            if (e.RowIndex < 0) return;
            if (e.ColumnIndex != 2 && e.ColumnIndex != 3) return; // colSource=2, colTarget=3

            // Source column highlights the Source-box term; Target column the
            // Target-box term.
            var query = e.ColumnIndex == 2 ? _highlightSource : _highlightTarget;
            if (string.IsNullOrEmpty(query)) return;

            var cellText = e.Value?.ToString();
            if (string.IsNullOrEmpty(cellText)) return;

            var comparison = _highlightCaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            // Check if the query appears in this cell
            int firstIdx = cellText.IndexOf(query, comparison);
            if (firstIdx < 0) return;

            // Let the grid paint the background and borders
            e.PaintBackground(e.ClipBounds, true);

            var font = e.CellStyle.Font ?? _grid.Font;
            var cellBounds = e.CellBounds;
            var isSelected = (e.State & DataGridViewElementStates.Selected) != 0;
            var fgColor = isSelected ? e.CellStyle.SelectionForeColor : e.CellStyle.ForeColor;

            // Text area (account for padding)
            var textRect = new Rectangle(
                cellBounds.X + 3, cellBounds.Y + 2,
                cellBounds.Width - 6, cellBounds.Height - 4);

            var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                        TextFormatFlags.SingleLine;

            // ── Paint yellow highlight backgrounds FIRST, then draw text once on
            //    top.  This avoids all text-on-text overlap and width mismatch
            //    issues that the previous overlay approach suffered from.

            // Use NoPadding for accurate character-level measurement
            var measureFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                               TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
                               TextFormatFlags.NoPadding;

            using (var highlightBrush = new SolidBrush(Color.FromArgb(255, 235, 120)))
            {
                int pos = 0;
                while (pos < cellText.Length)
                {
                    int idx = cellText.IndexOf(query, pos, comparison);
                    if (idx < 0) break;

                    // Measure text-before and text-through-match to get pixel span
                    var before = idx > 0 ? cellText.Substring(0, idx) : "";
                    var through = cellText.Substring(0, idx + query.Length);

                    int xBefore = idx > 0
                        ? TextRenderer.MeasureText(e.Graphics, before, font,
                              new Size(int.MaxValue, textRect.Height), measureFlags).Width
                        : 0;
                    int xThrough = TextRenderer.MeasureText(e.Graphics, through, font,
                        new Size(int.MaxValue, textRect.Height), measureFlags).Width;

                    var highlightRect = new Rectangle(
                        textRect.X + xBefore, textRect.Y,
                        xThrough - xBefore, textRect.Height);

                    highlightRect.Intersect(textRect);

                    if (highlightRect.Width > 0)
                        e.Graphics.FillRectangle(highlightBrush, highlightRect);

                    pos = idx + query.Length;
                }
            }

            // Draw the full text once – it renders on top of the yellow rects
            TextRenderer.DrawText(e.Graphics, cellText, font, textRect, fgColor, flags);

            e.Handled = true;
        }

        // ─── Preview Pane ────────────────────────────────────────

        private void OnGridSelectionChanged(object sender, EventArgs e) => UpdatePreview();

        private void UpdatePreview()
        {
            var result = GetSelectedResult();
            if (result == null)
            {
                _rtbPreviewSource.Text = "";
                _rtbPreviewTarget.Text = "";
                return;
            }

            PopulatePreview(_rtbPreviewSource, result.SourceText, _highlightSource);
            PopulatePreview(_rtbPreviewTarget, result.TargetText, _highlightTarget);
        }

        /// <summary>
        /// Sets the preview text and highlights the given term in yellow
        /// (source-box term in the source pane, target-box term in the target pane).
        /// </summary>
        private void PopulatePreview(RichTextBox rtb, string text, string query)
        {
            rtb.Text = text ?? "";

            if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(text))
                return;

            var comparison = _highlightCaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            int pos = 0;
            while (pos < text.Length)
            {
                int idx = text.IndexOf(query, pos, comparison);
                if (idx < 0) break;

                rtb.Select(idx, query.Length);
                rtb.SelectionBackColor = Color.FromArgb(255, 235, 120);
                pos = idx + query.Length;
            }

            rtb.Select(0, 0);
        }

        // ─── Preview Copy / Selection ────────────────────────────

        // The preview RichTextBoxes are ReadOnly, which still allows mouse
        // selection and Ctrl+C, but gives no right-click menu and no Ctrl+A.
        // Wire both up so a translator can copy a prior translation verbatim.
        private void BuildPreviewContextMenus()
        {
            _rtbPreviewSource.ContextMenuStrip = CreatePreviewContextMenu(_rtbPreviewSource);
            _rtbPreviewTarget.ContextMenuStrip = CreatePreviewContextMenu(_rtbPreviewTarget);

            _rtbPreviewSource.KeyDown += OnPreviewKeyDown;
            _rtbPreviewTarget.KeyDown += OnPreviewKeyDown;
        }

        private ContextMenuStrip CreatePreviewContextMenu(RichTextBox owner)
        {
            var menu = new ContextMenuStrip();

            var copyItem = new ToolStripMenuItem("Copy") { ShortcutKeyDisplayString = "Ctrl+C" };
            copyItem.Click += (s, e) =>
            {
                if (owner.SelectionLength > 0)
                    owner.Copy();
                else if (!string.IsNullOrEmpty(owner.Text))
                    Clipboard.SetText(owner.Text);
            };
            menu.Items.Add(copyItem);

            var selectAllItem = new ToolStripMenuItem("Select All") { ShortcutKeyDisplayString = "Ctrl+A" };
            selectAllItem.Click += (s, e) => owner.SelectAll();
            menu.Items.Add(selectAllItem);

            menu.Items.Add(new ToolStripSeparator());

            var copySourceItem = new ToolStripMenuItem("Copy source");
            copySourceItem.Click += (s, e) =>
            {
                if (!string.IsNullOrEmpty(_rtbPreviewSource.Text))
                    Clipboard.SetText(_rtbPreviewSource.Text);
            };
            menu.Items.Add(copySourceItem);

            var copyTargetItem = new ToolStripMenuItem("Copy target");
            copyTargetItem.Click += (s, e) =>
            {
                if (!string.IsNullOrEmpty(_rtbPreviewTarget.Text))
                    Clipboard.SetText(_rtbPreviewTarget.Text);
            };
            menu.Items.Add(copyTargetItem);

            menu.Opening += (s, e) =>
            {
                copyItem.Enabled = owner.SelectionLength > 0 || !string.IsNullOrEmpty(owner.Text);
                selectAllItem.Enabled = !string.IsNullOrEmpty(owner.Text);
                copySourceItem.Enabled = !string.IsNullOrEmpty(_rtbPreviewSource.Text);
                copyTargetItem.Enabled = !string.IsNullOrEmpty(_rtbPreviewTarget.Text);
            };

            return menu;
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.A)
            {
                (sender as RichTextBox)?.SelectAll();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        // ─── Private Helpers ─────────────────────────────────────

        private void OnLiveFilterTextChanged()
        {
            // Only narrows an existing result set; a fresh full search still
            // happens on Enter / Search. Debounced so it doesn't run per keystroke.
            if (_chkLiveFilter == null || !_chkLiveFilter.Checked) return;
            if (_unfilteredResults == null) return;
            _liveFilterTimer.Stop();
            _liveFilterTimer.Start();
        }

        private void ApplyLiveFilter()
        {
            if (_unfilteredResults == null) return;

            string src = _txtSearch.Text, tgt = _txtSearchTarget.Text;
            bool cs = _chkCaseSensitive.Checked, rx = _chkRegex.Checked, ww = _chkWholeWord.Checked;

            var filtered = _unfilteredResults.Where(r =>
                LiveMatches(r.SourceText, src, cs, rx, ww) &&
                LiveMatches(r.TargetText, tgt, cs, rx, ww)).ToList();

            _highlightSource = src;
            _highlightTarget = tgt;
            _highlightCaseSensitive = cs;
            PopulateGrid(filtered);
            SetStatus($"{filtered.Count} of {_unfilteredResults.Count} result(s) (live filter)");
        }

        /// <summary>Client-side match used only by the live filter. An empty
        /// query is no constraint; an invalid regex (while typing) matches nothing.</summary>
        private static bool LiveMatches(string text, string query,
            bool caseSensitive, bool useRegex, bool wholeWord)
        {
            if (string.IsNullOrEmpty(query)) return true;
            if (string.IsNullOrEmpty(text)) return false;
            try
            {
                if (useRegex)
                    return Regex.IsMatch(text, query,
                        caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
                if (wholeWord)
                    return Regex.IsMatch(text, @"\b" + Regex.Escape(query) + @"\b",
                        caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
                return text.IndexOf(query,
                    caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        private void FireSearch()
        {
            // At least one box must be filled.
            if (string.IsNullOrWhiteSpace(_txtSearch.Text)
                && string.IsNullOrWhiteSpace(_txtSearchTarget.Text)) return;

            SearchRequested?.Invoke(this, new SearchRequestEventArgs
            {
                SourceQuery = _txtSearch.Text,
                TargetQuery = _txtSearchTarget.Text,
                CaseSensitive = _chkCaseSensitive.Checked,
                UseRegex = _chkRegex.Checked,
                WholeWord = _chkWholeWord.Checked,
                SourceMode = SelectedSourceMode
            });
        }

        private void FireReplace()
        {
            var result = GetSelectedResult();
            if (result == null) return;

            // Replace operates on the target text, so the find term is the
            // Target box.
            ReplaceRequested?.Invoke(this, new ReplaceRequestEventArgs
            {
                SearchText = _txtSearchTarget.Text,
                ReplaceText = _txtReplace.Text,
                CaseSensitive = _chkCaseSensitive.Checked,
                UseRegex = _chkRegex.Checked,
                WholeWord = _chkWholeWord.Checked,
                SelectedResult = result
            });
        }

        private void FireReplaceAll()
        {
            ReplaceAllRequested?.Invoke(this, new ReplaceRequestEventArgs
            {
                SearchText = _txtSearchTarget.Text,
                ReplaceText = _txtReplace.Text,
                CaseSensitive = _chkCaseSensitive.Checked,
                UseRegex = _chkRegex.Checked,
                WholeWord = _chkWholeWord.Checked
            });
        }

        // ─── #104: edit a termbase hit ──────────────────────────────────

        private ContextMenuStrip _rowMenu;
        private ToolStripMenuItem _miEditTerm;

        private void OnGridCellMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;

            // Right-click selects the row under the pointer, as everywhere else.
            var col = Math.Max(0, e.ColumnIndex);
            _grid.ClearSelection();
            _grid.Rows[e.RowIndex].Selected = true;
            try { _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[col]; } catch { }

            var rect = _grid.GetCellDisplayRectangle(col, e.RowIndex, true);
            _rowMenu.Show(_grid, rect.Left + e.X, rect.Top + e.Y);
        }

        private void OnRowMenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // The menu always appears and the item is always there: a right-click
            // that shows nothing is indistinguishable from a broken one. Whether the
            // hit can actually be edited is decided on the click, which explains
            // itself when it cannot - a disabled ToolStrip item shows no tooltip, so
            // greying it out would have said nothing either.
            var r = SelectedResult();
            _miEditTerm.Text = r != null && r.Kind == ResultKind.TermbaseEntry
                ? "Edit term…"
                : "Edit term… (termbase entries only)";
        }

        private SearchResult SelectedResult()
        {
            return _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].Tag as SearchResult : null;
        }

        private static bool CanEditTerm(SearchResult r, out string why)
        {
            why = null;
            if (r == null || r.Kind != ResultKind.TermbaseEntry)
            {
                why = "Only termbase entries can be edited from here.";
                return false;
            }
            if (r.Term != null && r.Term.IsMultiTerm)
            {
                why = "MultiTerm and .ttb termbases are read-only in Supervertaler. Edit this entry in MultiTerm.";
                return false;
            }
            if (r.Term == null)
            {
                why = "This termbase was read straight from the database (TermLens had not finished loading it), " +
                      "so the hit does not carry the entry. Open the termbase in the Termbase Editor to edit it, " +
                      "or search again once TermLens has loaded.";
                return false;
            }
            if (!TermLensEditorViewPart.HasActiveInstance)
            {
                why = "Open a document in the editor first \u2013 the term editor works through TermLens.";
                return false;
            }
            return true;
        }

        private void OnEditTermClick(object sender, EventArgs e)
        {
            var r = SelectedResult();
            if (!CanEditTerm(r, out var why))
            {
                MessageBox.Show(FindForm(), why, "Supervertaler \u2014 Edit term",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // The same path as the chip's "Edit Term\u2026": resolves the termbase,
            // opens the dialog, and on save updates TermLens's index and display.
            TermLensEditorViewPart.HandleEditCurrentTerm(r.Term, null);

            // Results come from memory, so re-running the search is instant and
            // the row shows the edited text.
            FireSearch();
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
            // Treat the supplied size as a MINIMUM and let the button grow to
            // fit its label. Fixed widths are only right at one font/DPI
            // combination: at the user's scaling "Stop" was clipped to "Sto",
            // the same way "Select None" once clipped to "Select" in the picker
            // bar. Growing-only keeps the intended layout everywhere it already
            // fits, and rescues it everywhere it doesn't.
            btn.MinimumSize = new Size(width, height);
            btn.AutoSize = true;
            btn.AutoSizeMode = AutoSizeMode.GrowOnly;
            btn.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
            btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(230, 230, 230);
            return btn;
        }

        /// <summary>
        /// Lays out the "Select All / Select None / OK" bottom bar shared by the
        /// picker dialogs. Uses a TableLayoutPanel of <b>AutoSize</b> buttons so the
        /// button labels and the bar height grow with the font on high-DPI / high-
        /// resolution screens instead of clipping (the fixed-width version truncated
        /// "Select None" to "Select"). Select All / None sit on the left; a spring
        /// column pushes OK to the right.
        /// </summary>
        private static TableLayoutPanel BuildPickerBottomBar(
            Button btnSelectAll, Button btnSelectNone, Button btnOk, Color headerBg)
        {
            foreach (var b in new[] { btnSelectAll, btnSelectNone, btnOk })
            {
                b.MinimumSize = b.Size;                 // keep the base size as a floor
                b.AutoSize = true;                      // but grow to fit scaled text
                b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                b.Margin = new Padding(0, 0, 6, 0);
                b.Anchor = AnchorStyles.Left;
            }
            btnOk.Margin = new Padding(0);
            btnOk.Anchor = AnchorStyles.Right;

            var bar = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 4,
                RowCount = 1,
                BackColor = headerBg,
                Padding = new Padding(8, 6, 8, 6)
            };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); // Select All
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); // Select None
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f)); // spring
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); // OK
            bar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            bar.Controls.Add(btnSelectAll, 0, 0);
            bar.Controls.Add(btnSelectNone, 1, 0);
            bar.Controls.Add(btnOk, 3, 0);
            return bar;
        }
    }

    // ─── Event Argument Classes ──────────────────────────────

    public class SearchRequestEventArgs : EventArgs
    {
        // Dual-box search: either query may be empty. A segment matches when its
        // source contains SourceQuery (if set) AND its target contains
        // TargetQuery (if set). At least one is non-empty.
        public string SourceQuery { get; set; }
        public string TargetQuery { get; set; }
        public bool CaseSensitive { get; set; }
        public bool UseRegex { get; set; }
        public bool WholeWord { get; set; }
        public SuperSearchSourceMode SourceMode { get; set; }
    }

    public class ReplaceRequestEventArgs : EventArgs
    {
        public string SearchText { get; set; }
        public string ReplaceText { get; set; }
        public bool CaseSensitive { get; set; }
        public bool UseRegex { get; set; }
        public bool WholeWord { get; set; }
        public SearchResult SelectedResult { get; set; }
    }

    /// <summary>
    /// A request to run <see cref="Query"/> against <see cref="Resources"/>.
    /// The language pair is deliberately absent: only the controller can see the
    /// active document, so it fills that in.
    /// </summary>
    public class WebSearchRequestEventArgs : EventArgs
    {
        public string Query { get; set; }
        public List<WebResource> Resources { get; set; }

        /// <summary>True when the term came from the target side, so the
        /// controller should search target→source instead.</summary>
        public bool FromTarget { get; set; }
    }
}
