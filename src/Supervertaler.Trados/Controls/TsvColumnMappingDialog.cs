using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// Shown on TSV import, after the header row has been read (#94): one row per
    /// column in the file, with a sample of what is in it and a dropdown saying
    /// which termbase field it goes to. Pre-filled from the automatic matching
    /// (#93), so a file we exported ourselves is one glance and OK - and a file
    /// with no language headers, or with target before source, or with a notes
    /// column called something unexpected, is a choice rather than a guess.
    ///
    /// The result is <see cref="Mapping"/>: field key to column index, the same
    /// shape the importer builds for itself when no mapping is supplied.
    /// </summary>
    internal sealed class TsvColumnMappingDialog : Form
    {
        // Field keys as ImportTsv reads them, and the labels the user sees.
        private const string IgnoreLabel = "— ignore —";
        private static readonly (string Key, string Label)[] FieldList =
        {
            ("source",     "Source term"),
            ("target",     "Target term"),
            ("uuid",       "Term UUID"),
            ("priority",   "Priority"),
            ("domain",     "Domain"),
            ("definition", "Definition"),
            ("notes",      "Notes"),
            ("project",    "Project"),
            ("client",     "Client"),
            ("forbidden",  "Forbidden (yes/no)"),
        };

        private readonly string _srcName, _tgtName;
        private readonly TableLayoutPanel _layout;
        private readonly Label _lblIntro;
        private readonly DataGridView _grid;
        private readonly Label _lblNote;
        private readonly Button _btnOk, _btnCancel;
        private readonly Dictionary<string, string> _labelToKey = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _keyToLabel = new Dictionary<string, string>();
        private const int MaxSamples = 3;

        /// <summary>Field key to zero-based column index, for every column not ignored. Set on OK.</summary>
        public Dictionary<string, int> Mapping { get; private set; }

        /// <summary>
        /// The frame: file, destination, and a note about what the headers said.
        /// Call <see cref="SetColumns"/> before showing; without it the grid is empty
        /// (the layout probe constructs the dialog this way).
        /// </summary>
        public TsvColumnMappingDialog(string fileName, int rowCount, string termbaseName,
            string srcName, string tgtName, string note)
        {
            _srcName = srcName ?? "source";
            _tgtName = tgtName ?? "target";
            foreach (var f in FieldList)
            {
                var label = f.Key == "source" ? $"Source term ({_srcName})"
                          : f.Key == "target" ? $"Target term ({_tgtName})"
                          : f.Label;
                _labelToKey[label] = f.Key;
                _keyToLabel[f.Key] = label;
            }

            Icon = IconHelper.AppIcon;
            Text = "Import from TSV – columns";
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = true;
            ShowInTaskbar = false;
            ClientSize = new Size(760, 440);
            MinimumSize = new Size(560, 340);

            _layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(12),
            };
            _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // intro
            _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));  // grid
            _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // note
            _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // buttons

            var rows = rowCount > 0 ? $"{rowCount:N0} row{(rowCount == 1 ? "" : "s")} from " : "";
            _lblIntro = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(ClientSize.Width - 24, 0),
                Dock = DockStyle.Top,
                Margin = new Padding(0, 0, 0, 8),
                Text = $"Importing {rows}“{fileName}” into “{termbaseName}” ({_srcName} → {_tgtName}).\r\n" +
                       "Each row below is one column of the file. Choose what each column is; " +
                       "the suggestion comes from the file’s headers.",
            };

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle,
                EditMode = DataGridViewEditMode.EditOnEnter,
                Margin = new Padding(0),
            };
            _grid.ColumnHeadersDefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
            _grid.DataError += (s, e) => { e.ThrowException = false; };
            // One click opens the dropdown, rather than click-to-select then click-to-open.
            _grid.CellEnter += (s, e) =>
            {
                if (e.RowIndex >= 0 && _grid.Columns[e.ColumnIndex] is DataGridViewComboBoxColumn)
                    BeginInvoke((Action)(() => { try { _grid.BeginEdit(true); } catch { } }));
            };
            _grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };

            _lblNote = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(ClientSize.Width - 24, 0),
                Dock = DockStyle.Top,
                Margin = new Padding(0, 8, 0, 0),
                ForeColor = Color.FromArgb(150, 90, 0),
                Text = note ?? "",
                Visible = !string.IsNullOrEmpty(note),
            };

            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 12, 0, 0),
                WrapContents = false,
            };
            _btnCancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(88, 28), DialogResult = DialogResult.Cancel };
            _btnOk = new Button { Text = "Import", AutoSize = true, MinimumSize = new Size(88, 28) };
            _btnOk.Click += OnOk;
            buttons.Controls.Add(_btnCancel);
            buttons.Controls.Add(_btnOk);

            _layout.Controls.Add(_lblIntro, 0, 0);
            _layout.Controls.Add(_grid, 0, 1);
            _layout.Controls.Add(_lblNote, 0, 2);
            _layout.Controls.Add(buttons, 0, 3);
            Controls.Add(_layout);

            AcceptButton = _btnOk;
            CancelButton = _btnCancel;

            Resize += (s, e) =>
            {
                _lblIntro.MaximumSize = new Size(Math.Max(200, ClientSize.Width - 24), 0);
                _lblNote.MaximumSize = _lblIntro.MaximumSize;
            };
        }

        /// <summary>
        /// Fills the grid: one row per header, up to three sample values, and the
        /// suggested destination (or ignore) for each.
        /// </summary>
        public void SetColumns(string[] headers, IList<string[]> sampleRows, IDictionary<string, int> suggested)
        {
            headers = headers ?? new string[0];
            sampleRows = sampleRows ?? new List<string[]>();
            int sampleCount = Math.Min(MaxSamples, sampleRows.Count);

            _grid.Columns.Clear();
            var colHeader = new DataGridViewTextBoxColumn
            {
                HeaderText = "Column in file", ReadOnly = true, FillWeight = 22, MinimumWidth = 90,
            };
            _grid.Columns.Add(colHeader);
            for (int s = 0; s < sampleCount; s++)
            {
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = sampleCount == 1 ? "Sample" : $"Sample {s + 1}",
                    ReadOnly = true, FillWeight = 18, MinimumWidth = 70,
                });
            }
            var colField = new DataGridViewComboBoxColumn
            {
                HeaderText = "Import as", FillWeight = 26, MinimumWidth = 150,
                FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
            };
            colField.Items.Add(IgnoreLabel);
            foreach (var f in FieldList) colField.Items.Add(_keyToLabel[f.Key]);
            _grid.Columns.Add(colField);

            // Invert the suggestion: column index -> field key.
            var byColumn = new Dictionary<int, string>();
            if (suggested != null)
                foreach (var kv in suggested)
                    if (kv.Value >= 0 && kv.Value < headers.Length && !byColumn.ContainsKey(kv.Value))
                        byColumn[kv.Value] = kv.Key;

            _grid.Rows.Clear();
            for (int c = 0; c < headers.Length; c++)
            {
                var cells = new List<object> { string.IsNullOrWhiteSpace(headers[c]) ? $"(column {c + 1})" : headers[c].Trim() };
                for (int s = 0; s < sampleCount; s++)
                {
                    var row = sampleRows[s];
                    cells.Add(row != null && c < row.Length ? Shorten(row[c]) : "");
                }
                cells.Add(byColumn.TryGetValue(c, out var key) && _keyToLabel.ContainsKey(key) ? _keyToLabel[key] : IgnoreLabel);
                _grid.Rows.Add(cells.ToArray());
                var mapped = byColumn.ContainsKey(c);
                _grid.Rows[c].DefaultCellStyle.ForeColor = mapped ? SystemColors.ControlText : Color.FromArgb(120, 120, 120);
            }
            _grid.ClearSelection();
        }

        private static string Shorten(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= 48 ? s : s.Substring(0, 48) + "…";
        }

        /// <summary>Reads the grid into a mapping; refuses one that cannot be imported.</summary>
        private void OnOk(object sender, EventArgs e)
        {
            try { _grid.EndEdit(); } catch { }

            var mapping = new Dictionary<string, int>();
            var duplicates = new List<string>();
            int fieldCol = _grid.Columns.Count - 1;
            for (int r = 0; r < _grid.Rows.Count; r++)
            {
                var label = _grid.Rows[r].Cells[fieldCol].Value as string;
                if (string.IsNullOrEmpty(label) || label == IgnoreLabel) continue;
                if (!_labelToKey.TryGetValue(label, out var key)) continue;
                if (mapping.ContainsKey(key)) { duplicates.Add(label); continue; }
                mapping[key] = r;
            }

            if (duplicates.Count > 0)
            {
                MessageBox.Show(this,
                    "Two columns are set to the same field: " + string.Join(", ", duplicates.Distinct()) +
                    ".\r\n\r\nEach field can take one column. Set the other to “" + IgnoreLabel + "”.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!mapping.ContainsKey("source") || !mapping.ContainsKey("target"))
            {
                MessageBox.Show(this,
                    $"Choose which column holds the source term ({_srcName}) and which the target term ({_tgtName}). " +
                    "Nothing can be imported without both.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Mapping = mapping;
            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>
        /// The one-line note under the grid, from what the headers said (#93). The
        /// grid already shows the mapping, so this only says what is worth knowing:
        /// that the file points the other way, or belongs to another pair, or gives
        /// no language at all.
        /// </summary>
        public static string NoteFor(TermbaseReader.TsvColumnOrigin origin, string fileSrcHeader, string fileTgtHeader,
            string srcName, string tgtName)
        {
            switch (origin)
            {
                case TermbaseReader.TsvColumnOrigin.Swapped:
                    return $"This file is {fileTgtHeader} → {fileSrcHeader}, the other way round from this termbase. " +
                           "The suggestion swaps its two columns so each term lands on the side it belongs to.";
                case TermbaseReader.TsvColumnOrigin.Mismatch:
                    return $"This file is {fileSrcHeader} → {fileTgtHeader}, which is not this termbase’s pair " +
                           $"({srcName} → {tgtName}). Check you picked the right termbase before importing.";
                case TermbaseReader.TsvColumnOrigin.NamedColumns:
                    return "The headers say “Source” and “Target” but not which language is which, " +
                           "so the suggestion follows the file’s order. Check it against the samples.";
                case TermbaseReader.TsvColumnOrigin.Ambiguous:
                    return $"Both sides of this termbase are {srcName}, so the headers cannot say which column is which. " +
                           "The suggestion follows the file’s order.";
                case TermbaseReader.TsvColumnOrigin.Positional:
                    return "The headers do not name this termbase’s languages, so the suggestion follows the " +
                           "file’s order. Check it against the samples.";
                case TermbaseReader.TsvColumnOrigin.Unresolved:
                    return "No source and target columns were recognised. Choose them here.";
                default:
                    return null;
            }
        }
    }
}
