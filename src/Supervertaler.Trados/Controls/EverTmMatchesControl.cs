using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi.Interfaces;
using Supervertaler.Core.SuperVault;
using Supervertaler.Trados.SuperVault;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// What EverTM Matches shows for one segment: the lookup's answer, and what the pane copied from the editor to
    /// insert a match later. Built on the worker (the answer) and the UI thread (the rest).
    /// </summary>
    public sealed class EverTmPaneResult
    {
        public List<VaultMatch> Matches = new List<VaultMatch>();
        public long Units;
        public string Problem, Building, Error;
        public long Milliseconds;
        /// <summary>"EverTM is not in this project's TMs…", once per document; null when it learns or it isn't known.</summary>
        public string ProjectNote;
        // For inserting and for the summary line.
        public EverTmEditorSegment Editor;
        public Dictionary<int, Core.TagInfo> TagMapAtLookup;
        public string ParagraphId, SegmentId;
        public string SourceLang, SourceCulture, TargetCulture;
    }

    /// <summary>
    /// The EverTM Matches pane: a status line, the matches (an owner-drawn list that draws only the rows on screen),
    /// and the selected match's details below, on a splitter (docs/design/evertm-pane.md, section 1).
    /// </summary>
    public sealed class EverTmMatchesControl : UserControl, IUIControl
    {
        readonly Label _status;
        readonly LinkLabel _sort;
        readonly SplitContainer _split;
        readonly EverTmMatchList _list;
        readonly RichTextBox _details;
        Panel _licenseOverlay;
        EverTmPaneResult _result;
        bool _byDate;
        string _note;   // a message about the last action ("inserted without formatting"), until the next result

        /// <summary>The user asked to insert this match (double-click, Enter, a digit in the pane, the menu).</summary>
        public event Action<VaultMatch, EverTmPaneResult> InsertRequested;

        public EverTmMatchesControl()
        {
            BackColor = Color.White;
            Font = new Font("Segoe UI", 9f);

            var top = new Panel { Dock = DockStyle.Top, Height = 22, BackColor = Color.FromArgb(246, 246, 246), Padding = new Padding(6, 0, 6, 0) };
            _status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, ForeColor = Color.FromArgb(70, 70, 70) };
            _sort = new LinkLabel { Dock = DockStyle.Right, AutoSize = true, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 4, 0, 0), LinkColor = Color.FromArgb(30, 100, 180) };
            _sort.LinkClicked += (s, e) => { _byDate = !_byDate; UpdateSortLink(); Redraw(); };
            top.Controls.Add(_status);
            top.Controls.Add(_sort);
            UpdateSortLink();

            _list = new EverTmMatchList { Dock = DockStyle.Fill };
            _list.SelectionChanged += (s, e) => ShowDetails();
            _list.InsertRequested += m => { if (_result != null) InsertRequested?.Invoke(m, _result); };

            _details = new RichTextBox
            {
                Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.White,
                DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Vertical, WordWrap = true, TabStop = false,
            };

            _split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 5, BackColor = Color.FromArgb(225, 225, 225) };
            _split.Panel1.BackColor = Color.White;
            _split.Panel2.BackColor = Color.White;
            _split.Panel1.Controls.Add(_list);
            _split.Panel2.Controls.Add(_details);
            _split.Panel2.Padding = new Padding(6, 4, 4, 2);

            Controls.Add(_split);
            Controls.Add(top);
            Load += (s, e) => { try { _split.SplitterDistance = Math.Max(60, (int)(Height * 0.62)); } catch { } };

            ShowStatus("Open a document to see EverTM's matches.");
        }

        void UpdateSortLink() => _sort.Text = _byDate ? "Sorted by date" : "Sorted by score";

        // ---- what the view part calls ----

        /// <summary>The newest segment's result. UI thread.</summary>
        public void ShowResult(EverTmPaneResult r)
        {
            _result = r;
            _note = null;
            Redraw();
        }

        /// <summary>A plain message instead of matches: no document, no segment, no licence.</summary>
        public void ShowMessage(string message)
        {
            _result = null;
            _note = null;
            _list.SetRows(new List<VaultMatch>(), null);
            _details.Clear();
            ShowStatus(message);
        }

        /// <summary>The segment changed and its lookup is on its way: the old matches are greyed, and can't be inserted.</summary>
        public void MarkStale() => _list.Stale = true;

        /// <summary>A note about the last action, shown in the status line until the next segment's result.</summary>
        public void ShowNote(string note)
        {
            _note = note;
            ShowStatus(StatusText());
        }

        public void FocusList()
        {
            try { _list.Focus(); } catch { }
        }

        /// <summary>The match shown as number <paramref name="n"/> (1-based), for the Alt+Shift+N shortcuts.</summary>
        public VaultMatch MatchNumber(int n) => _list.Stale ? null : _list.RowAt(n - 1);

        public EverTmPaneResult Result => _list.Stale ? null : _result;

        // ---- drawing ----

        void Redraw()
        {
            var r = _result;
            if (r == null) return;
            var rows = r.Matches ?? new List<VaultMatch>();
            if (_byDate)
                rows = rows.Select((m, i) => (m, i)).OrderByDescending(x => (x.m.Candidate?.Changed ?? x.m.Candidate?.Created) ?? DateTime.MinValue).ThenBy(x => x.i).Select(x => x.m).ToList();
            _list.SetRows(rows, r);
            ShowStatus(StatusText());
            ShowDetails();
        }

        string StatusText()
        {
            var r = _result;
            if (r == null) return _status.Text;
            string text;
            if (r.Error != null) text = "EverTM lookup failed: " + r.Error;
            else if (r.Problem != null) text = r.Problem;
            else
            {
                int n = r.Matches?.Count ?? 0;
                text = VaultLibrary.Units(r.Units) + " · " + (n == 0 ? "no matches" : n == 1 ? "1 match" : n + " matches") + " · " + r.Milliseconds.ToString(CultureInfo.CurrentCulture) + " ms";
                if (r.Building != null) text = "EverTM is being rebuilt (" + r.Building + "); matches from the index before it · " + text;
            }
            if (r.ProjectNote != null) text += " · " + r.ProjectNote;
            if (_note != null) text = _note + " · " + text;
            return text;
        }

        void ShowStatus(string text)
        {
            _status.Text = text ?? "";
        }

        void ShowDetails()
        {
            var m = _list.Selected;
            _details.Clear();
            if (m == null || _result == null) return;
            var c = m.Candidate ?? new VaultCandidate();
            var bold = new Font(Font, FontStyle.Bold);
            void Line(string label, string value)
            {
                _details.SelectionFont = bold;
                _details.SelectionColor = Color.FromArgb(90, 90, 90);
                _details.AppendText(label + "  ");
                _details.SelectionFont = Font;
                _details.SelectionColor = Color.Black;
                _details.AppendText(value + "\n");
            }

            _details.SelectionFont = bold;
            _details.AppendText("Match " + (_list.SelectedIndex + 1) + ", " + m.Score + "%" + (c.Exact ? " (exact)" : "") + "\n");

            var fields = c.Fields ?? new List<(string Field, string Value)>();
            foreach (var name in VaultFields.Order.Concat(fields.Select(f => f.Field).Where(f => !VaultFields.Order.Contains(f)).Distinct()))
            {
                var values = fields.Where(f => f.Field == name && !string.IsNullOrWhiteSpace(f.Value)).Select(f => f.Value.Trim()).Distinct().ToList();
                if (values.Count == 0) continue;
                string shown = string.Join("; ", values);
                if (values.Count >= VaultFields.MaxValuesPerField) shown += "  (" + values.Count + " shown; more may exist)";
                Line(name, shown);
            }
            if (fields.Count == 0) Line("Fields", "none stored for this translation unit");
            Line("Created", When(c.Created, c.CreatedBy));
            Line("Changed", When(c.Changed, c.ChangedBy));
            Line("In EverTM since", c.Added == null ? "not known" : EverTmSummary.FormatDate(c.Added.Value));
            Line("Language variants", string.IsNullOrWhiteSpace(c.Variant) ? "not known" : c.Variant.Replace(">", " > "));
            if (c.Older > 0)
                Line("Older translations", c.Older.ToString(CultureInfo.CurrentCulture) + " (newer translations of the same sentence win; the older ones will be listed here in a later version)");
            if (m.NumbersReplaced > 0)
                Line("Numbers", m.NumbersReplaced == 1 ? "1 number replaced by your segment's" : m.NumbersReplaced + " numbers replaced by your segment's");
            if (m.Tags == VaultTagOutcome.Unmapped && _result.Editor != null && _result.Editor.HasTags)
                Line("Formatting", "this match's tags can't be placed with confidence: it is inserted without formatting");
            if (m.Diff != null && m.Diff.Edits == null)
                Line("Differences", "too long to compare word by word");
            _details.SelectionStart = 0;
            _details.ScrollToCaret();
            bold.Dispose();
        }

        static string When(DateTime? utc, string user)
        {
            if (utc == null) return string.IsNullOrWhiteSpace(user) ? "not known" : user;
            return EverTmSummary.FormatDate(utc.Value) + (string.IsNullOrWhiteSpace(user) ? "" : ", " + user);
        }

        // ---- licence ----

        public void ShowLicenseRequired()
        {
            if (_licenseOverlay != null) return;
            _licenseOverlay = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            _licenseOverlay.Controls.Add(new Label
            {
                Text = "EverTM Matches needs a Supervertaler licence.\nOpen Settings → Licence (the ⚙ button in TermLens)\nand enter a licence key.",
                AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5f), ForeColor = Color.FromArgb(100, 100, 100),
            });
            Controls.Add(_licenseOverlay);
            _licenseOverlay.BringToFront();
        }

        public void HideLicenseRequired()
        {
            if (_licenseOverlay == null) return;
            Controls.Remove(_licenseOverlay);
            _licenseOverlay.Dispose();
            _licenseOverlay = null;
        }
    }

    /// <summary>
    /// The list of matches. Each row: number and score; the stored source with the differences from your segment
    /// marked; the stored target; one faint summary line. Owner-drawn: only the rows on screen are painted, and the
    /// layout (word positions) is worked out once per result and per width, for at most 20 rows.
    /// </summary>
    internal sealed class EverTmMatchList : Control
    {
        enum Style { Normal, Removed, Added, Tag }

        struct Run { public string Text; public Style Style; public bool Space; }
        struct Placed { public Run Run; public Rectangle Box; }

        sealed class Row
        {
            public VaultMatch Match;
            public List<Run> Source, Target;
            public string Summary, Older;
            public bool TooLong;
            // Layout, for the current width.
            public int Top, Height;
            public List<Placed> SourceRuns = new List<Placed>(), TargetRuns = new List<Placed>();
            public int SummaryY;
        }

        readonly VScrollBar _scroll = new VScrollBar { Dock = DockStyle.Right, Visible = false };
        readonly ContextMenuStrip _menu = new ContextMenuStrip();
        readonly Font _normal, _strike, _under, _small, _bold, _tag;
        readonly Dictionary<(string, Style), int> _widths = new Dictionary<(string, Style), int>();
        List<Row> _rows = new List<Row>();
        EverTmPaneResult _result;
        int _selected = -1;
        int _laidOutWidth = -1;
        int _contentHeight;
        bool _stale;

        const int Gutter = 54, Pad = 5, Gap = 2;
        static readonly Color RemovedColor = Color.FromArgb(190, 30, 30), AddedColor = Color.FromArgb(0, 120, 40),
            AddedBack = Color.FromArgb(222, 244, 226), TagColor = Color.FromArgb(120, 120, 120), TagBack = Color.FromArgb(232, 232, 232),
            SummaryColor = Color.FromArgb(140, 140, 140), SelectedBack = Color.FromArgb(220, 235, 252), Line = Color.FromArgb(232, 232, 232);

        public event EventHandler SelectionChanged;
        public event Action<VaultMatch> InsertRequested;

        public EverTmMatchList()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            BackColor = Color.White;
            _normal = new Font("Segoe UI", 9.5f);
            _strike = new Font(_normal, FontStyle.Strikeout);
            _under = new Font(_normal, FontStyle.Underline);
            _small = new Font("Segoe UI", 8f);
            _bold = new Font("Segoe UI", 9f, FontStyle.Bold);
            _tag = new Font("Segoe UI", 7.5f);
            Controls.Add(_scroll);
            _scroll.Scroll += (s, e) => Invalidate();
            _scroll.ValueChanged += (s, e) => Invalidate();

            var insert = new ToolStripMenuItem("Insert this match", null, (s, e) => RequestInsert());
            var copy = new ToolStripMenuItem("Copy the target text", null, (s, e) => CopyTarget());
            _menu.Items.Add(insert);
            _menu.Items.Add(copy);
            _menu.Opening += (s, e) => { insert.Enabled = !_stale && Selected != null; copy.Enabled = Selected != null; };
            ContextMenuStrip = _menu;
        }

        public bool Stale
        {
            get => _stale;
            set { if (_stale != value) { _stale = value; Invalidate(); } }
        }

        public VaultMatch Selected => _selected >= 0 && _selected < _rows.Count ? _rows[_selected].Match : null;
        public int SelectedIndex => _selected;
        public VaultMatch RowAt(int i) => i >= 0 && i < _rows.Count ? _rows[i].Match : null;

        public void SetRows(List<VaultMatch> matches, EverTmPaneResult result)
        {
            _result = result;
            _rows = matches.Select(m => MakeRow(m, result)).ToList();
            _selected = _rows.Count > 0 ? 0 : -1;
            _stale = false;
            _laidOutWidth = -1;
            _scroll.Value = 0;
            LayoutRows();
            Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        // ---- rows ----

        Row MakeRow(VaultMatch m, EverTmPaneResult r)
        {
            var row = new Row { Match = m, Source = SourceRuns(m, r), Target = PlainRuns(m.Target, Style.Normal) };
            row.TooLong = m.Diff != null && m.Diff.Edits == null;
            if (m.Candidate?.Older > 0) row.Older = m.Candidate.Older == 1 ? "1 older" : m.Candidate.Older + " older";
            return row;
        }

        /// <summary>
        /// The stored source with the differences from the document marked, from the alignment EverTM computed for the
        /// score (the one Translation Results draws): a stored word your segment lacks is struck through in red; a
        /// word your segment has and the stored one lacks is shown in place, green and underlined; a changed word is
        /// the stored one struck through, then yours. A number EverTM replaced counts as the same.
        /// </summary>
        static List<Run> SourceRuns(VaultMatch m, EverTmPaneResult r)
        {
            var memory = m.SourceTokens;
            if (m.Diff?.Edits == null || memory == null || m.DocumentTokens == null) return PlainRuns(m.Source, Style.Normal);
            var docPieces = r?.Editor?.Pieces;
            var before = new Dictionary<int, List<int>>();         // memory position -> document tokens to show before it
            var style = new Dictionary<int, Style>();              // memory token -> how it is drawn
            var changedTo = new Dictionary<int, int>();            // memory token -> the document token that replaced it
            foreach (var e in m.Diff.Edits)
            {
                switch (e.Op)
                {
                    case VaultEditOp.Insert: style[e.B] = Style.Removed; break;
                    case VaultEditOp.Delete:
                        if (!before.TryGetValue(e.B, out var list)) before[e.B] = list = new List<int>();
                        list.Add(e.A);
                        break;
                    case VaultEditOp.Change:
                        if (!e.Replaced) { style[e.B] = Style.Removed; changedTo[e.B] = e.A; }
                        break;
                }
            }
            var runs = new List<Run>();
            for (int b = 0; b <= memory.Count; b++)
            {
                if (before.TryGetValue(b, out var docs))
                    foreach (var a in docs) runs.Add(TokenRun(m.DocumentTokens, a, docPieces, Style.Added));
                if (b == memory.Count) break;
                var s = style.TryGetValue(b, out var st) ? st : Style.Normal;
                runs.Add(TokenRun(memory, b, m.Source, s));
                if (changedTo.TryGetValue(b, out var a2)) runs.Add(TokenRun(m.DocumentTokens, a2, docPieces, Style.Added));
            }
            return runs;
        }

        static Run TokenRun(List<VaultToken> tokens, int i, List<VaultPiece> pieces, Style style)
        {
            if (i < 0 || i >= tokens.Count) return new Run { Text = "", Style = style };
            var t = tokens[i];
            if (t.Kind == VaultTokenKind.Tag) return new Run { Text = TagMark(pieces, t.Piece), Style = Style.Tag };
            if (t.Kind == VaultTokenKind.Space) return new Run { Text = " ", Style = style == Style.Added ? Style.Added : Style.Normal, Space = true };
            return new Run { Text = t.Text, Style = style };
        }

        static string TagMark(List<VaultPiece> pieces, int piece)
        {
            var p = pieces != null && piece >= 0 && piece < pieces.Count ? pieces[piece] : null;
            if (p == null || !p.IsTag) return "•";
            return p.Kind == VaultTagKind.Open ? "‹" : p.Kind == VaultTagKind.Close ? "›" : "•";
        }

        static List<Run> PlainRuns(List<VaultPiece> pieces, Style style)
        {
            var runs = new List<Run>();
            if (pieces == null) return runs;
            var tokens = VaultDiff.Tokenize(pieces);
            for (int i = 0; i < tokens.Count; i++) runs.Add(TokenRun(tokens, i, pieces, style));
            return runs;
        }

        // ---- layout ----

        int ContentWidth => Math.Max(80, ClientSize.Width - (_scroll.Visible ? _scroll.Width : 0) - Gutter - 8);

        int Measure(string text, Style style)
        {
            var key = (text, style);
            if (_widths.TryGetValue(key, out var w)) return w;
            var font = FontOf(style);
            if (text == " ") w = TextRenderer.MeasureText("a a", font, Size.Empty, TextFormatFlags.NoPadding).Width - TextRenderer.MeasureText("aa", font, Size.Empty, TextFormatFlags.NoPadding).Width;
            else w = TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + (style == Style.Tag ? 6 : 0);
            if (_widths.Count > 20000) _widths.Clear();   // bounded: one segment's words, many segments
            _widths[key] = w;
            return w;
        }

        Font FontOf(Style s) => s == Style.Removed ? _strike : s == Style.Added ? _under : s == Style.Tag ? _tag : _normal;

        /// <summary>Places the runs word by word, wrapping at the width; returns the height used.</summary>
        int Flow(List<Run> runs, int x0, int y0, int width, List<Placed> placed)
        {
            placed.Clear();
            int line = _normal.Height + 1;
            int x = x0, y = y0;
            foreach (var r in runs)
            {
                int w = Measure(r.Text, r.Style);
                if (r.Space && x == x0) continue;                       // no space at the start of a line
                if (!r.Space && x > x0 && x + w > x0 + width) { x = x0; y += line; }
                placed.Add(new Placed { Run = r, Box = new Rectangle(x, y, w, line) });
                x += w;
            }
            return y - y0 + line;
        }

        void LayoutRows()
        {
            int width = ContentWidth;
            int y = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                y = 0;
                width = ContentWidth;
                foreach (var row in _rows)
                {
                    row.Top = y;
                    int h = Pad;
                    h += Flow(row.Source, Gutter, y + h, width, row.SourceRuns) + Gap;
                    h += Flow(row.Target, Gutter, y + h, width, row.TargetRuns) + Gap;
                    row.SummaryY = y + h;
                    row.Summary = SummaryLine(row, width);
                    h += _small.Height + Pad;
                    row.Height = h;
                    y += h;
                }
                bool need = y > ClientSize.Height;
                if (need == _scroll.Visible) break;
                _scroll.Visible = need;   // the scroll bar takes width: lay out once more
            }
            _contentHeight = y;
            _laidOutWidth = ClientSize.Width;
            if (_scroll.Visible)
            {
                _scroll.Maximum = Math.Max(0, _contentHeight);
                _scroll.LargeChange = Math.Max(1, ClientSize.Height);
                _scroll.SmallChange = _normal.Height * 2;
                if (_scroll.Value > Math.Max(0, _contentHeight - ClientSize.Height)) _scroll.Value = Math.Max(0, _contentHeight - ClientSize.Height);
            }
            else _scroll.Value = 0;
        }

        int OlderWidth(Row row) => row.Older == null ? 0 : TextRenderer.MeasureText(row.Older, _small, Size.Empty, TextFormatFlags.NoPadding).Width + 10;

        /// <summary>The faint line, fitted once per layout (not per paint): Client · Project · date, and the variant.</summary>
        string SummaryLine(Row row, int width)
        {
            var parts = EverTmSummary.Make(row.Match.Candidate, _result?.SourceLang, _result?.SourceCulture, _result?.TargetCulture);
            if (row.TooLong) parts.Tail += " · too long to compare word by word";
            return EverTmSummary.Fit(parts, width - OlderWidth(row), s => TextRenderer.MeasureText(s, _small, Size.Empty, TextFormatFlags.NoPadding).Width);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (ClientSize.Width != _laidOutWidth && _rows.Count > 0) LayoutRows();
            Invalidate();
        }

        // ---- painting ----

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (_rows.Count == 0) return;
            if (_laidOutWidth != ClientSize.Width) LayoutRows();
            int offset = _scroll.Visible ? _scroll.Value : 0;
            int right = ClientSize.Width - (_scroll.Visible ? _scroll.Width : 0);
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                int top = row.Top - offset;
                if (top + row.Height < 0) continue;
                if (top > ClientSize.Height) break;                // only the rows on screen

                if (i == _selected)
                    using (var b = new SolidBrush(Focused ? SelectedBack : Color.FromArgb(236, 242, 250))) g.FillRectangle(b, 0, top, right, row.Height);
                using (var p = new Pen(Line)) g.DrawLine(p, 0, top + row.Height - 1, right, top + row.Height - 1);

                // Number and score.
                var score = row.Match.Score;
                Color badge = score >= 100 ? Color.FromArgb(46, 140, 60) : score >= 85 ? Color.FromArgb(200, 130, 0) : Color.FromArgb(150, 110, 60);
                if (_stale) badge = Color.Silver;
                TextRenderer.DrawText(g, (i + 1).ToString(CultureInfo.InvariantCulture), _bold, new Point(6, top + Pad), _stale ? Color.Silver : Color.FromArgb(80, 80, 80), TextFormatFlags.NoPadding);
                var scoreText = score + "%";
                var scoreBox = new Rectangle(18, top + Pad, Gutter - 22, _normal.Height);
                using (var b = new SolidBrush(badge)) g.FillRectangle(b, scoreBox);
                TextRenderer.DrawText(g, scoreText, _bold, scoreBox, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                DrawRuns(g, row.SourceRuns, -offset);
                DrawRuns(g, row.TargetRuns, -offset);

                // The summary line, faint; "N older" at its right.
                int sy = row.SummaryY - offset;
                if (row.Older != null)
                {
                    int olderWidth = OlderWidth(row);
                    TextRenderer.DrawText(g, row.Older, _small, new Point(right - olderWidth, sy), SummaryColor, TextFormatFlags.NoPadding);
                }
                TextRenderer.DrawText(g, row.Summary ?? "", _small, new Point(Gutter, sy), SummaryColor, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
        }

        void DrawRuns(Graphics g, List<Placed> runs, int dy)
        {
            foreach (var p in runs)
            {
                var box = p.Box;
                box.Offset(0, dy);
                if (box.Bottom < 0 || box.Top > ClientSize.Height) continue;
                var style = p.Run.Style;
                Color fore = _stale ? Color.Silver : style == Style.Removed ? RemovedColor : style == Style.Added ? AddedColor : style == Style.Tag ? TagColor : ForeColorFor();
                if (style == Style.Added && !_stale) using (var b = new SolidBrush(AddedBack)) g.FillRectangle(b, box);
                if (style == Style.Tag)
                {
                    var pill = new Rectangle(box.X + 1, box.Y + 3, box.Width - 2, box.Height - 6);
                    using (var b = new SolidBrush(TagBack)) g.FillRectangle(b, pill);
                    TextRenderer.DrawText(g, p.Run.Text, _tag, pill, fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    continue;
                }
                TextRenderer.DrawText(g, p.Run.Text, FontOf(style), box.Location, fore, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
        }

        Color ForeColorFor() => Color.FromArgb(25, 25, 25);

        // ---- mouse and keyboard ----

        int RowAtPoint(Point p)
        {
            int y = p.Y + (_scroll.Visible ? _scroll.Value : 0);
            for (int i = 0; i < _rows.Count; i++) if (y >= _rows[i].Top && y < _rows[i].Top + _rows[i].Height) return i;
            return -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            int i = RowAtPoint(e.Location);
            if (i >= 0) Select(i);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left && RowAtPoint(e.Location) == _selected) RequestInsert();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!_scroll.Visible) return;
            int v = _scroll.Value - Math.Sign(e.Delta) * _normal.Height * 3;
            _scroll.Value = Math.Max(0, Math.Min(Math.Max(0, _contentHeight - ClientSize.Height), v));
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Up: case Keys.Down: case Keys.Home: case Keys.End: case Keys.PageUp: case Keys.PageDown: case Keys.Enter: return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Modifiers != Keys.None) return;
            switch (e.KeyCode)
            {
                case Keys.Up: Select(Math.Max(0, _selected - 1)); e.Handled = true; break;
                case Keys.Down: Select(Math.Min(_rows.Count - 1, _selected + 1)); e.Handled = true; break;
                case Keys.Home: Select(0); e.Handled = true; break;
                case Keys.End: Select(_rows.Count - 1); e.Handled = true; break;
                case Keys.PageUp: Select(Math.Max(0, _selected - 5)); e.Handled = true; break;
                case Keys.PageDown: Select(Math.Min(_rows.Count - 1, _selected + 5)); e.Handled = true; break;
                case Keys.Enter: RequestInsert(); e.Handled = true; e.SuppressKeyPress = true; break;
                default:
                    if (e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D9 || e.KeyCode >= Keys.NumPad1 && e.KeyCode <= Keys.NumPad9)
                    {
                        int n = e.KeyCode >= Keys.NumPad1 ? e.KeyCode - Keys.NumPad1 : e.KeyCode - Keys.D1;
                        if (n < _rows.Count) { Select(n); RequestInsert(); }
                        e.Handled = true;
                    }
                    break;
            }
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        void Select(int i)
        {
            if (i < 0 || i >= _rows.Count || i == _selected) return;
            _selected = i;
            // Keep it in view.
            if (_scroll.Visible)
            {
                var row = _rows[i];
                if (row.Top < _scroll.Value) _scroll.Value = row.Top;
                else if (row.Top + row.Height > _scroll.Value + ClientSize.Height)
                    _scroll.Value = Math.Min(Math.Max(0, _contentHeight - ClientSize.Height), row.Top + row.Height - ClientSize.Height);
            }
            Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        void RequestInsert()
        {
            var m = Selected;
            if (m == null || _stale) return;
            InsertRequested?.Invoke(m);
        }

        void CopyTarget()
        {
            var m = Selected;
            if (m == null) return;
            try { Clipboard.SetText(EverTmEditorSegment.PlainTarget(m)); } catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _normal.Dispose(); _strike.Dispose(); _under.Dispose(); _small.Dispose(); _bold.Dispose(); _tag.Dispose();
                _menu.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
