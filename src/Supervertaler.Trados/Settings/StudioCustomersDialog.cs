using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.Settings
{
    /// <summary>
    /// Studio's customer list, edited from Supervertaler (job profiles, step 2): add, rename, change email, remove,
    /// always alphabetical. Every change goes through Studio's own customer list while Studio runs
    /// (<see cref="StudioCustomers"/>), never into projects.xml behind its back. A rename keeps the customer's identity,
    /// so projects that use it still do. Removing a customer that projects use says how many and asks first.
    /// </summary>
    internal sealed class StudioCustomersDialog : Form
    {
        readonly ListView _list = new ListView { View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, Dock = DockStyle.Fill };
        readonly Label _intro = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 48 };
        readonly Label _status = new Label { Dock = DockStyle.Bottom, AutoSize = false, Height = 40 };
        readonly Button _add = new Button { Text = "Add...", AutoSize = true };
        readonly Button _rename = new Button { Text = "Rename...", AutoSize = true };
        readonly Button _email = new Button { Text = "Change email...", AutoSize = true };
        readonly Button _remove = new Button { Text = "Remove...", AutoSize = true };
        List<StudioCustomer> _customers = new List<StudioCustomer>();
        bool _readable;

        public StudioCustomersDialog()
        {
            Text = "Studio customers";
            Font = new Font("Segoe UI", 9F);
            Icon = IconHelper.AppIcon;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = MaximizeBox = false;
            ClientSize = new Size(600, 420);

            _intro.Text = "Trados Studio's customer list, in alphabetical order. Changes go straight into Studio's list " +
                          "(the one Studio's Project Settings and New Project offer), and are kept in order there too.";
            _list.Columns.Add("Customer", 300);
            _list.Columns.Add("Email", 250);
            _list.SelectedIndexChanged += (s, e) => UpdateButtons();
            _list.DoubleClick += (s, e) => RenameSelected();

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 6, 0, 0) };
            var close = new Button { Text = "Close", DialogResult = DialogResult.OK, AutoSize = true };
            buttons.Controls.AddRange(new Control[] { _add, _rename, _email, _remove, close });
            AcceptButton = close;
            CancelButton = close;
            _add.Click += (s, e) => AddCustomer();
            _rename.Click += (s, e) => RenameSelected();
            _email.Click += (s, e) => ChangeEmail();
            _remove.Click += (s, e) => RemoveSelected();

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
            body.Controls.Add(_list);
            body.Controls.Add(_intro);
            body.Controls.Add(_status);
            body.Controls.Add(buttons);
            Controls.Add(body);

            Shown += (s, e) => Reload(null);
        }

        StudioCustomer Selected => _list.SelectedItems.Count == 1 ? (StudioCustomer)_list.SelectedItems[0].Tag : null;

        void Reload(Guid? select)
        {
            var list = StudioCustomers.List(out var error);
            _readable = list != null;
            _customers = list ?? new List<StudioCustomer>();
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var c in _customers)
            {
                var item = new ListViewItem(new[] { c.Name, c.Email }) { Tag = c };
                _list.Items.Add(item);
                if (select == c.Id) { item.Selected = true; item.EnsureVisible(); }
            }
            _list.EndUpdate();
            if (error != null) _status.Text = error;
            else if (select == null) _status.Text = _customers.Count == 1 ? "1 customer." : _customers.Count + " customers.";
            UpdateButtons();
        }

        void UpdateButtons()
        {
            _add.Enabled = _readable;
            _rename.Enabled = _email.Enabled = _remove.Enabled = _readable && Selected != null;
        }

        void AddCustomer()
        {
            using (var f = new CustomerForm("Add a customer", "", "", true, true))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                var added = StudioCustomers.Add(f.CustomerName, f.Email, out var error);
                if (added == null) { Say(error); return; }
                Reload(added.Id);
                _status.Text = "Added \"" + added.Name + "\".";
            }
        }

        void RenameSelected()
        {
            var c = Selected;
            if (c == null) return;
            using (var f = new CustomerForm("Rename a customer", c.Name, c.Email, true, false))
            {
                if (f.ShowDialog(this) != DialogResult.OK || f.CustomerName.Trim() == c.Name.Trim()) return;
                if (!StudioCustomers.Rename(c.Id, f.CustomerName, out var error)) { Say(error); return; }
                Reload(c.Id);
                _status.Text = "Renamed \"" + c.Name + "\" to \"" + f.CustomerName.Trim() + "\". Projects that use it still do; " +
                               "Studio may show the old name for them until their customer is set again.";
            }
        }

        void ChangeEmail()
        {
            var c = Selected;
            if (c == null) return;
            using (var f = new CustomerForm("Change a customer's email", c.Name, c.Email, false, true))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                if (!StudioCustomers.SetEmail(c.Id, f.Email, out var error)) { Say(error); return; }
                Reload(c.Id);
                _status.Text = "Email of \"" + c.Name + "\" changed.";
            }
        }

        void RemoveSelected()
        {
            var c = Selected;
            if (c == null) return;
            int used = StudioCustomers.CountProjectsUsing(c.Id, out _);
            var answer = MessageBox.Show(this, JobProfile.RemoveQuestion(c.Name, used), Text,
                MessageBoxButtons.YesNo, used == 0 ? MessageBoxIcon.Question : MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
            if (!StudioCustomers.Remove(c.Id, out var error)) { Say(error); return; }
            Reload(null);
            _status.Text = "Removed \"" + c.Name + "\".";
        }

        void Say(string message)
        {
            _status.Text = message ?? "";
            MessageBox.Show(this, message ?? "That did not work.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>The small form for a name and an email address; a field that is not being changed is shown read-only.</summary>
        sealed class CustomerForm : Form
        {
            readonly TextBox _name = new TextBox { Width = 320 };
            readonly TextBox _mail = new TextBox { Width = 320 };
            public string CustomerName => _name.Text;
            public string Email => _mail.Text;

            public CustomerForm(string title, string name, string email, bool nameEditable, bool emailEditable)
            {
                Text = title;
                Font = new Font("Segoe UI", 9F);
                StartPosition = FormStartPosition.CenterParent;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MinimizeBox = MaximizeBox = false;
                ShowInTaskbar = false;
                AutoSize = true;
                AutoSizeMode = AutoSizeMode.GrowAndShrink;
                _name.Text = name ?? ""; _name.ReadOnly = !nameEditable;
                _mail.Text = email ?? ""; _mail.ReadOnly = !emailEditable;

                var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(12), Dock = DockStyle.Fill };
                grid.Controls.Add(new Label { Text = "Name:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
                grid.Controls.Add(_name, 1, 0);
                grid.Controls.Add(new Label { Text = "Email:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
                grid.Controls.Add(_mail, 1, 1);
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
                var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Anchor = AnchorStyles.Right };
                buttons.Controls.Add(cancel);
                buttons.Controls.Add(ok);
                grid.Controls.Add(buttons, 1, 2);
                Controls.Add(grid);
                AcceptButton = ok;
                CancelButton = cancel;
                Shown += (s, e) => (nameEditable ? _name : _mail).Focus();
            }
        }
    }
}
