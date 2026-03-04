using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using TermLens.Models;

namespace TermLens.Controls
{
    /// <summary>
    /// Displays a single source word/phrase with its translation(s) underneath.
    /// Port of Supervertaler's TermBlock widget.
    ///
    /// Layout:
    ///   ┌──────────────────────┐
    ///   │  source_text         │
    ///   │  target_translation  │
    ///   │  [+N] shortcut badge │
    ///   └──────────────────────┘
    /// </summary>
    public class TermBlock : Control
    {
        // Colors matching Supervertaler's scheme
        private static readonly Color ProjectBg = ColorTranslator.FromHtml("#FFE5F0");
        private static readonly Color ProjectHover = ColorTranslator.FromHtml("#FFD0E8");
        private static readonly Color RegularBg = ColorTranslator.FromHtml("#D6EBFF");
        private static readonly Color RegularHover = ColorTranslator.FromHtml("#BBDEFB");
        private static readonly Color SeparatorColor = Color.FromArgb(180, 180, 180);

        private bool _isHovered;
        private readonly List<TermEntry> _entries;
        private readonly string _sourceText;
        private readonly int _shortcutIndex; // -1 = no shortcut
        private readonly bool _isProjectGlossary;

        public event EventHandler<TermInsertEventArgs> TermInsertRequested;

        public TermBlock(string sourceText, List<TermEntry> entries, int shortcutIndex = -1, bool isProjectGlossary = false)
        {
            _sourceText = sourceText;
            _entries = entries ?? new List<TermEntry>();
            _shortcutIndex = shortcutIndex;
            _isProjectGlossary = isProjectGlossary;

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            Cursor = Cursors.Hand;
            CalculateSize();
        }

        /// <summary>
        /// True when the primary entry's termbase is marked as a project glossary by the user.
        /// Controls background color: pink for project glossaries, blue for others.
        /// </summary>
        public bool IsProjectGlossary => _isProjectGlossary;
        public TermEntry PrimaryEntry => _entries.Count > 0 ? _entries[0] : null;
        public IReadOnlyList<TermEntry> Entries => _entries;
        public int ShortcutIndex => _shortcutIndex;

        private const int BadgeDiameter = 16;

