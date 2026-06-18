using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// A simple dialog that reads the JSONL token-usage ledger and shows totals
    /// grouped by project / client / model / etc., with CSV / Excel export.
    /// </summary>
    public class UsageReportForm : Form
    {
        private readonly ComboBox _cmbRange;
        private readonly ComboBox _cmbGroup;
        private readonly DataGridView _grid;
        private readonly Label _lblTotals;
        private List<UsageRecord> _records = new List<UsageRecord>();

        public UsageReportForm()
        {
            Text = "Token Usage & Costs";
            Width = 860;
            Height = 540;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;

            var top = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Padding = new Padding(8, 8, 8, 4)
            };

            top.Controls.Add(MakeLabel("Range:", 0));
            _cmbRange = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
            _cmbRange.Items.AddRange(new object[] { "This month", "Last 3 months", "This year", "All time" });
            _cmbRange.SelectedIndex = 0;
            _cmbRange.SelectedIndexChanged += (s, e) => Reload();
            top.Controls.Add(_cmbRange);

            top.Controls.Add(MakeLabel("Group by:", 14));
            _cmbGroup = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
            _cmbGroup.Items.AddRange(new object[] { "Project", "Client", "Model", "Provider", "Task", "Day", "Month" });
            _cmbGroup.SelectedIndex = 0;
            _cmbGroup.SelectedIndexChanged += (s, e) => Rebind();
            top.Controls.Add(_cmbGroup);

            top.Controls.Add(MakeButton("Refresh", 14, (s, e) => Reload()));
            top.Controls.Add(MakeButton("Export CSV…", 14, (s, e) => Export(false)));
            top.Controls.Add(MakeButton("Export Excel…", 6, (s, e) => Export(true)));

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                AutoGenerateColumns = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                BorderStyle = BorderStyle.None,
                BackgroundColor = SystemColors.Window
            };
            _grid.DataBindingComplete += (s, e) => FormatGrid();

            _lblTotals = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };

            // Add the Fill control first so it occupies the space left by the docked bars.
            Controls.Add(_grid);
            Controls.Add(_lblTotals);
            Controls.Add(top);

            Load += (s, e) => Reload();
        }

        private static Label MakeLabel(string text, int leftMargin) => new Label
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(leftMargin, 6, 4, 0)
        };

        private static Button MakeButton(string text, int leftMargin, EventHandler onClick)
        {
            var b = new Button { Text = text, AutoSize = true, Margin = new Padding(leftMargin, 2, 0, 0) };
            b.Click += onClick;
            return b;
        }

        private void Reload()
        {
            try
            {
                var range = RangeUtc();
                _records = UsageReader.Load(range.Item1, range.Item2);
            }
            catch { _records = new List<UsageRecord>(); }
            Rebind();
        }

        private void Rebind()
        {
            UsageDimension dim;
            var sel = _cmbGroup.SelectedItem as string ?? "Project";
            if (!Enum.TryParse(sel, out dim)) dim = UsageDimension.Project;

            var rows = UsageAggregator.Group(_records, dim);
            _grid.DataSource = null;
            _grid.DataSource = rows;

            var total = UsageAggregator.Total(_records);
            _lblTotals.Text = string.Format(
                "Total: {0:N0} calls · {1:N0} in / {2:N0} out · ${3:0.00} · {4} from provider",
                total.Calls, total.InputTokens, total.OutputTokens, total.CostUsd, total.ActualShare);
        }

        private void FormatGrid()
        {
            foreach (DataGridViewColumn c in _grid.Columns)
            {
                switch (c.DataPropertyName)
                {
                    case "Group": c.HeaderText = "Group"; c.FillWeight = 180; break;
                    case "Calls": c.HeaderText = "Calls"; break;
                    case "InputTokens": c.HeaderText = "Input"; break;
                    case "OutputTokens": c.HeaderText = "Output"; break;
                    case "TotalTokens": c.HeaderText = "Total"; break;
                    case "CostUsd": c.HeaderText = "Cost (USD)"; c.DefaultCellStyle.Format = "0.0000"; break;
                    case "ActualShare": c.HeaderText = "% actual"; break;
                    case "ActualCalls": c.Visible = false; break;
                }
                if (c.DataPropertyName != "Group" && c.Visible)
                    c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            }
        }

        private Tuple<DateTime, DateTime> RangeUtc()
        {
            var now = DateTime.UtcNow;
            DateTime from;
            DateTime to = now;
            switch (_cmbRange.SelectedIndex)
            {
                case 0: from = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc); break;
                case 1: from = now.AddMonths(-3); break;
                case 2: from = new DateTime(now.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc); break;
                default: from = DateTime.MinValue; to = DateTime.MaxValue; break;
            }
            return Tuple.Create(from, to);
        }

        private void Export(bool xlsx)
        {
            try
            {
                using (var dlg = new SaveFileDialog())
                {
                    dlg.Filter = xlsx ? "Excel workbook (*.xlsx)|*.xlsx" : "CSV file (*.csv)|*.csv";
                    dlg.FileName = "supervertaler-usage-" + DateTime.Now.ToString("yyyy-MM-dd") + (xlsx ? ".xlsx" : ".csv");
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;

                    if (xlsx) UsageExport.WriteXlsx(dlg.FileName, _records);
                    else UsageExport.WriteCsv(dlg.FileName, _records);

                    MessageBox.Show(this,
                        string.Format("Exported {0:N0} record(s) to:\n{1}", _records.Count, dlg.FileName),
                        "Export complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Export failed: " + ex.Message,
                    "Export", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
