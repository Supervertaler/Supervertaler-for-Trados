using System;
using System.Collections.Generic;
using System.Drawing;
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

        // Proofreading card colours
        private static readonly Color CardColor = Color.FromArgb(255, 253, 231);      // #FFFDE7
        private static readonly Color HoverColor = Color.FromArgb(255, 249, 196);
        private static readonly Color TextColor = Color.FromArgb(60, 60, 60);
        private static readonly Color SuggColor = Color.FromArgb(120, 120, 120);

        // Prompt log card colours
        private static readonly Color PromptCardColor = Color.FromArgb(227, 242, 253);  // #E3F2FD
        private static readonly Color PromptHoverColor = Color.FromArgb(207, 232, 252);
        private static readonly Color PromptHeaderColor = Color.FromArgb(30, 90, 160);

        private const int MaxPromptLogEntries = 500;
        private int _promptLogCount;

        /// <summary>Fired when user clicks an issue card to navigate to that segment.</summary>
        public event EventHandler<NavigateToSegmentEventArgs> NavigateToSegmentRequested;

        /// <summary>Fired when user clicks "Clear Results".</summary>
        public event EventHandler ClearResultsRequested;

        /// <summary>Fired when the user clicks "Save report\u2026" (#105). Only raised
        /// while a proofreading report is loaded; the button is disabled otherwise.</summary>
        public event EventHandler SaveReportRequested;

        /// <summary>Gets the number of issues currently displayed.</summary>
        public int IssueCount => _issueCount;

        /// <summary>Gets whether "Also add as Trados comments" is checked.</summary>
        // AddAsComments moved to BatchTranslateControl

        public ReportsControl()
        {
            BuildUI();
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

            // ─── Scrollable results panel (fills remaining space) ──
            _resultsPanel = new Panel
            {
                Location = new Point(0, y),
                AutoScroll = true,
                BackColor = Color.White,
                Padding = new Padding(8, 4, 8, 4),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };
            Controls.Add(_resultsPanel);

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

            // Save sits immediately left of Clear (#105).
            var leftmostButton = _btnClear.Left;
            if (_btnSave != null)
            {
                _btnSave.Location = new Point(Math.Max(HeaderLeft, _btnClear.Left - _btnSave.Width - HeaderSpacing), _btnClear.Top);
                leftmostButton = _btnSave.Left;
            }

            // Position issue count label between the title and the buttons; if the
            // panel is too narrow for it, hide it rather than let it run under them
            // (the count is also on the tab badge).
            var labelLeft = leftmostButton - _lblIssueCount.Width - HeaderSpacing;
            _lblIssueCount.Visible = labelLeft >= _lblHeader.Right + HeaderSpacing;
            _lblIssueCount.Location = new Point(
                Math.Max(_lblHeader.Right + HeaderSpacing, labelLeft),
                _btnClear.Top + Math.Max(0, (_btnClear.Height - _lblIssueCount.Height) / 2));

            var resultsTop = Math.Max(_lblHeader.Bottom, _btnClear.Bottom) + HeaderSpacing;
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

            _btnClear.Size = new Size(
                Math.Max(ClearButtonMinWidth, measured.Width + ClearButtonHorizontalPadding),
                Math.Max(ClearButtonMinHeight, measured.Height + ClearButtonVerticalPadding));
            if (_btnSave != null)
            {
                var m2 = TextRenderer.MeasureText(_btnSave.Text, _btnSave.Font);
                _btnSave.Size = new Size(
                    Math.Max(ClearButtonMinWidth, m2.Width + ClearButtonHorizontalPadding),
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

            ClearResultsInternal();

            _issueCount = report.IssueCount;
            var totalChecked = report.TotalSegmentsChecked;

            // Update count label
            _lblIssueCount.Text = $"{_issueCount} issue{(_issueCount != 1 ? "s" : "")} found in {totalChecked} segment{(totalChecked != 1 ? "s" : "")}";
            // Force re-position after text change
            OnControlResize(this, EventArgs.Empty);

            // Update footer
            _lblFooter.Text = $"Last run: {report.Timestamp:HH:mm:ss} \u2014 {report.Duration.TotalSeconds:F1}s";
            _footerTip.SetToolTip(_lblFooter, "");
            if (_btnSave != null) _btnSave.Enabled = true;   // #105

            if (_issueCount == 0)
            {
                _lblEmpty.Text = "No issues found \u2014 all segments look good!";
                _lblEmpty.Visible = true;
                return;
            }

            _lblEmpty.Visible = false;
            _resultsPanel.SuspendLayout();

            var bodyFont = new Font("Segoe UI", 8.5f);
            var segNumFont = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            var suggFont = new Font("Segoe UI", 8f);

            _checkedCount = 0;
            int yPos = 4;

            foreach (var issue in report.Issues)
            {
                if (issue.IsOk) continue;

                var card = new Panel
                {
                    Location = new Point(4, yPos),
                    BackColor = CardColor,
                    Cursor = Cursors.Hand,
                    Padding = new Padding(8, 6, 8, 6),
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                    Tag = issue
                };

                // Checkbox for marking issue as addressed
                var chk = new CheckBox
                {
                    AutoSize = true,
                    Location = new Point(8, 6),
                    BackColor = Color.Transparent,
                    Cursor = Cursors.Hand,
                    Tag = "chk"
                };

                // Segment number + warning icon (offset right for checkbox)
                var lblSegNum = new Label
                {
                    Text = $"\u26A0 Segment {issue.SegmentNumber}",
                    Font = segNumFont,
                    ForeColor = TextColor,
                    Location = new Point(26, 6),
                    AutoSize = true
                };

                // Issue description (TextBox so users can select and copy text)
                var txtDesc = new TextBox
                {
                    Text = issue.IssueDescription ?? "",
                    Font = bodyFont,
                    ForeColor = TextColor,
                    BackColor = CardColor,
                    Location = new Point(8, 24),
                    ReadOnly = true,
                    BorderStyle = BorderStyle.None,
                    Multiline = true,
                    WordWrap = true,
                    TabStop = false,
                    Tag = "desc"
                };

                // Evidence (if model cited specific source segments – e.g. for terminology
                // consistency claims). Rendered between description and suggestion in italic
                // grey so the eye reads it as "the *why* for the issue", not as an action.
                TextBox txtEvidence = null;
                if (!string.IsNullOrEmpty(issue.Evidence))
                {
                    txtEvidence = new TextBox
                    {
                        Text = "Evidence: " + issue.Evidence,
                        Font = new Font("Segoe UI", 8f, FontStyle.Italic),
                        ForeColor = SuggColor,
                        BackColor = CardColor,
                        Location = new Point(8, 44),
                        ReadOnly = true,
                        BorderStyle = BorderStyle.None,
                        Multiline = true,
                        WordWrap = true,
                        TabStop = false,
                        Tag = "evidence"
                    };
                }

                // Suggestion (if available, also a selectable TextBox)
                TextBox txtSugg = null;
                if (!string.IsNullOrEmpty(issue.Suggestion))
                {
                    txtSugg = new TextBox
                    {
                        Text = "Suggestion: " + issue.Suggestion,
                        Font = suggFont,
                        ForeColor = SuggColor,
                        BackColor = CardColor,
                        Location = new Point(8, 44),
                        ReadOnly = true,
                        BorderStyle = BorderStyle.None,
                        Multiline = true,
                        WordWrap = true,
                        TabStop = false,
                        Tag = "sugg"
                    };
                }

                card.Controls.Add(chk);
                card.Controls.Add(lblSegNum);
                card.Controls.Add(txtDesc);
                if (txtEvidence != null)
                    card.Controls.Add(txtEvidence);
                if (txtSugg != null)
                    card.Controls.Add(txtSugg);

                // Right-click context menu for copying text
                var ctxMenu = BuildIssueContextMenu(issue, txtSugg != null);
                card.ContextMenuStrip = ctxMenu;
                lblSegNum.ContextMenuStrip = ctxMenu;
                txtDesc.ContextMenuStrip = ctxMenu;
                if (txtEvidence != null)
                    txtEvidence.ContextMenuStrip = ctxMenu;
                if (txtSugg != null)
                    txtSugg.ContextMenuStrip = ctxMenu;

                // Checkbox toggle – change card colour and text when checked
                var capturedCard = card;
                var capturedSegNum = lblSegNum;
                var capturedDesc = txtDesc;
                var capturedSugg = txtSugg;
                chk.CheckedChanged += (s, e) =>
                {
                    if (!chk.Checked) return;

                    // Remove the card from the panel
                    _resultsPanel.SuspendLayout();
                    _resultsPanel.Controls.Remove(capturedCard);
                    capturedCard.Dispose();

                    // Update count
                    _checkedCount++;
                    UpdateIssueCountLabel();

                    // Re-flow remaining cards
                    RelayoutCards();
                    _resultsPanel.ResumeLayout(true);

                    // Show empty state if all issues addressed
                    if (_checkedCount >= _issueCount)
                    {
                        _lblEmpty.Text = "All issues addressed \u2014 well done!";
                        _lblEmpty.Visible = true;
                    }
                };

                // Hover effect – apply to card and all children
                // TextBox controls keep IBeam cursor for text selection;
                // navigation on click only for non-checkbox, non-textbox controls
                Action<Control> applyHover = null;
                applyHover = (ctrl) =>
                {
                    ctrl.MouseEnter += (s, e) =>
                    {
                        capturedCard.BackColor = HoverColor;
                        // Update TextBox BackColor so selected text looks right
                        foreach (Control c in capturedCard.Controls)
                            if (c is TextBox tb) tb.BackColor = HoverColor;
                    };
                    ctrl.MouseLeave += (s, e) =>
                    {
                        capturedCard.BackColor = CardColor;
                        foreach (Control c in capturedCard.Controls)
                            if (c is TextBox tb) tb.BackColor = CardColor;
                    };
                    // TextBox: let users select text – don't hijack click for navigation
                    // CheckBox: has its own click handler
                    if (!(ctrl is CheckBox) && !(ctrl is TextBox))
                    {
                        ctrl.Click += (s, e) => OnIssueCardClick(capturedCard.Tag as ProofreadingIssue);
                        ctrl.Cursor = Cursors.Hand;
                    }
                };
                applyHover(card);
                foreach (Control child in card.Controls)
                    applyHover(child);

                _resultsPanel.Controls.Add(card);

                // Layout the card – need to measure text height
                LayoutCard(card, lblSegNum, txtDesc, txtEvidence, txtSugg);

                yPos += card.Height + 4;
            }

            _resultsPanel.ResumeLayout(true);

            // Re-layout cards on panel resize
            _resultsPanel.Resize -= OnResultsPanelResize;
            _resultsPanel.Resize += OnResultsPanelResize;
        }

        /// <summary>
        /// Clears all results and shows empty state.
        /// </summary>
        public void ClearResults()
        {
            ClearResultsInternal();
            _issueCount = 0;
            _lblIssueCount.Text = "";
            _lblFooter.Text = "";
            _footerTip.SetToolTip(_lblFooter, "");
            if (_btnSave != null) _btnSave.Enabled = false;   // #105
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
            _lblFooter.Text += "  \u2014  saved as " + System.IO.Path.GetFileName(path);
            _footerTip.SetToolTip(_lblFooter, path);
        }

        // ─── Prompt Log ─────────────────────────────────────────────

        /// <summary>
        /// Adds a prompt log card to the results panel.
        /// Cards are inserted at the top so the latest call is always visible.
        /// </summary>
        public void AddPromptLog(PromptLogEntry entry)
        {
            if (entry == null) return;

            _lblEmpty.Visible = false;

            // Cap entries
            if (_promptLogCount >= MaxPromptLogEntries)
            {
                // Remove the oldest prompt log card (last one with PromptCardColor)
                for (int i = _resultsPanel.Controls.Count - 1; i >= 0; i--)
                {
                    var ctrl = _resultsPanel.Controls[i];
                    if (ctrl is Panel p && p.Tag is PromptLogEntry)
                    {
                        _resultsPanel.Controls.RemoveAt(i);
                        ctrl.Dispose();
                        _promptLogCount--;
                        break;
                    }
                }
            }

            _resultsPanel.SuspendLayout();

            var bodyFont = new Font("Segoe UI", 8.5f);
            var headerFont = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            var smallFont = new Font("Segoe UI", 7.5f);

            var availableWidth = _resultsPanel.ClientSize.Width
                - SystemInformation.VerticalScrollBarWidth - 24;
            if (availableWidth < 100) availableWidth = 300;

            var card = new Panel
            {
                Width = availableWidth,
                BackColor = PromptCardColor,
                Padding = new Padding(8, 6, 8, 6),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
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

            var copyAllHeight = Math.Max(14, copyAllSize.Height);
            // Calculate height from actual control positions
            int maxBottom = 0;
            foreach (Control ctrl in card.Controls)
            {
                if (ctrl.Visible)
                {
                    int b = ctrl.Top + ctrl.Height;
                    if (b > maxBottom) maxBottom = b;
                }
            }
            card.Height = Math.Max(Math.Max(40, yPos + copyAllHeight + 8), maxBottom + 8);

            // Hover effect
            Action<Control> applyHover = null;
            applyHover = (ctrl) =>
            {
                ctrl.MouseEnter += (s, e) => card.BackColor = PromptHoverColor;
                ctrl.MouseLeave += (s, e) => card.BackColor = PromptCardColor;
            };
            applyHover(card);
            foreach (Control child in card.Controls)
                applyHover(child);

            _resultsPanel.Controls.Add(card);
            _promptLogCount++;

            // Re-flow all cards (sorts prompt log entries by timestamp descending)
            RelayoutCards();
            _resultsPanel.ResumeLayout(true);

            // Scroll to top so the newest entry is visible
            _resultsPanel.AutoScrollPosition = new Point(0, 0);
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
                RelayoutCards();
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
                    RelayoutCards();
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

        private void ClearResultsInternal()
        {
            _resultsPanel.SuspendLayout();
            for (int i = _resultsPanel.Controls.Count - 1; i >= 0; i--)
            {
                var ctrl = _resultsPanel.Controls[i];
                if (ctrl != _lblEmpty)
                {
                    _resultsPanel.Controls.RemoveAt(i);
                    ctrl.Dispose();
                }
            }
            _resultsPanel.ResumeLayout();
        }

        private void UpdateIssueCountLabel()
        {
            var totalChecked = _issueCount; // total issues, not total segments
            if (_checkedCount > 0)
                _lblIssueCount.Text = $"{_issueCount} issue{(_issueCount != 1 ? "s" : "")} found – {_checkedCount} addressed";
            // Text is set in SetResults initially; only update when checked count changes
            OnControlResize(this, EventArgs.Empty);
        }

        private ContextMenuStrip BuildIssueContextMenu(ProofreadingIssue issue, bool hasSuggestion)
        {
            var menu = new ContextMenuStrip();
            var menuFont = new Font("Segoe UI", 9f);

            var miCopyIssue = new ToolStripMenuItem("Copy issue description", null, (s, e) =>
            {
                try { if (!string.IsNullOrEmpty(issue.IssueDescription)) Clipboard.SetText(issue.IssueDescription); }
                catch { }
            }) { Font = menuFont };
            menu.Items.Add(miCopyIssue);

            if (!string.IsNullOrEmpty(issue.Evidence))
            {
                var miCopyEv = new ToolStripMenuItem("Copy evidence", null, (s, e) =>
                {
                    try { Clipboard.SetText(issue.Evidence); }
                    catch { }
                }) { Font = menuFont };
                menu.Items.Add(miCopyEv);
            }

            if (hasSuggestion)
            {
                var miCopySugg = new ToolStripMenuItem("Copy suggestion", null, (s, e) =>
                {
                    try { if (!string.IsNullOrEmpty(issue.Suggestion)) Clipboard.SetText(issue.Suggestion); }
                    catch { }
                }) { Font = menuFont };
                menu.Items.Add(miCopySugg);
            }

            menu.Items.Add(new ToolStripSeparator());

            var miCopyAll = new ToolStripMenuItem("Copy all", null, (s, e) =>
            {
                try
                {
                    var text = $"Segment {issue.SegmentNumber}\n{issue.IssueDescription}";
                    if (!string.IsNullOrEmpty(issue.Evidence))
                        text += $"\nEvidence: {issue.Evidence}";
                    if (!string.IsNullOrEmpty(issue.Suggestion))
                        text += $"\nSuggestion: {issue.Suggestion}";
                    Clipboard.SetText(text);
                }
                catch { }
            }) { Font = menuFont };
            menu.Items.Add(miCopyAll);

            return menu;
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

        private void LayoutCard(Panel card, Label lblSegNum, Control txtDesc,
            Control txtEvidence, Control txtSugg)
        {
            var availableWidth = _resultsPanel.ClientSize.Width
                - SystemInformation.VerticalScrollBarWidth - 24;
            if (availableWidth < 100) availableWidth = 300;

            card.Width = availableWidth;
            var textWidth = availableWidth - 20;

            // Size the description TextBox to fit its content
            txtDesc.Location = new Point(8, lblSegNum.Bottom + 2);
            txtDesc.Width = textWidth;
            var descSize = TextRenderer.MeasureText(txtDesc.Text, txtDesc.Font,
                new Size(textWidth, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
            txtDesc.Height = Math.Max(16, descSize.Height + 4);

            int cardHeight = txtDesc.Bottom + 6;
            int nextTop = txtDesc.Bottom + 2;

            if (txtEvidence != null)
            {
                txtEvidence.Location = new Point(8, nextTop);
                txtEvidence.Width = textWidth;
                var evSize = TextRenderer.MeasureText(txtEvidence.Text, txtEvidence.Font,
                    new Size(textWidth, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                txtEvidence.Height = Math.Max(16, evSize.Height + 4);
                cardHeight = txtEvidence.Bottom + 6;
                nextTop = txtEvidence.Bottom + 2;
            }

            if (txtSugg != null)
            {
                txtSugg.Location = new Point(8, nextTop);
                txtSugg.Width = textWidth;
                var suggSize = TextRenderer.MeasureText(txtSugg.Text, txtSugg.Font,
                    new Size(textWidth, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                txtSugg.Height = Math.Max(16, suggSize.Height + 4);
                cardHeight = txtSugg.Bottom + 6;
            }

            card.Height = Math.Max(40, cardHeight);
        }

        private void RelayoutCards()
        {
            OnResultsPanelResize(this, EventArgs.Empty);
        }

        private void OnResultsPanelResize(object sender, EventArgs e)
        {
            if (_resultsPanel == null) return;
            _resultsPanel.SuspendLayout();

            // Collect all card panels so we can sort prompt log entries by timestamp
            // before assigning y-positions (newest entry at top).
            var allCards = new List<Panel>();
            foreach (Control ctrl in _resultsPanel.Controls)
            {
                if (ctrl == _lblEmpty || !(ctrl is Panel p)) continue;
                allCards.Add(p);
            }

            // Prompt log cards newest-first, then issue cards by segment number.
            // List<T>.Sort is not stable, so issue cards need an explicit tie-break
            // on SegmentNumber – otherwise removing a card (e.g. checkbox toggle)
            // can scramble the remaining cards' order.
            allCards.Sort((a, b) =>
            {
                var ea = a.Tag as PromptLogEntry;
                var eb = b.Tag as PromptLogEntry;
                if (ea != null && eb != null)
                    return eb.Timestamp.CompareTo(ea.Timestamp); // newest first
                if (ea != null) return -1; // prompt logs before issue cards
                if (eb != null) return 1;
                var ia = a.Tag as ProofreadingIssue;
                var ib = b.Tag as ProofreadingIssue;
                if (ia != null && ib != null)
                    return ia.SegmentNumber.CompareTo(ib.SegmentNumber);
                return 0;
            });

            int yPos = 4;
            foreach (var card in allCards)
            {

                card.Location = new Point(4, yPos);

                // Prompt log cards manage their own height – skip LayoutCard for them
                if (card.Tag is PromptLogEntry)
                {
                    // Just update width to match panel
                    var pw = _resultsPanel.ClientSize.Width
                        - SystemInformation.VerticalScrollBarWidth - 24;
                    if (pw < 100) pw = 300;
                    card.Width = pw;
                    yPos += card.Height + 4;
                    continue;
                }

                // Find controls inside card by Tag
                Label lblSegNum = null;
                Control txtDesc = null, txtEvidence = null, txtSugg = null;
                foreach (Control child in card.Controls)
                {
                    if (child is Label lbl && lbl.Font.Bold)
                        lblSegNum = lbl;
                    else if (child is TextBox tb && (string)tb.Tag == "desc")
                        txtDesc = tb;
                    else if (child is TextBox te && (string)te.Tag == "evidence")
                        txtEvidence = te;
                    else if (child is TextBox ts && (string)ts.Tag == "sugg")
                        txtSugg = ts;
                }

                if (lblSegNum != null && txtDesc != null)
                    LayoutCard(card, lblSegNum, txtDesc, txtEvidence, txtSugg);

                yPos += card.Height + 4;
            }

            _resultsPanel.ResumeLayout(true);
        }
    }
}
