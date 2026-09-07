using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.Controls
{
    /// <summary>What the Images dialog shows: the state of the figure pipeline for the open project (#84).</summary>
    internal sealed class ImagesState
    {
        public bool ProjectOpen;
        public string ProjectName;
        /// <summary>The reference images folder, or empty when none is set.</summary>
        public string Folder;
        /// <summary>Image files in that folder, when it is set and exists; -1 when it does not exist.</summary>
        public int FolderImages;
        /// <summary>One line per Word document found beside the project, or the reason none was.</summary>
        public List<string> Documents = new List<string>();
        public int TotalImages;
        public int Labelled;
        public string BankName;
        public string FiguresPath;
        public DateTime? FiguresWritten;
        public int FiguresRows;
        /// <summary>True when figures.md was written without the AI pass, so the "what the drawing shows" column is missing.</summary>
        public bool FiguresWithoutVision;
        public bool AnalysisRunning;
        public string ProviderName;
    }

    /// <summary>The actions the dialog can run; each runs synchronously on the UI thread except Analyse, which starts a background run.</summary>
    internal sealed class ImagesActions
    {
        public Func<ImagesState> Refresh;
        public Action Browse;
        public Action Extract;
        public Action Analyse;
        public Action WriteFigures;
        public Action ShowReport;
    }

    /// <summary>
    /// The figure pipeline as one panel (#84): the folder, what the documents
    /// contain, the three stages as buttons with their cost stated, and the
    /// state of figures.md. Replaces five peer links that read as five
    /// unrelated choices, and needs no tooltips because the layout says the
    /// order. The overwrite confirmation and the re-entrancy guard live in the
    /// actions, not here.
    /// </summary>
    internal sealed class ImagesDialog : Form
    {
        private readonly ImagesActions _actions;
        private ImagesState _state;

        private readonly TextBox _txtFolder;
        private readonly Label _lblFolderNote;
        private readonly Label _lblFound;
        private readonly Button _btnExtract, _btnAnalyse, _btnWrite;
        private readonly Label _lblExtractCost, _lblAnalyseCost, _lblWriteCost;
        private readonly Label _lblFigures;
        private readonly Timer _poll;

        /// <summary>For the layout probe: the longest realistic state, no actions.</summary>
        public ImagesDialog() : this(null, new ImagesState
        {
            ProjectOpen = true,
            ProjectName = "Acme PROJ-001 (application as filed, drawings as filed, sequence listing)",
            Folder = @"D:\Google Drive\Jobs\Acme\0187\Acme PROJ-001 (application as filed)\Reference images folder with a long name",
            FolderImages = 14,
            Documents = new List<string>
            {
                "20260713-PROJ-001 Application as filed.docx: 0 images",
                "20260713-PROJ-001 Figures as filed.docx: 14 images, 14 with a figure label, paired by position and checked",
            },
            TotalImages = 14, Labelled = 14,
            BankName = "acme-proj-001",
            FiguresPath = @"D:\Supervertaler\memory-banks\acme-proj-001\figures.md",
            FiguresWritten = new DateTime(2026, 8, 26, 23, 58, 0),
            FiguresRows = 14, FiguresWithoutVision = true,
            ProviderName = "Claude (Anthropic)",
        }) { }

        public ImagesDialog(ImagesActions actions, ImagesState initial)
        {
            _actions = actions ?? new ImagesActions();
            _state = initial ?? new ImagesState();

            Icon = IconHelper.AppIcon;
            Text = "Images";
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(UiScale.Pixels(640), UiScale.Pixels(430));
            MinimumSize = new Size(UiScale.Pixels(520), UiScale.Pixels(380));

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(UiScale.Pixels(12)) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            int row = 0;

            var intro = Wrap("The images that belong to a document are often not in the file you translate, and what they show exists only as pixels. " +
                             "This panel finds them, keeps them in a folder, and writes what each one shows to figures.md in the memory bank, where every prompt reads it.");
            root.Controls.Add(intro, 0, row); root.SetColumnSpan(intro, 3); row++;

            // Folder
            root.Controls.Add(L("Folder:"), 0, row);
            // TabStop off: as the first control it took focus and opened with the
            // whole path selected, which reads as "something to edit". It is a label.
            _txtFolder = new TextBox { ReadOnly = true, TabStop = false, Dock = DockStyle.Fill, Margin = new Padding(0, UiScale.Pixels(3), UiScale.Pixels(6), 0) };
            root.Controls.Add(_txtFolder, 1, row);
            var btnBrowse = Btn("Browse\u2026"); btnBrowse.Click += (s, e) => Run(_actions.Browse);
            root.Controls.Add(btnBrowse, 2, row); row++;
            _lblFolderNote = Wrap(""); _lblFolderNote.ForeColor = Color.FromArgb(100, 100, 100);
            root.Controls.Add(_lblFolderNote, 1, row); root.SetColumnSpan(_lblFolderNote, 2); row++;

            // Found
            root.Controls.Add(L("Found:"), 0, row);
            _lblFound = Wrap("");
            root.Controls.Add(_lblFound, 1, row); root.SetColumnSpan(_lblFound, 2); row++;

            // The three stages, in the order they run, each with its cost beside it.
            var stages = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = new Padding(0, UiScale.Pixels(10), 0, UiScale.Pixels(6)) };
            stages.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            stages.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            _btnExtract = Stage("Extract images to folder", out _lblExtractCost, stages, 0); _btnExtract.Click += (s, e) => Run(_actions.Extract);
            _btnAnalyse = Stage("Analyse with AI", out _lblAnalyseCost, stages, 1); _btnAnalyse.Click += (s, e) => Run(_actions.Analyse);
            _btnWrite = Stage("Write figures.md", out _lblWriteCost, stages, 2); _btnWrite.Click += (s, e) => Run(_actions.WriteFigures);
            root.Controls.Add(stages, 0, row); root.SetColumnSpan(stages, 3); row++;

            // figures.md state
            _lblFigures = Wrap(""); _lblFigures.ForeColor = Color.FromArgb(70, 70, 70);
            root.Controls.Add(_lblFigures, 0, row); root.SetColumnSpan(_lblFigures, 3); row++;

            // filler
            root.RowStyles.Clear();
            for (int i = 0; i < row; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.Controls.Add(new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty }, 0, row); row++;

            // bottom row: report link, help, close
            var bottom = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
            var btnClose = Btn("Close"); btnClose.Click += (s, e) => Close();
            bottom.Controls.Add(btnClose);
            var lnkReport = new LinkLabel { Text = "Document images report", AutoSize = true, Margin = new Padding(0, UiScale.Pixels(7), UiScale.Pixels(12), 0), LinkBehavior = LinkBehavior.HoverUnderline };
            lnkReport.LinkClicked += (s, e) => Run(_actions.ShowReport);
            new ToolTip().SetToolTip(lnkReport, "Every image in the project's Word documents, with its figure label and the text it sits among. Opens in the Chat tab. No AI call.");
            bottom.Controls.Add(lnkReport);
            var lnkHelp = new LinkLabel { Text = "? Help", AutoSize = true, Margin = new Padding(0, UiScale.Pixels(7), UiScale.Pixels(12), 0), LinkBehavior = LinkBehavior.HoverUnderline };
            lnkHelp.LinkClicked += (s, e) => Core.HelpSystem.OpenHelp(Core.HelpSystem.Topics.BatchOperations);
            bottom.Controls.Add(lnkHelp);
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(bottom, 0, row); root.SetColumnSpan(bottom, 3); row++;
            root.RowCount = row;

            Controls.Add(root);
            CancelButton = btnClose;
            Shown += (s, e) => btnClose.Focus();

            // While an analysis runs on a pool thread, the figures.md line follows it.
            _poll = new Timer { Interval = 1500 };
            _poll.Tick += (s, e) => { if (_state.AnalysisRunning) RefreshState(); };
            _poll.Start();
            FormClosed += (s, e) => _poll.Dispose();

            Render();
        }

        private void Run(Action action)
        {
            if (action == null) return;
            try { action(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Images", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            RefreshState();
        }

        private void RefreshState()
        {
            if (_actions.Refresh == null) return;
            try { _state = _actions.Refresh() ?? _state; } catch { }
            Render();
        }

        private void Render()
        {
            var st = _state;
            bool folderSet = !string.IsNullOrEmpty(st.Folder);
            _txtFolder.Text = folderSet ? st.Folder : "";
            _lblFolderNote.Text = !st.ProjectOpen ? "No project open."
                : !folderSet ? "Not set. Choose the folder holding this project's drawings; it is remembered per project."
                : st.FolderImages < 0 ? "The folder no longer exists."
                : st.FolderImages == 0 ? "Empty so far. Extract puts the document's images here, named for their figures."
                : st.FolderImages + " image file(s) in the folder.";

            var found = st.Documents.Count == 0
                ? "No Word documents found beside this project."
                : string.Join(Environment.NewLine, st.Documents);
            _lblFound.Text = found;

            bool haveImages = st.TotalImages > 0;
            _btnExtract.Enabled = st.ProjectOpen && folderSet && haveImages && !st.AnalysisRunning;
            _btnAnalyse.Enabled = st.ProjectOpen && folderSet && haveImages && !string.IsNullOrEmpty(st.BankName) && !st.AnalysisRunning;
            _btnWrite.Enabled = st.ProjectOpen && haveImages && !string.IsNullOrEmpty(st.BankName) && !st.AnalysisRunning;

            _lblExtractCost.Text = !folderSet ? "needs the folder" : "free, no AI";
            _lblAnalyseCost.Text = st.AnalysisRunning ? "running\u2026"
                : !folderSet ? "needs the folder"
                : string.IsNullOrEmpty(st.BankName) ? "needs an active memory bank"
                : haveImages ? st.TotalImages + " AI request(s) to " + (st.ProviderName ?? "the provider") + ", one per image; writes figures.md"
                : "no images";
            _lblWriteCost.Text = string.IsNullOrEmpty(st.BankName) ? "needs an active memory bank"
                : "free, no AI \u2013 what the text says about each figure, without the drawings";

            if (string.IsNullOrEmpty(st.BankName))
                _lblFigures.Text = "No memory bank is active, so there is nowhere to write figures.md.";
            else if (st.FiguresWritten == null)
                _lblFigures.Text = "figures.md not written yet in memory bank \u201c" + st.BankName + "\u201d.";
            else
                _lblFigures.Text = "figures.md last written " + st.FiguresWritten.Value.ToString("yyyy-MM-dd HH:mm")
                    + " \u00b7 " + st.FiguresRows + " figure(s)"
                    + (st.FiguresWithoutVision ? " \u00b7 from the text only, not yet analysed" : " \u00b7 with what the AI saw")
                    + " \u00b7 memory bank \u201c" + st.BankName + "\u201d, read into every prompt.";
        }

        private static Label L(string text) => new Label { Text = text, AutoSize = true, Margin = new Padding(0, UiScale.Pixels(6), UiScale.Pixels(8), 0) };

        private Label Wrap(string text)
        {
            var l = new Label { Text = text, AutoSize = true, MaximumSize = new Size(ClientSize.Width - UiScale.Pixels(40), 0), Margin = new Padding(0, UiScale.Pixels(4), 0, UiScale.Pixels(4)) };
            SizeChanged += (s, e) => l.MaximumSize = new Size(ClientSize.Width - UiScale.Pixels(40), 0);
            return l;
        }

        private static Button Btn(string text) => new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlatStyle = FlatStyle.System, Padding = new Padding(UiScale.Pixels(8), 0, UiScale.Pixels(8), 0), Margin = new Padding(0, UiScale.Pixels(3), 0, UiScale.Pixels(3)) };

        private static Button Stage(string text, out Label cost, TableLayoutPanel host, int r)
        {
            var b = Btn(text);
            b.Dock = DockStyle.Fill;
            cost = new Label { AutoSize = true, ForeColor = Color.FromArgb(100, 100, 100), Margin = new Padding(UiScale.Pixels(10), UiScale.Pixels(9), 0, 0) };
            host.Controls.Add(b, 0, r);
            host.Controls.Add(cost, 1, r);
            return b;
        }
    }
}
