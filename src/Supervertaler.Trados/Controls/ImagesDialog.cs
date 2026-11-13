using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// One document in the Images panel's table: its name in one column, what was
    /// found in it in the other. Kept apart rather than pre-joined into a sentence,
    /// so names line up under names on a project of several files.
    /// </summary>
    internal sealed class DocumentRow
    {
        public string Name;
        public string Note;
    }

    /// <summary>What the Images dialog shows: the state of the image pipeline for the open project (#84).</summary>
    internal sealed class ImagesState
    {
        public bool ProjectOpen;
        public string ProjectName;
        /// <summary>The images folder, or empty when none is chosen yet.</summary>
        public string Folder;
        /// <summary>Image files in that folder, when it is set and exists; -1 when it does not exist.</summary>
        public int FolderImages;
        /// <summary>True when the folder holds more image files than one run takes; FolderImages is then only how far the count got.</summary>
        public bool FolderTooMany;
        /// <summary>Of the folder's image files, those whose name gives a figure number ("Figure 3.png").</summary>
        public int FolderMatched;
        /// <summary>Files in the folder that are not images (PDF, Word, ...) and are not used.</summary>
        public int FolderOtherFiles;
        /// <summary>Figures the documents' text describes ("Figuur 3 toont ..."), whether or not there is an image for them.</summary>
        public int TextFigures;
        /// <summary>Of those, the ones no file name in the folder gives.</summary>
        public int FiguresWithoutFile;
        /// <summary>How the last run ended, when there is something to say: stopped, nothing could be described, an error. Null otherwise.</summary>
        public string LastOutcome;
        /// <summary>Whether <see cref="LastOutcome"/> reports a failure (shown in red) or just how the run ended.</summary>
        public bool LastOutcomeIsError;
        /// <summary>One row per Word document in the project that has images.</summary>
        public List<DocumentRow> Documents = new List<DocumentRow>();
        /// <summary>Every Word document in the project, images or not.</summary>
        public int DocumentCount;
        /// <summary>Documents with no images in them.</summary>
        public List<DocumentRow> DocumentsWithoutImages = new List<DocumentRow>();
        /// <summary>Documents that could not be read, with the reason.</summary>
        public List<DocumentRow> DocumentsUnreadable = new List<DocumentRow>();
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
        /// <summary>Ends a running description; nothing is saved.</summary>
        public Action Stop;
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
        private readonly ListView _lstDocs;
        private bool _fittingColumns;
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
        private readonly Label _lblOutcome;
        private readonly Label _lblResult;
        private readonly Timer _poll;

        /// <summary>For the layout probe: the longest realistic state, no actions.</summary>
        public ImagesDialog() : this(null, new ImagesState
        {
            ProjectOpen = true,
            ProjectName = "Acme PROJ-001 (application as filed, drawings as filed, sequence listing)",
            Folder = @"D:\Google Drive\Jobs\Acme\0187\Acme PROJ-001 (application as filed)\Images folder with a long name",
            FolderImages = 14,
            Documents = new List<DocumentRow>
            {
                new DocumentRow { Name = "20260713-PROJ-001 Figures as filed.docx", Note = "14 images, 14 with a figure label, paired by position and checked" },
                new DocumentRow { Name = "20260713-PROJ-001 Figures as filed, sheet 2 of a very long document title that wraps.docx", Note = "9 images, 9 with a figure label, labels taken from nearby text" },
                new DocumentRow { Name = "Annex A.docx", Note = "2 images, 0 with a figure label" },
                new DocumentRow { Name = "Annex B.docx", Note = "1 image, 1 with a figure label, paired by position and checked" },
                new DocumentRow { Name = "Annex C.docx", Note = "1 image, 1 with a figure label, paired by position and checked" },
                new DocumentRow { Name = "Annex D.docx", Note = "3 images, 3 with a figure label, paired by position and checked" },
                new DocumentRow { Name = "Annex E.docx", Note = "1 image, 1 with a figure label, paired by position and checked" },
            },
            DocumentCount = 60,
            DocumentsWithoutImages = new List<DocumentRow>
            {
                new DocumentRow { Name = "Annex F.docx", Note = "no images" },
                new DocumentRow { Name = "Annex G.docx", Note = "no images" },
            },
            DocumentsUnreadable = new List<DocumentRow>
            {
                new DocumentRow { Name = "Annex H.docx", Note = "could not be read (the file is in use by another process)" },
            },
            BankIsShared = true, SuggestedBankName = "acme-proj-001-application-as-filed",
            TotalImages = 31, Labelled = 29,
            BankName = "acme-proj-001",
            FiguresPath = @"D:\Supervertaler\memory-banks\acme-proj-001\figures.md",
            FiguresWritten = new DateTime(2026, 8, 26, 23, 58, 0),
            FiguresRows = 14, FiguresWithoutVision = true,
            ProviderName = "Claude (Anthropic)",
            FolderMatched = 12, FolderOtherFiles = 3, TextFigures = 14, FiguresWithoutFile = 2,
            LastOutcomeIsError = true,
            LastOutcome = "None of the 14 image(s) could be described. figures.md was not changed. Reason given for the first: 401: the API key is not valid for this provider",
        }) { }

        public ImagesDialog(ImagesActions actions, ImagesState initial)
        {
            _actions = actions ?? new ImagesActions();
            _state = initial ?? new ImagesState();

            Icon = IconHelper.AppIcon;
            Text = "FigureLens";
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
            // Wide enough for a button and its note on one line; the height grows to the content (FitHeight).
            ClientSize = new Size(UiScale.Pixels(880), UiScale.Pixels(380));
            MinimumSize = new Size(UiScale.Pixels(640), UiScale.Pixels(360));

            var tips = new ToolTip { AutoPopDelay = 15000, InitialDelay = 300 };
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(UiScale.Pixels(12)) };
            _root = root;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            int row = 0;

            var intro = Wrap("The AI sees the text of your documents, not the pictures in them. FigureLens gives it a description of each image, in two steps: " +
                             "get the images out of the documents into a folder, then have them described.");
            root.Controls.Add(intro, 0, row); root.SetColumnSpan(intro, 2); row++;

            // What is there
            root.Controls.Add(L("Your documents:"), 0, row);
            _lblDocs = Wrap("");
            root.Controls.Add(_lblDocs, 1, row); row++;
            // A table, not a sentence per row: on a project of several files the eye
            // should find every name in one column and every finding in the other,
            // instead of hunting for where each name ends. Every document is listed,
            // the ones with images first.
            _lstDocs = new ListView
            {
                Dock = DockStyle.Fill,
                Height = UiScale.Pixels(96),
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                ShowItemToolTips = true,
                Margin = new Padding(0, 0, 0, UiScale.Pixels(6)),
            };
            _lstDocs.Columns.Add("Document");
            _lstDocs.Columns.Add("Images");
            _lstDocs.SizeChanged += (s, e) => FitColumns();
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
            // One button, two jobs: while a run is on it is the way to end it.
            _btnAnalyse.Click += (s, e) => Run(_state.AnalysisRunning ? _actions.Stop : _actions.Analyse);
            tips.SetToolTip(_btnAnalyse, "Shows each image to the AI, together with what the text says about it, and saves the descriptions where every prompt reads them. One paid request per image. "
                + "When the documents hold no images, the image files in the folder above are used, each matched to a figure by its file name (Figure 3.png, fig3.jpg). "
                + "Asks before replacing descriptions that already exist. While it runs, the button stops it; a stopped run saves nothing.");
            _lblAnalyseNote = Note("");
            root.Controls.Add(Pair(_btnAnalyse, _lblAnalyseNote), 1, row); row++;

            _btnWrite = Btn("Describe from the text only");
            _btnWrite.Click += (s, e) => Run(_actions.WriteFigures);
            tips.SetToolTip(_btnWrite, "The free alternative: saves what the document itself says about each figure, without looking at the images, so it needs no image files. Use one or the other. Asks before replacing descriptions that already exist.");
            _lblWriteNote = Note("");
            var alt = Pair(_btnWrite, _lblWriteNote); alt.Margin = new Padding(0, 0, 0, UiScale.Pixels(8));
            root.Controls.Add(alt, 1, row); row++;

            // How the last run ended. The log it is also written to is behind
            // this window. Red only for a failure: a red "Described 4 image(s)"
            // read as an error (13 Nov 2026).
            _lblOutcome = Wrap("");
            _lblOutcome.Visible = false;
            root.Controls.Add(_lblOutcome, 1, row); row++;

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
            // Images come from the documents when they hold any. When they hold
            // none, the image files in the chosen folder are used instead: the
            // drawings of a patent usually arrive as separate files.
            bool docImages = st.TotalImages > 0;
            bool useFolder = !docImages && folderSet && st.FolderImages > 0;
            bool haveImages = docImages || useFolder;
            bool haveBank = !string.IsNullOrEmpty(st.BankName) && !st.BankIsShared;
            string n = st.TotalImages + " image" + (st.TotalImages == 1 ? "" : "s");
            string folderCount = st.FolderTooMany
                ? "more than " + Supervertaler.Core.ReferenceImages.MaxImages + " image files"
                : Plural(Math.Max(0, st.FolderImages), "image file");

            _lblDocs.Text = DocumentsText(st)
                + (useFolder ? " The " + folderCount + " in the folder below will be used." : "");
            _lstDocs.BeginUpdate();
            _lstDocs.Items.Clear();
            foreach (var d in st.Documents) AddDocumentRow(d, _lstDocs.ForeColor);
            foreach (var d in st.DocumentsWithoutImages) AddDocumentRow(d, SystemColors.GrayText);
            foreach (var d in st.DocumentsUnreadable) AddDocumentRow(d, Color.Firebrick);
            _lstDocs.EndUpdate();
            _lstDocs.Visible = st.DocumentCount > 0;
            FitColumns();

            // Step 1
            _btnExtract.Enabled = st.ProjectOpen && docImages && !st.AnalysisRunning;
            _lblExtractNote.Text = useFolder ? "Nothing to extract, and not needed: your images are already in the folder below."
                : !docImages ? "Nothing to extract: the documents above have no images. If the images came as separate files, choose their folder below."
                : !folderSet ? "Copies the " + n + " out of the documents above. You will be asked where to put them; a new, empty folder next to the job is fine."
                : "Copies the " + n + " out of the documents above into the folder below. Free, no AI.";
            _lblFolder.Text = !folderSet ? "Folder: not chosen yet."
                : st.FolderImages < 0 ? "Folder: " + st.Folder + "  (no longer exists)"
                : "Folder: " + st.Folder + "  (" + folderCount
                    + (useFolder && st.FolderOtherFiles > 0 ? "; " + Plural(st.FolderOtherFiles, "other file") + " not used: PDF and other formats are not read" : "")
                    + ")";
            _lnkChange.Text = folderSet ? "Change\u2026" : "Already have the images in a folder? Choose it\u2026";
            _lnkOpen.Visible = folderSet && st.FolderImages >= 0;

            // Step 2
            bool tooMany = useFolder && st.FolderTooMany;
            _btnAnalyse.Text = st.AnalysisRunning ? "Stop" : "Describe images with AI";
            _btnAnalyse.Enabled = st.AnalysisRunning
                || (st.ProjectOpen && haveImages && folderSet && haveBank && !tooMany);
            string provider = st.ProviderName ?? "the provider";
            _lblAnalyseNote.Text = st.AnalysisRunning ? "Running\u2026 Stop ends the run and saves nothing."
                : !haveImages ? (!folderSet ? "No images to describe. If the images came as separate files, choose their folder under step 1."
                    : st.FolderImages < 0 ? "No images in the documents, and the folder no longer exists. Choose it again under step 1."
                    : "No images to describe: none in the documents, and no image files in the folder.")
                : !folderSet ? "Do step 1 first."
                : tooMany ? "The folder holds more than " + Supervertaler.Core.ReferenceImages.MaxImages + " image files, and each would be a paid request. Choose a folder that holds only this project\u2019s drawings."
                : !haveBank ? "Needs a memory bank for this project to save the descriptions in \u2013 see Result."
                : useFolder ? FolderRunText(st, provider)
                : st.TotalImages + " AI request" + (st.TotalImages == 1 ? "" : "s") + " to " + provider + ", one per image.";

            bool anyText = docImages || st.TextFigures > 0;
            _btnWrite.Enabled = st.ProjectOpen && anyText && haveBank && !st.AnalysisRunning;
            _lblWriteNote.Text = !haveBank ? "Needs a memory bank for this project \u2013 see Result."
                : docImages ? "Free, no AI: only what the document says about each figure. The alternative to the button above, not a third step."
                : st.TextFigures > 0 ? "Free, no AI: what the text says about each of the " + Plural(st.TextFigures, "figure") + " it describes. Needs no image files. The alternative to the button above, not a third step."
                : "Nothing to write without the AI: the text describes no figures (no sentence opens with \u201cFigure 1 \u2026\u201d or \u201cFiguur 1 \u2026\u201d).";

            _lblOutcome.Text = st.LastOutcome ?? "";
            _lblOutcome.ForeColor = st.LastOutcomeIsError ? Color.Firebrick : SystemColors.ControlText;
            _lblOutcome.Visible = !st.AnalysisRunning && !string.IsNullOrEmpty(st.LastOutcome);

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
                 + (st.DocumentsWithoutImages.Count > 0 || st.DocumentsUnreadable.Count > 0 ? "; the rest have none." : ".");
        }

        private static string Plural(int n, string noun) => n + " " + noun + (n == 1 ? "" : "s");

        /// <summary>
        /// The cost of describing the folder's files, and how they were matched,
        /// said before the button is pressed: how many requests, how many files
        /// got a figure number from their name, and what will happen to the rest.
        /// </summary>
        private static string FolderRunText(ImagesState st, string provider)
        {
            int files = st.FolderImages;
            int unmatched = Math.Max(0, files - st.FolderMatched);
            var text = files + " AI request" + (files == 1 ? "" : "s") + " to " + provider + ", one per image file in the folder. ";
            if (unmatched == 0)
                text += (files == 1 ? "It is" : "All are") + " matched to a figure by file name.";
            else if (st.FolderMatched == 0)
                text += "No file name gives a figure number (such as \u201cFigure 3.png\u201d), so " + (files == 1 ? "it" : "they") + " will be described without one.";
            else
                text += st.FolderMatched + " matched to a figure by file name; " + unmatched + " with no figure number in the name will be described without one.";
            if (st.FiguresWithoutFile > 0)
                text += " " + Plural(st.FiguresWithoutFile, "figure") + " in the text " + (st.FiguresWithoutFile == 1 ? "has" : "have") + " no file.";
            return text;
        }

        private void AddDocumentRow(DocumentRow row, Color colour)
        {
            var item = new ListViewItem(row.Name ?? "") { ForeColor = colour };
            item.SubItems.Add(row.Note ?? "");
            // A name wider than its column is cut off on screen; the tooltip carries
            // the whole row, so nothing is unreadable.
            item.ToolTipText = (row.Name ?? "") + "  \u2013  " + (row.Note ?? "");
            _lstDocs.Items.Add(item);
        }

        /// <summary>
        /// The name column takes what the longest name needs, capped at a share of the
        /// width so one long file name cannot push the finding off the right edge; the
        /// finding column takes the rest.
        ///
        /// <para>Guarded: setting a column width from inside the list's own
        /// SizeChanged re-enters layout and raises it again. That does not fail a
        /// check, it hangs - the memoQ side lost five minutes to it before killing
        /// the run.</para>
        /// </summary>
        private void FitColumns()
        {
            if (_fittingColumns || _lstDocs == null || _lstDocs.Columns.Count < 2) return;
            _fittingColumns = true;
            try
            {
                var available = _lstDocs.ClientSize.Width - UiScale.Pixels(2);
                if (available <= 0) return;

                // Measured, not ListView's own auto-size (Width = -1): auto-size needs
                // a window handle, and before the dialog is shown it stores the -1
                // literally - a column of negative width, and a harness that cannot
                // tell the difference.
                var needed = TextRenderer.MeasureText(_lstDocs.Columns[0].Text ?? "", _lstDocs.Font).Width;
                foreach (ListViewItem item in _lstDocs.Items)
                {
                    var w = TextRenderer.MeasureText(item.Text ?? "", _lstDocs.Font).Width;
                    if (w > needed) needed = w;
                }
                needed += UiScale.Pixels(18);   // the cell's own padding

                var cap = (int)(available * 0.42);
                var first = Math.Max(UiScale.Pixels(80), Math.Min(needed, cap));
                _lstDocs.Columns[0].Width = first;
                _lstDocs.Columns[1].Width = Math.Max(UiScale.Pixels(80), available - first);
            }
            catch { }
            finally { _fittingColumns = false; }
        }

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