        private void CalculateSize()
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero))
            {
                var sourceSize = g.MeasureString(_sourceText, SourceFont);
                var targetText = PrimaryEntry?.TargetTerm ?? "";
                var targetSize = g.MeasureString(targetText, TargetFont);

                int extraCount = _entries.Count - 1;
                int extraWidth = 0;
                if (extraCount > 0)
                    extraWidth = (int)Math.Ceiling(g.MeasureString($"+{extraCount}", BadgeFont).Width) + 6;

                int badgeWidth = 0;
                if (_shortcutIndex >= 0)
                    badgeWidth = BadgeDiameter + 4;

                int targetRowWidth = (int)Math.Ceiling(targetSize.Width) + extraWidth + badgeWidth + 10;
                int width = (int)Math.Ceiling(Math.Max(sourceSize.Width + 10, targetRowWidth));
                int height = (int)Math.Ceiling(sourceSize.Height + targetSize.Height) + 8;

                Size = new Size(width, Math.Max(height, 28));
            }
        }

        private Font SourceFont => new Font(Font.FontFamily, Font.Size, FontStyle.Regular);
        private Font TargetFont => new Font(Font.FontFamily, Font.Size, FontStyle.Regular);
        private Font BadgeFont => new Font(Font.FontFamily, Font.Size - 1, FontStyle.Bold);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // Source text — plain, no background
            float y = 3;
            var sourceHeight = g.MeasureString(_sourceText, SourceFont).Height;
            using (var brush = new SolidBrush(Color.FromArgb(40, 40, 40)))
            {
                g.DrawString(_sourceText, SourceFont, brush, 4, y);
            }
            y += sourceHeight;

            // Target row — highlighted background only around translation
            var targetText = PrimaryEntry?.TargetTerm ?? "";
            var targetSize = g.MeasureString(targetText, TargetFont);

            int extraCount = _entries.Count - 1;
            float extraWidth = 0;
            if (extraCount > 0)
                extraWidth = g.MeasureString($"+{extraCount}", BadgeFont).Width + 4;

            float badgeWidth = _shortcutIndex >= 0 ? BadgeDiameter + 4 : 0;
            float targetRowWidth = targetSize.Width + extraWidth + badgeWidth + 4;

            var bgColor = IsProjectGlossary
                ? (_isHovered ? ProjectHover : ProjectBg)
                : (_isHovered ? RegularHover : RegularBg);

            var targetRect = new RectangleF(2, y, targetRowWidth, targetSize.Height + 2);
            using (var brush = new SolidBrush(bgColor))
            using (var path = RoundedRect(Rectangle.Round(targetRect), 3))
            {
                g.FillPath(brush, path);
            }

            // Target text
            float targetX = 4;
            using (var brush = new SolidBrush(Color.FromArgb(20, 20, 20)))
            {
                g.DrawString(targetText, TargetFont, brush, targetX, y);
                targetX += targetSize.Width;
            }

            // "+N" indicator for multiple translations
            if (extraCount > 0)
            {
                var extraText = $"+{extraCount}";
                using (var brush = new SolidBrush(Color.FromArgb(120, 120, 120)))
                {
                    g.DrawString(extraText, BadgeFont, brush, targetX, y);
                    targetX += g.MeasureString(extraText, BadgeFont).Width + 2;
                }
            }

            // Shortcut badge — filled circle with number, after translation
            if (_shortcutIndex >= 0)
            {
                var badgeText = (_shortcutIndex + 1).ToString();
                float circleX = targetX + 2;
                float circleY = y + (targetSize.Height - BadgeDiameter) / 2 + 1;

                var badgeColor = IsProjectGlossary
                    ? Color.FromArgb(200, 100, 150)
                    : Color.FromArgb(90, 140, 210);

                using (var circleBrush = new SolidBrush(badgeColor))
                {
                    g.FillEllipse(circleBrush, circleX, circleY, BadgeDiameter, BadgeDiameter);
                }

                using (var textBrush = new SolidBrush(Color.White))
                {
                    var badgeSize = g.MeasureString(badgeText, BadgeFont);
                    float tx = circleX + (BadgeDiameter - badgeSize.Width) / 2 + 1;
                    float ty = circleY + (BadgeDiameter - badgeSize.Height) / 2 + 1;
                    g.DrawString(badgeText, BadgeFont, textBrush, tx, ty);
                }
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _isHovered = true;
            Invalidate();

            // Show tooltip with all translations and metadata
            if (_entries.Count > 0)
            {
                var lines = new List<string>();
                foreach (var entry in _entries)
                {
                    var line = $"{entry.SourceTerm} \u2192 {entry.TargetTerm}";
                    if (!string.IsNullOrEmpty(entry.TermbaseName))
                        line += $" [{entry.TermbaseName}]";
                    lines.Add(line);

                    foreach (var syn in entry.TargetSynonyms)
                        lines.Add($"  \u2022 {syn}");

                    if (!string.IsNullOrEmpty(entry.Definition))
                        lines.Add($"  Def: {entry.Definition}");
                }

                var tip = new ToolTip { AutoPopDelay = 10000 };
                tip.SetToolTip(this, string.Join("\n", lines));
            }

            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _isHovered = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnClick(EventArgs e)
        {
            if (PrimaryEntry != null)
            {
                TermInsertRequested?.Invoke(this, new TermInsertEventArgs
                {
                    TargetTerm = PrimaryEntry.TargetTerm,
                    Entry = PrimaryEntry
                });
            }
            base.OnClick(e);
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    /// <summary>
    /// Displays a plain (unmatched) word in the segment flow.
    /// </summary>
    public class WordLabel : Label
    {
        public WordLabel(string text)
        {
            Text = text;
            AutoSize = true;
            ForeColor = Color.FromArgb(100, 100, 100);
            Padding = new Padding(2, 4, 2, 4);
            Margin = new Padding(1, 0, 1, 0);
        }
    }

    public class TermInsertEventArgs : EventArgs
    {
        public string TargetTerm { get; set; }
        public TermEntry Entry { get; set; }
    }
}
