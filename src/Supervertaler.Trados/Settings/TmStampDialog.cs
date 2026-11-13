using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using Sdl.ProjectAutomation.FileBased;
using Supervertaler.Core.SuperVault;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.Settings
{
    /// <summary>
    /// "This project's TMs": for the project in use, each of its Studio TMs, whether it has a Project and a Client field
    /// (Studio stores a stamped value only in a TM that has the field), and the one action that changes a TM: adding a
    /// missing field, TM by TM, after the user said yes to a warning (Michael, 11 Nov: "Yes, offer to add"). Never offered
    /// for a GroupShare TM, a read-only file, or a project from a package or GroupShare. For such a project, also the
    /// switch to stamp it anyway (off unless switched on, kept in the project).
    /// </summary>
    internal sealed class TmStampDialog : Form
    {
        readonly bool _wantProject, _wantClient;
        readonly FileBasedProject _project;
        readonly bool _fromElsewhere;
        readonly List<TmFieldStamping.ProjectTm> _tms = new List<TmFieldStamping.ProjectTm>();
        readonly ListView _list = new ListView { View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, Dock = DockStyle.Fill };
        readonly Label _intro = new Label { Dock = DockStyle.Top, AutoSize = false };
        readonly Label _status = new Label { Dock = DockStyle.Bottom, AutoSize = false, Height = 40 };
        readonly CheckBox _allowHere = new CheckBox { Dock = DockStyle.Bottom, AutoSize = false, Height = 26, Visible = false };
        readonly Button _addProject = new Button { Text = "Add field \"Project\"...", AutoSize = true, Enabled = false };
        readonly Button _addClient = new Button { Text = "Add field \"Client\"...", AutoSize = true, Enabled = false };
        Dictionary<string, List<FieldDefinition>> _fields = new Dictionary<string, List<FieldDefinition>>(StringComparer.OrdinalIgnoreCase);

        public TmStampDialog(bool wantProject, bool wantClient)
        {
            _wantProject = wantProject; _wantClient = wantClient;
            Text = "This project's translation memories";
            Font = new Font("Segoe UI", 9F);
            Icon = IconHelper.AppIcon;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = MaximizeBox = false;
            ClientSize = new Size(720, 380);

            _list.Columns.Add("Translation memory", 300);
            _list.Columns.Add("Kind", 90);
            _list.Columns.Add("Update", 60);
            _list.Columns.Add("Project field", 100);
            _list.Columns.Add("Client field", 100);
            _list.SelectedIndexChanged += (s, e) => UpdateButtons();

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 6, 0, 0) };
            var close = new Button { Text = "Close", DialogResult = DialogResult.OK, AutoSize = true };
            buttons.Controls.AddRange(new Control[] { _addProject, _addClient, close });
            AcceptButton = close;
            _addProject.Click += (s, e) => AddField("Project");
            _addClient.Click += (s, e) => AddField("Client");
            _allowHere.CheckedChanged += (s, e) => SaveAllowHere();

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
            body.Controls.Add(_list);
            body.Controls.Add(_intro);
            body.Controls.Add(_allowHere);
            body.Controls.Add(_status);
            body.Controls.Add(buttons);
            Controls.Add(body);

            _project = TmFieldStampMonitor.Project;
            if (_project == null)
            {
                _intro.Text = "No project is open. Open a project (or one of its documents) in Studio, then come back here.";
                _intro.Height = 40;
                _list.Enabled = false;
                return;
            }
            var info = _project.GetProjectInfo();
            _fromElsewhere = TmFieldStamping.IsFromElsewhere(info.ProjectType);
            _intro.Text = "Project \"" + info.Name + "\". Studio stores a stamped value only in a TM that has a field of that name. " +
                          "Adding a field changes the TM, so it is done only when you say so, one TM at a time.";
            _intro.Height = 44;
            if (_fromElsewhere)
            {
                _allowHere.Visible = true;
                _allowHere.Text = "This project came from a package or GroupShare: stamp its name and client into its TMs anyway";
                _allowHere.Checked = TmFieldStampMonitor.AllowedInImportedProject(_project);
            }
            _status.Text = TmFieldStampMonitor.LastOutcome ?? "";
            try { _tms.AddRange(TmFieldStamping.TmsOf(_project).GroupBy(t => t.Uri.ToString()).Select(g => g.First())); }
            catch (Exception ex) { _status.Text = "The project's TMs could not be read: " + ex.Message; }
            Shown += (s, e) => LoadFields();
        }

        /// <summary>Reads each file TM's fields on the thread pool (cached), then fills the list.</summary>
        void LoadFields()
        {
            UseWaitCursor = true;
            var paths = _tms.Where(t => t.IsFile).Select(t => t.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Task.Run(() => paths.ToDictionary(p => p, TmFieldStamping.FieldsOf, StringComparer.OrdinalIgnoreCase)).ContinueWith(t =>
            {
                if (IsDisposed) return;
                UseWaitCursor = false;
                if (t.Exception == null) _fields = t.Result;
                Fill();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void Fill()
        {
            _list.Items.Clear();
            foreach (var tm in _tms)
            {
                string name = tm.IsFile ? Path.GetFileNameWithoutExtension(tm.Path) : TmSearcher.DisplayName(tm.Uri.ToString());
                var fields = tm.IsFile && _fields.TryGetValue(tm.Path, out var f) ? f : null;
                string Has(string canonical) => !tm.IsFile ? "(on the server)" : fields == null ? "(can't open)"
                    : TmFieldStamping.Find(fields, canonical) != null ? "yes" : fields.Any(x => VaultFields.Canonical(x.Name) == canonical) ? "not text" : "no";
                var item = new ListViewItem(new[] { name + (tm.Language != null ? " (" + tm.Language + ")" : ""), tm.IsFile ? "File" : "GroupShare", tm.Update ? "yes" : "no", Has(VaultFields.Project), Has(VaultFields.Client) }) { Tag = tm };
                _list.Items.Add(item);
            }
            UpdateButtons();
        }

        TmFieldStamping.ProjectTm Selected => _list.SelectedItems.Count == 1 ? (TmFieldStamping.ProjectTm)_list.SelectedItems[0].Tag : null;

        void UpdateButtons()
        {
            var tm = Selected;
            _addProject.Enabled = tm != null && TmFieldStamping.CannotAddField(tm, _fromElsewhere, "Project") == null;
            _addClient.Enabled = tm != null && TmFieldStamping.CannotAddField(tm, _fromElsewhere, "Client") == null;
            if (tm != null && !_addProject.Enabled)
                _status.Text = "Can't add a field to this TM: " + TmFieldStamping.CannotAddField(tm, _fromElsewhere, "Project") + ".";
        }

        void AddField(string fieldName)
        {
            var tm = Selected;
            if (tm == null) return;
            var why = TmFieldStamping.CannotAddField(tm, _fromElsewhere, fieldName);
            if (why != null) { _status.Text = "Can't add a field to this TM: " + why + "."; return; }
            var answer = MessageBox.Show(this,
                "Add a field \"" + fieldName + "\" to the translation memory " + Path.GetFileName(tm.Path) + "?\n\n" +
                "This changes the TM. From now on, every unit Studio adds to it from a project with this switched on can carry " +
                "the project's " + (fieldName == "Project" ? "name" : "client") + ". Units already in it are not changed.\n\n" +
                "Only do this for your own TMs, never for a TM that belongs to a client or an agency.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
            try
            {
                Cursor = Cursors.WaitCursor;
                TmFieldStamping.AddField(tm.Path, fieldName);
                _fields[tm.Path] = TmFieldStamping.FieldsOf(tm.Path);
                _status.Text = "Added \"" + fieldName + "\" to " + Path.GetFileName(tm.Path) + ".";
                TmFieldStampMonitor.Run("field added");
            }
            catch (Exception ex) { _status.Text = "The field was not added: " + ex.Message; }
            finally { Cursor = Cursors.Default; }
            Fill();
        }

        void SaveAllowHere()
        {
            try
            {
                TmFieldStampMonitor.SetAllowedInImportedProject(_project, _allowHere.Checked);
                TmFieldStampMonitor.Run("switched for this project");
            }
            catch (Exception ex) { _status.Text = "Could not save the switch: " + ex.Message; }
        }
    }
}
