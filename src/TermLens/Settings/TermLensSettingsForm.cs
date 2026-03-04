using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using TermLens.Core;
using TermLens.Models;

namespace TermLens.Settings
{
    /// <summary>
    /// Settings dialog for the TermLens plugin.
    /// Allows the user to select a Supervertaler termbase (.db) file,
    /// choose which termbases to search (Read) and which one receives new terms (Write).
    /// </summary>
    public class TermLensSettingsForm : Form
    {
        private readonly TermLensSettings _settings;

        // Controls
        private TextBox _txtTermbasePath;
        private Button _btnBrowse;
        private Label _lblTermbaseInfo;
        private DataGridView _dgvTermbases;
        private Label _lblTermbasesHeader;
        private CheckBox _chkAutoLoad;
        private Button _btnOK;
        private Button _btnCancel;

        // Cached termbase list from the DB, aligned with DataGridView row indices
        private List<TermbaseInfo> _termbases = new List<TermbaseInfo>();

        public TermLensSettingsForm(TermLensSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            BuildUI();
            PopulateFromSettings();
        }

        private void BuildUI()
        {
            Text = "TermLens Settings";
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 420);
            MinimumSize = new Size(480, 340);
            BackColor = Color.White;

            // === Termbase section ===
            var lblSection = new Label
            {
                Text = "Termbase",
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(50, 50, 50),
                Location = new Point(16, 16),
                AutoSize = true
            };

            var lblPath = new Label
            {
                Text = "Termbase file (.db):",
                Location = new Point(16, 42),
                AutoSize = true,
                ForeColor = Color.FromArgb(80, 80, 80)
            };

            _btnBrowse = new Button
            {
                Text = "Browse...",
                Width = 75,
                Height = 23,
                FlatStyle = FlatStyle.System,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnBrowse.Location = new Point(ClientSize.Width - 16 - _btnBrowse.Width, 58);
            _btnBrowse.Click += OnBrowseClick;

            _txtTermbasePath = new TextBox
            {
                Location = new Point(16, 60),
                ReadOnly = true,
                BackColor = Color.FromArgb(250, 250, 250),
                ForeColor = Color.FromArgb(40, 40, 40),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _txtTermbasePath.Width = _btnBrowse.Left - 16 - 6;

            _lblTermbaseInfo = new Label
            {
                Location = new Point(16, 86),
                AutoSize = false,
                Height = 32,
                ForeColor = Color.FromArgb(100, 100, 100),
                Font = new Font("Segoe UI", 8f),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _lblTermbaseInfo.Width = ClientSize.Width - 32;

            // === Glossary grid (Read / Write columns) ===
            _lblTermbasesHeader = new Label
            {
                Text = "Glossaries:",
                Location = new Point(16, 122),
                AutoSize = true,
                ForeColor = Color.FromArgb(80, 80, 80)
            };

            _dgvTermbases = new DataGridView
            {
                Location = new Point(16, 140),
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                ReadOnly = false,
                BorderStyle = BorderStyle.FixedSingle,
                BackgroundColor = Color.FromArgb(250, 250, 250),
                Font = new Font("Segoe UI", 8.5f),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                EnableHeadersVisualStyles = false
            };
            _dgvTermbases.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(240, 240, 240),
                ForeColor = Color.FromArgb(50, 50, 50),
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                SelectionBackColor = Color.FromArgb(240, 240, 240),
                SelectionForeColor = Color.FromArgb(50, 50, 50)
            };
            _dgvTermbases.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(250, 250, 250),
                ForeColor = Color.FromArgb(40, 40, 40),
                SelectionBackColor = Color.FromArgb(220, 235, 252),
                SelectionForeColor = Color.FromArgb(40, 40, 40)
            };
            _dgvTermbases.Width = ClientSize.Width - 32;
            _dgvTermbases.Height = ClientSize.Height - 140 - 100;

            // Columns
            var colRead = new DataGridViewCheckBoxColumn
            {
                Name = "colRead",
                HeaderText = "Read",
                Width = 54,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                FillWeight = 1
            };
            var colWrite = new DataGridViewCheckBoxColumn
            {
                Name = "colWrite",
                HeaderText = "Write",
                Width = 54,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                FillWeight = 1
            };
            var colProject = new DataGridViewCheckBoxColumn
            {
                Name = "colProject",
                HeaderText = "Project",
                Width = 62,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                FillWeight = 1,
                ToolTipText = "Mark as project glossary (shown in pink)"
            };
            var colName = new DataGridViewTextBoxColumn
            {
                Name = "colName",
                HeaderText = "Termbase",
                ReadOnly = true,
                FillWeight = 40
            };
            var colTermCount = new DataGridViewTextBoxColumn
            {
                Name = "colTermCount",
                HeaderText = "Terms",
                ReadOnly = true,
                Width = 60,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                FillWeight = 1,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Alignment = DataGridViewContentAlignment.MiddleRight
                }
            };
            var colLanguages = new DataGridViewTextBoxColumn
            {
                Name = "colLanguages",
                HeaderText = "Languages",
                ReadOnly = true,
                FillWeight = 20
            };
            _dgvTermbases.Columns.AddRange(new DataGridViewColumn[]
            {
                colRead, colWrite, colProject, colName, colTermCount, colLanguages
            });

