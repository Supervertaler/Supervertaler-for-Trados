using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Sdl.ProjectAutomation.FileBased;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.Settings
{
    /// <summary>
    /// "Apply a job profile" (step 2): pick a Studio project template and apply it to the project open in Studio -
    /// Supervertaler's set-up, the template's Studio termbases and TMs (added to the project's own, never replacing them), and the project's Studio customer. Shows what applying would do
    /// before anything changes, asks before applying (and before adding a customer to Studio's list), and shows a short
    /// summary afterwards. The template is read fresh from disk each time, off the UI thread (it may be on a NAS); Studio
    /// is written on the UI thread only (<see cref="JobProfileStudio"/>).
    /// </summary>
    internal sealed class JobProfileDialog : Form
    {
        readonly ListView _list = new ListView { View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, Dock = DockStyle.Fill };
        readonly TextBox _filter = new TextBox { Dock = DockStyle.Top };
        readonly Label _intro = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 44 };
        readonly ListView _plan = new ListView { View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, Dock = DockStyle.Fill };
        readonly TextBox _notes = new TextBox { Dock = DockStyle.Bottom, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 84, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control };
        readonly Label _planTitle = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 24, TextAlign = ContentAlignment.BottomLeft, Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
        Font _bold;
        readonly Button _apply = new Button { Text = "Apply to this project...", AutoSize = true, Enabled = false };
        readonly Button _customers = new Button { Text = "Studio customers...", AutoSize = true };
        readonly FileBasedProject _project;
        ProjectFacts _facts;
        List<TradosTools.TemplateListing> _templates = new List<TradosTools.TemplateListing>();
        PreparedProfile _prepared;
        int _read;   // the newest template read; older ones that finish later are ignored
        bool _busy;

        public JobProfileDialog()
        {
            Text = "Apply a job profile";
            Font = new Font("Segoe UI", 9F);
            Icon = IconHelper.AppIcon;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ClientSize = new Size(900, 780);
            MinimumSize = new Size(640, 520);

            _list.Columns.Add("Job profile (Studio project template)", 300);
            _list.Columns.Add("Studio", 130);
            _list.Columns.Add("Folder", 360);
            _list.SelectedIndexChanged += (s, e) => ReadSelected();
            _filter.TextChanged += (s, e) => Fill();

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 6, 0, 0) };
            var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true };
            buttons.Controls.AddRange(new Control[] { _apply, _customers, close });
            CancelButton = close;
            _apply.Click += (s, e) => ApplySelected();
            _customers.Click += (s, e) =>
            {
                using (var d = new StudioCustomersDialog()) d.ShowDialog(this);
                RefreshFacts();
                ReadSelected();
            };

            _plan.Columns.Add("Item", 420);
            _plan.Columns.Add("What applying does", 380);
            _bold = new Font(_plan.Font, FontStyle.Bold);
            Disposed += (s, e) => _bold.Dispose();
            // The list of job profiles above, what the chosen one holds below; drag the bar between them to give either more room.
            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterWidth = 6,
            };
            split.Panel1.Controls.Add(_list);
            split.Panel2.Padding = new Padding(0, 2, 0, 4);
            split.Panel2.Controls.Add(_plan);
            split.Panel2.Controls.Add(_notes);
            split.Panel2.Controls.Add(_planTitle);

            var filterLabel = new Label { Text = "Filter:", Dock = DockStyle.Top, AutoSize = false, Height = 18 };
            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
            body.Controls.Add(split);
            body.Controls.Add(_filter);
            body.Controls.Add(filterLabel);
            body.Controls.Add(_intro);
            body.Controls.Add(buttons);
            Controls.Add(body);
            // Most of the room goes to what the job profile holds; a third to the list.
            // The minimum sizes are set only now: on a SplitContainer that is still at its small default size they throw.
            Shown += (s, e) =>
            {
                split.Panel1MinSize = 80;
                split.Panel2MinSize = 140;
                split.SplitterDistance = System.Math.Max(80, split.Height / 3);
            };

            _project = TmFieldStampMonitor.Project;
            RefreshFacts();
            Shown += (s, e) => LoadTemplates();
        }

        /// <summary>
        /// True once a job profile has been applied (in full or in part) while this window was open. The Settings window
        /// that opened it then re-reads the project's termbase and prompt choices: it still shows the ones from before,
        /// and its OK would write those back over what was just applied.
        /// </summary>
        public bool Applied { get; private set; }

        void RefreshFacts()
        {
            if (_project == null)
            {
                _facts = null;
                _intro.Text = "No project is open. Open a project (or one of its documents) in Studio, then come back here. " +
                              "You can still look at what a job profile holds.";
                return;
            }
            try
            {
                _facts = JobProfileStudio.Facts(_project);
                _intro.Text = "Project \"" + _facts.Name + "\" (" + _facts.Source + " → " + string.Join(", ", _facts.Targets) + "). " +
                              "A job profile is a Studio project template: pick one to see what applying it would change.";
            }
            catch (Exception ex)
            {
                _facts = null;
                _intro.Text = "The open project could not be read: " + ex.Message;
            }
        }

        /// <summary>A message in place of the table: reading, an error, nothing chosen.</summary>
        void ShowMessage(string text)
        {
            _plan.Items.Clear();
            _plan.Groups.Clear();
            _planTitle.Text = "";
            _notes.Text = text ?? "";
        }

        /// <summary>The table: every TM, termbase, customer and Supervertaler setting, grouped, and what applying does to it.</summary>
        void ShowPlan(PreparedProfile prepared)
        {
            var rows = JobProfile.Rows(prepared, _facts, out var notes);
            _planTitle.Text = _facts == null
                ? "What job profile \"" + prepared.Template.ProfileName + "\" holds (no project is open)"
                : "Applying \"" + prepared.Template.ProfileName + "\" to this project";
            _plan.BeginUpdate();
            _plan.Items.Clear();
            _plan.Groups.Clear();
            var groups = new Dictionary<string, ListViewGroup>();
            foreach (var r in rows)
            {
                if (!groups.TryGetValue(r.Group, out var g)) { g = new ListViewGroup(r.Group, r.Group); groups[r.Group] = g; _plan.Groups.Add(g); }
                var item = new ListViewItem(new[] { r.Item, r.Action }, g) { UseItemStyleForSubItems = false };
                if (r.Changes) item.SubItems[1].Font = _bold;
                _plan.Items.Add(item);
            }
            _plan.EndUpdate();
            var all = new List<string>();
            if (_facts != null && !_facts.FromElsewhere) all.Add("Nothing is removed: the project keeps all its Studio TMs and termbases. What applying changes is in bold.");
            all.AddRange(notes);
            all.Add("A template sent to someone else carries these names too (customer, client, bank and prompt names).");
            _notes.Text = string.Join("\r\n", all);
        }

        /// <summary>Studio's registered templates and the ones in its template folders: projects.xml and folder listings only.</summary>
        void LoadTemplates()
        {
            UseWaitCursor = true;
            Task.Run(() => TradosTools.FindProjectTemplates()).ContinueWith(t =>
            {
                if (IsDisposed) return;
                UseWaitCursor = false;
                if (t.Exception != null)
                {
                    ShowMessage("Studio's project templates could not be listed: " + t.Exception.GetBaseException().Message);
                    return;
                }
                _templates = JobProfile.SortByName(t.Result, x => Path.GetFileNameWithoutExtension(x.Path));
                Fill();
                ShowMessage(_templates.Count == 0 ? "No Studio project templates were found. Make one in Studio (Projects view > Create Project Template), then come back here." : "Pick a job profile to see what it holds and what applying it would change.");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void Fill()
        {
            var words = (_filter.Text ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var t in _templates)
            {
                var name = Path.GetFileNameWithoutExtension(t.Path);
                if (!words.All(w => (name + " " + t.Path).IndexOf(w, StringComparison.CurrentCultureIgnoreCase) >= 0)) continue;
                var studio = string.Join(", ", t.RegisteredIn.Count > 0 ? t.RegisteredIn : t.FoundIn);
                _list.Items.Add(new ListViewItem(new[] { name, studio, Path.GetDirectoryName(t.Path) ?? "" }) { Tag = t });
            }
            _list.EndUpdate();
            UpdateButtons();
        }

        string SelectedPath => _list.SelectedItems.Count == 1 ? ((TradosTools.TemplateListing)_list.SelectedItems[0].Tag).Path : null;

        void UpdateButtons() => _apply.Enabled = !_busy && _facts != null && _prepared != null && SelectedPath != null
                                                && string.Equals(_prepared.Template.Path, SelectedPath, StringComparison.OrdinalIgnoreCase);

        /// <summary>Reads the chosen template off the UI thread and shows what applying it would do.</summary>
        void ReadSelected()
        {
            _prepared = null;
            UpdateButtons();
            var path = SelectedPath;
            if (path == null) { ShowMessage("Pick a job profile to see what it holds and what applying it would change."); return; }
            int read = ++_read;
            ShowMessage("Reading " + path + " ...");
            var facts = _facts ?? new ProjectFacts();
            Task.Run(() =>
            {
                var error = JobProfileStudio.Prepare(path, facts, out var prepared);
                return (error, prepared);
            }).ContinueWith(t =>
            {
                if (IsDisposed || read != _read) return;
                if (t.Exception != null) { ShowMessage("The template could not be read: " + t.Exception.GetBaseException().Message); return; }
                var (error, prepared) = t.Result;
                if (error != null) { ShowMessage(error); return; }
                _prepared = prepared;
                ShowPlan(prepared);
                UpdateButtons();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Reads the template once more (it may have changed), asks, applies, and shows the summary.</summary>
        void ApplySelected()
        {
            var path = SelectedPath;
            if (path == null || _project == null) return;
            var now = TmFieldStampMonitor.Project;
            if (now == null || !string.Equals(now.FilePath, _project.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "The project open in Studio is not the one this window was opened for. Close this window and open it again.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            RefreshFacts();
            if (_facts == null) return;
            var facts = _facts;
            _busy = true;
            UpdateButtons();
            UseWaitCursor = true;
            Task.Run(() =>
            {
                var error = JobProfileStudio.Prepare(path, facts, out var prepared);
                return (error, prepared);
            }).ContinueWith(t =>
            {
                if (IsDisposed) return;
                UseWaitCursor = false;
                _busy = false;
                try
                {
                    if (t.Exception != null) { ShowMessage("The template could not be read: " + t.Exception.GetBaseException().Message); return; }
                    var (error, prepared) = t.Result;
                    if (error != null) { ShowMessage(error); MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                    Confirm(prepared, facts);
                }
                finally { UpdateButtons(); }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void Confirm(PreparedProfile prepared, ProjectFacts facts)
        {
            // The table may be from before the template or the project changed: show what is true now.
            _prepared = prepared;
            ShowPlan(prepared);
            var question = JobProfile.ConfirmQuestion(prepared.Template.ProfileName, facts.Name, JobProfile.Rows(prepared, facts, out _));
            if (question == null)
            {
                MessageBox.Show(this, "The project already has everything in this job profile: applying it would change nothing.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this, question, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            bool addCustomer = false;
            if (!facts.FromElsewhere && prepared.Customer.Choice == CustomerChoice.AskToAdd)
            {
                var answer = MessageBox.Show(this,
                    "The template's customer \"" + prepared.Customer.Name + "\" is not in Studio's customer list.\r\n\r\n" +
                    "Yes: add it to Studio's list and make it this project's customer.\r\nNo: leave the project's customer as it is.\r\nCancel: apply nothing.",
                    Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (answer == DialogResult.Cancel) return;
                addCustomer = answer == DialogResult.Yes;
            }

            string summary;
            try
            {
                Cursor = Cursors.WaitCursor;
                summary = JobProfileStudio.Apply(_project, prepared, facts, addCustomer);
            }
            catch (Exception ex)
            {
                summary = "Applying the job profile stopped: " + ex.Message;
                DiagnosticLog.WriteAlways("JobProfile", "apply stopped: " + ex);
            }
            finally { Cursor = Cursors.Default; }
            Applied = true;
            RefreshFacts();
            MessageBox.Show(this, summary, Text, MessageBoxButtons.OK,
                summary.Contains("Not done:") ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            ReadSelected();   // the table now shows the project as it is after applying
        }
    }
}
