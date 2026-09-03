using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Models;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// UserControl for the "Prompts" tab in the Settings dialog.
    /// TreeView-based folder structure on the left, context-sensitive detail pane on the right.
    /// </summary>
    public class PromptManagerPanel : UserControl
    {
        // ─── Shell ───────────────────────────────────────────────
        // Splitter, tree, toolbar/footer strips and detail swapping live in
        // TreeDetailPanel so the Library tab's memory-bank view can use the
        // same one. See docs/design/library-tab.md.
        private TreeDetailPanel _shell;

        // ─── Left panel controls ─────────────────────────────────
        private TreeView _tvPrompts;
        private Button _btnNew;
        private Button _btnEdit;
        private Button _btnDelete;
        private Button _btnRestore;
        private Button _btnNewFolder;
        private Button _btnMoveUp;
        private Button _btnMoveDown;
        private Button _btnRefresh;
        private ContextMenuStrip _treeContextMenu;
        private System.Windows.Forms.ToolTip _toolTip = new System.Windows.Forms.ToolTip();

        // ─── Right panel controls ────────────────────────────────
        // Now the shell's DetailHost; kept as a field so the many
        // _rightPanel.Controls.Add calls below read unchanged.
        private Panel _rightPanel;

        // System prompt detail panel
        private Panel _panelSystemPrompt;
        private TextBox _txtSystemPrompt;
        private Button _btnEditSystem;
        private Button _btnResetSystem;
        private Label _lblSystemStatus;

        // Prompt detail panel
        private Panel _panelPromptDetail;
        private Label _lblPromptName;
        private Label _lblPromptCategorySource;
        private Label _lblPromptDescription;
        private TextBox _txtPromptContent;
        private Label _lblShortcutLabel;
        private ComboBox _cboShortcut;

        // Folder info panel
        private Panel _panelFolderInfo;
        private Label _lblFolderName;
        private Label _lblFolderPromptCount;
        private Label _lblFolderSubfolderCount;

        // ─── State ───────────────────────────────────────────────
        private PromptLibrary _library;
        private string _customSystemPrompt; // null = use default
        private AiSettings _aiSettings;
        private Dictionary<string, string> _shortcutAssignments; // FilePath -> shortcut display string

        private const string SystemPromptTag = "__SYSTEM_PROMPT__";

        // AutoTagger instruction editor (mirrors the System Prompt node/panel)
        private Panel _panelAutoTagger;
        private TextBox _txtAutoTagger;
        private Button _btnEditAutoTagger;
        private Button _btnResetAutoTagger;
        private Label _lblAutoTaggerStatus;
        private string _autoTaggerInstruction; // null = use default
        private const string AutoTaggerTag = "__AUTOTAGGER__";
        private string _activePromptPath; // per-project active prompt relative path

        // ─── SuperMemory (read-only) ─────────────────────────────
        // A distinct Tag TYPE, not a string: OnTreeAfterSelect treats any string
        // tag as a prompt folder, so a bank node tagged with a string would be
        // silently mistaken for one.
        private enum BankNodeKind { Root, Bank, File, ReferenceFolder }

        private class BankNode
        {
            public BankNodeKind Kind;
            public string BankName;
            public string FilePath;   // File nodes only
            public bool ReadIntoPrompts;
        }

        private Panel _panelBankFile;
        private Panel _panelImagesRow;
        private Label _lblImagesLabel;
        private TextBox _txtImagesFolder;
        private Button _btnImagesBrowse;
        private Button _btnImagesClear;
        private Label _lblImagesCaveat;
        private Label _lblBankFileName;
        private Label _lblBankFileNote;
        private RichTextBox _txtBankFile;

        /// <summary>
        /// Fired when the user toggles the per-project active prompt (right-click →
        /// "Set as active prompt for this project"). The string argument is the new
        /// active prompt's relative path, or empty if the active prompt was cleared.
        /// Consumers use this to live-refresh the Batch Translate dropdown while the
        /// Settings dialog is still open – the change is persisted only on OK.
        /// </summary>
        public event EventHandler<string> ActivePromptChanged;

        /// <summary>
        /// Static/global variant of <see cref="ActivePromptChanged"/>. Subscribed
        /// once by <c>AiAssistantViewPart</c> at initialisation so the Batch
        /// Translate panel refreshes regardless of which entry point opened the
        /// Settings dialog (AI Assistant gear, termbase gear, etc.).
        /// </summary>
        public static event EventHandler<string> ActivePromptChangedGlobal;

        public PromptManagerPanel()
        {
            BuildUI();
        }

        // ═══════════════════════════════════════════════════════════
        //  UI CONSTRUCTION
        // ═══════════════════════════════════════════════════════════

        private void BuildUI()
        {
            // Let WinForms scale this dialog by system DPI so it doesn't squish
            // at >100% Windows display scaling. Cheap fallback; for surfaces
            // with their own UiScale-driven layout, set AutoScaleMode = None
            // instead and let UiScale own scaling.
            AutoScaleMode = AutoScaleMode.Dpi;
            SuspendLayout();
            BackColor = Color.White;

            _shell = new TreeDetailPanel();
            _tvPrompts = _shell.Tree;
            _rightPanel = _shell.DetailHost;

            // Prompts are reordered by dragging; memory banks will not be, so
            // this is set here rather than in the shell.
            _tvPrompts.AllowDrop = true;

            BuildLeftPanel();
            BuildRightPanels();

            Controls.Add(_shell);

            ResumeLayout(false);
        }

        private void BuildLeftPanel()
        {
            var labelColor = Color.FromArgb(80, 80, 80);

            // ─── Toolbar ─────────────────────────────────────────
            var toolbar = _shell.Toolbar;

            _btnNew = CreateToolbarButton("New", 45);
            _btnNew.Click += OnNewPrompt;

            _btnEdit = CreateToolbarButton("Edit", 45);
            _btnEdit.Click += OnEditPrompt;

            _btnDelete = CreateToolbarButton("Delete", 65);
            _btnDelete.Click += OnDeletePrompt;

            _btnRestore = CreateToolbarButton("Restore", 65);
            _btnRestore.Click += OnRestoreDefaults;
            _toolTip.SetToolTip(_btnRestore, "Restore all default prompts to their original state");

            _btnNewFolder = CreateToolbarButton("New Folder", 90);
            _btnNewFolder.Click += OnNewFolder;

            _btnRefresh = CreateToolbarButton("Refresh", 65);
            _btnRefresh.Click += OnRefresh;

            // The only icon-only buttons in this toolbar, so the only ones
            // whose purpose is not written on them. Both name the
            // consequence rather than the gesture: the tree order IS the
            // QuickLauncher menu order.
            _btnMoveUp = CreateToolbarButton("\u25B2", 28);
            _btnMoveUp.Click += OnMoveUp;
            _btnMoveUp.Font = new Font("Segoe UI", 7f);
            _toolTip.SetToolTip(_btnMoveUp,
                "Move the selected prompt up within its folder" + Environment.NewLine +
                "(this order is the order of the Alt+Q QuickLauncher menu)");

            _btnMoveDown = CreateToolbarButton("\u25BC", 28);
            _btnMoveDown.Click += OnMoveDown;
            _btnMoveDown.Font = new Font("Segoe UI", 7f);
            _toolTip.SetToolTip(_btnMoveDown,
                "Move the selected prompt down within its folder" + Environment.NewLine +
                "(this order is the order of the Alt+Q QuickLauncher menu)");

            toolbar.Controls.AddRange(new Control[]
            {
                _btnNew, _btnEdit, _btnDelete, _btnRestore, _btnMoveUp, _btnMoveDown, _btnNewFolder, _btnRefresh
            });

            // Step 2 of the Library plan: the shell can now enable and disable
            // buttons per selected node. Every rule here returns true on
            // purpose, so this tab behaves exactly as it did before and the
            // mechanism can be verified as "nothing changed" - the only cheap
            // way to test it on a tab that already works. Real rules arrive with
            // the memory-bank tree in step 3.
            //
            // Worth noting for whoever writes them: these buttons currently do
            // NOTHING when clicked against the wrong selection - Move Up with
            // the System Prompt selected, Delete with a folder. Silent no-ops,
            // and exactly what this mechanism exists to replace.
            // Refresh always applies. Everything else acts on the prompt
            // library and is dead against a SuperMemory node - which is what the
            // step-2 mechanism was built for. Bank actions (rename, delete, set
            // active) arrive in step 5.
            _shell.RegisterToolbarButton(_btnRefresh, _ => true);

            // Edit is the one prompt-library button that also means something
            // for SuperMemory: a bank file is editable, a bank or the
            // reference folder is not.
            _shell.RegisterToolbarButton(_btnEdit, node =>
            {
                if (node?.Tag is BankNode bn)
                    return bn.Kind == BankNodeKind.File
                           && (bn.FilePath ?? "").EndsWith(".md", StringComparison.OrdinalIgnoreCase);
                return true;
            });

            // Delete now means something for a bank too - but not for _shared,
            // and not for a file or the reference folder.
            _shell.RegisterToolbarButton(_btnDelete, node =>
            {
                if (node?.Tag is BankNode bn)
                    return bn.Kind == BankNodeKind.Bank && !IsSharedBank(bn.BankName);
                return true;
            });

            foreach (var b in new[]
                     { _btnNew, _btnRestore,
                       _btnMoveUp, _btnMoveDown, _btnNewFolder })
            {
                _shell.RegisterToolbarButton(b, node => !(node?.Tag is BankNode));
            }

            // Position buttons from right edge
            toolbar.Resize += (s, e) =>
            {
                var pw = toolbar.Width;
                _btnRefresh.Location = new Point(pw - 4 - _btnRefresh.Width, 6);
                _btnNewFolder.Location = new Point(_btnRefresh.Left - _btnNewFolder.Width - 2, 6);
                _btnMoveDown.Location = new Point(_btnNewFolder.Left - _btnMoveDown.Width - 6, 6);
                _btnMoveUp.Location = new Point(_btnMoveDown.Left - _btnMoveUp.Width - 1, 6);
                _btnRestore.Location = new Point(_btnMoveUp.Left - _btnRestore.Width - 6, 6);
                _btnDelete.Location = new Point(_btnRestore.Left - _btnDelete.Width - 2, 6);
                _btnEdit.Location = new Point(_btnDelete.Left - _btnEdit.Width - 2, 6);
                _btnNew.Location = new Point(_btnEdit.Left - _btnNew.Width - 2, 6);
            };

            // ─── TreeView ────────────────────────────────────────
            // Created by the shell; only the handlers are ours.
            _tvPrompts.AfterSelect += OnTreeAfterSelect;
            _tvPrompts.NodeMouseDoubleClick += OnTreeNodeDoubleClick;
            _tvPrompts.NodeMouseClick += OnTreeNodeMouseClick;
            _tvPrompts.ItemDrag += OnTreeItemDrag;
            _tvPrompts.DragEnter += OnTreeDragEnter;
            _tvPrompts.DragOver += OnTreeDragOver;
            _tvPrompts.DragDrop += OnTreeDragDrop;

            BuildTreeContextMenu();

            // ─── Bottom link ─────────────────────────────────────
            var folderPanel = _shell.Footer;
            var lnkFolder = new LinkLabel
            {
                Text = "Open prompts folder",
                Location = new Point(10, 4),
                AutoSize = true,
                Font = new Font("Segoe UI", 8f),
                LinkColor = Color.FromArgb(0, 102, 204)
            };
            lnkFolder.LinkClicked += (s, ev) =>
            {
                try
                {
                    var dir = PromptLibrary.PromptsFolderPath;
                    if (!Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    System.Diagnostics.Process.Start("explorer.exe", dir);
                }
                catch { }
            };
            folderPanel.Controls.Add(lnkFolder);
        }

        /// <summary>Toolbar button in the shared house style. Delegates so the
        /// Library tab's memory-bank toolbar cannot drift from this one.</summary>
        private Button CreateToolbarButton(string text, int width)
            => TreeDetailPanel.CreateToolbarButton(text, width);

        private void BuildTreeContextMenu()
        {
            _treeContextMenu = new ContextMenuStrip();

            var miEdit = new ToolStripMenuItem("Edit");
            miEdit.Click += OnEditPrompt;
            _treeContextMenu.Items.Add(miEdit);

            var miClone = new ToolStripMenuItem("Clone");
            miClone.Click += OnClonePrompt;
            _treeContextMenu.Items.Add(miClone);

            var miDelete = new ToolStripMenuItem("Delete");
            miDelete.Click += OnDeletePrompt;
            _treeContextMenu.Items.Add(miDelete);

            _treeContextMenu.Items.Add(new ToolStripSeparator());

            var miShortcut = new ToolStripMenuItem("Assign Shortcut");
            for (int i = 1; i <= 10; i++)
            {
                var digit = i == 10 ? "0" : i.ToString();
                var display = "Ctrl+Alt+" + digit;
                var slot = i;
                var mi = new ToolStripMenuItem(display);
                mi.Click += (s, ev) => AssignShortcutToSelected(slot, display);
                miShortcut.DropDownItems.Add(mi);
            }
            _treeContextMenu.Items.Add(miShortcut);

            var miSetActive = new ToolStripMenuItem("Set as active prompt for this project");
            miSetActive.Click += (s, ev) => SetActivePromptFromTree();
            _treeContextMenu.Items.Add(miSetActive);

            var miDeleteFolder = new ToolStripMenuItem("Delete Folder");
            miDeleteFolder.Click += OnDeleteFolder;
            _treeContextMenu.Items.Add(miDeleteFolder);

            var miFlatSection = new ToolStripMenuItem("Show as section in menu");
            miFlatSection.Click += (s2, ev2) => ToggleFlatFolder();
            _treeContextMenu.Items.Add(miFlatSection);

            // ─── Memory bank actions ─────────────────────────────
            var miBankSep = new ToolStripSeparator();
            _treeContextMenu.Items.Add(miBankSep);

            var miBankSetActive = new ToolStripMenuItem("Set as active memory bank");
            miBankSetActive.Click += (s2, ev2) => SetSelectedBankActive();
            _treeContextMenu.Items.Add(miBankSetActive);

            var miBankRename = new ToolStripMenuItem("Rename memory bank\u2026");
            miBankRename.Click += (s2, ev2) => RenameSelectedBank();
            _treeContextMenu.Items.Add(miBankRename);

            var miBankDelete = new ToolStripMenuItem("Delete memory bank\u2026");
            miBankDelete.Click += (s2, ev2) => DeleteSelectedBank();
            _treeContextMenu.Items.Add(miBankDelete);

            var miBankOpen = new ToolStripMenuItem("Open bank folder");
            miBankOpen.Click += (s2, ev2) => OpenSelectedBankFolder();
            _treeContextMenu.Items.Add(miBankOpen);

            _treeContextMenu.Opening += (s, ev) =>
            {
                var node = _tvPrompts.SelectedNode;
                if (node == null) { ev.Cancel = true; return; }

                // A memory bank gets its own menu entirely: none of the prompt
                // items below mean anything for it.
                var bank = node.Tag as BankNode;
                var isBank = bank != null && bank.Kind == BankNodeKind.Bank;
                var isShared = isBank && string.Equals(
                    bank.BankName, MemoryBankReader.SharedBankName,
                    StringComparison.OrdinalIgnoreCase);

                miBankSep.Visible = isBank;
                miBankOpen.Visible = isBank;
                // _shared is loaded by name and holds the cross-client defaults,
                // so it can be opened but never renamed, deleted or "activated".
                miBankSetActive.Visible = isBank && !isShared;
                miBankRename.Visible = isBank && !isShared;
                miBankDelete.Visible = isBank && !isShared;

                if (bank != null)
                {
                    foreach (ToolStripItem it in _treeContextMenu.Items)
                    {
                        if (it != miBankSep && it != miBankSetActive && it != miBankRename
                            && it != miBankDelete && it != miBankOpen)
                            it.Visible = false;
                    }
                    if (!isBank) ev.Cancel = true;   // files and reference/ have no menu yet
                    return;
                }

                var prompt = node.Tag as PromptTemplate;
                var isFolder = node.Tag is string folderPath && folderPath != SystemPromptTag;

                // Determine if this is a QuickLauncher folder
                var isQlFolder = false;
                if (isFolder && node.Tag is string fp)
                {
                    var normalised = fp.Replace('\\', '/');
                    isQlFolder = normalised.StartsWith("QuickLauncher/")
                                 || normalised == "QuickLauncher";
                }

                // Show/hide items based on whether a prompt or folder is selected
                miEdit.Visible = prompt != null;
                miClone.Visible = prompt != null;
                miDelete.Visible = prompt != null;
                miShortcut.Visible = prompt != null && prompt.IsQuickLauncher;
                miSetActive.Visible = prompt != null && !prompt.IsQuickLauncher;
                if (prompt != null && miSetActive.Visible)
                {
                    miSetActive.Checked = !string.IsNullOrEmpty(_activePromptPath)
                        && PromptPaths.Match(prompt.RelativePath, _activePromptPath) /* marker-tolerant, #100 */;
                }
                miDeleteFolder.Visible = isFolder;
                miFlatSection.Visible = isQlFolder;

                // Update checkmark for flat section toggle
                if (isQlFolder && node.Tag is string folderRel)
                {
                    var flatList = _aiSettings?.QuickLauncherFlatFolders;
                    miFlatSection.Checked = flatList != null && flatList.Contains(folderRel);
                }

                if (prompt != null)
                {
                    miDelete.Enabled = !prompt.IsReadOnly;

                    // Update checkmarks on shortcut submenu
                    if (prompt.IsQuickLauncher)
                    {
                        string currentShortcut;
                        _shortcutAssignments.TryGetValue(prompt.FilePath, out currentShortcut);
                        if (currentShortcut == null) currentShortcut = "";

                        foreach (ToolStripMenuItem item in miShortcut.DropDownItems)
                        {
                            item.Checked = item.Text == currentShortcut;
                        }
                    }
                }
            };
        }

        // ─── Right panel: three sub-panels ───────────────────────

        private void BuildRightPanels()
        {
            BuildSystemPromptPanel();
            BuildAutoTaggerPanel();
            BuildPromptDetailPanel();
            BuildFolderInfoPanel();

            BuildBankFilePanel();

            _rightPanel.Controls.Add(_panelBankFile);
            _rightPanel.Controls.Add(_panelSystemPrompt);
            _rightPanel.Controls.Add(_panelAutoTagger);
            _rightPanel.Controls.Add(_panelPromptDetail);
            _rightPanel.Controls.Add(_panelFolderInfo);

            // Hide all initially
            _shell.HideAllDetails();
        }

        private void BuildAutoTaggerPanel()
        {
            var headerFont = new Font("Segoe UI", 9f, FontStyle.Bold);
            var bodyFont = new Font("Segoe UI", 8.5f);

            _panelAutoTagger = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };

            var topPanel = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Color.White };
            var lblHeader = new Label
            {
                Text = "AutoTagger Instruction",
                Font = headerFont,
                ForeColor = Color.FromArgb(50, 50, 50),
                Location = new Point(6, 10),
                AutoSize = true
            };
            var lblInfo = new Label
            {
                Text = "Tells the AI how to place the source segment's inline tags into the current translation (Ctrl+Alt+G). Placeholders: {{SOURCE_TEXT}}, {{TARGET_TEXT}}, {{TAG_LIST}}.",
                Location = new Point(6, 30),
                Font = new Font("Segoe UI", 7.5f, FontStyle.Italic),
                ForeColor = Color.FromArgb(130, 130, 130),
                AutoSize = false,
                Height = 44,
                Width = 400,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            var btnHelpAutoTagger = new Button
            {
                Text = "?",
                Size = new Size(22, 22),
                FlatStyle = FlatStyle.System,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                TabStop = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnHelpAutoTagger.Click += (s, e) => HelpSystem.OpenHelp(HelpSystem.Topics.AutoTagger);
            _toolTip.SetToolTip(btnHelpAutoTagger, "Open AutoTagger help (online)");
            topPanel.Controls.AddRange(new Control[] { lblHeader, lblInfo, btnHelpAutoTagger });
            void PositionAutoTaggerHelp() => btnHelpAutoTagger.Location = new Point(topPanel.Width - btnHelpAutoTagger.Width - 8, 6);
            topPanel.SizeChanged += (s, e) => PositionAutoTaggerHelp();
            PositionAutoTaggerHelp();

            var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = Color.White };
            _btnEditAutoTagger = new Button
            {
                Text = "Edit AutoTagger Instruction",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(130, 25),
                Padding = new Padding(8, 0, 8, 0),
                Margin = new Padding(6, 4, 0, 4),
                FlatStyle = FlatStyle.System,
                Font = bodyFont
            };
            _btnEditAutoTagger.Click += OnEditAutoTagger;
            _btnResetAutoTagger = new Button
            {
                Text = "Reset to Default",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(120, 25),
                Padding = new Padding(8, 0, 8, 0),
                Margin = new Padding(12, 4, 0, 4),
                FlatStyle = FlatStyle.System,
                Font = bodyFont
            };
            _btnResetAutoTagger.Click += OnResetAutoTagger;
            _lblAutoTaggerStatus = new Label
            {
                Text = "",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                ForeColor = Color.FromArgb(100, 100, 100),
                Font = new Font("Segoe UI", 8f),
                Margin = new Padding(8, 8, 0, 0)
            };
            var buttonFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = Padding.Empty,
                Margin = Padding.Empty
            };
            buttonFlow.Controls.Add(_btnEditAutoTagger);
            buttonFlow.Controls.Add(_btnResetAutoTagger);
            buttonFlow.Controls.Add(_lblAutoTaggerStatus);
            bottomPanel.Controls.Add(buttonFlow);

            _txtAutoTagger = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 7.5f),
                BackColor = Color.FromArgb(248, 248, 248),
                ForeColor = Color.FromArgb(60, 60, 60),
                WordWrap = true
            };
            var textPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 0, 6, 0), BackColor = Color.White };
            textPanel.Controls.Add(_txtAutoTagger);

            _panelAutoTagger.Controls.Add(textPanel);
            _panelAutoTagger.Controls.Add(bottomPanel);
            _panelAutoTagger.Controls.Add(topPanel);
        }

        private void UpdateAutoTaggerDisplay()
        {
            if (!string.IsNullOrWhiteSpace(_autoTaggerInstruction))
            {
                _txtAutoTagger.Text = _autoTaggerInstruction;
                _lblAutoTaggerStatus.Text = "(customised)";
                _lblAutoTaggerStatus.ForeColor = Color.FromArgb(180, 120, 0);
            }
            else
            {
                _txtAutoTagger.Text = AiSettings.DefaultAutoTaggerInstruction;
                _lblAutoTaggerStatus.Text = "(default)";
                _lblAutoTaggerStatus.ForeColor = Color.FromArgb(30, 130, 60);
            }
        }

        private void OnEditAutoTagger(object sender, EventArgs e)
        {
            var content = !string.IsNullOrWhiteSpace(_autoTaggerInstruction)
                ? _autoTaggerInstruction
                : AiSettings.DefaultAutoTaggerInstruction;

            var prompt = new PromptTemplate
            {
                Name = "AutoTagger Instruction",
                Description = "Instruction for placing inline tags into the target",
                Category = "System",
                Content = content
            };

            using (var dlg = new PromptEditorDialog(prompt))
            {
                dlg.Text = "Edit AutoTagger Instruction";
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _autoTaggerInstruction = dlg.Result.Content;
                    UpdateAutoTaggerDisplay();
                }
            }
        }

        private void OnResetAutoTagger(object sender, EventArgs e)
        {
            var result = MessageBox.Show(
                "Reset the AutoTagger instruction to the default?\n\nThis will discard any customisations.",
                "Reset AutoTagger Instruction",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);

            if (result == DialogResult.Yes)
            {
                _autoTaggerInstruction = null;
                UpdateAutoTaggerDisplay();
            }
        }

        /// <summary>
        /// Read-only view of one memory-bank file. Read-only on purpose for now:
        /// seeing the bank is most of the value, and these files are edited
        /// concurrently by Obsidian and the Python assistant, so writing them
        /// safely is its own problem (step 4).
        /// </summary>
        private void BuildBankFilePanel()
        {
            _panelBankFile = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Visible = false };

            _lblBankFileName = new Label
            {
                Location = new Point(14, 10),
                AutoSize = true,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = Color.FromArgb(30, 30, 30)
            };

            _lblBankFileNote = new Label
            {
                Location = new Point(14, 34),
                AutoSize = false,
                Height = 32,
                Font = new Font("Segoe UI", 8.25f, FontStyle.Italic),
                ForeColor = Color.FromArgb(110, 110, 110)
            };

            // RichTextBox, not TextBox: these files are markdown and are far
            // easier to scan rendered - terminology.md in particular is a table.
            // MarkdownToRtf is the same renderer the chat bubbles use.
            //
            // It also fixes a real defect the raw view exposed. A TextBox needs
            // CRLF to break a line, and these files are written by Obsidian and
            // the Python assistant with LF only, so every one of them displayed
            // as a single run-on paragraph. The converter normalises line
            // endings itself.
            _txtBankFile = new RichTextBox
            {
                ReadOnly = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                WordWrap = true,
                BackColor = Color.FromArgb(250, 250, 250),
                BorderStyle = BorderStyle.FixedSingle,
                DetectUrls = false
            };

            _panelBankFile.Controls.Add(_lblBankFileName);
            _panelBankFile.Controls.Add(_lblBankFileNote);
            _panelBankFile.Controls.Add(_txtBankFile);

            // ─── Reference images ────────────────────────────────
            // Where a bank's figures.md comes FROM. Shown on the bank and on
            // figures.md itself, so the artifact carries its own provenance
            // rather than the setting living somewhere unrelated.
            _panelImagesRow = new Panel { Dock = DockStyle.Bottom, Height = 86, BackColor = Color.White };

            // A rule above it. Without one the row sits flush against the
            // rendered content at the very bottom edge and reads as part of it -
            // missed twice on first use, which is enough evidence.
            _panelImagesRow.Controls.Add(new Label
            {
                Dock = DockStyle.Top,
                Height = 1,
                BorderStyle = BorderStyle.Fixed3D
            });

            _lblImagesLabel = new Label
            {
                Location = new Point(14, 10),
                AutoSize = false,
                Height = 15,
                Font = new Font("Segoe UI", 8.25f, FontStyle.Bold),
                ForeColor = Color.FromArgb(60, 60, 60)
            };

            _txtImagesFolder = new TextBox
            {
                Location = new Point(14, 28),
                ReadOnly = true,
                Font = new Font("Segoe UI", 8.25f),
                BackColor = Color.FromArgb(250, 250, 250),
                BorderStyle = BorderStyle.FixedSingle
            };

            _btnImagesBrowse = TreeDetailPanel.CreateToolbarButton("Browse\u2026", 70);
            _btnImagesBrowse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnImagesBrowse.Click += (s, e) => BrowseForReferenceImages();

            _btnImagesClear = TreeDetailPanel.CreateToolbarButton("Clear", 55);
            _btnImagesClear.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnImagesClear.Click += (s, e) => ClearReferenceImages();

            // The folder is groundwork: no code reads it yet. Saying so beats a
            // setting that looks connected to something and is not.
            _lblImagesCaveat = new Label
            {
                Location = new Point(14, 56),
                AutoSize = false,
                Height = 26,
                Font = new Font("Segoe UI", 7.5f),
                ForeColor = Color.FromArgb(140, 140, 140),
                Text = "Nothing reads this folder yet \u2014 the pass that turns drawings into "
                     + "figures.md is not built. A figures.md you put in the bank yourself IS "
                     + "read into every prompt."
            };

            _panelImagesRow.Controls.Add(_lblImagesCaveat);
            _panelImagesRow.Controls.Add(_lblImagesLabel);
            _panelImagesRow.Controls.Add(_txtImagesFolder);
            _panelImagesRow.Controls.Add(_btnImagesBrowse);
            _panelImagesRow.Controls.Add(_btnImagesClear);

            _panelImagesRow.Resize += (s, e) =>
            {
                var w = _panelImagesRow.Width;
                if (w < 200) return;
                _lblImagesLabel.Width = w - 28;
                _lblImagesCaveat.Width = w - 28;
                _btnImagesClear.Location = new Point(w - 14 - _btnImagesClear.Width, 27);
                _btnImagesBrowse.Location = new Point(_btnImagesClear.Left - _btnImagesBrowse.Width - 6, 27);
                _txtImagesFolder.Width = Math.Max(60, _btnImagesBrowse.Left - 20);
            };

            _panelBankFile.Controls.Add(_panelImagesRow);

            _panelBankFile.Resize += (s, e) =>
            {
                var w = _panelBankFile.Width - 28;
                if (w < 40) return;
                _lblBankFileNote.Width = w;
                _txtBankFile.Location = new Point(14, 72);
                var below = _panelImagesRow.Visible ? _panelImagesRow.Height : 0;
                _txtBankFile.Size = new Size(w, Math.Max(40, _panelBankFile.Height - 86 - below));
            };
        }

        private void BuildSystemPromptPanel()
        {
            var headerFont = new Font("Segoe UI", 9f, FontStyle.Bold);
            var bodyFont = new Font("Segoe UI", 8.5f);

            _panelSystemPrompt = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White
            };

            // Top section: header + info. Taller so the wrapped subtitle isn't
            // clipped at high DPI + Windows text scaling.
            var topPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 92,
                BackColor = Color.White
            };

            var lblSysHeader = new Label
            {
                Text = "System Prompt",
                Font = headerFont,
                ForeColor = Color.FromArgb(50, 50, 50),
                Location = new Point(6, 10),
                AutoSize = true
            };

            var lblSysInfo = new Label
            {
                Text = "Base instructions for AI translation. Always included before custom prompts.",
                Location = new Point(6, 30),
                Font = new Font("Segoe UI", 7.5f, FontStyle.Italic),
                ForeColor = Color.FromArgb(130, 130, 130),
                AutoSize = false,
                Height = 44,
                Width = 400,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            topPanel.Controls.AddRange(new Control[] { lblSysHeader, lblSysInfo });

            // Bottom section: Edit/Reset buttons. Taller so the AutoSize buttons
            // aren't clipped at the bottom at high DPI + Windows text scaling.
            var bottomPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 48,
                BackColor = Color.White
            };

            // AutoSize on these two buttons so they grow to fit the text at
            // any DPI – at 150% Windows scaling the fixed Width = 130 / 120
            // clipped "Edit System Prompt" / "Reset to Default". Position the
            // second button dynamically against the first's actual right edge,
            // and the status label dynamically against the second button.
            // AutoSize buttons in a FlowLayoutPanel so they grow to fit the text
            // at any DPI and the status label always sits past the second button's
            // real edge (the old design-time .Right placement slid the label under
            // the button at high DPI, clipping "(customised)" to "lt)").
            _btnEditSystem = new Button
            {
                Text = "Edit System Prompt",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(130, 25),
                Padding = new Padding(8, 0, 8, 0),
                Margin = new Padding(6, 4, 0, 4),
                FlatStyle = FlatStyle.System,
                Font = bodyFont
            };
            _btnEditSystem.Click += OnEditSystemPrompt;

            _btnResetSystem = new Button
            {
                Text = "Reset to Default",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(120, 25),
                Padding = new Padding(8, 0, 8, 0),
                Margin = new Padding(12, 4, 0, 4),
                FlatStyle = FlatStyle.System,
                Font = bodyFont
            };
            _btnResetSystem.Click += OnResetSystemPrompt;

            _lblSystemStatus = new Label
            {
                Text = "",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                ForeColor = Color.FromArgb(100, 100, 100),
                Font = new Font("Segoe UI", 8f),
                Margin = new Padding(8, 8, 0, 0)
            };

            var systemButtonFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = Padding.Empty,
                Margin = Padding.Empty
            };
            systemButtonFlow.Controls.Add(_btnEditSystem);
            systemButtonFlow.Controls.Add(_btnResetSystem);
            systemButtonFlow.Controls.Add(_lblSystemStatus);
            bottomPanel.Controls.Add(systemButtonFlow);

            // Middle: system prompt textbox
            _txtSystemPrompt = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 7.5f),
                BackColor = Color.FromArgb(248, 248, 248),
                ForeColor = Color.FromArgb(60, 60, 60),
                WordWrap = true
            };

            var textPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(6, 0, 6, 0),
                BackColor = Color.White
            };
            textPanel.Controls.Add(_txtSystemPrompt);

            // Add in reverse order for correct Dock layout
            _panelSystemPrompt.Controls.Add(textPanel);      // Fill
            _panelSystemPrompt.Controls.Add(bottomPanel);    // Bottom
            _panelSystemPrompt.Controls.Add(topPanel);       // Top
        }

        private void BuildPromptDetailPanel()
        {
            _panelPromptDetail = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                Padding = new Padding(8)
            };

            // Top info area
            var infoPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 80,
                BackColor = Color.White
            };

            _lblPromptName = new Label
            {
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = Color.FromArgb(40, 40, 40),
                Location = new Point(0, 4),
                AutoSize = false,
                Width = 400,
                Height = 22,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            _lblPromptCategorySource = new Label
            {
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(120, 120, 120),
                Location = new Point(0, 28),
                AutoSize = true
            };

            _lblPromptDescription = new Label
            {
                Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
                ForeColor = Color.FromArgb(100, 100, 100),
                Location = new Point(0, 48),
                AutoSize = false,
                Width = 400,
                Height = 28,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            infoPanel.Controls.AddRange(new Control[] { _lblPromptName, _lblPromptCategorySource, _lblPromptDescription });

            // Separator
            var separator = new Label
            {
                Dock = DockStyle.Top,
                Height = 1,
                BackColor = Color.FromArgb(220, 220, 220),
                AutoSize = false
            };

            // Prompt content textbox
            _txtPromptContent = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 8f),
                BackColor = Color.FromArgb(248, 248, 248),
                ForeColor = Color.FromArgb(60, 60, 60),
                WordWrap = true
            };

            // Bottom area: shortcut combo
            var shortcutPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 32,
                BackColor = Color.White
            };

            _lblShortcutLabel = new Label
            {
                Text = "Shortcut:",
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(80, 80, 80),
                Location = new Point(0, 7),
                AutoSize = true
            };

            _cboShortcut = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 8f),
                Location = new Point(60, 4),
                Width = 130
            };
            _cboShortcut.Items.AddRange(new object[]
            {
                "",
                "Ctrl+Alt+1", "Ctrl+Alt+2", "Ctrl+Alt+3", "Ctrl+Alt+4", "Ctrl+Alt+5",
                "Ctrl+Alt+6", "Ctrl+Alt+7", "Ctrl+Alt+8", "Ctrl+Alt+9", "Ctrl+Alt+0"
            });
            _cboShortcut.SelectedIndexChanged += OnShortcutComboChanged;

            shortcutPanel.Controls.AddRange(new Control[] { _lblShortcutLabel, _cboShortcut });

            // Add in reverse order for correct Dock layout
            _panelPromptDetail.Controls.Add(_txtPromptContent);  // Fill
            _panelPromptDetail.Controls.Add(shortcutPanel);       // Bottom
            _panelPromptDetail.Controls.Add(separator);           // Top (below info)
            _panelPromptDetail.Controls.Add(infoPanel);           // Top
        }

        private void BuildFolderInfoPanel()
        {
            _panelFolderInfo = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                Padding = new Padding(8)
            };

            _lblFolderName = new Label
            {
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = Color.FromArgb(40, 40, 40),
                Location = new Point(8, 12),
                AutoSize = true
            };

            _lblFolderPromptCount = new Label
            {
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(100, 100, 100),
                Location = new Point(8, 38),
                AutoSize = true
            };

            _lblFolderSubfolderCount = new Label
            {
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(100, 100, 100),
                Location = new Point(8, 58),
                AutoSize = true
            };

            _panelFolderInfo.Controls.AddRange(new Control[]
            {
                _lblFolderName, _lblFolderPromptCount, _lblFolderSubfolderCount
            });
        }

        // ═══════════════════════════════════════════════════════════
        //  PUBLIC API
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Populates the panel from current settings and prompt library.
        /// </summary>
        public void PopulateFromSettings(AiSettings settings, PromptLibrary library, string projectActivePromptPath = null)
        {
            _library = library ?? new PromptLibrary();
            _aiSettings = settings;
            _customSystemPrompt = settings?.CustomSystemPrompt;
            _autoTaggerInstruction = settings?.AutoTaggerInstruction;

            // Per-project active prompt – use project override if set, else global
            _activePromptPath = !string.IsNullOrEmpty(projectActivePromptPath)
                ? projectActivePromptPath
                : settings?.SelectedPromptPath ?? "";

            // Build shortcut assignments from settings
            _shortcutAssignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (settings?.QuickLauncherSlots != null)
            {
                foreach (var kvp in settings.QuickLauncherSlots)
                {
                    int slotNum;
                    if (!int.TryParse(kvp.Key, out slotNum)) continue;
                    var digit = slotNum == 10 ? "0" : slotNum.ToString();
                    var display = "Ctrl+Alt+" + digit;
                    _shortcutAssignments[kvp.Value] = display;
                }
            }

            RefreshTree();

            // Select the system prompt node by default
            if (_tvPrompts.Nodes.Count > 0)
                _tvPrompts.SelectedNode = _tvPrompts.Nodes[0];
        }

        /// <summary>
        /// Applies changes back to AI settings.
        /// </summary>
        /// <summary>
        /// Returns the active prompt path set by the user (for saving to project settings).
        /// </summary>
        public string ActivePromptPath => _activePromptPath ?? "";

        public void ApplyToSettings(AiSettings settings)
        {
            if (settings == null) return;
            settings.CustomSystemPrompt = _customSystemPrompt;
            settings.AutoTaggerInstruction = _autoTaggerInstruction;
            settings.SelectedPromptPath = _activePromptPath ?? "";

            // Save shortcut slot assignments from _shortcutAssignments
            var slots = new Dictionary<string, string>();
            foreach (var kvp in _shortcutAssignments)
            {
                var shortcutDisplay = kvp.Value;
                if (string.IsNullOrEmpty(shortcutDisplay)) continue;

                var digit = shortcutDisplay.Replace("Ctrl+Alt+", "");
                int slotNum;
                if (digit == "0") slotNum = 10;
                else if (int.TryParse(digit, out slotNum)) { }
                else continue;

                var slotKey = slotNum.ToString();
                if (!slots.ContainsKey(slotKey))
                    slots[slotKey] = kvp.Key; // kvp.Key = FilePath
            }
            settings.QuickLauncherSlots = slots;

            // Flat folder display preferences
            settings.QuickLauncherFlatFolders = _aiSettings?.QuickLauncherFlatFolders
                ?? new List<string>();
        }

        // ═══════════════════════════════════════════════════════════
        //  TREE POPULATION
        // ═══════════════════════════════════════════════════════════

        private void RefreshTree()
        {
            // Save expanded state and selection
            var expandedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectExpandedPaths(_tvPrompts.Nodes, expandedPaths);
            string selectedTag = null;
            if (_tvPrompts.SelectedNode != null)
            {
                if (_tvPrompts.SelectedNode.Tag is string s)
                    selectedTag = s;
                else if (_tvPrompts.SelectedNode.Tag is PromptTemplate pt)
                    selectedTag = pt.FilePath;
            }

            _tvPrompts.BeginUpdate();
            try
            {
                _tvPrompts.Nodes.Clear();
                _library.Refresh();

                // 1) System Prompt node (always first)
                var sysNode = new TreeNode("System Prompt")
                {
                    Tag = SystemPromptTag,
                    ForeColor = Color.FromArgb(30, 30, 30)
                };
                MakeBoldHeaderNode(sysNode);
                _tvPrompts.Nodes.Add(sysNode);

                // 1b) AutoTagger Instruction node
                var autoTagNode = new TreeNode("AutoTagger Instruction")
                {
                    Tag = AutoTaggerTag,
                    ForeColor = Color.FromArgb(30, 30, 30)
                };
                MakeBoldHeaderNode(autoTagNode);
                _tvPrompts.Nodes.Add(autoTagNode);

                // 2) Build folder structure
                var root = _library.GetFolderStructure();
                AddFolderChildren(root, _tvPrompts.Nodes);

                // 3) SuperMemory last: foundation, then tasks, then knowledge.
                AddSuperMemoryNodes();

                // Expand all by default, or restore previous state
                if (expandedPaths.Count == 0)
                {
                    _tvPrompts.ExpandAll();

                    // ...but not SuperMemory. ExpandAll would open every bank and
                    // every reference file on first view, which buries the prompt
                    // library the tab is mostly used for. Collapsed, it reads as
                    // one more top-level entry; the user opens what they want.
                    foreach (TreeNode n in _tvPrompts.Nodes)
                    {
                        if (n.Tag is BankNode smRoot && smRoot.Kind == BankNodeKind.Root)
                            n.Collapse();
                    }
                }
                else
                {
                    RestoreExpandedState(_tvPrompts.Nodes, expandedPaths);
                    // Always expand the system prompt
                    sysNode.Expand();
                }

                // Restore selection
                if (selectedTag != null)
                {
                    var found = FindNodeByTag(_tvPrompts.Nodes, selectedTag);
                    if (found != null)
                        _tvPrompts.SelectedNode = found;
                }

                if (_tvPrompts.SelectedNode == null && _tvPrompts.Nodes.Count > 0)
                    _tvPrompts.SelectedNode = _tvPrompts.Nodes[0];
            }
            finally
            {
                _tvPrompts.EndUpdate();
            }
        }

        /// <summary>
        /// Adds the SuperMemory subtree: every memory bank and the files in it.
        ///
        /// <para>Two things this has to say that nothing else in the plugin
        /// does. That <c>_shared</c> is loaded ALONGSIDE the active bank rather
        /// than being an alternative to it \u2014 the toolbar dropdown lists it
        /// like any other bank, which invites exactly the wrong reading. And
        /// that <c>reference/</c> is NOT read into prompts: it is the audit
        /// trail, so a claim can be checked against its source, and a user who
        /// does not know that will keep filing things there and wonder why the
        /// AI ignores them.</para>
        /// </summary>
        private void AddSuperMemoryNodes()
        {
            var smNode = new TreeNode("SuperMemory")
            {
                Tag = new BankNode { Kind = BankNodeKind.Root },
                ForeColor = Color.FromArgb(30, 30, 30)
            };
            MakeBoldHeaderNode(smNode);
            _tvPrompts.Nodes.Add(smNode);

            List<string> banks;
            try { banks = UserDataPath.ListMemoryBanks() ?? new List<string>(); }
            catch { return; }

            var active = "";
            try { active = SettingsService.Current?.AiSettings?.ActiveMemoryBankName ?? ""; }
            catch { }

            var shared = MemoryBankReader.SharedBankName;

            foreach (var bank in banks)
            {
                var isShared = string.Equals(bank, shared, StringComparison.OrdinalIgnoreCase);
                var isActive = !isShared && string.Equals(bank, active, StringComparison.OrdinalIgnoreCase);

                var label = bank;
                if (isShared) label += "   (loaded with every bank)";
                else if (isActive) label += "   (active)";

                var bankNode = new TreeNode(label)
                {
                    Tag = new BankNode { Kind = BankNodeKind.Bank, BankName = bank },
                    ForeColor = isActive
                        ? Color.FromArgb(0, 90, 158)      // the blue the active prompt already uses
                        : Color.FromArgb(80, 80, 80)
                };
                smNode.Nodes.Add(bankNode);

                string dir;
                try { dir = UserDataPath.GetMemoryBankDir(bank); }
                catch { continue; }
                if (!Directory.Exists(dir)) continue;

                // Markdown at the bank root IS read into prompts - all of it,
                // since defect E replaced the old three-name allow-list with a
                // directory walk.
                try
                {
                    var files = Directory.GetFiles(dir, "*.md", SearchOption.TopDirectoryOnly);
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                    foreach (var f in files)
                    {
                        bankNode.Nodes.Add(new TreeNode(Path.GetFileName(f))
                        {
                            Tag = new BankNode
                            {
                                Kind = BankNodeKind.File,
                                BankName = bank,
                                FilePath = f,
                                ReadIntoPrompts = true
                            },
                            ForeColor = Color.FromArgb(30, 30, 30)
                        });
                    }
                }
                catch { }

                // reference/ is deliberately NOT read. Greyed, and labelled.
                try
                {
                    var refDir = Path.Combine(dir, MemoryBankReader.ReferenceFolder);
                    if (Directory.Exists(refDir))
                    {
                        var refNode = new TreeNode(
                            MemoryBankReader.ReferenceFolder + "/   (not read into prompts)")
                        {
                            Tag = new BankNode { Kind = BankNodeKind.ReferenceFolder, BankName = bank },
                            ForeColor = Color.FromArgb(150, 150, 150)
                        };
                        bankNode.Nodes.Add(refNode);

                        var refFiles = Directory.GetFiles(refDir, "*.*", SearchOption.TopDirectoryOnly);
                        Array.Sort(refFiles, StringComparer.OrdinalIgnoreCase);
                        foreach (var f in refFiles)
                        {
                            refNode.Nodes.Add(new TreeNode(Path.GetFileName(f))
                            {
                                Tag = new BankNode
                                {
                                    Kind = BankNodeKind.File,
                                    BankName = bank,
                                    FilePath = f,
                                    ReadIntoPrompts = false
                                },
                                ForeColor = Color.FromArgb(150, 150, 150)
                            });
                        }
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// Opens one bank file for editing, then re-reads it into the pane.
        ///
        /// <para>Only Markdown. reference/ also holds harvested TMX and other
        /// non-text material, and offering to edit those in a plain text box
        /// invites corrupting a file the user cannot easily reconstruct.</para>
        /// </summary>
        private void EditBankFile(BankNode bn)
        {
            if (bn == null || string.IsNullOrEmpty(bn.FilePath)) return;

            if (!bn.FilePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this,
                    "Only Markdown files can be edited here.\n\n"
                    + "Open the bank folder to work on this one.",
                    "Supervertaler", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new BankFileEditorDialog(bn.FilePath, bn.BankName, bn.ReadIntoPrompts))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
            }

            // Re-read from disk rather than trusting what the dialog had: the
            // file is the source of truth, and something else may have written
            // to it too.
            ShowBankFile(bn);
        }

        /// <summary>
        /// Shows or hides the reference-images row, and fills it in.
        ///
        /// <para>Visible for a bank and for its <c>figures.md</c>: the folder is
        /// where that file comes from, so it belongs beside it. Hidden for
        /// everything else.</para>
        ///
        /// <para>The setting is per PROJECT while this tab is bank-shaped, so
        /// the label names the project. With no project open there is nothing to
        /// attach a folder to, and the row says so rather than offering a Browse
        /// button that would have nowhere to save.</para>
        /// </summary>
        private void UpdateImagesRow(BankNode bn)
        {
            var isBank = bn != null && bn.Kind == BankNodeKind.Bank;
            var isFigures = bn != null && bn.Kind == BankNodeKind.File
                            && string.Equals(Path.GetFileName(bn.FilePath ?? ""), "figures.md",
                                             StringComparison.OrdinalIgnoreCase);

            _panelImagesRow.Visible = isBank || isFigures;
            if (!_panelImagesRow.Visible)
            {
                _panelBankFile.PerformLayout();
                return;
            }

            var projectPath = TermLensEditorViewPart.GetCurrentProjectPath();
            var projectName = TermLensEditorViewPart.GetCurrentProjectName();

            if (string.IsNullOrEmpty(projectPath))
            {
                _lblImagesLabel.Text = "Reference images \u2014 no Trados project is open";
                _txtImagesFolder.Text =
                    "The drawings folder is remembered per project, so open the project first.";
                _btnImagesBrowse.Enabled = false;
                _btnImagesClear.Enabled = false;
                _lblImagesCaveat.Visible = false;
                _panelBankFile.PerformLayout();
                return;
            }

            _lblImagesLabel.Text = "Reference images for " + (projectName ?? "this project");
            _btnImagesBrowse.Enabled = true;
            _lblImagesCaveat.Visible = true;

            var folder = "";
            try { folder = ProjectSettings.Load(projectPath)?.ReferenceImagesFolder ?? ""; }
            catch { }

            var resolved = Core.ReferenceImages.Resolve(folder);
            if (!string.IsNullOrEmpty(resolved))
            {
                var count = Core.ReferenceImages.List(resolved).Count;
                _txtImagesFolder.Text = resolved + "   (" + count + " image" + (count == 1 ? "" : "s") + ")";
                _btnImagesClear.Enabled = true;
            }
            else if (!string.IsNullOrEmpty(folder))
            {
                _txtImagesFolder.Text = folder + "   \u2014 this folder is missing";
                _btnImagesClear.Enabled = true;
            }
            else
            {
                _txtImagesFolder.Text = "(none set)";
                _btnImagesClear.Enabled = false;
            }

            _panelBankFile.PerformLayout();
        }

        /// <summary>
        /// Picks the drawings folder for the open project.
        ///
        /// <para>Starts wherever <c>ReferenceImages.Suggest</c> proposes, which
        /// is only ever a suggestion: a folder found by walking up the tree can
        /// belong to a different job, and drawings from the wrong matter are
        /// worse than none because the output still reads plausibly. So the
        /// dialog opens there and the user confirms.</para>
        /// </summary>
        private void BrowseForReferenceImages()
        {
            var projectPath = TermLensEditorViewPart.GetCurrentProjectPath();
            if (string.IsNullOrEmpty(projectPath)) return;

            var start = "";
            try
            {
                start = ProjectSettings.Load(projectPath)?.ReferenceImagesFolder ?? "";
                if (string.IsNullOrEmpty(start))
                {
                    var suggestions = Core.ReferenceImages.Suggest(projectPath);
                    if (suggestions.Count > 0) start = suggestions[0];
                }
            }
            catch { }

            // FolderPicker, not FolderBrowserDialog: the latter is still the
            // Windows 2000-era tree with no address bar and nowhere to paste a
            // path. This wrapper already existed for exactly that reason - see
            // Controls/FolderPicker.cs, which uses IFileOpenDialog with
            // FOS_PICKFOLDERS and falls back only if the COM call fails.
            var chosen = FolderPicker.Show(
                this, "Choose the folder holding this project's drawings", start);
            if (string.IsNullOrEmpty(chosen)) return;

            var images = Core.ReferenceImages.List(chosen);
            if (images.Count == 0)
            {
                var go = MessageBox.Show(this,
                    "No images found in\n\n" + chosen + "\n\nUse it anyway?",
                    "Reference images",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (go != DialogResult.Yes) return;
            }

            SaveReferenceImagesFolder(projectPath, chosen);

            UpdateImagesRow(_tvPrompts.SelectedNode?.Tag as BankNode);
        }

        private void ClearReferenceImages()
        {
            var projectPath = TermLensEditorViewPart.GetCurrentProjectPath();
            if (string.IsNullOrEmpty(projectPath)) return;

            SaveReferenceImagesFolder(projectPath, "");
            UpdateImagesRow(_tvPrompts.SelectedNode?.Tag as BankNode);
        }

        /// <summary>
        /// Writes the folder into the project's own settings.
        ///
        /// <para>Read-modify-write on the stored object rather than through
        /// TermLensSettings.ExtractProjectSettings, which rebuilds a
        /// ProjectSettings from the GLOBAL settings and would blank every
        /// per-project field this one does not know about.</para>
        /// </summary>
        private void SaveReferenceImagesFolder(string projectPath, string folder)
        {
            try
            {
                var ps = ProjectSettings.Load(projectPath) ?? new ProjectSettings();
                ps.ReferenceImagesFolder = folder ?? "";
                if (string.IsNullOrEmpty(ps.ProjectPath)) ps.ProjectPath = projectPath;
                if (string.IsNullOrEmpty(ps.ProjectName))
                    ps.ProjectName = TermLensEditorViewPart.GetCurrentProjectName() ?? "";
                ProjectSettings.Save(projectPath, ps);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save the reference images folder.\n\n" + ex.Message,
                    "Reference images", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>Shows a bank file, read-only, saying whether the AI reads it.</summary>
        private void ShowBankFile(BankNode bn)
        {
            _lblBankFileName.Text = Path.GetFileName(bn.FilePath ?? "");
            _lblBankFileNote.Text = bn.ReadIntoPrompts
                ? "In memory bank \"" + bn.BankName + "\". Read into the AI's context."
                : "In memory bank \"" + bn.BankName + "\", reference folder. Kept for you, "
                  + "never read into a prompt - fold anything worth keeping into the bank's own files.";

            try
            {
                // reference/ holds whatever the user or a harvest put there,
                // including PDFs. Showing their bytes as text is worse than
                // useless: it looks like a corrupted file rather than a file
                // this pane cannot display.
                if (LooksBinary(bn.FilePath))
                {
                    var size = new FileInfo(bn.FilePath).Length;
                    _txtBankFile.Text =
                        "(" + Path.GetExtension(bn.FilePath).TrimStart('.').ToUpperInvariant()
                        + " file, " + FormatSize(size) + ")"
                        + Environment.NewLine + Environment.NewLine
                        + "Not a text file, so there is nothing to show here."
                        + Environment.NewLine
                        + "Open the bank folder to work with it.";
                }
                else
                {
                    var raw = File.ReadAllText(bn.FilePath);
                    // Non-markdown text lands in reference/ too (harvested TMX,
                    // notes). Rendering those as markdown is pointless, so show
                    // them as they are.
                    if (bn.FilePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                        _txtBankFile.Rtf = MarkdownToRtf.Convert(raw);
                    else
                        _txtBankFile.Text = raw;
                }
            }
            catch (Exception ex)
            {
                _txtBankFile.Text = "(could not read this file: " + ex.Message + ")";
            }

            _txtBankFile.Select(0, 0);
            _txtBankFile.ScrollToCaret();
        }

        /// <summary>
        /// Whether a file is binary, by looking for a NUL byte in its first 8 KB.
        ///
        /// <para>Sniffed rather than judged by extension: reference/ is a folder
        /// the user fills by hand, so the extension list would always be one
        /// format behind. A NUL byte in the first few KB is the standard test
        /// and is what git itself uses.</para>
        /// </summary>
        private static bool LooksBinary(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var buf = new byte[8192];
                    var read = fs.Read(buf, 0, buf.Length);
                    for (int i = 0; i < read; i++)
                        if (buf[i] == 0) return true;
                }
            }
            catch { }
            return false;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024L) return (bytes / (1024.0 * 1024.0)).ToString("0.#") + " MB";
            if (bytes >= 1024L) return (bytes / 1024.0).ToString("0.#") + " KB";
            return bytes + " bytes";
        }

        /// <summary>Summary shown for a bank, the SuperMemory root, or reference/.</summary>
        private void ShowBankSummary(BankNode bn, TreeNode node)
        {
            switch (bn.Kind)
            {
                case BankNodeKind.Root:
                    _lblBankFileName.Text = "SuperMemory";
                    _lblBankFileNote.Text =
                        "What the AI knows about your clients and jobs. Each bank is a folder of "
                        + "Markdown files; _shared is loaded alongside whichever bank is active.";
                    _txtBankFile.Text = "";
                    break;

                case BankNodeKind.Bank:
                    _lblBankFileName.Text = bn.BankName;
                    _lblBankFileNote.Text = "Memory bank. Select a file to read it.";
                    _txtBankFile.Text = "";
                    break;

                default:
                    _lblBankFileName.Text = MemoryBankReader.ReferenceFolder + "/";
                    _lblBankFileNote.Text =
                        "The audit trail for \"" + bn.BankName + "\" - where harvested "
                        + "changes and notes land. Never read into a prompt, so a claim the AI "
                        + "makes can be checked against what it came from.";
                    _txtBankFile.Text = "";
                    break;
            }
        }

        // ─── Drag and drop ──────────────────────────────────────

        private void OnTreeItemDrag(object sender, ItemDragEventArgs e)
        {
            var node = e.Item as TreeNode;
            if (node == null) return;

            // Only allow dragging prompt nodes (not folders or system prompt)
            var prompt = node.Tag as PromptTemplate;
            if (prompt == null || prompt.IsReadOnly) return;

            DoDragDrop(node, DragDropEffects.Move);
        }

        private void OnTreeDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(typeof(TreeNode))
                ? DragDropEffects.Move
                : DragDropEffects.None;
        }

        private void OnTreeDragOver(object sender, DragEventArgs e)
        {
            var pt = _tvPrompts.PointToClient(new Point(e.X, e.Y));
            var targetNode = _tvPrompts.GetNodeAt(pt);

            if (targetNode == null)
            {
                e.Effect = DragDropEffects.None;
                return;
            }

            // Can only drop on folder nodes (Tag is a string path, not PromptTemplate)
            var isFolderTarget = targetNode.Tag is string && targetNode.Tag as string != "__SYSTEM_PROMPT__";
            e.Effect = isFolderTarget ? DragDropEffects.Move : DragDropEffects.None;

            _tvPrompts.SelectedNode = targetNode;
        }

        private void OnTreeDragDrop(object sender, DragEventArgs e)
        {
            var draggedNode = e.Data.GetData(typeof(TreeNode)) as TreeNode;
            if (draggedNode == null) return;

            var prompt = draggedNode.Tag as PromptTemplate;
            if (prompt == null || prompt.IsReadOnly) return;

            var pt = _tvPrompts.PointToClient(new Point(e.X, e.Y));
            var targetNode = _tvPrompts.GetNodeAt(pt);
            if (targetNode == null) return;

            var targetPath = targetNode.Tag as string;
            if (targetPath == null || targetPath == "__SYSTEM_PROMPT__") return;

            // Move the prompt file to the target folder
            _library.MovePrompt(prompt, targetPath);
            RefreshTree();
        }

        private void AddFolderChildren(PromptFolderNode folderNode, TreeNodeCollection parentNodes)
        {
            // Add subfolders
            foreach (var child in folderNode.Children)
            {
                var displayName = "\U0001F4C1 " + child.Name;
                var treeNode = new TreeNode(displayName)
                {
                    Tag = child.RelativePath ?? child.Name
                };
                parentNodes.Add(treeNode);
                AddFolderChildren(child, treeNode.Nodes);
            }

            // Add prompts
            foreach (var prompt in folderNode.Prompts)
            {
                var displayName = prompt.Name;

                // Show "(hidden)" suffix for QuickLauncher prompts hidden from the menu
                if (prompt.IsQuickLauncher && prompt.HiddenFromMenu)
                    displayName += "  (hidden)";

                // Show shortcut suffix for QuickLauncher prompts
                if (prompt.IsQuickLauncher)
                {
                    string shortcut;
                    if (_shortcutAssignments.TryGetValue(prompt.FilePath, out shortcut) &&
                        !string.IsNullOrEmpty(shortcut))
                    {
                        displayName += "  [" + shortcut + "]";
                    }
                }

                // Mark the active prompt for this project
                var isActive = !string.IsNullOrEmpty(_activePromptPath)
                    && PromptPaths.Match(prompt.RelativePath, _activePromptPath) /* marker-tolerant, #100 */;
                if (isActive)
                    displayName = "\U0001F4CC " + displayName; // 📌 pin emoji

                var node = new TreeNode(displayName)
                {
                    Tag = prompt
                };

                // Active prompt gets accent color. Bold NodeFont triggers a
                // WinForms TreeView clipping bug (the node's display rect is
                // measured with the regular font and never re-measured for the
                // bold font, so the right edge of the bold text gets cut off
                // after the dialog is reopened). The 📌 emoji + blue colour
                // are already a strong enough active marker.
                if (isActive)
                {
                    node.ForeColor = Color.FromArgb(0, 90, 158); // Supervertaler blue
                }
                else
                {
                    // Muted for default or hidden, dark for custom
                    node.ForeColor = (prompt.IsDefault || prompt.HiddenFromMenu)
                        ? Color.FromArgb(80, 80, 80)
                        : Color.FromArgb(30, 30, 30);
                }

                parentNodes.Add(node);
            }
        }

        // ─── Expand/collapse state helpers ───────────────────────

        private void CollectExpandedPaths(TreeNodeCollection nodes, HashSet<string> paths)
        {
            foreach (TreeNode node in nodes)
            {
                if (node.IsExpanded)
                {
                    var key = GetNodeTagKey(node);
                    if (key != null)
                        paths.Add(key);
                }
                CollectExpandedPaths(node.Nodes, paths);
            }
        }

        private void RestoreExpandedState(TreeNodeCollection nodes, HashSet<string> paths)
        {
            foreach (TreeNode node in nodes)
            {
                var key = GetNodeTagKey(node);
                if (key != null && paths.Contains(key))
                    node.Expand();
                RestoreExpandedState(node.Nodes, paths);
            }
        }

        private TreeNode FindNodeByTag(TreeNodeCollection nodes, string tagKey)
        {
            foreach (TreeNode node in nodes)
            {
                var key = GetNodeTagKey(node);
                if (key != null && string.Equals(key, tagKey, StringComparison.OrdinalIgnoreCase))
                    return node;

                var child = FindNodeByTag(node.Nodes, tagKey);
                if (child != null)
                    return child;
            }
            return null;
        }

        private string GetNodeTagKey(TreeNode node)
        {
            if (node.Tag is string s)
                return s;
            if (node.Tag is PromptTemplate pt)
                return pt.FilePath;
            // Bank nodes need a key too, or expanding a bank is undone by the
            // next Refresh. Prefixed so it can never collide with a prompt
            // folder path, which is what the plain-string case above means.
            if (node.Tag is BankNode bn)
                return "sm:" + bn.Kind + ":" + (bn.BankName ?? "") + ":" + (bn.FilePath ?? "");
            return null;
        }

        // ═══════════════════════════════════════════════════════════
        //  TREE SELECTION – swap right panels
        // ═══════════════════════════════════════════════════════════

        private void OnTreeAfterSelect(object sender, TreeViewEventArgs e)
        {
            if (e.Node == null)
            {
                _shell.HideAllDetails();
                return;
            }

            // Before the string cases: the final branch below treats ANY string
            // tag as a prompt folder, so a bank node must be matched first.
            if (e.Node.Tag is BankNode bankNode)
            {
                if (bankNode.Kind == BankNodeKind.File) ShowBankFile(bankNode);
                else ShowBankSummary(bankNode, e.Node);
                UpdateImagesRow(bankNode);
                _shell.ShowDetail(_panelBankFile);
            }
            else if (e.Node.Tag is string tagStr && tagStr == SystemPromptTag)
            {
                // System prompt selected
                UpdateSystemPromptDisplay();
                _shell.ShowDetail(_panelSystemPrompt);
            }
            else if (e.Node.Tag is string atTag && atTag == AutoTaggerTag)
            {
                // AutoTagger instruction selected
                UpdateAutoTaggerDisplay();
                _shell.ShowDetail(_panelAutoTagger);
            }
            else if (e.Node.Tag is PromptTemplate prompt)
            {
                // Prompt selected – show detail
                ShowPromptDetail(prompt);
                _shell.ShowDetail(_panelPromptDetail);
            }
            else if (e.Node.Tag is string folderPath)
            {
                // Folder selected – show folder info
                ShowFolderInfo(e.Node, folderPath);
                _shell.ShowDetail(_panelFolderInfo);
            }
            else
            {
                _shell.HideAllDetails();
            }
        }

        private void ShowPromptDetail(PromptTemplate prompt)
        {
            _lblPromptName.Text = prompt.Name;

            var source = prompt.IsReadOnly ? "Supervertaler" : (prompt.IsDefault ? "Default" : "Custom");
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(prompt.Category))
                parts.Add(prompt.Category);
            parts.Add(source);
            _lblPromptCategorySource.Text = string.Join(" \u2022 ", parts);

            if (!string.IsNullOrWhiteSpace(prompt.Description))
            {
                _lblPromptDescription.Text = prompt.Description;
                _lblPromptDescription.Visible = true;
            }
            else
            {
                _lblPromptDescription.Text = "";
                _lblPromptDescription.Visible = false;
            }

            _txtPromptContent.Text = prompt.Content;

            // Show shortcut combo only for QuickLauncher prompts
            _lblShortcutLabel.Visible = prompt.IsQuickLauncher;
            _cboShortcut.Visible = prompt.IsQuickLauncher;

            if (prompt.IsQuickLauncher)
            {
                string currentShortcut;
                _shortcutAssignments.TryGetValue(prompt.FilePath, out currentShortcut);
                _cboShortcut.Tag = prompt; // store prompt reference to identify on change
                _cboShortcut.SelectedIndexChanged -= OnShortcutComboChanged;
                _cboShortcut.SelectedItem = currentShortcut ?? "";
                _cboShortcut.SelectedIndexChanged += OnShortcutComboChanged;
            }
        }

        private void ShowFolderInfo(TreeNode treeNode, string folderPath)
        {
            // Extract display name (remove emoji prefix if present)
            var displayName = treeNode.Text;
            if (displayName.StartsWith("\U0001F4C1 "))
                displayName = displayName.Substring(3);

            _lblFolderName.Text = displayName;

            // Count prompts (direct children that are PromptTemplate)
            int promptCount = 0;
            int subfolderCount = 0;
            foreach (TreeNode child in treeNode.Nodes)
            {
                if (child.Tag is PromptTemplate)
                    promptCount++;
                else if (child.Tag is string)
                    subfolderCount++;
            }

            _lblFolderPromptCount.Text = promptCount == 1
                ? "1 prompt"
                : promptCount + " prompts";

            if (subfolderCount > 0)
            {
                _lblFolderSubfolderCount.Text = subfolderCount == 1
                    ? "1 subfolder"
                    : subfolderCount + " subfolders";
                _lblFolderSubfolderCount.Visible = true;
            }
            else
            {
                _lblFolderSubfolderCount.Visible = false;
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  SYSTEM PROMPT
        // ═══════════════════════════════════════════════════════════

        private void UpdateSystemPromptDisplay()
        {
            if (!string.IsNullOrWhiteSpace(_customSystemPrompt))
            {
                _txtSystemPrompt.Text = _customSystemPrompt;
                _lblSystemStatus.Text = "(customised)";
                _lblSystemStatus.ForeColor = Color.FromArgb(180, 120, 0);
            }
            else
            {
                _txtSystemPrompt.Text = TranslationPrompt.GetDefaultBaseSystemPrompt();
                _lblSystemStatus.Text = "(default)";
                _lblSystemStatus.ForeColor = Color.FromArgb(30, 130, 60);
            }
        }

        private void OnEditSystemPrompt(object sender, EventArgs e)
        {
            var content = !string.IsNullOrWhiteSpace(_customSystemPrompt)
                ? _customSystemPrompt
                : TranslationPrompt.GetDefaultBaseSystemPrompt();

            var prompt = new PromptTemplate
            {
                Name = "System Prompt",
                Description = "Base system instructions for AI translation",
                Category = "System",
                Content = content
            };

            using (var dlg = new PromptEditorDialog(prompt))
            {
                dlg.Text = "Edit System Prompt";
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _customSystemPrompt = dlg.Result.Content;
                    UpdateSystemPromptDisplay();
                }
            }
        }

        private void OnResetSystemPrompt(object sender, EventArgs e)
        {
            var result = MessageBox.Show(
                "Reset the system prompt to the default?\n\nThis will discard any customisations.",
                "Reset System Prompt",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);

            if (result == DialogResult.Yes)
            {
                _customSystemPrompt = null;
                UpdateSystemPromptDisplay();
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  PROMPT OPERATIONS
        // ═══════════════════════════════════════════════════════════

        private PromptTemplate GetSelectedPrompt()
        {
            if (_tvPrompts.SelectedNode == null) return null;
            return _tvPrompts.SelectedNode.Tag as PromptTemplate;
        }

        private void OnNewPrompt(object sender, EventArgs e)
        {
            // Pre-fill Domain from selected folder
            string preFillDomain = null;
            if (_tvPrompts.SelectedNode != null)
            {
                if (_tvPrompts.SelectedNode.Tag is string folderPath && folderPath != SystemPromptTag)
                {
                    // Selected a folder – use the full relative path as domain
                    if (!string.IsNullOrEmpty(folderPath))
                        preFillDomain = folderPath;
                }
                else if (_tvPrompts.SelectedNode.Tag is PromptTemplate pt)
                {
                    preFillDomain = pt.Category;
                }
            }

            var newPrompt = new PromptTemplate();
            // Default to "Translate" when no folder is selected so the new prompt
            // is visible in the Batch Translate dropdown (which filters by category).
            newPrompt.Category = !string.IsNullOrEmpty(preFillDomain) ? preFillDomain : "Translate";

            using (var dlg = new PromptEditorDialog(newPrompt))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _library.SavePrompt(dlg.Result);
                    RefreshTree();
                }
            }
        }

        private void OnEditPrompt(object sender, EventArgs e)
        {
            // A bank file uses the same Edit button rather than gaining its own
            // gesture: one tab, one way to edit the thing you have selected.
            if (_tvPrompts.SelectedNode != null &&
                _tvPrompts.SelectedNode.Tag is BankNode bankFile &&
                bankFile.Kind == BankNodeKind.File)
            {
                EditBankFile(bankFile);
                return;
            }

            // If system prompt node is selected, edit system prompt instead
            if (_tvPrompts.SelectedNode != null &&
                _tvPrompts.SelectedNode.Tag is string tag && tag == SystemPromptTag)
            {
                OnEditSystemPrompt(sender, e);
                return;
            }

            var selected = GetSelectedPrompt();
            if (selected == null)
            {
                MessageBox.Show("Select a prompt to edit.",
                    "Prompts", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Built-in prompts open read-only (content immutable, but hidden checkbox editable).
            // To modify a built-in prompt's content, use Clone.
            using (var dlg = new PromptEditorDialog(selected))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _library.SavePrompt(dlg.Result);
                    RefreshTree();
                }
            }
        }

        private void OnDeletePrompt(object sender, EventArgs e)
        {
            // Delete means "delete this memory bank" when one is selected.
            if (GetSelectedBank() != null)
            {
                DeleteSelectedBank();
                return;
            }

            var selected = GetSelectedPrompt();
            if (selected == null)
            {
                MessageBox.Show("Select a prompt to delete.",
                    "Prompts", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (selected.IsReadOnly)
            {
                MessageBox.Show("This prompt is from the Supervertaler desktop app and cannot be deleted from here.",
                    "Prompts", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var result = MessageBox.Show(
                $"Delete prompt \"{selected.Name}\"?\n\nThis cannot be undone.",
                "Delete Prompt",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

            if (result == DialogResult.Yes)
            {
                // Remove shortcut assignment if any
                _shortcutAssignments.Remove(selected.FilePath);
                _library.DeletePrompt(selected);
                RefreshTree();
            }
        }

        private void OnClonePrompt(object sender, EventArgs e)
        {
            var selected = GetSelectedPrompt();
            if (selected == null) return;

            // Read the original file content
            if (string.IsNullOrEmpty(selected.FilePath) || !System.IO.File.Exists(selected.FilePath))
                return;

            var originalContent = System.IO.File.ReadAllText(selected.FilePath);

            // Generate a unique clone name: "Name (2)", "Name (3)", etc.
            var dir = Path.GetDirectoryName(selected.FilePath);
            var baseName = selected.Name;
            string cloneName = null;
            string clonePath = null;

            for (int i = 2; i <= 99; i++)
            {
                var candidate = $"{baseName} ({i})";
                var candidatePath = Path.Combine(dir, candidate + ".md");
                if (!System.IO.File.Exists(candidatePath))
                {
                    cloneName = candidate;
                    clonePath = candidatePath;
                    break;
                }
            }

            if (cloneName == null) return;

            // Update the name in the YAML front matter
            var cloneContent = originalContent;
            var namePattern = new System.Text.RegularExpressions.Regex(
                @"^name:\s*""[^""]*""", System.Text.RegularExpressions.RegexOptions.Multiline);
            cloneContent = namePattern.Replace(cloneContent,
                $"name: \"{PromptLibrary.EscapeYaml(cloneName)}\"", 1);

            System.IO.File.WriteAllText(clonePath, cloneContent);
            _library.Refresh();
            RefreshTree();
        }

        private void OnRefresh(object sender, EventArgs e)
        {
            _library.Refresh();
            RefreshTree();
        }

        private void OnDeleteFolder(object sender, EventArgs e)
        {
            var node = _tvPrompts.SelectedNode;
            if (node == null || !(node.Tag is string folderPath) || folderPath == SystemPromptTag)
                return;

            var folderName = Path.GetFileName(folderPath);
            var result = MessageBox.Show(
                $"Delete the folder '{folderName}' and all prompts inside it?\n\nThis cannot be undone.",
                "Delete Folder",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

            if (result == DialogResult.Yes)
            {
                _library.DeleteFolder(folderPath);
                RefreshTree();
            }
        }

        private void ToggleFlatFolder()
        {
            var node = _tvPrompts.SelectedNode;
            if (node == null || !(node.Tag is string folderPath) || folderPath == SystemPromptTag)
                return;

            if (_aiSettings == null) return;
            if (_aiSettings.QuickLauncherFlatFolders == null)
                _aiSettings.QuickLauncherFlatFolders = new List<string>();

            if (_aiSettings.QuickLauncherFlatFolders.Contains(folderPath))
                _aiSettings.QuickLauncherFlatFolders.Remove(folderPath);
            else
                _aiSettings.QuickLauncherFlatFolders.Add(folderPath);
        }

        private void OnRestoreDefaults(object sender, EventArgs e)
        {
            var result = MessageBox.Show(
                "Restore all default prompts?\n\nThis will overwrite any edits to default prompts and re-create deleted ones.",
                "Restore Default Prompts",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);

            if (result == DialogResult.Yes)
            {
                _library.RestoreDefaultPrompts();
                RefreshTree();
            }
        }

        private void OnNewFolder(object sender, EventArgs e)
        {
            // Determine parent folder from current selection
            string parentRelativePath = "";
            if (_tvPrompts.SelectedNode != null)
            {
                if (_tvPrompts.SelectedNode.Tag is string folderPath && folderPath != SystemPromptTag)
                {
                    parentRelativePath = folderPath;
                }
                else if (_tvPrompts.SelectedNode.Tag is PromptTemplate pt)
                {
                    // Use parent folder of the selected prompt
                    var relDir = Path.GetDirectoryName(pt.RelativePath);
                    if (!string.IsNullOrEmpty(relDir))
                        parentRelativePath = relDir.Replace('\\', '/');
                }
            }

            var folderName = PromptInputBox("New Folder", "Folder name:");
            if (string.IsNullOrWhiteSpace(folderName)) return;

            // Sanitise folder name
            foreach (var c in Path.GetInvalidFileNameChars())
                folderName = folderName.Replace(c, '_');

            var relativePath = string.IsNullOrEmpty(parentRelativePath)
                ? folderName
                : parentRelativePath + "/" + folderName;

            _library.CreateFolder(relativePath);
            RefreshTree();
        }

        // ─── Move up/down ──────────────────────────────────────

        /// <summary>
        /// Makes a node bold without it being clipped.
        ///
        /// <para>WinForms measures a TreeNode's display rectangle with the
        /// CONTROL's font, never with its NodeFont, so bold text is laid out in
        /// a box sized for regular text and the right edge is cut off. Both
        /// header nodes worked around it by appending two literal spaces — but
        /// the shortfall grows with the string, so a fixed pad cannot cover it.
        /// "System Prompt" fitted; "AutoTagger Instruction" lost its last
        /// letters.</para>
        ///
        /// <para>So measure it: widen with spaces until the regular-font width
        /// of the padded text covers the bold width of the real text. Self-
        /// correcting for any font, any DPI, any label length.</para>
        ///
        /// <para>Padding the text is safe because these nodes are identified by
        /// their Tag, never by Text.</para>
        /// </summary>
        private void MakeBoldHeaderNode(TreeNode node)
        {
            var bold = new Font(_tvPrompts.Font, FontStyle.Bold);
            node.NodeFont = bold;

            var needed = TextRenderer.MeasureText(node.Text, bold).Width;
            var pad = "";
            // Bounded: a handful of spaces always suffices, and a runaway loop
            // in tree construction would hang the dialog.
            for (int i = 0; i < 12; i++)
            {
                if (TextRenderer.MeasureText(node.Text + pad, _tvPrompts.Font).Width >= needed) break;
                pad += " ";
            }
            // One more, so the glyph never touches the edge of its box.
            node.Text += pad + " ";
        }

        private void OnMoveUp(object sender, EventArgs e)
        {
            MoveSelectedPrompt(-1);
        }

        private void OnMoveDown(object sender, EventArgs e)
        {
            MoveSelectedPrompt(1);
        }

        private void MoveSelectedPrompt(int direction)
        {
            var prompt = GetSelectedPrompt();
            if (prompt == null) return;

            // Get sibling prompts in the same folder
            var folderRelPath = Path.GetDirectoryName(prompt.RelativePath)?.Replace('\\', '/') ?? "";
            var allPrompts = _library.GetAllPrompts();
            var siblings = new List<PromptTemplate>();
            foreach (var p in allPrompts)
            {
                var pFolder = Path.GetDirectoryName(p.RelativePath)?.Replace('\\', '/') ?? "";
                if (string.Equals(pFolder, folderRelPath, StringComparison.OrdinalIgnoreCase))
                    siblings.Add(p);
            }

            // Sort siblings by current sort order
            siblings.Sort((a, b) =>
            {
                var cmp = a.SortOrder.CompareTo(b.SortOrder);
                return cmp != 0 ? cmp : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            // Find index of the selected prompt
            var idx = -1;
            for (int i = 0; i < siblings.Count; i++)
            {
                if (siblings[i].FilePath == prompt.FilePath)
                {
                    idx = i;
                    break;
                }
            }
            if (idx < 0) return;

            var newIdx = idx + direction;
            if (newIdx < 0 || newIdx >= siblings.Count) return;

            // Reassign sort orders: give each sibling a sequential value (10, 20, 30...)
            // then swap the two positions
            for (int i = 0; i < siblings.Count; i++)
                siblings[i].SortOrder = (i + 1) * 10;

            // Swap
            var tmp = siblings[idx].SortOrder;
            siblings[idx].SortOrder = siblings[newIdx].SortOrder;
            siblings[newIdx].SortOrder = tmp;

            // Save both to disk
            _library.SavePrompt(siblings[idx]);
            _library.SavePrompt(siblings[newIdx]);

            RefreshTree();

            // Re-select the moved prompt
            var found = FindNodeByTag(_tvPrompts.Nodes, prompt.FilePath);
            if (found != null)
                _tvPrompts.SelectedNode = found;
        }

        // ─── Tree interaction ────────────────────────────────────

        private void OnTreeNodeDoubleClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Node == null) return;

            if (e.Node.Tag is string tag && tag == SystemPromptTag)
            {
                OnEditSystemPrompt(sender, EventArgs.Empty);
            }
            else if (e.Node.Tag is PromptTemplate)
            {
                OnEditPrompt(sender, EventArgs.Empty);
            }
        }

        private void OnTreeNodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Button == MouseButtons.Right && e.Node != null)
            {
                _tvPrompts.SelectedNode = e.Node;
                if (e.Node.Tag is PromptTemplate ||
                    e.Node.Tag is BankNode ||
                    (e.Node.Tag is string tag && tag != SystemPromptTag))
                {
                    _treeContextMenu.Show(_tvPrompts, e.Location);
                }
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  MEMORY BANK ACTIONS
        // ═══════════════════════════════════════════════════════════

        /// <summary>The selected node's bank, when a whole bank is selected.</summary>
        private BankNode GetSelectedBank()
        {
            var bn = _tvPrompts.SelectedNode?.Tag as BankNode;
            return (bn != null && bn.Kind == BankNodeKind.Bank) ? bn : null;
        }

        private static bool IsSharedBank(string name)
            => string.Equals(name, MemoryBankReader.SharedBankName,
                             StringComparison.OrdinalIgnoreCase);

        private void SetSelectedBankActive()
        {
            var bank = GetSelectedBank();
            if (bank == null || IsSharedBank(bank.BankName)) return;

            SettingsService.Update(s =>
            {
                if (s.AiSettings == null) s.AiSettings = new AiSettings();
                s.AiSettings.ActiveMemoryBankName = bank.BankName;
            });

            // Against the project as well, or opening this job again picks
            // whatever bank was last used anywhere - which for a bank chosen HERE
            // used to mean the choice never survived a project switch at all.
            AiAssistantViewPart.RememberBankForCurrentProject(bank.BankName);

            RefreshTree();
        }

        private void RenameSelectedBank()
        {
            var bank = GetSelectedBank();
            if (bank == null || IsSharedBank(bank.BankName)) return;

            var newName = PromptForBankName("Rename memory bank",
                "New name for '" + bank.BankName + "':", bank.BankName);
            if (string.IsNullOrWhiteSpace(newName)) return;

            // Our own inbox watcher holds a handle on the ACTIVE bank's
            // reference/ folder, and Windows will not rename a folder above an
            // open handle. Without this, renaming the active bank always failed
            // and blamed Obsidian.
            string sanitised, error;
            bool renamed;
            AiAssistantViewPart.ReleaseMemoryBankHandles();
            try
            {
                renamed = UserDataPath.TryRenameMemoryBank(
                    bank.BankName, newName, out sanitised, out error);
            }
            finally
            {
                AiAssistantViewPart.ReacquireMemoryBankHandles();
            }

            if (!renamed)
            {
                MessageBox.Show(this, error, "Rename memory bank",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // The active-bank setting stores a NAME. Without this it would point
            // at a folder that no longer exists, and the reader treats a missing
            // bank as an empty one - so SuperMemory would contribute nothing to
            // every prompt, silently.
            var wasActive = string.Equals(
                SettingsService.Current?.AiSettings?.ActiveMemoryBankName, bank.BankName,
                StringComparison.OrdinalIgnoreCase);

            // Projects record a bank by NAME too, and a project naming the old
            // one now names nothing. Runs for every rename, not just the active
            // bank's: a project can name a bank that is not currently active.
            Settings.ProjectSettings.RenameMemoryBankEverywhere(bank.BankName, sanitised);

            if (wasActive)
            {
                SettingsService.Update(s =>
                {
                    if (s.AiSettings == null) s.AiSettings = new AiSettings();
                    s.AiSettings.ActiveMemoryBankName = sanitised;
                });

                // Re-point the watcher at the renamed folder. The reacquire above
                // ran while the setting still named the old bank, so it attached
                // to nothing.
                AiAssistantViewPart.ReleaseMemoryBankHandles();
                AiAssistantViewPart.ReacquireMemoryBankHandles();
            }

            RefreshTree();
        }

        private void DeleteSelectedBank()
        {
            var bank = GetSelectedBank();
            if (bank == null || IsSharedBank(bank.BankName)) return;

            // Refuse the active bank rather than silently switching away from
            // it: which bank is active changes what every prompt is built from,
            // and that should never be a side effect of deleting something else.
            var isActive = string.Equals(
                SettingsService.Current?.AiSettings?.ActiveMemoryBankName, bank.BankName,
                StringComparison.OrdinalIgnoreCase);

            if (isActive)
            {
                MessageBox.Show(this,
                    "'" + bank.BankName + "' is the active memory bank.\n\n"
                    + "Switch to another bank first, then delete this one.",
                    "Delete memory bank", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var answer = MessageBox.Show(this,
                "Remove the memory bank '" + bank.BankName + "'?\n\n"
                + "Everything in it goes with it: the brief, the terminology, the style "
                + "rules and the reference folder.\n\n"
                + "It is moved to a .trash folder inside memory-banks rather than deleted, "
                + "so you can put it back by renaming that folder.",
                "Delete memory bank",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes) return;

            string movedTo, error;
            bool removed;
            AiAssistantViewPart.ReleaseMemoryBankHandles();
            try
            {
                removed = UserDataPath.TryDeleteMemoryBank(bank.BankName, out movedTo, out error);
            }
            finally
            {
                AiAssistantViewPart.ReacquireMemoryBankHandles();
            }

            if (!removed)
            {
                MessageBox.Show(this, error, "Delete memory bank",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            RefreshTree();

            MessageBox.Show(this,
                "Moved to:\n\n" + movedTo + "\n\nRename that folder back into memory-banks to restore it.",
                "Memory bank removed", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OpenSelectedBankFolder()
        {
            var bank = GetSelectedBank();
            if (bank == null) return;
            try
            {
                var dir = UserDataPath.GetMemoryBankDir(bank.BankName);
                if (Directory.Exists(dir))
                    System.Diagnostics.Process.Start("explorer.exe", dir);
            }
            catch { }
        }

        /// <summary>Small one-line prompt. Bank names are filesystem
        /// identifiers, so the caller sanitises whatever comes back.</summary>
        private string PromptForBankName(string title, string label, string initial)
        {
            using (var dlg = new Form())
            {
                dlg.Icon = Core.IconHelper.AppIcon;
                dlg.Text = title;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.ClientSize = new Size(420, 132);
                dlg.BackColor = Color.White;

                dlg.Controls.Add(new Label
                {
                    Text = label,
                    Location = new Point(14, 14),
                    AutoSize = true,
                    ForeColor = Color.FromArgb(60, 60, 60)
                });

                var txt = new TextBox
                {
                    Text = initial ?? "",
                    Location = new Point(14, 40),
                    Width = dlg.ClientSize.Width - 28,
                    Font = new Font("Segoe UI", 9.5f)
                };
                dlg.Controls.Add(txt);

                var hint = new Label
                {
                    Text = "Lowercase letters, digits, hyphens and underscores; spaces become hyphens.",
                    Location = new Point(14, 68),
                    AutoSize = false,
                    Width = dlg.ClientSize.Width - 28,
                    Height = 16,
                    Font = new Font("Segoe UI", 7.5f),
                    ForeColor = Color.FromArgb(140, 140, 140)
                };
                dlg.Controls.Add(hint);

                var ok = new Button
                {
                    Text = "OK", DialogResult = DialogResult.OK, FlatStyle = FlatStyle.System,
                    Width = 85, Height = 26,
                    Location = new Point(dlg.ClientSize.Width - 184, 94)
                };
                var cancel = new Button
                {
                    Text = "Cancel", DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.System,
                    Width = 85, Height = 26,
                    Location = new Point(dlg.ClientSize.Width - 94, 94)
                };
                dlg.Controls.Add(ok);
                dlg.Controls.Add(cancel);
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;

                txt.SelectAll();
                return dlg.ShowDialog(this) == DialogResult.OK ? txt.Text : null;
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  SHORTCUT MANAGEMENT
        // ═══════════════════════════════════════════════════════════

        private void OnShortcutComboChanged(object sender, EventArgs e)
        {
            var prompt = _cboShortcut.Tag as PromptTemplate;
            if (prompt == null) return;

            var newVal = _cboShortcut.SelectedItem?.ToString() ?? "";

            // Clear any previous assignment for this prompt
            _shortcutAssignments.Remove(prompt.FilePath);

            if (!string.IsNullOrEmpty(newVal))
            {
                // Enforce uniqueness: clear this shortcut from any other prompt
                string keyToRemove = null;
                foreach (var kvp in _shortcutAssignments)
                {
                    if (kvp.Value == newVal)
                    {
                        keyToRemove = kvp.Key;
                        break;
                    }
                }
                if (keyToRemove != null)
                    _shortcutAssignments.Remove(keyToRemove);

                _shortcutAssignments[prompt.FilePath] = newVal;
            }

            // Refresh tree to update shortcut suffixes in node text
            RefreshTree();
        }

        private void AssignShortcutToSelected(int slot, string display)
        {
            var prompt = GetSelectedPrompt();
            if (prompt == null || !prompt.IsQuickLauncher) return;

            // Clear previous assignment for this prompt
            _shortcutAssignments.Remove(prompt.FilePath);

            // If the prompt already has this exact shortcut, toggle it off
            // Otherwise assign it (clearing from any other prompt)
            string keyToRemove = null;
            foreach (var kvp in _shortcutAssignments)
            {
                if (kvp.Value == display)
                {
                    keyToRemove = kvp.Key;
                    break;
                }
            }
            if (keyToRemove != null)
                _shortcutAssignments.Remove(keyToRemove);

            _shortcutAssignments[prompt.FilePath] = display;

            RefreshTree();
        }

        private void SetActivePromptFromTree()
        {
            var prompt = GetSelectedPrompt();
            if (prompt == null) return;

            // Toggle: if already active, clear it; otherwise set it
            if (!string.IsNullOrEmpty(_activePromptPath)
                && PromptPaths.Match(prompt.RelativePath, _activePromptPath) /* marker-tolerant, #100 */)
            {
                _activePromptPath = "";
            }
            else
            {
                _activePromptPath = prompt.RelativePath;
            }

            RefreshTree();
            ActivePromptChanged?.Invoke(this, _activePromptPath ?? "");
            ActivePromptChangedGlobal?.Invoke(this, _activePromptPath ?? "");
        }

        // ═══════════════════════════════════════════════════════════
        //  HELPERS
        // ═══════════════════════════════════════════════════════════

        private static string PromptInputBox(string title, string label)
        {
            using (var form = new Form())
            {
                form.Text = title;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.StartPosition = FormStartPosition.CenterParent;
                form.ClientSize = new Size(320, 100);
                form.BackColor = Color.White;

                var lbl = new Label
                {
                    Text = label,
                    Location = new Point(12, 12),
                    AutoSize = true,
                    Font = new Font("Segoe UI", 9f)
                };

                var txt = new TextBox
                {
                    Location = new Point(12, 34),
                    Width = 292,
                    Font = new Font("Segoe UI", 9f)
                };

                var btnOK = new Button
                {
                    Text = "OK",
                    DialogResult = DialogResult.OK,
                    Location = new Point(148, 66),
                    Width = 75,
                    FlatStyle = FlatStyle.System,
                    Font = new Font("Segoe UI", 8.5f)
                };

                var btnCancel = new Button
                {
                    Text = "Cancel",
                    DialogResult = DialogResult.Cancel,
                    Location = new Point(229, 66),
                    Width = 75,
                    FlatStyle = FlatStyle.System,
                    Font = new Font("Segoe UI", 8.5f)
                };

                form.AcceptButton = btnOK;
                form.CancelButton = btnCancel;
                form.Controls.AddRange(new Control[] { lbl, txt, btnOK, btnCancel });

                if (form.ShowDialog() == DialogResult.OK)
                    return txt.Text.Trim();
                return null;
            }
        }
    }
}
