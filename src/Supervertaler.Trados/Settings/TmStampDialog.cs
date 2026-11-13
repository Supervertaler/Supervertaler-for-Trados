using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Sdl.LanguagePlatform.TranslationMemory;
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
    ///
    /// Also says plainly (13 Nov 2026, from a real project):
    ///  - a field Studio leaves out because two TMs define it differently, naming the TMs and how each defines it;
    ///  - a value already in the project's field values that isn't this project's (a template that carried an older
    ///    project's name), with a button to replace it, also when Studio no longer lists that field (it still applies
    ///    it). Never replaced without that button.
    /// A field is added with the name and type the project's other TMs already use, or not at all.
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
        readonly Label _notes = new Label { Dock = DockStyle.Bottom, AutoSize = false, Height = 0, ForeColor = Color.DarkRed };
        readonly Button _useOwn = new Button { Text = "Use this project's values...", AutoSize = true, Visible = false };
        List<TmFieldStamping.Stale> _stale = new List<TmFieldStamping.Stale>();
        string _customer;
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
            buttons.Controls.AddRange(new Control[] { _addProject, _addClient, _useOwn, close });
            AcceptButton = close;
            _addProject.Click += (s, e) => AddField(VaultFields.Project);
            _addClient.Click += (s, e) => AddField(VaultFields.Client);
            _useOwn.Click += (s, e) => UseOwnValues();
            _allowHere.CheckedChanged += (s, e) => SaveAllowHere();

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
            body.Controls.Add(_list);
            body.Controls.Add(_intro);
            body.Controls.Add(_allowHere);
            body.Controls.Add(_notes);
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

        /// <summary>Reads each file TM's fields and the project's customer on the thread pool (cached), then fills the list.</summary>
        void LoadFields()
        {
            UseWaitCursor = true;
            var paths = _tms.Where(t => t.IsFile).Select(t => t.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string projectFile = null;
            try { projectFile = _project.FilePath; } catch { }
            Task.Run(() =>
            {
                string customer = null;
                if (_wantClient && projectFile != null)
                    try { customer = SuperVault.StudioProjects.ReadProject(projectFile).Item2; } catch { }
                return (fields: paths.ToDictionary(p => p, TmFieldStamping.FieldsOf, StringComparer.OrdinalIgnoreCase), customer);
            }).ContinueWith(t =>
            {
                if (IsDisposed) return;
                UseWaitCursor = false;
                if (t.Exception == null) { _fields = t.Result.fields; _customer = t.Result.customer; }
                Fill();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Each file TM once, with its fields, for the project-wide rules.</summary>
        IEnumerable<(string tm, IEnumerable<FieldDefinition> fields)> PerTm() =>
            _tms.Where(t => t.IsFile).GroupBy(t => t.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .Select(t => (TmFieldStamping.TmName(t), (IEnumerable<FieldDefinition>)(_fields.TryGetValue(t.Path, out var f) ? f : null) ?? Enumerable.Empty<FieldDefinition>()));

        Dictionary<string, string> Wanted() =>
            TmFieldStamping.Wanted(_wantProject, _wantClient, _project.GetProjectInfo().Name, _customer);

        void Fill()
        {
            _list.Items.Clear();
            var perTm = PerTm().ToList();
            var clashing = new[] { VaultFields.Project, VaultFields.Client }.ToDictionary(c => c, c => TmFieldStamping.Clashing(TmFieldStamping.Uses(perTm, c)));
            foreach (var tm in _tms)
            {
                string name = tm.IsFile ? Path.GetFileNameWithoutExtension(tm.Path) : TmSearcher.DisplayName(tm.Uri.ToString());
                var fields = tm.IsFile && _fields.TryGetValue(tm.Path, out var f) ? f : null;
                string Has(string canonical)
                {
                    if (!tm.IsFile) return "(on the server)";
                    if (fields == null) return "(can't open)";
                    var own = fields.FirstOrDefault(x => VaultFields.Canonical(x.Name) == canonical);
                    if (own == null) return "no";
                    // Never a plain "yes" for a field Studio leaves out: the line under the list says why.
                    if (clashing[canonical].Any(u => u.Tm == TmFieldStamping.TmName(tm) && u.Name == own.Name)) return "clash: see below";
                    return TmFieldStamping.Find(fields, canonical) != null ? "yes (\"" + own.Name + "\")" : "not text";
                }
                var item = new ListViewItem(new[] { name + (tm.Language != null ? " (" + tm.Language + ")" : ""), tm.IsFile ? "File" : "GroupShare", tm.Update ? "yes" : "no", Has(VaultFields.Project), Has(VaultFields.Client) }) { Tag = tm };
                _list.Items.Add(item);
            }

            // What the list alone can't show: a field Studio leaves out, and a value that isn't this project's.
            var notes = new List<string>();
            foreach (var kv in clashing.Where(kv => kv.Value.Count > 0))
                notes.Add("Studio leaves the " + kv.Key + " field out, because the TMs define it differently: " + TmFieldStamping.DescribeClash(kv.Value) +
                          ". Nothing new is stamped into it until they are the same (Studio's TM settings > Fields).");
            _stale = ReadStale();
            foreach (var x in _stale)
                notes.Add(x.Canonical + " is \"" + x.Value + "\"" + (x.Where != null ? " for " + x.Where : "") + "; this project's " +
                          (x.Canonical == VaultFields.Project ? "name" : "client") + " is \"" + x.Wanted + "\". It was there already, so it is kept" +
                          (clashing.TryGetValue(x.Canonical, out var c) && c.Any(u => u.Name == x.Field) ? " (Studio doesn't list this field now, but still applies the value)" : "") + ".");
            _notes.Text = string.Join("\n", notes);
            // As tall as the text needs at the window's width (the notes are whole sentences); the list gives way.
            _notes.Height = notes.Count == 0 ? 0 : Math.Min(ClientSize.Height / 2,
                TextRenderer.MeasureText(_notes.Text, _notes.Font, new Size(Math.Max(200, ClientSize.Width - 30), 0), TextFormatFlags.WordBreak).Height + 8);
            _useOwn.Visible = _stale.Count > 0;
            UpdateButtons();
        }

        /// <summary>The values that aren't this project's, in the project's settings and each language pair's own. UI thread.</summary>
        List<TmFieldStamping.Stale> ReadStale()
        {
            var list = new List<TmFieldStamping.Stale>();
            try
            {
                var wanted = Wanted();
                if (wanted.Count == 0) return list;
                FieldValues ValuesOf(Sdl.Core.Settings.ISettingsBundle b) => b.GetSettingsGroup<Sdl.ProjectAutomation.Settings.TranslationMemorySettings>().ProjectSettings.Value;
                list.AddRange(TmFieldStamping.StaleValues(ValuesOf(_project.GetSettings()), wanted, null));
                foreach (var lang in _tms.Where(t => t.Language != null).Select(t => t.Language).Distinct())
                {
                    var l = _project.GetProjectInfo().TargetLanguages.FirstOrDefault(x => x.IsoAbbreviation == lang);
                    if (l != null) list.AddRange(TmFieldStamping.StaleValues(ValuesOf(_project.GetSettings(l)), wanted, lang));
                }
            }
            catch (Exception ex) { _status.Text = "The project's field values could not be read: " + ex.Message; }
            return list;
        }

        /// <summary>Replaces the values that aren't this project's, after the user said yes to exactly what changes.</summary>
        void UseOwnValues()
        {
            if (_stale.Count == 0) return;
            var answer = MessageBox.Show(this,
                "Replace these values in this project's field values?\n\n" +
                string.Join("\n", _stale.Select(x => x.Field + (x.Where != null ? " (" + x.Where + ")" : "") + ": \"" + x.Value + "\" becomes \"" + x.Wanted + "\"")) +
                "\n\nStudio stamps these into every unit you confirm from now on. Units already in your TMs are not changed.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
            try
            {
                int n = TmFieldStamping.ReplaceStale(_project, _stale);
                _status.Text = n == 1 ? "1 value replaced." : n + " values replaced.";
                TmFieldStampMonitor.Run("values replaced");
            }
            catch (Exception ex) { _status.Text = "The values were not replaced: " + ex.Message; }
            Fill();
        }

        TmFieldStamping.ProjectTm Selected => _list.SelectedItems.Count == 1 ? (TmFieldStamping.ProjectTm)_list.SelectedItems[0].Tag : null;

        (FieldDefinition field, string refused) Plan(TmFieldStamping.ProjectTm tm, string canonical) =>
            TmFieldStamping.FieldToAdd(tm, _tms, _fromElsewhere, canonical);

        void UpdateButtons()
        {
            var tm = Selected;
            var reasons = new List<string>();
            foreach (var (button, canonical) in new[] { (_addProject, VaultFields.Project), (_addClient, VaultFields.Client) })
            {
                var plan = tm == null ? (null, null) : Plan(tm, canonical);
                button.Enabled = plan.field != null;
                // The button says the exact name it will add: the one the project's other TMs already use.
                button.Text = "Add field \"" + (plan.field?.Name ?? canonical) + "\"...";
                if (tm != null && plan.refused != null) reasons.Add(canonical + ": " + plan.refused);
            }
            if (tm != null && reasons.Count > 0) _status.Text = "Can't add a field to this TM. " + string.Join(". ", reasons) + ".";
        }

        void AddField(string canonical)
        {
            var tm = Selected;
            if (tm == null) return;
            var (field, why) = Plan(tm, canonical);
            if (field == null) { _status.Text = "Can't add a field to this TM: " + why + "."; return; }
            var answer = MessageBox.Show(this,
                "Add a field \"" + field.Name + "\" (" + TmFieldStamping.Describe(field.ValueType) + ") to the translation memory " + Path.GetFileName(tm.Path) + "?\n\n" +
                "This changes the TM. From now on, every unit Studio adds to it from a project with this switched on can carry " +
                "the project's " + (canonical == VaultFields.Project ? "name" : "client") + ". Units already in it are not changed.\n\n" +
                (field.Name != canonical || field.ValueType != FieldValueType.MultipleString
                    ? "It gets the same name and type as in the project's other TMs, so Studio treats them as one field.\n\n" : "") +
                "Only do this for your own TMs, never for a TM that belongs to a client or an agency.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
            try
            {
                Cursor = Cursors.WaitCursor;
                TmFieldStamping.AddField(tm.Path, field);
                _fields[tm.Path] = TmFieldStamping.FieldsOf(tm.Path);
                _status.Text = "Added \"" + field.Name + "\" to " + Path.GetFileName(tm.Path) + ".";
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
