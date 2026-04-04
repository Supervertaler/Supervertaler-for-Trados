using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// memoQ-style term entry editor with source/target fields and synonym lists.
    /// Supports add and edit modes. Synonyms can be added, removed, reordered,
    /// and toggled as forbidden. The top item in each list is the primary term.
    /// </summary>
    public class TermEntryEditorDialog : Form
    {
        private readonly string _dbPath;
        private TermbaseInfo _termbase;
        private long _termId;
        private bool _isInverted; // true when project direction is opposite to termbase direction

        // Main fields
        private TextBox _txtSource;
        private TextBox _txtTarget;
        private TextBox _txtSourceAbbr;
        private TextBox _txtTargetAbbr;
        private TextBox _txtDefinition;
        private TextBox _txtDomain;
        private TextBox _txtNotes;
        private TextBox _txtUrl;
        private TextBox _txtClient;
        private const int CollapsedHeight = 60;
        private const int ExpandedHeight = 120;
        private bool _definitionExpanded;
        private bool _notesExpanded;
        private CheckBox _chkNonTranslatable;

        // Synonym lists
        private ListBox _lstSourceSynonyms;
        private ListBox _lstTargetSynonyms;
        private TextBox _txtNewSourceSyn;
        private TextBox _txtNewTargetSyn;

        // Internal synonym data (parallel to ListBox items)
        private List<SynonymEntry> _sourceSyns = new List<SynonymEntry>();
        private List<SynonymEntry> _targetSyns = new List<SynonymEntry>();

        // Layout
        private Panel _contentPanel;
        private ToolTip _toolTip;

        // Multi-entry support (for editing entries from multiple termbases)
        private readonly List<EntryData> _allEntryData = new List<EntryData>();
        private int _activeEntryIndex;
        private ComboBox _cboTermbase;

        private class EntryData
        {
            public TermEntry Entry;
            public TermbaseInfo Termbase;
            public string Source, Target, Definition, Domain, Notes, Url, Client;
            public string SourceAbbr, TargetAbbr;
            public bool IsNonTranslatable;
            public bool IsInverted;
            public List<SynonymEntry> SourceSyns = new List<SynonymEntry>();
            public List<SynonymEntry> TargetSyns = new List<SynonymEntry>();
            public bool SynonymsLoaded;
        }

        // Output properties
        public string SourceTerm => _txtSource.Text.Trim();
        public string TargetTerm => _txtTarget.Text.Trim();
        public string SourceAbbreviation => _txtSourceAbbr.Text.Trim();
        public string TargetAbbreviation => _txtTargetAbbr.Text.Trim();
        public string Definition => _txtDefinition.Text.Trim();
        public string Domain => _txtDomain.Text.Trim();
        public string Notes => _txtNotes.Text.Trim();
        public string Url => _txtUrl.Text.Trim();
        public string Client => _txtClient.Text.Trim();
        public bool IsNonTranslatable => _chkNonTranslatable.Checked;
        public long TermId => _termId;
        public bool IsEditMode => _termId > 0;
        public List<SynonymEntry> SourceSynonymsList => _sourceSyns;
        public List<SynonymEntry> TargetSynonymsList => _targetSyns;

        /// <summary>
        /// Edit mode — opens an existing term with its synonyms.
        /// </summary>
        public TermEntryEditorDialog(TermEntry entry, string dbPath, TermbaseInfo termbase,
            string projectSourceLang = null)
        {
            _dbPath = dbPath;
            _termbase = termbase;
            _termId = entry?.Id ?? -1;
            _isInverted = IsDirectionInverted(projectSourceLang, termbase?.SourceLang);

            BuildUI(termbase);
            PopulateFromEntry(entry);
            LoadSynonymsFromDb();
        }

        /// <summary>
        /// Add mode — creates a new term entry.
        /// </summary>
        public TermEntryEditorDialog(string sourceTerm, string targetTerm,
            string dbPath, TermbaseInfo termbase, string projectSourceLang = null)
        {
            _dbPath = dbPath;
            _termbase = termbase;
            _termId = -1;
            _isInverted = IsDirectionInverted(projectSourceLang, termbase?.SourceLang);

            BuildUI(termbase);
            _txtSource.Text = sourceTerm ?? "";
            _txtTarget.Text = targetTerm ?? "";
        }

        private static bool IsDirectionInverted(string projectSourceLang, string termbaseSourceLang)
        {
            if (string.IsNullOrEmpty(projectSourceLang) || string.IsNullOrEmpty(termbaseSourceLang))
                return false;

            // Normalize both to shortened form so that "English (United States)" and
            // "English (US)" compare correctly — Trados returns full names, the DB may
            // store either form.
            var projNorm = LanguageUtils.ShortenLanguageName(projectSourceLang);
            var tbNorm = LanguageUtils.ShortenLanguageName(termbaseSourceLang);

            return !projNorm.StartsWith(tbNorm, StringComparison.OrdinalIgnoreCase)
                && !tbNorm.StartsWith(projNorm, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Multi-entry edit mode — opens with a termbase switcher for entries from multiple termbases.
        /// </summary>
        public TermEntryEditorDialog(List<KeyValuePair<TermEntry, TermbaseInfo>> entries, string dbPath,
            string projectSourceLang = null)
        {
            _dbPath = dbPath;

            foreach (var kv in entries)
            {
                var entry = kv.Key;
                var inverted = IsDirectionInverted(projectSourceLang, kv.Value?.SourceLang);
                _allEntryData.Add(new EntryData
                {
                    Entry = entry,
                    Termbase = kv.Value,
                    Source = entry.SourceTerm ?? "",
                    Target = entry.TargetTerm ?? "",
                    SourceAbbr = entry.SourceAbbreviation ?? "",
                    TargetAbbr = entry.TargetAbbreviation ?? "",
                    Definition = entry.Definition ?? "",
                    Domain = entry.Domain ?? "",
                    Notes = entry.Notes ?? "",
                    Url = entry.Url ?? "",
                    Client = entry.Client ?? "",
                    IsNonTranslatable = entry.IsNonTranslatable,
                    IsInverted = inverted
                });
            }

            _activeEntryIndex = 0;
            var primary = _allEntryData[0];
            _termbase = primary.Termbase;
            _termId = primary.Entry.Id;
            _isInverted = primary.IsInverted;

            BuildUI(primary.Termbase);
            PopulateFromEntry(primary.Entry);
            LoadSynonymsFromDb();

            // Store loaded synonyms back into the entry data
            primary.SourceSyns = new List<SynonymEntry>(_sourceSyns);
            primary.TargetSyns = new List<SynonymEntry>(_targetSyns);
            primary.SynonymsLoaded = true;
        }

        private void BuildUI(TermbaseInfo termbase)
        {
            Text = IsEditMode ? $"Edit term entry (ID {_termId})" : "Add term entry";
            if (termbase != null)
                Text += $" \u2014 {termbase.Name}";

            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            HelpButton = true;
            HelpButtonClicked += OnHelpButtonClicked;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(580, 770);
            MinimumSize = new Size(500, 644);
            var formBg = Color.FromArgb(243, 243, 243);
            BackColor = formBg;

            var labelColor = Color.FromArgb(80, 80, 80);
            var inputBg = Color.White;

            _toolTip = new ToolTip { InitialDelay = 400, ShowAlways = true };

            // ── Bottom bar (Dock=Bottom) ────────────────────────────────
            var bottomPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 48,
                Width = ClientSize.Width,
                BackColor = formBg
            };
            bottomPanel.Controls.Add(new Label
            {
                Dock = DockStyle.Top,
                Height = 1,
                BorderStyle = BorderStyle.Fixed3D
            });

            if (IsEditMode)
            {
                var btnDelete = new Button
                {
                    Text = "Delete",
                    Width = 75,
                    FlatStyle = FlatStyle.System,
                    ForeColor = Color.FromArgb(180, 60, 60),
                    Location = new Point(16, 14)
                };
                btnDelete.Click += OnDeleteClick;
                bottomPanel.Controls.Add(btnDelete);
            }

            var btnCancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Width = 75,
                FlatStyle = FlatStyle.System,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(bottomPanel.Width - 75 - 16, 14)
            };
            bottomPanel.Controls.Add(btnCancel);

            var btnSave = new Button
            {
                Text = IsEditMode ? "Save" : "Add",
                DialogResult = DialogResult.None,
                Width = 75,
                FlatStyle = FlatStyle.System,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(btnCancel.Left - 75 - 8, 14)
            };
            btnSave.Click += OnSaveClick;
            bottomPanel.Controls.Add(btnSave);

            AcceptButton = btnSave;
            CancelButton = btnCancel;

            // ── Content panel (Dock=Fill, hosts all controls above bottom bar) ──
            // Set explicit Size so anchored children get correct distances before docking.
            _contentPanel = new Panel
            {
                BackColor = formBg,
                Size = new Size(ClientSize.Width, ClientSize.Height - 48)
            };

            int colWidth = (_contentPanel.Width - 48) / 2;
            int leftX = 16;
            int rightX = leftX + colWidth + 16;
            int y = 14;

            // === Termbase selector (multi-entry mode only) ===
            if (_allEntryData.Count > 1)
            {
                _contentPanel.Controls.Add(MakeLabel("Editing in:", leftX, y, labelColor));
                y += 18;

                _cboTermbase = new ComboBox
                {
                    Location = new Point(leftX, y),
                    Width = _contentPanel.Width - 32,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };

                foreach (var ed in _allEntryData)
                {
                    var tbName = ed.Termbase?.Name ?? "Unknown";
                    _cboTermbase.Items.Add($"{tbName}: {ed.Source} \u2192 {ed.Target}");
                }
                _cboTermbase.SelectedIndex = 0;
                _cboTermbase.SelectedIndexChanged += OnTermbaseSwitched;
                _contentPanel.Controls.Add(_cboTermbase);
                y += 30;
            }

            // === Source / Target terms (use actual language names when available) ===
            // When the project direction is inverted relative to the termbase, show labels in
            // project order (project source on the left, project target on the right).
            var srcLangLabel = _isInverted
                ? (!string.IsNullOrEmpty(termbase?.TargetLang) ? $"{LanguageUtils.ShortenLanguageName(termbase.TargetLang)}:" : "Source term:")
                : (!string.IsNullOrEmpty(termbase?.SourceLang) ? $"{LanguageUtils.ShortenLanguageName(termbase.SourceLang)}:" : "Source term:");
            var tgtLangLabel = _isInverted
                ? (!string.IsNullOrEmpty(termbase?.SourceLang) ? $"{LanguageUtils.ShortenLanguageName(termbase.SourceLang)}:" : "Target term:")
                : (!string.IsNullOrEmpty(termbase?.TargetLang) ? $"{LanguageUtils.ShortenLanguageName(termbase.TargetLang)}:" : "Target term:");
            _contentPanel.Controls.Add(MakeLabel(srcLangLabel, leftX, y, labelColor));
            _contentPanel.Controls.Add(MakeLabel(tgtLangLabel, rightX, y, labelColor));
            y += 18;

            _txtSource = new TextBox
            {
                Location = new Point(leftX, y),
                Width = colWidth,
                BackColor = inputBg,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };
            _contentPanel.Controls.Add(_txtSource);

            // Reverse button: swaps source ↔ target head terms
            var btnReverse = new Button
            {
                Text = "\u21C4",
                Location = new Point(leftX + colWidth + 1, y - 1),
                Size = new Size(14, 24),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f),
                Cursor = Cursors.Hand,
                TabStop = false
            };
            btnReverse.FlatAppearance.BorderSize = 0;
            var reverseTip = new ToolTip();
            reverseTip.SetToolTip(btnReverse, "Swap source and target terms");
            btnReverse.Click += (s, ev) =>
            {
                var tmp = _txtSource.Text;
                _txtSource.Text = _txtTarget.Text;
                _txtTarget.Text = tmp;
            };
            _contentPanel.Controls.Add(btnReverse);

            _txtTarget = new TextBox
            {
                Location = new Point(rightX, y),
                Width = colWidth,
                BackColor = inputBg,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };
            _contentPanel.Controls.Add(_txtTarget);
            y += 30;

            // === Abbreviation fields ===
            _contentPanel.Controls.Add(MakeLabel("Abbreviation:", leftX, y, labelColor));
            _contentPanel.Controls.Add(MakeLabel("Abbreviation:", rightX, y, labelColor));
            y += 18;

            _txtSourceAbbr = new TextBox
            {
                Location = new Point(leftX, y),
                Width = colWidth,
                BackColor = inputBg,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = new Font("Segoe UI", 9f)
            };
            _contentPanel.Controls.Add(_txtSourceAbbr);

            _txtTargetAbbr = new TextBox
            {
                Location = new Point(rightX, y),
                Width = colWidth,
                BackColor = inputBg,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Font = new Font("Segoe UI", 9f)
            };
            _contentPanel.Controls.Add(_txtTargetAbbr);
            y += 30;

            // Auto-fill target abbreviation when non-translatable
            _txtSourceAbbr.TextChanged += (s, e) =>
            {
                if (_chkNonTranslatable.Checked)
                    _txtTargetAbbr.Text = _txtSourceAbbr.Text;
            };

            // ── Separator between primary terms and synonyms ──
            _contentPanel.Controls.Add(new Label
            {
                AutoSize = false,
                Location = new Point(leftX, y + 2),
                Width = _contentPanel.Width - 32,
                Height = 1,
                BackColor = Color.FromArgb(215, 215, 215),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            });
            y += 14;

            // === Source / Target synonyms (use actual language names when available) ===
            var srcSynLabel = _isInverted
                ? (!string.IsNullOrEmpty(termbase?.TargetLang) ? $"{LanguageUtils.ShortenLanguageName(termbase.TargetLang)} synonyms:" : "Source synonyms:")
                : (!string.IsNullOrEmpty(termbase?.SourceLang) ? $"{LanguageUtils.ShortenLanguageName(termbase.SourceLang)} synonyms:" : "Source synonyms:");
            var tgtSynLabel = _isInverted
                ? (!string.IsNullOrEmpty(termbase?.SourceLang) ? $"{LanguageUtils.ShortenLanguageName(termbase.SourceLang)} synonyms:" : "Target synonyms:")
                : (!string.IsNullOrEmpty(termbase?.TargetLang) ? $"{LanguageUtils.ShortenLanguageName(termbase.TargetLang)} synonyms:" : "Target synonyms:");
            _contentPanel.Controls.Add(MakeLabel(srcSynLabel, leftX, y, labelColor));
            _contentPanel.Controls.Add(MakeLabel(tgtSynLabel, rightX, y, labelColor));
            y += 18;

            _txtNewSourceSyn = new TextBox
            {
                Location = new Point(leftX, y),
                Width = colWidth - 28,
                BackColor = inputBg,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _contentPanel.Controls.Add(_txtNewSourceSyn);

            var btnAddSrc = MakeSmallButton("+", new Point(leftX + colWidth - 24, y), () => AddSourceSynonym());
            btnAddSrc.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _toolTip.SetToolTip(btnAddSrc, "Add synonym");
            _contentPanel.Controls.Add(btnAddSrc);

            _txtNewTargetSyn = new TextBox
            {
                Location = new Point(rightX, y),
                Width = colWidth - 28,
                BackColor = inputBg,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _contentPanel.Controls.Add(_txtNewTargetSyn);

            var btnAddTgt = MakeSmallButton("+", new Point(rightX + colWidth - 24, y), () => AddTargetSynonym());
            btnAddTgt.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _toolTip.SetToolTip(btnAddTgt, "Add synonym");
            _contentPanel.Controls.Add(btnAddTgt);
            y += 26;

            // Source synonym list + buttons
            int listHeight = 120;

            _lstSourceSynonyms = new ListBox
            {
                Location = new Point(leftX, y),
                Width = colWidth - 28,
                Height = listHeight,
                BackColor = inputBg,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _lstSourceSynonyms.MouseDown += OnSynonymListRightClick;
            _lstSourceSynonyms.DoubleClick += (s, ev) => OnSynonymDoubleClick(_lstSourceSynonyms, _sourceSyns);
            _toolTip.SetToolTip(_lstSourceSynonyms, "Double-click to promote to primary term\nRight-click for more options");
            _contentPanel.Controls.Add(_lstSourceSynonyms);

            int btnX = leftX + colWidth - 24;
            var srcUp = MakeSmallButton("\u25B2", new Point(btnX, y), () => MoveSynonymUp(_lstSourceSynonyms, _sourceSyns));
            _toolTip.SetToolTip(srcUp, "Move up");
            _contentPanel.Controls.Add(srcUp);
            var srcDown = MakeSmallButton("\u25BC", new Point(btnX, y + 26), () => MoveSynonymDown(_lstSourceSynonyms, _sourceSyns));
            _toolTip.SetToolTip(srcDown, "Move down");
            _contentPanel.Controls.Add(srcDown);
            var srcRemove = MakeSmallButton("\u2715", new Point(btnX, y + 52), () => RemoveSynonym(_lstSourceSynonyms, _sourceSyns));
            _toolTip.SetToolTip(srcRemove, "Remove synonym");
            _contentPanel.Controls.Add(srcRemove);

            // Target synonym list + buttons
            _lstTargetSynonyms = new ListBox
            {
                Location = new Point(rightX, y),
                Width = colWidth - 28,
                Height = listHeight,
                BackColor = inputBg,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _lstTargetSynonyms.MouseDown += OnSynonymListRightClick;
            _lstTargetSynonyms.DoubleClick += (s, ev) => OnSynonymDoubleClick(_lstTargetSynonyms, _targetSyns);
            _toolTip.SetToolTip(_lstTargetSynonyms, "Double-click to promote to primary term\nRight-click for more options");
            _contentPanel.Controls.Add(_lstTargetSynonyms);

            btnX = rightX + colWidth - 24;
            var tgtUp = MakeSmallButton("\u25B2", new Point(btnX, y), () => MoveSynonymUp(_lstTargetSynonyms, _targetSyns));
            tgtUp.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _toolTip.SetToolTip(tgtUp, "Move up");
            _contentPanel.Controls.Add(tgtUp);
            var tgtDown = MakeSmallButton("\u25BC", new Point(btnX, y + 26), () => MoveSynonymDown(_lstTargetSynonyms, _targetSyns));
            tgtDown.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _toolTip.SetToolTip(tgtDown, "Move down");
            _contentPanel.Controls.Add(tgtDown);
            var tgtRemove = MakeSmallButton("\u2715", new Point(btnX, y + 52), () => RemoveSynonym(_lstTargetSynonyms, _targetSyns));
            tgtRemove.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _toolTip.SetToolTip(tgtRemove, "Remove synonym");
            _contentPanel.Controls.Add(tgtRemove);

            y += listHeight + 8;

            // ── Separator between synonyms and metadata ──
            _contentPanel.Controls.Add(new Label
            {
                AutoSize = false,
                Location = new Point(leftX, y + 2),
                Width = _contentPanel.Width - 32,
                Height = 1,
                BackColor = Color.FromArgb(215, 215, 215),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            });
            y += 14;

            // === Metadata fields ===
            _contentPanel.Controls.Add(MakeLabel("Definition:", leftX, y, labelColor));
            var btnExpandDef = MakeExpandButton(leftX + 75, y, labelColor);
            y += 18;

            _txtDefinition = new TextBox
            {
                Location = new Point(leftX, y),
                Width = _contentPanel.Width - 32,
                Height = CollapsedHeight,
                BackColor = inputBg,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                AcceptsReturn = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _contentPanel.Controls.Add(_txtDefinition);
            // Add button AFTER textbox so it's on top (WinForms z-order)
            _contentPanel.Controls.Add(btnExpandDef);
            btnExpandDef.BringToFront();
            btnExpandDef.Click += (s, e) => ToggleExpand(_txtDefinition, btnExpandDef, ref _definitionExpanded);
            y += CollapsedHeight + 8;

            _contentPanel.Controls.Add(MakeLabel("Domain:", leftX, y, labelColor));
            y += 18;

            _txtDomain = new TextBox
            {
                Location = new Point(leftX, y),
                Width = colWidth,
                BackColor = inputBg,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _contentPanel.Controls.Add(_txtDomain);
            y += 28;

            _contentPanel.Controls.Add(MakeLabel("Notes:", leftX, y, labelColor));
            var btnExpandNotes = MakeExpandButton(leftX + 48, y, labelColor);
            y += 18;

            _txtNotes = new TextBox
            {
                Location = new Point(leftX, y),
                Width = _contentPanel.Width - 32,
                Height = CollapsedHeight,
                BackColor = inputBg,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                AcceptsReturn = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _contentPanel.Controls.Add(_txtNotes);
            // Add button AFTER textbox so it's on top (WinForms z-order)
            _contentPanel.Controls.Add(btnExpandNotes);
            btnExpandNotes.BringToFront();
            btnExpandNotes.Click += (s, e) => ToggleExpand(_txtNotes, btnExpandNotes, ref _notesExpanded);
            y += CollapsedHeight + 8;

            _contentPanel.Controls.Add(MakeLabel("URL (optional):", leftX, y, labelColor));
            y += 18;

            _txtUrl = new TextBox
            {
                Location = new Point(leftX, y),
                Width = _contentPanel.Width - 32,
                BackColor = inputBg,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _contentPanel.Controls.Add(_txtUrl);
            y += 28;

            _contentPanel.Controls.Add(MakeLabel("Client (optional):", leftX, y, labelColor));
            y += 18;

            _txtClient = new TextBox
            {
                Location = new Point(leftX, y),
                Width = _contentPanel.Width - 32,
                BackColor = inputBg,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            var clientTip = new ToolTip { AutoPopDelay = 8000 };
            clientTip.SetToolTip(_txtClient,
                "Client code — a short identifier like ACME or GLOBEX.\n" +
                "Used to filter SuperMemory knowledge base context\n" +
                "and link terms to specific clients.");
            _contentPanel.Controls.Add(_txtClient);
            y += 28;

            // Non-translatable checkbox
            _chkNonTranslatable = new CheckBox
            {
                Text = "Non-translatable (keep source text in target)",
                Location = new Point(leftX, y),
                AutoSize = true,
                ForeColor = labelColor
            };
            _chkNonTranslatable.CheckedChanged += (s, e) =>
            {
                if (_chkNonTranslatable.Checked)
                {
                    _txtTarget.Text = _txtSource.Text;
                    _txtTarget.ReadOnly = true;
                    _txtTarget.BackColor = Color.FromArgb(230, 230, 230);
                    _txtTargetAbbr.Text = _txtSourceAbbr.Text;
                    _txtTargetAbbr.ReadOnly = true;
                    _txtTargetAbbr.BackColor = Color.FromArgb(230, 230, 230);
                }
                else
                {
                    _txtTarget.ReadOnly = false;
                    _txtTarget.BackColor = inputBg;
                    _txtTargetAbbr.ReadOnly = false;
                    _txtTargetAbbr.BackColor = inputBg;
                }
            };
            _contentPanel.Controls.Add(_chkNonTranslatable);

            _txtSource.TextChanged += (s, e) =>
            {
                if (_chkNonTranslatable.Checked)
                    _txtTarget.Text = _txtSource.Text;
            };

            // === Add panels to form (last-added docks first) ===
            _contentPanel.Dock = DockStyle.Fill;
            Controls.Add(_contentPanel);
            Controls.Add(bottomPanel);
        }

        // ─── Lifecycle ────────────────────────────────────────────────

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // Set placeholder text AFTER handles are created (accessing .Handle
            // during the constructor breaks the WinForms parent-child handle chain)
            SetPlaceholder(_txtNewSourceSyn, "Type synonym, press Enter or +");
            SetPlaceholder(_txtNewTargetSyn, "Type synonym, press Enter or +");
        }

        // ─── Key handling ─────────────────────────────────────────────

        private void OnHelpButtonClicked(object sender, System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            HelpSystem.OpenHelp(HelpSystem.Topics.AddTermDialog);
        }

        /// <summary>
        /// Intercepts Enter in the synonym textboxes so AcceptButton (Save) doesn't steal it.
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F1)
            {
                HelpSystem.OpenHelp(HelpSystem.Topics.AddTermDialog);
                return true;
            }

            if (keyData == Keys.Enter)
            {
                if (_txtNewSourceSyn.Focused)
                {
                    AddSourceSynonym();
                    return true;
                }
                if (_txtNewTargetSyn.Focused)
                {
                    AddTargetSynonym();
                    return true;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ─── Populate / Load ─────────────────────────────────────────

        private void PopulateFromEntry(TermEntry entry)
        {
            if (entry == null) return;
            _txtSource.Text = entry.SourceTerm ?? "";
            _txtTarget.Text = entry.TargetTerm ?? "";
            _txtSourceAbbr.Text = entry.SourceAbbreviation ?? "";
            _txtTargetAbbr.Text = entry.TargetAbbreviation ?? "";
            _txtDefinition.Text = entry.Definition ?? "";
            _txtDomain.Text = entry.Domain ?? "";
            _txtNotes.Text = entry.Notes ?? "";
            _txtUrl.Text = entry.Url ?? "";
            _txtClient.Text = entry.Client ?? "";
            _chkNonTranslatable.Checked = entry.IsNonTranslatable;
        }

        private void LoadSynonymsFromDb()
        {
            if (_termId <= 0 || string.IsNullOrEmpty(_dbPath)) return;

            try
            {
                var synonyms = TermbaseReader.GetSynonyms(_dbPath, _termId);
                foreach (var syn in synonyms)
                {
                    // When inverted: DB source synonyms (termbase source language) display on the
                    // right (project target side); DB target synonyms display on the left (project source side).
                    bool showOnLeft = _isInverted ? syn.Language == "target" : syn.Language == "source";
                    if (showOnLeft)
                    {
                        _sourceSyns.Add(syn);
                        _lstSourceSynonyms.Items.Add(FormatSynonymDisplay(syn));
                    }
                    else
                    {
                        _targetSyns.Add(syn);
                        _lstTargetSynonyms.Items.Add(FormatSynonymDisplay(syn));
                    }
                }
            }
            catch
            {
                // Non-critical — editor still works without synonyms
            }
        }

        // ─── Multi-entry switching ─────────────────────────────────────

        private void OnTermbaseSwitched(object sender, EventArgs e)
        {
            if (_cboTermbase == null || _allEntryData.Count <= 1) return;
            int newIndex = _cboTermbase.SelectedIndex;
            if (newIndex == _activeEntryIndex || newIndex < 0) return;
            SwitchToEntry(newIndex);
        }

        private void CaptureCurrentState()
        {
            if (_activeEntryIndex < 0 || _activeEntryIndex >= _allEntryData.Count) return;
            var ed = _allEntryData[_activeEntryIndex];
            ed.Source = _txtSource.Text.Trim();
            ed.Target = _txtTarget.Text.Trim();
            ed.SourceAbbr = _txtSourceAbbr.Text.Trim();
            ed.TargetAbbr = _txtTargetAbbr.Text.Trim();
            ed.Definition = _txtDefinition.Text.Trim();
            ed.Domain = _txtDomain.Text.Trim();
            ed.Notes = _txtNotes.Text.Trim();
            ed.Url = _txtUrl.Text.Trim();
            ed.Client = _txtClient.Text.Trim();
            ed.IsNonTranslatable = _chkNonTranslatable.Checked;
            // Synonym lists are stored by reference — already up to date
            ed.SourceSyns = new List<SynonymEntry>(_sourceSyns);
            ed.TargetSyns = new List<SynonymEntry>(_targetSyns);
        }

        private void SwitchToEntry(int newIndex)
        {
            // Save current state
            CaptureCurrentState();

            _activeEntryIndex = newIndex;
            var ed = _allEntryData[newIndex];

            // Update tracked termbase/ID/direction for save/delete operations
            _termbase = ed.Termbase;
            _termId = ed.Entry.Id;
            _isInverted = ed.IsInverted;

            // Load fields
            _txtSource.Text = ed.Source;
            _txtTarget.Text = ed.Target;
            _txtSourceAbbr.Text = ed.SourceAbbr ?? "";
            _txtTargetAbbr.Text = ed.TargetAbbr ?? "";
            _txtDefinition.Text = ed.Definition;
            _txtDomain.Text = ed.Domain;
            _txtNotes.Text = ed.Notes;
            _txtUrl.Text = ed.Url ?? "";
            _txtClient.Text = ed.Client ?? "";
            _chkNonTranslatable.Checked = ed.IsNonTranslatable;

            // Load synonyms
            _sourceSyns.Clear();
            _targetSyns.Clear();
            _lstSourceSynonyms.Items.Clear();
            _lstTargetSynonyms.Items.Clear();

            if (!ed.SynonymsLoaded)
            {
                // Lazy-load synonyms from DB on first access
                LoadSynonymsForEntry(ed);
            }
            else
            {
                // Restore from cached state
                _sourceSyns.AddRange(ed.SourceSyns);
                _targetSyns.AddRange(ed.TargetSyns);
            }

            foreach (var syn in _sourceSyns)
                _lstSourceSynonyms.Items.Add(FormatSynonymDisplay(syn));
            foreach (var syn in _targetSyns)
                _lstTargetSynonyms.Items.Add(FormatSynonymDisplay(syn));

            // Update title bar
            Text = $"Edit term entry (ID {ed.Entry.Id}) \u2014 {ed.Termbase?.Name ?? "Unknown"}";
        }

        private void LoadSynonymsForEntry(EntryData ed)
        {
            if (ed.Entry.Id <= 0 || string.IsNullOrEmpty(_dbPath)) return;

            try
            {
                var synonyms = TermbaseReader.GetSynonyms(_dbPath, ed.Entry.Id);
                foreach (var syn in synonyms)
                {
                    if (syn.Language == "source")
                    {
                        _sourceSyns.Add(syn);
                        ed.SourceSyns.Add(syn);
                    }
                    else
                    {
                        _targetSyns.Add(syn);
                        ed.TargetSyns.Add(syn);
                    }
                }
            }
            catch
            {
                // Non-critical
            }
            ed.SynonymsLoaded = true;
        }

        // ─── Synonym operations ──────────────────────────────────────

        private void AddSourceSynonym()
        {
            var text = _txtNewSourceSyn.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            // Check for duplicates
            if (IsDuplicate(text, _sourceSyns, _txtSource.Text))
            {
                MessageBox.Show("This synonym already exists.", "TermLens",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var syn = new SynonymEntry
            {
                Text = text,
                Language = "source",
                DisplayOrder = _sourceSyns.Count,
                Forbidden = false
            };
            _sourceSyns.Add(syn);
            _lstSourceSynonyms.Items.Add(FormatSynonymDisplay(syn));
            _txtNewSourceSyn.Clear();
            _txtNewSourceSyn.Focus();
        }

        private void AddTargetSynonym()
        {
            var text = _txtNewTargetSyn.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            if (IsDuplicate(text, _targetSyns, _txtTarget.Text))
            {
                MessageBox.Show("This synonym already exists.", "TermLens",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var syn = new SynonymEntry
            {
                Text = text,
                Language = "target",
                DisplayOrder = _targetSyns.Count,
                Forbidden = false
            };
            _targetSyns.Add(syn);
            _lstTargetSynonyms.Items.Add(FormatSynonymDisplay(syn));
            _txtNewTargetSyn.Clear();
            _txtNewTargetSyn.Focus();
        }

        private bool IsDuplicate(string text, List<SynonymEntry> list, string primaryTerm)
        {
            if (string.Equals(text, primaryTerm, StringComparison.OrdinalIgnoreCase))
                return true;
            foreach (var syn in list)
            {
                if (string.Equals(syn.Text, text, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private void MoveSynonymUp(ListBox list, List<SynonymEntry> data)
        {
            int idx = list.SelectedIndex;
            if (idx <= 0) return;

            var syn = data[idx];
            data.RemoveAt(idx);
            data.Insert(idx - 1, syn);

            var item = list.Items[idx];
            list.Items.RemoveAt(idx);
            list.Items.Insert(idx - 1, item);
            list.SelectedIndex = idx - 1;
        }

        private void MoveSynonymDown(ListBox list, List<SynonymEntry> data)
        {
            int idx = list.SelectedIndex;
            if (idx < 0 || idx >= data.Count - 1) return;

            var syn = data[idx];
            data.RemoveAt(idx);
            data.Insert(idx + 1, syn);

            var item = list.Items[idx];
            list.Items.RemoveAt(idx);
            list.Items.Insert(idx + 1, item);
            list.SelectedIndex = idx + 1;
        }

        private void RemoveSynonym(ListBox list, List<SynonymEntry> data)
        {
            int idx = list.SelectedIndex;
            if (idx < 0) return;

            data.RemoveAt(idx);
            list.Items.RemoveAt(idx);

            if (list.Items.Count > 0)
                list.SelectedIndex = Math.Min(idx, list.Items.Count - 1);
        }

        // ─── Drawing ─────────────────────────────────────────────────

        private void OnDrawSynonymItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            e.DrawBackground();

            var list = (ListBox)sender;
            var data = list == _lstSourceSynonyms ? _sourceSyns : _targetSyns;

            if (e.Index < data.Count)
            {
                var syn = data[e.Index];
                var text = syn.Text;
                var color = syn.Forbidden ? Color.FromArgb(180, 60, 60) : Color.FromArgb(40, 40, 40);
                var font = syn.Forbidden
                    ? new Font(e.Font, FontStyle.Strikeout)
                    : e.Font;

                if (syn.Forbidden)
                    text = "[F] " + text;

                TextRenderer.DrawText(e.Graphics, text, font,
                    new Rectangle(e.Bounds.X + 2, e.Bounds.Y, e.Bounds.Width - 4, e.Bounds.Height),
                    color, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);

                if (syn.Forbidden && font != e.Font)
                    font.Dispose();
            }

            e.DrawFocusRectangle();
        }

        private void OnSynonymListRightClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;

            var list = (ListBox)sender;
            int idx = list.IndexFromPoint(e.Location);
            if (idx < 0) return;

            list.SelectedIndex = idx;
            var data = list == _lstSourceSynonyms ? _sourceSyns : _targetSyns;

            if (idx >= data.Count) return;
            var syn = data[idx];

            var menu = new ContextMenuStrip();

            var toggleForbidden = new ToolStripMenuItem(
                syn.Forbidden ? "Mark as Allowed" : "Mark as Forbidden");
            toggleForbidden.Click += (s, ev) =>
            {
                syn.Forbidden = !syn.Forbidden;
                list.Items[idx] = FormatSynonymDisplay(syn);
                list.Invalidate();
            };
            menu.Items.Add(toggleForbidden);

            var promoteItem = new ToolStripMenuItem("Promote to Primary");
            promoteItem.Click += (s, ev) => PromoteToPrimary(list, data, idx);
            menu.Items.Add(promoteItem);

            menu.Show(list, e.Location);
        }

        private void OnSynonymDoubleClick(ListBox list, List<SynonymEntry> data)
        {
            int idx = list.SelectedIndex;
            if (idx >= 0)
                PromoteToPrimary(list, data, idx);
        }

        private void PromoteToPrimary(ListBox list, List<SynonymEntry> data, int idx)
        {
            if (idx < 0 || idx >= data.Count) return;

            var syn = data[idx];
            var textBox = list == _lstSourceSynonyms ? _txtSource : _txtTarget;

            // Swap: current primary → synonym, selected synonym → primary
            var oldPrimary = textBox.Text.Trim();
            textBox.Text = syn.Text;
            syn.Text = oldPrimary;

            // Update display
            list.Items[idx] = FormatSynonymDisplay(syn);
            list.Invalidate();
        }

        private static string FormatSynonymDisplay(SynonymEntry syn)
        {
            return syn.Forbidden ? $"[F] {syn.Text}" : syn.Text;
        }

        // ─── Save / Delete ───────────────────────────────────────────

        private void OnSaveClick(object sender, EventArgs e)
        {
            var source = _txtSource.Text.Trim();
            var target = _txtTarget.Text.Trim();

            // When the project direction is inverted relative to the termbase, the left field
            // holds the project source (termbase target) and vice versa — swap before saving.
            if (_isInverted) { var t = source; source = target; target = t; }

            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
            {
                MessageBox.Show("Both source and target terms are required.",
                    "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                if (IsEditMode)
                {
                    // Update the main term
                    TermbaseReader.UpdateTerm(_dbPath, _termId,
                        source, target, Definition, Domain, Notes,
                        isNonTranslatable: IsNonTranslatable,
                        sourceAbbreviation: SourceAbbreviation,
                        targetAbbreviation: TargetAbbreviation,
                        url: Url, client: Client);
                }
                else if (_termbase != null)
                {
                    // Insert new term — store ID for synonym save
                    // Note: InsertTerm is for single termbase; for add mode we need the termbase
                    var newId = TermbaseReader.InsertTerm(_dbPath, _termbase.Id,
                        source, target,
                        _termbase.SourceLang, _termbase.TargetLang,
                        Definition, Domain, Notes,
                        isNonTranslatable: IsNonTranslatable,
                        sourceAbbreviation: SourceAbbreviation,
                        targetAbbreviation: TargetAbbreviation,
                        url: Url, client: Client);

                    // Can't save synonyms without a term ID
                    if (newId <= 0)
                    {
                        MessageBox.Show(
                            "This term already exists in the termbase.",
                            "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    // Save synonyms for the newly created term
                    SaveAllSynonyms(newId);
                    DialogResult = DialogResult.OK;
                    Close();
                    return;
                }

                // Save synonyms (edit mode)
                SaveAllSynonyms(_termId);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save term:\n{ex.Message}",
                    "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SaveAllSynonyms(long termId)
        {
            // Combine source and target synonym lists.
            // When inverted, the left column shows DB target synonyms and the right
            // column shows DB source synonyms, so we must reverse the language tags.
            var leftLang = _isInverted ? "target" : "source";
            var rightLang = _isInverted ? "source" : "target";
            var allSynonyms = new List<SynonymEntry>();

            for (int i = 0; i < _sourceSyns.Count; i++)
            {
                _sourceSyns[i].DisplayOrder = i;
                _sourceSyns[i].Language = leftLang;
                allSynonyms.Add(_sourceSyns[i]);
            }

            for (int i = 0; i < _targetSyns.Count; i++)
            {
                _targetSyns[i].DisplayOrder = i;
                _targetSyns[i].Language = rightLang;
                allSynonyms.Add(_targetSyns[i]);
            }

            TermbaseReader.SaveSynonyms(_dbPath, termId, allSynonyms);
        }

        private void OnDeleteClick(object sender, EventArgs e)
        {
            var result = MessageBox.Show(
                $"Delete the term \u201c{_txtSource.Text.Trim()} \u2192 {_txtTarget.Text.Trim()}\u201d" +
                " and all its synonyms?\n\nThis cannot be undone.",
                "TermLens \u2014 Delete Term",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (result != DialogResult.Yes) return;

            try
            {
                TermbaseReader.DeleteTerm(_dbPath, _termId);
                DialogResult = DialogResult.Abort; // Signal deletion to caller
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to delete term:\n{ex.Message}",
                    "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ─── UI helpers ──────────────────────────────────────────────

        private static Button MakeExpandButton(int x, int y, Color color)
        {
            var btn = new Button
            {
                Text = "\u25BE",  // ▾ small down triangle
                Location = new Point(x, y),
                Size = new Size(18, 16),
                Font = new Font("Segoe UI", 6f),
                ForeColor = color,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                TabStop = false
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(230, 230, 230);
            btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(210, 210, 210);
            return btn;
        }

        private void ToggleExpand(TextBox textBox, Button button, ref bool expanded)
        {
            expanded = !expanded;
            int delta = ExpandedHeight - CollapsedHeight;
            if (!expanded) delta = -delta;

            textBox.Height = expanded ? ExpandedHeight : CollapsedHeight;
            button.Text = expanded ? "\u25B4" : "\u25BE";  // ▴ or ▾

            // Shift all controls below the textbox
            int threshold = textBox.Bottom - (expanded ? delta : 0);
            foreach (Control ctrl in _contentPanel.Controls)
            {
                if (ctrl != textBox && ctrl.Top >= threshold)
                    ctrl.Top += delta;
            }

            // Resize the dialog
            Height += delta;
        }

        private static Label MakeLabel(string text, int x, int y, Color color)
        {
            return new Label
            {
                Text = text,
                Location = new Point(x, y),
                AutoSize = true,
                ForeColor = color
            };
        }

        // Win32 cue banner (placeholder text) for textboxes
        private const int EM_SETCUEBANNER = 0x1501;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private static void SetPlaceholder(TextBox textBox, string placeholder)
        {
            SendMessage(textBox.Handle, EM_SETCUEBANNER, (IntPtr)1, placeholder);
        }

        private static Button MakeSmallButton(string text, Point location, Action onClick)
        {
            var btn = new Button
            {
                Text = text,
                Location = location,
                Size = new Size(26, 26),
                FlatStyle = FlatStyle.System,
                Font = new Font("Segoe UI", 8f)
            };
            btn.Click += (s, e) => onClick();
            return btn;
        }
    }
}
