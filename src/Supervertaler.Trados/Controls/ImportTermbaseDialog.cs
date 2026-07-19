using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// Maps an external MultiTerm termbase (.sdltb / .ttb) onto a Supervertaler termbase:
    /// pick the source/target language, choose a destination termbase (new or existing),
    /// and map each descriptive field onto a Supervertaler column. Writes via
    /// <see cref="TermbaseImporter"/>. Concept-oriented entries are flattened to bilingual
    /// pairs; extra terms per language become synonyms.
    /// </summary>
    public class ImportTermbaseDialog : Form
    {
        private readonly ImportedTermbase _tb;
        private readonly string _dbPath;
        private readonly List<TermbaseInfo> _existing;
        private readonly string _projectSourceLanguage;

        private ComboBox _cboSource;
        private ComboBox _cboTarget;
        private Label _lblSourceCode;
        private Label _lblTargetCode;
        private RadioButton _rdoNew;
        private RadioButton _rdoExisting;
        private TextBox _txtNewName;
        private ComboBox _cboExisting;
        private DataGridView _grid;
        private Label _lblPreview;
        private Button _btnImport;

        /// <summary>True if an import was performed.</summary>
        public bool DidImport { get; private set; }

        /// <summary>Summary of the completed import (null if none).</summary>
        public ImportSummary Summary { get; private set; }

        // Friendly labels for the field-target combo, in display order.
        private static readonly (ImportFieldTarget Target, string Label)[] TargetChoices =
        {
            (ImportFieldTarget.Ignore, "(ignore)"),
            (ImportFieldTarget.Definition, "Definition"),
            (ImportFieldTarget.Domain, "Domain"),
            (ImportFieldTarget.Notes, "Notes"),
            (ImportFieldTarget.Context, "Context"),
            (ImportFieldTarget.PartOfSpeech, "Part of speech"),
            (ImportFieldTarget.Url, "URL"),
            (ImportFieldTarget.Client, "Client"),
            (ImportFieldTarget.Project, "Project"),
            (ImportFieldTarget.ForbiddenFlag, "Forbidden flag"),
            (ImportFieldTarget.AppendToNotes, "Append to notes"),
        };

        public ImportTermbaseDialog(ImportedTermbase tb, string dbPath, string projectSourceLanguage = null)
        {
            _tb = tb ?? throw new ArgumentNullException(nameof(tb));
            _dbPath = dbPath;
            _projectSourceLanguage = projectSourceLanguage;
            _existing = LoadExistingTermbases(dbPath);

            BuildUi();
            PopulateLanguages();
            PopulateFieldGrid();
            UpdatePreview();
        }

        private static List<TermbaseInfo> LoadExistingTermbases(string dbPath)
        {
            try
            {
                using (var reader = new TermbaseReader(dbPath))
                {
                    if (reader.Open()) return reader.GetTermbases();
                }
            }
            catch { /* fall through to empty list */ }
            return new List<TermbaseInfo>();
        }

        private void BuildUi()
        {
            Icon = IconHelper.AppIcon;
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Import termbase";
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(640, 540);
            BackColor = Color.White;

            Color labelColor = Color.FromArgb(80, 80, 80);
            int margin = 16;
            int width = ClientSize.Width - margin * 2;
            int y = margin;

            var lblHeader = new Label
            {
                Text = $"Source: {_tb.Name}  ({_tb.Format.ToUpperInvariant()}) — "
                     + $"{_tb.ConceptCount} entries, {_tb.Languages.Count} languages",
                Location = new Point(margin, y),
                Width = width,
                AutoSize = false,
                Height = 20,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(40, 40, 40)
            };
            Controls.Add(lblHeader);
            y += 30;

            // ---- Language mapping ----
            int half = (width - 8) / 2;
            Controls.Add(new Label { Text = "Source language:", Location = new Point(margin, y), AutoSize = true, ForeColor = labelColor });
            Controls.Add(new Label { Text = "Target language:", Location = new Point(margin + half + 8, y), AutoSize = true, ForeColor = labelColor });
            y += 20;

            _cboSource = new ComboBox { Location = new Point(margin, y), Width = half, DropDownStyle = ComboBoxStyle.DropDownList };
            _cboTarget = new ComboBox { Location = new Point(margin + half + 8, y), Width = half, DropDownStyle = ComboBoxStyle.DropDownList };
            _cboSource.SelectedIndexChanged += (s, e) => { UpdateCodeLabels(); UpdatePreview(); };
            _cboTarget.SelectedIndexChanged += (s, e) => { UpdateCodeLabels(); UpdatePreview(); };
            Controls.Add(_cboSource);
            Controls.Add(_cboTarget);
            y += 26;

            _lblSourceCode = new Label { Location = new Point(margin, y), Width = half, AutoSize = false, Height = 16, ForeColor = Color.Gray, Font = new Font("Segoe UI", 8f) };
            _lblTargetCode = new Label { Location = new Point(margin + half + 8, y), Width = half, AutoSize = false, Height = 16, ForeColor = Color.Gray, Font = new Font("Segoe UI", 8f) };
            Controls.Add(_lblSourceCode);
            Controls.Add(_lblTargetCode);
            y += 26;

            // ---- Destination ----
            Controls.Add(new Label { Text = "Destination termbase:", Location = new Point(margin, y), AutoSize = true, ForeColor = labelColor });
            y += 22;

            _rdoNew = new RadioButton { Text = "Create new:", Location = new Point(margin, y), AutoSize = true, Checked = true };
            _txtNewName = new TextBox { Location = new Point(margin + 110, y - 2), Width = width - 110, Text = _tb.Name, BackColor = Color.FromArgb(250, 250, 250) };
            _rdoNew.CheckedChanged += (s, e) => UpdateDestinationEnabled();
            Controls.Add(_rdoNew);
            Controls.Add(_txtNewName);
            y += 28;

            _rdoExisting = new RadioButton { Text = "Import into:", Location = new Point(margin, y), AutoSize = true, Enabled = _existing.Count > 0 };
            _cboExisting = new ComboBox { Location = new Point(margin + 110, y - 2), Width = width - 110, DropDownStyle = ComboBoxStyle.DropDownList, Enabled = false };
            _rdoExisting.CheckedChanged += (s, e) => UpdateDestinationEnabled();
            foreach (var tbi in _existing)
                _cboExisting.Items.Add($"{tbi.Name}  ({tbi.SourceLang} → {tbi.TargetLang})");
            if (_existing.Count > 0) _cboExisting.SelectedIndex = 0;
            Controls.Add(_rdoExisting);
            Controls.Add(_cboExisting);
            y += 32;

            // ---- Field mapping ----
            Controls.Add(new Label { Text = "Field mapping:", Location = new Point(margin, y), AutoSize = true, ForeColor = labelColor });
            y += 20;

            _grid = new DataGridView
            {
                Location = new Point(margin, y),
                Width = width,
                Height = 190,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                EditMode = DataGridViewEditMode.EditOnEnter
            };
            var colField = new DataGridViewTextBoxColumn { HeaderText = "MultiTerm field", ReadOnly = true, FillWeight = 30, MinimumWidth = 120 };
            var colSample = new DataGridViewTextBoxColumn { HeaderText = "Example", ReadOnly = true, FillWeight = 38, MinimumWidth = 120 };
            var colTarget = new DataGridViewComboBoxColumn { HeaderText = "Import as", FillWeight = 32, MinimumWidth = 150, FlatStyle = FlatStyle.Flat };
            foreach (var choice in TargetChoices) colTarget.Items.Add(choice.Label);
            _grid.Columns.AddRange(colField, colSample, colTarget);
            Controls.Add(_grid);
            y += 200;

            _lblPreview = new Label { Location = new Point(margin, y), Width = width, AutoSize = false, Height = 18, ForeColor = Color.FromArgb(60, 60, 60) };
            Controls.Add(_lblPreview);

            // ---- Buttons ----
            _btnImport = new Button
            {
                Text = "Import",
                Location = new Point(ClientSize.Width - 170, ClientSize.Height - 40),
                Width = 75,
                FlatStyle = FlatStyle.System
            };
            _btnImport.Click += OnImportClick;
            var btnCancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(ClientSize.Width - 88, ClientSize.Height - 40),
                Width = 75,
                FlatStyle = FlatStyle.System
            };
            Controls.Add(_btnImport);
            Controls.Add(btnCancel);
            AcceptButton = _btnImport;
            CancelButton = btnCancel;
        }

        private void PopulateLanguages()
        {
            foreach (var lang in _tb.Languages)
            {
                _cboSource.Items.Add(lang.ToString());
                _cboTarget.Items.Add(lang.ToString());
            }

            // Default the source to the active project's source language so the imported
            // termbase's direction matches how it will be used (TermLens matches the
            // project's source language against the termbase's source terms).
            int srcIdx = _tb.Languages.Count > 0 ? 0 : -1;
            var matched = FindLanguageIndexMatching(_projectSourceLanguage);
            if (matched.HasValue) srcIdx = matched.Value;
            int tgtIdx = -1;
            for (int i = 0; i < _tb.Languages.Count; i++)
                if (i != srcIdx) { tgtIdx = i; break; }

            if (srcIdx >= 0) _cboSource.SelectedIndex = srcIdx;
            if (tgtIdx >= 0) _cboTarget.SelectedIndex = tgtIdx;
            else if (_tb.Languages.Count > 0) _cboTarget.SelectedIndex = srcIdx;

            if (_tb.Languages.Count < 2)
            {
                _btnImport.Enabled = false;
                _lblPreview.Text = "This termbase has fewer than two languages — nothing to import.";
            }
            UpdateCodeLabels();
        }

        // Finds the language index whose locale/name matches the given project language
        // (e.g. "nl-NL" or "Dutch (Netherlands)"), comparing on base code and name.
        private int? FindLanguageIndexMatching(string projectLang)
        {
            if (string.IsNullOrWhiteSpace(projectLang)) return null;
            var projBase = BaseToken(projectLang);
            for (int i = 0; i < _tb.Languages.Count; i++)
            {
                var lang = _tb.Languages[i];
                if (!string.IsNullOrEmpty(projBase) && projBase == BaseToken(lang.Locale)) return i;
                if (!string.IsNullOrWhiteSpace(lang.Name)
                    && projectLang.IndexOf(lang.Name, StringComparison.OrdinalIgnoreCase) >= 0) return i;
            }
            return null;
        }

        // The leading token of a language string, lowered (before any -, _, space or "(").
        private static string BaseToken(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            int i = s.IndexOfAny(new[] { '-', '_', ' ', '(' });
            return (i > 0 ? s.Substring(0, i) : s).Trim().ToLowerInvariant();
        }

        private void UpdateCodeLabels()
        {
            var src = SelectedSourceLanguage();
            var tgt = SelectedTargetLanguage();
            _lblSourceCode.Text = src != null ? $"stored as: {LanguageUtils.CanonicalLocale(src.Locale ?? src.Name)}" : "";
            _lblTargetCode.Text = tgt != null ? $"stored as: {LanguageUtils.CanonicalLocale(tgt.Locale ?? tgt.Name)}" : "";
        }

        private void PopulateFieldGrid()
        {
            var suggested = TermbaseImporter.SuggestFieldMap(_tb.DiscoveredFields);
            foreach (var field in _tb.DiscoveredFields)
            {
                var sample = FirstSampleValue(field);
                int idx = _grid.Rows.Add(field, sample, LabelFor(suggested.TryGetValue(field, out var t) ? t : ImportFieldTarget.AppendToNotes));
                _grid.Rows[idx].Cells[0].Tag = field;
            }
            if (_tb.DiscoveredFields.Count == 0)
            {
                _grid.Enabled = false;
            }
        }

        private string FirstSampleValue(string field)
        {
            foreach (var c in _tb.Concepts)
            {
                if (c.Fields.TryGetValue(field, out var v) && !string.IsNullOrWhiteSpace(v)) return Trunc(v);
                foreach (var lf in c.LanguageFields.Values)
                    if (lf.TryGetValue(field, out var lv) && !string.IsNullOrWhiteSpace(lv)) return Trunc(lv);
            }
            return "";
        }

        private static string Trunc(string s) => s.Length > 60 ? s.Substring(0, 60) + "…" : s;

        private ImportLanguage SelectedSourceLanguage() =>
            _cboSource.SelectedIndex >= 0 && _cboSource.SelectedIndex < _tb.Languages.Count
                ? _tb.Languages[_cboSource.SelectedIndex] : null;

        private ImportLanguage SelectedTargetLanguage() =>
            _cboTarget.SelectedIndex >= 0 && _cboTarget.SelectedIndex < _tb.Languages.Count
                ? _tb.Languages[_cboTarget.SelectedIndex] : null;

        private void UpdateDestinationEnabled()
        {
            _txtNewName.Enabled = _rdoNew.Checked;
            _cboExisting.Enabled = _rdoExisting.Checked && _existing.Count > 0;
        }

        private void UpdatePreview()
        {
            var src = SelectedSourceLanguage();
            var tgt = SelectedTargetLanguage();
            if (src == null || tgt == null) { _lblPreview.Text = ""; return; }
            if (src.Id == tgt.Id)
            {
                _lblPreview.Text = "Source and target languages must differ.";
                return;
            }

            int pairs = _tb.Concepts.Count(c =>
                c.TermsFor(src.Id).Count > 0 && c.TermsFor(tgt.Id).Count > 0);
            int skipped = _tb.ConceptCount - pairs;
            _lblPreview.Text = skipped > 0
                ? $"Will import ~{pairs} term pair(s); {skipped} entr(y/ies) have no term on one side and will be skipped."
                : $"Will import ~{pairs} term pair(s).";
        }

        private ImportOptions BuildOptions(long destId)
        {
            var opt = new ImportOptions
            {
                SourceLanguageId = SelectedSourceLanguage().Id,
                TargetLanguageId = SelectedTargetLanguage().Id,
                DestinationTermbaseId = destId
            };
            foreach (DataGridViewRow row in _grid.Rows)
            {
                var field = row.Cells[0].Tag as string;
                if (string.IsNullOrEmpty(field)) continue;
                var label = row.Cells[2].Value as string;
                opt.FieldMap[field] = TargetFor(label);
            }
            return opt;
        }

        private async void OnImportClick(object sender, EventArgs e)
        {
            var src = SelectedSourceLanguage();
            var tgt = SelectedTargetLanguage();
            if (src == null || tgt == null || src.Id == tgt.Id)
            {
                MessageBox.Show("Choose two different languages for source and target.",
                    "Import termbase", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            long destId;
            try
            {
                if (_rdoNew.Checked)
                {
                    var name = _txtNewName.Text.Trim();
                    if (name.Length == 0)
                    {
                        MessageBox.Show("Enter a name for the new termbase.",
                            "Import termbase", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    destId = TermbaseReader.CreateTermbase(_dbPath, name,
                        LanguageUtils.CanonicalLocale(src.Locale ?? src.Name),
                        LanguageUtils.CanonicalLocale(tgt.Locale ?? tgt.Name));
                }
                else
                {
                    var idx = _cboExisting.SelectedIndex;
                    if (idx < 0 || idx >= _existing.Count)
                    {
                        MessageBox.Show("Select a termbase to import into.",
                            "Import termbase", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    destId = _existing[idx].Id;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not prepare the destination termbase:\n{ex.Message}",
                    "Import termbase", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var options = BuildOptions(destId);

            _btnImport.Enabled = false;
            UseWaitCursor = true;
            ImportSummary summary = null;
            Exception error = null;
            try
            {
                summary = await Task.Run(() => TermbaseImporter.Import(_tb, options, _dbPath));
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                UseWaitCursor = false;
                _btnImport.Enabled = true;
            }

            if (error != null)
            {
                MessageBox.Show($"Import failed:\n{error.Message}",
                    "Import termbase", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Summary = summary;
            DidImport = true;

            var msg = $"Imported {summary.Added} term pair(s)"
                    + (summary.SynonymsAdded > 0 ? $" and {summary.SynonymsAdded} synonym(s)" : "")
                    + $".\n\nDuplicates skipped: {summary.Duplicates}";
            if (summary.Warnings.Count > 0)
                msg += "\n\n" + string.Join("\n", summary.Warnings);

            MessageBox.Show(msg, "Import termbase", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
        }

        private static string LabelFor(ImportFieldTarget target) =>
            TargetChoices.First(c => c.Target == target).Label;

        private static ImportFieldTarget TargetFor(string label)
        {
            foreach (var c in TargetChoices)
                if (c.Label == label) return c.Target;
            return ImportFieldTarget.Ignore;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F1)
            {
                HelpSystem.OpenHelp(HelpSystem.Topics.TermbaseEditor);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
