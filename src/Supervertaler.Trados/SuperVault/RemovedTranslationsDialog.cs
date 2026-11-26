using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Supervertaler.Core.SuperVault;

namespace Supervertaler.Trados.SuperVault
{
    /// <summary>
    /// The translations removed from EverTM (EverTM window > Removed translations...): newest first, 50 at a time, each
    /// with its texts, how many units, when and by whom, and Restore (docs/design/evertm-pane.md, section 8; Michael's
    /// decision 8). Restoring brings the units back exactly as they were; it is applied in the background, like a
    /// confirmation, and shows in lookups within a second or two.
    /// </summary>
    public sealed class RemovedTranslationsDialog : Form
    {
        readonly VaultLibrary _library;
        readonly ListView _list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true };
        readonly Button _restore = new Button { Text = "Restore", AutoSize = true, Enabled = false };
        readonly Button _more = new Button { AutoSize = true, Visible = false };
        readonly Label _note = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(70, 70, 70) };
        int _pages, _total;

        public RemovedTranslationsDialog(VaultLibrary library)
        {
            _library = library;
            Text = "Removed translations";
            Icon = Supervertaler.Trados.Core.IconHelper.AppIcon;
            Font = new Font("Segoe UI", 9F);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ClientSize = new Size(760, 380);

            _list.Columns.Add("Removed translation", 470);
            _list.Columns.Add("Units", 50, HorizontalAlignment.Right);
            _list.Columns.Add("Removed", 200);
            _list.SelectedIndexChanged += (s, e) => _restore.Enabled = _list.SelectedItems.Count > 0;
            _list.DoubleClick += (s, e) => RestoreSelected();
            _restore.Click += (s, e) => RestoreSelected();
            _more.Click += (s, e) => LoadPage(_pages);

            var intro = new Label
            {
                Dock = DockStyle.Top, Height = 40, Padding = new Padding(0, 0, 0, 6),
                Text = "Translations you removed from EverTM. They are no longer offered, and importing a file that has them doesn't bring them back. " +
                       "Restore one to bring it back exactly as it was. Only EverTM is affected: your Studio TMs and files never are.",
            };
            var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 40, ColumnCount = 4, Padding = new Padding(0, 6, 0, 0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 3; i++) bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.OK };
            bottom.Controls.Add(_note, 0, 0);
            bottom.Controls.Add(_more, 1, 0);
            bottom.Controls.Add(_restore, 2, 0);
            bottom.Controls.Add(close, 3, 0);
            AcceptButton = close;
            CancelButton = close;

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
            body.Controls.Add(_list);
            body.Controls.Add(bottom);
            body.Controls.Add(intro);
            Controls.Add(body);
            Shown += (s, e) => LoadPage(0);
        }

        /// <summary>A page of 50 from the store (one small indexed query on the marks table).</summary>
        void LoadPage(int page)
        {
            List<VaultRemoved> items;
            try { items = _library.Removed(page, out _total); }
            catch (Exception ex)
            {
                _note.Text = "Could not read the removed translations: " + ex.Message;
                Supervertaler.Trados.Core.DiagnosticLog.WriteAlways("EverTM", "Removed translations: " + ex);
                return;
            }
            if (page == 0) _list.Items.Clear();
            foreach (var r in items)
            {
                string when = r.RemovedAt.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.CurrentCulture) + (string.IsNullOrWhiteSpace(r.RemovedBy) ? "" : ", " + r.RemovedBy);
                var item = new ListViewItem(new[] { Describe(r), r.Units.ToString(CultureInfo.CurrentCulture), when }) { Tag = r };
                _list.Items.Add(item);
            }
            _pages = page + 1;
            int left = _total - _list.Items.Count;
            _more.Text = "Show " + Math.Min(VaultLibrary.TranslationsPageSize, left) + " more";
            _more.Visible = left > 0;
            _note.Text = _total == 0 ? "Nothing has been removed from EverTM." : _total.ToString("N0", CultureInfo.CurrentCulture) + (_total == 1 ? " removed translation" : " removed translations");
        }

        /// <summary>"nl: De pomp wordt gestart.  ⇄  en: The pmup is started."</summary>
        static string Describe(VaultRemoved r) =>
            (r.Edited ? "(edited) " : "") + r.Key.LangA + ": " + VaultMarkup.Plain(r.TextA ?? "") + "  ⇄  " + r.Key.LangB + ": " + VaultMarkup.Plain(r.TextB ?? "");

        void RestoreSelected()
        {
            var chosen = _list.SelectedItems.Cast<ListViewItem>().ToList();
            if (chosen.Count == 0) return;
            foreach (var item in chosen)
            {
                _library.Restore(((VaultRemoved)item.Tag).Key, Environment.UserName);
                _list.Items.Remove(item);
            }
            _total -= chosen.Count;
            _note.Text = (chosen.Count == 1 ? "Restored: it" : "Restored " + chosen.Count + ": they") + " will be offered again in a moment.";
        }

        /// <summary>Tests (tools/SuperVaultCheck): the list, and Restore on every row.</summary>
        public ListView List => _list;
        public void RestoreForTests() { foreach (ListViewItem i in _list.Items) i.Selected = true; RestoreSelected(); }
    }
}
