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
                Text = "No proofreading results yet",
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
            var cardColor = Color.FromArgb(255, 253, 231);  // #FFFDE7
            var hoverColor = Color.FromArgb(255, 249, 196);
            var textColor = Color.FromArgb(60, 60, 60);
            var suggColor = Color.FromArgb(120, 120, 120);

            int yPos = 4;

            foreach (var issue in report.Issues)
            {
                if (issue.IsOk) continue;

                var card = new Panel
                {
                    Location = new Point(4, yPos),
                    BackColor = cardColor,
                    Cursor = Cursors.Hand,
                    Padding = new Padding(8, 6, 8, 6),
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                    Tag = issue
                };

                // Segment number + warning icon
                var lblSegNum = new Label
                {
                    Text = $"\u26A0 Segment {issue.SegmentNumber}",
                    Font = segNumFont,
                    ForeColor = textColor,
                    Location = new Point(8, 6),
                    AutoSize = true
                };

                // Issue description
                var lblDesc = new Label
                {
                    Text = issue.IssueDescription ?? "",
                    Font = bodyFont,
                    ForeColor = textColor,
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
                        ForeColor = suggColor,
                        Location = new Point(8, 44),
                        AutoSize = false,
                        MaximumSize = new Size(0, 0),
                        AutoEllipsis = false
                    };
                }

                card.Controls.Add(lblSegNum);
                card.Controls.Add(lblDesc);
                if (lblSugg != null)
                    card.Controls.Add(lblSugg);

                // Hover effect — apply to card and all children
                var capturedCard = card;
                Action<Control> applyHover = null;
                applyHover = (ctrl) =>
                {
                    ctrl.MouseEnter += (s, e) => capturedCard.BackColor = hoverColor;
                    ctrl.MouseLeave += (s, e) => capturedCard.BackColor = cardColor;
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
            _lblEmpty.Text = "No proofreading results yet";
            _lblEmpty.Visible = true;
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

                // Find labels inside card
                Label lblSegNum = null, lblDesc = null, lblSugg = null;
                foreach (Control child in card.Controls)
                {
                    var lbl = child as Label;
                    if (lbl == null) continue;
                    if (lbl.Font.Bold)
                        lblSegNum = lbl;
                    else if (lbl.ForeColor == Color.FromArgb(120, 120, 120))
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
