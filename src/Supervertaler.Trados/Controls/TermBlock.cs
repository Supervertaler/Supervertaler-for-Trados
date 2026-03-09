using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados.Controls
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
        private static readonly Color NonTranslatableBg = ColorTranslator.FromHtml("#FFF3D0");
        private static readonly Color NonTranslatableHover = ColorTranslator.FromHtml("#FFE8A0");
        private static readonly Color MultiTermBg = ColorTranslator.FromHtml("#D4EDDA");
        private static readonly Color MultiTermHover = ColorTranslator.FromHtml("#B8D9C0");
        private static readonly Color SeparatorColor = Color.FromArgb(180, 180, 180);

        private bool _isHovered;
        private readonly List<TermEntry> _entries;
        private readonly string _sourceText;
        private readonly int _shortcutIndex; // -1 = no shortcut
        private readonly bool _isProjectTermbase;
        private readonly bool _isNonTranslatable;
        private readonly bool _isMultiTerm;

        public event EventHandler<TermInsertEventArgs> TermInsertRequested;
        public event EventHandler<TermEditEventArgs> TermEditRequested;
        public event EventHandler<TermEditEventArgs> TermDeleteRequested;
        public event EventHandler<TermEditEventArgs> TermNonTranslatableToggled;

        public TermBlock(string sourceText, List<TermEntry> entries, int shortcutIndex = -1,
            bool isProjectTermbase = false, bool isNonTranslatable = false, bool isMultiTerm = false)
        {
            _sourceText = sourceText;
            _entries = entries ?? new List<TermEntry>();
            _shortcutIndex = shortcutIndex;
            _isProjectTermbase = isProjectTermbase;
            _isNonTranslatable = isNonTranslatable;
            _isMultiTerm = isMultiTerm;

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            Cursor = Cursors.Hand;
            CalculateSize();

            // Right-click context menu for edit/delete/non-translatable
            // (not shown for read-only MultiTerm entries)
            if (!isMultiTerm)
            {
                var contextMenu = new ContextMenuStrip();

                var editItem = new ToolStripMenuItem("Edit Term\u2026");
                editItem.Click += (s, ev) =>
                {
                    if (PrimaryEntry != null)
                        TermEditRequested?.Invoke(this, new TermEditEventArgs
                        {
                            Entry = PrimaryEntry,
                            AllEntries = _entries.AsReadOnly()
                        });
                };
                contextMenu.Items.Add(editItem);

                var toggleNtItem = new ToolStripMenuItem(
                    _isNonTranslatable ? "Mark as Translatable" : "Mark as Non-Translatable");
                toggleNtItem.Click += (s, ev) =>
                {
                    if (PrimaryEntry != null)
                        TermNonTranslatableToggled?.Invoke(this, new TermEditEventArgs { Entry = PrimaryEntry });
                };
                contextMenu.Items.Add(toggleNtItem);

                var deleteItem = new ToolStripMenuItem("Delete Term");
                deleteItem.Click += (s, ev) =>
                {
                    if (PrimaryEntry != null)
                        TermDeleteRequested?.Invoke(this, new TermEditEventArgs { Entry = PrimaryEntry });
                };
                contextMenu.Items.Add(deleteItem);

                ContextMenuStrip = contextMenu;
            }
        }

        /// <summary>
        /// True when the primary entry's termbase is marked as a project termbase by the user.
        /// Controls background color: pink for project termbases, blue for others.
        /// </summary>
        public bool IsProjectTermbase => _isProjectTermbase;
        public bool IsNonTranslatable => _isNonTranslatable;
        public bool IsMultiTerm => _isMultiTerm;
        public TermEntry PrimaryEntry => _entries.Count > 0 ? _entries[0] : null;
        public IReadOnlyList<TermEntry> Entries => _entries;
        public int ShortcutIndex => _shortcutIndex;

        private const int BadgeHeight = 16;

        /// <summary>
        /// Calculates the badge width for the shortcut number.
        /// Single digits use a circle (diameter = BadgeHeight).
        /// Double digits use a wider pill shape.
        /// </summary>
        private int GetBadgeWidth(Graphics g)
        {
            if (_shortcutIndex < 0) return 0;

            var badgeText = (_shortcutIndex + 1).ToString();
            if (badgeText.Length <= 1)
                return BadgeHeight; // circle

            // Pill: text width + horizontal padding
            return (int)Math.Ceiling(g.MeasureString(badgeText, BadgeFont).Width) + 6;
        }

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
                    badgeWidth = GetBadgeWidth(g) + 4;

                int targetRowWidth = (int)Math.Ceiling(targetSize.Width) + extraWidth + badgeWidth + 10;
                int width = (int)Math.Ceiling(Math.Max(sourceSize.Width + 10, targetRowWidth));
                int height = (int)Math.Ceiling(sourceSize.Height + targetSize.Height) + 8;

                Size = new Size(width, Math.Max(height, 28));
            }
        }

        private Font SourceFont => new Font(Font.FontFamily, Font.Size, FontStyle.Regular);
        private Font TargetFont => new Font(Font.FontFamily, Font.Size, FontStyle.Regular);
        private Font BadgeFont => new Font(Font.FontFamily, Font.Size - 1, FontStyle.Bold);

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            CalculateSize();
            Invalidate();
        }

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

            float badgeWidth = _shortcutIndex >= 0 ? GetBadgeWidth(g) + 4 : 0;
            float targetRowWidth = targetSize.Width + extraWidth + badgeWidth + 4;

            Color bgColor;
            if (_isNonTranslatable)
                bgColor = _isHovered ? NonTranslatableHover : NonTranslatableBg;
            else if (IsProjectTermbase)
                bgColor = _isHovered ? ProjectHover : ProjectBg;
            else if (_isMultiTerm)
                bgColor = _isHovered ? MultiTermHover : MultiTermBg;
            else
                bgColor = _isHovered ? RegularHover : RegularBg;

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

            // Shortcut badge — filled circle/pill with number, after translation
            if (_shortcutIndex >= 0)
            {
                var badgeText = (_shortcutIndex + 1).ToString();
                int badgeW = GetBadgeWidth(g);
                float badgeX = targetX + 2;
                float badgeY = y + (targetSize.Height - BadgeHeight) / 2 + 1;

                Color badgeColor;
                if (_isNonTranslatable)
                    badgeColor = Color.FromArgb(180, 150, 50);
                else if (IsProjectTermbase)
                    badgeColor = Color.FromArgb(200, 100, 150);
                else if (_isMultiTerm)
                    badgeColor = Color.FromArgb(80, 150, 90);
                else
                    badgeColor = Color.FromArgb(90, 140, 210);

                using (var circleBrush = new SolidBrush(badgeColor))
                {
                    if (badgeText.Length > 1)
                    {
                        // Pill shape for double-digit numbers
                        using (var path = RoundedRect(
                            new Rectangle((int)badgeX, (int)badgeY, badgeW, BadgeHeight),
                            BadgeHeight / 2))
                        {
                            g.FillPath(circleBrush, path);
                        }
                    }
                    else
                    {
                        // Circle for single-digit numbers
                        g.FillEllipse(circleBrush, badgeX, badgeY, BadgeHeight, BadgeHeight);
                    }
                }

                using (var textBrush = new SolidBrush(Color.White))
                {
                    var textSize = g.MeasureString(badgeText, BadgeFont);
                    float tx = badgeX + (badgeW - textSize.Width) / 2 + 1;
                    float ty = badgeY + (BadgeHeight - textSize.Height) / 2 + 1;
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
                if (_isMultiTerm)
                    lines.Add("[MultiTerm \u2014 read-only]");
                if (_isNonTranslatable)
                    lines.Add("[Non-translatable]");
                foreach (var entry in _entries)
                {
                    var line = $"{entry.SourceTerm} \u2192 {entry.TargetTerm}";
                    if (!string.IsNullOrEmpty(entry.TermbaseName))
                        line += $" [{entry.TermbaseName}]";
                    line += $" (ID {entry.Id})";
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
            UseCompatibleTextRendering = true; // GDI+ rendering, same as TermBlock
            Padding = new Padding(2, 3, 2, 0);
            Margin = new Padding(2, 1, 2, 1);
        }
    }

    public class TermInsertEventArgs : EventArgs
    {
        public string TargetTerm { get; set; }
        public TermEntry Entry { get; set; }
    }

    public class TermEditEventArgs : EventArgs
    {
        public TermEntry Entry { get; set; }

        /// <summary>
        /// All entries from all termbases for this matched term.
        /// Used to enable multi-termbase editing in the editor dialog.
        /// </summary>
        public IReadOnlyList<TermEntry> AllEntries { get; set; }
    }
}
