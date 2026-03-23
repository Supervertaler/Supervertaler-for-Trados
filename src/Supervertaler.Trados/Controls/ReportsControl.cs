using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
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

        // Results area
        private Panel _resultsPanel;
        private Label _lblEmpty;

        // Footer
        private Label _lblFooter;

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
            _lblFooter = new Label
            {
                Height = 22,
                Font = new Font("Segoe UI", 7.5f),
                ForeColor = Color.FromArgb(140, 140, 140),
                Text = "",
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 12, 0),
                BackColor = Color.FromArgb(250, 250, 250),
                Dock = DockStyle.Bottom
            };
            Controls.Add(_lblFooter);

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
            OnControlResize(this, EventArgs.Empty);
        }

        private void OnControlResize(object sender, EventArgs e)
        {
            if (_btnClear == null || _lblIssueCount == null || _resultsPanel == null || _lblFooter == null)
                return;

            UpdateClearButtonSize();

            var clientWidth = ClientSize.Width;

            // Position Clear button at top-right using the actual rendered button width.
            _btnClear.Location = new Point(
                Math.Max(HeaderLeft, clientWidth - _btnClear.Width - HeaderSpacing),
                HeaderTop - 2);

            // Position issue count label between the title and Clear button.
            _lblIssueCount.Location = new Point(
                Math.Max(_lblHeader.Right + HeaderSpacing, _btnClear.Left - _lblIssueCount.Width - HeaderSpacing),
                _btnClear.Top + Math.Max(0, (_btnClear.Height - _lblIssueCount.Height) / 2));

            var resultsTop = Math.Max(_lblHeader.Bottom, _btnClear.Bottom) + HeaderSpacing;
            _resultsPanel.Location = new Point(0, resultsTop);
            _resultsPanel.Width = clientWidth;
            _resultsPanel.Height = Math.Max(40, _lblFooter.Top - _resultsPanel.Top);
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
            _lblIssueCount.Parent?.PerformLayout();

            // Update footer
            _lblFooter.Text = $"Last run: {report.Timestamp:HH:mm:ss} \u2014 {report.Duration.TotalSeconds:F1}s";

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

                // Issue description
                var lblDesc = new Label
                {
                    Text = issue.IssueDescription ?? "",
                    Font = bodyFont,
                    ForeColor = TextColor,
                    Location = new Point(8, 24),
                    AutoSize = false,
                    MaximumSize = new Size(0, 0),  // will be set on resize
                    AutoEllipsis = false
                };

                // Suggestion (if available)
                Label lblSugg = null;
                if (!string.IsNullOrEmpty(issue.Suggestion))
                {
                    lblSugg = new Label
                    {
                        Text = "Suggestion: " + issue.Suggestion,
                        Font = suggFont,
                        ForeColor = SuggColor,
                        Location = new Point(8, 44),
                        AutoSize = false,
                        MaximumSize = new Size(0, 0),
                        AutoEllipsis = false
                    };
                }

                card.Controls.Add(chk);
                card.Controls.Add(lblSegNum);
                card.Controls.Add(lblDesc);
                if (lblSugg != null)
                    card.Controls.Add(lblSugg);

                // Checkbox toggle — change card colour and text when checked
                var capturedCard = card;
                var capturedSegNum = lblSegNum;
                var capturedDesc = lblDesc;
                var capturedSugg = lblSugg;
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

                // Hover effect — apply to card and all children (except checkbox)
                Action<Control> applyHover = null;
                applyHover = (ctrl) =>
                {
                    ctrl.MouseEnter += (s, e) =>
                    {
                        capturedCard.BackColor = HoverColor;
                    };
                    ctrl.MouseLeave += (s, e) =>
                    {
                        capturedCard.BackColor = CardColor;
                    };
                    // Only navigate on click for non-checkbox controls
                    if (!(ctrl is CheckBox))
                        ctrl.Click += (s, e) => OnIssueCardClick(capturedCard.Tag as ProofreadingIssue);
                    ctrl.Cursor = Cursors.Hand;
                };
                applyHover(card);
                foreach (Control child in card.Controls)
                    applyHover(child);

                _resultsPanel.Controls.Add(card);

                // Layout the card — need to measure text height
                LayoutCard(card, lblSegNum, lblDesc, lblSugg);

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
            _lblEmpty.Text = "No reports yet";
            _lblEmpty.Visible = true;
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

            // Insert at top (after _lblEmpty which is index 0)
            int insertIndex = 0;
            for (int i = 0; i < _resultsPanel.Controls.Count; i++)
            {
                if (_resultsPanel.Controls[i] == _lblEmpty)
                {
                    insertIndex = i + 1;
                    break;
                }
            }
            _resultsPanel.Controls.Add(card);
            _resultsPanel.Controls.SetChildIndex(card, insertIndex);

            _promptLogCount++;

            // Re-flow all cards
            RelayoutCards();
            _resultsPanel.ResumeLayout(true);

            // Scroll to top
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
                _lblIssueCount.Text = $"{_issueCount} issue{(_issueCount != 1 ? "s" : "")} found — {_checkedCount} addressed";
            // Text is set in SetResults initially; only update when checked count changes
            _lblIssueCount.Parent?.PerformLayout();
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

        private void LayoutCard(Panel card, Label lblSegNum, Label lblDesc, Label lblSugg)
        {
            var availableWidth = _resultsPanel.ClientSize.Width
                - SystemInformation.VerticalScrollBarWidth - 24;
            if (availableWidth < 100) availableWidth = 300;

            card.Width = availableWidth;
            var textWidth = availableWidth - 20;

            lblDesc.MaximumSize = new Size(textWidth, 0);
            lblDesc.AutoSize = true;
            lblDesc.Location = new Point(8, lblSegNum.Bottom + 2);

            int cardHeight = lblDesc.Bottom + 6;

            if (lblSugg != null)
            {
                lblSugg.MaximumSize = new Size(textWidth, 0);
                lblSugg.AutoSize = true;
                lblSugg.Location = new Point(8, lblDesc.Bottom + 2);
                cardHeight = lblSugg.Bottom + 6;
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

            int yPos = 4;
            foreach (Control ctrl in _resultsPanel.Controls)
            {
                if (ctrl == _lblEmpty) continue;
                var card = ctrl as Panel;
                if (card == null) continue;

                card.Location = new Point(4, yPos);

                // Prompt log cards manage their own height — skip LayoutCard for them
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

                // Find labels inside card (skip CheckBox controls)
                Label lblSegNum = null, lblDesc = null, lblSugg = null;
                foreach (Control child in card.Controls)
                {
                    var lbl = child as Label;
                    if (lbl == null) continue;
                    if (lbl.Font.Bold)
                        lblSegNum = lbl;
                    else if (lbl.ForeColor == SuggColor)
                        lblSugg = lbl;
                    else
                        lblDesc = lbl;
                }

                if (lblSegNum != null && lblDesc != null)
                    LayoutCard(card, lblSegNum, lblDesc, lblSugg);

                yPos += card.Height + 4;
            }

            _resultsPanel.ResumeLayout(true);
        }
    }
}
