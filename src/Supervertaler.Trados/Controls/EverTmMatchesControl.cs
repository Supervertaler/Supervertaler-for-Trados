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
        /// <summary>"EverTM is not in this project's TMs…"; null when it learns or it isn't known.</summary>
        public string ProjectNote;
        /// <summary>EverTM is switched off in the project's TM list (Enabled or Lookup unticked): not looked up, as in Studio.</summary>
        public bool EverTmOff;
        // For inserting and for the summary line.
        public EverTmEditorSegment Editor;
        public Dictionary<int, Core.TagInfo> TagMapAtLookup;
        public string ParagraphId, SegmentId;
        public string SourceLang, SourceCulture, TargetCulture;

        // Studio TMs' matches (Options > "Also show matches from the project's Studio TMs"). Matches is then the merged
        // list (StudioTmMatches.Merge); EverTmMatches keeps EverTM's own.
        /// <summary>EverTM's own matches, once Studio TMs' have been merged in; null before.</summary>
        public List<VaultMatch> EverTmMatches;
        /// <summary>The rows that are a Studio TM's, and which TM's (by reference).</summary>
        public Dictionary<VaultMatch, StudioTmMatches.Tm> FromStudio = new Dictionary<VaultMatch, StudioTmMatches.Tm>();
        /// <summary>EverTM's rows that a Studio TM has too: those TMs' names.</summary>
        public Dictionary<VaultMatch, List<string>> AlsoIn = new Dictionary<VaultMatch, List<string>>();
        /// <summary>Which Studio TM search belongs to this result ("file|segment"); null when the switch is off.</summary>
        public string StudioKey;
        /// <summary>The Studio TMs are still being searched.</summary>
        public bool StudioPending;
        /// <summary>The Studio TMs' answer, once merged in.</summary>
        public StudioTmMatches.Answer Studio;

        /// <summary>The Studio TM a row came from; null for EverTM's.</summary>
        public StudioTmMatches.Tm StudioOf(VaultMatch m) => m != null && FromStudio != null && FromStudio.TryGetValue(m, out var t) ? t : null;

        /// <summary>Merges the Studio TMs' answer into this result (UI thread). Again with a newer answer: from EverTM's own.</summary>
        public void MergeStudio(StudioTmMatches.Answer answer, int max)
        {
            if (answer == null) return;
            EverTmMatches = EverTmMatches ?? Matches ?? new List<VaultMatch>();
            var merged = StudioTmMatches.Merge(EverTmMatches, answer.Hits, max);
            Matches = merged.Matches;
            FromStudio = merged.FromStudio;
            AlsoIn = merged.AlsoIn;
            Studio = answer;
            StudioPending = false;
        }
    }

    /// <summary>
    /// The EverTM Matches pane: a status line, the matches (an owner-drawn list that draws only the rows on screen),
    /// and the selected match's details below, on a splitter (docs/design/evertm-pane.md, section 1).
    /// </summary>
    public sealed class EverTmMatchesControl : UserControl, IUIControl
    {
        readonly Label _status;
        readonly LinkLabel _options;
        /// <summary>"Undo" after a removal: until the next removal or until the pane closes.</summary>
        readonly LinkLabel _undo;
        string _removedNote;      // "Removed from EverTM: ..." until a result for another segment arrives
        string _removedSegment;
        readonly ContextMenuStrip _optionsMenu = new ContextMenuStrip();
        readonly SplitContainer _split;
        readonly EverTmMatchList _list;
        readonly RichTextBox _details;
        // The selected match's older translations, under its details (docs/design/evertm-pane.md, section 7).
        readonly SplitContainer _detailSplit;
        readonly Label _olderTitle;
        readonly LinkLabel _olderMore;
        readonly EverTmMatchList _older;
        VaultMatch _olderFor;      // the match whose older translations are shown (or on their way)
        int _olderTotal, _olderPages;
        bool _olderLoading;
        VaultMatch _detailsOf;     // the older translation whose details are shown; null: the selected match's
        Panel _licenseOverlay;
        EverTmPaneResult _result;
        bool _byDate;
        string _note;   // a message about the last action ("inserted without formatting"), until the next result

        /// <summary>The user asked to insert this match (double-click, Enter, a digit in the pane, the menu).</summary>
        public event Action<VaultMatch, EverTmPaneResult> InsertRequested;

        /// <summary>The minimum % or the number of matches changed: the segment is looked up again.</summary>
        public event EventHandler OptionsChanged;

        /// <summary>"Remove from EverTM" on a match or an older translation (right-click, or Delete).</summary>
        public event Action<VaultMatch> RemoveRequested;

        /// <summary>"Undo" after a removal or an edit.</summary>
        public event EventHandler UndoRequested;

        /// <summary>"Edit in EverTM..." on a match or an older translation (right-click, or F2).</summary>
        public event Action<VaultMatch> EditRequested;

        /// <summary>A page (0 is the first) of this match's older translations is wanted: the view part looks them up in
        /// the background and hands them to <see cref="ShowOlder"/>.</summary>
        public event Action<VaultMatch, EverTmPaneResult, int> OlderWanted;

        public EverTmMatchesControl()
        {
            BackColor = Color.White;
            Font = new Font("Segoe UI", 9f);

            var top = new Panel { Dock = DockStyle.Top, Height = Font.Height + 8, BackColor = Color.FromArgb(246, 246, 246), Padding = new Padding(6, 0, 6, 0) };
            _status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, ForeColor = Color.FromArgb(70, 70, 70) };
            _options = new LinkLabel { Dock = DockStyle.Right, AutoSize = true, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 4, 0, 0), LinkColor = Color.FromArgb(30, 100, 180), Text = "Options ▾" };
            _options.LinkClicked += (s, e) => { BuildOptionsMenu(); _optionsMenu.Show(_options, new Point(0, _options.Height)); };
            _undo = new LinkLabel { Dock = DockStyle.Right, AutoSize = true, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 4, 10, 0), LinkColor = Color.FromArgb(30, 100, 180), Text = "Undo", Visible = false };
            _undo.LinkClicked += (s, e) => UndoRequested?.Invoke(this, EventArgs.Empty);
            top.Controls.Add(_status);
            top.Controls.Add(_undo);
            top.Controls.Add(_options);

            _list = new EverTmMatchList { Dock = DockStyle.Fill };
            try { _list.Stacked = Settings.SettingsService.Current.EverTmPaneStacked; } catch { }
            _list.SelectionChanged += (s, e) => MatchSelected();
            _list.InsertRequested += m => { if (_result != null) InsertRequested?.Invoke(m, _result); };
            _list.OlderClicked += m => ToggleOlder(m);
            _list.RemoveRequested += m => RemoveRequested?.Invoke(m);
            _list.EditRequested += m => EditRequested?.Invoke(m);
            _list.ShowOlderNote = !OlderHidden;

            _details = new RichTextBox
            {
                Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.White,
                DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Vertical, WordWrap = true, TabStop = false,
            };

            _older = new EverTmMatchList(others: true) { Dock = DockStyle.Fill };
            _older.SelectionChanged += (s, e) => { _detailsOf = _older.Selected; ShowDetails(); };
            _older.InsertRequested += m => { if (_result != null) InsertRequested?.Invoke(m, _result); };
            _older.RemoveRequested += m => RemoveRequested?.Invoke(m);
            _older.EditRequested += m => EditRequested?.Invoke(m);
            var olderTop = new Panel { Dock = DockStyle.Top, Height = Font.Height + 6, BackColor = Color.FromArgb(246, 246, 246), Padding = new Padding(6, 0, 6, 0) };
            _olderTitle = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, ForeColor = Color.FromArgb(70, 70, 70) };
            _olderMore = new LinkLabel { Dock = DockStyle.Right, AutoSize = true, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 3, 0, 0), LinkColor = Color.FromArgb(30, 100, 180), Visible = false };
            _olderMore.LinkClicked += (s, e) => MoreOlder();
            olderTop.Controls.Add(_olderTitle);
            olderTop.Controls.Add(_olderMore);

            _detailSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 5, BackColor = Color.FromArgb(225, 225, 225), Panel2Collapsed = true };
            _detailSplit.Panel1.BackColor = Color.White;
            _detailSplit.Panel2.BackColor = Color.White;
            _detailSplit.Panel1.Padding = new Padding(6, 4, 4, 2);
            _detailSplit.Panel1.Controls.Add(_details);
            _detailSplit.Panel2.Controls.Add(_older);
            _detailSplit.Panel2.Controls.Add(olderTop);

            _split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 5, BackColor = Color.FromArgb(225, 225, 225) };
            _split.Panel1.BackColor = Color.White;
            _split.Panel2.BackColor = Color.White;
            _split.Panel1.Controls.Add(_list);
            _split.Panel2.Controls.Add(_detailSplit);

            Controls.Add(_split);
            Controls.Add(top);
            Load += (s, e) =>
            {
                try { _split.SplitterDistance = Math.Max(60, (int)(Height * 0.62)); } catch { }
                try { _detailSplit.SplitterDistance = Math.Max(40, _detailSplit.Height / 2); } catch { }
            };

            ShowStatus("Open a document to see EverTM's matches.");
        }

        /// <summary>
        /// The Options menu: the layout (side by side or stacked), the lowest match % shown, how many matches, and the
        /// order. Remembered in Supervertaler's settings, except the order (it starts on score each time).
        /// </summary>
        void BuildOptionsMenu()
        {
            var s = Settings.SettingsService.Current;
            _optionsMenu.Items.Clear();
            ToolStripMenuItem Item(string text, bool check, Action click)
            {
                var item = new ToolStripMenuItem(text) { Checked = check };
                item.Click += (o, e) => click();
                return item;
            }
            _optionsMenu.Items.Add(Item("Source and target side by side", !s.EverTmPaneStacked, () => SetStacked(false)));
            _optionsMenu.Items.Add(Item("Source above target", s.EverTmPaneStacked, () => SetStacked(true)));
            _optionsMenu.Items.Add(new ToolStripSeparator());

            var min = new ToolStripMenuItem("Lowest match shown: " + s.EverTmPaneMinScore + "%");
            foreach (var v in new[] { 50, 60, 70, 75, 80, 85, 90, 95, 100 })
            {
                int value = v;
                min.DropDownItems.Add(Item(value + "%", s.EverTmPaneMinScore == value, () =>
                {
                    Settings.SettingsService.Update(x => x.EverTmPaneMinScore = value);
                    OptionsChanged?.Invoke(this, EventArgs.Empty);
                }));
            }
            _optionsMenu.Items.Add(min);

            var max = new ToolStripMenuItem("Most matches shown: " + s.EverTmPaneMaxMatches);
            foreach (var v in new[] { 1, 3, 5, 10, 15, 20 })
            {
                int value = v;
                max.DropDownItems.Add(Item(value.ToString(CultureInfo.CurrentCulture), s.EverTmPaneMaxMatches == value, () =>
                {
                    Settings.SettingsService.Update(x => x.EverTmPaneMaxMatches = value);
                    OptionsChanged?.Invoke(this, EventArgs.Empty);
                }));
            }
            _optionsMenu.Items.Add(max);
            _optionsMenu.Items.Add(DetailsMenu());
            _optionsMenu.Items.Add(new ToolStripSeparator());
            _optionsMenu.Items.Add(Item("Also show matches from the project's Studio TMs", s.EverTmPaneStudioTms, () =>
            {
                Settings.SettingsService.Update(x => x.EverTmPaneStudioTms = !x.EverTmPaneStudioTms);
                OptionsChanged?.Invoke(this, EventArgs.Empty);
            }));
            _optionsMenu.Items.Add(new ToolStripSeparator());
            _optionsMenu.Items.Add(Item("Keep Translation Results behind Supervertaler's panes", s.KeepTranslationResultsBack,
                () => Settings.SettingsService.Update(x => x.KeepTranslationResultsBack = !x.KeepTranslationResultsBack)));
            _optionsMenu.Items.Add(new ToolStripSeparator());
            _optionsMenu.Items.Add(Item("Best match first", !_byDate, () => { _byDate = false; Redraw(); }));
            _optionsMenu.Items.Add(Item("Newest first", _byDate, () => { _byDate = true; Redraw(); }));
        }

        void SetStacked(bool stacked)
        {
            Settings.SettingsService.Update(x => x.EverTmPaneStacked = stacked);
            _list.Stacked = stacked;
        }

        // ---- what the view part calls ----

        /// <summary>The newest segment's result. UI thread.</summary>
        public void ShowResult(EverTmPaneResult r)
        {
            _result = r;
            _note = null;
            if (_removedNote != null && r?.SegmentId != _removedSegment) _removedNote = null;   // said on the segment it was removed on
            Redraw();
        }

        /// <summary>
        /// The same segment's result again with the Studio TMs' matches merged in: the selected match stays selected,
        /// and the note about the last action stays. UI thread.
        /// </summary>
        public void UpdateResult(EverTmPaneResult r)
        {
            var selected = _list.Selected;
            _result = r;
            Redraw();
            if (selected != null) _list.SelectMatch(selected);
        }

        /// <summary>A plain message instead of matches: no document, no segment, no licence.</summary>
        public void ShowMessage(string message)
        {
            _result = null;
            _note = null;
            _list.SetRows(new List<VaultMatch>(), null);
            ClearOlder();
            _details.Clear();
            ShowStatus(message);
        }

        /// <summary>The segment changed and its lookup is on its way: the old matches are greyed, and can't be inserted.</summary>
        public void MarkStale() { _list.Stale = true; _older.Stale = true; }

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

        /// <summary>The last result shown, also while the next segment's is on its way (for the languages of a removal).</summary>
        public EverTmPaneResult LastResult => _result;

        /// <summary>
        /// After a removal: says what was removed, and that only EverTM was changed, and offers Undo (until the next
        /// removal or until the pane closes). <paramref name="undo"/> false: after Undo, the message only.
        /// </summary>
        public void ShowRemoved(string message, bool undo)
        {
            _removedNote = message;
            _removedSegment = _result?.SegmentId;
            _undo.Visible = undo;
            ShowStatus(StatusText());
        }

        // ---- drawing ----

        void Redraw()
        {
            var r = _result;
            if (r == null) return;
            var rows = r.Matches ?? new List<VaultMatch>();
            if (_byDate)
                rows = rows.Select((m, i) => (m, i)).OrderByDescending(x => (x.m.Candidate?.Changed ?? x.m.Candidate?.Created) ?? DateTime.MinValue).ThenBy(x => x.i).Select(x => x.m).ToList();
            _list.ShowOlderNote = !OlderHidden;
            _list.SetRows(rows, r);
            ShowStatus(StatusText());
            ShowDetails();
        }

        string StatusText()
        {
            var r = _result;
            if (r == null) return _status.Text;
            string text;
            if (r.EverTmOff) text = "No EverTM matches: EverTM is off in this project's TMs (Enabled or Lookup unticked)";
            else if (r.Error != null) text = "EverTM lookup failed: " + r.Error;
            else if (r.Problem != null) text = r.Problem;
            else
            {
                int n = r.Matches?.Count ?? 0;
                text = VaultLibrary.Units(r.Units) + " · " + (n == 0 ? "no matches" : n == 1 ? "1 match" : n + " matches") + " · " + r.Milliseconds.ToString(CultureInfo.CurrentCulture) + " ms";
                if (r.Building != null) text = "EverTM is being rebuilt (" + r.Building + "); matches from the index before it · " + text;
            }
            if (r.StudioPending) text += " · searching Studio TMs\u2026";
            else if (r.Studio != null && r.Error == null)
            {
                var a = r.Studio;
                if (a.Tms == 0) text += " · no Studio TMs with Lookup ticked in this project";
                else
                {
                    int n = r.FromStudio?.Count ?? 0;
                    int both = r.AlsoIn?.Keys.Count(k => r.StudioOf(k) == null) ?? 0;   // EverTM's rows a Studio TM has too
                    int total = n + both;
                    string found = total == 0 ? "no matches"
                        : (total == 1 ? "1 match" : total + " matches")
                          + (both == 0 ? "" : both == total ? (total == 1 ? ", also in EverTM" : ", all also in EverTM") : ", " + both + " also in EverTM");
                    text += " · " + a.Tms + " Studio TM" + (a.Tms == 1 ? "" : "s") + ": " + found + " · " + a.Milliseconds.ToString(CultureInfo.CurrentCulture) + " ms";
                    if (a.LeftOut.Count > 0) text += " · left out: " + string.Join(", ", a.LeftOut);
                }
            }
            if (r.ProjectNote != null) text += " · " + r.ProjectNote;
            if (_removedNote != null) text = _removedNote + " · " + text;
            if (_note != null) text = _note + " · " + text;
            return text;
        }

        void ShowStatus(string text)
        {
            _status.Text = text ?? "";
        }

        // ---- older translations ----

        /// <summary>A match was selected (or a new result came): its details. Its older translations stay closed until
        /// asked for: usually only the newest is wanted (Michael, 22 Nov 2026).</summary>
        void MatchSelected()
        {
            _detailsOf = null;
            ShowDetails();
            var m = _list.Selected;
            if (m != null && ReferenceEquals(m, _olderFor)) return;
            ClearOlder();
        }

        /// <summary>
        /// "2 older" clicked (or the menu's "Show older translations"): opens that match's older translations under its
        /// details, looking up the first page; clicked again, closes them. Only then, and only for that match, are they
        /// looked up.
        /// </summary>
        public void ToggleOlder(VaultMatch m)
        {
            if (m == null || _result == null || (m.Candidate?.Older ?? 0) <= 0 || OlderHidden) return;
            if (ReferenceEquals(m, _olderFor)) { ClearOlder(); ShowDetails(); return; }
            ClearOlder();
            _olderFor = m;
            _olderTotal = m.Candidate.Older;
            _detailSplit.Panel2Collapsed = false;
            AskOlder(0);
        }

        static bool OlderHidden =>
            (Settings.SettingsService.Current.EverTmPaneHiddenDetails ?? new List<string>()).Contains("Older translations");

        void ClearOlder()
        {
            _olderFor = null;
            _olderTotal = _olderPages = 0;
            _olderLoading = false;
            _detailsOf = null;
            _older.SetRows(new List<VaultMatch>(), null);
            _olderMore.Visible = false;
            _detailSplit.Panel2Collapsed = true;
        }

        void AskOlder(int page)
        {
            _olderLoading = true;
            OlderTitle();
            OlderWanted?.Invoke(_olderFor, _result, page);
        }

        void MoreOlder()
        {
            if (_olderFor == null || _olderLoading) return;
            AskOlder(_olderPages);
        }

        /// <summary>
        /// A page of <paramref name="match"/>'s older translations, from the view part. Dropped if another match has been
        /// selected meanwhile. UI thread.
        /// </summary>
        public void ShowOlder(VaultMatch match, int page, List<VaultMatch> items, int total, string error)
        {
            if (match == null || !ReferenceEquals(match, _olderFor)) return;
            _olderLoading = false;
            if (error != null) { _olderTitle.Text = "Older translations: couldn't be read (" + error + ")"; return; }
            _olderTotal = total;
            if (page == 0) _older.SetRows(items ?? new List<VaultMatch>(), _result);
            else _older.AddRows(items);
            _olderPages = page + 1;
            if (total == 0 && page == 0) { _detailSplit.Panel2Collapsed = true; return; }
            OlderTitle();
        }

        void OlderTitle()
        {
            int shown = _older.Count;
            string count = _olderTotal.ToString("N0", CultureInfo.CurrentCulture);
            _olderTitle.Text = "Older translations (" + count + ")"
                + (_olderLoading ? " · loading…" : shown < _olderTotal ? " · " + shown.ToString("N0", CultureInfo.CurrentCulture) + " shown" : "")
                + (_olderLoading ? "" : " · double-click to insert");
            int more = Math.Min(VaultLibrary.TranslationsPageSize, _olderTotal - shown);
            _olderMore.Text = "Show " + more + " more";
            _olderMore.Visible = !_olderLoading && more > 0 && _olderPages > 0;
        }

        /// <summary>
        /// The selected match's details (Michael, 23 Nov 2026): first where it was found ("Found in", with labels in the
        /// row's colours: EverTM's, and Studio blue for each Studio TM), then the entry it shows: "EverTM entry" (its
        /// fields, where EverTM got it from, its dates) or, for a match only a Studio TM has, "Studio TM entry" (the
        /// TM's file, the unit's fields and dates); then what was done to it for your segment (numbers, formatting).
        /// </summary>
        void ShowDetails()
        {
            var m = _detailsOf ?? _list.Selected;
            _details.Clear();
            if (m == null || _result == null) return;
            var c = m.Candidate ?? new VaultCandidate();
            var bold = new Font(Font, FontStyle.Bold);
            var hidden = new HashSet<string>(Settings.SettingsService.Current.EverTmPaneHiddenDetails ?? new List<string>(), StringComparer.Ordinal);
            void Put(string text, Font font, Color fore, Color back)
            {
                _details.SelectionStart = _details.TextLength;
                _details.SelectionLength = 0;
                _details.SelectionFont = font;
                _details.SelectionColor = fore;
                _details.SelectionBackColor = back;
                _details.SelectedText = text;
            }
            void Line(string label, string value, int indent = 12)
            {
                if (hidden.Contains(label)) return;   // switched off in Options > Details shown
                _details.SelectionStart = _details.TextLength;
                _details.SelectionIndent = indent;
                Put(label + "  ", bold, Color.FromArgb(90, 90, 90), Color.White);
                Put(value + "\n", Font, Color.Black, Color.White);
            }
            void Heading(string text)
            {
                _details.SelectionStart = _details.TextLength;
                _details.SelectionIndent = 0;
                Put(text + "\n", bold, Color.FromArgb(60, 60, 60), Color.White);
            }

            var tm = _detailsOf != null ? null : _result.StudioOf(m);
            _details.SelectionIndent = 0;
            Put(_detailsOf != null
                ? "Older translation " + (_older.SelectedIndex + 1) + " of match " + (_list.SelectedIndex + 1) + "\n"
                : "Match " + (_list.SelectedIndex + 1) + ", " + m.Score + "%" + (c.Exact ? " (exact)" : "") + "\n", bold, Color.Black, Color.White);

            // Found in: EverTM and/or the Studio TMs, as labels in the row's colours.
            Put("Found in  ", bold, Color.FromArgb(90, 90, 90), Color.White);
            var studioNames = new List<string>();
            if (tm != null) studioNames.Add(tm.Name);
            if (_detailsOf == null && _result.AlsoIn != null && _result.AlsoIn.TryGetValue(m, out var also)) studioNames.AddRange(also);
            if (tm == null) { Put(" EverTM ", bold, Color.White, EverTmGreen); Put(" ", Font, Color.Black, Color.White); }
            foreach (var name in studioNames.Distinct()) { Put(" " + name + " ", bold, Color.White, EverTmMatchList.StudioBlue); Put(" ", Font, Color.Black, Color.White); }
            Put("\n", Font, Color.Black, Color.White);

            Heading(tm != null ? "Studio TM entry" : "EverTM entry");
            if (tm != null)
                Line(tm.Entry.EndsWith(".sdltm", StringComparison.OrdinalIgnoreCase) ? "File" : "Server",
                     tm.Entry + (tm.Penalty > 0 ? " · project penalty " + tm.Penalty + "%" : ""));
            var fields = c.Fields ?? new List<(string Field, string Value)>();
            foreach (var name in VaultFields.Order.Concat(fields.Select(f => f.Field).Where(f => !VaultFields.Order.Contains(f)).Distinct()))
            {
                var values = fields.Where(f => f.Field == name && !string.IsNullOrWhiteSpace(f.Value)).Select(f => f.Value.Trim()).Distinct().ToList();
                if (values.Count == 0) continue;
                if (name == VaultFields.Source) values = values.Select(CameFrom).ToList();
                string shown = string.Join("; ", values);
                if (values.Count >= VaultFields.MaxValuesPerField) shown += "  (" + values.Count + " shown; more may exist)";
                Line(name == VaultFields.Source ? CameFromLabel : name, shown);
            }
            if (fields.Count == 0 && !VaultFields.Order.All(hidden.Contains)) Line("Fields", "none stored for this translation unit");
            Line("Created", When(c.Created, c.CreatedBy));
            Line("Changed", When(c.Changed, c.ChangedBy));
            if (tm == null) Line("In EverTM since", c.Added == null ? "not known" : EverTmSummary.FormatDate(c.Added.Value));
            Line("Language variants", string.IsNullOrWhiteSpace(c.Variant) ? "not known" : c.Variant.Replace(">", " > "));

            // What was done to it for your segment.
            if (m.NumbersReplaced > 0)
                Line("Numbers", m.NumbersReplaced == 1 ? "1 number replaced by your segment's" : m.NumbersReplaced + " numbers replaced by your segment's", 0);
            if (m.Tags == VaultTagOutcome.Unmapped && _result.Editor != null && _result.Editor.HasTags)
                Line("Formatting", "this match's tags can't be placed with confidence: it is inserted without formatting", 0);
            if (m.Diff != null && m.Diff.Edits == null)
                Line("Differences", "too long to compare word by word", 0);
            _details.SelectionStart = 0;
            _details.ScrollToCaret();
            bold.Dispose();
        }

        static readonly Color EverTmGreen = Color.FromArgb(46, 140, 60);

        /// <summary>The details' name for EverTM's Source field: where EverTM got the entry (not a TM that has it now).</summary>
        const string CameFromLabel = "Came from";

        /// <summary>
        /// A Source value in words: "Studio: BRANTS (HELLO-003)" is a segment confirmed in that Studio project (learned),
        /// "Acme.sdltm" an imported Studio TM, "Acme.tmx" an imported TMX file; anything else (a Workbench TM's name:
        /// "PATENTS", "trash") as it is.
        /// </summary>
        public static string CameFrom(string value)
        {
            var v = (value ?? "").Trim();
            if (v.Equals("Studio", StringComparison.OrdinalIgnoreCase)) return "confirmed in a Studio project";
            if (v.StartsWith("Studio: ", StringComparison.OrdinalIgnoreCase)) return "confirmed in Studio project " + v.Substring(8).Trim();
            if (v.EndsWith(".sdltm", StringComparison.OrdinalIgnoreCase)) return "imported from Studio TM " + v;
            if (v.EndsWith(".tmx", StringComparison.OrdinalIgnoreCase)) return "imported from TMX file " + v;
            return v;
        }

        /// <summary>Every item the details area can show, in its order: what Options > Details shown lists.</summary>
        static readonly string[] DetailItems = VaultFields.Order.Select(f => f == VaultFields.Source ? CameFromLabel : f).Concat(new[]
            { "Created", "Changed", "In EverTM since", "Language variants", "Older translations", "Numbers", "Formatting", "Differences" }).ToArray();

        /// <summary>
        /// Options > Details shown: a tick per item of the details area, and "Show all". Stays open while ticking, so
        /// several can be changed in one go; each tick is saved and the details redrawn at once.
        /// </summary>
        ToolStripMenuItem DetailsMenu()
        {
            var menu = new ToolStripMenuItem("Details shown");
            var hidden = new HashSet<string>(Settings.SettingsService.Current.EverTmPaneHiddenDetails ?? new List<string>(), StringComparer.Ordinal);
            menu.DropDown.Closing += (s, e) => { if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true; };
            var showAll = new ToolStripMenuItem("Show all") { Enabled = hidden.Count > 0 };
            var items = new List<ToolStripMenuItem>();
            void Save()
            {
                var off = items.Where(i => !i.Checked).Select(i => i.Text).ToList();
                Settings.SettingsService.Update(x => x.EverTmPaneHiddenDetails = off.Count == 0 ? null : off);
                showAll.Enabled = off.Count > 0;
                ShowDetails();
                _list.ShowOlderNote = !OlderHidden;
                if (OlderHidden) ClearOlder();
            }
            showAll.Click += (s, e) => { foreach (var i in items) i.Checked = true; Save(); };
            menu.DropDownItems.Add(showAll);
            menu.DropDownItems.Add(new ToolStripSeparator());
            foreach (var name in DetailItems)
            {
                var item = new ToolStripMenuItem(name) { Checked = !hidden.Contains(name), CheckOnClick = true };
                item.Click += (s, e) => Save();
                items.Add(item);
                menu.DropDownItems.Add(item);
            }
            return menu;
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
    ///
    /// <para>The same list shows a match's older translations (<see cref="Others"/>): the target and the summary line
    /// only, as their source is the match's; no number, as Alt+Shift+1-9 and the digits insert matches. Up to a few
    /// hundred rows (pages of 50, shown on request); laid out once per page and per width.</para>
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
            /// <summary>The Studio TM's name, in a blue chip before the summary line; null for EverTM's matches.</summary>
            public string Badge;
            /// <summary>On EverTM's row a Studio TM has too: "also in Hydraulics_TM", in an outlined chip.</summary>
            public string Also;
            /// <summary>The chip's text as drawn: shortened to the pane's width (a TM's ".sdltm" kept).</summary>
            public string Chip;
            public bool TooLong;
            // Layout, for the current width.
            public int Top, Height;
            public List<Placed> SourceRuns = new List<Placed>(), TargetRuns = new List<Placed>();
            public int SummaryY;
            /// <summary>The thin line between source and target: across (stacked) or down (side by side).</summary>
            public Rectangle Divider;
        }

        readonly VScrollBar _scroll = new VScrollBar { Dock = DockStyle.Right, Visible = false };
        readonly ContextMenuStrip _menu = new ContextMenuStrip();
        readonly Font _normal, _strike, _under, _small, _bold, _tag, _smallBold;
        readonly Dictionary<(string, Style), int> _widths = new Dictionary<(string, Style), int>();
        List<Row> _rows = new List<Row>();
        EverTmPaneResult _result;
        int _selected = -1;
        int _laidOutWidth = -1;
        int _contentHeight;
        bool _stale;

        const int Pad = 5, Gap = 2;
        // The number and score column, measured from its widest contents ("20", "100%"), so it grows with Windows'
        // display scaling instead of the score badge running over the number.
        readonly int _numberWidth, _badgeWidth, Gutter;
        static readonly Color RemovedColor = Color.FromArgb(190, 30, 30), AddedColor = Color.FromArgb(0, 120, 40),
            AddedBack = Color.FromArgb(222, 244, 226), TagColor = Color.FromArgb(120, 120, 120), TagBack = Color.FromArgb(232, 232, 232),
            SummaryColor = Color.FromArgb(140, 140, 140), LinkColor = Color.FromArgb(30, 100, 180), SelectedBack = Color.FromArgb(220, 235, 252), Line = Color.FromArgb(232, 232, 232);

        /// <summary>Studio blue: a Studio TM's score, stripe and name label, here and in the details.</summary>
        internal static readonly Color StudioBlue = Color.FromArgb(25, 100, 175);

        public event EventHandler SelectionChanged;
        public event Action<VaultMatch> InsertRequested;

        /// <summary>A match's older translations: target and summary line only, nothing selected at first.</summary>
        public readonly bool Others;

        /// <summary>"2 older" on a row was clicked, or the menu's "Show older translations" chosen.</summary>
        public event Action<VaultMatch> OlderClicked;

        /// <summary>"Remove from EverTM" chosen, or Delete pressed, on the selected row.</summary>
        public event Action<VaultMatch> RemoveRequested;

        /// <summary>"Edit in EverTM..." chosen, or F2 pressed, on the selected row.</summary>
        public event Action<VaultMatch> EditRequested;

        /// <summary>Whether rows say "2 older" (Options > Details shown > Older translations).</summary>
        public bool ShowOlderNote
        {
            get => _showOlderNote;
            set { if (_showOlderNote != value) { _showOlderNote = value; _laidOutWidth = -1; Invalidate(); } }
        }
        bool _showOlderNote = true;

        public EverTmMatchList(bool others = false)
        {
            Others = others;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            BackColor = Color.White;
            _normal = new Font("Segoe UI", 9.5f);
            _strike = new Font(_normal, FontStyle.Strikeout);
            _under = new Font(_normal, FontStyle.Underline);
            _small = new Font("Segoe UI", 8f);
            _bold = new Font("Segoe UI", 9f, FontStyle.Bold);
            _tag = new Font("Segoe UI", 7.5f);
            _smallBold = new Font("Segoe UI", 8f, FontStyle.Bold);
            _numberWidth = TextRenderer.MeasureText("20", _bold, Size.Empty, TextFormatFlags.NoPadding).Width;
            _badgeWidth = TextRenderer.MeasureText("100%", _bold, Size.Empty, TextFormatFlags.NoPadding).Width + 8;
            Gutter = others ? 8 : 6 + _numberWidth + 4 + _badgeWidth + 8;
            Controls.Add(_scroll);
            _scroll.Scroll += (s, e) => Invalidate();
            _scroll.ValueChanged += (s, e) => Invalidate();

            var insert = new ToolStripMenuItem(others ? "Insert this translation" : "Insert this match", null, (s, e) => RequestInsert());
            var copy = new ToolStripMenuItem("Copy the target text", null, (s, e) => CopyTarget());
            var older = new ToolStripMenuItem("Show older translations", null, (s, e) => { if (Selected != null) OlderClicked?.Invoke(Selected); });
            var remove = new ToolStripMenuItem("Remove from EverTM", null, (s, e) => { if (Selected != null) RemoveRequested?.Invoke(Selected); }) { ShortcutKeyDisplayString = "Del" };
            var edit = new ToolStripMenuItem("Edit in EverTM...", null, (s, e) => { if (Selected != null) EditRequested?.Invoke(Selected); }) { ShortcutKeyDisplayString = "F2" };
            _menu.Items.Add(insert);
            _menu.Items.Add(copy);
            if (!others) _menu.Items.Add(older);
            _menu.Items.Add(new ToolStripSeparator());
            // A Studio TM's match is changed in Studio, not here.
            var studio = new ToolStripMenuItem("") { Enabled = false, Visible = false };
            _menu.Items.Add(edit);
            _menu.Items.Add(remove);
            _menu.Items.Add(studio);
            _menu.Opening += (s, e) =>
            {
                insert.Enabled = !_stale && Selected != null; copy.Enabled = Selected != null; remove.Enabled = Selected != null; edit.Enabled = Selected != null;
                older.Visible = _showOlderNote && (Selected?.Candidate?.Older ?? 0) > 0;
                var tm = _result?.StudioOf(Selected);
                edit.Visible = remove.Visible = tm == null;
                studio.Visible = tm != null;
                if (tm != null) studio.Text = "From the Studio TM " + tm.Name + ": edit or delete it in Studio's Translation Results";
            };
            ContextMenuStrip = _menu;
        }

        /// <summary>Source above target (true), or side by side as in Translation Results (false, the default).</summary>
        public bool Stacked
        {
            get => _stacked;
            set { if (_stacked != value) { _stacked = value; _laidOutWidth = -1; if (_rows.Count > 0) LayoutRows(); Invalidate(); } }
        }
        bool _stacked;

        public bool Stale
        {
            get => _stale;
            set { if (_stale != value) { _stale = value; Invalidate(); } }
        }

        public VaultMatch Selected => _selected >= 0 && _selected < _rows.Count ? _rows[_selected].Match : null;
        public int SelectedIndex => _selected;
        public VaultMatch RowAt(int i) => i >= 0 && i < _rows.Count ? _rows[i].Match : null;

        /// <summary>Selects this match if it is a row (by reference).</summary>
        public void SelectMatch(VaultMatch m)
        {
            int i = _rows.FindIndex(r => ReferenceEquals(r.Match, m));
            if (i >= 0) Select(i);
        }

        public void SetRows(List<VaultMatch> matches, EverTmPaneResult result)
        {
            _result = result;
            _rows = matches.Select(m => MakeRow(m, result)).ToList();
            _selected = _rows.Count > 0 && !Others ? 0 : -1;
            _stale = false;
            _laidOutWidth = -1;
            _scroll.Value = 0;
            LayoutRows();
            Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>The next page of older translations, below those shown; the selection and scroll position stay.</summary>
        public void AddRows(List<VaultMatch> more)
        {
            if (more == null || more.Count == 0) return;
            _rows.AddRange(more.Select(m => MakeRow(m, _result)));
            _laidOutWidth = -1;
            LayoutRows();
            Invalidate();
        }

        public int Count => _rows.Count;

        // ---- rows ----

        Row MakeRow(VaultMatch m, EverTmPaneResult r)
        {
            var row = new Row { Match = m, Source = Others ? new List<Run>() : SourceRuns(m, r), Target = PlainRuns(m.Target, Style.Normal) };
            row.TooLong = m.Diff != null && m.Diff.Edits == null;
            if (!Others) row.Badge = r?.StudioOf(m)?.Name;
            if (!Others && r?.AlsoIn != null && r.AlsoIn.TryGetValue(m, out var also) && also.Count > 0)
            {
                if (row.Badge != null) row.Badge += " +" + also.Count;   // other Studio TMs have it too
                else row.Also = also.Count == 1 ? "also in " + also[0] + " ·" : "also in " + also.Count + " Studio TMs ·";   // the names: "Found in", in the details
            }
            if (!Others && m.Candidate?.Older > 0) row.Older = (m.Candidate.Older == 1 ? "1 older" : m.Candidate.Older.ToString("N0", CultureInfo.CurrentCulture) + " older") + " ▸";
            return row;
        }

        /// <summary>
        /// The stored source with the differences from the document marked, from the alignment EverTM computed for the
        /// score (the one Translation Results draws), grouped as Translation Results groups them (Michael, 22 Nov
        /// 2026): each stretch that differs is shown as the stored words struck through in red, all together, then
        /// your segment's words for it, green and underlined, all together. Spaces between changed words don't end a
        /// stretch; an unchanged word does. A number EverTM replaced counts as unchanged.
        /// </summary>
        static List<Run> SourceRuns(VaultMatch m, EverTmPaneResult r)
        {
            var memory = m.SourceTokens;
            var doc = m.DocumentTokens;
            if (m.Diff?.Edits == null || memory == null || doc == null) return PlainRuns(m.Source, Style.Normal);
            var docPieces = r?.Editor?.Pieces;
            int n = memory.Count;
            var removed = new bool[n];
            var addedAt = new Dictionary<int, List<int>>();     // memory position -> document tokens that belong there
            void Add(int at, int a)
            {
                if (a < 0 || a >= doc.Count) return;
                if (!addedAt.TryGetValue(at, out var list)) addedAt[at] = list = new List<int>();
                list.Add(a);
            }
            foreach (var e in m.Diff.Edits)
            {
                switch (e.Op)
                {
                    case VaultEditOp.Insert: if (e.B >= 0 && e.B < n) removed[e.B] = true; break;
                    case VaultEditOp.Delete: Add(Math.Max(0, Math.Min(n, e.B)), e.A); break;
                    case VaultEditOp.Change:
                        if (e.Replaced || e.B < 0 || e.B >= n) break;
                        removed[e.B] = true;
                        Add(e.B + 1, e.A);
                        break;
                }
            }

            bool Changed(int k) => (k < n && removed[k]) || addedAt.ContainsKey(k);
            var runs = new List<Run>();
            int b = 0;
            while (b <= n)
            {
                if (!Changed(b))
                {
                    if (b == n) break;
                    runs.Add(TokenRun(memory, b, m.Source, Style.Normal));
                    b++;
                    continue;
                }
                // One stretch: from here to the next unchanged word.
                int memFirst = -1, memLast = -1, docMin = int.MaxValue, docMax = -1, j = b;
                while (j <= n)
                {
                    if (addedAt.TryGetValue(j, out var list))
                    {
                        foreach (var a in list) { docMin = Math.Min(docMin, a); docMax = Math.Max(docMax, a); }
                        addedAt.Remove(j);   // taken: the unchanged word at j is drawn next, as it is
                    }
                    if (j == n) { j = n + 1; break; }
                    if (removed[j]) { if (memFirst < 0) memFirst = j; memLast = j; j++; continue; }
                    if (memory[j].Kind == VaultTokenKind.Space)
                    {
                        int k = j;
                        while (k < n && memory[k].Kind == VaultTokenKind.Space) k++;
                        if (Changed(k) && k > j) { j = k; continue; }   // only spaces before more change: same stretch
                    }
                    break;
                }
                if (memFirst >= 0)
                    for (int k = memFirst; k <= memLast; k++)
                        runs.Add(TokenRun(memory, k, m.Source, Style.Removed));
                // Your words, without spaces at either end: the unchanged text around them has its own.
                while (docMax >= docMin && docMax >= 0 && doc[docMax].Kind == VaultTokenKind.Space) docMax--;
                while (docMin <= docMax && doc[docMin].Kind == VaultTokenKind.Space) docMin++;
                if (docMax >= 0 && docMin <= docMax)
                    for (int a = docMin; a <= docMax; a++)
                        runs.Add(TokenRun(doc, a, docPieces, Style.Added));
                // The spaces skipped after the last removed word, before the unchanged word, are drawn as they are.
                int resume = memLast >= 0 ? memLast + 1 : b;
                for (int k = resume; k < Math.Min(j, n); k++)
                    if (!removed[k]) runs.Add(TokenRun(memory, k, m.Source, Style.Normal));
                b = j;   // the unchanged word that ended the stretch (its additions are taken), or past the end
            }
            return runs;
        }

        static Run TokenRun(List<VaultToken> tokens, int i, List<VaultPiece> pieces, Style style)
        {
            if (i < 0 || i >= tokens.Count) return new Run { Text = "", Style = style };
            var t = tokens[i];
            if (t.Kind == VaultTokenKind.Tag) return new Run { Text = TagMark(pieces, t.Piece), Style = Style.Tag };
            if (t.Kind == VaultTokenKind.Space) return new Run { Text = " ", Style = style == Style.Added || style == Style.Removed ? style : Style.Normal, Space = true };
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
            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            // Measured with a mark after it and the mark taken off again, so a trailing space counts in full.
            if (text.EndsWith(" ", StringComparison.Ordinal))
                w = TextRenderer.MeasureText(text + "x", font, Size.Empty, flags).Width - TextRenderer.MeasureText("x", font, Size.Empty, flags).Width;
            else w = TextRenderer.MeasureText(text, font, Size.Empty, flags).Width + (style == Style.Tag ? 6 : 0);
            if (_widths.Count > 20000) _widths.Clear();   // bounded: one segment's words, many segments
            _widths[key] = w;
            return w;
        }

        Font FontOf(Style s) => s == Style.Removed ? _strike : s == Style.Added ? _under : s == Style.Tag ? _tag : _normal;

        /// <summary>
        /// Places the runs word by word, wrapping at the width; returns the height used. A space, and punctuation
        /// straight after a word, join the run before them when it has the same look, and are measured and drawn
        /// with it: measured one by one, each piece gained a little width and the words stood too far apart.
        /// </summary>
        int Flow(List<Run> runs, int x0, int y0, int width, List<Placed> placed)
        {
            placed.Clear();
            int line = _normal.Height + 1;
            int x = x0, y = y0;
            foreach (var r in runs)
            {
                if (r.Space && x == x0) continue;                       // no space at the start of a line
                if (placed.Count > 0)
                {
                    var last = placed[placed.Count - 1];
                    bool sameLine = last.Box.Y == y, sameLook = last.Run.Style == r.Style && r.Style != Style.Tag;
                    bool glued = !last.Run.Text.EndsWith(" ", StringComparison.Ordinal);   // no space in between
                    // Everything between two spaces that looks the same stays one piece ("(P", "drops.", "formule:").
                    // A changed stretch (struck through, or underlined) stays one piece across its spaces, so its line runs
                    // unbroken, as in Translation Results; it breaks only where the line wraps.
                    bool stretch = sameLook && (r.Style == Style.Added || r.Style == Style.Removed);
                    if (sameLine && (r.Space ? last.Run.Style != Style.Tag : sameLook && (glued || stretch) && r.Text.Length > 0)
                        && (r.Space || glued || last.Box.X + Measure(last.Run.Text + r.Text, r.Style) <= x0 + width))
                    {
                        var joined = last.Run;
                        joined.Text += r.Space ? " " : r.Text;
                        int jw = Measure(joined.Text, joined.Style);
                        placed[placed.Count - 1] = new Placed { Run = joined, Box = new Rectangle(last.Box.X, y, jw, line) };
                        x = last.Box.X + jw;
                        continue;
                    }
                }
                if (r.Space) { x += Measure(" ", Style.Normal); continue; }   // after a tag: just room
                int w = Measure(r.Text, r.Style);
                if (x > x0 && x + w > x0 + width) { x = x0; y += line; }
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
                    if (Others)
                    {
                        h += Flow(row.Target, Gutter, y + h, width, row.TargetRuns) + Gap;
                        row.Divider = Rectangle.Empty;
                    }
                    else if (_stacked)
                    {
                        h += Flow(row.Source, Gutter, y + h, width, row.SourceRuns) + Gap;
                        row.Divider = new Rectangle(Gutter, y + h, width, 1);
                        h += Gap + 1;
                        h += Flow(row.Target, Gutter, y + h, width, row.TargetRuns) + Gap;
                    }
                    else
                    {
                        // Two columns, as Translation Results has them, with a thin line down between.
                        const int between = 13;
                        int column = Math.Max(40, (width - between) / 2);
                        int hs = Flow(row.Source, Gutter, y + h, column, row.SourceRuns);
                        int ht = Flow(row.Target, Gutter + column + between, y + h, column, row.TargetRuns);
                        row.Divider = new Rectangle(Gutter + column + between / 2, y + h, 1, Math.Max(hs, ht));
                        h += Math.Max(hs, ht) + Gap;
                    }
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

        bool HasOlderNote(Row row) => row.Older != null && _showOlderNote;

        int OlderWidth(Row row) => !HasOlderNote(row) ? 0 : TextRenderer.MeasureText(row.Older, _small, Size.Empty, TextFormatFlags.NoPadding).Width + 10;

        /// <summary>Where a row's "2 older" link is, in content coordinates; empty when it has none.</summary>
        Rectangle OlderBox(Row row)
        {
            if (!HasOlderNote(row)) return Rectangle.Empty;
            int right = ClientSize.Width - (_scroll.Visible ? _scroll.Width : 0);
            int w = OlderWidth(row);
            return new Rectangle(right - w, row.SummaryY - 2, w, _small.Height + 4);
        }

        /// <summary>The faint line, fitted once per layout (not per paint): Client · Project · date, and the variant.</summary>
        string SummaryLine(Row row, int width)
        {
            var full = row.Badge ?? row.Also;
            row.Chip = full == null ? null : FitChip(full, Math.Max(60, width * 3 / 5), ChipFont(row));
            var parts = EverTmSummary.Make(row.Match.Candidate, _result?.SourceLang, _result?.SourceCulture, _result?.TargetCulture);
            if (row.TooLong) parts.Tail += " · too long to compare word by word";
            return EverTmSummary.Fit(parts, width - OlderWidth(row) - BadgeWidth(row), s => TextRenderer.MeasureText(s, _small, Size.Empty, TextFormatFlags.NoPadding).Width);
        }

        /// <summary>The chip before the summary line (a Studio TM's name, or "also in …"), with the gap after it; 0 when none.</summary>
        int BadgeWidth(Row row)
        {
            var text = ChipText(row);
            if (text == null) return 0;
            return row.Badge != null ? ChipTextWidth(text, _smallBold) + 10 + 6 : ChipTextWidth(text, _small) + 4;
        }

        /// <summary>A Studio TM's name label is bold; "also in …" on EverTM's row is the summary line's own weight.</summary>
        Font ChipFont(Row row) => row.Badge != null ? _smallBold : _small;

        static int ChipTextWidth(string text, Font font) => TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;

        static string ChipText(Row row) => row.Chip;

        /// <summary>
        /// A chip's text shortened to <paramref name="max"/> pixels: "BRANTS (HELLO-0….sdltm", the TM's ".sdltm" (and
        /// a "+1" after it) kept, so it still reads as a Studio TM; other text cut at the end.
        /// </summary>
        static string FitChip(string text, int max, Font font)
        {
            if (ChipTextWidth(text, font) + 10 <= max) return text;
            int ext = text.LastIndexOf(".sdltm", StringComparison.OrdinalIgnoreCase);
            string tail = ext > 0 ? text.Substring(ext) : "";
            string stem = ext > 0 ? text.Substring(0, ext) : text;
            while (stem.Length > 1 && ChipTextWidth(stem + "\u2026" + tail, font) + 10 > max) stem = stem.Substring(0, stem.Length - 1);
            return stem.TrimEnd() + "\u2026" + tail;
        }

        /// <summary>
        /// A Studio TM's row stands out at a glance (Michael, 23 Nov 2026: "Make the Studio TM matches stand out more"):
        /// a blue stripe down its left edge, its score in Studio blue (EverTM's are green and orange), and the TM's name
        /// in a filled blue chip. EverTM's row that a Studio TM has too keeps EverTM's look, with "also in …" in plain
        /// blue text at the summary line's weight: EverTM's match is the main thing there (Michael: the outlined label
        /// "draws the eye there too much").
        /// </summary>
        void PaintChip(Graphics g, Row row, int sy)
        {
            var text = ChipText(row);
            if (text == null) return;
            int w = BadgeWidth(row) - 6;
            var box = new Rectangle(Gutter, sy - 1, w, _small.Height + 2);
            var blue = _stale ? Color.Silver : StudioBlue;
            if (row.Badge != null)
            {
                using (var b = new SolidBrush(blue)) g.FillRectangle(b, box);
                TextRenderer.DrawText(g, text, _smallBold, box, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
            else TextRenderer.DrawText(g, text, _small, new Point(Gutter, sy), blue, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
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

                if (row.Badge != null)
                    using (var b = new SolidBrush(_stale ? Color.Silver : StudioBlue)) g.FillRectangle(b, 0, top, 4, row.Height - 1);
                if (!Others) PaintNumber(g, i, row.Match.Score, top, studio: row.Badge != null);

                var divider = row.Divider;
                if (!divider.IsEmpty)
                {
                    divider.Offset(0, -offset);
                    using (var b = new SolidBrush(Color.FromArgb(215, 215, 215))) g.FillRectangle(b, divider);
                }
                DrawRuns(g, row.SourceRuns, -offset);
                DrawRuns(g, row.TargetRuns, -offset);

                // The summary line, faint; "N older" at its right.
                int sy = row.SummaryY - offset;
                if (HasOlderNote(row))
                {
                    // A link: it opens this match's older translations.
                    int olderWidth = OlderWidth(row);
                    using (var link = new Font(_small, FontStyle.Underline))
                        TextRenderer.DrawText(g, row.Older, link, new Point(right - olderWidth, sy), _stale ? Color.Silver : LinkColor, TextFormatFlags.NoPadding);
                }
                PaintChip(g, row, sy);
                TextRenderer.DrawText(g, row.Summary ?? "", _small, new Point(Gutter + BadgeWidth(row), sy), SummaryColor, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
        }

        /// <summary>A match row's number and score badge: green or orange for EverTM's matches, Studio blue for a Studio TM's.</summary>
        void PaintNumber(Graphics g, int i, int score, int top, bool studio = false)
        {
            Color badge = score >= 100 ? Color.FromArgb(46, 140, 60) : score >= 85 ? Color.FromArgb(200, 130, 0) : Color.FromArgb(150, 110, 60);
            if (_stale) badge = Color.Silver;
            TextRenderer.DrawText(g, (i + 1).ToString(CultureInfo.InvariantCulture), _bold, new Point(6, top + Pad), _stale ? Color.Silver : Color.FromArgb(80, 80, 80), TextFormatFlags.NoPadding);
            var scoreBox = new Rectangle(6 + _numberWidth + 4, top + Pad, _badgeWidth, _normal.Height);
            if (studio && !_stale) badge = StudioBlue;
            using (var b = new SolidBrush(badge)) g.FillRectangle(b, scoreBox);
            TextRenderer.DrawText(g, score + "%", _bold, scoreBox, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
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
                // The line under or through a word stops at the word: the space after it is drawn plain.
                string text = p.Run.Text;
                if ((style == Style.Added || style == Style.Removed) && text.EndsWith(" ", StringComparison.Ordinal))
                {
                    text = text.TrimEnd();
                    box.Width = Measure(text, style);
                }
                if (style == Style.Added && !_stale) using (var b = new SolidBrush(AddedBack)) g.FillRectangle(b, box);
                if (style == Style.Tag)
                {
                    var pill = new Rectangle(box.X + 1, box.Y + 3, box.Width - 2, box.Height - 6);
                    using (var b = new SolidBrush(TagBack)) g.FillRectangle(b, pill);
                    TextRenderer.DrawText(g, p.Run.Text, _tag, pill, fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    continue;
                }
                TextRenderer.DrawText(g, text, FontOf(style), box.Location, fore, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
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
            if (i >= 0 && e.Button == MouseButtons.Left && !_stale && OnOlderLink(i, e.Location)) OlderClicked?.Invoke(_rows[i].Match);
        }

        bool OnOlderLink(int i, Point p)
        {
            var box = OlderBox(_rows[i]);
            if (box.IsEmpty) return false;
            box.Offset(0, -(_scroll.Visible ? _scroll.Value : 0));
            return box.Contains(p);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = Others ? -1 : RowAtPoint(e.Location);
            Cursor = i >= 0 && OnOlderLink(i, e.Location) ? Cursors.Hand : Cursors.Default;
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
                case Keys.Up: case Keys.Down: case Keys.Home: case Keys.End: case Keys.PageUp: case Keys.PageDown: case Keys.Enter: case Keys.Delete: case Keys.F2: return true;
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
                case Keys.Delete: if (Selected != null) RemoveRequested?.Invoke(Selected); e.Handled = true; e.SuppressKeyPress = true; break;
                case Keys.F2: if (Selected != null) EditRequested?.Invoke(Selected); e.Handled = true; e.SuppressKeyPress = true; break;
                default:
                    if (!Others && (e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D9 || e.KeyCode >= Keys.NumPad1 && e.KeyCode <= Keys.NumPad9))
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
                _normal.Dispose(); _strike.Dispose(); _under.Dispose(); _small.Dispose(); _bold.Dispose(); _tag.Dispose(); _smallBold.Dispose();
                _menu.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
