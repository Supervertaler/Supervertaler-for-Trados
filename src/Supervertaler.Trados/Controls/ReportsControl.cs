using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// Event args for navigating to a specific segment in the editor.
    /// </summary>
    public class NavigateToSegmentEventArgs : EventArgs
    {
        public string ParagraphUnitId { get; set; }
        public string SegmentId { get; set; }
    }

    /// <summary>A finding on the Reports tab, for Apply.</summary>
    public class ProofreadIssueEventArgs : EventArgs
    {
        public ProofreadingIssue Issue { get; set; }
    }

    /// <summary>
    /// WinForms UserControl for the Reports tab.
    /// Displays proofreading results as clickable issue cards.
    /// All layout is programmatic (no designer file).
    /// </summary>
    public class ReportsControl : UserControl
    {
        private const int HeaderLeft = 12;
        private const int HeaderTop = 10;
        private const int HeaderSpacing = 8;
        private const int ClearButtonMinWidth = 64;
        private const int ClearButtonMinHeight = 26;
        private const int ClearButtonHorizontalPadding = 16;
        private const int ClearButtonVerticalPadding = 8;

        // Header row (absolute positioned, like BatchTranslateControl)
        private Label _lblHeader;
        private Label _lblIssueCount;
        private Button _btnClear;
        private Button _btnSave;      // #105: enabled only while a proofreading report is showing
        private Button _btnApplyAll;  // enabled while a card has a correction that can be applied; "Stop" while applying
        private Label _lblRunningCost; // item 7 (198): this session's and today's AI cost
        private readonly ToolTip _footerTip = new ToolTip();

        // Results area
        private Panel _resultsPanel;
        private Label _lblEmpty;

        // Footer
        private Panel _footerPanel;
        private Label _lblFooter;
        private LinkLabel _lnkCostNote;

        // State
        private int _issueCount;
        private int _checkedCount;

        // The footer's own text ("Last run: ... - saved as ..."), which an Apply
        // all summary is shown after rather than added to.
        private string _footerBase = "";

        // Apply all is running: the cards' Apply buttons are off and Apply all reads Stop.
        private bool _applying;

        // Proofreading card colours
        private static readonly Color CardColor = Color.FromArgb(255, 253, 231);      // #FFFDE7
        private static readonly Color HoverColor = Color.FromArgb(255, 249, 196);
        private static readonly Color TextColor = Color.FromArgb(60, 60, 60);
        private static readonly Color SuggColor = Color.FromArgb(120, 120, 120);
        private static readonly Color CorrColor = Color.FromArgb(46, 110, 52);        // the text Apply writes
        private static readonly Color RefusedColor = Color.FromArgb(176, 0, 32);      // could not be applied
        private static readonly Color NoteColor = Color.FromArgb(150, 95, 0);         // applied, with something to check

        // Card fonts, shared by every card (never disposed: they live as long as Studio).
        private static readonly Font CardBodyFont = new Font("Segoe UI", 8.5f);
        private static readonly Font CardSegNumFont = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        private static readonly Font CardSmallFont = new Font("Segoe UI", 8f);
        private static readonly Font CardEvidenceFont = new Font("Segoe UI", 8f, FontStyle.Italic);

        // Prompt log card colours
        private static readonly Color PromptCardColor = Color.FromArgb(227, 242, 253);  // #E3F2FD
        private static readonly Color PromptHoverColor = Color.FromArgb(207, 232, 252);
        private static readonly Color PromptHeaderColor = Color.FromArgb(30, 90, 160);

        private const int MaxPromptLogEntries = 500;

        /// <summary>Fired when user clicks an issue card to navigate to that segment.</summary>
        public event EventHandler<NavigateToSegmentEventArgs> NavigateToSegmentRequested;

        /// <summary>Fired when user clicks "Clear Results".</summary>
        public event EventHandler ClearResultsRequested;

        /// <summary>Fired when the user clicks "Save report\u2026" (#105). Only raised
        /// while a proofreading report is loaded; the button is disabled otherwise.</summary>
        public event EventHandler SaveReportRequested;

        /// <summary>An issue was ticked off (#105); the owner persists the report so the
        /// dismissal survives a restart.</summary>
        public event EventHandler IssueDismissed;

        /// <summary>Apply on a card: write the finding's correction into its
        /// segment. The owner answers with <see cref="ShowApplyOutcome"/>.</summary>
        public event EventHandler<ProofreadIssueEventArgs> ApplyRequested;

        /// <summary>Apply all in the header: every finding not ticked off - or,
        /// while one is running, Stop.</summary>
        public event EventHandler ApplyAllRequested;

        /// <summary>Gets the number of issues currently displayed.</summary>
        public int IssueCount => _issueCount;

        /// <summary>Gets whether "Also add as Trados comments" is checked.</summary>
        // AddAsComments moved to BatchTranslateControl

        public ReportsControl()
        {
            BuildUI();

            // The running cost: after every AI call, and whenever the tab comes into
            // view (the other Studio version writes the same usage log). Unsubscribed
            // in Dispose: SessionCost is static and would otherwise hold this control.
            SessionCost.Changed += OnRunningCostChanged;
            HandleCreated += (s, e) => RefreshRunningCost();
            VisibleChanged += (s, e) => { if (Visible) RefreshRunningCost(); };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SessionCost.Changed -= OnRunningCostChanged;
                _refineTimer?.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>Raised on the thread that made the AI call; the label is updated on the UI thread.</summary>
        private void OnRunningCostChanged(object sender, EventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)RefreshRunningCost); }
            catch { /* the control is going away */ }
        }

        private void RefreshRunningCost()
        {
            if (IsDisposed || _lblRunningCost == null) return;
            try { _lblRunningCost.Text = SessionCost.Format(SessionCost.Read()); }
            catch { /* a cost display must never break the tab */ }
        }

        private void BuildUI()
        {
            // Let WinForms scale this dialog by system DPI so it doesn't squish
            // at >100% Windows display scaling. Cheap fallback; for surfaces
            // with their own UiScale-driven layout, set AutoScaleMode = None
            // instead and let UiScale own scaling.
            AutoScaleMode = AutoScaleMode.Dpi;
            SuspendLayout();
            BackColor = Color.White;
            AutoScroll = false;
            Padding = Padding.Empty;

            var labelColor = Color.FromArgb(80, 80, 80);
            var headerFont = new Font("Segoe UI", 9f, FontStyle.Bold);
            var bodyFont = new Font("Segoe UI", 8.5f);

            var y = 10;

            // ─── Header (absolute positioned, same pattern as BatchTranslateControl) ───
            _lblHeader = new Label
            {
                Text = "Reports",
                Font = headerFont,
                ForeColor = Color.FromArgb(50, 50, 50),
                Location = new Point(HeaderLeft, HeaderTop),
                AutoSize = true
            };
            Controls.Add(_lblHeader);

            _btnClear = new Button
            {
                Text = "Clear",
                Size = new Size(ClearButtonMinWidth, ClearButtonMinHeight),
                Location = new Point(200, y),
                FlatStyle = FlatStyle.Flat,
                Font = bodyFont,
                ForeColor = Color.FromArgb(80, 80, 80),
                BackColor = Color.FromArgb(245, 245, 245),
                Cursor = Cursors.Hand
            };
            _btnClear.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
            _btnClear.FlatAppearance.MouseOverBackColor = Color.FromArgb(230, 230, 230);
            _btnClear.Click += (s, e) => ClearResultsRequested?.Invoke(this, EventArgs.Empty);
            Controls.Add(_btnClear);

            // #105: save the proofreading report. Sits beside Clear so it is seen, and
            // is enabled only while a report is showing - the prompt-log cards that
            // share this tab are already on disk (#98) and are not what it saves.
            _btnSave = new Button
            {
                Text = "Save report\u2026",
                Size = new Size(ClearButtonMinWidth, ClearButtonMinHeight),
                Location = new Point(120, y),
                FlatStyle = FlatStyle.Flat,
                Font = bodyFont,
                ForeColor = Color.FromArgb(80, 80, 80),
                BackColor = Color.FromArgb(245, 245, 245),
                Cursor = Cursors.Hand,
                Enabled = false
            };
            _btnSave.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
            _btnSave.FlatAppearance.MouseOverBackColor = Color.FromArgb(230, 230, 230);
            _btnSave.Click += (s, e) => SaveReportRequested?.Invoke(this, EventArgs.Empty);
            Controls.Add(_btnSave);

            // Apply all: writes every open finding's correction into its segment.
            // Sits left of Save; enabled only while some card can be applied.
            _btnApplyAll = new Button
            {
                Text = "Apply all",
                Size = new Size(ClearButtonMinWidth, ClearButtonMinHeight),
                Location = new Point(40, y),
                FlatStyle = FlatStyle.Flat,
                Font = bodyFont,
                ForeColor = Color.FromArgb(80, 80, 80),
                BackColor = Color.FromArgb(245, 245, 245),
                Cursor = Cursors.Hand,
                Enabled = false
            };
            _btnApplyAll.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
            _btnApplyAll.FlatAppearance.MouseOverBackColor = Color.FromArgb(230, 230, 230);
            _btnApplyAll.Click += (s, e) => ApplyAllRequested?.Invoke(this, EventArgs.Empty);
            _footerTip.SetToolTip(_btnApplyAll,
                "Writes the AI's correction into every segment whose card is not ticked off.\r\n" +
                "A segment that has changed since it was checked is left alone.\r\n" +
                "Each fixed segment is set to Draft; with Track Changes on, the fix is tracked.");
            Controls.Add(_btnApplyAll);

            _lblIssueCount = new Label
            {
                Text = "",
                Font = bodyFont,
                ForeColor = Color.FromArgb(100, 100, 100),
                Location = new Point(80, y + 2),
                AutoSize = true
            };
            Controls.Add(_lblIssueCount);
            y += 28;

            // ─── Running cost (item 7, 198): its own row, the header row is full ───
            _lblRunningCost = new Label
            {
                Text = "",
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(110, 110, 110),
                Location = new Point(HeaderLeft, y),
                AutoSize = true
            };
            var costLineTip = new ToolTip { AutoPopDelay = 20000, InitialDelay = 300 };
            costLineTip.SetToolTip(_lblRunningCost,
                "What your AI calls from Trados have cost: Batch Translate and Proofread,\r\n" +
                "Translate active segment, the AI Assistant chat, QuickLauncher, AutoPrompt\r\n" +
                "and the memory bank selection. Counted whether or not prompt logging is on.\r\n\r\n" +
                "This session: since Studio started. Today: from the usage log, so it also\r\n" +
                "includes earlier sessions today and the other Studio version. It needs the\r\n" +
                "usage log, which is on unless you have switched it off in AI Settings.\r\n\r\n" +
                "Figures use the token counts your provider reports where it reports them.\r\n" +
                "\"Up to\" means a model without a price was used: its calls are counted at\r\n" +
                "the most they can have cost. Supervertaler for memoQ keeps its own log.");
            Controls.Add(_lblRunningCost);
            y += 20;

            // ─── Footer (anchored to bottom) ─────────────────────
            // Two-part bar: status text on the left ("Last run: ..." for proofreading
            // reports), and a permanent estimate disclaimer + AI Cost Guide link on the
            // right. The link is always visible so users see "these numbers are estimates"
            // every time they look at the Reports tab, without needing to click anything.
            _footerPanel = new Panel
            {
                Height = 22,
                BackColor = Color.FromArgb(250, 250, 250),
                Dock = DockStyle.Bottom
            };

            _lnkCostNote = new LinkLabel
            {
                Text = "Token counts and costs are estimates · AI Cost Guide",
                Font = new Font("Segoe UI", 7.5f),
                LinkColor = Color.FromArgb(37, 99, 235),
                ActiveLinkColor = Color.FromArgb(37, 99, 235),
                VisitedLinkColor = Color.FromArgb(37, 99, 235),
                LinkBehavior = LinkBehavior.HoverUnderline,
                AutoSize = true,
                Padding = new Padding(0, 0, 12, 0)
            };
            // Make only the trailing "AI Cost Guide" the clickable link.
            const string linkText = "AI Cost Guide";
            _lnkCostNote.LinkArea = new LinkArea(
                _lnkCostNote.Text.LastIndexOf(linkText, StringComparison.Ordinal),
                linkText.Length);
            _lnkCostNote.LinkClicked += (s, e) =>
                HelpSystem.OpenHelp(HelpSystem.Topics.AiCostGuide);
            var costTip = new ToolTip { AutoPopDelay = 10000, InitialDelay = 300 };
            costTip.SetToolTip(_lnkCostNote,
                "Token counts shown in this tab are computed locally with a chars/4\r\n" +
                "heuristic – they are not the actual token counts billed by the provider.\r\n" +
                "Click \"AI Cost Guide\" for provider-by-provider links to your real usage console.");
            _footerPanel.Controls.Add(_lnkCostNote);

            _lblFooter = new Label
            {
                Font = new Font("Segoe UI", 7.5f),
                ForeColor = Color.FromArgb(140, 140, 140),
                Text = "",
                TextAlign = ContentAlignment.MiddleLeft,
                AutoSize = false,
                // One line, cut with "...": the row is shared with the cost note,
                // and wrapped text was cut in half by the row's height instead.
                // The tooltip carries the whole text where it matters.
                AutoEllipsis = true,
                Location = new Point(0, 0),
                Padding = new Padding(12, 0, 12, 0)
            };
            _footerPanel.Controls.Add(_lblFooter);

            _footerPanel.Resize += (s, e) =>
            {
                if (_lnkCostNote == null || _lblFooter == null || _footerPanel == null) return;
                int linkX = Math.Max(0, _footerPanel.ClientSize.Width - _lnkCostNote.Width);
                _lnkCostNote.Location = new Point(linkX,
                    Math.Max(0, (_footerPanel.ClientSize.Height - _lnkCostNote.Height) / 2));
                _lblFooter.Size = new Size(Math.Max(0, linkX), _footerPanel.ClientSize.Height);
            };
            Controls.Add(_footerPanel);

            // ─── Results panel (fills remaining space) ──
            // Not AutoScroll: it holds only the cards in view and scrolls them
            // itself, with its own scroll bar (see "Findings: a virtual list").
            _resultsPanel = new Panel
            {
                Location = new Point(0, y),
                AutoScroll = false,
                BackColor = Color.White,
                Padding = new Padding(8, 4, 8, 4),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };
            Controls.Add(_resultsPanel);

            _scrollBar = new VScrollBar { Dock = DockStyle.Right, Visible = false };
            _scrollBar.Scroll += (s, e) =>
            {
                _thumbTracking = e.Type == ScrollEventType.ThumbTrack;
                if (e.NewValue == _scrollY) return;
                _scrollY = e.NewValue;
                Arrange();
            };
            _resultsPanel.Controls.Add(_scrollBar);
            _resultsPanel.MouseWheel += OnListMouseWheel;

            // ─── Empty state label ────────────────────────────────
            _lblEmpty = new Label
            {
                Text = "No reports yet",
                Font = new Font("Segoe UI", 9f),
                ForeColor = Color.FromArgb(160, 160, 160),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Fill
            };
            _resultsPanel.Controls.Add(_lblEmpty);
            _lblEmpty.BringToFront();
            _resultsPanel.Resize += OnResultsPanelResize;

            ResumeLayout(false);

            // Handle resize for responsive layout
            Resize += OnControlResize;
            // The count label is AutoSize: its width is only known after its text is
            // set, and setting text raises no Resize on this control - so the header
            // was laid out against an empty label and the text then grew under the
            // buttons. Re-lay out whenever the label's size changes (#105).
            _lblIssueCount.SizeChanged += OnControlResize;
            OnControlResize(this, EventArgs.Empty);
        }

        private void OnControlResize(object sender, EventArgs e)
        {
            if (_btnClear == null || _lblIssueCount == null || _resultsPanel == null || _footerPanel == null)
                return;

            UpdateClearButtonSize();

            var clientWidth = ClientSize.Width;

            // Position Clear button at top-right using the actual rendered button width.
            _btnClear.Location = new Point(
                Math.Max(HeaderLeft, clientWidth - _btnClear.Width - HeaderSpacing),
                HeaderTop - 2);

            // Save sits immediately left of Clear (#105), Apply all left of Save.
            var leftmostButton = _btnClear.Left;
            if (_btnSave != null)
            {
                _btnSave.Location = new Point(Math.Max(HeaderLeft, _btnClear.Left - _btnSave.Width - HeaderSpacing), _btnClear.Top);
                leftmostButton = _btnSave.Left;
            }
            if (_btnApplyAll != null)
            {
                _btnApplyAll.Location = new Point(Math.Max(HeaderLeft, leftmostButton - _btnApplyAll.Width - HeaderSpacing), _btnClear.Top);
                leftmostButton = _btnApplyAll.Left;
            }

            // Position issue count label between the title and the buttons; if the
            // panel is too narrow for it, hide it rather than let it run under them
            // (the count is also on the tab badge).
            var labelLeft = leftmostButton - _lblIssueCount.Width - HeaderSpacing;
            _lblIssueCount.Visible = labelLeft >= _lblHeader.Right + HeaderSpacing;
            _lblIssueCount.Location = new Point(
                Math.Max(_lblHeader.Right + HeaderSpacing, labelLeft),
                _btnClear.Top + Math.Max(0, (_btnClear.Height - _lblIssueCount.Height) / 2));

            var headerBottom = Math.Max(_lblHeader.Bottom, _btnClear.Bottom);
            var resultsTop = headerBottom + HeaderSpacing;
            if (_lblRunningCost != null)
            {
                _lblRunningCost.Location = new Point(HeaderLeft, headerBottom + 4);
                resultsTop = _lblRunningCost.Bottom + HeaderSpacing;
            }
            _resultsPanel.Location = new Point(0, resultsTop);
            _resultsPanel.Width = clientWidth;
            _resultsPanel.Height = Math.Max(40, _footerPanel.Top - _resultsPanel.Top);
        }

        private void UpdateClearButtonSize()
        {
            var measured = TextRenderer.MeasureText(
                _btnClear.Text,
                _btnClear.Font,
                new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

            // Height: what a flat button says it needs for its text. The text's own
            // height plus a fixed padding came out 5 px short at 125% scaling, and
            // the "p"s and "y"s of Apply all and Save report were cut off at the
            // bottom (Michael, 16 Oct 2026). The other two buttons take this height.
            _btnClear.Size = new Size(
                Math.Max(ClearButtonMinWidth, measured.Width + ClearButtonHorizontalPadding),
                Math.Max(Math.Max(ClearButtonMinHeight, measured.Height + ClearButtonVerticalPadding),
                    _btnClear.GetPreferredSize(Size.Empty).Height));
            if (_btnSave != null)
            {
                var m2 = TextRenderer.MeasureText(_btnSave.Text, _btnSave.Font);
                _btnSave.Size = new Size(
                    Math.Max(ClearButtonMinWidth, m2.Width + ClearButtonHorizontalPadding),
                    _btnClear.Height);
            }
            if (_btnApplyAll != null)
            {
                var m3 = TextRenderer.MeasureText(_btnApplyAll.Text, _btnApplyAll.Font);
                _btnApplyAll.Size = new Size(
                    Math.Max(ClearButtonMinWidth, m3.Width + ClearButtonHorizontalPadding),
                    _btnClear.Height);
            }
        }

        // ─── Public Methods ───────────────────────────────────────

        /// <summary>
        /// Populates the results list with proofreading report data.
        /// Only issues (not OK segments) are displayed.
        /// </summary>
        public void SetResults(ProofreadingReport report)
        {
            if (report == null) return;

            // The findings only: the request log stays (only Clear empties it).
            ClearFindings();

            _issueCount = report.IssueCount;
            var totalChecked = report.TotalSegmentsChecked;

            // Update count label
            _lblIssueCount.Text = $"{_issueCount} issue{(_issueCount != 1 ? "s" : "")} found in {totalChecked} segment{(totalChecked != 1 ? "s" : "")}";
            // Force re-position after text change
            OnControlResize(this, EventArgs.Empty);

            // Update footer
            _footerBase = $"Last run: {report.Timestamp:HH:mm:ss} \u2013 {report.Duration.TotalSeconds:F1}s"
                + (report.TargetOnly ? " \u2013 translation only" : "");
            _lblFooter.Text = _footerBase;
            _footerTip.SetToolTip(_lblFooter, "");
            if (_btnSave != null) _btnSave.Enabled = true;   // #105

            if (_issueCount == 0)
            {
                ShowEmptyText("No issues found \u2013 all segments look good!");
                UpdateApplyAllButton();
                return;
            }

            _lblEmpty.Visible = false;
            if (report.Issues.All(i => i.IsOk || i.Dismissed))
            {
                _checkedCount = _issueCount;
                UpdateIssueCountLabel();
                ShowEmptyText("All issues addressed \u2013 well done!");
                UpdateApplyAllButton();
                return;
            }

            // Issues already ticked off (a restored report) count as addressed and get no row.
            _checkedCount = report.Issues.Count(i => !i.IsOk && i.Dismissed);
            if (_checkedCount > 0) UpdateIssueCountLabel();

            // One row per open finding, in document order: file by file, then by
            // number. By number alone, a merged document's files interleaved.
            foreach (var issue in report.InDocumentOrder(report.Issues.Where(i => !i.IsOk && !i.Dismissed)))
            {
                _rows.Add(issue);
                _rowInfo[issue] = new RowInfo();
            }

            // Estimated heights now, exact ones for the rows in view (Arrange),
            // the rest while Studio is idle (StartRefine).
            EstimateAllRows(CardWidth());
            _scrollY = 0;
            Arrange();
            UpdateApplyAllButton();
            StartRefine();
        }

        // ─── Findings: a virtual list ─────────────────────────────
        //
        // A report can hold thousands of findings. One card of controls per
        // finding cost about six window handles and six GDI objects each: at
        // 1,000 findings filling the tab took 52 seconds and 6,300 handles, and
        // at 3,000 the process ran out of them - inside Trados Studio, Studio's
        // own process. And Windows cannot place a control more than 32,767 pixels
        // down, so beyond roughly 200 findings the cards piled up on top of one
        // another. (Measured 16 Oct 2026 with .dev/reports-scale-test.ps1.)
        //
        // So a finding is a row of data with its height at the current width,
        // and only the rows in view have a card. Cards come from a pool the size
        // of the view and are bound to other rows as the list scrolls. The panel
        // scrolls itself, with its own scroll bar, so no control is ever placed
        // outside the view. The prompt log cards that share the panel stay real
        // controls (at most MaxPromptLogEntries) and are placed the same way.
        //
        // Measuring a row exactly takes about a millisecond (TextRenderer, one
        // wrapped text at a time, whatever device context it is given), so a
        // report of 3,000 findings would take seconds to measure before showing
        // anything. Rows are therefore given an estimated height from their
        // character counts, the rows in view are measured exactly before they
        // are shown, and the rest are measured in short slices while Studio is
        // idle. A row above the view that changes height moves the scroll
        // position by as much, so what is in view does not jump.

        /// <summary>What a row needs besides its finding.</summary>
        private sealed class RowInfo
        {
            /// <summary>The card's height: exact at <see cref="Width"/>, an
            /// estimate while <see cref="Width"/> is -1.</summary>
            public int Height;

            /// <summary>The card width <see cref="Height"/> was measured at; -1
            /// while it is an estimate.</summary>
            public int Width = -1;

            /// <summary><see cref="Status"/> and <see cref="CanApply"/> are worked
            /// out (see Resolve). Until then the row has no status line, and counts
            /// as one Apply all may be able to apply.</summary>
            public bool Resolved;

            /// <summary>The card's last line - why it cannot be applied, or what
            /// Apply did - or null for none.</summary>
            public string Status;
            public Color StatusColor;

            /// <summary>The card has an Apply button.</summary>
            public bool CanApply;
        }

        private readonly List<ProofreadingIssue> _rows = new List<ProofreadingIssue>();
        private readonly Dictionary<ProofreadingIssue, RowInfo> _rowInfo = new Dictionary<ProofreadingIssue, RowInfo>();

        // _rowTops[i] is row i's top within the findings, _rowTops[_rows.Count]
        // their total height. Rebuilt from the stored heights when stale: O(rows),
        // nothing measured.
        private int[] _rowTops = new int[1];
        private bool _rowTopsStale = true;

        private readonly Dictionary<ProofreadingIssue, Panel> _boundCards = new Dictionary<ProofreadingIssue, Panel>();
        private readonly Stack<Panel> _freeCards = new Stack<Panel>();
        private readonly List<Panel> _promptCards = new List<Panel>();   // oldest first, as on screen
        private VScrollBar _scrollBar;
        private int _scrollY;
        private bool _binding;   // a card's tick box is being reset, not ticked

        // Measures the rows still on an estimate, a slice at a time, while Studio
        // is idle (a WinForms timer ticks only when no other message is waiting).
        private Timer _refineTimer;
        private const int RefineSliceMs = 25;

        // The scroll bar's thumb is being dragged: the scroll position is the
        // user's, and refining (which moves it to keep the view still) waits.
        private bool _thumbTracking;

        // The card width the rows' heights (estimated or exact) are for.
        private int _layoutWidth = -1;

        // For estimating a text's height: each card font's average character
        // width and line height, measured once.
        private float[] _avgCharWidth;
        private int[] _lineHeight;

        private static readonly Font PromptSmallFont = new Font("Segoe UI", 7.5f);
        private static readonly Font MenuFont = new Font("Segoe UI", 9f);

        /// <summary>Works out the row's status line and whether it can be applied
        /// - once; an Apply outcome shown on it since is left as it is.</summary>
        private static void Resolve(ProofreadingIssue issue, RowInfo info)
        {
            if (info.Resolved) return;
            info.Resolved = true;
            if (issue.Applied)
            {
                info.Status = "Applied.";
                info.StatusColor = NoteColor;
            }
            else if (issue.CorrectedTarget != null)
            {
                var problem = ProofreadCorrection.Problem(issue.SourceText, issue.TargetText, issue.CorrectedTarget);
                if (problem != null)
                {
                    info.Status = "Cannot be applied: " + problem;
                    info.StatusColor = RefusedColor;
                }
                else
                {
                    info.CanApply = true;
                }
            }
        }

        /// <summary>The width every card gets: the panel's, less the scroll bar
        /// and margins, whether the scroll bar is showing or not - so a card's
        /// height never depends on whether the list is long enough to scroll.</summary>
        private int CardWidth()
        {
            var w = _resultsPanel.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 24;
            return w < 100 ? 300 : w;
        }

        private Graphics MeasureGraphics() =>
            _resultsPanel.IsHandleCreated ? _resultsPanel.CreateGraphics() : Graphics.FromHwnd(IntPtr.Zero);

        // ── A card's layout, in one place: measuring a row and laying out its card
        // both go through CardBlocks, so a card is always exactly its row's height.

        private const int CardTextLeft = 8;
        private const int CardBlockGap = 2;
        private const int CardBottomPad = 6;

        private static string[] CardTexts(ProofreadingIssue issue, RowInfo info) => new[]
        {
            issue.IssueDescription ?? "",
            string.IsNullOrEmpty(issue.Evidence) ? null : "Evidence: " + issue.Evidence,
            string.IsNullOrEmpty(issue.Suggestion) ? null : "Suggestion: " + issue.Suggestion,
            issue.CorrectedTarget == null ? null : "Correction: " + ForTextBox(issue.CorrectedTarget),
            info.Status,
        };

        private static readonly Font[] CardFonts = { CardBodyFont, CardEvidenceFont, CardSmallFont, CardSmallFont, CardSmallFont };
        private static readonly string[] CardTags = { "desc", "evidence", "sugg", "corr", "status" };

        /// <summary>Where the texts start: under the segment number, and under
        /// the Apply button, whether or not this card has one, so Apply coming and
        /// going never changes a row's height.</summary>
        private int CardHeaderBottom(IDeviceContext dc)
        {
            var seg = TextRenderer.MeasureText(dc, "Xg", CardSegNumFont);
            var btn = ApplyButtonSize();
            return Math.Max(6 + seg.Height, 3 + btn.Height) + CardBlockGap;
        }

        private Size _applyButtonSize;

        /// <summary>The cards' Apply button: the size a flat button says it needs
        /// for "Apply" - not the text measured plus a fixed padding, which came out
        /// 5 px short at 125% and cut off the descenders (16 Oct 2026). Worked out
        /// once, from a button that is never shown.</summary>
        private Size ApplyButtonSize()
        {
            if (_applyButtonSize.IsEmpty)
            {
                using (var probe = new Button { Text = "Apply", Font = CardSmallFont, FlatStyle = FlatStyle.Flat })
                    _applyButtonSize = probe.GetPreferredSize(Size.Empty);
            }
            return _applyButtonSize;
        }

        /// <summary>The height of each text block (0 where there is none) and of
        /// the whole card, for a card <paramref name="cardWidth"/> wide.</summary>
        private int CardBlocks(ProofreadingIssue issue, RowInfo info, int cardWidth, IDeviceContext dc,
            int headerBottom, int[] heights)
        {
            var texts = CardTexts(issue, info);
            int textWidth = cardWidth - 20;
            int top = headerBottom, height = headerBottom + 4;
            for (int b = 0; b < texts.Length; b++)
            {
                heights[b] = 0;
                if (texts[b] == null) continue;
                var size = TextRenderer.MeasureText(dc, texts[b], CardFonts[b],
                    new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                heights[b] = Math.Max(16, size.Height + 4);
                height = top + heights[b] + CardBottomPad;
                top += heights[b] + CardBlockGap;
            }
            return Math.Max(40, height);
        }

        /// <summary>The row's exact height at <paramref name="cardWidth"/>
        /// (its status worked out first: the status line is part of the card).</summary>
        private void MeasureRow(ProofreadingIssue issue, RowInfo info, int cardWidth, IDeviceContext dc, int headerBottom)
        {
            Resolve(issue, info);
            info.Height = CardBlocks(issue, info, cardWidth, dc, headerBottom, new int[CardTexts(issue, info).Length]);
            info.Width = cardWidth;
            _rowTopsStale = true;
        }

        /// <summary>Every row given an estimated height for <paramref name="cardWidth"/>:
        /// milliseconds for thousands of rows, where measuring them takes seconds.</summary>
        private void EstimateAllRows(int cardWidth)
        {
            _layoutWidth = cardWidth;
            if (_rows.Count == 0) return;
            using (var g = MeasureGraphics())
            {
                EnsureFontMetrics(g);
                var header = CardHeaderBottom(g);
                foreach (var issue in _rows)
                {
                    var info = _rowInfo[issue];
                    info.Width = -1;
                    info.Height = EstimateRow(issue, info, cardWidth, header);
                }
            }
            _rowTopsStale = true;
        }

        private void EnsureFontMetrics(IDeviceContext dc)
        {
            if (_avgCharWidth != null) return;
            // Text of the kind the cards hold, so the average is a fair one.
            const string sample = "The valve is closed when the pressure exceeds 17,1 bar; use 'valve', not 'flap'. Het apparaat werkt.";
            _avgCharWidth = new float[CardFonts.Length];
            _lineHeight = new int[CardFonts.Length];
            for (int b = 0; b < CardFonts.Length; b++)
            {
                _avgCharWidth[b] = TextRenderer.MeasureText(dc, sample, CardFonts[b]).Width / (float)sample.Length;
                _lineHeight[b] = TextRenderer.MeasureText(dc, "Xg", CardFonts[b]).Height;
            }
        }

        /// <summary>A card's height from its character counts: each text's lines,
        /// each line wrapped at the average character width plus a margin for the
        /// space word wrapping leaves at line ends. Close, not exact: an exact
        /// height replaces it before the row is shown.</summary>
        private int EstimateRow(ProofreadingIssue issue, RowInfo info, int cardWidth, int headerBottom)
        {
            var texts = CardTexts(issue, info);
            int textWidth = Math.Max(20, cardWidth - 20);
            int top = headerBottom, height = headerBottom + 4;
            for (int b = 0; b < texts.Length; b++)
            {
                if (texts[b] == null) continue;
                int lines = 0;
                foreach (var line in texts[b].Split('\n'))
                    lines += Math.Max(1, (int)Math.Ceiling(line.Length * _avgCharWidth[b] * 1.1f / textWidth));
                int block = Math.Max(16, lines * _lineHeight[b] + 4);
                height = top + block + CardBottomPad;
                top += block + CardBlockGap;
            }
            return Math.Max(40, height);
        }

        private void EnsureRowTops()
        {
            if (!_rowTopsStale && _rowTops.Length == _rows.Count + 1) return;
            if (_rowTops.Length != _rows.Count + 1) _rowTops = new int[_rows.Count + 1];
            int y = 0;
            for (int i = 0; i < _rows.Count; i++)
            {
                _rowTops[i] = y;
                y += _rowInfo[_rows[i]].Height + 4;
            }
            _rowTops[_rows.Count] = y;
            _rowTopsStale = false;
        }

        /// <summary>The first row whose bottom is below <paramref name="y"/>
        /// (within the findings).</summary>
        private int FirstRowAt(int y)
        {
            int lo = 0, hi = _rows.Count - 1, found = _rows.Count;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (_rowTops[mid + 1] > y) { found = mid; hi = mid - 1; }
                else lo = mid + 1;
            }
            return found;
        }

        // The list, top to bottom: the findings (document order), then the prompt
        // log cards (oldest first). The log is last because it grows at the
        // bottom, like the chat, and a report's findings stay where they are.
        private const int ListTop = 4;

        /// <summary>The prompt log cards' height, with their gaps.</summary>
        private int PromptsHeight()
        {
            int h = 0;
            foreach (var p in _promptCards) h += p.Height + 4;
            return h;
        }

        /// <summary>The whole list's height: findings, then prompt log cards.</summary>
        private int ListHeight()
        {
            EnsureRowTops();
            return ListTop + _rowTops[_rows.Count] + PromptsHeight();
        }

        private int ViewHeight() => Math.Max(1, _resultsPanel.ClientSize.Height);

        /// <summary>
        /// Places everything for the current scroll position: a card for each
        /// finding in view, then the prompt log cards (oldest first). Cards whose
        /// row has scrolled away go back to the pool. Cheap enough to run on every
        /// scroll step: it measures only rows in view whose height is still an
        /// estimate.
        /// </summary>
        private void Arrange()
        {
            if (_resultsPanel == null) return;
            int cardWidth = CardWidth();
            int viewHeight = ViewHeight();

            // Rows in view whose height is still an estimate: measured now, the
            // rest by the refine timer.
            EnsureRowTops();
            int first = FirstRowAt(_scrollY - ListTop);
            bool remeasured = false;
            using (var g = MeasureGraphics())
            {
                int header = -1;
                for (int i = first; i < _rows.Count && ListTop + _rowTops[i] < _scrollY + viewHeight; i++)
                {
                    var info = _rowInfo[_rows[i]];
                    if (info.Width == cardWidth) continue;
                    if (header < 0) header = CardHeaderBottom(g);
                    MeasureRow(_rows[i], info, cardWidth, g, header);
                    remeasured = true;
                }
                if (remeasured) EnsureRowTops();

                int total = ListTop + _rowTops[_rows.Count] + PromptsHeight();
                UpdateScrollBar(total, viewHeight);
                first = FirstRowAt(_scrollY - ListTop);

                _resultsPanel.SuspendLayout();
                try
                {
                    int y = ListTop + _rowTops[_rows.Count];
                    foreach (var p in _promptCards)
                    {
                        bool inView = y + p.Height > _scrollY && y < _scrollY + viewHeight;
                        if (inView)
                        {
                            if (p.Width != cardWidth) p.Width = cardWidth;
                            var at = new Point(4, y - _scrollY);
                            if (p.Location != at) p.Location = at;
                        }
                        if (p.Visible != inView) p.Visible = inView;
                        y += p.Height + 4;
                    }

                    // The rows to show, then cards: a card keeps its row while the
                    // row stays in view, and only cards whose row left are reused.
                    var inViewRows = new List<int>();
                    for (int i = first; i < _rows.Count && ListTop + _rowTops[i] < _scrollY + viewHeight; i++)
                        inViewRows.Add(i);
                    var wanted = new HashSet<ProofreadingIssue>(inViewRows.Select(i => _rows[i]));
                    foreach (var issue in _boundCards.Keys.Where(k => !wanted.Contains(k)).ToList())
                        UnbindCard(issue);

                    int headerBottom = -1;
                    foreach (var i in inViewRows)
                    {
                        var issue = _rows[i];
                        if (!_boundCards.TryGetValue(issue, out var card) || card.Width != cardWidth)
                        {
                            if (card == null) card = _freeCards.Count > 0 ? _freeCards.Pop() : MakeCard();
                            if (headerBottom < 0) headerBottom = CardHeaderBottom(g);
                            BindCard(card, issue, _rowInfo[issue], cardWidth, g, headerBottom);
                            _boundCards[issue] = card;
                        }
                        var at = new Point(4, ListTop + _rowTops[i] - _scrollY);
                        if (card.Location != at) card.Location = at;
                        if (!card.Visible) card.Visible = true;
                    }
                }
                finally
                {
                    _resultsPanel.ResumeLayout(false);
                }
            }
        }

        private void UpdateScrollBar(int total, int viewHeight)
        {
            int maxScroll = Math.Max(0, total - viewHeight);
            if (_scrollY > maxScroll) _scrollY = maxScroll;
            if (_scrollY < 0) _scrollY = 0;
            if (maxScroll > 0)
            {
                // A scroll bar's Value goes up to Maximum - LargeChange + 1.
                _scrollBar.Minimum = 0;
                _scrollBar.Maximum = total - 1;
                _scrollBar.LargeChange = viewHeight;
                _scrollBar.SmallChange = Math.Max(1, CardBodyFont.Height * 3);
                _scrollBar.Value = _scrollY;
            }
            if (_scrollBar.Visible != (maxScroll > 0)) _scrollBar.Visible = maxScroll > 0;
        }

        /// <summary>Scrolls the list to <paramref name="y"/> pixels from its top
        /// (clamped).</summary>
        private void ScrollFindingsTo(int y)
        {
            _scrollY = Math.Max(0, y);
            Arrange();
        }

        private void OnListMouseWheel(object sender, MouseEventArgs e)
        {
            var lines = SystemInformation.MouseWheelScrollLines;
            int step = lines < 0 ? _resultsPanel.ClientSize.Height : Math.Max(1, lines) * CardBodyFont.Height;
            ScrollFindingsTo(_scrollY - (int)((long)e.Delta * step / 120));
            // The read-only TextBoxes would otherwise scroll their own text.
            if (e is HandledMouseEventArgs handled) handled.Handled = true;
        }

        private void StartRefine()
        {
            if (_rows.Count == 0) return;
            if (_refineTimer == null)
            {
                _refineTimer = new Timer { Interval = 15 };
                _refineTimer.Tick += OnRefineTick;
            }
            _refineTimer.Start();
        }

        /// <summary>
        /// Measures rows still on an estimate for about <see cref="RefineSliceMs"/>,
        /// top to bottom, then gives Studio the thread back. Rows above the view
        /// that change height move the scroll position by as much, so the cards
        /// in view stay exactly where they are; rows below only change how long
        /// the scroll bar says the list is. Stops when every row is exact.
        /// </summary>
        private void OnRefineTick(object sender, EventArgs e)
        {
            if (_thumbTracking) return;
            if (_rows.Count == 0 || IsDisposed) { _refineTimer.Stop(); return; }

            int cardWidth = CardWidth();
            EnsureRowTops();
            // With the view on the prompt log cards (below every row), every row
            // is above it and moves the scroll position: the cards stay put.
            int anchor = FirstRowAt(_scrollY - ListTop);

            int shift = 0;
            bool measured = false, more = false;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            using (var g = MeasureGraphics())
            {
                int header = CardHeaderBottom(g);
                for (int i = 0; i < _rows.Count; i++)
                {
                    var info = _rowInfo[_rows[i]];
                    if (info.Width == cardWidth) continue;
                    if (clock.ElapsedMilliseconds >= RefineSliceMs) { more = true; break; }
                    int before = info.Height;
                    MeasureRow(_rows[i], info, cardWidth, g, header);
                    if (i < anchor) shift += info.Height - before;
                    measured = true;
                }
            }

            if (measured)
            {
                _scrollY += shift;
                Arrange();
                UpdateApplyAllButton();
            }
            if (!more) _refineTimer.Stop();
        }

        /// <summary>
        /// A card for the pool: tick box, segment number, Apply, then a read-only
        /// TextBox for each of the issue, evidence, suggestion, correction and
        /// status, so the text can be selected. Made once and bound to row after
        /// row: its handlers read the finding from the card's Tag when they run.
        /// </summary>
        private Panel MakeCard()
        {
            var card = new Panel { BackColor = CardColor, Cursor = Cursors.Hand, Visible = false };

            var chk = new CheckBox
            {
                AutoSize = true,
                Location = new Point(8, 6),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                Tag = "chk"
            };
            var lblSegNum = new Label
            {
                Font = CardSegNumFont,
                ForeColor = TextColor,
                Location = new Point(26, 6),
                AutoSize = false,
                Tag = "seg"
            };
            var btnApply = new Button
            {
                Text = "Apply",
                Font = CardSmallFont,
                FlatStyle = FlatStyle.Flat,
                ForeColor = CorrColor,
                BackColor = Color.White,
                Cursor = Cursors.Hand,
                Tag = "apply"
            };
            btnApply.FlatAppearance.BorderColor = Color.FromArgb(170, 200, 170);
            btnApply.FlatAppearance.MouseOverBackColor = Color.FromArgb(232, 245, 233);
            _footerTip.SetToolTip(btnApply,
                "Write the correction into this segment. It is set to Draft; with Track Changes on, the fix is tracked.");

            card.Controls.Add(chk);
            card.Controls.Add(lblSegNum);
            card.Controls.Add(btnApply);
            for (int b = 0; b < CardTags.Length; b++)
                card.Controls.Add(CardText("", CardFonts[b], b == 3 ? CorrColor : b == 0 ? TextColor : SuggColor, CardTags[b]));

            // Right-click: copy the card's texts. Filled when it opens, for
            // whichever finding the card shows then.
            var menu = new ContextMenuStrip();
            menu.Opening += (s, e) =>
            {
                var issue = card.Tag as ProofreadingIssue;
                if (issue == null) { e.Cancel = true; return; }
                FillIssueContextMenu(menu, issue);
            };
            card.ContextMenuStrip = menu;
            card.Disposed += (s, e) => menu.Dispose();

            // Tick box: the finding is dealt with, and its row goes.
            chk.CheckedChanged += (s, e) =>
            {
                if (_binding || !chk.Checked) return;
                var issue = card.Tag as ProofreadingIssue;
                if (issue == null) return;
                issue.Dismissed = true;   // #105: the report remembers
                try { IssueDismissed?.Invoke(this, EventArgs.Empty); } catch { }
                int removed = RemoveRows(new[] { issue });
                Arrange();
                if (removed > 0) AfterCardsRemoved();
            };

            btnApply.Click += (s, e) =>
            {
                if (_applying) return;
                var issue = card.Tag as ProofreadingIssue;
                if (issue != null) ApplyRequested?.Invoke(this, new ProofreadIssueEventArgs { Issue = issue });
            };

            WireCardControl(card, card);
            foreach (Control child in card.Controls)
                WireCardControl(card, child);

            _resultsPanel.Controls.Add(card);
            return card;
        }

        /// <summary>Shows <paramref name="issue"/> on <paramref name="card"/>,
        /// laid out for <paramref name="cardWidth"/> exactly as its row was measured.</summary>
        private void BindCard(Panel card, ProofreadingIssue issue, RowInfo info, int cardWidth,
            IDeviceContext dc, int headerBottom)
        {
            var texts = CardTexts(issue, info);
            var heights = new int[texts.Length];
            int height = CardBlocks(issue, info, cardWidth, dc, headerBottom, heights);

            card.SuspendLayout();
            card.Tag = issue;
            card.BackColor = CardColor;
            card.Size = new Size(cardWidth, height);

            int top = headerBottom;
            foreach (Control c in card.Controls)
            {
                var tag = c.Tag as string;
                if (c is CheckBox chk)
                {
                    _binding = true;
                    try { chk.Checked = false; } finally { _binding = false; }
                }
                else if (tag == "seg")
                {
                    // "Segment 3 of Acme - Text.docx" in a merged document, where
                    // the number alone is in every file; kept to one short line.
                    var label = issue.Label;
                    if (label.Length > 60) label = label.Substring(0, 57) + "\u2026";
                    c.Text = "\u26A0 Segment " + label;
                    var m = TextRenderer.MeasureText(dc, c.Text, CardSegNumFont);
                    c.Size = new Size(m.Width + 4, m.Height);
                }
                else if (tag == "apply")
                {
                    var b = (Button)c;
                    b.Size = ApplyButtonSize();
                    b.Location = new Point(Math.Max(26 + 8, cardWidth - b.Width - 8), 3);
                    b.Enabled = !_applying;
                    b.Visible = info.CanApply;
                }
            }

            // The texts, in order, each under the last.
            for (int b = 0; b < CardTags.Length; b++)
            {
                var tb = (TextBox)card.Controls.Cast<Control>().First(c => (c.Tag as string) == CardTags[b]);
                tb.BackColor = CardColor;
                if (texts[b] == null)
                {
                    tb.Visible = false;
                    tb.Text = "";
                    continue;
                }
                tb.Text = texts[b];
                if (b == 4) tb.ForeColor = info.StatusColor;
                tb.Location = new Point(CardTextLeft, top);
                tb.Size = new Size(cardWidth - 20, heights[b]);
                tb.Visible = true;
                top += heights[b] + CardBlockGap;
            }
            card.ResumeLayout(false);
        }

        private void UnbindCard(ProofreadingIssue issue)
        {
            if (!_boundCards.TryGetValue(issue, out var card)) return;
            _boundCards.Remove(issue);
            card.Visible = false;
            card.Tag = null;
            _freeCards.Push(card);
        }

        /// <summary>
        /// Hover, context menu, click-to-navigate and the mouse wheel for one
        /// control on a card, or the card itself. TextBox controls keep the I-beam
        /// for text selection; the tick box and Apply have their own clicks.
        /// </summary>
        private void WireCardControl(Panel card, Control ctrl)
        {
            ctrl.MouseWheel += OnListMouseWheel;
            ctrl.MouseEnter += (s, e) =>
            {
                card.BackColor = HoverColor;
                // Update TextBox BackColor so selected text looks right
                foreach (Control c in card.Controls)
                    if (c is TextBox tb) tb.BackColor = HoverColor;
            };
            ctrl.MouseLeave += (s, e) =>
            {
                card.BackColor = CardColor;
                foreach (Control c in card.Controls)
                    if (c is TextBox tb) tb.BackColor = CardColor;
            };
            if (ctrl is CheckBox || ctrl is Button) return;

            if (ctrl != card) ctrl.ContextMenuStrip = card.ContextMenuStrip;
            // TextBox: let users select text – don't hijack click for navigation
            if (!(ctrl is TextBox))
            {
                ctrl.Click += (s, e) => OnIssueCardClick(card.Tag as ProofreadingIssue);
                ctrl.Cursor = Cursors.Hand;
            }
        }

        private static TextBox CardText(string text, Font font, Color color, string tag) => new TextBox
        {
            Text = text,
            Font = font,
            ForeColor = color,
            BackColor = CardColor,
            Location = new Point(8, 24),
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            Multiline = true,
            WordWrap = true,
            TabStop = false,
            Tag = tag
        };

        /// <summary>A TextBox breaks lines only at CR LF; a translation's soft
        /// returns are bare LFs.</summary>
        private static string ForTextBox(string text) =>
            (text ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");

        /// <summary>Takes the rows of <paramref name="issues"/> out of the list
        /// in one pass and counts them as addressed. Returns how many there were.</summary>
        private int RemoveRows(IEnumerable<ProofreadingIssue> issues)
        {
            var gone = new HashSet<ProofreadingIssue>(issues ?? Enumerable.Empty<ProofreadingIssue>());
            gone.RemoveWhere(i => i == null || !_rowInfo.ContainsKey(i));
            if (gone.Count == 0) return 0;

            foreach (var issue in gone)
            {
                UnbindCard(issue);
                _rowInfo.Remove(issue);
            }
            _rows.RemoveAll(gone.Contains);
            _rowTopsStale = true;
            _checkedCount += gone.Count;
            return gone.Count;
        }

        // ─── Apply ────────────────────────────────────────────────

        /// <summary>
        /// What Apply did, for any number of findings at once (one Apply all
        /// step): the rows in <paramref name="done"/> go, as a ticked-off one
        /// does; each row in <paramref name="notes"/> stays and shows its line -
        /// a refusal in red, or, for one that was applied with something to check,
        /// in amber (<paramref name="appliedWithNote"/>), without its Apply button.
        /// Only the noted rows are measured again.
        /// </summary>
        public void ShowApplyOutcome(IEnumerable<ProofreadingIssue> done,
            IEnumerable<KeyValuePair<ProofreadingIssue, string>> notes,
            ISet<ProofreadingIssue> appliedWithNote = null)
        {
            int removed = RemoveRows(done);

            var noteList = (notes ?? Enumerable.Empty<KeyValuePair<ProofreadingIssue, string>>()).ToList();
            if (noteList.Count > 0)
            {
                int cardWidth = CardWidth();
                using (var g = MeasureGraphics())
                {
                    int header = CardHeaderBottom(g);
                    foreach (var kv in noteList)
                    {
                        if (!_rowInfo.TryGetValue(kv.Key, out var info)) continue;
                        bool applied = appliedWithNote != null && appliedWithNote.Contains(kv.Key);
                        Resolve(kv.Key, info);   // so it never overwrites this line later
                        info.Status = kv.Value;
                        info.StatusColor = applied ? NoteColor : RefusedColor;
                        if (applied) info.CanApply = false;   // written: nothing left to apply
                        MeasureRow(kv.Key, info, cardWidth, g, header);
                        UnbindCard(kv.Key);   // bound again, with its new line, by Arrange
                    }
                }
            }

            Arrange();
            if (removed > 0) AfterCardsRemoved();
            else UpdateApplyAllButton();
        }

        /// <summary>
        /// Apply all running, or not. While it runs the cards' Apply buttons are
        /// off and Apply all reads "Stop (done/total)".
        /// </summary>
        public void SetApplying(bool running, int done = 0, int total = 0)
        {
            _applying = running;
            if (_btnApplyAll != null)
                _btnApplyAll.Text = running ? $"Stop ({done}/{total})" : "Apply all";
            foreach (var card in _boundCards.Values)
                foreach (Control c in card.Controls)
                    if (c is Button b && (string)b.Tag == "apply") b.Enabled = !running;
            UpdateApplyAllButton();
            UpdateClearButtonSize();
            OnControlResize(this, EventArgs.Empty);
        }

        /// <summary>
        /// Apply all's outcome in the footer, in place of the run line until the
        /// next report; null puts the run line back. Also how many findings were
        /// added as Trados comments after the run. The footer shares its row
        /// with the cost note and is narrow in a docked panel, so the text is cut
        /// with an ellipsis and given whole in the tooltip.
        /// </summary>
        public void ShowApplySummary(string summary)
        {
            if (_lblFooter == null) return;
            _lblFooter.Text = string.IsNullOrEmpty(summary) ? _footerBase : summary;
            // The label's own ellipsis tooltip stays off while this ToolTip is
            // associated with it (it is, from SetResults on), so it shows once.
            if (!string.IsNullOrEmpty(summary)) _footerTip.SetToolTip(_lblFooter, summary);
        }

        private void AfterCardsRemoved()
        {
            UpdateIssueCountLabel();
            UpdateApplyAllButton();

            // Show empty state if all issues addressed
            if (_checkedCount >= _issueCount)
                ShowEmptyText("All issues addressed \u2013 well done!");
        }

        /// <summary>The empty-list text, which fills the panel - so only while
        /// there are no request cards for it to cover. The header's count says
        /// the same when there are.</summary>
        private void ShowEmptyText(string text)
        {
            _lblEmpty.Text = text;
            _lblEmpty.Visible = _promptCards.Count == 0;
        }

        /// <summary>Apply all is on while it runs (it is Stop then) and while
        /// any row still has an Apply button - or may have, for a row not yet
        /// worked out. Apply all checks each correction again before it starts.</summary>
        private void UpdateApplyAllButton()
        {
            if (_btnApplyAll == null) return;
            _btnApplyAll.Enabled = _applying || _rowInfo.Any(kv => kv.Value.Resolved
                ? kv.Value.CanApply
                : kv.Key.CorrectedTarget != null && !kv.Key.Applied);
        }

        /// <summary>
        /// Clears all results and shows empty state.
        /// </summary>
        public void ClearResults()
        {
            ClearResultsInternal();
            _issueCount = 0;
            _lblIssueCount.Text = "";
            _footerBase = "";
            _lblFooter.Text = "";
            _footerTip.SetToolTip(_lblFooter, "");
            if (_btnSave != null) _btnSave.Enabled = false;   // #105
            UpdateApplyAllButton();
            _lblEmpty.Text = "No reports yet";
            _lblEmpty.Visible = true;
        }

        /// <summary>
        /// Notes in the footer that the report was written to disk (#105); the full
        /// path is the footer's tooltip.
        /// </summary>
        public void ShowSavedPath(string path)
        {
            if (string.IsNullOrEmpty(path) || _lblFooter == null) return;
            _footerBase += "  \u2013  saved as " + System.IO.Path.GetFileName(path);
            _lblFooter.Text = _footerBase;
            _footerTip.SetToolTip(_lblFooter, path);
        }

        // ─── Prompt Log ─────────────────────────────────────────────

        // How near the bottom the view may be and still count as "at the bottom".
        private const int FollowSlack = 8;

        /// <summary>
        /// Adds a prompt log card at the bottom of the list, as the chat does.
        /// The view follows it - scrolls to the very bottom - if it was at the
        /// bottom already, or the list did not scroll yet; a view scrolled up to
        /// read something stays where it is.
        /// </summary>
        public void AddPromptLog(PromptLogEntry entry)
        {
            if (entry == null) return;

            _lblEmpty.Visible = false;

            // Before anything changes: is the view at the bottom (or is there
            // nothing to scroll)?
            bool follow = _scrollY >= ListHeight() - ViewHeight() - FollowSlack;

            // Cap entries: the oldest card goes - the first one. If it was above
            // the view, the view moves up with what is under it, so it stays on
            // what it showed.
            if (_promptCards.Count >= MaxPromptLogEntries)
            {
                var oldest = _promptCards[0];
                int oldestTop = ListTop + _rowTops[_rows.Count];
                _scrollY -= Math.Min(oldest.Height + 4, Math.Max(0, _scrollY - oldestTop));
                _promptCards.RemoveAt(0);
                _resultsPanel.Controls.Remove(oldest);
                oldest.Dispose();
            }

            _resultsPanel.SuspendLayout();

            // Shared fonts, as for the findings' cards: one set, not three per call.
            var bodyFont = CardBodyFont;
            var headerFont = CardSegNumFont;
            var smallFont = PromptSmallFont;

            var availableWidth = CardWidth();

            var card = new Panel
            {
                Width = availableWidth,
                BackColor = PromptCardColor,
                Padding = new Padding(8, 6, 8, 6),
                Visible = false,
                Tag = entry
            };

            // Header: Feature label + timestamp
            var headerText = $"{entry.FeatureLabel}  {entry.Timestamp:HH:mm:ss}";
            var headerHeight = Math.Max(16, TextRenderer.MeasureText(headerText, headerFont).Height);
            var lblHeader = new Label
            {
                Text = headerText,
                Font = headerFont,
                ForeColor = PromptHeaderColor,
                Location = new Point(8, 6),
                AutoSize = true
            };
            card.Controls.Add(lblHeader);

            // Summary line
            var summaryY = 6 + headerHeight + 2;
            var summaryHeight = Math.Max(16, TextRenderer.MeasureText(entry.SummaryLine, bodyFont).Height);
            var lblSummary = new Label
            {
                Text = entry.SummaryLine,
                Font = bodyFont,
                ForeColor = TextColor,
                Location = new Point(8, summaryY),
                AutoSize = true
            };
            card.Controls.Add(lblSummary);

            int yPos = summaryY + summaryHeight + 4;
            var textWidth = availableWidth - 20;

            // Expandable sections
            if (!string.IsNullOrEmpty(entry.SystemPrompt))
            {
                yPos = AddExpandableSection(card, "System prompt", entry.SystemPrompt,
                    yPos, textWidth, smallFont, bodyFont);
            }

            if (entry.Messages != null && entry.Messages.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var msg in entry.Messages)
                    sb.AppendLine($"[{msg.Role}]: {msg.Content}");
                yPos = AddExpandableSection(card, "Messages", sb.ToString().TrimEnd(),
                    yPos, textWidth, smallFont, bodyFont);
            }
            else if (!string.IsNullOrEmpty(entry.UserPrompt))
            {
                yPos = AddExpandableSection(card, "User prompt", entry.UserPrompt,
                    yPos, textWidth, smallFont, bodyFont);
            }

            if (!string.IsNullOrEmpty(entry.Response))
            {
                yPos = AddExpandableSection(card, "Response", entry.Response,
                    yPos, textWidth, smallFont, bodyFont);
            }
            else if (entry.IsError && !string.IsNullOrEmpty(entry.ErrorMessage))
            {
                yPos = AddExpandableSection(card, "Error", entry.ErrorMessage,
                    yPos, textWidth, smallFont, bodyFont);
            }

            // Copy All link
            var copyAllSize = TextRenderer.MeasureText("Copy all", smallFont);
            var lnkCopyAll = new LinkLabel
            {
                Text = "Copy all",
                Font = smallFont,
                Location = new Point(8, yPos),
                Size = new System.Drawing.Size(copyAllSize.Width + 4, Math.Max(14, copyAllSize.Height)),
                LinkColor = PromptHeaderColor,
                ActiveLinkColor = PromptHeaderColor
            };
            lnkCopyAll.Click += (s, e) =>
            {
                try { Clipboard.SetText(entry.ToFullText()); }
                catch { }
            };
            card.Controls.Add(lnkCopyAll);

            // Height from the controls' positions. Not from Visible, which reads
            // false for every control on a card that is not in view; the content
            // boxes, collapsed, are the only ones meant to take no room.
            var copyAllHeight = Math.Max(14, copyAllSize.Height);
            card.Height = Math.Max(40, yPos + copyAllHeight + 8);

            // Hover effect, and the wheel scrolls the whole list
            Action<Control> applyHover = (ctrl) =>
            {
                ctrl.MouseEnter += (s, e) => card.BackColor = PromptHoverColor;
                ctrl.MouseLeave += (s, e) => card.BackColor = PromptCardColor;
                if (!(ctrl is TextBox)) ctrl.MouseWheel += OnListMouseWheel;
            };
            applyHover(card);
            foreach (Control child in card.Controls)
                applyHover(child);

            _resultsPanel.Controls.Add(card);
            _promptCards.Add(card);
            _resultsPanel.ResumeLayout(false);

            if (!follow) { Arrange(); return; }
            // To the bottom. Twice at most: rows coming into view on the way are
            // measured, which can change how long the list is.
            ScrollFindingsTo(ListHeight() - ViewHeight());
            int end = ListHeight() - ViewHeight();
            if (_scrollY < end) ScrollFindingsTo(end);
        }

        private int AddExpandableSection(Panel card, string title, string content,
            int yPos, int textWidth, Font linkFont, Font contentFont)
        {
            var toggleText = $"Show {title.ToLowerInvariant()}...";
            var toggleSize = TextRenderer.MeasureText(toggleText, linkFont);
            var toggleHeight = Math.Max(14, toggleSize.Height);
            var lnkToggle = new LinkLabel
            {
                Text = toggleText,
                Font = linkFont,
                Location = new Point(8, yPos),
                Size = new System.Drawing.Size(Math.Max(80, toggleSize.Width + 4), toggleHeight),
                LinkColor = PromptHeaderColor,
                ActiveLinkColor = PromptHeaderColor,
                Tag = "toggle"
            };

            var txtContent = new TextBox
            {
                Text = content,
                Font = contentFont,
                ForeColor = TextColor,
                BackColor = Color.FromArgb(245, 248, 252),
                Location = new Point(8, yPos + toggleHeight + 2),
                Multiline = true,
                ReadOnly = true,
                WordWrap = true,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.FixedSingle,
                Visible = false,
                Width = textWidth,
                Height = Math.Min(150, Math.Max(40, content.Split('\n').Length * 16 + 10))
            };

            var copySize = TextRenderer.MeasureText("Copy", linkFont);
            var lnkCopy = new LinkLabel
            {
                Text = "Copy",
                Font = linkFont,
                Size = new System.Drawing.Size(copySize.Width + 4, Math.Max(14, copySize.Height)),
                LinkColor = PromptHeaderColor,
                ActiveLinkColor = PromptHeaderColor,
                Visible = false,
                Tag = "copy"
            };
            lnkCopy.Location = new Point(8 + toggleSize.Width + 12, yPos);
            lnkCopy.Click += (s, e) =>
            {
                try { Clipboard.SetText(content); }
                catch { }
            };

            var capturedCard = card;
            lnkToggle.Click += (s, e) =>
            {
                txtContent.Visible = !txtContent.Visible;
                lnkCopy.Visible = txtContent.Visible;
                lnkToggle.Text = txtContent.Visible
                    ? $"Hide {title.ToLowerInvariant()}"
                    : $"Show {title.ToLowerInvariant()}...";

                // Recalculate card height
                RecalcPromptCardHeight(capturedCard);
                Arrange();
            };

            // Escape key collapses the expanded section
            txtContent.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    txtContent.Visible = false;
                    lnkCopy.Visible = false;
                    lnkToggle.Text = $"Show {title.ToLowerInvariant()}...";
                    RecalcPromptCardHeight(capturedCard);
                    Arrange();
                    e.Handled = true;
                }
            };

            card.Controls.Add(lnkToggle);
            card.Controls.Add(txtContent);
            card.Controls.Add(lnkCopy);

            return yPos + toggleHeight + 2;
        }

        private void RecalcPromptCardHeight(Panel card)
        {
            // Runs on a click on the card, which is in view, so Visible is the
            // controls' own setting here.
            int maxBottom = 0;
            foreach (Control ctrl in card.Controls)
            {
                if (!ctrl.Visible) continue;
                int bottom = ctrl.Top + ctrl.Height;
                if (bottom > maxBottom) maxBottom = bottom;
            }
            card.Height = Math.Max(40, maxBottom + 8);
        }

        // ─── Internal Helpers ──────────────────────────────────────

        /// <summary>Takes every finding out of the list, and puts the view at its
        /// top; the prompt log cards stay. The pool's cards are kept, unbound,
        /// for the next report.</summary>
        private void ClearFindings()
        {
            foreach (var issue in _boundCards.Keys.ToList())
                UnbindCard(issue);
            _rows.Clear();
            _rowInfo.Clear();
            _rowTopsStale = true;
            _scrollY = 0;
            Arrange();
        }

        /// <summary>Empties the list: every row, every prompt log card (Clear).</summary>
        private void ClearResultsInternal()
        {
            ClearFindings();

            _resultsPanel.SuspendLayout();
            foreach (var p in _promptCards)
            {
                _resultsPanel.Controls.Remove(p);
                p.Dispose();
            }
            _promptCards.Clear();
            _resultsPanel.ResumeLayout(false);

            _scrollY = 0;
            Arrange();
        }

        private void UpdateIssueCountLabel()
        {
            if (_checkedCount > 0)
                _lblIssueCount.Text = $"{_issueCount} issue{(_issueCount != 1 ? "s" : "")} found – {_checkedCount} addressed";
            // Text is set in SetResults initially; only update when checked count changes
            OnControlResize(this, EventArgs.Empty);
        }

        /// <summary>The card's right-click menu, for the finding it shows when it
        /// opens. The previous items are disposed: the menu belongs to a pooled
        /// card and is filled again on every opening.</summary>
        private static void FillIssueContextMenu(ContextMenuStrip menu, ProofreadingIssue issue)
        {
            var old = menu.Items.Cast<ToolStripItem>().ToList();
            menu.Items.Clear();
            foreach (var item in old) item.Dispose();

            Action<string, string> add = (label, text) =>
            {
                if (string.IsNullOrEmpty(text)) return;
                menu.Items.Add(new ToolStripMenuItem(label, null, (s, e) =>
                {
                    try { Clipboard.SetText(text); }
                    catch { }
                }) { Font = MenuFont });
            };

            add("Copy issue description", issue.IssueDescription);
            add("Copy evidence", issue.Evidence);
            add("Copy suggestion", issue.Suggestion);
            add("Copy correction", issue.CorrectedTarget);

            menu.Items.Add(new ToolStripSeparator());

            var all = $"Segment {issue.Label}\n{issue.IssueDescription}";
            if (!string.IsNullOrEmpty(issue.Evidence))
                all += $"\nEvidence: {issue.Evidence}";
            if (!string.IsNullOrEmpty(issue.Suggestion))
                all += $"\nSuggestion: {issue.Suggestion}";
            if (issue.CorrectedTarget != null)
                all += $"\nCorrection: {issue.CorrectedTarget}";
            add("Copy all", all);
        }

        private void OnIssueCardClick(ProofreadingIssue issue)
        {
            if (issue == null) return;
            NavigateToSegmentRequested?.Invoke(this, new NavigateToSegmentEventArgs
            {
                ParagraphUnitId = issue.ParagraphUnitId,
                SegmentId = issue.SegmentId
            });
        }

        /// <summary>The panel was resized. At a new width every row gets a new
        /// estimate, the rows in view are measured at once and the rest while
        /// Studio is idle - a docked panel being dragged resizes many times a
        /// second, and estimating is cheap where measuring is not.</summary>
        private void OnResultsPanelResize(object sender, EventArgs e)
        {
            if (_rows.Count > 0 && CardWidth() != _layoutWidth)
            {
                EstimateAllRows(CardWidth());
                StartRefine();
            }
            Arrange();
        }
    }
}
