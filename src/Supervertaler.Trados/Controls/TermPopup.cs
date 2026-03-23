using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// Lightweight hover popup for term chip details.
    /// Replaces the standard ToolTip with an interactive panel that supports
    /// clickable URLs, word wrap, and stays open when the mouse moves into it.
    /// </summary>
    internal class TermPopup : Form
    {
        private static readonly Color BorderColor = Color.FromArgb(190, 190, 190);
        private static readonly Color BgColor = Color.White;
        private static readonly Color HeadingColor = Color.FromArgb(50, 50, 50);
        private static readonly Color MetaLabelColor = Color.FromArgb(120, 120, 120);
        private static readonly Color MetaValueColor = Color.FromArgb(60, 60, 60);
        private static readonly Color SynonymColor = Color.FromArgb(80, 80, 80);
        private static readonly Color LinkColor = Color.FromArgb(30, 100, 180);

        private static readonly Regex UrlRegex = new Regex(
            @"(https?://[^\s\)\""\\>]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static int MaxPopupWidth => UiScale.Pixels(500);
        private static int MinPopupWidth => UiScale.Pixels(180);
        private static int ContentPad => UiScale.Pixels(10);

        private readonly Timer _closeTimer;
        private bool _mouseInPopup;
        private Control _ownerChip;

        // Reuse a single instance across all term chips
        private static TermPopup _instance;

        public TermPopup()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = BgColor;
            AutoScaleMode = AutoScaleMode.Dpi;
            TopMost = true;

            // Prevent stealing focus from Trados
            SetStyle(ControlStyles.Selectable, false);

            _closeTimer = new Timer { Interval = 150 };
            _closeTimer.Tick += OnCloseTimerTick;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // WS_EX_TOOLWINDOW — no taskbar entry
                // WS_EX_NOACTIVATE — don't steal focus
                cp.ExStyle |= 0x80 | 0x08000000;
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            // Draw border
            using (var pen = new Pen(BorderColor))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _mouseInPopup = true;
            _closeTimer.Stop();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _mouseInPopup = false;
            ScheduleClose();
            base.OnMouseLeave(e);
        }

        private void OnCloseTimerTick(object sender, EventArgs e)
        {
            _closeTimer.Stop();
            if (!_mouseInPopup)
                HidePopup();
        }

        /// <summary>
        /// Starts the close timer. The popup closes after 200ms unless the
        /// mouse re-enters either the popup or the owner chip.
        /// </summary>
        public void ScheduleClose()
        {
            _closeTimer.Stop();
            _closeTimer.Start();
        }

        /// <summary>
        /// Cancels any pending close (called when mouse re-enters the owner chip).
        /// </summary>
        public void CancelClose()
        {
            _closeTimer.Stop();
            _mouseInPopup = true; // treat as if mouse is in popup
        }

        public void HidePopup()
        {
            _closeTimer.Stop();
            _mouseInPopup = false;
            _ownerChip = null;
            Hide();
        }

        /// <summary>
        /// Shows the popup with the given content lines below the specified chip control.
        /// </summary>
        public void ShowBelow(Control chip, List<PopupLine> lines)
        {
            if (chip == _ownerChip && Visible)
            {
                CancelClose();
                return; // already showing for this chip
            }

            _ownerChip = chip;
            _closeTimer.Stop();
            _mouseInPopup = false;

            // Clear previous content
            SuspendLayout();
            Controls.Clear();

            var font = new Font("Segoe UI", UiScale.FontSize(8.5f), FontStyle.Regular);
            var boldFont = new Font("Segoe UI", UiScale.FontSize(8.5f), FontStyle.Bold);
            var smallFont = new Font("Segoe UI", UiScale.FontSize(7.75f), FontStyle.Regular);

            int contentWidth = MaxPopupWidth - ContentPad * 2;

            // First pass: measure natural widths to determine optimal popup width
            int maxNaturalWidth = MinPopupWidth - ContentPad * 2;
            foreach (var line in lines)
            {
                if (line.Type == PopupLineType.Separator) continue;
                if (line.Type == PopupLineType.MetaLabelled)
                {
                    var fullText = (line.Label ?? "") + " " + (line.Text ?? "");
                    var textWidth = TextRenderer.MeasureText(fullText, font).Width + 4;
                    maxNaturalWidth = Math.Max(maxNaturalWidth, textWidth);
                }
                else
                {
                    var lineFont = GetFontForLine(line, font, boldFont, smallFont);
                    var textWidth = TextRenderer.MeasureText(line.Text, lineFont).Width + 4;
                    maxNaturalWidth = Math.Max(maxNaturalWidth, textWidth);
                }
            }

            // Use the smaller of natural width and max width
            int popupWidth = Math.Min(maxNaturalWidth + ContentPad * 2, MaxPopupWidth);
            if (popupWidth < MinPopupWidth) popupWidth = MinPopupWidth;
            contentWidth = popupWidth - ContentPad * 2;

            // Second pass: create controls with word wrap at the determined width
            int y = ContentPad;

            foreach (var line in lines)
            {
                if (line.Type == PopupLineType.Separator)
                {
                    var sep = CreateSeparator(contentWidth);
                    sep.Location = new Point(ContentPad, y);
                    Controls.Add(sep);
                    y += sep.Height;
                }
                else if (line.Type == PopupLineType.MetaLabelled)
                {
                    // Check if the value contains URLs
                    if (UrlRegex.IsMatch(line.Text ?? ""))
                    {
                        var ctrl = CreateMetaLineWithUrls("  " + line.Label + " " + line.Text, font, contentWidth);
                        ctrl.Location = new Point(ContentPad, y);
                        Controls.Add(ctrl);
                        y += ctrl.Height;
                    }
                    else
                    {
                        var ctrl = CreateLabelledMeta(line, font, boldFont, contentWidth);
                        ctrl.Location = new Point(ContentPad, y);
                        Controls.Add(ctrl);
                        y += ctrl.Height;
                    }
                }
                else if (line.Type == PopupLineType.Meta && UrlRegex.IsMatch(line.Text))
                {
                    // Meta line with URLs — use special rendering
                    var ctrl = CreateMetaLineWithUrls(line.Text, font, contentWidth);
                    ctrl.Location = new Point(ContentPad, y);
                    Controls.Add(ctrl);
                    y += ctrl.Height;
                }
                else
                {
                    var label = CreateLabel(line, font, boldFont, smallFont, contentWidth);
                    label.Location = new Point(ContentPad, y);
                    Controls.Add(label);
                    y += label.Height;
                }
            }

            var totalHeight = y + ContentPad;

            Size = new Size(popupWidth, totalHeight);

            // Position below the chip
            var screenPos = chip.PointToScreen(new Point(0, chip.Height + 2));

            // Ensure popup stays on screen
            var screen = Screen.FromControl(chip).WorkingArea;
            if (screenPos.X + popupWidth > screen.Right)
                screenPos.X = screen.Right - popupWidth;
            if (screenPos.Y + totalHeight > screen.Bottom)
                screenPos.Y = chip.PointToScreen(Point.Empty).Y - totalHeight - 2;

            Location = screenPos;
            ResumeLayout(true);

            if (!Visible)
                Show();
        }

        private static readonly Color SeparatorColor = Color.FromArgb(225, 225, 225);

        private Font GetFontForLine(PopupLine line, Font font, Font boldFont, Font smallFont)
        {
            switch (line.Type)
            {
                case PopupLineType.Heading: return boldFont;
                case PopupLineType.Tag: return smallFont;
                default: return font;
            }
        }

        private Color GetColorForLine(PopupLine line)
        {
            switch (line.Type)
            {
                case PopupLineType.Heading: return HeadingColor;
                case PopupLineType.Tag: return MetaLabelColor;
                case PopupLineType.Synonym: return SynonymColor;
                case PopupLineType.Meta: return MetaValueColor;
                case PopupLineType.MetaLabelled: return MetaValueColor;
                default: return MetaValueColor;
            }
        }

        /// <summary>
        /// Creates a simple label with word wrap support for non-URL lines.
        /// </summary>
        private Label CreateLabel(PopupLine line, Font font, Font boldFont, Font smallFont, int maxWidth)
        {
            var lineFont = GetFontForLine(line, font, boldFont, smallFont);

            var label = new Label
            {
                Text = line.Text,
                Font = lineFont,
                ForeColor = GetColorForLine(line),
                BackColor = BgColor,
                AutoSize = false,
                MaximumSize = new Size(maxWidth, 0),
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };

            // Measure with word wrap
            var proposed = new Size(maxWidth, int.MaxValue);
            var measured = TextRenderer.MeasureText(line.Text, lineFont, proposed,
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);

            label.Size = new Size(maxWidth, measured.Height);

            // Wire up mouse events to keep popup open
            label.MouseEnter += (s, e) => { _mouseInPopup = true; _closeTimer.Stop(); };
            label.MouseLeave += (s, e) => { _mouseInPopup = false; ScheduleClose(); };

            return label;
        }

        /// <summary>
        /// Creates a horizontal separator line.
        /// </summary>
        private Control CreateSeparator(int maxWidth)
        {
            var sepHeight = UiScale.Pixels(9);
            var panel = new Panel
            {
                Size = new Size(maxWidth, sepHeight),
                BackColor = BgColor,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            panel.Paint += (s, e) =>
            {
                using (var pen = new Pen(SeparatorColor))
                    e.Graphics.DrawLine(pen, 0, sepHeight / 2, maxWidth, sepHeight / 2);
            };
            panel.MouseEnter += (s, e) => { _mouseInPopup = true; _closeTimer.Stop(); };
            panel.MouseLeave += (s, e) => { _mouseInPopup = false; ScheduleClose(); };
            return panel;
        }

        /// <summary>
        /// Creates a meta line with a bold label and hanging-indent value text.
        /// The value text wraps with its left edge aligned to where the value starts.
        /// </summary>
        private Control CreateLabelledMeta(PopupLine line, Font font, Font boldFont, int maxWidth)
        {
            string labelText = line.Label ?? "";
            string valueText = line.Text ?? "";

            // Measure the bold label width to establish the hanging indent
            int labelWidth = TextRenderer.MeasureText(labelText + " ", boldFont).Width - 4;

            var panel = new Panel
            {
                AutoSize = false,
                BackColor = BgColor,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };

            // Bold label
            var lblLabel = new Label
            {
                Text = labelText,
                Font = boldFont,
                ForeColor = MetaLabelColor,
                BackColor = BgColor,
                AutoSize = true,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                Location = new Point(0, 0)
            };
            panel.Controls.Add(lblLabel);

            // Value text with hanging indent — wraps within (maxWidth - labelWidth)
            int valueWidth = maxWidth - labelWidth;
            if (valueWidth < 100) valueWidth = maxWidth; // fallback if label is very wide

            var lblValue = new Label
            {
                Text = valueText,
                Font = font,
                ForeColor = MetaValueColor,
                BackColor = BgColor,
                AutoSize = false,
                MaximumSize = new Size(valueWidth, 0),
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                Location = new Point(labelWidth, 0)
            };

            var proposed = new Size(valueWidth, int.MaxValue);
            var measured = TextRenderer.MeasureText(valueText, font, proposed,
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
            lblValue.Size = new Size(valueWidth, measured.Height);
            panel.Controls.Add(lblValue);

            int totalHeight = Math.Max(lblLabel.PreferredHeight, lblValue.Height);
            panel.Size = new Size(maxWidth, totalHeight);

            panel.MouseEnter += (s, e) => { _mouseInPopup = true; _closeTimer.Stop(); };
            panel.MouseLeave += (s, e) => { _mouseInPopup = false; ScheduleClose(); };
            lblLabel.MouseEnter += (s, e) => { _mouseInPopup = true; _closeTimer.Stop(); };
            lblLabel.MouseLeave += (s, e) => { _mouseInPopup = false; ScheduleClose(); };
            lblValue.MouseEnter += (s, e) => { _mouseInPopup = true; _closeTimer.Stop(); };
            lblValue.MouseLeave += (s, e) => { _mouseInPopup = false; ScheduleClose(); };

            return panel;
        }

        /// <summary>
        /// Creates a meta line that may contain clickable URLs mixed with plain text.
        /// Uses a two-part approach: label prefix + link for URLs.
        /// </summary>
        private Control CreateMetaLineWithUrls(string text, Font font, int maxWidth)
        {
            var matches = UrlRegex.Matches(text);

            // If the text is just a label + URL (most common case: "  URL: https://..."),
            // use a simple panel with a label and a link
            if (matches.Count == 1)
            {
                var match = matches[0];
                var beforeUrl = match.Index > 0 ? text.Substring(0, match.Index) : "";
                var url = match.Value;
                var afterUrl = match.Index + match.Length < text.Length
                    ? text.Substring(match.Index + match.Length) : "";

                var panel = new Panel
                {
                    AutoSize = false,
                    BackColor = BgColor,
                    Margin = Padding.Empty,
                    Padding = Padding.Empty
                };

                int x = 0;
                int totalHeight = 0;

                // Prefix text (e.g., "  URL: ")
                if (!string.IsNullOrEmpty(beforeUrl))
                {
                    var prefixLabel = new Label
                    {
                        Text = beforeUrl,
                        Font = font,
                        ForeColor = MetaValueColor,
                        AutoSize = true,
                        BackColor = BgColor,
                        Margin = Padding.Empty,
                        Location = new Point(0, 0)
                    };
                    panel.Controls.Add(prefixLabel);
                    x = TextRenderer.MeasureText(beforeUrl, font).Width - 4; // adjust for label padding
                    totalHeight = prefixLabel.PreferredHeight;
                }

                // URL as clickable link
                var link = new LinkLabel
                {
                    Text = url,
                    Font = font,
                    AutoSize = false,
                    LinkColor = LinkColor,
                    ActiveLinkColor = LinkColor,
                    VisitedLinkColor = LinkColor,
                    BackColor = BgColor,
                    Margin = Padding.Empty,
                    Location = new Point(x, 0)
                };

                // Measure and allow wrap within remaining width
                var linkWidth = maxWidth - x;
                if (linkWidth < 100) linkWidth = maxWidth; // wrap to new line if too narrow
                var linkSize = TextRenderer.MeasureText(url, font,
                    new Size(linkWidth, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                link.Size = new Size(linkWidth, linkSize.Height);

                var capturedUrl = url;
                link.LinkClicked += (s, e) =>
                {
                    try { Process.Start(new ProcessStartInfo(capturedUrl) { UseShellExecute = true }); }
                    catch { }
                };
                link.MouseEnter += (s, e) => { _mouseInPopup = true; _closeTimer.Stop(); };
                link.MouseLeave += (s, e) => { _mouseInPopup = false; ScheduleClose(); };
                panel.Controls.Add(link);

                totalHeight = Math.Max(totalHeight, link.Bottom);

                // Suffix text (if any)
                if (!string.IsNullOrEmpty(afterUrl))
                {
                    var suffixLabel = new Label
                    {
                        Text = afterUrl,
                        Font = font,
                        ForeColor = MetaValueColor,
                        AutoSize = true,
                        BackColor = BgColor,
                        Margin = Padding.Empty,
                        Location = new Point(link.Right, 0)
                    };
                    panel.Controls.Add(suffixLabel);
                    totalHeight = Math.Max(totalHeight, suffixLabel.PreferredHeight);
                }

                panel.Size = new Size(maxWidth, totalHeight);

                panel.MouseEnter += (s, e) => { _mouseInPopup = true; _closeTimer.Stop(); };
                panel.MouseLeave += (s, e) => { _mouseInPopup = false; ScheduleClose(); };

                return panel;
            }

            // Fallback for multiple URLs or complex cases: render as a wrapping label
            // with the full text (URLs are visible but not clickable)
            var fallback = new Label
            {
                Text = text,
                Font = font,
                ForeColor = MetaValueColor,
                BackColor = BgColor,
                AutoSize = false,
                MaximumSize = new Size(maxWidth, 0),
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };

            var proposed = new Size(maxWidth, int.MaxValue);
            var measured = TextRenderer.MeasureText(text, font, proposed,
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
            fallback.Size = new Size(maxWidth, measured.Height);

            fallback.MouseEnter += (s, e) => { _mouseInPopup = true; _closeTimer.Stop(); };
            fallback.MouseLeave += (s, e) => { _mouseInPopup = false; ScheduleClose(); };

            return fallback;
        }

        /// <summary>
        /// Gets or creates the singleton popup instance.
        /// </summary>
        public static TermPopup GetInstance()
        {
            if (_instance == null || _instance.IsDisposed)
                _instance = new TermPopup();
            return _instance;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _closeTimer?.Stop();
                _closeTimer?.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // ─── Data Types ─────────────────────────────────────────────

    internal enum PopupLineType
    {
        Heading,
        Tag,
        Synonym,
        Meta,
        /// <summary>Meta line with a bold label prefix and hanging indent for wrapped text.</summary>
        MetaLabelled,
        Separator,
        Plain
    }

    internal class PopupLine
    {
        public string Text { get; set; }
        public PopupLineType Type { get; set; }
        /// <summary>Bold label prefix for MetaLabelled lines (e.g. "Def:").</summary>
        public string Label { get; set; }

        public PopupLine(string text, PopupLineType type = PopupLineType.Plain)
        {
            Text = text;
            Type = type;
        }

        public PopupLine(string label, string value, PopupLineType type)
        {
            Label = label;
            Text = value;
            Type = type;
        }
    }
}
