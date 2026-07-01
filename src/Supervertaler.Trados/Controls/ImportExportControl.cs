using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Core.Export;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// Tab UI for the new "Import / Export" feature in the Supervertaler
    /// Assistant pane. Lets the translator:
    ///
    /// - Export the active Trados document's bilingual segments to one of
    ///   three formats (DOCX, Markdown, HTML) in one of three layouts
    ///   (Supervertaler Bilingual Table, stacked source-on-top, stacked
    ///   target-on-top).
    /// - Round-trip a previously-exported DOCX or Markdown file back into
    ///   the active document, updating target segments where the file
    ///   contains edits.
    /// - Browse recent exports.
    ///
    /// The control is dumb-by-design — it fires events for export / import /
    /// open-folder / open-file and the hosting <c>AiAssistantViewPart</c>
    /// does the Trados SDK plumbing.
    /// </summary>
    public class ImportExportControl : UserControl
    {
        // Config controls.
        private ComboBox _cmbFormat;
        private Label _lblRoundTripStatus;   // dynamic "can / cannot be re-imported" hint
        private CheckBox _chkStrictTagCheck;
        private CheckBox _chkIncludeLocked;
        private System.Collections.Generic.List<CheckBox> _statusCheckBoxes;
        private Label _lblSegmentCount;

        // Multi-file controls (shown only when active document contains
        // more than one file). Hidden + collapsed when single-file.
        private Label _lblFilesHeading;
        private Panel _pnlFiles;                       // scrollable container for the per-file checkbox rows
        private readonly List<CheckBox> _fileCheckBoxes = new List<CheckBox>();
        private Button _btnSelectActive;
        private Button _btnSelectAll;
        private Button _btnSelectNone;
        private Label _lblOutputMode;
        private RadioButton _rbCombineOne;
        private RadioButton _rbSeparatePerFile;
        private string _activeFileId;          // file id we treat as "the active one" for the [Active] quick-select

        // Action buttons.
        private Button _btnExport;
        private Button _btnImport;

        // History list.
        private Label _lblHistoryHeading;
        private ListView _lvHistory;
        private Button _btnOpenFile;
        private Button _btnOpenFolder;
        private Button _btnReImportSelected;

        // Log.
        private Label _lblLog;
        private TextBox _txtLog;

        // State.
        private int _totalSegments;

        // ─── Public events ────────────────────────────────────────────

        /// <summary>Fired when the user clicks the Export button.
        /// The handler is responsible for collecting segments from Trados,
        /// invoking <see cref="BilingualExporter"/>, and recording the
        /// result via <see cref="AddHistoryEntry"/>.</summary>
        public event EventHandler<ExportRequestedEventArgs> ExportRequested;

        /// <summary>Fired when the user picks a file to re-import (either via
        /// the "Re-import…" button or the history pane). The handler runs
        /// <see cref="BilingualImporter"/> and applies the resulting diffs to
        /// the active Trados document.</summary>
        public event EventHandler<ImportRequestedEventArgs> ImportRequested;

        /// <summary>Fired when the user clicks "Open file" on a history entry.</summary>
        public event EventHandler<string> OpenFileRequested;

        /// <summary>Fired when the user clicks "Open folder" on a history entry.</summary>
        public event EventHandler<string> OpenFolderRequested;

        // ─── Construction ─────────────────────────────────────────────

        public ImportExportControl()
        {
            InitializeUI();
        }

        /// <summary>
        /// Widen a drop-down list's popup so every item shows in full. WinForms
        /// otherwise clips the popup to the control's width, which truncates the
        /// longer Format/Layout names (and it's worse at high DPI / large fonts,
        /// e.g. on a laptop). Measured with the control's own font so it stays
        /// DPI- and theme-correct.
        /// </summary>
        private static void SizeDropDownToFitItems(ComboBox cmb)
        {
            int widest = cmb.Width;
            foreach (var item in cmb.Items)
            {
                int w = TextRenderer.MeasureText(item?.ToString() ?? string.Empty, cmb.Font).Width;
                if (w > widest) widest = w;
            }
            // Pad for the list's internal margins plus a possible scrollbar.
            cmb.DropDownWidth = widest + SystemInformation.VerticalScrollBarWidth + UiScale.Pixels(8);
        }

        private void InitializeUI()
        {
            SuspendLayout();
            BackColor = Color.White;
            Font = new Font("Segoe UI", UiScale.FontSize(8.5f));

            // Enable vertical scrolling so the tab still works on laptop-
            // sized screens where the log textbox would otherwise be off
            // the bottom of the panel. AutoScrollMinSize is set after all
            // child controls are added (see the bottom of this method)
            // so WinForms knows how tall the virtual canvas needs to be.
            AutoScroll = true;

            var bodyFont = new Font("Segoe UI", UiScale.FontSize(8.5f));
            var labelColor = Color.FromArgb(60, 60, 60);
            int leftMargin = UiScale.Pixels(12);
            int y = UiScale.Pixels(10);

            // ─── Header ──────────────────────────────────────────────
            // The panel-level "?" button at the very top-right of the
            // Supervertaler Assistant pane is tab-aware (see
            // AiAssistantControl.OnHelpDropdown) and routes to
            // HelpSystem.Topics.ImportExport when this tab is active —
            // so we don't add a per-tab help button here.
            var lblHeader = new Label
            {
                Text = "Import/Export",
                Location = new Point(leftMargin, y),
                AutoSize = true,
                Font = new Font("Segoe UI", UiScale.FontSize(11f), FontStyle.Bold),
                ForeColor = Color.FromArgb(20, 20, 20)
            };
            Controls.Add(lblHeader);
            y += UiScale.Pixels(28);

            var lblBlurb = new Label
            {
                Text = "Export bilingual files for proofreaders or clients, then re-import edits back into Trados.",
                Location = new Point(leftMargin, y),
                AutoSize = false,
                Width = UiScale.Pixels(540),
                Height = UiScale.Pixels(32),
                Font = bodyFont,
                ForeColor = labelColor
            };
            Controls.Add(lblBlurb);
            y += UiScale.Pixels(36);

            // Tag-handling hint. Inline formatting (bold, italic, field
            // codes, page numbers) is serialised as numbered <t1>...</t1>
            // / <t2/> placeholders in the cell text. The proofreader can
            // move the markers around the translated text; on re-import
            // Trados puts the corresponding tags back where they ended up.
            // Removing a marker drops that formatting; mismatched or
            // unknown markers fall back to plain-text writeback with a
            // warning in the log.
            var lblTagCaveat = new Label
            {
                Text = "Inline formatting shows as <b>...</b>, <i>...</i>, <u>...</u> markers " +
                       "(or <t1>...</t1> for field codes / page numbers). Reorder markers around " +
                       "your translation; remove a pair to drop that formatting.",
                Location = new Point(leftMargin, y),
                AutoSize = false,
                Width = UiScale.Pixels(540),
                Height = UiScale.Pixels(56),
                Font = new Font("Segoe UI", UiScale.FontSize(8f), FontStyle.Italic),
                ForeColor = Color.FromArgb(60, 90, 140)
            };
            Controls.Add(lblTagCaveat);
            y += UiScale.Pixels(60);

            // ─── Format row ──────────────────────────────────────────
            var lblFormat = new Label
            {
                Text = "Format:",
                Location = new Point(leftMargin, y + UiScale.Pixels(4)),
                AutoSize = false,
                Width = UiScale.Pixels(70),
                Font = bodyFont,
                ForeColor = labelColor
            };
            Controls.Add(lblFormat);

            _cmbFormat = new ComboBox
            {
                Location = new Point(leftMargin + UiScale.Pixels(72), y),
                Width = UiScale.Pixels(220),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = bodyFont
            };
            _cmbFormat.Items.Add("Word document (.docx)");
            _cmbFormat.Items.Add("Bilingual Text (AI-friendly) (.txt)");
            _cmbFormat.Items.Add("HTML report (.html, read-only)");
            _cmbFormat.SelectedIndex = 0;
            SizeDropDownToFitItems(_cmbFormat);
            Controls.Add(_cmbFormat);
            y += UiScale.Pixels(30);

            // Layout is no longer user-selectable: each format has exactly one
            // layout (DOCX/HTML → 5-column Bilingual Table; Text → bracketed
            // [SEGMENT NNNN]). The stacked layouts and the standalone Markdown
            // format were retired to keep the export choices simple.

            // ─── Re-import status line ───────────────────────────────
            // Tells the user, for the current Format + Layout, whether the
            // exported file can be edited and round-tripped back into Trados.
            _lblRoundTripStatus = new Label
            {
                Location = new Point(leftMargin + UiScale.Pixels(72), y),
                AutoSize = false,
                // Wide + tall enough for the longest message to wrap to ~3 lines
                // without clipping (the box has AutoSize off, so it clips text
                // that overflows its bounds — see the earlier truncation bug).
                Width = UiScale.Pixels(540),
                Height = UiScale.Pixels(46),
                Font = new Font("Segoe UI", UiScale.FontSize(8f))
            };
            Controls.Add(_lblRoundTripStatus);
            _cmbFormat.SelectedIndexChanged += (s, e) => UpdateRoundTripStatus();
            UpdateRoundTripStatus();
            y += UiScale.Pixels(50);

            // ─── Strict tag-integrity check ──────────────────────────
            // Default ON: re-import refuses to apply a segment when the
            // proofreader's edit has fewer tag markers than the source has
            // tags — applying it would create a Trados QA failure (source
            // tags must appear in target). Power users can toggle this off
            // when they're intentionally stripping tags.
            _chkStrictTagCheck = new CheckBox
            {
                Text = "Refuse to apply edits that drop source-required tags (recommended)",
                Location = new Point(leftMargin, y),
                AutoSize = true,
                Font = bodyFont,
                ForeColor = labelColor,
                Checked = true
            };
            var strictTip = new ToolTip { AutoPopDelay = 14000, InitialDelay = 300 };
            strictTip.SetToolTip(_chkStrictTagCheck,
                "When ON (default): if the proofreader's edit has fewer tag markers " +
                "than the source segment has tags, that segment is reported as a " +
                "tag-mismatch issue and NOT written to Trados. Applying it would " +
                "create a Trados QA failure (source tags must appear in target).\r\n\r\n" +
                "When OFF: tag-mismatched edits are applied verbatim with a per-segment " +
                "warning in the log. Only turn this off if you're intentionally " +
                "stripping tags and you know the consequences.");
            Controls.Add(_chkStrictTagCheck);
            y += UiScale.Pixels(24);

            // v4.20.18: include locked segments toggle. Default ON
            // matches pre-v4.20.18 behaviour (everything exported). When
            // OFF, locked segments are skipped entirely — useful on
            // large projects where most of the work is locked-approved
            // and the proofreader should only see what's still editable.
            // When ON, locked rows get a 🔒 prefix in the Status column
            // so the proofreader can see at a glance which ones won't
            // round-trip back to Trados.
            _chkIncludeLocked = new CheckBox
            {
                Text = "Include locked segments (🔒 marked in Status column)",
                Location = new Point(leftMargin, y),
                AutoSize = true,
                Font = bodyFont,
                ForeColor = labelColor,
                Checked = true
            };
            var lockTip = new ToolTip { AutoPopDelay = 14000, InitialDelay = 300 };
            lockTip.SetToolTip(_chkIncludeLocked,
                "When ON (default): locked segments are exported alongside everything " +
                "else and visually marked with 🔒 in the Status column. Re-import " +
                "will refuse to overwrite them (they show up as the 'locked' counter " +
                "in the re-import summary's 'other issues' line).\r\n\r\n" +
                "When OFF: locked segments are skipped entirely. Useful on large " +
                "projects where most segments are locked-approved and the proofreader " +
                "should only see what's actually still editable.");
            Controls.Add(_chkIncludeLocked);
            y += UiScale.Pixels(24);

            // v4.20.24: confirmation-status filter. A row of checkboxes —
            // one per Trados ConfirmationLevel — that lets the user
            // include only segments in chosen statuses. All checked by
            // default = no filter (same as pre-v4.20.24 behaviour). The
            // labels here MUST match what pair.Properties.ConfirmationLevel
            // .ToString() returns at export time, since the collector
            // compares them verbatim (case-insensitive).
            var lblStatuses = new Label
            {
                Text = "Statuses to include in export:",
                Location = new Point(leftMargin, y),
                AutoSize = true,
                Font = new Font("Segoe UI", UiScale.FontSize(9f), FontStyle.Bold),
                ForeColor = labelColor
            };
            Controls.Add(lblStatuses);
            y += UiScale.Pixels(20);

            // Two rows of 3 checkboxes. Order matches the Trados
            // ConfirmationLevel enum's natural progression — unconfirmed
            // → reviewed → signed off → rejected as an outlier.
            string[] statusEnumNames = {
                "Unspecified", "Draft", "Translated",
                "ApprovedTranslation", "ApprovedSignOff", "Rejected"
            };
            string[] statusLabels = {
                "Unspecified", "Draft", "Translated",
                "Approved (translation)", "Approved (sign-off)", "Rejected"
            };
            _statusCheckBoxes = new System.Collections.Generic.List<CheckBox>();
            int colWidth = UiScale.Pixels(180);
            int rowHeight = UiScale.Pixels(22);
            for (int i = 0; i < statusEnumNames.Length; i++)
            {
                int col = i % 3;
                int row = i / 3;
                var cb = new CheckBox
                {
                    Text = statusLabels[i],
                    Tag = statusEnumNames[i],   // raw enum name for comparison
                    Location = new Point(leftMargin + col * colWidth, y + row * rowHeight),
                    AutoSize = true,
                    Checked = true,
                    Font = bodyFont,
                    ForeColor = labelColor,
                    TabStop = false,
                    FlatStyle = FlatStyle.System,
                    UseVisualStyleBackColor = true
                };
                Controls.Add(cb);
                _statusCheckBoxes.Add(cb);
            }
            y += rowHeight * 2 + UiScale.Pixels(6);

            // ─── Multi-file controls (hidden when single-file) ───────
            // Trados projects can have many files; users can also open
            // multiple files together as a "merged" document. When the
            // active document contains more than one file, we surface a
            // checklist + output-mode chooser. Single-file documents
            // hide all of this and behave like before.
            _lblFilesHeading = new Label
            {
                Text = "Files to export:",
                Location = new Point(leftMargin, y),
                AutoSize = true,
                Font = new Font("Segoe UI", UiScale.FontSize(9f), FontStyle.Bold),
                ForeColor = labelColor,
                Visible = false
            };
            Controls.Add(_lblFilesHeading);
            y += UiScale.Pixels(20);

            // Panel of CheckBox rows (replaces CheckedListBox in v4.20.8 —
            // CheckedListBox's distinct "highlighted" vs "checked" states
            // confused users; a plain checkbox per row has no such
            // ambiguity. Auto-scrolls vertically when the file count
            // exceeds the visible area.
            _pnlFiles = new Panel
            {
                Location = new Point(leftMargin, y),
                Width = UiScale.Pixels(540),
                Height = UiScale.Pixels(110),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                AutoScroll = true,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = SystemColors.Window,
                Visible = false
            };
            Controls.Add(_pnlFiles);
            y += _pnlFiles.Height + UiScale.Pixels(4);

            // Quick-select buttons row.
            _btnSelectActive = new Button
            {
                Text = "Active only",
                Location = new Point(leftMargin, y),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowOnly,
                MinimumSize = new Size(UiScale.Pixels(90), UiScale.Pixels(24)),
                FlatStyle = FlatStyle.System,
                Font = bodyFont,
                Visible = false
            };
            _btnSelectActive.Click += (s, e) => SelectActiveFileOnly();
            var selectActiveTip = new ToolTip { AutoPopDelay = 10000, InitialDelay = 300 };
            selectActiveTip.SetToolTip(_btnSelectActive,
                "Select only the file your cursor is currently on in the Trados editor. " +
                "Quick way to export just the one file you're actively translating from " +
                "a multi-file merged document, without unchecking the others manually.");
            Controls.Add(_btnSelectActive);

            _btnSelectAll = new Button
            {
                Text = "All",
                Location = new Point(_btnSelectActive.Right + UiScale.Pixels(6), y),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowOnly,
                MinimumSize = new Size(UiScale.Pixels(60), UiScale.Pixels(24)),
                FlatStyle = FlatStyle.System,
                Font = bodyFont,
                Visible = false
            };
            _btnSelectAll.Click += (s, e) => CheckAllFiles(true);
            Controls.Add(_btnSelectAll);

            _btnSelectNone = new Button
            {
                Text = "None",
                Location = new Point(_btnSelectAll.Right + UiScale.Pixels(6), y),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowOnly,
                MinimumSize = new Size(UiScale.Pixels(60), UiScale.Pixels(24)),
                FlatStyle = FlatStyle.System,
                Font = bodyFont,
                Visible = false
            };
            _btnSelectNone.Click += (s, e) => CheckAllFiles(false);
            Controls.Add(_btnSelectNone);
            y += UiScale.Pixels(32);

            // Output mode radios.
            _lblOutputMode = new Label
            {
                Text = "Output:",
                Location = new Point(leftMargin, y + UiScale.Pixels(2)),
                AutoSize = true,
                Font = bodyFont,
                ForeColor = labelColor,
                Visible = false
            };
            Controls.Add(_lblOutputMode);

            _rbCombineOne = new RadioButton
            {
                Text = "Combine into one file",
                Location = new Point(leftMargin + UiScale.Pixels(60), y),
                AutoSize = true,
                Font = bodyFont,
                Checked = true,
                Visible = false
            };
            Controls.Add(_rbCombineOne);

            _rbSeparatePerFile = new RadioButton
            {
                Text = "Separate file per file",
                // .Right at construction time hasn't been widened by
                // AutoSize yet, so we'd end up overlapping the previous
                // radio. Use a fixed offset that's wide enough for the
                // "Combine into one file" label at typical font/dpi.
                Location = new Point(leftMargin + UiScale.Pixels(60) + UiScale.Pixels(190), y),
                AutoSize = true,
                Font = bodyFont,
                Visible = false
            };
            Controls.Add(_rbSeparatePerFile);

            // Belt-and-suspenders: once Combine has been auto-sized,
            // re-anchor Separate to flush-right of it. Same pattern as
            // the Copy/Paste fix in BatchTranslateControl.
            _rbCombineOne.SizeChanged += (s, e) =>
            {
                _rbSeparatePerFile.Location = new Point(
                    _rbCombineOne.Right + UiScale.Pixels(16),
                    _rbSeparatePerFile.Location.Y);
            };
            y += UiScale.Pixels(28);

            // ─── Segment count + action buttons ──────────────────────
            _lblSegmentCount = new Label
            {
                Text = "Segments: 0",
                Location = new Point(leftMargin, y + UiScale.Pixels(4)),
                AutoSize = true,
                Font = bodyFont,
                ForeColor = labelColor
            };
            Controls.Add(_lblSegmentCount);

            _btnExport = new Button
            {
                Text = "📤  Export",
                Location = new Point(leftMargin + UiScale.Pixels(150), y),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowOnly,
                MinimumSize = new Size(UiScale.Pixels(110), UiScale.Pixels(28)),
                FlatStyle = FlatStyle.System,
                Font = bodyFont
            };
            _btnExport.Click += (s, e) => RaiseExport();
            Controls.Add(_btnExport);

            _btnImport = new Button
            {
                Text = "📥  Re-import…",
                Location = new Point(_btnExport.Right + UiScale.Pixels(8), y),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowOnly,
                MinimumSize = new Size(UiScale.Pixels(140), UiScale.Pixels(28)),
                FlatStyle = FlatStyle.System,
                Font = bodyFont
            };
            _btnImport.Click += (s, e) => OnImportButton();
            Controls.Add(_btnImport);
            y += UiScale.Pixels(40);

            // ─── History heading + list ──────────────────────────────
            _lblHistoryHeading = new Label
            {
                Text = "Recent exports",
                Location = new Point(leftMargin, y),
                AutoSize = true,
                Font = new Font("Segoe UI", UiScale.FontSize(9f), FontStyle.Bold),
                ForeColor = labelColor
            };
            Controls.Add(_lblHistoryHeading);
            y += UiScale.Pixels(22);

            _lvHistory = new ListView
            {
                Location = new Point(leftMargin, y),
                Width = UiScale.Pixels(540),
                Height = UiScale.Pixels(150),
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                GridLines = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                Font = bodyFont,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _lvHistory.Columns.Add("When", UiScale.Pixels(130));
            _lvHistory.Columns.Add("Format", UiScale.Pixels(70));
            _lvHistory.Columns.Add("File", UiScale.Pixels(330));
            Controls.Add(_lvHistory);
            y += _lvHistory.Height + UiScale.Pixels(6);

            _btnOpenFile = new Button
            {
                Text = "Open file",
                Location = new Point(leftMargin, y),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowOnly,
                MinimumSize = new Size(UiScale.Pixels(90), UiScale.Pixels(26)),
                FlatStyle = FlatStyle.System,
                Font = bodyFont
            };
            _btnOpenFile.Click += (s, e) => OnOpenFileClicked();
            Controls.Add(_btnOpenFile);

            _btnOpenFolder = new Button
            {
                Text = "Open folder",
                Location = new Point(_btnOpenFile.Right + UiScale.Pixels(8), y),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowOnly,
                MinimumSize = new Size(UiScale.Pixels(100), UiScale.Pixels(26)),
                FlatStyle = FlatStyle.System,
                Font = bodyFont
            };
            _btnOpenFolder.Click += (s, e) => OnOpenFolderClicked();
            Controls.Add(_btnOpenFolder);

            _btnReImportSelected = new Button
            {
                Text = "Re-import this",
                Location = new Point(_btnOpenFolder.Right + UiScale.Pixels(8), y),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowOnly,
                MinimumSize = new Size(UiScale.Pixels(110), UiScale.Pixels(26)),
                FlatStyle = FlatStyle.System,
                Font = bodyFont
            };
            _btnReImportSelected.Click += (s, e) => OnReImportSelectedClicked();
            Controls.Add(_btnReImportSelected);
            y += UiScale.Pixels(34);

            // ─── Log ─────────────────────────────────────────────────
            _lblLog = new Label
            {
                Text = "Log:",
                Location = new Point(leftMargin, y),
                AutoSize = true,
                Font = bodyFont,
                ForeColor = labelColor
            };
            Controls.Add(_lblLog);
            y += UiScale.Pixels(18);

            // Log textbox. Fixed minimum height instead of stretching to
            // fill the bottom — with the panel set to AutoScroll, anchoring
            // to Bottom would compute the wrong height (Bottom would mean
            // bottom-of-viewport, but the panel's content extends beyond
            // the viewport on smaller screens). OnResize keeps the textbox
            // wide enough to fill the available horizontal space; the
            // height is fixed at 160 px so the panel below can scroll.
            int logH = UiScale.Pixels(160);
            _txtLog = new TextBox
            {
                Location = new Point(leftMargin, y),
                Height = logH,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", UiScale.FontSize(8.5f)),
                BackColor = Color.FromArgb(248, 248, 248),
                ForeColor = Color.FromArgb(60, 60, 60),
                BorderStyle = BorderStyle.FixedSingle,
                WordWrap = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(_txtLog);
            y += logH + UiScale.Pixels(8);

            // Tell WinForms how tall the virtual canvas needs to be so
            // AutoScroll can decide when to show the scrollbar. Width = 0
            // means "use the viewport width" (no horizontal scrolling).
            AutoScrollMinSize = new Size(0, y);

            ResumeLayout(false);

            Resize += OnResize;
            OnResize(this, EventArgs.Empty);
        }

        private void OnResize(object sender, EventArgs e)
        {
            if (_txtLog == null) return;
            // Subtract a bit extra to leave room for the vertical scrollbar
            // that AutoScroll may show when the panel is too small for its
            // content.
            int rightPad = UiScale.Pixels(40);
            int wAvail = Math.Max(UiScale.Pixels(100), Width - rightPad);

            _lvHistory.Width = wAvail;
            _txtLog.Width = wAvail;
        }

        // ─── Public API ───────────────────────────────────────────────

        /// <summary>Whether strict tag-integrity checking is enabled in the
        /// UI. The ViewPart consults this before deciding whether to honour
        /// a TagMismatch diff classification (strict ON = skip mismatched
        /// segments; strict OFF = apply verbatim with a warning).</summary>
        public bool StrictTagIntegrityCheck => _chkStrictTagCheck?.Checked ?? true;

        /// <summary>Whether locked segments are included in the export.
        /// When false, the collector skips them entirely; when true,
        /// they're exported and visually marked with 🔒 in the Status
        /// column. Default true.</summary>
        public bool IncludeLockedSegments => _chkIncludeLocked?.Checked ?? true;

        /// <summary>v4.20.24: returns the set of checked confirmation-
        /// status enum names (e.g. {"Translated", "ApprovedTranslation"}).
        /// When ALL checkboxes are checked, returns an empty set —
        /// signalling "no status filter, include everything" to the
        /// collector (matches pre-v4.20.24 behaviour). When SOME are
        /// checked, returns just the checked names. When NONE are
        /// checked, returns the sentinel singleton {"__none__"} so the
        /// collector matches against nothing and the export comes out
        /// empty (predictable outcome rather than the implicit
        /// "no filter" interpretation).</summary>
        public HashSet<string> GetSelectedStatuses()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_statusCheckBoxes == null) return set;
            int total = _statusCheckBoxes.Count;
            int checkedCount = 0;
            foreach (var cb in _statusCheckBoxes)
            {
                if (cb.Checked)
                {
                    checkedCount++;
                    var name = cb.Tag as string;
                    if (!string.IsNullOrEmpty(name)) set.Add(name);
                }
            }
            // All checked → no filter (empty set).
            if (checkedCount == total) set.Clear();
            // None checked → sentinel that never matches.
            else if (checkedCount == 0) { set.Clear(); set.Add("__none__"); }
            return set;
        }

        /// <summary>Multi-file output mode. SeparatePerFile = produce one
        /// DOCX per selected file; CombineOne = one DOCX containing all
        /// selected files joined with section breaks + file column.
        /// Single-file documents ignore this value (always single output).</summary>
        public MultiFileOutputMode SelectedOutputMode =>
            (_rbSeparatePerFile?.Checked ?? false) ? MultiFileOutputMode.SeparatePerFile
                                                   : MultiFileOutputMode.CombineOne;

        /// <summary>Fires whenever the user ticks / unticks files in the
        /// multi-file list. The ViewPart uses this to refresh the
        /// segment count to "X selected / Y total".</summary>
        public event EventHandler FileSelectionChanged;

        /// <summary>Replace the multi-file list with the given entries. Pass
        /// an empty enumerable (or null) to hide the multi-file UI entirely
        /// — that's what single-file documents do. <paramref name="activeFileId"/>
        /// is the file id we treat as "the active one" for the [Active only]
        /// quick-select button.</summary>
        public void SetFileList(IEnumerable<FileEntry> files, string activeFileId)
        {
            SafeInvoke(() =>
            {
                _activeFileId = activeFileId ?? "";

                // Tear down the previous row set.
                foreach (var cb in _fileCheckBoxes) { try { cb.CheckedChanged -= OnFileCheckChanged; } catch { } cb.Dispose(); }
                _fileCheckBoxes.Clear();
                _pnlFiles.Controls.Clear();

                var list = files == null ? new List<FileEntry>() : new List<FileEntry>(files);
                bool multi = list.Count > 1;

                int rowH = UiScale.Pixels(22);
                int rowY = UiScale.Pixels(2);
                int rowX = UiScale.Pixels(4);
                int rowW = Math.Max(UiScale.Pixels(200), _pnlFiles.Width - UiScale.Pixels(24));
                foreach (var f in list)
                {
                    var cb = new CheckBox
                    {
                        Text = f?.ToString() ?? "",
                        Tag = f,
                        Location = new Point(rowX, rowY),
                        Width = Math.Max(UiScale.Pixels(200), rowW),
                        Height = rowH,
                        AutoEllipsis = true,
                        Checked = true,                   // default: every file checked
                        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                        Font = _pnlFiles.Font,
                        UseVisualStyleBackColor = true,
                        // No tab-stop = no keyboard focus rectangle. Combined
                        // with FlatStyle.System (native renderer), the row
                        // never shows the blue "selected" highlight that
                        // CheckedListBox had — clicking just toggles the
                        // box, full stop.
                        TabStop = false,
                        FlatStyle = FlatStyle.System
                    };
                    cb.CheckedChanged += OnFileCheckChanged;
                    _pnlFiles.Controls.Add(cb);
                    _fileCheckBoxes.Add(cb);
                    rowY += rowH;
                }

                _lblFilesHeading.Visible = multi;
                _pnlFiles.Visible = multi;
                _btnSelectActive.Visible = multi;
                _btnSelectAll.Visible = multi;
                _btnSelectNone.Visible = multi;
                _lblOutputMode.Visible = multi;
                _rbCombineOne.Visible = multi;
                _rbSeparatePerFile.Visible = multi;
            });
        }

        private void OnFileCheckChanged(object sender, EventArgs e)
        {
            FileSelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>True when the multi-file UI (file list + Active/All/None
        /// + Combine/Separate radios) is currently visible — i.e. the active
        /// document has more than one file merged in the editor. Lets the
        /// caller distinguish "empty selection because single-file" from
        /// "empty selection because user clicked None" without poking at
        /// the private controls.</summary>
        public bool IsMultiFileUiVisible
        {
            get { return _pnlFiles != null && _pnlFiles.Visible; }
        }

        /// <summary>Return the FileIds the user has currently checked. Empty
        /// list when the UI is hidden (single-file documents) or when the
        /// user has unchecked everything.</summary>
        public List<string> GetSelectedFileIds()
        {
            var ids = new List<string>();
            if (_pnlFiles == null || !_pnlFiles.Visible) return ids;
            foreach (var cb in _fileCheckBoxes)
            {
                if (!cb.Checked) continue;
                var entry = cb.Tag as FileEntry;
                if (entry != null) ids.Add(entry.FileId);
            }
            return ids;
        }

        private void SelectActiveFileOnly()
        {
            if (_pnlFiles == null) return;
            // Suppress per-box CheckedChanged so we fire one event at the end.
            foreach (var cb in _fileCheckBoxes)
            {
                cb.CheckedChanged -= OnFileCheckChanged;
                var entry = cb.Tag as FileEntry;
                cb.Checked = entry != null
                    && string.Equals(entry.FileId, _activeFileId, StringComparison.Ordinal);
                cb.CheckedChanged += OnFileCheckChanged;
            }
            FileSelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        private void CheckAllFiles(bool checkedState)
        {
            if (_pnlFiles == null) return;
            foreach (var cb in _fileCheckBoxes)
            {
                cb.CheckedChanged -= OnFileCheckChanged;
                cb.Checked = checkedState;
                cb.CheckedChanged += OnFileCheckChanged;
            }
            FileSelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        public void UpdateSegmentCount(int total)
        {
            _totalSegments = total;
            SafeInvoke(() => _lblSegmentCount.Text = "Segments: " + total);
        }

        public void AppendLog(string message, bool isError = false)
        {
            if (string.IsNullOrEmpty(message)) return;
            SafeInvoke(() =>
            {
                _txtLog.AppendText((isError ? "[ERROR] " : "") + message + Environment.NewLine);
            });
        }

        public void ClearLog()
        {
            SafeInvoke(() => _txtLog.Clear());
        }

        public void SetBusy(bool busy)
        {
            SafeInvoke(() =>
            {
                _btnExport.Enabled = !busy;
                _btnImport.Enabled = !busy;
                _btnReImportSelected.Enabled = !busy;
                Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
            });
        }

        /// <summary>Add a row to the recent-exports list.</summary>
        public void AddHistoryEntry(DateTime whenLocal, string format, string filePath)
        {
            SafeInvoke(() =>
            {
                var item = new ListViewItem(whenLocal.ToString("yyyy-MM-dd HH:mm"));
                item.SubItems.Add(format);
                item.SubItems.Add(filePath);
                item.Tag = filePath;
                _lvHistory.Items.Insert(0, item);

                // Keep history bounded.
                while (_lvHistory.Items.Count > 30)
                    _lvHistory.Items.RemoveAt(_lvHistory.Items.Count - 1);
            });
        }

        public void LoadHistoryEntries(IEnumerable<HistoryEntry> entries)
        {
            SafeInvoke(() =>
            {
                _lvHistory.Items.Clear();
                foreach (var e in entries)
                {
                    var item = new ListViewItem(e.WhenLocal.ToString("yyyy-MM-dd HH:mm"));
                    item.SubItems.Add(e.Format);
                    item.SubItems.Add(e.FilePath);
                    item.Tag = e.FilePath;
                    _lvHistory.Items.Add(item);
                }
            });
        }

        // ─── Event raisers ────────────────────────────────────────────

        private void RaiseExport()
        {
            var opts = new ExportOptions
            {
                Format = SelectedFormat(),
                Layout = SelectedLayout()
            };

            var args = new ExportRequestedEventArgs(opts);
            ExportRequested?.Invoke(this, args);
        }

        private void OnImportButton()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Filter = "Supported (*.docx;*.txt;*.md;*.markdown)|*.docx;*.txt;*.md;*.markdown|Word documents (*.docx)|*.docx|Bilingual Text (*.txt)|*.txt|Markdown (*.md;*.markdown)|*.md;*.markdown|All files|*.*";
                dlg.Title = "Choose a Supervertaler bilingual file to re-import";
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    ImportRequested?.Invoke(this, new ImportRequestedEventArgs(dlg.FileName));
                }
            }
        }

        private void OnOpenFileClicked()
        {
            var path = SelectedHistoryPath();
            if (string.IsNullOrEmpty(path)) return;
            OpenFileRequested?.Invoke(this, path);
        }

        private void OnOpenFolderClicked()
        {
            var path = SelectedHistoryPath();
            if (string.IsNullOrEmpty(path)) return;
            OpenFolderRequested?.Invoke(this, path);
        }

        private void OnReImportSelectedClicked()
        {
            var path = SelectedHistoryPath();
            if (string.IsNullOrEmpty(path)) return;
            ImportRequested?.Invoke(this, new ImportRequestedEventArgs(path));
        }

        private string SelectedHistoryPath()
        {
            if (_lvHistory.SelectedItems.Count == 0) return null;
            return _lvHistory.SelectedItems[0].Tag as string;
        }

        // ─── Selection mapping ────────────────────────────────────────

        private ExportFormat SelectedFormat()
        {
            switch (_cmbFormat.SelectedIndex)
            {
                case 0: return ExportFormat.Docx;
                case 1: return ExportFormat.Text;
                case 2: return ExportFormat.Html;
                default: return ExportFormat.Docx;
            }
        }

        private ExportLayout SelectedLayout()
        {
            // Layout is implied by the format: the Text format uses the
            // bracketed [SEGMENT NNNN] layout; DOCX and HTML use the table.
            return SelectedFormat() == ExportFormat.Text
                ? ExportLayout.Bracketed
                : ExportLayout.Table;
        }

        /// <summary>
        /// Updates the re-import status line for the current format. Re-import
        /// support is format-driven: DOCX (Bilingual Table) and Text (bracketed
        /// [SEGMENT NNNN]) round-trip back into Trados; the HTML report is
        /// export-only.
        /// </summary>
        private void UpdateRoundTripStatus()
        {
            if (_lblRoundTripStatus == null) return;

            var fmt = SelectedFormat();

            bool canReimport;
            string msg;

            if (fmt == ExportFormat.Html)
            {
                canReimport = false;
                msg = "Export only. HTML reports cannot be re-imported.";
            }
            else if (fmt == ExportFormat.Text)
            {
                canReimport = true;
                msg = "Can be edited (in any text editor or by an AI) and re-imported. "
                    + "The AI-friendly [SEGMENT NNNN] format round-trips back into Trados.";
            }
            else // Docx
            {
                canReimport = true;
                msg = "Can be edited and re-imported back into Trados.";
            }

            _lblRoundTripStatus.ForeColor = canReimport
                ? Color.FromArgb(0, 128, 0)      // green
                : Color.FromArgb(180, 100, 0);   // amber
            _lblRoundTripStatus.Text = (canReimport ? "✓ " : "⚠ ") + msg;
        }

        private void SafeInvoke(Action a)
        {
            if (a == null) return;
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke(a); } catch { } }
            else a();
        }

        // ─── Nested helper types ──────────────────────────────────────

        public class HistoryEntry
        {
            public DateTime WhenLocal { get; set; }
            public string Format { get; set; }
            public string FilePath { get; set; }
        }

        public class FileEntry
        {
            public string FileId { get; set; }
            public string FileName { get; set; }
            public int SegmentCount { get; set; }

            // CheckedListBox renders items via ToString.
            public override string ToString() =>
                $"{FileName}   ({SegmentCount} segments)";
        }
    }

    public enum MultiFileOutputMode
    {
        /// <summary>One bilingual DOCX containing all selected files with
        /// a file column and yellow-highlighted section breaks between
        /// files. Round-trippable as a single file.</summary>
        CombineOne,

        /// <summary>One bilingual DOCX per selected source file. Each
        /// gets its own sidecar manifest. Re-import works per file.</summary>
        SeparatePerFile
    }

    public class ExportRequestedEventArgs : EventArgs
    {
        public ExportOptions Options { get; }
        public ExportRequestedEventArgs(ExportOptions options) { Options = options; }
    }

    public class ImportRequestedEventArgs : EventArgs
    {
        public string FilePath { get; }
        public ImportRequestedEventArgs(string filePath) { FilePath = filePath; }
    }
}
