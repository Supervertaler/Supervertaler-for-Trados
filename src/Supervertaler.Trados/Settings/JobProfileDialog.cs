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
        /// <summary>True while ShowPlan fills the table; it fits the columns itself when done.</summary>
        bool _filling;
        readonly Button _apply = new Button { Text = "Apply to this project...", AutoSize = true, Enabled = false };
        readonly Button _customers = new Button { Text = "Studio customers...", AutoSize = true };
        // Step 4: make a template from the open project, update one from it, edit Supervertaler's part of one.
        readonly Button _saveNew = new Button { Text = "Save project as template...", AutoSize = true, Enabled = false };
        readonly Button _update = new Button { Text = "Update from project...", AutoSize = true, Enabled = false };
        readonly Button _edit = new Button { Text = "Edit...", AutoSize = true, Enabled = false };
        /// <summary>The templates this Studio has in its list (only those can be changed); null when unknown.</summary>
        HashSet<string> _registered;
        string _selectAfterLoad;
        // Step 4: the same table shows either what the project template holds or what applying it would change.
        readonly RadioButton _showHolds = new RadioButton { Text = "What it holds", AutoSize = true };
        readonly RadioButton _showApply = new RadioButton { Text = "What applying it to the open project would change", AutoSize = true };
        // The chosen template's file; the path is a link that opens its folder with the file selected (Michael, 20 Nov).
        readonly LinkLabel _file = new LinkLabel { Dock = DockStyle.Bottom, AutoSize = false, Height = 22, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Visible = false };
        readonly FileBasedProject _project;
        ProjectFacts _facts;
        List<TradosTools.TemplateListing> _templates = new List<TradosTools.TemplateListing>();
        PreparedProfile _prepared;
        int _read;   // the newest template read; older ones that finish later are ignored
        bool _busy;

        public JobProfileDialog()
        {
            Text = "Project templates";
            Font = new Font("Segoe UI", 9F);
            Icon = IconHelper.AppIcon;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ClientSize = new Size(900, 780);
            MinimumSize = new Size(640, 520);

            _list.Columns.Add("Project template", 300);
            _list.Columns.Add("Studio", 130);
            _list.Columns.Add("Folder", 360);
            _list.SelectedIndexChanged += (s, e) => ReadSelected();
            _filter.TextChanged += (s, e) => Fill();

            // Seven buttons and the ? Help link: the row grows to two lines in a narrow window rather than cutting off what
            // does not fit (the Help link went missing at 900 pixels, Michael, 20 Nov).
            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true,
                FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 6, 0, 0),
            };
            var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true };
            buttons.Controls.AddRange(new Control[] { _apply, _saveNew, _update, _edit, _customers, close, StudioCustomersDialog.HelpLink(HelpSystem.Topics.ProjectTemplates, "Project templates") });
            CancelButton = close;
            _apply.Click += (s, e) => ApplySelected();
            _saveNew.Click += (s, e) => SaveProjectAsTemplate();
            _update.Click += (s, e) => UpdateFromProject();
            _edit.Click += (s, e) => EditSelected();
            var tips = new ToolTip();
            tips.SetToolTip(_saveNew, "Make a new project template from the open project: Studio's settings and Supervertaler's project settings.");
            tips.SetToolTip(_update, "Replace everything in the selected template with the open project's settings, as Studio's own update does.");
            tips.SetToolTip(_edit, "Change the Supervertaler project settings and the customer stored in the selected template.");
            _customers.Click += (s, e) =>
            {
                using (var d = new StudioCustomersDialog()) d.ShowDialog(this);
                RefreshFacts();
                ReadSelected();
            };

            _plan.Columns.Add("Item", 420);
            _plan.Columns.Add("What applying does", 380);
            _bold = new Font(_plan.Font, FontStyle.Bold);
            _plan.ClientSizeChanged += (s, e) => FitPlanColumns();
            Disposed += (s, e) => _bold.Dispose();
            // The list of project templates above, what the chosen one holds below; drag the bar between them to give either more room.
            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterWidth = 6,
            };
            split.Panel1.Controls.Add(_list);
            split.Panel2.Padding = new Padding(0, 2, 0, 4);
            split.Panel2.Controls.Add(_plan);
            split.Panel2.Controls.Add(_file);    // added before the notes, so it sits between the table and them
            split.Panel2.Controls.Add(_notes);
            _file.LinkClicked += (s, e) => ShowInFolder(_file.Tag as string);
            split.Panel2.Controls.Add(_planTitle);
            var show = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 28, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
            show.Controls.AddRange(new Control[] { new Label { Text = "Show:", AutoSize = true, Margin = new Padding(0, 5, 6, 0) }, _showHolds, _showApply });
            split.Panel2.Controls.Add(show);
            _showHolds.CheckedChanged += (s, e) => { if (_showHolds.Checked && _prepared != null) ShowPlan(_prepared); };
            _showApply.CheckedChanged += (s, e) => { if (_showApply.Checked && _prepared != null) ShowPlan(_prepared); };

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
            // With a project open, the window is mostly for applying; without one, it can only show what a profile holds.
            if (_facts != null) _showApply.Checked = true; else _showHolds.Checked = true;
            _showApply.Enabled = _facts != null;
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
                _intro.Text = "No project is open, so this window shows what each project template holds. To apply one, open a project " +
                              "(or one of its documents) in Studio, then come back here.";
                return;
            }
            try
            {
                _facts = JobProfileStudio.Facts(_project);
                _intro.Text = "The open project is \"" + _facts.Name + "\" (" + _facts.Source + " → " + string.Join(", ", _facts.Targets) + "). " +
                              "Pick a project template (Studio's, with Supervertaler's project settings stored inside it) to see what it holds, or what applying it to this project would change.";
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
            SetFile(null);
            _notes.Text = text ?? "";
        }

        /// <summary>The table: every TM, termbase, customer and Supervertaler setting, grouped, and what applying does to it.</summary>
        void ShowPlan(PreparedProfile prepared)
        {
            bool holds = _facts == null || !_showApply.Checked;
            List<string> notes;
            List<PreviewRow> rows;
            if (holds)
            {
                // Worked out off the UI thread when the template was read; never here, where a NAS could freeze the window.
                rows = prepared.Contents ?? new List<PreviewRow>();
                notes = new List<string>(prepared.ContentNotes ?? new List<string>());
            }
            else rows = JobProfile.Rows(prepared, _facts, out notes);
            _planTitle.Text = holds
                ? "What project template \"" + prepared.Template.ProfileName + "\" holds"
                : "Applying \"" + prepared.Template.ProfileName + "\" to this project";
            _plan.Columns[1].Text = holds ? "How it is set" : "What applying does";
            _filling = true;
            _plan.BeginUpdate();
            try
            {
            _plan.Items.Clear();
            _plan.Groups.Clear();
            // Two headings, Supervertaler and Trados Studio; Studio's parts ("Trados Studio: termbases") are subheadings
            // under the second (Michael, 20 Nov). A ListView's groups neither nest nor take a bold font, so every heading
            // is a row of its own: a heading in bold blue, a subheading in bold, indented one step, and each item one step
            // further in than its heading.
            const string Step = "      ";
            string lastTop = null, lastSub = null;
            foreach (var r in rows)
            {
                int colon = r.Group.IndexOf(": ", StringComparison.Ordinal);
                string top = colon < 0 ? (r.Group == JobProfile.GroupSupervertaler ? "Supervertaler" : r.Group) : r.Group.Substring(0, colon);
                string sub = colon < 0 ? r.Sub : r.Group.Substring(colon + 2);
                if (top != lastTop)
                {
                    if (lastTop != null) _plan.Items.Add(new ListViewItem(new[] { "", "" }));
                    _plan.Items.Add(new ListViewItem(new[] { top, "" }) { Font = _bold, ForeColor = SystemColors.HotTrack });
                    lastSub = null;
                }
                if (sub != null && sub != lastSub)
                    _plan.Items.Add(new ListViewItem(new[] { Step + char.ToUpper(sub[0]) + sub.Substring(1), "" }) { Font = _bold });
                lastTop = top;
                lastSub = sub;
                var item = new ListViewItem(new[] { (sub != null ? Step + Step : Step) + r.Item, r.Action }) { UseItemStyleForSubItems = false };
                if (r.Changes) item.SubItems[1].Font = _bold;
                _plan.Items.Add(item);
            }
            }
            finally
            {
                _plan.EndUpdate();
                _filling = false;
            }
            FitPlanColumns();
            var all = new List<string>();
            SetFile(prepared.Template.Path);
            if (!holds && !_facts.FromElsewhere) all.Add("Nothing is removed: the project keeps all its Studio TMs and termbases. What applying changes is in bold.");
            all.AddRange(notes);
            all.Add("A template sent to someone else carries these names too (customer, client, bank and prompt names).");
            _notes.Text = string.Join("\r\n", all);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F1) { HelpSystem.OpenHelp(HelpSystem.Topics.ProjectTemplates); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>"Template file: path", the path a link; hidden when no template is shown.</summary>
        void SetFile(string path)
        {
            _file.Tag = path;
            if (string.IsNullOrEmpty(path)) { _file.Text = ""; _file.Visible = false; return; }
            const string label = "Template file: ";
            _file.Text = label + path;
            _file.LinkArea = new LinkArea(label.Length, path.Length);
            _file.Visible = true;
        }

        /// <summary>Opens the template's folder in Explorer with the file selected; says so if it is gone.</summary>
        void ShowInFolder(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (File.Exists(path))
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
                    else MessageBox.Show(this, "The template's folder is not there any more:\r\n" + dir, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "The folder could not be opened: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// The Item column as wide as its longest entry (but at most three fifths of the table), and the second column
        /// filling the rest, so the header has one dividing line, over the boundary between the two columns, and no stub
        /// column after them (Michael, 20 Nov).
        /// </summary>
        void FitPlanColumns()
        {
            // Not while the table is being filled: a row being added can make the scrollbar appear, which
            // resizes the table and lands here while Windows holds an empty slot for that row - a crash in
            // Studio 2024 (Michael, 21 Nov, the GTE template). ShowPlan fits the columns once it is done.
            if (_filling || IsDisposed) return;
            try
            {
                int width = _plan.ClientSize.Width;
                if (width <= 0 || _plan.Columns.Count < 2) return;
                int item = 200;
                foreach (ListViewItem i in _plan.Items)
                    if (i != null)
                        item = System.Math.Max(item, TextRenderer.MeasureText(i.Text ?? "", i.Font ?? _plan.Font).Width + 16);
                item = System.Math.Min(item, width * 3 / 5);
                _plan.Columns[0].Width = item;
                _plan.Columns[1].Width = System.Math.Max(120, width - item);
            }
            catch (Exception ex)
            {
                // Column widths are cosmetic: never a reason to stop the window, or Studio.
                DiagnosticLog.Log("JobProfile", "fitting the table's columns failed: " + ex.Message);
            }
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
                _registered = StudioTemplates.RegisteredPaths();
                Fill();
                ShowMessage(_templates.Count == 0 ? "No Studio project templates were found. Make one in Studio (Projects view > Create Project Template), then come back here." : "Pick a project template to see what it holds and what applying it would change.");
                var select = _selectAfterLoad;
                _selectAfterLoad = null;
                if (select != null)
                    foreach (ListViewItem item in _list.Items)
                        if (string.Equals(((TradosTools.TemplateListing)item.Tag).Path, select, StringComparison.OrdinalIgnoreCase))
                        {
                            item.Selected = true;
                            item.EnsureVisible();
                            break;
                        }
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

        void UpdateButtons()
        {
            bool read = !_busy && _prepared != null && SelectedPath != null
                        && string.Equals(_prepared.Template.Path, SelectedPath, StringComparison.OrdinalIgnoreCase);
            _apply.Enabled = read && _facts != null;
            _saveNew.Enabled = !_busy && _facts != null;
            // Changing a template needs it in this Studio's list (else Studio would overwrite the change); the click says so.
            _edit.Enabled = read;
            _update.Enabled = read && _facts != null;
        }

        bool Registered(string path)
        {
            if (_registered == null) return true;   // unknown: StudioTemplates checks again and says so
            try { return _registered.Contains(Path.GetFullPath(path)); } catch { return _registered.Contains(path); }
        }

        string NotInThisStudio(string path) =>
            "\"" + Path.GetFileNameWithoutExtension(path) + "\" is not in this Studio's list of project templates (it is " +
            (_list.SelectedItems.Count == 1 ? "listed for " + _list.SelectedItems[0].SubItems[1].Text : "elsewhere") + "). " +
            "Supervertaler changes a template only through the Studio that has it in its list, or Studio would overwrite the change. " +
            "Add it to this Studio first (Projects view > Project Templates > Import), or change it from the Studio that lists it.";

        /// <summary>Makes a new template from the open project: Studio's settings, and Supervertaler's from its own record.</summary>
        void SaveProjectAsTemplate()
        {
            if (_project == null || _facts == null) return;
            string folder = null;
            try
            {
                folder = SelectedPath != null ? Path.GetDirectoryName(SelectedPath)
                    : _templates.Where(t => Registered(t.Path)).Select(t => Path.GetDirectoryName(t.Path))
                        .GroupBy(d => d, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            }
            catch { }
            string path;
            using (var dlg = new SaveFileDialog
            {
                Title = "Save the open project as a project template",
                Filter = "Studio project templates (*.sdltpl)|*.sdltpl",
                DefaultExt = "sdltpl", AddExtension = true, OverwritePrompt = false,
                FileName = string.Join("_", _facts.Name.Split(Path.GetInvalidFileNameChars())),
            })
            {
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) dlg.InitialDirectory = folder;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                path = dlg.FileName;
            }
            var why = ProjectTemplateEdit.CheckNewTemplatePath(path, File.Exists);
            if (why != null) { MessageBox.Show(this, why, Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (!Ready(out var values)) return;
            string error;
            try { Cursor = Cursors.WaitCursor; error = StudioTemplates.SaveAsNew(_project, path, values); }
            finally { Cursor = Cursors.Default; }
            if (error != null) { MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            MessageBox.Show(this, "Saved \"" + _facts.Name + "\" as the project template \"" + Path.GetFileNameWithoutExtension(path) + "\":\r\n" + path +
                "\r\n\r\nIt holds Studio's settings and Supervertaler's project settings, and Studio now offers it under New Project.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            _selectAfterLoad = path;
            LoadTemplates();
        }

        /// <summary>Replaces everything in the selected template with the open project's settings, after asking.</summary>
        void UpdateFromProject()
        {
            var path = SelectedPath;
            if (path == null || _project == null || _facts == null) return;
            if (!Registered(path)) { MessageBox.Show(this, NotInThisStudio(path), Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            var name = Path.GetFileNameWithoutExtension(path);
            var question = "Replace everything in the project template \"" + name + "\" with the settings of the open project \"" + _facts.Name + "\"?\r\n\r\n" +
                           "This is what Studio does when it updates a template from a project: the template's TMs, termbases, customer, " +
                           "file types, QA and other Studio settings, and Supervertaler's project settings, all become the project's. " +
                           "Projects already made from the template do not change.";
            if (MessageBox.Show(this, question, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            if (!Ready(out var values)) return;
            string error;
            try { Cursor = Cursors.WaitCursor; error = StudioTemplates.UpdateFromProject(_project, path, values); }
            finally { Cursor = Cursors.Default; }
            if (error != null) { MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            MessageBox.Show(this, "The project template \"" + name + "\" now holds the settings of \"" + _facts.Name + "\".", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            ReadSelected();
        }

        /// <summary>Edits the Supervertaler project settings and the customer stored in the selected template.</summary>
        void EditSelected()
        {
            var path = SelectedPath;
            if (path == null || _prepared == null) return;
            if (!Registered(path)) { MessageBox.Show(this, NotInThisStudio(path), Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            bool saved;
            using (var d = new ProjectTemplateEditDialog(_prepared.Template)) { d.ShowDialog(this); saved = d.Saved; }
            if (saved) ReadSelected();
        }

        /// <summary>The open project is still the one this window is for, and Supervertaler's settings for it could be read.</summary>
        bool Ready(out ProjectProfileValues values)
        {
            values = null;
            var now = TmFieldStampMonitor.Project;
            if (now == null || !string.Equals(now.FilePath, _project.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "The project open in Studio is not the one this window was opened for. Close this window and open it again.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            values = ProjectProfileStore.CurrentValues(_project, _facts.Name);
            if (values == null)
            {
                MessageBox.Show(this, "Supervertaler's project settings for this project could not be read, so nothing was saved. " +
                    "The diagnostic log says why.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            return true;
        }

        /// <summary>Reads the chosen template off the UI thread and shows what applying it would do.</summary>
        void ReadSelected()
        {
            _prepared = null;
            UpdateButtons();
            var path = SelectedPath;
            if (path == null) { ShowMessage("Pick a project template to see what it holds and what applying it would change."); return; }
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
            _showApply.Checked = true;   // the question refers to this table
            ShowPlan(prepared);
            var question = JobProfile.ConfirmQuestion(prepared.Template.ProfileName, facts.Name, JobProfile.Rows(prepared, facts, out _));
            if (question == null)
            {
                MessageBox.Show(this, "The project already has everything in this project template: applying it would change nothing.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                summary = "Applying the project template stopped: " + ex.Message;
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
