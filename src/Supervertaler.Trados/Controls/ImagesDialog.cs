using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.Controls
{
    /// <summary>What the Images dialog shows: the state of the image pipeline for the open project (#84).</summary>
    internal sealed class ImagesState
    {
        public bool ProjectOpen;
        public string ProjectName;
        /// <summary>The images folder, or empty when none is chosen yet.</summary>
        public string Folder;
        /// <summary>Image files in that folder, when it is set and exists; -1 when it does not exist.</summary>
        public int FolderImages;
        /// <summary>One line per Word document in the project that has images (or could not be read).</summary>
        public List<string> Documents = new List<string>();
        /// <summary>Every Word document in the project, images or not.</summary>
        public int DocumentCount;
        public int DocumentsWithoutImages;
        public List<string> DocumentsWithoutImagesNames = new List<string>();
        /// <summary>True when the active bank is the shared one, which every project reads.</summary>
        public bool BankIsShared;
        /// <summary>The bank name the panel offers to create for this project.</summary>
        public string SuggestedBankName;
        public int TotalImages;
        public int Labelled;
        public string BankName;
        public string FiguresPath;
        public DateTime? FiguresWritten;
        public int FiguresRows;
        /// <summary>True when figures.md was written without the AI pass, so the "what the image shows" column is missing.</summary>
        public bool FiguresWithoutVision;
        public bool AnalysisRunning;
        public string ProviderName;
    }

    /// <summary>The actions the dialog can run; each runs synchronously on the UI thread except Analyse, which starts a background run.</summary>
    internal sealed class ImagesActions
    {
        public Func<ImagesState> Refresh;
        /// <summary>Step 1: ask for a folder if none is chosen yet, then extract into it.</summary>
        public Action ExtractChoosingFolder;
        /// <summary>Change the folder without extracting (the user already has the images somewhere).</summary>
        public Action ChangeFolder;
        public Action OpenFolder;
        public Action Analyse;
        public Action WriteFigures;
        public Action ShowReport;
        /// <summary>Create (or reuse) a memory bank named after the project and switch to it.</summary>
        public Action CreateProjectBank;
    }

    /// <summary>
    /// The image pipeline as one panel (#84), laid out as the two steps a new
    /// user takes: get the images out into a folder, then have them described
    /// for the AI. The folder is chosen inside step 1, not as a step of its own;
    /// "Change" exists for someone who already keeps the images in a folder. Cost
    /// is stated beside each button before it is spent. The overwrite
    /// confirmations and the re-entrancy guard live in the actions, not here.
    /// </summary>
    internal sealed class ImagesDialog : Form
    {
        private readonly ImagesActions _actions;
        private ImagesState _state;

        private readonly Label _lblDocs;
        private readonly ListBox _lstDocs;
        private readonly LinkLabel _lnkCreateBank;
        private readonly TableLayoutPanel _root;
        private readonly Button _btnExtract;
        private readonly Label _lblExtractNote;
        private readonly Label _lblFolder;
        private readonly LinkLabel _lnkChange, _lnkOpen;
        private readonly Button _btnAnalyse;
        private readonly Label _lblAnalyseNote;
        private readonly Button _btnWrite;
        private readonly Label _lblWriteNote;
        private readonly Label _lblResult;
        private readonly Timer _poll;

        /// <summary>For the layout probe: the longest realistic state, no actions.</summary>
        public ImagesDialog() : this(null, new ImagesState
        {
            ProjectOpen = true,
            ProjectName = "Acme PROJ-001 (application as filed, drawings as filed, sequence listing)",
            Folder = @"D:\Google Drive\Jobs\Acme\0187\Acme PROJ-001 (application as filed)\Images folder with a long name",
            FolderImages = 14,
            Documents = new List<string>
            {
                "20260713-PROJ-001 Figures as filed.docx: 14 images, 14 with a figure label, paired by position and checked",
                "20260713-PROJ-001 Figures as filed, sheet 2 of a very long document title that wraps.docx: 9 images, 9 with a figure label, labels taken from nearby text",
                "Annex A.docx: 2 images, 0 with a figure label",
                "Annex B.docx: 1 image, 1 with a figure label, paired by position and checked",
                "Annex C.docx: 1 image, 1 with a figure label, paired by position and checked",
                "Annex D.docx: 3 images, 3 with a figure label, paired by position and checked",
                "Annex E.docx: 1 image, 1 with a figure label, paired by position and checked",
            },
            DocumentCount = 60, DocumentsWithoutImages = 53,
            DocumentsWithoutImagesNames = new List<string> { "Annex F.docx", "Annex G.docx", "Annex H.docx" },
            BankIsShared = true, SuggestedBankName = "acme-proj-001-application-as-filed",
            TotalImages = 31, Labelled = 29,
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
            ClientSize = new Size(UiScale.Pixels(660), UiScale.Pixels(470));
            MinimumSize = new Size(UiScale.Pixels(540), UiScale.Pixels(420));

            var tips = new ToolTip { AutoPopDelay = 15000, InitialDelay = 300 };
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(UiScale.Pixels(12)) };
            _root = root;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            int row = 0;

            var intro = Wrap("The AI sees the text of your documents, not the pictures in them. Two steps give it a description of each image: " +
                             "get the images out of the documents into a folder, then have them described.");
            root.Controls.Add(intro, 0, row); root.SetColumnSpan(intro, 2); row++;

            // What is there
            root.Controls.Add(L("Your documents:"), 0, row);
            _lblDocs = Wrap("");
            root.Controls.Add(_lblDocs, 1, row); row++;
            // A list, not a label: a project can hold sixty files, and every one of
            // them should be findable here, the ones with images first.
            _lstDocs = new ListBox { Dock = DockStyle.Fill, Height = UiScale.Pixels(84), IntegralHeight = false, HorizontalScrollbar = true, SelectionMode = SelectionMode.None, Margin = new Padding(0, 0, 0, UiScale.Pixels(6)) };
            root.Controls.Add(_lstDocs, 1, row); row++;

            // Step 1
            root.Controls.Add(Step("Step 1"), 0, row);
            _btnExtract = Btn("Extract images to a folder\u2026");
            _btnExtract.Click += (s, e) => Run(_actions.ExtractChoosingFolder);
            tips.SetToolTip(_btnExtract, "Asks where to put the images the first time, then copies them out of the documents into that folder, named after their figure numbers (Figure 01.png, Figure 02.png...). Running it again replaces the copies.");
            _lblExtractNote = Note("");
            root.Controls.Add(Pair(_btnExtract, _lblExtractNote), 1, row); row++;

            var folderRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 0, 0, UiScale.Pixels(8)) };
            _lblFolder = new Label { AutoSize = true, ForeColor = Color.FromArgb(100, 100, 100), Margin = new Padding(0, UiScale.Pixels(2), UiScale.Pixels(8), 0) };
            _lnkChange = Lnk("Change\u2026"); _lnkChange.LinkClicked += (s, e) => Run(_actions.ChangeFolder);
            tips.SetToolTip(_lnkChange, "Use a different folder - for example one where you already keep the images. Remembered for this project.");
            _lnkOpen = Lnk("Open folder"); _lnkOpen.LinkClicked += (s, e) => Run(_actions.OpenFolder);
            folderRow.Controls.Add(_lblFolder); folderRow.Controls.Add(_lnkChange); folderRow.Controls.Add(_lnkOpen);
            root.Controls.Add(folderRow, 1, row); row++;

            // Step 2
            root.Controls.Add(Step("Step 2"), 0, row);
            _btnAnalyse = Btn("Describe images with AI");
            _btnAnalyse.Click += (s, e) => Run(_actions.Analyse);
            tips.SetToolTip(_btnAnalyse, "Shows each image to the AI, together with what the text says about it, and saves the descriptions where every prompt reads them. One paid request per image. Asks before replacing descriptions that already exist.");
            _lblAnalyseNote = Note("");
            root.Controls.Add(Pair(_btnAnalyse, _lblAnalyseNote), 1, row); row++;

            _btnWrite = Btn("Describe from the text only");
            _btnWrite.Click += (s, e) => Run(_actions.WriteFigures);
            tips.SetToolTip(_btnWrite, "The free alternative: saves what the document itself says about each figure, without looking at the images. Use one or the other. Asks before replacing descriptions that already exist.");
            _lblWriteNote = Note("");
            var alt = Pair(_btnWrite, _lblWriteNote); alt.Margin = new Padding(0, 0, 0, UiScale.Pixels(8));
            root.Controls.Add(alt, 1, row); row++;

            // Result
            root.Controls.Add(L("Result:"), 0, row);
            _lblResult = Wrap("");
            root.Controls.Add(_lblResult, 1, row); row++;
            _lnkCreateBank = Lnk(""); _lnkCreateBank.Margin = new Padding(0, 0, 0, UiScale.Pixels(6));
            _lnkCreateBank.LinkClicked += (s, e) => Run(_actions.CreateProjectBank);
            tips.SetToolTip(_lnkCreateBank, "Makes a memory bank named after this project (or reuses one with that name) and switches to it, so the descriptions belong to this project.");
            root.Controls.Add(_lnkCreateBank, 1, row); row++;

            // filler
            root.RowStyles.Clear();
            for (int i = 0; i < row; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.Controls.Add(new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty }, 0, row); row++;

            // bottom: report link, help, close
            var bottom = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
            var btnClose = Btn("Close"); btnClose.Click += (s, e) => Close();
            bottom.Controls.Add(btnClose);
            var lnkReport = Lnk("Document images report"); lnkReport.Margin = new Padding(0, UiScale.Pixels(7), UiScale.Pixels(12), 0);
            lnkReport.LinkClicked += (s, e) => Run(_actions.ShowReport);
            tips.SetToolTip(lnkReport, "Every image in the project's documents, with its figure label and the text around it. Opens in the Chat tab. No AI call.");
            bottom.Controls.Add(lnkReport);
            var lnkHelp = Lnk("? Help"); lnkHelp.Margin = new Padding(0, UiScale.Pixels(7), UiScale.Pixels(12), 0);
            lnkHelp.LinkClicked += (s, e) => HelpSystem.OpenHelp(HelpSystem.Topics.Images);
            bottom.Controls.Add(lnkHelp);
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(bottom, 0, row); root.SetColumnSpan(bottom, 2); row++;
            root.RowCount = row;

            Controls.Add(root);
            CancelButton = btnClose;
            ActiveControl = btnClose;

            // While a description run is on a pool thread, the result line follows it.
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
            RenderInner();
            FitHeight();
        }

        /// <summary>Tall enough for everything, never taller than the screen; the probe's sixty-document state is the test.</summary>
        private void FitHeight()
        {
            try
            {
                var pref = _root.GetPreferredSize(new Size(ClientSize.Width, 0));
                int wanted = pref.Height + UiScale.Pixels(8);
                int max = Screen.FromControl(this).WorkingArea.Height - UiScale.Pixels(80);
                if (wanted > ClientSize.Height) ClientSize = new Size(ClientSize.Width, Math.Min(wanted, max));
            }
            catch { }
        }

        private void RenderInner()
        {
            var st = _state;
            bool folderSet = !string.IsNullOrEmpty(st.Folder);
            bool haveImages = st.TotalImages > 0;
            bool haveBank = !string.IsNullOrEmpty(st.BankName) && !st.BankIsShared;
            string n = st.TotalImages + " image" + (st.TotalImages == 1 ? "" : "s");

            _lblDocs.Text = DocumentsText(st);
            _lstDocs.BeginUpdate();
            _lstDocs.Items.Clear();
            foreach (var d in st.Documents) _lstDocs.Items.Add(d);
            foreach (var d in st.DocumentsWithoutImagesNames) _lstDocs.Items.Add(d + ": no images");
            _lstDocs.EndUpdate();
            _lstDocs.Visible = st.DocumentCount > 0;

            // Step 1
            _btnExtract.Enabled = st.ProjectOpen && haveImages && !st.AnalysisRunning;
            _lblExtractNote.Text = !haveImages ? "Nothing to extract: the documents above have no images."
                : !folderSet ? "Copies the " + n + " out of the documents above. You will be asked where to put them; a new, empty folder next to the job is fine."
                : "Copies the " + n + " out of the documents above into the folder below. Free, no AI.";
            _lblFolder.Text = !folderSet ? "Folder: not chosen yet."
                : st.FolderImages < 0 ? "Folder: " + st.Folder + "  (no longer exists)"
                : "Folder: " + st.Folder + "  (" + st.FolderImages + " image file" + (st.FolderImages == 1 ? "" : "s") + ")";
            _lnkChange.Text = folderSet ? "Change\u2026" : "Already have the images in a folder? Choose it\u2026";
            _lnkOpen.Visible = folderSet && st.FolderImages >= 0;

            // Step 2
            _btnAnalyse.Enabled = st.ProjectOpen && haveImages && folderSet && haveBank && !st.AnalysisRunning;
            _lblAnalyseNote.Text = st.AnalysisRunning ? "Running\u2026"
                : !haveImages ? "No images to describe."
                : !folderSet ? "Do step 1 first."
                : !haveBank ? "Needs a memory bank for this project to save the descriptions in \u2013 see Result."
                : st.TotalImages + " AI request" + (st.TotalImages == 1 ? "" : "s") + " to " + (st.ProviderName ?? "the provider") + ", one per image.";
            _btnWrite.Enabled = st.ProjectOpen && haveImages && haveBank && !st.AnalysisRunning;
            _lblWriteNote.Text = !haveBank ? "Needs a memory bank for this project \u2013 see Result."
                : "Free, no AI: only what the document says about each figure. The alternative to the button above, not a third step.";

            // Result
            _lnkCreateBank.Visible = !haveBank && !string.IsNullOrEmpty(st.SuggestedBankName);
            _lnkCreateBank.Text = "Create memory bank \u201c" + st.SuggestedBankName + "\u201d for this project and switch to it";
            if (st.BankIsShared)
                _lblResult.Text = "The active memory bank is the shared one, which every project reads. Descriptions of this project's images belong in a bank of its own:";
            else if (!haveBank)
                _lblResult.Text = "No memory bank is active, so there is nowhere to save the descriptions:";
            else if (st.FiguresWritten == null)
                _lblResult.Text = "No descriptions yet. They are saved as figures.md in memory bank \u201c" + st.BankName + "\u201d, which the AI reads with every request.";
            else
                _lblResult.Text = "Descriptions saved " + st.FiguresWritten.Value.ToString("yyyy-MM-dd HH:mm")
                    + " \u00b7 " + st.FiguresRows + " figure" + (st.FiguresRows == 1 ? "" : "s")
                    + (st.FiguresWithoutVision ? " \u00b7 from the text only, the images not yet looked at" : " \u00b7 with what the AI saw")
                    + " \u00b7 figures.md in memory bank \u201c" + st.BankName + "\u201d, read by the AI with every request.";
        }

        /// <summary>
        /// A summary first, then only the documents that have images, bulleted and
        /// capped: a project can hold sixty files with pictures in three of them.
        /// </summary>
        private static string DocumentsText(ImagesState st)
        {
            if (!st.ProjectOpen) return "No project open.";
            if (st.DocumentCount == 0)
                return "No Word documents in this project. Images are read from the project's source documents, the files in Studio's Files view.";
            if (st.TotalImages == 0)
                return "No images in the " + Plural(st.DocumentCount, "document") + " of this project.";
            int withImages = st.Documents.Count;
            return Plural(st.TotalImages, "image") + " in " + withImages + " of " + Plural(st.DocumentCount, "document")
                 + (st.DocumentsWithoutImages > 0 ? "; the rest have none." : ".");
        }

        private static string Plural(int n, string noun) => n + " " + noun + (n == 1 ? "" : "s");

        private static Label L(string text) => new Label { Text = text, AutoSize = true, Margin = new Padding(0, UiScale.Pixels(6), UiScale.Pixels(10), 0) };

        private static Label Step(string text) => new Label { Text = text, AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Margin = new Padding(0, UiScale.Pixels(8), UiScale.Pixels(10), 0) };

        private static Label Note(string text) => new Label { Text = text, AutoSize = true, ForeColor = Color.FromArgb(100, 100, 100), Margin = new Padding(UiScale.Pixels(10), UiScale.Pixels(8), 0, 0) };

        private Label Wrap(string text)
        {
            var l = new Label { Text = text, AutoSize = true, MaximumSize = new Size(ClientSize.Width - UiScale.Pixels(40), 0), Margin = new Padding(0, UiScale.Pixels(4), 0, UiScale.Pixels(4)) };
            SizeChanged += (s, e) => l.MaximumSize = new Size(ClientSize.Width - UiScale.Pixels(40), 0);
            return l;
        }

        private static LinkLabel Lnk(string text) => new LinkLabel { Text = text, AutoSize = true, LinkBehavior = LinkBehavior.HoverUnderline, Margin = new Padding(0, UiScale.Pixels(2), UiScale.Pixels(12), 0) };

        private static Button Btn(string text) => new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlatStyle = FlatStyle.System, Padding = new Padding(UiScale.Pixels(8), 0, UiScale.Pixels(8), 0), Margin = new Padding(0, UiScale.Pixels(3), 0, UiScale.Pixels(3)) };

        /// <summary>A button with its note to the right; the note wraps under the dialog width.</summary>
        private Control Pair(Button b, Label note)
        {
            var host = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = Padding.Empty };
            host.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            note.MaximumSize = new Size(ClientSize.Width - UiScale.Pixels(300), 0);
            SizeChanged += (s, e) => note.MaximumSize = new Size(Math.Max(UiScale.Pixels(200), ClientSize.Width - UiScale.Pixels(300)), 0);
            host.Controls.Add(b, 0, 0);
            host.Controls.Add(note, 1, 0);
            return host;
        }
    }
}
