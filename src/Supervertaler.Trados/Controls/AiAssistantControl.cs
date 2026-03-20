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
        private Label _lblStatus;
        private Label _lblThinking;
        private Control _thinkingBubble;
        private Timer _thinkingTimer;
        private int _thinkingTicks;
        private FlowLayoutPanel _attachmentStrip;

        // Batch Translate tab
        private readonly BatchTranslateControl _batchTranslateControl;

        // Reports tab
        private ReportsControl _reportsControl;

        private bool _isThinking;

        // Pending image attachments for the next message
        private readonly List<ImageAttachment> _pendingImages = new List<ImageAttachment>();

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

        private const int MaxImages = 5;
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

        /// <summary>Exposes the BatchTranslateControl for event wiring by the ViewPart.</summary>
        public BatchTranslateControl BatchTranslateControl => _batchTranslateControl;

        /// <summary>Exposes the ReportsControl for event wiring by the ViewPart.</summary>
        public ReportsControl ReportsControl => _reportsControl;

        public AiAssistantControl()
        {
            SuspendLayout();
            BackColor = Color.White;

            // ─── Tab control (fills entire panel) ────────────────────
            _tabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8.5f),
                Padding = new Point(6, 3),
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
                Text = "\u2699\uFE0E",  // gear character + text presentation selector
                Size = new Size(26, 22),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Symbol", 10f),
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
                Size = new Size(26, 22),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                ForeColor = Color.FromArgb(100, 100, 100),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                TabStop = false,
                UseCompatibleTextRendering = true,
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(0, 1, 0, 0),
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
                Height = 28,
                BackColor = Color.FromArgb(248, 248, 248),
                Padding = new Padding(8, 0, 60, 0)  // extra right padding to avoid gear/help buttons
            };

            _lblContext = new Label
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 7.5f),
                ForeColor = Color.FromArgb(100, 100, 100),
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "No document open",
                AutoEllipsis = true
            };
            _contextStrip.Controls.Add(_lblContext);

            // Thin separator line below context
            var contextSep = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 1,
                BackColor = Color.FromArgb(220, 220, 220)
            };
            _contextStrip.Controls.Add(contextSep);

            // ─── Input panel (bottom) ─────────────────────────────
            _inputPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 90,
                Padding = new Padding(8, 4, 8, 4),
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
                Size = new Size(60, 26),
                Font = new Font("Segoe UI", 8f),
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
                Size = new Size(48, 26),
                Font = new Font("Segoe UI", 8f),
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
                Size = new Size(48, 26),
                Font = new Font("Segoe UI", 8f),
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

            // Attach button for browsing image files
            _btnAttach = new Button
            {
                Text = "\uE8B9",  // Photo icon in Segoe MDL2 Assets
                Size = new Size(28, 26),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe MDL2 Assets", 10f),
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

            _lblStatus = new Label
            {
                Font = new Font("Segoe UI", 7f),
                ForeColor = Color.FromArgb(140, 140, 140),
                Text = "",
                AutoSize = true,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left
            };

            _txtInput = new ChatInputTextBox
            {
                Multiline = true,
                AcceptsReturn = true,   // allow Enter keys as input (we handle send vs newline in KeyDown)
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Segoe UI", 9f),
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
            inputTips.SetToolTip(_btnAttach, "Attach image (paste with Ctrl+V, or drag and drop)");
            inputTips.SetToolTip(_txtInput, "Type your message. Shift+Enter for new line.");
            inputTips.SetToolTip(_resizeHandle, "Drag to resize input area");

            // ─── Thinking indicator ───────────────────────────────
            _lblThinking = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 24,
                Font = new Font("Segoe UI", 8f, FontStyle.Italic),
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
            page.Controls.Add(_contextStrip);

            // Layout input controls
            _inputPanel.Resize += (s, e) => LayoutInputPanel();
            _chatPanel.Resize += (s, e) => RelayoutBubbles();

            // Initial layout when tab loads
            page.Layout += (s, e) => LayoutInputPanel();
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
                    if (_batchTranslateControl.CurrentMode == BatchMode.Proofread)
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
            menu.Items.Add("Example Project", null, (s, ev) =>
                HelpSystem.OpenHelp(HelpSystem.Topics.ExampleProject));
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
                    case 1: topic = _batchTranslateControl.CurrentMode == BatchMode.Proofread
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

            // Find and remove the corresponding thumbnail panel
            for (int i = _attachmentStrip.Controls.Count - 1; i >= 0; i--)
            {
                if (_attachmentStrip.Controls[i].Tag == attachment)
                {
                    var ctrl = _attachmentStrip.Controls[i];
                    _attachmentStrip.Controls.RemoveAt(i);
                    ctrl.Dispose();
                    break;
                }
            }

            UpdateAttachmentStripVisibility();
            LayoutInputPanel();
        }

        private void ClearAttachments()
        {
            _pendingImages.Clear();
            _attachmentStrip.SuspendLayout();
            foreach (Control ctrl in _attachmentStrip.Controls)
                ctrl.Dispose();
            _attachmentStrip.Controls.Clear();
            _attachmentStrip.ResumeLayout();
            UpdateAttachmentStripVisibility();
            LayoutInputPanel();
        }

        private void UpdateAttachmentStripVisibility()
        {
            _attachmentStrip.Visible = _pendingImages.Count > 0;

            // Grow input panel when attachments are present
            _inputPanel.Height = _pendingImages.Count > 0 ? 140 : 90;
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
                Size = new Size(16, 16),
                Location = new Point(34, 0),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 7f, FontStyle.Bold),
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
                if (files != null && HasImageFiles(files))
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
                if (_pendingImages.Count >= MaxImages) break;
                if (IsImageFile(file))
                    AddImageFromFile(file);
            }
        }

        private static bool HasImageFiles(string[] files)
        {
            foreach (var f in files)
                if (IsImageFile(f)) return true;
            return false;
        }

        private static bool IsImageFile(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg"
                || ext == ".gif" || ext == ".webp" || ext == ".bmp";
        }

        // ─── Attach button (file browse) ─────────────────────

        private void OnAttachClick(object sender, EventArgs e)
        {
            if (_pendingImages.Count >= MaxImages) return;

            using (var dlg = new OpenFileDialog
            {
                Title = "Attach Image",
                Filter = "Image files|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|All files|*.*",
                Multiselect = true
            })
            {
                if (dlg.ShowDialog(FindForm()) == DialogResult.OK)
                {
                    foreach (var file in dlg.FileNames)
                    {
                        if (_pendingImages.Count >= MaxImages) break;
                        AddImageFromFile(file);
                    }
                }
            }
        }

        // ─── Send ──────────────────────────────────────────────

        internal void DoSend()
        {
            if (_isThinking) return;
            var text = _txtInput.Text?.Trim();

            // Allow sending images even without text, and text without images
            var hasText = !string.IsNullOrEmpty(text);
            var hasImages = _pendingImages.Count > 0;

            if (!hasText && !hasImages) return;

            _txtInput.Clear();

            // Capture and clear pending images
            List<ImageAttachment> images = null;
            if (hasImages)
            {
                images = new List<ImageAttachment>(_pendingImages);
                ClearAttachments();
            }

            var displayText = _pendingDisplayText;
            _pendingDisplayText = null;
            var maxTokens = _pendingMaxTokens;
            _pendingMaxTokens = null;
            var showAsStatus = _pendingShowAsStatus;
            _pendingShowAsStatus = false;

            SendRequested?.Invoke(this, new ChatSendEventArgs
            {
                Text = text ?? "",
                Images = images,
                DisplayText = displayText,
                MaxTokens = maxTokens,
                ShowAsStatus = showAsStatus
            });
        }

        /// <summary>
        /// Adds a message bubble to the chat panel and scrolls to it.
        /// </summary>
        public void AddMessage(ChatMessage message)
        {
            var bubbleWidth = _chatPanel.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2;
            var bubble = new ChatBubble(message, Math.Max(200, bubbleWidth));
            bubble.ApplyRequested += (s, text) =>
                ApplyToTargetRequested?.Invoke(this, text);
            bubble.SaveAsPromptRequested += (s, text) =>
                SaveAsPromptRequested?.Invoke(this, text);

            _messageFlow.Controls.Add(bubble);

            // Auto-scroll to latest message
            _chatPanel.ScrollControlIntoView(bubble);
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
                Font = new Font("Segoe UI", 9f, FontStyle.Italic),
                ForeColor = Color.FromArgb(100, 100, 100),
                BackColor = ColorTranslator.FromHtml("#F0F0F0"),
                AutoSize = false,
                Size = new Size(200, 30),
                Location = new Point(8, 4),
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
            _lblStatus.Text = !string.IsNullOrEmpty(provider) && !string.IsNullOrEmpty(model)
                ? $"{provider} / {model}"
                : "";
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
        public void SubmitMessage(string text, string displayText, int? maxTokens = null,
            bool showAsStatus = false)
        {
            if (_isThinking) return;
            if (string.IsNullOrWhiteSpace(text)) return;

            // Switch to the Chat tab so the response is visible
            _tabControl.SelectedIndex = 0;

            _pendingDisplayText = displayText;
            _pendingMaxTokens = maxTokens;
            _pendingShowAsStatus = showAsStatus;
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
                Text = "The Supervertaler Assistant requires a\n\"TermLens + Supervertaler Assistant\" license.\n\nUpgrade in Settings \u2192 License.",
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = Color.FromArgb(100, 100, 100)
            };

            _upgradeOverlay.Controls.Add(lbl);
            Controls.Add(_upgradeOverlay);
            _upgradeOverlay.BringToFront();
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