            // Enforce radio-button behaviour on the Write column
            _dgvTermbases.CellContentClick += OnGridCellContentClick;

            // === Options section ===
            var sep = new Label
            {
                Location = new Point(16, ClientSize.Height - 90),
                Height = 1,
                BorderStyle = BorderStyle.Fixed3D,
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom | AnchorStyles.Right
            };
            sep.Width = ClientSize.Width - 32;

            _chkAutoLoad = new CheckBox
            {
                Text = "Automatically load termbase when Trados Studio starts",
                Location = new Point(16, ClientSize.Height - 80),
                AutoSize = true,
                ForeColor = Color.FromArgb(60, 60, 60),
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom
            };

            // === OK / Cancel ===
            _btnOK = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Location = new Point(ClientSize.Width - 170, ClientSize.Height - 40),
                Width = 75,
                FlatStyle = FlatStyle.System,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            _btnOK.Click += OnOKClick;

            _btnCancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(ClientSize.Width - 88, ClientSize.Height - 40),
                Width = 75,
                FlatStyle = FlatStyle.System,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };

            AcceptButton = _btnOK;
            CancelButton = _btnCancel;

            Controls.AddRange(new Control[]
            {
                lblSection, lblPath, _txtTermbasePath, _btnBrowse,
                _lblTermbaseInfo, _lblTermbasesHeader, _dgvTermbases,
                sep, _chkAutoLoad,
                _btnOK, _btnCancel
            });
        }

        private void OnGridCellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.ColumnIndex < 0 || e.RowIndex < 0) return;

            if (_dgvTermbases.Columns[e.ColumnIndex].Name == "colWrite")
            {
                // Commit the edit so .Value is up-to-date
                _dgvTermbases.CommitEdit(DataGridViewDataErrorContexts.Commit);

                var clicked = _dgvTermbases.Rows[e.RowIndex].Cells["colWrite"].Value as bool? ?? false;

                if (clicked)
                {
                    // Radio-button: uncheck all other rows
                    foreach (DataGridViewRow row in _dgvTermbases.Rows)
                    {
                        if (row.Index != e.RowIndex)
                            row.Cells["colWrite"].Value = false;
                    }
                }
            }
        }

        private void PopulateFromSettings()
        {
            _txtTermbasePath.Text = _settings.TermbasePath ?? "";
            _chkAutoLoad.Checked = _settings.AutoLoadOnStartup;
            UpdateTermbaseInfo(_settings.TermbasePath);
            PopulateTermbaseList(_settings.TermbasePath);
        }

        private void OnBrowseClick(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Select Supervertaler Termbase";
                dlg.Filter = "Supervertaler Termbase (*.db)|*.db|All files (*.*)|*.*";
                dlg.FilterIndex = 1;

                var current = _txtTermbasePath.Text;
                if (!string.IsNullOrEmpty(current) && File.Exists(current))
                    dlg.InitialDirectory = Path.GetDirectoryName(current);

                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _txtTermbasePath.Text = dlg.FileName;
                    UpdateTermbaseInfo(dlg.FileName);
                    PopulateTermbaseList(dlg.FileName);
                }
            }
        }

        private void UpdateTermbaseInfo(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                _lblTermbaseInfo.Text = string.IsNullOrEmpty(path)
                    ? "No termbase selected."
                    : "File not found.";
                _lblTermbaseInfo.ForeColor = Color.FromArgb(160, 160, 160);
                return;
            }

            try
            {
                using (var reader = new TermbaseReader(path))
                {
                    if (!reader.Open())
                    {
                        _lblTermbaseInfo.Text = $"Could not open: {reader.LastError}";
                        _lblTermbaseInfo.ForeColor = Color.FromArgb(180, 60, 60);
                        return;
                    }

                    var termbases = reader.GetTermbases();
                    int total = 0;
                    foreach (var tb in termbases) total += tb.TermCount;

                    _lblTermbaseInfo.Text = termbases.Count == 1
                        ? $"\u2713  {termbases[0].Name}  \u2014  {total:N0} terms  ({termbases[0].SourceLang} \u2192 {termbases[0].TargetLang})"
                        : $"\u2713  {termbases.Count} termbases, {total:N0} terms total";

                    _lblTermbaseInfo.ForeColor = Color.FromArgb(30, 130, 60);
                }
            }
            catch
            {
                _lblTermbaseInfo.Text = "Error reading termbase.";
                _lblTermbaseInfo.ForeColor = Color.FromArgb(180, 60, 60);
            }
        }

        private void PopulateTermbaseList(string path)
        {
            _dgvTermbases.Rows.Clear();
            _termbases.Clear();

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;

            try
            {
                using (var reader = new TermbaseReader(path))
                {
                    if (!reader.Open())
                        return;

                    _termbases = reader.GetTermbases();
                    var disabled = new HashSet<long>(_settings.DisabledTermbaseIds ?? new List<long>());
                    var projectIds = new HashSet<long>(_settings.ProjectTermbaseIds ?? new List<long>());

                    foreach (var tb in _termbases)
                    {
                        bool isRead = !disabled.Contains(tb.Id);
                        bool isWrite = tb.Id == _settings.WriteTermbaseId;
                        bool isProject = projectIds.Contains(tb.Id);
                        _dgvTermbases.Rows.Add(
                            isRead,
                            isWrite,
                            isProject,
                            tb.Name,
                            tb.TermCount.ToString("N0"),
                            $"{tb.SourceLang} \u2192 {tb.TargetLang}");
                    }
                }
            }
            catch
            {
                // If we can't read the DB, just leave the grid empty
            }
        }

        private void OnOKClick(object sender, EventArgs e)
        {
            _settings.TermbasePath = _txtTermbasePath.Text.Trim();
            _settings.AutoLoadOnStartup = _chkAutoLoad.Checked;

            // Build disabled list, write ID, and project IDs from grid cells
            _settings.DisabledTermbaseIds = new List<long>();
            _settings.WriteTermbaseId = -1;
            _settings.ProjectTermbaseIds = new List<long>();

            for (int i = 0; i < _termbases.Count; i++)
            {
                var readChecked = _dgvTermbases.Rows[i].Cells["colRead"].Value as bool? ?? false;
                var writeChecked = _dgvTermbases.Rows[i].Cells["colWrite"].Value as bool? ?? false;
                var projectChecked = _dgvTermbases.Rows[i].Cells["colProject"].Value as bool? ?? false;

                if (!readChecked)
                    _settings.DisabledTermbaseIds.Add(_termbases[i].Id);
                if (writeChecked)
                    _settings.WriteTermbaseId = _termbases[i].Id;
                if (projectChecked)
                    _settings.ProjectTermbaseIds.Add(_termbases[i].Id);
            }

            _settings.Save();
        }
    }
}
