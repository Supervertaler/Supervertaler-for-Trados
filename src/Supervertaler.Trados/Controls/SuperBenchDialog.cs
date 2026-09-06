using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Supervertaler.Core;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// SuperBench (#107): pick three models and a judge, translate the first N
    /// segments of the open document with each under the batch's own settings, and
    /// read the judge's blind verdict beside the translations. Nothing is written
    /// to the document. The report is saved under trados\reports like a proofread.
    /// </summary>
    internal sealed class SuperBenchDialog : Form
    {
        private readonly Func<SuperBenchInputs> _prepare;   // on the UI thread; reads the open document
        private readonly AiSettings _settings;
        private SuperBenchInputs _inputs;
        private SuperBench.Run _run;
        private string _savedPath;
        private CancellationTokenSource _cts;

        private readonly TableLayoutPanel _layout;
        private readonly Label _lblIntro;
        private readonly List<(ComboBox provider, ComboBox model)> _slots = new List<(ComboBox, ComboBox)>();
        private readonly ComboBox _cboJudgeProvider, _cboJudgeModel;
        private readonly NumericUpDown _nudSegments;
        private readonly Label _lblEstimate;
        private readonly Button _btnRun, _btnCancel, _btnSave, _btnFolder, _btnClose;
        private readonly Label _lblStatus;
        private readonly SplitContainer _split;
        private readonly DataGridView _grid;
        private readonly TextBox _txtReport;

        private sealed class ProviderItem
        {
            public string Key;
            public override string ToString() => LlmModels.GetProviderDisplayName(Key);
        }
        private sealed class ModelItem
        {
            public LlmModelInfo Info;
            public override string ToString() => Info.DisplayName;
        }

        /// <summary>For the layout probe.</summary>
        public SuperBenchDialog() : this(null, null, "(document)", 40) { }

        public SuperBenchDialog(Func<SuperBenchInputs> prepare, AiSettings settings, string documentName, int availableSegments)
        {
            _prepare = prepare;
            _settings = settings ?? new AiSettings();

            Icon = IconHelper.AppIcon;
            Text = "SuperBench";
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = true;
            ShowInTaskbar = false;
            ClientSize = new Size(1000, 700);
            MinimumSize = new Size(760, 520);

            _layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 9, Padding = new Padding(12) };
            _layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            for (int i = 0; i < 7; i++) _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));   // results
            _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // status

            int row = 0;
            _lblIntro = new Label
            {
                AutoSize = true, MaximumSize = new Size(ClientSize.Width - 24, 0), Margin = new Padding(0, 0, 0, 8),
                Text = $"Translate the first segments of “{documentName}” with three models under exactly the settings a batch would use " +
                       "– the selected prompt, the termbase terms that occur, document context – then have a judge compare the three " +
                       "blind and say which to use for this project. Nothing is written to the document.",
            };
            _layout.Controls.Add(_lblIntro, 0, row); _layout.SetColumnSpan(_lblIntro, 2); row++;

            var providerKeys = LlmModels.AllProviderKeys.Where(k => k != LlmModels.ProviderCustomOpenAi).ToList();
            var defaults = new[]
            {
                (LlmModels.ProviderClaude, "claude-opus-5"),
                (LlmModels.ProviderOpenAi, "gpt-5.6-sol"),
                (LlmModels.ProviderGemini, "gemini-3.1-pro-preview"),
            };
            for (int i = 0; i < 3; i++)
            {
                var pair = MakeModelRow(providerKeys, defaults[i].Item1, defaults[i].Item2);
                _slots.Add(pair);
                _layout.Controls.Add(L("Model " + (char)('A' + i) + ":"), 0, row);
                _layout.Controls.Add(Host(pair.provider, pair.model), 1, row); row++;
            }
            var judge = MakeModelRow(providerKeys, LlmModels.ProviderClaude, "claude-fable-5-1");
            _cboJudgeProvider = judge.provider; _cboJudgeModel = judge.model;
            _layout.Controls.Add(L("Judge:"), 0, row);
            _layout.Controls.Add(Host(judge.provider, judge.model), 1, row); row++;

            _nudSegments = new NumericUpDown
            {
                Minimum = 1, Maximum = Math.Max(1, availableSegments), Value = Math.Min(20, Math.Max(1, availableSegments)),
                Width = 80, Margin = new Padding(0, 3, 8, 3), Anchor = AnchorStyles.Left,
            };
            _nudSegments.ValueChanged += (s, e) => UpdateEstimate();
            _lblEstimate = new Label { AutoSize = true, ForeColor = Color.FromArgb(100, 100, 100), Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 0, 0) };
            var segHost = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
            segHost.Controls.Add(_nudSegments);
            segHost.Controls.Add(_lblEstimate);
            _layout.Controls.Add(L("Segments:"), 0, row);
            _layout.Controls.Add(segHost, 1, row); row++;

            var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 6, 0, 6) };
            _btnRun = Btn("Run SuperBench"); _btnRun.Click += async (s, e) => await RunAsync();
            _btnCancel = Btn("Cancel"); _btnCancel.Enabled = false; _btnCancel.Click += (s, e) => _cts?.Cancel();
            _btnSave = Btn("Save report…"); _btnSave.Enabled = false; _btnSave.Click += OnSave;
            _btnFolder = Btn("Open reports folder"); _btnFolder.Click += (s, e) => OpenFolder();
            _btnClose = Btn("Close"); _btnClose.Click += (s, e) => Close();
            foreach (var b in new[] { _btnRun, _btnCancel, _btnSave, _btnFolder, _btnClose }) buttons.Controls.Add(b);
            // Contextual help: the docs page for this dialog (also F1). A LinkLabel rather
            // than the title-bar HelpButton, which WinForms only shows when the window
            // cannot be maximised - and a results table wants to be maximised.
            var lnkHelp = new LinkLabel
            {
                Text = "? Help", AutoSize = true, Margin = new Padding(8, 7, 0, 0),
                LinkBehavior = LinkBehavior.HoverUnderline,
            };
            lnkHelp.LinkClicked += (s, e) => HelpSystem.OpenHelp(HelpSystem.Topics.SuperBench);
            new ToolTip().SetToolTip(lnkHelp, "Open the SuperBench page in the online documentation (F1)");
            buttons.Controls.Add(lnkHelp);
            _layout.Controls.Add(buttons, 0, row); _layout.SetColumnSpan(buttons, 2); row++;

            _split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Margin = new Padding(0), SplitterWidth = 6 };
            _grid = new DataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells, BackgroundColor = SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle, SelectionMode = DataGridViewSelectionMode.CellSelect,
            };
            _grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            _grid.ColumnHeadersDefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
            _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            _txtReport = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                Font = new Font("Segoe UI", 9.5f), BackColor = Color.FromArgb(252, 252, 248), WordWrap = true,
            };
            _split.Panel1.Controls.Add(_grid);
            _split.Panel2.Controls.Add(_txtReport);
            _layout.Controls.Add(_split, 0, row); _layout.SetColumnSpan(_split, 2); row++;

            _lblStatus = new Label { AutoSize = true, ForeColor = Color.FromArgb(100, 100, 100), Margin = new Padding(0, 6, 0, 0), MaximumSize = new Size(ClientSize.Width - 24, 0) };
            _layout.Controls.Add(_lblStatus, 0, row); _layout.SetColumnSpan(_lblStatus, 2); row++;

            Controls.Add(_layout);
            Resize += (s, e) =>
            {
                _lblIntro.MaximumSize = new Size(Math.Max(300, ClientSize.Width - 24), 0);
                _lblStatus.MaximumSize = _lblIntro.MaximumSize;
            };
            Shown += (s, e) =>
            {
                try { _split.SplitterDistance = Math.Max(120, _split.Height * 55 / 100); } catch { }
                if (_prepare != null) PrepareInputs();
            };
            SetGridColumns(null);
        }

        private static Label L(string text) => new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 8, 0), ForeColor = Color.FromArgb(80, 80, 80) };
        private static Button Btn(string text) => new Button { Text = text, AutoSize = true, MinimumSize = new Size(96, 28), FlatStyle = FlatStyle.System, Margin = new Padding(0, 0, 8, 0) };

        private static Control Host(ComboBox provider, ComboBox model)
        {
            var host = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            host.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            host.Controls.Add(provider, 0, 0);
            host.Controls.Add(model, 1, 0);
            return host;
        }

        private (ComboBox provider, ComboBox model) MakeModelRow(List<string> providerKeys, string defaultProvider, string defaultModel)
        {
            var provider = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(0, 3, 8, 3) };
            var model = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 3) };
            foreach (var k in providerKeys) provider.Items.Add(new ProviderItem { Key = k });
            provider.SelectedIndexChanged += (s, e) =>
            {
                model.Items.Clear();
                var key = (provider.SelectedItem as ProviderItem)?.Key;
                foreach (var m in ModelCatalog.ModelsFor(key, _settings)) model.Items.Add(new ModelItem { Info = m });
                if (model.Items.Count > 0) model.SelectedIndex = 0;
                UpdateEstimate();
            };
            model.SelectedIndexChanged += (s, e) => UpdateEstimate();
            int idx = providerKeys.IndexOf(defaultProvider);
            provider.SelectedIndex = idx >= 0 ? idx : 0;
            for (int i = 0; i < model.Items.Count; i++)
                if (string.Equals(((ModelItem)model.Items[i]).Info.Id, defaultModel, StringComparison.OrdinalIgnoreCase)) { model.SelectedIndex = i; break; }
            return (provider, model);
        }

        private static ModelChoice ChoiceOf(ComboBox provider, ComboBox model)
        {
            var p = (provider.SelectedItem as ProviderItem)?.Key;
            var m = (model.SelectedItem as ModelItem)?.Info;
            if (p == null || m == null) return null;
            return new ModelChoice { Provider = p, Model = m.Id, DisplayModel = m.DisplayName };
        }

        private List<ModelChoice> Contenders() => _slots.Select(s => ChoiceOf(s.provider, s.model)).Where(c => c != null).ToList();
        private ModelChoice JudgeChoice() => ChoiceOf(_cboJudgeProvider, _cboJudgeModel);

        private void PrepareInputs()
        {
            try
            {
                _inputs = _prepare();
                if (_inputs == null || _inputs.Segments.Count == 0)
                {
                    _lblStatus.Text = "No segments to translate in the open document.";
                    _btnRun.Enabled = false;
                    return;
                }
                _nudSegments.Maximum = _inputs.Segments.Count;
                if (_nudSegments.Value > _nudSegments.Maximum) _nudSegments.Value = _nudSegments.Maximum;
                UpdateEstimate();
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "Could not read the document: " + ex.Message;
                _btnRun.Enabled = false;
            }
        }

        private void UpdateEstimate()
        {
            if (_lblEstimate == null || _inputs == null) return;
            try
            {
                var est = SuperBenchRunner.EstimateCost(_inputs.Take((int)_nudSegments.Value), Contenders(), JudgeChoice());
                _lblEstimate.Text = $"of {_inputs.Segments.Count} in the document · estimated cost about ${est:0.00} for the three runs and the judge";
            }
            catch { _lblEstimate.Text = ""; }
        }

        private async Task RunAsync()
        {
            if (_inputs == null) { PrepareInputs(); if (_inputs == null) return; }
            var contenders = Contenders();
            var judge = JudgeChoice();
            if (contenders.Count < 2) { _lblStatus.Text = "Pick at least two models."; return; }
            var distinct = contenders.Select(c => c.Provider + "/" + c.Model).Distinct().Count();
            if (distinct < contenders.Count) { _lblStatus.Text = "Two slots have the same model; pick three different ones."; return; }

            var inputs = _inputs.Take((int)_nudSegments.Value);
            SetBusy(true);
            _cts = new CancellationTokenSource();
            var progress = new Progress<string>(msg => _lblStatus.Text = msg);
            try
            {
                _run = await Task.Run(() => SuperBenchRunner.RunAsync(inputs, contenders, judge, progress, _cts.Token));
                ShowRun(_run);
                _savedPath = TrySave(_run);
                _lblStatus.Text = "Done." + (_savedPath != null
                    ? " Saved automatically to the reports folder as " + Path.GetFileName(_savedPath) + " (Save report… writes a copy elsewhere)."
                    : " The report could not be saved to the reports folder; use Save report…");
                _btnSave.Enabled = true;
            }
            catch (OperationCanceledException)
            {
                _lblStatus.Text = "Cancelled.";
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "Failed: " + ex.Message;
            }
            finally
            {
                SetBusy(false);
                _cts?.Dispose(); _cts = null;
            }
        }

        private void SetBusy(bool busy)
        {
            foreach (var s in _slots) { s.provider.Enabled = !busy; s.model.Enabled = !busy; }
            _cboJudgeProvider.Enabled = !busy; _cboJudgeModel.Enabled = !busy;
            _nudSegments.Enabled = !busy;
            _btnRun.Enabled = !busy;
            _btnCancel.Enabled = busy;
            UseWaitCursor = busy;
        }

        private void SetGridColumns(SuperBench.Run run)
        {
            _grid.Columns.Clear();
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "#", FillWeight = 4, MinimumWidth = 36 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Source", FillWeight = 24 });
            if (run == null)
            {
                foreach (var l in new[] { "A", "B", "C" })
                    _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = l, FillWeight = 24 });
                return;
            }
            foreach (var c in SuperBench.InLabelOrder(run))
                _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = c.Label + " – " + (c.DisplayModel ?? c.Model), FillWeight = 24 });
        }

        private void ShowRun(SuperBench.Run run)
        {
            SetGridColumns(run);
            _grid.Rows.Clear();
            var ordered = SuperBench.InLabelOrder(run);
            for (int i = 0; i < run.Sources.Count; i++)
            {
                var cells = new List<object> { (i + 1).ToString(), run.Sources[i] };
                foreach (var c in ordered) cells.Add(i < c.Translations.Count ? c.Translations[i] ?? "" : "");
                _grid.Rows.Add(cells.ToArray());
            }

            var sb = new StringBuilder();
            sb.AppendLine("Legend: " + string.Join("   ", ordered.Select(c => c.Label + " = " + (c.DisplayModel ?? c.Model)
                + (c.CostKnown ? $" (${c.Cost:0.00##}, {c.Elapsed.TotalSeconds:F0} s)" : $" ({c.Elapsed.TotalSeconds:F0} s)")
                + (string.IsNullOrEmpty(c.Error) ? "" : " – " + c.Error))));
            sb.AppendLine();
            sb.AppendLine(string.IsNullOrWhiteSpace(run.JudgeReport)
                ? "No judge's report" + (string.IsNullOrEmpty(run.JudgeError) ? "." : ": " + run.JudgeError)
                : run.JudgeReport.Trim());
            _txtReport.Text = sb.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        private static string ReportsDir => Path.Combine(UserDataPath.TradosDir, "reports");

        private static string TrySave(SuperBench.Run run)
        {
            try
            {
                Directory.CreateDirectory(ReportsDir);
                var path = Path.Combine(ReportsDir, SuperBench.DefaultFileName(run));
                File.WriteAllText(path, SuperBench.RenderMarkdown(run), new UTF8Encoding(false));
                return path;
            }
            catch { return null; }
        }

        private void OnSave(object sender, EventArgs e)
        {
            if (_run == null) return;
            using (var dlg = new SaveFileDialog
            {
                Title = "Save SuperBench report", FileName = SuperBench.DefaultFileName(_run),
                Filter = "Markdown (*.md)|*.md|All files (*.*)|*.*", DefaultExt = "md", AddExtension = true, OverwritePrompt = true,
                InitialDirectory = Directory.Exists(ReportsDir) ? ReportsDir : "",
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(dlg.FileName, SuperBench.RenderMarkdown(_run), new UTF8Encoding(false));
                    _lblStatus.Text = "Saved as " + dlg.FileName;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "The report could not be saved:\r\n\r\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void OpenFolder()
        {
            try
            {
                Directory.CreateDirectory(ReportsDir);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + ReportsDir + "\"");
            }
            catch { }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F1)
            {
                HelpSystem.OpenHelp(HelpSystem.Topics.SuperBench);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }
            base.OnFormClosing(e);
        }
    }
}
