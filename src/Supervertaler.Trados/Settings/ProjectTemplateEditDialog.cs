using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.Settings
{
    /// <summary>
    /// Project templates, step 4: edits the Supervertaler project settings stored in a Studio project template, and the
    /// template's Studio customer. Studio's own part (TMs, termbases, file types) stays in Studio's template editor; the
    /// window says so. The rules are <see cref="ProjectTemplateEdit"/>'s; the template is written through Studio's own
    /// template object (<see cref="StudioTemplates.Edit"/>), backed up first. MultiTerm choices are kept as they are.
    /// UI thread; the lists it shows are small local files and Studio's in-memory customer list.
    /// </summary>
    internal sealed class ProjectTemplateEditDialog : Form
    {
        readonly ComboBox _bank = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };
        readonly ComboBox _translate = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };
        readonly ComboBox _proofread = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };
        readonly TextBox _client = new TextBox { Width = 360 };
        readonly ComboBox _customer = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };
        readonly DataGridView _termbases = new DataGridView
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.CellSelect, BackgroundColor = SystemColors.Window,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, EditMode = DataGridViewEditMode.EditOnEnter,
        };
        readonly string _path;
        readonly TemplateProfile _template;
        readonly List<TemplateTermbaseRow> _rows;
        readonly string _customerBefore;

        public ProjectTemplateEditDialog(TemplateProfile template)
        {
            _template = template;
            _path = template.Path;
            Text = "Edit project template - " + template.Name;
            Font = new Font("Segoe UI", 9F);
            Icon = IconHelper.AppIcon;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ClientSize = new Size(640, 640);
            MinimumSize = new Size(520, 480);

            var sv = template.Supervertaler ?? new ProjectProfileValues();
            Fill(_bank, ProjectTemplateEdit.BankItems(SafeBanks(), sv.MemoryBankName), sv.MemoryBankName);
            var prompts = SafePrompts();
            Fill(_translate, ProjectTemplateEdit.PromptItems(prompts, false, global::Supervertaler.Trados.Controls.BatchTranslateControl.CategoryBelongsTo, sv.TranslatePromptPath), sv.TranslatePromptPath);
            Fill(_proofread, ProjectTemplateEdit.PromptItems(prompts, true, global::Supervertaler.Trados.Controls.BatchTranslateControl.CategoryBelongsTo, sv.ProofreadPromptPath), sv.ProofreadPromptPath);
            _client.Text = sv.ClientName ?? "";
            _customerBefore = template.CustomerName ?? "";
            FillCustomers();

            _rows = ProjectTemplateEdit.TermbaseRows(sv.Termbases, ProjectProfileStore.LocalTermbases());
            BuildGrid();

            var intro = new Label
            {
                Dock = DockStyle.Top, AutoSize = false, Height = 52,
                Text = "Supervertaler's project settings in this template. A project made from it, or one you apply it to, gets them. " +
                       "Studio's own part (TMs, termbases, file types) is edited in Studio: Projects view > Project Templates > Edit.",
            };
            var form = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 4, 0, 4) };
            form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            void RowOf(string label, Control c)
            {
                form.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) });
                form.Controls.Add(c);
            }
            RowOf("Memory bank:", _bank);
            RowOf("Translate prompt:", _translate);
            RowOf("Proofread prompt:", _proofread);
            RowOf("Client:", _client);
            RowOf("Studio customer:", _customer);
            var tbLabel = new Label
            {
                Dock = DockStyle.Top, AutoSize = false, Height = 40, TextAlign = ContentAlignment.BottomLeft,
                Text = "Supervertaler termbases (none ticked: a project keeps its own choices; some ticked: all others are off):",
            };
            var note = new Label
            {
                Dock = DockStyle.Bottom, AutoSize = false, Height = 36,
                Text = "MultiTerm termbase choices are kept as the template has them. Termbases this computer does not have are kept, unchanged.",
            };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 6, 0, 0) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var save = new Button { Text = "Save", AutoSize = true };
            save.Click += (s, e) => SaveTemplate();
            buttons.Controls.AddRange(new Control[] { cancel, save, StudioCustomersDialog.HelpLink(HelpSystem.Topics.EditProjectTemplate, "Project templates") });
            AcceptButton = save;
            CancelButton = cancel;

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
            body.Controls.Add(_termbases);
            body.Controls.Add(tbLabel);
            body.Controls.Add(form);
            body.Controls.Add(intro);
            body.Controls.Add(note);
            body.Controls.Add(buttons);
            Controls.Add(body);
        }

        /// <summary>True once the template was saved.</summary>
        public bool Saved { get; private set; }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F1) { HelpSystem.OpenHelp(HelpSystem.Topics.EditProjectTemplate); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        static List<string> SafeBanks()
        {
            try { return UserDataPath.ListMemoryBanks() ?? new List<string>(); }
            catch (Exception ex) { DiagnosticLog.Log("ProjectTemplates", "memory banks could not be listed: " + ex.Message); return new List<string>(); }
        }

        static List<KeyValuePair<string, string>> SafePrompts()
        {
            try
            {
                var lib = TermLensEditorViewPart.GetPromptLibrary() ?? new global::Supervertaler.Core.PromptLibrary();
                return (lib.GetAllPrompts() ?? new List<global::Supervertaler.Core.Models.PromptTemplate>())
                    .Where(p => p != null).Select(p => new KeyValuePair<string, string>(p.RelativePath, p.Category)).ToList();
            }
            catch (Exception ex) { DiagnosticLog.Log("ProjectTemplates", "prompts could not be listed: " + ex.Message); return new List<KeyValuePair<string, string>>(); }
        }

        static void Fill(ComboBox box, List<TemplatePromptItem> items, string stored)
        {
            box.Items.AddRange(items.Cast<object>().ToArray());
            var s = (stored ?? "").Trim();
            box.SelectedItem = items.FirstOrDefault(i => string.Equals(i.Value, s, StringComparison.OrdinalIgnoreCase)) ?? items[0];
        }

        void FillCustomers()
        {
            var items = new List<TemplatePromptItem> { new TemplatePromptItem { Value = "", Label = "(none)" } };
            var list = StudioCustomers.List(out var error);
            if (list != null)
                foreach (var c in JobProfile.SortByName(list, c => c.Name))
                    items.Add(new TemplatePromptItem { Value = c.Name, Label = c.Name });
            if (_customerBefore.Length > 0 && !items.Any(i => JobProfile.SameCustomerName(i.Value, _customerBefore)))
                items.Add(new TemplatePromptItem { Value = _customerBefore, Label = _customerBefore + " (not in Studio's customer list)" });
            Fill(_customer, items, items.FirstOrDefault(i => JobProfile.SameCustomerName(i.Value, _customerBefore))?.Value ?? "");
            if (list == null)
            {
                // Studio's list is out of reach: the customer cannot be chosen, so it is left as the template has it.
                _customer.Enabled = false;
                DiagnosticLog.Log("ProjectTemplates", "customer list for the template editor: " + error);
            }
        }

        void BuildGrid()
        {
            _termbases.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Termbase", ReadOnly = true, FillWeight = 46 });
            foreach (var h in new[] { "Read", "Write", "Project termbase", "AI" })
                _termbases.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = h, FillWeight = h == "Project termbase" ? 20 : 11 });
            foreach (var r in _rows)
            {
                int i = _termbases.Rows.Add(r.Name + (r.Missing ? ProjectTemplateEdit.NotOnThisComputer : ""), r.Read, r.Write, r.Project, r.Ai);
                var row = _termbases.Rows[i];
                row.Tag = r;
                if (r.Missing) { row.ReadOnly = true; row.DefaultCellStyle.ForeColor = SystemColors.GrayText; }
            }
            if (_rows.Count == 0)
                _termbases.Rows.Add("(no Supervertaler termbases on this computer)", false, false, false, false);
            // One Project termbase: ticking one unticks the others at once.
            _termbases.CurrentCellDirtyStateChanged += (s, e) => { if (_termbases.IsCurrentCellDirty) _termbases.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            _termbases.CellValueChanged += (s, e) =>
            {
                if (e.RowIndex < 0 || e.ColumnIndex != 3 || !(_termbases.Rows[e.RowIndex].Cells[3].Value is bool on) || !on) return;
                foreach (DataGridViewRow other in _termbases.Rows)
                    if (other.Index != e.RowIndex && !other.ReadOnly && other.Cells[3].Value is bool b && b) other.Cells[3].Value = false;
            };
        }

        void SaveTemplate()
        {
            _termbases.EndEdit();
            foreach (DataGridViewRow row in _termbases.Rows)
            {
                if (!(row.Tag is TemplateTermbaseRow r) || r.Missing) continue;
                r.Read = row.Cells[1].Value is bool a && a;
                r.Write = row.Cells[2].Value is bool b && b;
                r.Project = row.Cells[3].Value is bool c && c;
                r.Ai = row.Cells[4].Value is bool d && d;
            }
            var values = ProjectTemplateEdit.Edited(_template.Supervertaler,
                (_bank.SelectedItem as TemplatePromptItem)?.Value, (_translate.SelectedItem as TemplatePromptItem)?.Value,
                (_proofread.SelectedItem as TemplatePromptItem)?.Value, _client.Text, _rows);
            var customer = _customer.Enabled ? ((_customer.SelectedItem as TemplatePromptItem)?.Value ?? "") : null;
            if (customer != null && JobProfile.SameCustomerName(customer, _customerBefore)) customer = null;   // unchanged: left alone

            string error;
            try
            {
                Cursor = Cursors.WaitCursor;
                error = StudioTemplates.Edit(_path, values, customer);
            }
            finally { Cursor = Cursors.Default; }
            if (error != null) { MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            Saved = true;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
