using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Supervertaler.Core.SuperVault;

namespace Supervertaler.Trados.SuperVault
{
    /// <summary>
    /// Import from Workbench, step one: which of Workbench's translation memories to import. One line per TM with a
    /// tick box, its name, its language pairs and its number of units. The list is read in the background (one pass
    /// over Workbench's units: about 3 s for 1.55 million), so Studio never freezes. Cancel imports nothing.
    /// </summary>
    internal sealed class WorkbenchChoiceDialog : Form
    {
        readonly string _database;
        readonly HashSet<string> _unticked;   // remembered from last time: a TM added since then shows up ticked
        readonly ListView _list = new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, HideSelection = false };
        readonly Label _info = new Label { Dock = DockStyle.Top, AutoSize = false };
        readonly Label _total = new Label { AutoSize = true, Padding = new Padding(0, 8, 12, 0) };
        readonly Button _all = new Button { Text = "Select all", AutoSize = true };
        readonly Button _none = new Button { Text = "Select none", AutoSize = true };
        readonly Button _import = new Button { Text = "Import", AutoSize = true, DialogResult = DialogResult.OK, Enabled = false };
        readonly CancellationTokenSource _closing = new CancellationTokenSource();
        bool _filling;

        public WorkbenchChoiceDialog(string database, IEnumerable<string> untickedLastTime)
        {
            _database = database;
            _unticked = new HashSet<string>(untickedLastTime ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            Text = "Import from Workbench";
            Icon = Supervertaler.Trados.Core.IconHelper.AppIcon;
            Font = new Font("Segoe UI", 9F);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(640, 440);
            MinimumSize = new Size(420, 300);

            _info.Text = "Reading Workbench's translation memories...";
            _list.Columns.Add("Translation memory", 330);
            _list.Columns.Add("Languages", 150);
            _list.Columns.Add("Units", 100, HorizontalAlignment.Right);
            _list.ItemCheck += (s, e) => { if (!_filling && _list.Items[e.Index].Tag is WorkbenchTm tm && tm.Units == 0) e.NewValue = CheckState.Unchecked; };
            _list.ItemChecked += (s, e) => { if (!_filling) UpdateTotal(); };

            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 6, 0, 0) };
            bottom.Controls.AddRange(new Control[] { cancel, _import, _total });
            var picks = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
            picks.Controls.AddRange(new Control[] { _all, _none });
            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
            body.Controls.Add(_list);
            body.Controls.Add(picks);
            body.Controls.Add(_info);
            body.Controls.Add(bottom);
            Controls.Add(body);
            AcceptButton = _import;
            CancelButton = cancel;
            _all.Enabled = _none.Enabled = false;
            _all.Click += (s, e) => TickAll(true);
            _none.Click += (s, e) => TickAll(false);
            Load += (s, e) => { FitInfo(); StartReading(); };
            Resize += (s, e) => FitInfo();
            FormClosed += (s, e) => _closing.Cancel();
        }

        /// <summary>The ticked TMs' tm_ids (only after Import).</summary>
        public List<string> Ticked => Tms().Where(x => x.Item2).Select(x => x.Item1.TmId).ToList();

        /// <summary>The TMs with units that were left unticked, to remember for next time.</summary>
        public List<string> Unticked => Tms().Where(x => !x.Item2 && x.Item1.Units > 0).Select(x => x.Item1.TmId).ToList();

        IEnumerable<(WorkbenchTm, bool)> Tms() => _list.Items.Cast<ListViewItem>().Where(i => i.Tag is WorkbenchTm).Select(i => ((WorkbenchTm)i.Tag, i.Checked));

        void StartReading()
        {
            var token = _closing.Token;
            Task.Run(() => VaultStore.ListWorkbenchTms(_database, token), token).ContinueWith(t =>
            {
                if (token.IsCancellationRequested || IsDisposed) return;   // closed meanwhile: nothing to show, nothing imported
                if (t.IsFaulted) { _info.Text = "Workbench's translation memories could not be read: " + t.Exception?.GetBaseException().Message; FitInfo(); }
                else if (!t.IsCanceled) Fill(t.Result);
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Fills the list (also called directly by tests).</summary>
        internal void Fill(List<WorkbenchTm> tms)
        {
            var f = CultureInfo.CurrentCulture;
            _filling = true;
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                foreach (var tm in tms)
                {
                    var item = new ListViewItem(new[] { tm.Name, tm.Units == 0 ? "(no units)" : tm.PairsText, tm.Units.ToString("N0", f) }) { Tag = tm };
                    if (tm.Units == 0) item.ForeColor = SystemColors.GrayText;
                    item.Checked = tm.Units > 0 && !_unticked.Contains(tm.TmId);
                    _list.Items.Add(item);
                }
                for (int c = 0; c < _list.Columns.Count; c++)
                {
                    // Wide enough for the header and for the widest entry, whichever is wider.
                    _list.AutoResizeColumn(c, ColumnHeaderAutoResizeStyle.HeaderSize);
                    int header = _list.Columns[c].Width;
                    _list.AutoResizeColumn(c, ColumnHeaderAutoResizeStyle.ColumnContent);
                    if (_list.Columns[c].Width < header) _list.Columns[c].Width = header;
                }
            }
            finally { _list.EndUpdate(); _filling = false; }
            _info.Text = tms.Count == 0
                ? "Workbench has no translation memories."
                : "Tick the translation memories to import. Units SuperVault already holds are not added again; Workbench's database is only read.";
            _all.Enabled = _none.Enabled = tms.Any(t => t.Units > 0);
            FitInfo();
            UpdateTotal();
        }

        /// <summary>The text above the list gets the height it needs at this width and font.</summary>
        void FitInfo()
        {
            int width = Math.Max(50, _info.Parent?.ClientSize.Width - 24 ?? ClientSize.Width - 24);
            _info.Height = TextRenderer.MeasureText(_info.Text, Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height + 6;
        }

        void TickAll(bool on)
        {
            _filling = true;
            _list.BeginUpdate();
            try { foreach (ListViewItem i in _list.Items) i.Checked = on && ((WorkbenchTm)i.Tag).Units > 0; }
            finally { _list.EndUpdate(); _filling = false; }
            UpdateTotal();
        }

        void UpdateTotal()
        {
            var ticked = Tms().Where(x => x.Item2).Select(x => x.Item1).ToList();
            _total.Text = ticked.Count.ToString("N0", CultureInfo.CurrentCulture) + " ticked, " + VaultLibrary.Units(ticked.Sum(t => t.Units));
            _import.Enabled = ticked.Count > 0;
        }

        /// <summary>Tests: tick or untick one TM as a click would.</summary>
        internal void SetTicked(string tmId, bool on)
        {
            foreach (ListViewItem i in _list.Items) if (((WorkbenchTm)i.Tag).TmId == tmId) i.Checked = on;
        }

        internal bool ImportEnabled => _import.Enabled;
        internal void SelectAll() => TickAll(true);
        internal void SelectNone() => TickAll(false);

        protected override void Dispose(bool disposing)
        {
            if (disposing) _closing.Dispose();
            base.Dispose(disposing);
        }
    }
}
