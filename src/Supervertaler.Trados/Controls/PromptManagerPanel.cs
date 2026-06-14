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
        // ─── Left panel controls ─────────────────────────────────
        private Panel _leftPanel;
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
        private string _activePromptPath; // per-project active prompt relative path

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

            _leftPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White
            };

            _rightPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White
            };

            BuildLeftPanel();
            BuildRightPanels();

            var splitter = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                BackColor = Color.FromArgb(220, 220, 220),
                SplitterWidth = 5,
                FixedPanel = FixedPanel.None,
                BorderStyle = BorderStyle.None
            };
            splitter.Panel1.Controls.Add(_leftPanel);
            splitter.Panel2.Controls.Add(_rightPanel);
            Controls.Add(splitter);

            // Set initial splitter position after layout is ready
            splitter.SplitterDistance = 100; // temporary; updated on first resize
            var initialised = false;
            Resize += (s, e) =>
            {
                if (!initialised && Width > 100)
                {
                    splitter.SplitterDistance = (int)(Width * 0.55);
                    initialised = true;
                }
            };

            ResumeLayout(false);
        }

        private void BuildLeftPanel()
        {
            var labelColor = Color.FromArgb(80, 80, 80);

            // ─── Toolbar ─────────────────────────────────────────
            var toolbar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 46,
                BackColor = Color.White
            };

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

            _btnMoveUp = CreateToolbarButton("\u25B2", 28);
            _btnMoveUp.Click += OnMoveUp;
            _btnMoveUp.Font = new Font("Segoe UI", 7f);

            _btnMoveDown = CreateToolbarButton("\u25BC", 28);
            _btnMoveDown.Click += OnMoveDown;
            _btnMoveDown.Font = new Font("Segoe UI", 7f);

            toolbar.Controls.AddRange(new Control[]
            {
                _btnNew, _btnEdit, _btnDelete, _btnRestore, _btnMoveUp, _btnMoveDown, _btnNewFolder, _btnRefresh
            });

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
            _tvPrompts = new TreeView
            {
                Dock = DockStyle.Fill,
                HideSelection = false,
                ShowLines = true,
                FullRowSelect = true,
                Font = new Font("Segoe UI", 8.5f),
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(250, 250, 250),
                AllowDrop = true
            };
            _tvPrompts.AfterSelect += OnTreeAfterSelect;
            _tvPrompts.NodeMouseDoubleClick += OnTreeNodeDoubleClick;
            _tvPrompts.NodeMouseClick += OnTreeNodeMouseClick;
            _tvPrompts.ItemDrag += OnTreeItemDrag;
            _tvPrompts.DragEnter += OnTreeDragEnter;
            _tvPrompts.DragOver += OnTreeDragOver;
            _tvPrompts.DragDrop += OnTreeDragDrop;

            BuildTreeContextMenu();

            var treePanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10, 0, 4, 0),
                BackColor = Color.White
            };
            treePanel.Controls.Add(_tvPrompts);

            // ─── Bottom link ─────────────────────────────────────
            var folderPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 38,
                BackColor = Color.White
            };
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

            // Add in reverse order for correct Dock layout
            _leftPanel.Controls.Add(treePanel);    // Fill
            _leftPanel.Controls.Add(folderPanel);  // Bottom
            _leftPanel.Controls.Add(toolbar);       // Top
        }

        private Button CreateToolbarButton(string text, int width)
        {
            // AutoSize so buttons grow to fit their text at any DPI / font
            // size – the previous fixed-width pattern clipped "New", "Restore"
            // and "Refresh" labels at 150% Windows scaling. The `width` arg is
            // kept as a minimum so narrow labels still get a comfortable
            // click target rather than collapsing to text-width-only.
            var btn = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(width, 25),
                Padding = new Padding(8, 0, 8, 0),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(80, 80, 80),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 220, 220);
            return btn;
        }

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

            _treeContextMenu.Opening += (s, ev) =>
            {
                var node = _tvPrompts.SelectedNode;
                if (node == null) { ev.Cancel = true; return; }

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
                        && string.Equals(prompt.RelativePath, _activePromptPath, StringComparison.OrdinalIgnoreCase);
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
            BuildPromptDetailPanel();
            BuildFolderInfoPanel();

            _rightPanel.Controls.Add(_panelSystemPrompt);
            _rightPanel.Controls.Add(_panelPromptDetail);
            _rightPanel.Controls.Add(_panelFolderInfo);

            // Hide all initially
            _panelSystemPrompt.Visible = false;
            _panelPromptDetail.Visible = false;
            _panelFolderInfo.Visible = false;
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
                    NodeFont = new Font(_tvPrompts.Font, FontStyle.Bold),
                    ForeColor = Color.FromArgb(30, 30, 30)
                };
                // Pad text to work around WinForms bold node width calculation bug
                sysNode.Text += "  ";
                _tvPrompts.Nodes.Add(sysNode);

                // 2) Build folder structure
                var root = _library.GetFolderStructure();
                AddFolderChildren(root, _tvPrompts.Nodes);

                // Expand all by default, or restore previous state
                if (expandedPaths.Count == 0)
                {
                    _tvPrompts.ExpandAll();
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
                    && string.Equals(prompt.RelativePath, _activePromptPath, StringComparison.OrdinalIgnoreCase);
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
            return null;
        }

        // ═══════════════════════════════════════════════════════════
        //  TREE SELECTION – swap right panels
        // ═══════════════════════════════════════════════════════════

        private void OnTreeAfterSelect(object sender, TreeViewEventArgs e)
        {
            _panelSystemPrompt.Visible = false;
            _panelPromptDetail.Visible = false;
            _panelFolderInfo.Visible = false;

            if (e.Node == null) return;

            if (e.Node.Tag is string tagStr && tagStr == SystemPromptTag)
            {
                // System prompt selected
                UpdateSystemPromptDisplay();
                _panelSystemPrompt.Visible = true;
            }
            else if (e.Node.Tag is PromptTemplate prompt)
            {
                // Prompt selected – show detail
                ShowPromptDetail(prompt);
                _panelPromptDetail.Visible = true;
            }
            else if (e.Node.Tag is string folderPath)
            {
                // Folder selected – show folder info
                ShowFolderInfo(e.Node, folderPath);
                _panelFolderInfo.Visible = true;
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
                $"name: \"{Core.PromptLibrary.EscapeYaml(cloneName)}\"", 1);

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
                    (e.Node.Tag is string tag && tag != SystemPromptTag))
                {
                    _treeContextMenu.Show(_tvPrompts, e.Location);
                }
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
                && string.Equals(prompt.RelativePath, _activePromptPath, StringComparison.OrdinalIgnoreCase))
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
