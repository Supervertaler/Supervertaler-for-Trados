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
        private static readonly Color AbbreviationBg = ColorTranslator.FromHtml("#E8DAFF");
        private static readonly Color AbbreviationHover = ColorTranslator.FromHtml("#D8C8FF");
        private static readonly Color SeparatorColor = Color.FromArgb(180, 180, 180);

        private bool _isHovered;
        private readonly List<TermEntry> _entries;
        private readonly string _sourceText;
        private readonly int _shortcutIndex; // -1 = no shortcut
        private readonly bool _isProjectTermbase;
        private readonly bool _isNonTranslatable;
        private readonly bool _isMultiTerm;
        private readonly HashSet<long> _abbreviationMatchIds;

        public event EventHandler<TermInsertEventArgs> TermInsertRequested;
        public event EventHandler<TermEditEventArgs> TermEditRequested;
        public event EventHandler<TermEditEventArgs> TermDeleteRequested;
        public event EventHandler<TermEditEventArgs> TermNonTranslatableToggled;

        public TermBlock(string sourceText, List<TermEntry> entries, int shortcutIndex = -1,
            bool isProjectTermbase = false, bool isNonTranslatable = false, bool isMultiTerm = false,
            HashSet<long> abbreviationMatchIds = null)
        {
            _sourceText = sourceText;
            _entries = entries ?? new List<TermEntry>();
            _shortcutIndex = shortcutIndex;
            _isProjectTermbase = isProjectTermbase;
            _isNonTranslatable = isNonTranslatable;
            _isMultiTerm = isMultiTerm;
            _abbreviationMatchIds = abbreviationMatchIds ?? new HashSet<long>();

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

        /// <summary>
        /// True if the primary entry was matched via its SourceAbbreviation.
        /// When true, the chip shows TargetAbbreviation and Alt+digit inserts
        /// TargetAbbreviation instead of TargetTerm.
        /// </summary>
        public bool IsAbbreviationMatch =>
            PrimaryEntry != null && _abbreviationMatchIds.Contains(PrimaryEntry.Id);

        /// <summary>
        /// Number of extra translations to show in the +N badge.
        /// Includes: other entries - 1, plus the abbreviation pair if it exists
        /// and isn't already the primary display.
        /// </summary>
        private int ExtraCount
        {
            get
            {
                int count = _entries.Count - 1;
                // If the primary entry has an abbreviation pair that isn't the
                // primary display, count it as an extra
                if (PrimaryEntry != null &&
                    !string.IsNullOrEmpty(PrimaryEntry.SourceAbbreviation) &&
                    !string.IsNullOrEmpty(PrimaryEntry.PrimaryTargetAbbreviation))
                {
                    // If matched via abbreviation, the "extra" is the full term pair
                    // If matched via full term, the "extra" is the abbreviation pair
                    count++;
                }
                return count;
            }
        }

        /// <summary>
        /// The target text to display: TargetAbbreviation when matched via
        /// abbreviation, TargetTerm otherwise.
        /// </summary>
        private string DisplayTargetText
        {
            get
            {
                if (PrimaryEntry == null) return "";
                if (IsAbbreviationMatch && !string.IsNullOrEmpty(PrimaryEntry.PrimaryTargetAbbreviation))
                    return PrimaryEntry.PrimaryTargetAbbreviation;
                return PrimaryEntry.TargetTerm ?? "";
            }
        }

        private const int BadgeHeight = 16;

        /// <summary>
        /// Maximum number of repeated-digit tiers (1 through MaxTiers repeats).
        /// 5 tiers = 45 shortcuts: 1-9, 11-99, 111-999, 1111-9999, 11111-99999.
        /// </summary>
        internal const int MaxTiers = 5;

        /// <summary>
        /// Set to true to use repeated-digit badges (11, 222, etc.),
        /// false for sequential badges (10, 11, 12, ...).
        /// Updated from TermLensEditorViewPart when settings change.
        /// </summary>
        internal static bool UseRepeatedDigitBadges { get; set; }

        /// <summary>
        /// Returns the badge text for the given 0-based shortcut index.
        /// In sequential mode: "1", "2", ... "45" (plain numbers).
        /// In repeated mode: "1"–"9", "11"–"99", "111"–"999", etc.
        /// Returns null if the index is beyond the shortcut range.
        /// </summary>
        internal static string GetBadgeText(int shortcutIndex)
        {
            if (shortcutIndex < 0) return null;

            if (UseRepeatedDigitBadges)
            {
                int tier = shortcutIndex / 9;  // 0-based tier
                if (tier >= MaxTiers) return null;
                int d = shortcutIndex % 9 + 1; // digit 1-9
                return new string((char)('0' + d), tier + 1);
            }
            else
            {
                // Sequential: just show the 1-based number
                return (shortcutIndex + 1).ToString();
            }
        }

        /// <summary>
        /// Calculates the badge width for the shortcut number.
        /// Single digits use a circle (diameter = BadgeHeight).
        /// Double/triple digits use a wider pill shape.
        /// </summary>
        private int GetBadgeWidth(Graphics g)
        {
            if (_shortcutIndex < 0) return 0;

            var badgeText = GetBadgeText(_shortcutIndex);
            if (badgeText == null) return 0;
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
                var targetText = DisplayTargetText;
                var targetSize = g.MeasureString(targetText, TargetFont);

                int extraCount = ExtraCount;
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
            var targetText = DisplayTargetText;
            var targetSize = g.MeasureString(targetText, TargetFont);

            int extraCount = ExtraCount;
            float extraWidth = 0;
            if (extraCount > 0)
                extraWidth = g.MeasureString($"+{extraCount}", BadgeFont).Width + 4;

            float badgeWidth = _shortcutIndex >= 0 ? GetBadgeWidth(g) + 4 : 0;
            float targetRowWidth = targetSize.Width + extraWidth + badgeWidth + 4;

            Color bgColor;
            if (_isNonTranslatable)
                bgColor = _isHovered ? NonTranslatableHover : NonTranslatableBg;
            else if (IsAbbreviationMatch)
                bgColor = _isHovered ? AbbreviationHover : AbbreviationBg;
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
            if (_shortcutIndex >= 0 && GetBadgeText(_shortcutIndex) != null)
            {
                var badgeText = GetBadgeText(_shortcutIndex);
                int badgeW = GetBadgeWidth(g);
                float badgeX = targetX + 2;
                float badgeY = y + (targetSize.Height - BadgeHeight) / 2 + 1;

                Color badgeColor;
                if (_isNonTranslatable)
                    badgeColor = Color.FromArgb(180, 150, 50);
                else if (IsAbbreviationMatch)
                    badgeColor = Color.FromArgb(130, 90, 200);
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

                // Badge number — always white
                using (var textBrush = new SolidBrush(Color.White))
                {
                    var textSize = g.MeasureString(badgeText, BadgeFont);
                    float tx = badgeX + (badgeW - textSize.Width) / 2 + 1;
                    float ty = badgeY + (BadgeHeight - textSize.Height) / 2 + 1;
                    g.DrawString(badgeText, BadgeFont, textBrush, tx, ty);
                }

            }

            // Corner indicators — drawn outside the badge block so they appear
            // regardless of whether a shortcut badge is visible.

            // Amber corner dot when entry has metadata (definition/domain/notes)
            bool hasMetadata = _entries.Any(t =>
                !string.IsNullOrEmpty(t.Definition) ||
                !string.IsNullOrEmpty(t.Domain) ||
                !string.IsNullOrEmpty(t.Notes) ||
                !string.IsNullOrEmpty(t.Url));
            if (hasMetadata)
            {
                const int dotSize = 8;
                float dotX = targetRect.Right - dotSize / 2f;
                float dotY = targetRect.Top - dotSize / 2f;
                using (var dotBrush = new SolidBrush(Color.FromArgb(245, 158, 11))) // amber #F59E0B
                {
                    g.FillEllipse(dotBrush, dotX, dotY, dotSize, dotSize);
                }
                // White border so the dot pops against any chip color
                using (var borderPen = new Pen(Color.White, 1.5f))
                {
                    g.DrawEllipse(borderPen, dotX, dotY, dotSize, dotSize);
                }
            }

            // "≡" synonym indicator when any entry has synonyms (source or target)
            bool hasSynonyms = _entries.Any(t =>
                (t.TargetSynonyms != null && t.TargetSynonyms.Count > 0) ||
                (t.SourceSynonyms != null && t.SourceSynonyms.Count > 0));
            if (hasSynonyms)
            {
                const int iconSize = 10;
                // Position to the left of the metadata dot (if present), or at top-right
                float iconX = hasMetadata
                    ? targetRect.Right - iconSize / 2f - 11
                    : targetRect.Right - iconSize / 2f;
                float iconY = targetRect.Top - iconSize / 2f;

                // Draw a small filled circle background
                using (var bgBrush = new SolidBrush(Color.FromArgb(99, 102, 241))) // indigo #6366F1
                {
                    g.FillEllipse(bgBrush, iconX, iconY, iconSize, iconSize);
                }
                using (var borderPen = new Pen(Color.White, 1.5f))
                {
                    g.DrawEllipse(borderPen, iconX, iconY, iconSize, iconSize);
                }

                // Draw three horizontal lines (≡) inside the circle
                float cx = iconX + iconSize / 2f;
                float cy = iconY + iconSize / 2f;
                float lineHalf = 2.2f;
                using (var linePen = new Pen(Color.White, 1f))
                {
                    g.DrawLine(linePen, cx - lineHalf, cy - 2f, cx + lineHalf, cy - 2f);
                    g.DrawLine(linePen, cx - lineHalf, cy,      cx + lineHalf, cy);
                    g.DrawLine(linePen, cx - lineHalf, cy + 2f, cx + lineHalf, cy + 2f);
                }
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _isHovered = true;
            Invalidate();

            // Show interactive popup with all translations and metadata
            if (_entries.Count > 0)
            {
                var lines = new List<PopupLine>();
                if (_isMultiTerm)
                    lines.Add(new PopupLine("[MultiTerm \u2014 read-only]", PopupLineType.Tag));
                if (_isNonTranslatable)
                    lines.Add(new PopupLine("[Non-translatable]", PopupLineType.Tag));
                bool isFirstEntry = true;
                foreach (var entry in _entries)
                {
                    if (!isFirstEntry)
                        lines.Add(new PopupLine("", PopupLineType.Separator));
                    isFirstEntry = false;

                    bool isAbbrMatch = _abbreviationMatchIds.Contains(entry.Id);
                    string heading;
                    if (isAbbrMatch && !string.IsNullOrEmpty(entry.TargetAbbreviation))
                        heading = $"{entry.SourceAbbreviation} \u2192 {entry.TargetAbbreviation}";
                    else
                        heading = $"{entry.SourceTerm} \u2192 {entry.TargetTerm}";
                    if (!string.IsNullOrEmpty(entry.TermbaseName))
                        heading += $" [{entry.TermbaseName}]";
                    heading += $" (ID {entry.Id})";
                    lines.Add(new PopupLine(heading, PopupLineType.Heading));

                    // Show the complementary form (abbreviation or full term)
                    if (!string.IsNullOrEmpty(entry.SourceAbbreviation) &&
                        !string.IsNullOrEmpty(entry.TargetAbbreviation))
                    {
                        if (isAbbrMatch)
                            lines.Add(new PopupLine($"  Full: {entry.SourceTerm} \u2192 {entry.TargetTerm}", PopupLineType.Synonym));
                        else
                            lines.Add(new PopupLine($"  Abbr: {entry.SourceAbbreviation} \u2192 {entry.TargetAbbreviation}", PopupLineType.Synonym));
                    }

                    // Source synonyms (alternative source forms that match this entry)
                    if (entry.SourceSynonyms != null && entry.SourceSynonyms.Count > 0)
                    {
                        var srcSynTexts = string.Join(", ",
                            entry.SourceSynonyms
                                .Where(s => !string.IsNullOrEmpty(s.Text))
                                .Select(s => s.Text));
                        if (!string.IsNullOrEmpty(srcSynTexts))
                            lines.Add(new PopupLine($"  Also: {srcSynTexts}", PopupLineType.Synonym));
                    }

                    // Target synonyms (alternative translations)
                    foreach (var syn in entry.TargetSynonyms)
                        lines.Add(new PopupLine($"  \u2022 {syn}", PopupLineType.Synonym));

                    bool hasDefOrDomain = !string.IsNullOrEmpty(entry.Definition) || !string.IsNullOrEmpty(entry.Domain);
                    if (hasDefOrDomain)
                        lines.Add(new PopupLine("", PopupLineType.Plain)); // space before def/domain
                    if (!string.IsNullOrEmpty(entry.Definition))
                        lines.Add(new PopupLine("  Def:", entry.Definition, PopupLineType.MetaLabelled));
                    if (!string.IsNullOrEmpty(entry.Domain))
                        lines.Add(new PopupLine("  Domain:", entry.Domain, PopupLineType.MetaLabelled));
                    if (!string.IsNullOrEmpty(entry.Notes))
                    {
                        lines.Add(new PopupLine("", PopupLineType.Plain)); // space before notes
                        lines.Add(new PopupLine("  Notes:", entry.Notes, PopupLineType.MetaLabelled));
                    }
                    if (!string.IsNullOrEmpty(entry.Url))
                        lines.Add(new PopupLine("  URL:", entry.Url, PopupLineType.MetaLabelled));
                }

                var popup = TermPopup.GetInstance();
                popup.ShowBelow(this, lines);
            }

            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _isHovered = false;
            Invalidate();

            // Schedule popup close (cancelled if mouse enters the popup)
            var popup = TermPopup.GetInstance();
            popup.ScheduleClose();

            base.OnMouseLeave(e);
        }

        protected override void OnClick(EventArgs e)
        {
            if (PrimaryEntry != null)
            {
                var textToInsert = IsAbbreviationMatch && !string.IsNullOrEmpty(PrimaryEntry.PrimaryTargetAbbreviation)
                    ? PrimaryEntry.PrimaryTargetAbbreviation
                    : PrimaryEntry.TargetTerm;
                TermInsertRequested?.Invoke(this, new TermInsertEventArgs
                {
                    TargetTerm = textToInsert,
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
