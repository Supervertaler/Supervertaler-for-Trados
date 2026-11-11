using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// Add segments to SuperMemory: picks a range of segments, shows what it will
    /// write and what it costs, and writes it into the chosen bank's brief.md under
    /// <c>## Reference translations</c> (see <see cref="ReferenceTranslations"/>).
    ///
    /// <para>The dialog writes, not its caller, so that a version conflict - brief.md
    /// changed in Obsidian or by a colleague since the window opened - can be shown
    /// and tried again without losing the choices made. Nothing is written over such
    /// a change: the file is read again and the person clicks Add again.</para>
    ///
    /// <para>LAYOUT: TableLayoutPanels with AutoSize rows and Margins, no computed
    /// coordinates; <see cref="DialogScale.Apply"/> scales it once, at the end.</para>
    /// </summary>
    internal sealed class SuperMemoryAddSegmentsDialog : Form
    {
        private const int ContentWidth = 520;

        private static readonly Color Grey = Color.FromArgb(80, 80, 80);
        private static readonly Color Blue = Color.FromArgb(0, 90, 158);
        private static readonly Color Amber = Color.FromArgb(160, 100, 0);
        private static readonly Color Red = Color.FromArgb(180, 30, 30);

        private readonly IList<ReferenceSegment> _segments;
        private readonly string _sourceLabel;
        private readonly string _targetLabel;
        private readonly string _activeBank;
        private readonly string _fileNote;
        private readonly bool _merged;

        private readonly ComboBox _cmbBank;
        private readonly Label _lblDestination;
        private readonly ComboBox _cmbFromFile;
        private readonly ComboBox _cmbToFile;
        private readonly NumericUpDown _numFrom;
        private readonly NumericUpDown _numTo;
        private readonly CheckBox _chkConfirmed;
        private readonly Label _lblSummary;
        private readonly Label _lblFigures;
        private readonly Label _lblWarnings;
        private readonly TextBox _txtPreview;
        private readonly Button _btnAdd;

        // The bank as read when it was chosen: what the write is checked against.
        private string _bank;
        private string _briefText;
        private string _briefVersion;
        private string _bankProblem;
        private BankFigures _figures;

        // The range as it stands, for the write.
        private ReferencePick _pick;
        private string _body;

        /// <summary>What was written, once the dialog closes with OK.</summary>
        public BankFileResult Written { get; private set; }
        public int WrittenSegments { get; private set; }
        public int WrittenTokens { get; private set; }
        public bool ReplacedExisting { get; private set; }

        /// <param name="segments">Every segment of the document, in document order.</param>
        /// <param name="fileNote">Said in the dialog when the files of a merged
        /// document had to be told apart by their numbering; null otherwise.</param>
        /// <param name="fromFile">Where the range starts: null in a one-file document.</param>
        public SuperMemoryAddSegmentsDialog(IList<ReferenceSegment> segments, string fileNote,
            string fromFile, int from, string toFile, int to,
            string sourceLabel, string targetLabel, string activeBank)
        {
            _segments = segments ?? new List<ReferenceSegment>();
            _sourceLabel = sourceLabel;
            _targetLabel = targetLabel;
            _activeBank = activeBank;
            _fileNote = fileNote;
            var files = _segments.Select(s => s.File).Where(f => f != null).Distinct().ToList();
            _merged = files.Count > 0;

            Icon = IconHelper.AppIcon;
            AutoScaleMode = AutoScaleMode.None;
            Text = "Add segments to SuperMemory";
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.White;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            // The title-bar ? (shown because there is no minimise or maximise
            // box) and F1 open this dialog's help page, as the termbase dialogs do.
            HelpButton = true;
            HelpButtonClicked += (s, e) =>
            {
                e.Cancel = true;   // no ? cursor
                HelpSystem.OpenHelp(HelpSystem.Topics.SuperMemoryAddSegments);
            };
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.F1) return;
                e.Handled = true;
                HelpSystem.OpenHelp(HelpSystem.Topics.SuperMemoryAddSegments);
            };

            SuspendLayout();
            var root = new TableLayoutPanel
            {
                ColumnCount = 1,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Padding = new Padding(16, 14, 16, 12),
                GrowStyle = TableLayoutPanelGrowStyle.AddRows
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ContentWidth));

            // ── Where ────────────────────────────────────────────────
            root.Controls.Add(Caption("Save to memory bank:"));
            _cmbBank = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = ContentWidth,
                Margin = new Padding(0, 0, 0, 2)
            };
            root.Controls.Add(_cmbBank);
            _lblDestination = Wrapped("", Blue, new Font("Segoe UI", 8.25f, FontStyle.Italic), new Padding(2, 0, 0, 10));
            root.Controls.Add(_lblDestination);

            // ── Which segments ───────────────────────────────────────
            root.Controls.Add(Caption("Segments (the numbers Trados Studio shows):"));
            var range = new TableLayoutPanel
            {
                ColumnCount = _merged ? 3 : 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, 0, 6)
            };
            var max = Math.Max(1, _segments.Count == 0 ? 1 : _segments.Max(s => s.Value));
            _numFrom = Number(max, from);
            _numTo = Number(max, to);
            if (_merged)
            {
                _cmbFromFile = FileList(files, fromFile);
                _cmbToFile = FileList(files, toFile);
            }
            AddRangeRow(range, "From:", _cmbFromFile, _numFrom);
            AddRangeRow(range, "To:", _cmbToFile, _numTo);
            root.Controls.Add(range);

            _chkConfirmed = new CheckBox
            {
                Text = "Only confirmed segments (Translated, Translation Approved or Signed Off)",
                Checked = true,
                AutoSize = true,
                ForeColor = Color.FromArgb(60, 60, 60),
                Margin = new Padding(0, 0, 0, 10)
            };
            root.Controls.Add(_chkConfirmed);

            // ── What it comes to ─────────────────────────────────────
            _lblSummary = Wrapped("", Color.FromArgb(30, 30, 30), new Font("Segoe UI", 9f, FontStyle.Bold), new Padding(0, 0, 0, 2));
            root.Controls.Add(_lblSummary);
            _lblFigures = Wrapped("", Grey, null, new Padding(0, 0, 0, 6));
            root.Controls.Add(_lblFigures);
            _lblWarnings = Wrapped("", Amber, null, new Padding(0, 0, 0, 8));
            root.Controls.Add(_lblWarnings);

            root.Controls.Add(Caption("What will be written:"));
            _txtPreview = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                WordWrap = true,
                Width = ContentWidth,
                Height = 170,
                BackColor = Color.FromArgb(248, 248, 248),
                Font = new Font("Segoe UI", 8.25f),
                Margin = new Padding(0, 0, 0, 12)
            };
            root.Controls.Add(_txtPreview);

            // ── Buttons ──────────────────────────────────────────────
            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Anchor = AnchorStyles.Right,
                WrapContents = false,
                Margin = new Padding(0)
            };
            var btnCancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                AutoSize = true,
                MinimumSize = new Size(80, 0),
                FlatStyle = FlatStyle.System,
                Margin = new Padding(6, 0, 0, 0)
            };
            _btnAdd = new Button
            {
                Text = "Add",
                AutoSize = true,
                MinimumSize = new Size(80, 0),
                FlatStyle = FlatStyle.System,
                Margin = new Padding(0)
            };
            _btnAdd.Click += (s, e) => Add();
            buttons.Controls.Add(btnCancel);
            buttons.Controls.Add(_btnAdd);
            root.Controls.Add(buttons);
            AcceptButton = _btnAdd;
            CancelButton = btnCancel;

            Controls.Add(root);

            SuperMemoryQuickAddDialog.FillBankList(_cmbBank, activeBank);
            _cmbBank.SelectedIndexChanged += (s, e) => { LoadBank(); Recompute(); };
            ComboFilter.Attach(_cmbBank, "Type to filter memory banks");
            _numFrom.ValueChanged += (s, e) => Recompute();
            _numTo.ValueChanged += (s, e) => Recompute();
            if (_cmbFromFile != null) _cmbFromFile.SelectedIndexChanged += (s, e) => Recompute();
            if (_cmbToFile != null) _cmbToFile.SelectedIndexChanged += (s, e) => Recompute();
            _chkConfirmed.CheckedChanged += (s, e) => Recompute();

            LoadBank();
            Recompute();

            ResumeLayout(false);
            PerformLayout();
            DialogScale.Apply(this);
        }

        // ── The bank ─────────────────────────────────────────────────

        private bool IsShared => MemoryBanks.IsSharedName(_bank);

        private bool IsActive => !string.IsNullOrEmpty(_activeBank)
            && string.Equals(_bank, _activeBank, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Reads the chosen bank's brief.md and its version, once, when the bank is
        /// chosen: the write is checked against exactly this. A missing brief.md is
        /// created; a missing bank folder is a problem, since nothing creates banks
        /// here.
        /// </summary>
        private void LoadBank()
        {
            _bank = SuperMemoryQuickAddDialog.BankOf(_cmbBank.SelectedItem);
            _briefText = null;
            _briefVersion = null;
            _bankProblem = null;
            _figures = null;
            if (string.IsNullOrEmpty(_bank)) { _bankProblem = "Choose a memory bank."; return; }

            var dir = Path.Combine(MemoryBanks.Root, _bank);
            if (!Directory.Exists(dir))
            {
                _bankProblem = "The memory bank “" + _bank + "” has no folder at " + dir
                    + ". Create it from the memory bank list on the SuperMemory bar, or choose another bank.";
                return;
            }

            var read = BankFileStore.ForUser().Read(_bank, MemoryBankReader.BriefFile);
            if (read.Ok)
            {
                _briefText = read.Content;
                _briefVersion = read.Version;
            }
            else if (!File.Exists(Path.Combine(dir, MemoryBankReader.BriefFile)))
            {
                _briefVersion = BankFileStore.NewFile;   // created by the write
            }
            else
            {
                _bankProblem = read.Error;
                return;
            }

            _figures = ReferenceTranslations.Measure(dir, _briefText);
        }

        // ── The range, and what it comes to ──────────────────────────

        private void Recompute()
        {
            _pick = null;
            _body = null;
            var problems = new List<string>();
            var warnings = new List<string>();

            _lblDestination.Text = string.IsNullOrEmpty(_bank) ? ""
                : "→ brief.md, under “## " + ReferenceTranslations.Heading + "”, in " + _bank;
            _lblDestination.ForeColor = IsShared ? Amber : Blue;

            // Picking stops a little past the budget: a range that big is refused
            // anyway, and reading the rest of it would only cost time.
            var limitChars = (ReferenceTranslations.BudgetTokens + 1) * 4;
            var pick = ReferenceTranslations.Pick(_segments,
                FileOf(_cmbFromFile), (int)_numFrom.Value, FileOf(_cmbToFile), (int)_numTo.Value,
                _chkConfirmed.Checked, limitChars);

            if (pick.Problem != null) problems.Add(pick.Problem);
            else if (pick.Segments.Count == 0) problems.Add(NothingToAdd(pick));
            else
            {
                _pick = pick;
                _body = ReferenceTranslations.Body(pick.Segments, _sourceLabel, _targetLabel,
                    ReferenceTranslations.RangeLabel(pick.Segments), DateTime.Now);
            }

            var tokens = ReferenceTranslations.Tokens(_body);
            _lblSummary.Text = _pick == null ? "Nothing to add yet."
                : Count(_pick.Segments.Count, "segment") + ", about " + N(tokens) + " tokens"
                  + (pick.OverLimit ? " or more" : "") + "." + Skipped(pick);

            if (_bankProblem != null) problems.Add(_bankProblem);

            _lblFigures.Text = "";
            if (_bankProblem == null && _figures != null)
            {
                var ownAfter = _body == null ? _figures.Own
                    : ReferenceTranslations.OwnAfter(_figures, _briefText, _body);
                var shared = IsShared ? 0 : _figures.Shared;
                _lblFigures.Text = "“" + _bank + "”: about " + N(_figures.Own) + " tokens of its own now, "
                    + N(ownAfter) + " after."
                    + (shared > 0 ? " With _shared: " + N(_figures.Own + shared) + " now, " + N(ownAfter + shared)
                        + " after, before each document is narrowed down." : "");

                var existing = ReferenceTranslations.ExistingSection(_briefText, out var sections);
                if (sections > 1)
                    problems.Add("brief.md has " + sections + " sections headed “## " + ReferenceTranslations.Heading
                        + "”, so it is not clear which to replace. Remove one in Obsidian or in Settings → Library first.");
                else if (existing != null && _body != null)
                    warnings.Add("Replaces the reference translations already in brief.md (about "
                        + N(ReferenceTranslations.Tokens(existing)) + " tokens).");

                // A section that only shrinks an already over-large bank is let through.
                if (_body != null && (pick.OverLimit
                    || (ownAfter > ReferenceTranslations.BudgetTokens && ownAfter > _figures.Own)))
                    problems.Add("Too large: “" + _bank + "” would come to about " + N(ownAfter)
                        + (pick.OverLimit ? " tokens or more" : " tokens") + " of its own, over the "
                        + N(ReferenceTranslations.BudgetTokens) + " a translation sends, so parts of it would be cut from "
                        + "every prompt. Choose fewer segments.");
                else if (tokens > ReferenceTranslations.WarnTokens)
                    warnings.Add("Large: these " + N(tokens) + " tokens go with every translation request from this bank "
                        + "– about " + N(tokens * 20) + " for a 400-segment document translated in batches of 20.");

                if (_body != null && _figures.Own + shared <= BankExtract.Threshold
                    && ownAfter + shared > BankExtract.Threshold)
                    warnings.Add("This takes the bank past " + N(BankExtract.Threshold) + " tokens. From then on, each document "
                        + "is narrowed down to what it needs, with one small AI request the first time it is translated.");

                if (_figures.BriefAssistantOnly)
                    warnings.Add("brief.md is marked audience: assistant, so translations leave it out: these segments would "
                        + "reach the AI Assistant chat but not Batch Translate or Alt+T.");
            }

            if (!string.IsNullOrEmpty(_bank) && !IsShared && !IsActive)
                warnings.Add("“" + _bank + "” is not the active memory bank: nothing in it reaches a prompt until you "
                    + "switch to it on the SuperMemory bar.");
            if (IsShared)
                warnings.Add("_shared is loaded with every memory bank: these segments would go with every job, for every client.");

            var teamProblem = SupervertalerPaths.TeamFolderProblem;
            if (teamProblem != null)
                warnings.Add(teamProblem + " This goes into your own copy of the bank, which the rest of your team does not see.");

            if (_fileNote != null) warnings.Add(_fileNote);

            // A refusal on its own: the rest only matters once it is put right.
            var lines = problems.Count > 0 ? problems : warnings;
            _lblWarnings.Text = string.Join("\n\n", lines);
            _lblWarnings.ForeColor = problems.Count > 0 ? Red : Amber;
            _lblWarnings.Visible = lines.Count > 0;
            _lblFigures.Visible = _lblFigures.Text.Length > 0;

            _txtPreview.Text = _body == null ? "" : ("## " + ReferenceTranslations.Heading + "\n\n" + _body).Replace("\n", "\r\n");
            _btnAdd.Enabled = problems.Count == 0 && _body != null;
        }

        private static string NothingToAdd(ReferencePick pick)
        {
            var why = new List<string>();
            if (pick.SkippedUnconfirmed > 0) why.Add(Count(pick.SkippedUnconfirmed, "is", "are") + " not confirmed");
            if (pick.SkippedEmpty > 0) why.Add(Count(pick.SkippedEmpty, "has", "have") + " no translation");
            var tail = pick.SkippedUnconfirmed > 0 ? " Untick “Only confirmed segments” to include unconfirmed ones." : "";
            return "Nothing to add: of the " + Count(pick.InRange, "segment") + " in the range, "
                + string.Join(" and ", why) + "." + tail;
        }

        private static string Skipped(ReferencePick pick)
        {
            var parts = new List<string>();
            if (pick.SkippedUnconfirmed > 0) parts.Add(N(pick.SkippedUnconfirmed) + " not confirmed");
            if (pick.SkippedEmpty > 0) parts.Add(N(pick.SkippedEmpty) + " without a translation");
            return parts.Count == 0 ? "" : " Left out: " + string.Join(", ", parts) + ".";
        }

        // ── The write ────────────────────────────────────────────────

        private void Add()
        {
            // Enter in a number box presses Add before the typed number is the
            // box's value: commit every field first, so what is written is what
            // the window shows, and a range typed past the budget is refused.
            ValidateChildren();
            Recompute();
            if (_body == null || _bankProblem != null || !_btnAdd.Enabled) return;

            if (IsShared && MessageBox.Show(this,
                    "Write these segments to _shared?\n\n_shared is loaded with every memory bank, so they would go "
                    + "with every translation, for every client, until you remove them.",
                    "Supervertaler – SuperMemory", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            var existed = ReferenceTranslations.ExistingSection(_briefText, out _) != null;
            BankFileResult r;
            try
            {
                r = BankFileStore.ForUser().ReplaceSection(_bank, MemoryBankReader.BriefFile,
                    ReferenceTranslations.Heading, _body, _briefVersion ?? BankFileStore.NewFile, allowShared: IsShared);
            }
            catch (Exception ex)
            {
                r = null;
                Fail("Could not write to brief.md in “" + _bank + "”: " + ex.Message);
            }
            if (r == null) return;

            if (r.Ok)
            {
                Written = r;
                WrittenSegments = _pick.Segments.Count;
                WrittenTokens = ReferenceTranslations.Tokens(_body);
                ReplacedExisting = existed;
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            if (r.Conflict)
            {
                // Read again, so the figures and the next Add are against the file
                // as it is now. Nothing was written over the other change.
                LoadBank();
                Recompute();
                Fail("brief.md in “" + _bank + "” was changed by another program since this window opened – "
                    + "in Obsidian, say, or by a colleague – so nothing was written.\n\nThis window now shows the file as it "
                    + "is. Check it, then click Add again.");
                return;
            }

            LoadBank();
            Recompute();
            Fail("Nothing was written to brief.md in “" + _bank + "”.\n\n" + r.Error);
        }

        private void Fail(string message) =>
            MessageBox.Show(this, message, "Supervertaler – SuperMemory", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        // ── Building blocks ──────────────────────────────────────────

        private static string FileOf(ComboBox combo) => combo?.SelectedItem as string;

        private static Label Caption(string text) => new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = Grey,
            Margin = new Padding(0, 0, 0, 3)
        };

        private static Label Wrapped(string text, Color color, Font font, Padding margin)
        {
            var l = new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(ContentWidth, 0),
                ForeColor = color,
                Margin = margin
            };
            if (font != null) l.Font = font;
            return l;
        }

        private static NumericUpDown Number(int max, int value) => new NumericUpDown
        {
            Minimum = 1,
            Maximum = max,
            Value = Math.Max(1, Math.Min(max, value)),
            Width = 80,
            Margin = new Padding(0, 0, 0, 4)
        };

        private static ComboBox FileList(List<string> files, string selected)
        {
            var c = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 330,
                Margin = new Padding(0, 0, 8, 4)
            };
            foreach (var f in files) c.Items.Add(f);
            var i = selected == null ? 0 : files.IndexOf(selected);
            c.SelectedIndex = i < 0 ? 0 : i;
            return c;
        }

        private static void AddRangeRow(TableLayoutPanel t, string label, ComboBox file, NumericUpDown number)
        {
            t.Controls.Add(new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 0, 8, 4)
            });
            if (file != null) t.Controls.Add(file);
            t.Controls.Add(number);
        }

        private static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

        private static string Count(int n, string noun) => N(n) + " " + noun + (n == 1 ? "" : "s");

        private static string Count(int n, string singularVerb, string pluralVerb) =>
            N(n) + " " + (n == 1 ? singularVerb : pluralVerb);
    }
}
