using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Supervertaler.Core.SuperVault;

namespace Supervertaler.Trados.SuperVault
{
    /// <summary>
    /// Export TMX, step one: what to export. A language pair (or all of them) and, as tick boxes, where the units came
    /// from (each TMX file and Studio TM imported, Workbench, segments confirmed in Studio), each with its number of
    /// units. What SuperVault holds is read in the background (one pass over the store), so Studio never freezes.
    /// Export... asks where to save; Cancel exports nothing.
    /// </summary>
    internal sealed class ExportChoiceDialog : Form
    {
        readonly string _store;
        readonly ComboBox _pair = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, Enabled = false };
        readonly ListView _list = new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, HideSelection = false };
        readonly Label _info = new Label { Dock = DockStyle.Top, AutoSize = false };
        readonly Label _total = new Label { AutoSize = true, Padding = new Padding(0, 8, 12, 0) };
        readonly Button _all = new Button { Text = "Select all", AutoSize = true, Enabled = false };
        readonly Button _none = new Button { Text = "Select none", AutoSize = true, Enabled = false };
        readonly Button _export = new Button { Text = "Export...", AutoSize = true, Enabled = false };
        readonly CancellationTokenSource _closing = new CancellationTokenSource();
        List<VaultHolding> _holdings = new List<VaultHolding>();
        bool _filling;

        public ExportChoiceDialog(string store)
        {
            _store = store;
            Text = "Export EverTM to TMX";
            Icon = Supervertaler.Trados.Core.IconHelper.AppIcon;
            Font = new Font("Segoe UI", 9F);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(600, 420);
            MinimumSize = new Size(420, 300);

            _info.Text = "Reading what EverTM holds...";
            _list.Columns.Add("From", 380);
            _list.Columns.Add("Units", 100, HorizontalAlignment.Right);
            _list.ItemChecked += (s, e) => { if (!_filling) UpdateTotal(); };
            _pair.SelectedIndexChanged += (s, e) => { if (!_filling) FillOrigins(); };

            var pairRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 6) };
            pairRow.Controls.Add(new Label { Text = "Language pair:", AutoSize = true, Padding = new Padding(0, 6, 6, 0) });
            pairRow.Controls.Add(_pair);
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 6, 0, 0) };
            bottom.Controls.AddRange(new Control[] { cancel, _export, _total });
            var picks = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
            picks.Controls.AddRange(new Control[] { _all, _none });
            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
            body.Controls.Add(_list);
            body.Controls.Add(pairRow);
            body.Controls.Add(picks);
            body.Controls.Add(_info);
            body.Controls.Add(bottom);
            Controls.Add(body);
            AcceptButton = _export;
            CancelButton = cancel;
            _all.Click += (s, e) => TickAll(true);
            _none.Click += (s, e) => TickAll(false);
            _export.Click += (s, e) => ChooseFile();
            Load += (s, e) => { FitInfo(); StartReading(); };
            Resize += (s, e) => FitInfo();
            FormClosed += (s, e) => _closing.Cancel();
        }

        /// <summary>The chosen base language pair ("en|nl"), or null for all.</summary>
        public string Pair => (_pair.SelectedItem as PairItem)?.Pair;

        /// <summary>The ticked origins, or null when every one is ticked (then units of any origin are exported, including ones added meanwhile).</summary>
        public List<string> Origins
        {
            get
            {
                var items = _list.Items.Cast<ListViewItem>().ToList();
                return items.All(i => i.Checked) ? null : items.Where(i => i.Checked).Select(i => (string)i.Tag).ToList();
            }
        }

        /// <summary>Where to save (after Export...).</summary>
        public string FilePath { get; private set; }

        /// <summary>The number of units the choice covers, as last read.</summary>
        internal long ChosenUnits { get; private set; }

        sealed class PairItem
        {
            public string Pair;   // null: all
            public string Text;
            public override string ToString() => Text;
        }

        void StartReading()
        {
            var token = _closing.Token;
            Task.Run(() => VaultExport.Holdings(_store, token), token).ContinueWith(t =>
            {
                if (token.IsCancellationRequested || IsDisposed) return;   // closed meanwhile: nothing to show, nothing exported
                if (t.IsFaulted) { _info.Text = "Could not read what EverTM holds: " + t.Exception?.GetBaseException().Message; FitInfo(); }
                else if (!t.IsCanceled) Fill(t.Result);
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Fills the choices (also called directly by tests).</summary>
        internal void Fill(List<VaultHolding> holdings)
        {
            _holdings = holdings;
            var f = CultureInfo.CurrentCulture;
            _filling = true;
            try
            {
                _pair.Items.Clear();
                _pair.Items.Add(new PairItem { Text = "All language pairs (" + VaultLibrary.Units(holdings.Sum(h => h.Units), f) + ")" });
                foreach (var g in holdings.GroupBy(h => h.Pair).OrderByDescending(g => g.Sum(h => h.Units)))
                {
                    var h = g.First();
                    _pair.Items.Add(new PairItem { Pair = g.Key, Text = h.LangA + " – " + h.LangB + " (" + VaultLibrary.Units(g.Sum(x => x.Units), f) + ")" });
                }
                _pair.SelectedIndex = 0;
                _pair.Enabled = _pair.Items.Count > 2;   // one pair only: nothing to choose
            }
            finally { _filling = false; }
            _info.Text = holdings.Count == 0
                ? "EverTM is empty: there is nothing to export."
                : "Choose what to export. Each unit is written in the direction and language variants it was first stored with, " +
                  "with the date it was added and where it came from.";
            FitInfo();
            FillOrigins();
        }

        /// <summary>The origins of the chosen pair, with their counts. Ticks are kept for origins still listed.</summary>
        void FillOrigins()
        {
            var unticked = new HashSet<string>(_list.Items.Cast<ListViewItem>().Where(i => !i.Checked).Select(i => (string)i.Tag), StringComparer.Ordinal);
            var pair = Pair;
            var f = CultureInfo.CurrentCulture;
            _filling = true;
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                foreach (var g in _holdings.Where(h => pair == null || h.Pair == pair).GroupBy(h => h.Origin, StringComparer.Ordinal)
                                           .OrderBy(g => OriginOrder(g.Key)).ThenBy(g => VaultHolding.OriginText(g.Key), StringComparer.CurrentCultureIgnoreCase))
                    _list.Items.Add(new ListViewItem(new[] { VaultHolding.OriginText(g.Key), g.Sum(h => h.Units).ToString("N0", f) }) { Tag = g.Key, Checked = !unticked.Contains(g.Key) });
                for (int c = 0; c < _list.Columns.Count; c++)
                {
                    _list.AutoResizeColumn(c, ColumnHeaderAutoResizeStyle.HeaderSize);
                    int header = _list.Columns[c].Width;
                    _list.AutoResizeColumn(c, ColumnHeaderAutoResizeStyle.ColumnContent);
                    if (_list.Columns[c].Width < header) _list.Columns[c].Width = header;
                }
            }
            finally { _list.EndUpdate(); _filling = false; }
            _all.Enabled = _none.Enabled = _list.Items.Count > 0;
            UpdateTotal();
        }

        // Confirmed in Studio and Workbench first, then the files.
        static int OriginOrder(string origin) => origin == "studio" ? 0 : origin == "workbench" ? 1 : origin == "" ? 3 : 2;

        void TickAll(bool on)
        {
            _filling = true;
            _list.BeginUpdate();
            try { foreach (ListViewItem i in _list.Items) i.Checked = on; }
            finally { _list.EndUpdate(); _filling = false; }
            UpdateTotal();
        }

        void UpdateTotal()
        {
            var pair = Pair;
            var ticked = new HashSet<string>(_list.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => (string)i.Tag), StringComparer.Ordinal);
            ChosenUnits = _holdings.Where(h => (pair == null || h.Pair == pair) && ticked.Contains(h.Origin)).Sum(h => h.Units);
            _total.Text = VaultLibrary.Units(ChosenUnits) + " to export";
            _export.Enabled = ChosenUnits > 0;
        }

        void ChooseFile()
        {
            var pair = Pair;
            var name = "EverTM" + (pair == null ? "" : " " + pair.Replace('|', '-')) + " " + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".tmx";
            using (var dlg = new SaveFileDialog { Filter = "TMX files (*.tmx)|*.tmx", FileName = name, Title = "Export EverTM to TMX", OverwritePrompt = true, AddExtension = true, DefaultExt = "tmx" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                FilePath = dlg.FileName;
            }
            DialogResult = DialogResult.OK;
        }

        /// <summary>The text above the list gets the height it needs at this width and font.</summary>
        void FitInfo()
        {
            int width = Math.Max(50, _info.Parent?.ClientSize.Width - 24 ?? ClientSize.Width - 24);
            _info.Height = TextRenderer.MeasureText(_info.Text, Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height + 6;
        }

        // Tests
        internal void SetTicked(string origin, bool on) { foreach (ListViewItem i in _list.Items) if ((string)i.Tag == origin) i.Checked = on; }
        internal void ChoosePair(string pair) { for (int i = 0; i < _pair.Items.Count; i++) if (((PairItem)_pair.Items[i]).Pair == pair) _pair.SelectedIndex = i; }
        internal bool ExportEnabled => _export.Enabled;
        internal int PairChoices => _pair.Items.Count;
        internal List<string> ListedOrigins => _list.Items.Cast<ListViewItem>().Select(i => (string)i.Tag).ToList();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _closing.Dispose();
            base.Dispose(disposing);
        }
    }
}
