using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi.Interfaces;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// Main UI control for the Supervertaler Assistant dockable ViewPart.
    /// Hosts a TabControl with two tabs:
    ///   - Chat: conversational AI interface with context strip, message area, input, image attachments
    ///   - Batch Translate: BatchTranslateControl for bulk AI translation
    /// Gear and help buttons float at the top-right, visible on all tabs.
    /// </summary>
    public class AiAssistantControl : UserControl, IUIControl
    {
        // Tab control + floating buttons
        private readonly TabControl _tabControl;
        private readonly Button _btnSettings;
        private readonly Button _btnHelp;

        // Chat tab controls (assigned in BuildChatTab, called from constructor)
        private Panel _contextStrip;
        private Label _lblContext;
        private Panel _chatPanel;
        private FlowLayoutPanel _messageFlow;
        private Panel _inputPanel;
        private TextBox _txtInput;
        private Button _btnSend;
        private Button _btnStop;
        private Button _btnClear;
        private Button _btnAttach;
        private LinkLabel _lblStatus;
        private string _currentProvider;
        private string _currentModel;
        private Label _lblThinking;
        private Control _thinkingBubble;
        private Timer _thinkingTimer;
        private int _thinkingTicks;
        private FlowLayoutPanel _attachmentStrip;

        // Batch Translate tab
        private readonly BatchTranslateControl _batchTranslateControl;

        // Reports tab
        private ReportsControl _reportsControl;

        // SuperMemory toolbar (inside Chat tab)
        private SuperMemoryToolbar _superMemoryToolbar;

        private bool _isThinking;

        // Pending attachments for the next message
        private readonly List<ImageAttachment> _pendingImages = new List<ImageAttachment>();
        private readonly List<DocumentAttachment> _pendingDocuments = new List<DocumentAttachment>();

        // Optional display-only override for the next programmatically submitted message.
        // When set, the chat bubble shows this text instead of the full prompt content.
        // Used by SubmitMessage(text, displayText) for {{PROJECT}} prompts.
        private string _pendingDisplayText;

        // Optional max token override for the next API call.
        // Used by prompt generation which needs more output tokens than regular chat.
        private int? _pendingMaxTokens;

        // When true, the next message displays as an assistant-styled (gray) bubble
        // instead of a user-styled (blue) bubble. Used for system-initiated messages.
        private bool _pendingShowAsStatus;
        private string _pendingPromptName;

        private const int MaxImages = 5;
        private const int MaxDocuments = 5;
        private const int MaxImageBytes = 10 * 1024 * 1024; // 10 MB

        // Input panel resize handle
        private Panel _resizeHandle;
        private bool _resizeDragging;
        private int _resizeDragStartY;
        private int _resizeDragStartHeight;
        private const int InputPanelMinHeight = 90;
        private const int InputPanelMaxHeight = 400;

        /// <summary>Raised when the user presses Enter or clicks Send.</summary>
        public event EventHandler<ChatSendEventArgs> SendRequested;

        /// <summary>Raised when the user clicks Clear.</summary>
        public event EventHandler ClearRequested;

        /// <summary>Raised when the user clicks "Apply to target" on a message.</summary>
        public event EventHandler<string> ApplyToTargetRequested;

        /// <summary>Raised when the user clicks "Save as Prompt" on a message.</summary>
        public event EventHandler<string> SaveAsPromptRequested;

        /// <summary>Raised when the user clicks Stop during a chat request.</summary>
        public event EventHandler StopRequested;

        /// <summary>Raised when the user clicks the gear/settings button.</summary>
        public event EventHandler SettingsRequested;

        /// <summary>Raised when the user clicks "Process Inbox" in the SuperMemory toolbar.</summary>
        public event EventHandler ProcessInboxRequested;

        /// <summary>Raised when the user clicks "Health Check" in the SuperMemory toolbar.</summary>
        public event EventHandler HealthCheckRequested;

        /// <summary>Raised when the user clicks "Distill" in the SuperMemory toolbar.</summary>
        public event EventHandler DistillRequested;

        /// <summary>Raised when the user clicks the refresh button in the SuperMemory toolbar.</summary>
        public event EventHandler SuperMemoryRefreshRequested;

        /// <summary>
        /// Raised when the user picks a different memory bank from the dropdown
        /// in the SuperMemory toolbar. The ViewPart handler is responsible for
        /// persisting the new active bank to settings and invalidating any
        /// cached readers.
        /// </summary>
        public event EventHandler<MemoryBankChangedEventArgs> MemoryBankChanged;

        /// <summary>
        /// Raised when the user picks the "+ New memory bank…" sentinel entry
        /// in the SuperMemory toolbar. The ViewPart handler is responsible for
        /// prompting the user for a name, creating the bank on disk, and then
        /// refreshing the dropdown with the new bank selected.
        /// </summary>
        public event EventHandler NewMemoryBankRequested;

        /// <summary>
        /// Fired when the user changes chat font size via the A+/A- buttons.
        /// The ViewPart should persist the new size.
        /// </summary>
        public event EventHandler ChatFontSizeChanged;

        private float _chatFontSize = 9f;

        /// <summary>Exposes the BatchTranslateControl for event wiring by the ViewPart.</summary>
        public BatchTranslateControl BatchTranslateControl => _batchTranslateControl;

        /// <summary>Exposes the ReportsControl for event wiring by the ViewPart.</summary>
        public ReportsControl ReportsControl => _reportsControl;

        /// <summary>Exposes the SuperMemory toolbar for event wiring by the ViewPart.</summary>
        public SuperMemoryToolbar SuperMemoryToolbar => _superMemoryToolbar;

        /// <summary>Updates the SuperMemory inbox count display.</summary>
        public void UpdateInboxCount(int count) => _superMemoryToolbar?.UpdateInboxCount(count);

        /// <summary>Enables or disables the SuperMemory toolbar buttons.</summary>
        public void SetSuperMemoryBusy(bool busy) => _superMemoryToolbar?.SetBusy(busy);

        public AiAssistantControl()
        {
            SuspendLayout();
            BackColor = Color.White;

            // ─── Tab control (fills entire panel) ────────────────────
            _tabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", UiScale.FontSize(8.5f)),
                Padding = new Point(UiScale.Pixels(6), UiScale.Pixels(3)),
            };

            // === Chat tab ===
            var chatPage = new TabPage("Chat") { BackColor = Color.White };
            BuildChatTab(chatPage);
            _tabControl.TabPages.Add(chatPage);

            // === Batch Translate tab ===
            var batchPage = new TabPage("Batch Operations") { BackColor = Color.White };
            _batchTranslateControl = new BatchTranslateControl
            {
                Dock = DockStyle.Fill
            };
            batchPage.Controls.Add(_batchTranslateControl);
            _tabControl.TabPages.Add(batchPage);

            // === Reports tab ===
            var reportsPage = new TabPage("Reports") { BackColor = Color.White };
            _reportsControl = new ReportsControl { Dock = DockStyle.Fill };
            reportsPage.Controls.Add(_reportsControl);
            _tabControl.TabPages.Add(reportsPage);

            Controls.Add(_tabControl);

            // ─── Settings gear button — floats at top-right ──────────
            _btnSettings = new Button
            {
                Text = "\uE713",  // Settings gear from Segoe MDL2 Assets
                Size = new Size(UiScale.Pixels(26), UiScale.Pixels(22)),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe MDL2 Assets", UiScale.FontSize(8f)),
                ForeColor = Color.FromArgb(100, 100, 100),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                TabStop = false,
                UseCompatibleTextRendering = true,
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = Padding.Empty,
                Margin = Padding.Empty
            };
            _btnSettings.FlatAppearance.BorderSize = 0;
            _btnSettings.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 220, 220);
            _btnSettings.Click += (s, e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

            // Help/about button (?) — floats to the left of the gear button
            _btnHelp = new Button
            {
                Text = "?",
                Size = new Size(UiScale.Pixels(26), UiScale.Pixels(22)),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", UiScale.FontSize(8f), FontStyle.Bold),
                ForeColor = Color.FromArgb(100, 100, 100),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                TabStop = false,
                UseCompatibleTextRendering = true,
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(0, UiScale.Pixels(1), 0, 0),
                Margin = Padding.Empty
            };
            _btnHelp.FlatAppearance.BorderSize = 0;
            _btnHelp.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 220, 220);
            _btnHelp.Click += OnHelpDropdown;

            Controls.Add(_btnSettings);
            Controls.Add(_btnHelp);
            _btnSettings.BringToFront(); // render on top of the tab control
            _btnHelp.BringToFront();

            ResumeLayout(false);

            // Position buttons at top-right, and keep them there on resize
            Resize += (s, e) => PositionTopButtons();
            PositionTopButtons();
        }

        /// <summary>
        /// Builds the Chat tab contents inside the given TabPage.
        /// Layout: context strip (top) → input panel (bottom) → thinking indicator → chat area (fill)
        /// </summary>
        private void BuildChatTab(TabPage page)
        {
            // ─── Context strip (top) ──────────────────────────────
            _contextStrip = new Panel
            {
                Dock = DockStyle.Top,
                Height = UiScale.Pixels(28),
                BackColor = Color.FromArgb(248, 248, 248),
                Padding = new Padding(UiScale.Pixels(8), 0, UiScale.Pixels(60), 0)
            };

            _lblContext = new Label
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", UiScale.FontSize(7.5f)),
                // Material Design Green 800 — distinctive on the light grey
                // strip without being garish. Makes the language pair and
                // current source segment visually stand out as the most
                // important "where am I right now" hint in the panel.
                ForeColor = Color.FromArgb(46, 125, 50),
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "No document open",
                AutoEllipsis = true
            };
            _contextStrip.Controls.Add(_lblContext);

            // Font size increase button (A+) — docked right inside context strip
            var btnChatFontUp = new Button
            {
                Text = "A+",
                Dock = DockStyle.Right,
                Width = UiScale.Pixels(28),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", UiScale.FontSize(9f), FontStyle.Bold),
                ForeColor = Color.FromArgb(100, 100, 100),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                TabStop = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = Padding.Empty,
                Margin = Padding.Empty
            };
            btnChatFontUp.FlatAppearance.BorderSize = 0;
            btnChatFontUp.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 220, 220);
            btnChatFontUp.Click += OnChatFontIncrease;
            _contextStrip.Controls.Add(btnChatFontUp);

            // Font size decrease button (A−)
            var btnChatFontDown = new Button
            {
                Text = "A\u2212", // A followed by minus sign (−)
                Dock = DockStyle.Right,
                Width = UiScale.Pixels(28),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", UiScale.FontSize(7f), FontStyle.Bold),
                ForeColor = Color.FromArgb(100, 100, 100),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                TabStop = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = Padding.Empty,
                Margin = Padding.Empty
            };
            btnChatFontDown.FlatAppearance.BorderSize = 0;
            btnChatFontDown.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 220, 220);
            btnChatFontDown.Click += OnChatFontDecrease;
            _contextStrip.Controls.Add(btnChatFontDown);

            // Thin separator line below context
            var contextSep = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 1,
                BackColor = Color.FromArgb(220, 220, 220)
            };
            _contextStrip.Controls.Add(contextSep);

            // ─── SuperMemory toolbar (below context strip) ─────────
            _superMemoryToolbar = new SuperMemoryToolbar();
            _superMemoryToolbar.ProcessInboxRequested += (s, e) =>
                ProcessInboxRequested?.Invoke(this, EventArgs.Empty);
            _superMemoryToolbar.HealthCheckRequested += (s, e) =>
                HealthCheckRequested?.Invoke(this, EventArgs.Empty);
            _superMemoryToolbar.DistillRequested += (s, e) =>
                DistillRequested?.Invoke(this, EventArgs.Empty);
            _superMemoryToolbar.RefreshRequested += (s, e) =>
                SuperMemoryRefreshRequested?.Invoke(this, EventArgs.Empty);
            _superMemoryToolbar.MemoryBankChanged += (s, e) =>
                MemoryBankChanged?.Invoke(this, e);
            _superMemoryToolbar.NewMemoryBankRequested += (s, e) =>
                NewMemoryBankRequested?.Invoke(this, EventArgs.Empty);

            // ─── Input panel (bottom) ─────────────────────────────
            _inputPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = UiScale.Pixels(90),
                Padding = new Padding(UiScale.Pixels(8), UiScale.Pixels(4), UiScale.Pixels(8), UiScale.Pixels(4)),
                BackColor = Color.FromArgb(250, 250, 250)
            };

            // Drag handle at top of input panel — allows vertical resizing
            _resizeHandle = new Panel
            {
                Dock = DockStyle.Top,
                Height = 5,
                Cursor = Cursors.SizeNS,
                BackColor = Color.FromArgb(235, 235, 235)
            };

            // Visual grip: thin line centred in the handle
            var gripLine = new Panel
            {
                Height = 1,
                BackColor = Color.FromArgb(190, 190, 190),
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
            };
            _resizeHandle.Controls.Add(gripLine);
            _resizeHandle.Layout += (s, e) =>
            {
                gripLine.Location = new Point((_resizeHandle.Width / 2) - 30, 2);
                gripLine.Width = 60;
            };

            _resizeHandle.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                _resizeDragging = true;
                _resizeDragStartY = _resizeHandle.PointToScreen(e.Location).Y;
                _resizeDragStartHeight = _inputPanel.Height;
                _resizeHandle.Capture = true;
            };
            _resizeHandle.MouseMove += (s, e) =>
            {
                if (!_resizeDragging) return;
                var currentY = _resizeHandle.PointToScreen(e.Location).Y;
                var delta = _resizeDragStartY - currentY; // positive = dragging up = taller
                var newHeight = Math.Max(InputPanelMinHeight,
                    Math.Min(InputPanelMaxHeight, _resizeDragStartHeight + delta));
                _inputPanel.Height = newHeight;
                LayoutInputPanel();
            };
            _resizeHandle.MouseUp += (s, e) =>
            {
                _resizeDragging = false;
                _resizeHandle.Capture = false;
            };

            _inputPanel.Controls.Add(_resizeHandle);

            _btnSend = new Button
            {
                Text = "Send",
                Size = new Size(UiScale.Pixels(60), UiScale.Pixels(26)),
                Font = new Font("Segoe UI", UiScale.FontSize(8f)),
                FlatStyle = FlatStyle.Flat,
                BackColor = ColorTranslator.FromHtml("#D6EBFF"),
                ForeColor = Color.FromArgb(30, 30, 30),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                TabIndex = 1
            };
            _btnSend.FlatAppearance.BorderColor = Color.FromArgb(180, 200, 220);
            _btnSend.Click += (s, e) => DoSend();

            _btnStop = new Button
            {
                Text = "Stop",
                Size = new Size(UiScale.Pixels(48), UiScale.Pixels(26)),
                Font = new Font("Segoe UI", UiScale.FontSize(8f)),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(255, 230, 230),
                ForeColor = Color.FromArgb(30, 30, 30),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                Visible = false,
                TabIndex = 2
            };
            _btnStop.FlatAppearance.BorderColor = Color.FromArgb(220, 180, 180);
            _btnStop.Click += (s, e) => StopRequested?.Invoke(this, EventArgs.Empty);

            _btnClear = new Button
            {
                Text = "Clear",
                Size = new Size(UiScale.Pixels(48), UiScale.Pixels(26)),
                Font = new Font("Segoe UI", UiScale.FontSize(8f)),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = Color.FromArgb(120, 120, 120),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                TabIndex = 3
            };
            _btnClear.FlatAppearance.BorderSize = 0;
            _btnClear.FlatAppearance.MouseOverBackColor = Color.FromArgb(230, 230, 230);
            _btnClear.Click += (s, e) => ClearRequested?.Invoke(this, EventArgs.Empty);

            // Attach button for browsing files (images + documents)
            _btnAttach = new Button
            {
                Text = "\uE723",  // Attach / paperclip icon in Segoe MDL2 Assets
                Size = new Size(UiScale.Pixels(28), UiScale.Pixels(26)),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe MDL2 Assets", UiScale.FontSize(9f)),
                ForeColor = Color.FromArgb(100, 100, 100),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                TabIndex = 4,
                UseCompatibleTextRendering = true,
                TextAlign = ContentAlignment.MiddleCenter
            };
            _btnAttach.FlatAppearance.BorderSize = 0;
            _btnAttach.FlatAppearance.MouseOverBackColor = Color.FromArgb(230, 230, 230);
            _btnAttach.Click += OnAttachClick;

            _lblStatus = new LinkLabel
            {
                Font = new Font("Segoe UI", UiScale.FontSize(7f)),
                LinkColor = Color.FromArgb(140, 140, 140),
                ActiveLinkColor = Color.FromArgb(100, 100, 100),
                VisitedLinkColor = Color.FromArgb(140, 140, 140),
                LinkBehavior = LinkBehavior.HoverUnderline,
                Text = "",
                AutoSize = true,
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left
            };
            _lblStatus.LinkClicked += OnModelSelectorClicked;
            var modelTip = new ToolTip { AutoPopDelay = 5000, InitialDelay = 400 };
            modelTip.SetToolTip(_lblStatus, "Click to change model");

            _txtInput = new ChatInputTextBox
            {
                Multiline = true,
                AcceptsReturn = true,   // allow Enter keys as input (we handle send vs newline in KeyDown)
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Segoe UI", UiScale.FontSize(9f)),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                AllowDrop = true,
                TabIndex = 0
            };
            _txtInput.KeyDown += OnInputKeyDown;
            _txtInput.DragEnter += OnDragEnter;
            _txtInput.DragDrop += OnDragDrop;

            // ─── Attachment thumbnail strip ──────────────────────
            _attachmentStrip = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowOnly,
                MinimumSize = new Size(0, 0),
                MaximumSize = new Size(0, 60),  // max one row of thumbnails
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.FromArgb(250, 250, 250),
                Padding = new Padding(0),
                Margin = new Padding(0),
                Visible = false,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };

            _inputPanel.Controls.Add(_txtInput);
            _inputPanel.Controls.Add(_attachmentStrip);
            _inputPanel.Controls.Add(_btnSend);
            _inputPanel.Controls.Add(_btnStop);
            _inputPanel.Controls.Add(_btnClear);
            _inputPanel.Controls.Add(_btnAttach);
            _inputPanel.Controls.Add(_lblStatus);

            // Tooltips for input buttons
            var inputTips = new ToolTip { AutoPopDelay = 5000, InitialDelay = 400 };
            inputTips.SetToolTip(_btnSend, "Send message (Enter)");
            inputTips.SetToolTip(_btnStop, "Stop AI response");
            inputTips.SetToolTip(_btnClear, "Clear conversation history");
            inputTips.SetToolTip(_btnAttach,
                "Attach files — images, documents, spreadsheets, and translation files\n" +
                "(Ctrl+V to paste images, or drag and drop)\n\n" +
                "Supported: docx, pdf, rtf, pptx, xlsx, csv, tsv,\n" +
                "tmx, sdlxliff, xliff, tbx, txt, md, html, json, xml,\n" +
                "png, jpg, gif, webp, bmp");
            inputTips.SetToolTip(_txtInput, "Type your message. Shift+Enter for new line.");
            inputTips.SetToolTip(_resizeHandle, "Drag to resize input area");
            inputTips.SetToolTip(_lblStatus, "Click to change model");

            // ─── Thinking indicator ───────────────────────────────
            _lblThinking = new Label
            {
                Dock = DockStyle.Bottom,
                Height = UiScale.Pixels(24),
                Font = new Font("Segoe UI", UiScale.FontSize(8f), FontStyle.Italic),
                ForeColor = Color.FromArgb(100, 100, 100),
                Text = "  Thinking\u2026",
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.FromArgb(252, 252, 252),
                Visible = false
            };

            // ─── Chat message area (fill) ─────────────────────────
            _chatPanel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.White,
                Padding = new Padding(0, 4, 0, 4)
            };

            _messageFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowOnly,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                BackColor = Color.White,
                Padding = new Padding(0)
            };
            _chatPanel.Controls.Add(_messageFlow);

            // Add controls to the tab page in correct dock order
            // (bottom first, then top, then fill)
            page.Controls.Add(_chatPanel);
            page.Controls.Add(_lblThinking);
            page.Controls.Add(_inputPanel);
            page.Controls.Add(_superMemoryToolbar);
            page.Controls.Add(_contextStrip);

            // Layout input controls
            _inputPanel.Resize += (s, e) => LayoutInputPanel();
            _chatPanel.Resize += (s, e) => RelayoutBubbles();

            // Initial layout when tab loads
            page.Layout += (s, e) => LayoutInputPanel();
        }

        /// <summary>
        /// Sets the chat bubble font size. Called by the ViewPart on startup
        /// to restore the persisted size.
        /// </summary>
        public void SetChatFontSize(float sizeInPoints)
        {
            _chatFontSize = Math.Max(7f, Math.Min(16f, sizeInPoints));
        }

        /// <summary>Current chat font size in points.</summary>
        public float ChatFontSize => _chatFontSize;

        private void OnChatFontIncrease(object sender, EventArgs e)
        {
            _chatFontSize = Math.Min(_chatFontSize + 0.5f, 16f);
            ChatFontSizeChanged?.Invoke(this, EventArgs.Empty);
            RebuildBubblesWithNewFont();
        }

        private void OnChatFontDecrease(object sender, EventArgs e)
        {
            _chatFontSize = Math.Max(_chatFontSize - 0.5f, 7f);
            ChatFontSizeChanged?.Invoke(this, EventArgs.Empty);
            RebuildBubblesWithNewFont();
        }

        private void RebuildBubblesWithNewFont()
        {
            if (_chatPanel == null || _messageFlow == null) return;
            var w = _chatPanel.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2;
            _messageFlow.SuspendLayout();
            foreach (Control ctrl in _messageFlow.Controls)
            {
                var bubble = ctrl as ChatBubble;
                bubble?.UpdateFontSize(_chatFontSize, w);
            }
            _messageFlow.ResumeLayout(true);
        }

        private void PositionTopButtons()
        {
            if (_btnHelp == null || _btnSettings == null) return;
            // Help "?" at far right, gear to its left
            _btnHelp.Location = new Point(Width - _btnHelp.Width - 2, 1);
            _btnSettings.Location = new Point(_btnHelp.Left - _btnSettings.Width, 1);
        }

        private void OnHelpDropdown(object sender, EventArgs e)
        {
            string topic;
            string label;
            switch (_tabControl.SelectedIndex)
            {
                case 1:
                    if (_batchTranslateControl.IsClipboardMode)
                    {
                        topic = HelpSystem.Topics.ClipboardMode;
                        label = "Clipboard Mode Help";
                    }
                    else if (_batchTranslateControl.CurrentMode == BatchMode.Proofread)
                    {
                        topic = HelpSystem.Topics.AiProofreader;
                        label = "AI Proofreader Help";
                    }
                    else
                    {
                        topic = HelpSystem.Topics.BatchTranslate;
                        label = "Batch Translate Help";
                    }
                    break;
                case 2:
                    topic = HelpSystem.Topics.AiProofreaderReports;
                    label = "Reports Help";
                    break;
                default:
                    topic = HelpSystem.Topics.AiAssistantChat;
                    label = "Supervertaler Assistant Help";
                    break;
            }

            var menu = new ContextMenuStrip();
            menu.Items.Add(label, null, (s, ev) =>
                HelpSystem.OpenHelp(topic));
            // Add SuperMemory help on Chat tab (where the toolbar lives)
            if (_tabControl.SelectedIndex == 0)
            {
                menu.Items.Add("SuperMemory Help", null, (s, ev) =>
                    HelpSystem.OpenHelp(HelpSystem.Topics.SuperMemory));
            }
            menu.Items.Add("-");  // separator
            menu.Items.Add("About Supervertaler for Trados", null, (s, ev) =>
            {
                using (var dlg = new AboutDialog())
                    dlg.ShowDialog(FindForm());
            });
            menu.Show(_btnHelp, new Point(0, _btnHelp.Height));
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F1)
            {
                string topic;
                switch (_tabControl.SelectedIndex)
                {
                    case 1: topic = _batchTranslateControl.IsClipboardMode
                                ? HelpSystem.Topics.ClipboardMode
                                : _batchTranslateControl.CurrentMode == BatchMode.Proofread
                                    ? HelpSystem.Topics.AiProofreader
                                    : HelpSystem.Topics.BatchTranslate; break;
                    case 2: topic = HelpSystem.Topics.AiProofreaderReports; break;
                    default: topic = HelpSystem.Topics.AiAssistantChat; break;
                }
                HelpSystem.OpenHelp(topic);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void LayoutInputPanel()
        {
            var p = _inputPanel;
            if (p == null || p.Width < 50) return;

            var y2 = p.Height - 30; // bottom row for buttons
            var attachHeight = _attachmentStrip.Visible ? _attachmentStrip.Height + 2 : 0;
            var y1 = 6; // top of text input

            // Text input fills from top to above the attachment strip + button row
            _txtInput.Location = new Point(8, y1);
            _txtInput.Size = new Size(p.Width - 16, y2 - y1 - 4 - attachHeight);

            // Attachment strip sits between text input and button row
            if (_attachmentStrip.Visible)
            {
                _attachmentStrip.Location = new Point(8, _txtInput.Bottom + 2);
                _attachmentStrip.MaximumSize = new Size(p.Width - 16, 60);
                _attachmentStrip.Width = p.Width - 16;
            }

            _btnSend.Location = new Point(p.Width - _btnSend.Width - 8, y2);
            _btnStop.Location = new Point(_btnSend.Left - _btnStop.Width - 4, y2);
            _btnClear.Location = new Point(8, y2);
            _btnAttach.Location = new Point(_btnClear.Right + 2, y2);
            _lblStatus.Location = new Point(_btnAttach.Right + 8, y2 + 5);
        }

        private void RelayoutBubbles()
        {
            if (_chatPanel == null || _chatPanel.Width < 50) return;
            var w = _chatPanel.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2;
            foreach (Control ctrl in _messageFlow.Controls)
            {
                var bubble = ctrl as ChatBubble;
                bubble?.RecalculateSize(w);
            }
        }

        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+V with image on clipboard — intercept for image paste
            if (e.Control && e.KeyCode == Keys.V)
            {
                if (Clipboard.ContainsImage())
                {
                    e.SuppressKeyPress = true;
                    AddImageFromClipboard();
                    return;
                }
            }

            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                DoSend();
            }
            else if (e.KeyCode == Keys.Enter && e.Shift)
            {
                // Explicitly insert newline for Shift+Enter — Trados may intercept
                // the default TextBox handling, so we do it manually
                e.SuppressKeyPress = true;
                var selStart = _txtInput.SelectionStart;
                _txtInput.Text = _txtInput.Text.Insert(selStart, Environment.NewLine);
                _txtInput.SelectionStart = selStart + Environment.NewLine.Length;
            }
        }

        // ─── Image Attachment Methods ────────────────────────────

        private void AddImageFromClipboard()
        {
            if (_pendingImages.Count >= MaxImages) return;

            try
            {
                var img = Clipboard.GetImage();
                if (img == null) return;

                using (var ms = new MemoryStream())
                {
                    img.Save(ms, ImageFormat.Png);
                    var data = ms.ToArray();
                    if (data.Length > MaxImageBytes) return;

                    var attachment = new ImageAttachment
                    {
                        Data = data,
                        MimeType = "image/png",
                        FileName = $"paste_{DateTime.Now:HHmmss}.png",
                        Width = img.Width,
                        Height = img.Height
                    };
                    AddImage(attachment);
                }
            }
            catch { /* Clipboard access may fail */ }
        }

        private void AddImageFromFile(string filePath)
        {
            if (_pendingImages.Count >= MaxImages) return;

            try
            {
                var ext = Path.GetExtension(filePath).ToLowerInvariant();
                string mimeType;
                switch (ext)
                {
                    case ".png": mimeType = "image/png"; break;
                    case ".jpg": case ".jpeg": mimeType = "image/jpeg"; break;
                    case ".gif": mimeType = "image/gif"; break;
                    case ".webp": mimeType = "image/webp"; break;
                    case ".bmp": mimeType = "image/bmp"; break;
                    default: return; // unsupported format
                }

                var data = File.ReadAllBytes(filePath);
                if (data.Length > MaxImageBytes) return;

                // Get dimensions
                int w = 0, h = 0;
                try
                {
                    using (var ms = new MemoryStream(data))
                    using (var img = Image.FromStream(ms, false, false))
                    {
                        w = img.Width;
                        h = img.Height;
                    }
                }
                catch { }

                var attachment = new ImageAttachment
                {
                    Data = data,
                    MimeType = mimeType,
                    FileName = Path.GetFileName(filePath),
                    Width = w,
                    Height = h
                };
                AddImage(attachment);
            }
            catch { /* File read failure */ }
        }

        private void AddImage(ImageAttachment attachment)
        {
            _pendingImages.Add(attachment);
            AddThumbnailToStrip(attachment);
            UpdateAttachmentStripVisibility();
            LayoutInputPanel();
        }

        private void RemoveImage(ImageAttachment attachment)
        {
            _pendingImages.Remove(attachment);
            RemoveStripItem(attachment);
            UpdateAttachmentStripVisibility();
            LayoutInputPanel();
        }

        // ─── Document attachments ─────────────────────────────────

        private void AddDocumentFromFile(string filePath)
        {
            if (_pendingDocuments.Count >= MaxDocuments) return;

            try
            {
                var fi = new FileInfo(filePath);
                if (!fi.Exists) return;

                var extractedText = DocumentTextExtractor.ExtractText(filePath);

                var doc = new DocumentAttachment
                {
                    FileName = fi.Name,
                    ExtractedText = extractedText,
                    FileSize = fi.Length
                };
                AddDocument(doc);
            }
            catch (Exception ex)
            {
                MessageBox.Show(FindForm(),
                    $"Could not read file:\n{ex.Message}",
                    "Attachment Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private void AddDocument(DocumentAttachment doc)
        {
            _pendingDocuments.Add(doc);
            AddDocumentChipToStrip(doc);
            UpdateAttachmentStripVisibility();
            LayoutInputPanel();
        }

        private void RemoveDocument(DocumentAttachment doc)
        {
            _pendingDocuments.Remove(doc);
            RemoveStripItem(doc);
            UpdateAttachmentStripVisibility();
            LayoutInputPanel();
        }

        private void AddDocumentChipToStrip(DocumentAttachment doc)
        {
            // Container panel for document chip + remove button
            var chipPanel = new Panel
            {
                Size = new Size(54, 54),
                Margin = new Padding(2),
                Tag = doc
            };

            // Document icon area (file icon instead of image thumbnail)
            var iconLabel = new Label
            {
                Size = new Size(48, 48),
                Location = new Point(0, 0),
                BackColor = Color.FromArgb(235, 243, 254),
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe MDL2 Assets", UiScale.FontSize(16f)),
                ForeColor = Color.FromArgb(80, 120, 180),
                Text = GetDocumentIcon(doc.FileName),
                Cursor = Cursors.Hand
            };

            // ✕ remove button (top-right corner)
            var btnRemove = new Button
            {
                Text = "\u00D7", // ×
                Size = new Size(UiScale.Pixels(16), UiScale.Pixels(16)),
                Location = new Point(UiScale.Pixels(34), 0),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", UiScale.FontSize(7f), FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.FromArgb(180, 60, 60),
                Cursor = Cursors.Hand,
                TabStop = false,
                Padding = Padding.Empty,
                Margin = Padding.Empty
            };
            btnRemove.FlatAppearance.BorderSize = 0;
            btnRemove.Click += (s, e) => RemoveDocument(doc);

            // Tooltip with filename and size
            var sizeStr = DocumentTextExtractor.FormatFileSize(doc.FileSize);
            var tip = new ToolTip();
            tip.SetToolTip(iconLabel, $"{doc.FileName} ({sizeStr})");

            chipPanel.Controls.Add(btnRemove);
            chipPanel.Controls.Add(iconLabel);
            btnRemove.BringToFront();

            _attachmentStrip.Controls.Add(chipPanel);
        }

        private static string GetDocumentIcon(string fileName)
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            switch (ext)
            {
                case ".pdf": return "\uEA90";         // PDF icon
                case ".docx": case ".doc": case ".rtf":
                    return "\uE8A5";                  // Document icon
                case ".pptx": case ".ppt":
                    return "\uEE71";                  // Slideshow icon
                case ".xlsx": case ".xls": case ".csv": case ".tsv":
                    return "\uE80A";                  // Grid/table icon
                case ".tmx": case ".sdlxliff": case ".xliff": case ".xlf": case ".tbx":
                    return "\uE8C1";                  // Switch/translate icon
                default: return "\uE8A5";             // Generic document icon
            }
        }

        // ─── Shared attachment helpers ─────────────────────────────

        private void RemoveStripItem(object tag)
        {
            for (int i = _attachmentStrip.Controls.Count - 1; i >= 0; i--)
            {
                if (_attachmentStrip.Controls[i].Tag == tag)
                {
                    var ctrl = _attachmentStrip.Controls[i];
                    _attachmentStrip.Controls.RemoveAt(i);
                    ctrl.Dispose();
                    break;
                }
            }
        }

        private void ClearAttachments()
        {
            _pendingImages.Clear();
            _pendingDocuments.Clear();
            _attachmentStrip.SuspendLayout();
            foreach (Control ctrl in _attachmentStrip.Controls)
                ctrl.Dispose();
            _attachmentStrip.Controls.Clear();
            _attachmentStrip.ResumeLayout();
            UpdateAttachmentStripVisibility();
            LayoutInputPanel();
        }

        private bool HasPendingAttachments => _pendingImages.Count > 0 || _pendingDocuments.Count > 0;

        private void UpdateAttachmentStripVisibility()
        {
            _attachmentStrip.Visible = HasPendingAttachments;

            // Grow input panel when attachments are present
            _inputPanel.Height = HasPendingAttachments ? 140 : 90;
        }

        private void AddThumbnailToStrip(ImageAttachment attachment)
        {
            // Container panel for thumbnail + remove button
            var thumbPanel = new Panel
            {
                Size = new Size(54, 54),
                Margin = new Padding(2),
                Tag = attachment
            };

            // Thumbnail image
            var picBox = new PictureBox
            {
                Size = new Size(48, 48),
                Location = new Point(0, 0),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.FromArgb(240, 240, 240),
                BorderStyle = BorderStyle.FixedSingle,
                Cursor = Cursors.Hand
            };

            try
            {
                using (var ms = new MemoryStream(attachment.Data))
                {
                    picBox.Image = Image.FromStream(ms);
                }
            }
            catch
            {
                picBox.BackColor = Color.FromArgb(200, 200, 200);
            }

            // ✕ remove button (top-right corner of thumbnail)
            var btnRemove = new Button
            {
                Text = "\u00D7", // ×
                Size = new Size(UiScale.Pixels(16), UiScale.Pixels(16)),
                Location = new Point(UiScale.Pixels(34), 0),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", UiScale.FontSize(7f), FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.FromArgb(180, 60, 60),
                Cursor = Cursors.Hand,
                TabStop = false,
                Padding = Padding.Empty,
                Margin = Padding.Empty
            };
            btnRemove.FlatAppearance.BorderSize = 0;
            btnRemove.Click += (s, e) => RemoveImage(attachment);

            // Tooltip with filename
            var tip = new ToolTip();
            tip.SetToolTip(picBox, attachment.FileName);

            thumbPanel.Controls.Add(btnRemove);
            thumbPanel.Controls.Add(picBox);
            btnRemove.BringToFront();

            _attachmentStrip.Controls.Add(thumbPanel);
        }

        // ─── Drag and Drop ─────────────────────────────────────

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && HasSupportedFiles(files))
                {
                    e.Effect = DragDropEffects.Copy;
                    return;
                }
            }

            e.Effect = DragDropEffects.None;
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files == null) return;

            foreach (var file in files)
            {
                if (DocumentTextExtractor.IsImageFile(file))
                {
                    if (_pendingImages.Count < MaxImages)
                        AddImageFromFile(file);
                }
                else if (DocumentTextExtractor.IsDocumentFile(file))
                {
                    if (_pendingDocuments.Count < MaxDocuments)
                        AddDocumentFromFile(file);
                }
            }
        }

        private static bool HasSupportedFiles(string[] files)
        {
            foreach (var f in files)
                if (DocumentTextExtractor.IsImageFile(f) || DocumentTextExtractor.IsDocumentFile(f))
                    return true;
            return false;
        }

        // ─── Attach button (file browse) ─────────────────────

        private void OnAttachClick(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "Attach File",
                Filter =
                    "All supported files|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp;" +
                    "*.docx;*.doc;*.pdf;*.rtf;*.pptx;*.ppt;*.xlsx;*.xls;*.csv;*.tsv;" +
                    "*.tmx;*.sdlxliff;*.xliff;*.xlf;*.tbx;*.txt;*.md;*.htm;*.html;*.json;*.xml|" +
                    "Images|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|" +
                    "Documents|*.docx;*.doc;*.pdf;*.rtf;*.pptx;*.ppt|" +
                    "Spreadsheets|*.xlsx;*.xls;*.csv;*.tsv|" +
                    "Translation files|*.tmx;*.sdlxliff;*.xliff;*.xlf;*.tbx|" +
                    "Text files|*.txt;*.md;*.htm;*.html;*.json;*.xml|" +
                    "All files|*.*",
                Multiselect = true
            })
            {
                if (dlg.ShowDialog(FindForm()) == DialogResult.OK)
                {
                    foreach (var file in dlg.FileNames)
                    {
                        if (DocumentTextExtractor.IsImageFile(file))
                        {
                            if (_pendingImages.Count < MaxImages)
                                AddImageFromFile(file);
                        }
                        else if (DocumentTextExtractor.IsDocumentFile(file))
                        {
                            if (_pendingDocuments.Count < MaxDocuments)
                                AddDocumentFromFile(file);
                        }
                    }
                }
            }
        }

        // ─── Send ──────────────────────────────────────────────

        internal void DoSend()
        {
            if (_isThinking) return;
            var text = _txtInput.Text?.Trim();

            // Allow sending attachments even without text
            var hasText = !string.IsNullOrEmpty(text);
            var hasImages = _pendingImages.Count > 0;
            var hasDocs = _pendingDocuments.Count > 0;

            if (!hasText && !hasImages && !hasDocs) return;

            _txtInput.Clear();

            // Capture and clear pending images
            List<ImageAttachment> images = null;
            if (hasImages)
                images = new List<ImageAttachment>(_pendingImages);

            // Capture and clear pending documents
            List<DocumentAttachment> documents = null;
            if (hasDocs)
                documents = new List<DocumentAttachment>(_pendingDocuments);

            if (hasImages || hasDocs)
                ClearAttachments();

            var displayText = _pendingDisplayText;
            _pendingDisplayText = null;
            var maxTokens = _pendingMaxTokens;
            _pendingMaxTokens = null;
            var showAsStatus = _pendingShowAsStatus;
            _pendingShowAsStatus = false;
            var promptName = _pendingPromptName;
            _pendingPromptName = null;

            SendRequested?.Invoke(this, new ChatSendEventArgs
            {
                Text = text ?? "",
                Images = images,
                Documents = documents,
                DisplayText = displayText,
                MaxTokens = maxTokens,
                ShowAsStatus = showAsStatus,
                PromptName = promptName
            });
        }

        /// <summary>
        /// Adds a message bubble to the chat panel and scrolls to it.
        /// </summary>
        public void AddMessage(ChatMessage message)
        {
            var bubbleWidth = _chatPanel.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2;
            var bubble = new ChatBubble(message, Math.Max(200, bubbleWidth), _chatFontSize);
            bubble.ApplyRequested += (s, text) =>
                ApplyToTargetRequested?.Invoke(this, text);
            bubble.SaveAsPromptRequested += (s, text) =>
                SaveAsPromptRequested?.Invoke(this, text);

            _messageFlow.Controls.Add(bubble);

            // Auto-scroll to latest message
            _chatPanel.ScrollControlIntoView(bubble);
        }

        /// <summary>
        /// Adds a small muted summary line below the last chat bubble.
        /// Used to show token/cost info after an AI call completes.
        /// </summary>
        public void AddSummaryLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            var bubbleWidth = _chatPanel.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2;
            var lbl = new Label
            {
                Text = text,
                Font = new Font("Segoe UI", 7.5f),
                ForeColor = Color.FromArgb(150, 150, 150),
                AutoSize = false,
                Width = Math.Max(200, bubbleWidth),
                Height = 18,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 12, 0),
                Margin = new Padding(0, 0, 0, 4)
            };
            _messageFlow.Controls.Add(lbl);
            _chatPanel.ScrollControlIntoView(lbl);
        }

        /// <summary>
        /// Removes all chat bubbles and resets the display.
        /// </summary>
        public void ClearMessages()
        {
            _messageFlow.SuspendLayout();
            foreach (Control ctrl in _messageFlow.Controls)
                ctrl.Dispose();
            _messageFlow.Controls.Clear();
            _messageFlow.ResumeLayout();
        }

        // Status messages shown while waiting for AI response
        private static readonly string[] ThinkingMessages = new[]
        {
            "Thinking\u2026",
            "Still working on it\u2026",
            "Generating response\u2026",
            "Almost there\u2026",
            "Still thinking\u2026",
            "Working on it\u2026",
            "Processing\u2026",
            "Hang tight\u2026"
        };

        /// <summary>
        /// Shows or hides the "Thinking..." indicator and toggles Send/Stop buttons.
        /// Uses an animated bubble in the chat flow so it stays visible regardless of
        /// dock layout changes. The bubble cycles through reassuring status messages.
        /// </summary>
        /// <summary>
        /// Overload that sets a custom status message on the thinking bubble.
        /// Call with isThinking=true and a message like "Checking Trados projects..."
        /// to update the bubble text mid-flight (e.g. during tool use).
        /// </summary>
        public void SetThinking(bool isThinking, string statusMessage)
        {
            if (isThinking && _thinkingBubble != null && !string.IsNullOrEmpty(statusMessage))
            {
                // Update the existing thinking bubble text without recreating it
                var lbl = _thinkingBubble.Controls.Count > 0
                    ? _thinkingBubble.Controls[0] as Label : null;
                if (lbl != null)
                {
                    lbl.Text = "  " + statusMessage;
                    _chatPanel.ScrollControlIntoView(_thinkingBubble);
                }
                return;
            }

            SetThinking(isThinking);
        }

        public void SetThinking(bool isThinking)
        {
            _isThinking = isThinking;
            // Old docked label kept hidden — the thinking bubble in the chat flow
            // is more reliable and visible
            _lblThinking.Visible = false;
            _btnSend.Visible = !isThinking;
            _btnStop.Visible = isThinking;
            _txtInput.Enabled = !isThinking;

            if (isThinking)
            {
                // Add an animated thinking bubble to the chat flow
                _thinkingBubble = CreateThinkingBubble();
                _messageFlow.Controls.Add(_thinkingBubble);
                _chatPanel.ScrollControlIntoView(_thinkingBubble);

                // Animate: cycle through status messages every ~8 seconds,
                // with animated dots within each message
                _thinkingTicks = 0;
                if (_thinkingTimer == null)
                {
                    _thinkingTimer = new Timer { Interval = 2000 };
                    _thinkingTimer.Tick += (s, e) =>
                    {
                        if (_thinkingBubble == null) return;
                        _thinkingTicks++;
                        var lbl = _thinkingBubble.Controls.Count > 0
                            ? _thinkingBubble.Controls[0] as Label : null;
                        if (lbl == null) return;

                        // Cycle through messages every 4 ticks (~8 seconds each)
                        var msgIndex = (_thinkingTicks / 4) % ThinkingMessages.Length;
                        var dots = new string('.', (_thinkingTicks % 3) + 1);
                        var baseText = ThinkingMessages[msgIndex];
                        // Replace trailing ellipsis with animated dots
                        if (baseText.EndsWith("\u2026"))
                            baseText = baseText.Substring(0, baseText.Length - 1);
                        lbl.Text = "  " + baseText + dots;

                        // Keep scrolled to the thinking bubble
                        _chatPanel.ScrollControlIntoView(_thinkingBubble);
                    };
                }
                _thinkingTimer.Start();
            }
            else
            {
                // Remove the thinking bubble
                _thinkingTimer?.Stop();
                if (_thinkingBubble != null)
                {
                    _messageFlow.Controls.Remove(_thinkingBubble);
                    _thinkingBubble.Dispose();
                    _thinkingBubble = null;
                }
            }
        }

        /// <summary>
        /// Creates a lightweight thinking indicator styled like an assistant bubble.
        /// </summary>
        private Control CreateThinkingBubble()
        {
            var container = new Panel
            {
                Width = _chatPanel.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2,
                Height = 40,
                BackColor = Color.White,
                Margin = new Padding(0, 2, 0, 2)
            };

            var lbl = new Label
            {
                Text = "  Thinking\u2026",
                Font = new Font("Segoe UI", UiScale.FontSize(9f), FontStyle.Italic),
                ForeColor = Color.FromArgb(100, 100, 100),
                BackColor = ColorTranslator.FromHtml("#F0F0F0"),
                AutoSize = false,
                Size = new Size(UiScale.Pixels(200), UiScale.Pixels(30)),
                Location = new Point(UiScale.Pixels(8), UiScale.Pixels(4)),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(4, 0, 4, 0)
            };

            // Round the corners slightly via Region
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(0, 0, 10, 10, 180, 90);
            path.AddArc(lbl.Width - 10, 0, 10, 10, 270, 90);
            path.AddArc(lbl.Width - 10, lbl.Height - 10, 10, 10, 0, 90);
            path.AddArc(0, lbl.Height - 10, 10, 10, 90, 90);
            path.CloseFigure();
            lbl.Region = new Region(path);

            container.Controls.Add(lbl);
            return container;
        }

        /// <summary>
        /// Updates the context strip with current segment info.
        /// </summary>
        public void UpdateContextInfo(string sourceText, string targetText,
            int termCount, string langPair)
        {
            var parts = new List<string>();

            if (!string.IsNullOrEmpty(langPair))
                parts.Add(langPair);

            if (!string.IsNullOrEmpty(sourceText))
            {
                var truncated = sourceText.Length > 60
                    ? sourceText.Substring(0, 57) + "\u2026"
                    : sourceText;
                parts.Add("Source: \u201c" + truncated + "\u201d");
            }

            if (termCount > 0)
                parts.Add(termCount + " term" + (termCount == 1 ? "" : "s"));

            _lblContext.Text = parts.Count > 0
                ? string.Join("  |  ", parts)
                : "No document open";
        }

        /// <summary>
        /// Updates the status bar with provider/model info.
        /// </summary>
        public void UpdateProviderInfo(string provider, string model)
        {
            _currentProvider = provider;
            _currentModel = model;
            _lblStatus.Text = !string.IsNullOrEmpty(provider) && !string.IsNullOrEmpty(model)
                ? $"{provider} / {model}"
                : "";
        }

        /// <summary>
        /// Raised when the user selects a different model from the status bar dropdown.
        /// Args: (providerKey, modelId).
        /// </summary>
        public event Action<string, string> ModelChangeRequested;

        private void OnModelSelectorClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            var menu = new ContextMenuStrip { Font = new Font("Segoe UI", UiScale.FontSize(8.5f)) };

            foreach (var providerKey in LlmModels.AllProviderKeys)
            {
                var models = LlmModels.GetModelsForProvider(providerKey);

                // Custom OpenAI profiles are handled separately
                if (providerKey == LlmModels.ProviderCustomOpenAi)
                    continue;

                if (models.Length == 0) continue;

                var providerName = LlmModels.GetProviderDisplayName(providerKey);
                var providerItem = new ToolStripMenuItem(providerName);

                foreach (var model in models)
                {
                    var modelItem = new ToolStripMenuItem(model.DisplayName)
                    {
                        ToolTipText = model.Description,
                        Tag = new[] { providerKey, model.Id }
                    };

                    // Checkmark for current selection
                    if (providerKey == _currentProvider && model.Id == _currentModel)
                        modelItem.Checked = true;

                    modelItem.Click += OnModelMenuItemClicked;
                    providerItem.DropDownItems.Add(modelItem);
                }

                // Bold the provider submenu if it's the active one
                if (providerKey == _currentProvider)
                    providerItem.Font = new Font(providerItem.Font, FontStyle.Bold);

                menu.Items.Add(providerItem);
            }

            menu.Show(_lblStatus, new Point(0, -menu.PreferredSize.Height));
        }

        private void OnModelMenuItemClicked(object sender, EventArgs e)
        {
            var item = sender as ToolStripMenuItem;
            var tag = item?.Tag as string[];
            if (tag == null || tag.Length != 2) return;

            ModelChangeRequested?.Invoke(tag[0], tag[1]);
        }

        /// <summary>
        /// Sets focus to the input textbox.
        /// </summary>
        public void FocusInput()
        {
            _txtInput?.Focus();
        }

        /// <summary>
        /// Programmatically sets the input text and submits it, as if the user typed it and
        /// pressed Enter. Used by QuickLauncherAction to inject fully-expanded prompt content.
        /// Does nothing if a request is already in progress.
        /// </summary>
        public void SubmitMessage(string text)
        {
            SubmitMessage(text, null);
        }

        /// <summary>
        /// Like <see cref="SubmitMessage(string)"/> but shows <paramref name="displayText"/> in
        /// the chat bubble instead of the full <paramref name="text"/>. The full text is still
        /// sent to the AI. Use this when <paramref name="text"/> contains a large {{PROJECT}}
        /// expansion that would clutter the chat history.
        /// </summary>
        public void SubmitMessage(string text, string displayText, string promptName = null,
            int? maxTokens = null, bool showAsStatus = false)
        {
            if (_isThinking) return;
            if (string.IsNullOrWhiteSpace(text)) return;

            // Switch to the Chat tab so the response is visible
            _tabControl.SelectedIndex = 0;

            _pendingDisplayText = displayText;
            _pendingMaxTokens = maxTokens;
            _pendingShowAsStatus = showAsStatus;
            _pendingPromptName = promptName;
            _txtInput.Text = text;
            DoSend();
        }

        /// <summary>
        /// Updates the Reports tab text with a badge showing the issue count.
        /// </summary>
        public void UpdateReportsBadge(int issueCount)
        {
            _tabControl.TabPages[2].Text = issueCount > 0
                ? $"Reports  \u26A0 {issueCount}"
                : "Reports";
        }

        /// <summary>
        /// Switches to the Reports tab.
        /// </summary>
        public void SwitchToReportsTab()
        {
            _tabControl.SelectedIndex = 2;
        }

        // ─── License gating ────────────────────────────────────────

        private Panel _upgradeOverlay;

        /// <summary>
        /// Shows an upgrade-required overlay that covers the AI Assistant panel.
        /// </summary>
        public void ShowUpgradeRequired()
        {
            if (_upgradeOverlay != null) return;

            _upgradeOverlay = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White
            };

            var lbl = new Label
            {
                Text = "A licence is required to use this feature.\n\nClick the \u2699 button above to open Settings \u2192 Licence.",
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", UiScale.FontSize(9.5f)),
                ForeColor = Color.FromArgb(100, 100, 100)
            };

            _upgradeOverlay.Controls.Add(lbl);
            Controls.Add(_upgradeOverlay);
            _upgradeOverlay.BringToFront();

            // Keep gear and help buttons above the overlay so users can access settings
            _btnSettings.BringToFront();
            _btnHelp.BringToFront();
        }

        /// <summary>
        /// Hides the upgrade-required overlay after license upgrade.
        /// </summary>
        public void HideUpgradeRequired()
        {
            if (_upgradeOverlay == null) return;
            Controls.Remove(_upgradeOverlay);
            _upgradeOverlay.Dispose();
            _upgradeOverlay = null;
        }
    }

    /// <summary>
    /// TextBox subclass that uses a WH_GETMESSAGE hook to intercept Enter and
    /// Shift+Enter before Trados Studio's IMessageFilter chain can steal them.
    ///
    /// Key processing order in WinForms:
    ///   -1. WH_GETMESSAGE hook  ← we intercept HERE (fires inside GetMessage)
    ///    0. IMessageFilter chain ← Trados intercepts here (FIFO — its filter runs first)
    ///    1. ProcessCmdKey        ← walks focused control → parent chain
    ///    2. IsInputKey           ← only if ProcessCmdKey didn't consume
    ///    3. KeyDown event        ← only if IsInputKey returned true
    ///
    /// Trados registers its IMessageFilter at startup, so it runs before ours
    /// (FIFO order). The only way to beat it is a WH_GETMESSAGE hook, which
    /// fires when GetMessage returns — before the IMessageFilter chain starts.
    ///
    /// Also normalizes pasted text: Trados clipboard uses bare \n for soft
    /// returns, but the Windows EDIT control only displays \r\n as line breaks.
    /// </summary>
    internal class ChatInputTextBox : TextBox
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public int message;
            public IntPtr wParam;
            public IntPtr lParam;
            public int time;
            public int pt_x;
            public int pt_y;
        }

        private const int WH_GETMESSAGE = 3;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_NULL = 0x0000;
        private const int WM_PASTE = 0x0302;
        private const int VK_RETURN = 0x0D;
        private const int HC_ACTION = 0;
        private const int PM_REMOVE = 0x0001;

        private IntPtr _hookId;
        private HookProc _hookDelegate; // prevent GC collection of delegate

        public ChatInputTextBox()
        {
            _hookDelegate = GetMsgHookProc;
            _hookId = SetWindowsHookEx(WH_GETMESSAGE, _hookDelegate, IntPtr.Zero, GetCurrentThreadId());
        }

        /// <summary>
        /// WH_GETMESSAGE hook callback — fires when GetMessage returns, before
        /// the IMessageFilter chain. If the message is WM_KEYDOWN for VK_RETURN
        /// and our TextBox has Win32 focus, we replace the message with WM_NULL
        /// (killing it) and handle the Enter key ourselves via BeginInvoke.
        /// </summary>
        private IntPtr GetMsgHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode == HC_ACTION && (int)wParam == PM_REMOVE && IsHandleCreated)
            {
                var msg = (MSG)Marshal.PtrToStructure(lParam, typeof(MSG));

                if (msg.message == WM_KEYDOWN && msg.wParam == (IntPtr)VK_RETURN
                    && GetFocus() == Handle)
                {
                    // Kill the message so Trados's IMessageFilter sees WM_NULL
                    msg.message = WM_NULL;
                    Marshal.StructureToPtr(msg, lParam, false);

                    // Handle Enter key on the next message pump cycle
                    var modifiers = Control.ModifierKeys;
                    BeginInvoke(new Action(() =>
                        OnKeyDown(new KeyEventArgs(Keys.Enter | modifiers))));
                }
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        /// <summary>
        /// Normalize pasted text: Trados clipboard uses bare \n for soft returns
        /// (Shift+Enter), but the Windows EDIT control only shows \r\n as newlines.
        /// </summary>
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_PASTE && Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (text.Contains("\n") && !text.Contains("\r\n"))
                {
                    // Normalize \n → \r\n so the TextBox displays them
                    text = text.Replace("\n", "\r\n");
                    var selStart = SelectionStart;
                    var selLen = SelectionLength;
                    var before = Text.Substring(0, selStart);
                    var after = Text.Substring(selStart + selLen);
                    Text = before + text + after;
                    SelectionStart = selStart + text.Length;
                    return; // Don't call base — we handled the paste
                }
            }
            base.WndProc(ref m);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if ((keyData & Keys.KeyCode) == Keys.Enter)
                return true;
            return base.IsInputKey(keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
            base.Dispose(disposing);
        }
    }
}
