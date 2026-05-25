using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// Chat message bubble for the Supervertaler Assistant.
    /// User messages are right-aligned with blue background;
    /// assistant messages are left-aligned with gray background.
    /// Uses an embedded RichTextBox for markdown rendering in assistant messages.
    /// Supports image thumbnails above the text content.
    /// </summary>
    public class ChatBubble : Control
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private static readonly Color UserBg = ColorTranslator.FromHtml("#D6EBFF");
        private static readonly Color AssistantBg = ColorTranslator.FromHtml("#F0F0F0");
        private static readonly Color TextColor = Color.FromArgb(30, 30, 30);
        private static readonly Color TimestampColor = Color.FromArgb(140, 140, 140);

        // Base pixel constants (before UiScale)
        private const int BaseBubblePadding = 10;
        private const int BaseBubbleRadius = 8;
        private const int BaseTimestampHeight = 14;
        private const int BaseHorizontalMargin = 8;
        private const int BaseAvatarSize = 18;
        private const int BaseAvatarHeaderHeight = 22;
        private const int BaseImageThumbMaxWidth = 200;
        private const int BaseImageThumbMaxHeight = 150;
        private const int BaseImageSpacing = 4;

        private static readonly Color UserAvatarBg = ColorTranslator.FromHtml("#4A90D9");
        private static readonly Color AssistantAvatarBg = ColorTranslator.FromHtml("#6B7280");

        // Instance fonts – derived from the fontSize parameter
        private Font _messageFont;
        private Font _timestampFont;
        private Font _avatarFont;
        private Font _nameFont;

        private readonly ChatMessage _message;
        private readonly bool _isUser;
        private readonly string _timestampText;
        private readonly string _plainContent;
        private readonly string _markdownContent;
        private readonly RichTextBox _rtb;

        /// <summary>
        /// When set by the parent control (via <see cref="AiAssistantControl.AddMessage"/>),
        /// holds the full original markdown content of a message that was truncated
        /// for display. The Copy context menu item uses this instead of the truncated
        /// <see cref="_markdownContent"/> so the user always gets the complete text
        /// on Ctrl+C / right-click → Copy, even though only the first ~1000 characters
        /// are rendered in the bubble.
        /// </summary>
        internal string FullMarkdownContent { get; set; }

        /// <summary>Raised when the bubble is expanded or collapsed so the parent can update scroll.</summary>
        internal event EventHandler ExpandedChanged;

        private LinkLabel _expandLink;
        private bool _isExpanded;
        private string _fullMarkdownForExpand;
        private int _expandLinkHeight;

        private readonly List<PictureBox> _imageThumbs = new List<PictureBox>();

        private int _bubbleWidth;
        private int _bubbleHeight;
        private int _imageAreaHeight;
        private Rectangle _bubbleRect;

        private void CreateFonts(float fontSize)
        {
            _messageFont?.Dispose();
            _timestampFont?.Dispose();
            _avatarFont?.Dispose();
            _nameFont?.Dispose();

            _messageFont = new Font("Segoe UI", fontSize);
            _timestampFont = new Font("Segoe UI", Math.Max(6f, fontSize - 2f));
            _avatarFont = new Font("Segoe UI", Math.Max(6f, fontSize - 2f), FontStyle.Bold);
            _nameFont = new Font("Segoe UI", Math.Max(6f, fontSize - 1.5f));
        }

        /// <summary>
        /// Updates the font size and recalculates the bubble layout.
        /// Called when the user changes font size via A+/A- buttons.
        /// </summary>
        public void UpdateFontSize(float fontSize, int maxWidth)
        {
            CreateFonts(fontSize);
            if (_rtb != null) _rtb.Font = _messageFont;
            CalculateSize(maxWidth);
            Invalidate();
        }

        /// <summary>
        /// Enables the expand/collapse link on a truncated bubble.
        /// Call after construction, before the bubble is displayed.
        /// </summary>
        internal void SetTruncationSource(string fullMarkdown, int remainingChars)
        {
            _fullMarkdownForExpand = fullMarkdown;

            _expandLink = new LinkLabel
            {
                AutoSize = false,
                Text = $"Show full response ({remainingChars:N0} more characters)",
                LinkColor = ColorTranslator.FromHtml("#4A90D9"),
                ActiveLinkColor = ColorTranslator.FromHtml("#2A70B9"),
                VisitedLinkColor = ColorTranslator.FromHtml("#4A90D9"),
                Font = _timestampFont,
                BackColor = AssistantBg,
                Cursor = Cursors.Hand,
                Padding = Padding.Empty,
                Margin = Padding.Empty,
            };
            _expandLink.LinkClicked += OnExpandLinkClicked;

            // Forward mouse wheel to scrollable parent (same as RTB)
            _expandLink.MouseWheel += (s, e) =>
            {
                if (e is HandledMouseEventArgs hme)
                    hme.Handled = true;
                var scrollParent = Parent?.Parent;
                if (scrollParent != null)
                {
                    const int WM_MOUSEWHEEL = 0x020A;
                    SendMessage(scrollParent.Handle, WM_MOUSEWHEEL,
                        (IntPtr)((e.Delta << 16) | (int)Control.ModifierKeys),
                        IntPtr.Zero);
                }
            };

            Controls.Add(_expandLink);
            _expandLinkHeight = UiScale.Pixels(18);

            // Recalculate to accommodate the link
            CalculateSize(Width);
        }

        private void OnExpandLinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            if (_isExpanded)
            {
                // Collapse: not implemented – one-way expand is simpler and
                // avoids the complexity of re-truncating markdown mid-stream.
                return;
            }

            _isExpanded = true;

            // Re-render the RTB with the full markdown content
            try
            {
                var fullRtf = MarkdownToRtf.Convert(_fullMarkdownForExpand);
                _rtb.Rtf = fullRtf;
            }
            catch
            {
                _rtb.Text = _fullMarkdownForExpand;
            }

            // Hide the expand link
            _expandLink.Visible = false;

            // Recalculate and notify parent
            CalculateSize(Width);
            Invalidate();
            ExpandedChanged?.Invoke(this, EventArgs.Empty);
        }

        // Scaled pixel helpers – apply UiScale to base constants
        private static int BubblePadding => UiScale.Pixels(BaseBubblePadding);
        private static int BubbleRadius => UiScale.Pixels(BaseBubbleRadius);
        private static int TimestampHeight => UiScale.Pixels(BaseTimestampHeight);
        private static int HorizontalMargin => UiScale.Pixels(BaseHorizontalMargin);
        private static int AvatarSize => UiScale.Pixels(BaseAvatarSize);
        private static int AvatarHeaderHeight => UiScale.Pixels(BaseAvatarHeaderHeight);
        private static int ImageThumbMaxWidth => UiScale.Pixels(BaseImageThumbMaxWidth);
        private static int ImageThumbMaxHeight => UiScale.Pixels(BaseImageThumbMaxHeight);
        private static int ImageSpacing => UiScale.Pixels(BaseImageSpacing);

        /// <summary>Raised when user clicks "Apply to target" on an assistant bubble.</summary>
        public event EventHandler<string> ApplyRequested;

        /// <summary>Raised when user clicks "Save as Prompt" on an assistant bubble.</summary>
        public event EventHandler<string> SaveAsPromptRequested;

        /// <summary>Raised when user clicks "Save to memory bank" on an assistant bubble.</summary>
        public event EventHandler<string> SaveToMemoryBankRequested;

        public ChatBubble(ChatMessage message, int maxWidth, float fontSize = 9f)
        {
            _message = message;
            _isUser = message.Role == ChatRole.User;
            _timestampText = message.Timestamp.ToString("HH:mm");

            CreateFonts(fontSize);

            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw, true);

            BackColor = Color.White;
            Cursor = Cursors.Default;

            // Prepare display text.
            // DisplayContent, when set, shows a short summary (e.g. for {{PROJECT}} prompts
            // or system-initiated status messages) while Content is sent to the AI unchanged.
            var hasDisplayOverride = !string.IsNullOrEmpty(message.DisplayContent);
            var userDisplayText = _isUser || hasDisplayOverride
                ? (message.DisplayContent ?? message.Content ?? "")
                : null;

            // Prepare plain content (for apply-to-target) and raw markdown (for copy)
            _plainContent = _isUser || hasDisplayOverride
                ? userDisplayText
                : MarkdownToRtf.StripMarkdown(message.Content ?? "");
            _markdownContent = message.Content ?? "";

            // Create image thumbnails if present
            if (message.HasImages)
            {
                foreach (var img in message.Images)
                {
                    try
                    {
                        var picBox = new PictureBox
                        {
                            SizeMode = PictureBoxSizeMode.Zoom,
                            BackColor = _isUser ? UserBg : AssistantBg,
                            BorderStyle = BorderStyle.None,
                            Cursor = Cursors.Hand
                        };

                        using (var ms = new MemoryStream(img.Data))
                        {
                            picBox.Image = Image.FromStream(ms);
                        }

                        // Click to view full-size
                        var imgData = img; // capture for lambda
                        picBox.Click += (s, e) => ShowFullImage(imgData);

                        Controls.Add(picBox);
                        _imageThumbs.Add(picBox);
                    }
                    catch { /* Skip images that can't be loaded */ }
                }
            }

            // Create embedded RichTextBox for text rendering
            _rtb = new ChatRichTextBox
            {
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                ScrollBars = RichTextBoxScrollBars.None,
                BackColor = _isUser ? UserBg : AssistantBg,
                ForeColor = TextColor,
                Font = _messageFont,
                Cursor = Cursors.IBeam,
                DetectUrls = false,
                WordWrap = true,
                TabStop = true,
                ShortcutsEnabled = true,
            };

            // Set content: plain text for user or display-override messages,
            // RTF (markdown-rendered) for normal assistant messages
            if (_isUser || hasDisplayOverride)
            {
                _rtb.Text = userDisplayText;
            }
            else
            {
                try
                {
                    var rtf = MarkdownToRtf.Convert(message.Content ?? "");
                    _rtb.Rtf = rtf;
                }
                catch
                {
                    _rtb.Text = message.Content ?? "";
                }
            }

            // Forward mouse wheel events from the RichTextBox to the scrollable
            // chat panel so the user can scroll the conversation with the mouse wheel.
            _rtb.MouseWheel += (s, e) =>
            {
                if (e is HandledMouseEventArgs hme)
                    hme.Handled = true;
                // Find the scrollable parent (the Panel with AutoScroll)
                var scrollParent = Parent?.Parent;
                if (scrollParent != null)
                {
                    const int WM_MOUSEWHEEL = 0x020A;
                    SendMessage(scrollParent.Handle, WM_MOUSEWHEEL,
                        (IntPtr)((e.Delta << 16) | (int)Control.ModifierKeys),
                        IntPtr.Zero);
                }
            };

            Controls.Add(_rtb);

            CalculateSize(maxWidth);
            BuildContextMenu();
        }

        /// <summary>
        /// Recalculates size when the parent panel resizes.
        /// </summary>
        public void RecalculateSize(int maxWidth)
        {
            CalculateSize(maxWidth);
            Invalidate();
        }

        private void CalculateSize(int maxWidth)
        {
            // Max bubble width is 80% of available width, with reasonable bounds
            var maxBubble = Math.Max(200, (int)(maxWidth * 0.80));
            var textWidth = maxBubble - BubblePadding * 2;

            // Calculate image area height
            _imageAreaHeight = 0;
            if (_imageThumbs.Count > 0)
            {
                // Layout images in a horizontal row
                int maxThumbH = 0;
                foreach (var picBox in _imageThumbs)
                {
                    if (picBox.Image == null) continue;
                    var imgW = picBox.Image.Width;
                    var imgH = picBox.Image.Height;

                    // Scale to fit within max dimensions
                    var scale = Math.Min(
                        (float)ImageThumbMaxWidth / Math.Max(1, imgW),
                        (float)ImageThumbMaxHeight / Math.Max(1, imgH));
                    if (scale > 1f) scale = 1f;

                    var thumbW = (int)(imgW * scale);
                    var thumbH = (int)(imgH * scale);
                    picBox.Size = new Size(thumbW, thumbH);
                    if (thumbH > maxThumbH) maxThumbH = thumbH;
                }
                _imageAreaHeight = maxThumbH + ImageSpacing;
            }

            // Ensure RTB handle is created (needed for measurement)
            if (!_rtb.IsHandleCreated)
                _rtb.CreateControl();

            // Measure content height using the RichTextBox
            var contentHeight = MeasureRtbHeight(textWidth);

            // Extra height for the "Show full response" link when visible
            var linkAreaHeight = (_expandLink != null && _expandLink.Visible) ? _expandLinkHeight + 4 : 0;

            _bubbleWidth = maxBubble;
            _bubbleHeight = _imageAreaHeight + contentHeight + linkAreaHeight + BubblePadding * 2 + TimestampHeight;

            // Position the bubble rectangle (below avatar header)
            var topOffset = 4 + AvatarHeaderHeight;
            if (_isUser)
            {
                // Right-aligned
                var left = maxWidth - _bubbleWidth - HorizontalMargin;
                _bubbleRect = new Rectangle(Math.Max(HorizontalMargin, left), topOffset,
                    _bubbleWidth, _bubbleHeight);
            }
            else
            {
                // Left-aligned
                _bubbleRect = new Rectangle(HorizontalMargin, topOffset,
                    _bubbleWidth, _bubbleHeight);
            }

            // Position image thumbnails inside the bubble
            if (_imageThumbs.Count > 0)
            {
                int imgX = _bubbleRect.X + BubblePadding;
                int imgY = _bubbleRect.Y + BubblePadding;
                foreach (var picBox in _imageThumbs)
                {
                    picBox.Location = new Point(imgX, imgY);
                    imgX += picBox.Width + ImageSpacing;
                }
            }

            // Position the RTB inside the bubble area (below images)
            _rtb.Location = new Point(_bubbleRect.X + BubblePadding,
                _bubbleRect.Y + BubblePadding + _imageAreaHeight);
            _rtb.Size = new Size(textWidth, contentHeight);

            // Position the expand link below the RTB
            if (_expandLink != null && _expandLink.Visible)
            {
                _expandLink.Location = new Point(
                    _bubbleRect.X + BubblePadding,
                    _rtb.Bottom + 2);
                _expandLink.Size = new Size(textWidth, _expandLinkHeight);
            }

            Size = new Size(maxWidth, _bubbleHeight + AvatarHeaderHeight + 8);
        }

        private int MeasureRtbHeight(int width)
        {
            _rtb.Width = width;
            _rtb.Height = 100000; // allow full layout

            if (_rtb.TextLength == 0)
                return (int)Math.Ceiling(_messageFont.GetHeight()) + 4;

            var pos = _rtb.GetPositionFromCharIndex(_rtb.TextLength - 1);
            var lineHeight = (int)Math.Ceiling(_messageFont.GetHeight() * 1.3f);
            return Math.Max(lineHeight, pos.Y + lineHeight);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // Draw avatar circle + name above the bubble
            var avatarBg = _isUser ? UserAvatarBg : AssistantAvatarBg;
            var avatarText = "AI";
            var nameText = _isUser ? "You" : "Supervertaler";
            var avatarX = _bubbleRect.X;
            var avatarY = _bubbleRect.Y - AvatarHeaderHeight;

            // If user bubble is right-aligned, right-align the avatar too
            if (_isUser)
                avatarX = _bubbleRect.Right - AvatarSize
                    - (int)g.MeasureString(nameText, _nameFont).Width - 6;

            // Circle
            using (var brush = new SolidBrush(avatarBg))
                g.FillEllipse(brush, avatarX, avatarY, AvatarSize, AvatarSize);

            if (_isUser)
            {
                // Draw person silhouette (head circle + shoulders arc), clipped to the avatar circle
                var cx = avatarX + AvatarSize / 2f;
                var cy = avatarY + AvatarSize / 2f;
                var savedClip = g.Clip;
                using (var clipPath = new GraphicsPath())
                {
                    clipPath.AddEllipse(avatarX, avatarY, AvatarSize, AvatarSize);
                    g.SetClip(clipPath);

                    // Head
                    var headR = AvatarSize * 0.18f;
                    g.FillEllipse(Brushes.White, cx - headR, cy - headR - 2f, headR * 2, headR * 2);
                    // Shoulders
                    var shoulderW = AvatarSize * 0.48f;
                    var shoulderH = AvatarSize * 0.32f;
                    var shoulderY = cy + headR - 1f;
                    g.FillEllipse(Brushes.White, cx - shoulderW, shoulderY,
                        shoulderW * 2, shoulderH * 2);

                    g.Clip = savedClip;
                }
            }
            else
            {
                // "AI" text inside circle
                var avatarTextSize = g.MeasureString(avatarText, _avatarFont);
                g.DrawString(avatarText, _avatarFont, Brushes.White,
                    avatarX + (AvatarSize - avatarTextSize.Width) / 2f,
                    avatarY + (AvatarSize - avatarTextSize.Height) / 2f);
            }

            // Name label next to circle
            g.DrawString(nameText, _nameFont, new SolidBrush(Color.FromArgb(100, 100, 100)),
                avatarX + AvatarSize + 4, avatarY + 1);

            // Draw rounded rectangle background
            var bgColor = _isUser ? UserBg : AssistantBg;
            using (var brush = new SolidBrush(bgColor))
            using (var path = CreateRoundedRect(_bubbleRect, BubbleRadius))
            {
                g.FillPath(brush, path);
            }

            // Message text is rendered by the embedded RichTextBox (child control)
            // Images are rendered by PictureBox child controls

            // Draw timestamp (bottom-right of bubble)
            var tsRect = new Rectangle(
                _bubbleRect.X + BubblePadding,
                _bubbleRect.Bottom - TimestampHeight - 4,
                _bubbleRect.Width - BubblePadding * 2,
                TimestampHeight);

            TextRenderer.DrawText(g, _timestampText, _timestampFont,
                tsRect, TimestampColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }

        private static GraphicsPath CreateRoundedRect(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void BuildContextMenu()
        {
            var menu = new ContextMenuStrip();

            var copyItem = new ToolStripMenuItem("Copy");
            copyItem.Click += (s, e) =>
            {
                // If text is selected in the RTB, copy just the selection
                if (_rtb != null && _rtb.SelectionLength > 0)
                {
                    _rtb.Copy();
                }
                else
                {
                    // Copy raw markdown so tables, formatting etc. are preserved.
                    // Use FullMarkdownContent when available – this is the complete
                    // text of a response that was truncated for display. The user
                    // always gets the full version on Copy even though the bubble
                    // only renders the first ~1000 characters.
                    var textToCopy = FullMarkdownContent ?? _markdownContent;
                    if (!string.IsNullOrEmpty(textToCopy))
                        Clipboard.SetText(textToCopy);
                }
            };
            menu.Items.Add(copyItem);

            if (!_isUser)
            {
                var applyItem = new ToolStripMenuItem("Apply to target");
                applyItem.Click += (s, e) =>
                {
                    // If text is selected, apply just the selection (plain text)
                    string textToApply;
                    if (_rtb != null && _rtb.SelectionLength > 0)
                        textToApply = _rtb.SelectedText;
                    else
                        textToApply = _plainContent;

                    if (!string.IsNullOrEmpty(textToApply))
                        ApplyRequested?.Invoke(this, textToApply);
                };
                menu.Items.Add(applyItem);

                var savePromptItem = new ToolStripMenuItem("Save as Prompt\u2026");
                savePromptItem.Click += (s, e) =>
                {
                    // Use FullMarkdownContent when available – same as Copy – so that
                    // truncated bubbles save the complete prompt, not just the display excerpt.
                    var fullText = FullMarkdownContent ?? _markdownContent;
                    var textToSave = string.IsNullOrEmpty(fullText)
                        ? _plainContent
                        : MarkdownToRtf.StripMarkdown(fullText);
                    if (!string.IsNullOrEmpty(textToSave))
                        SaveAsPromptRequested?.Invoke(this, textToSave);
                };
                menu.Items.Add(savePromptItem);

                var saveToKbItem = new ToolStripMenuItem("Save to memory bank");
                saveToKbItem.Click += (s, e) =>
                {
                    var fullText = FullMarkdownContent ?? _markdownContent;
                    var textToSave = string.IsNullOrEmpty(fullText)
                        ? _plainContent
                        : MarkdownToRtf.StripMarkdown(fullText);
                    if (!string.IsNullOrEmpty(textToSave))
                        SaveToMemoryBankRequested?.Invoke(this, textToSave);
                };
                menu.Items.Add(saveToKbItem);
            }

            // Apply context menu to both the bubble and the RTB
            ContextMenuStrip = menu;
            _rtb.ContextMenuStrip = menu;
        }

        /// <summary>
        /// Shows a full-size image in a simple modal dialog.
        /// </summary>
        private void ShowFullImage(ImageAttachment imgAttachment)
        {
            try
            {
                using (var ms = new MemoryStream(imgAttachment.Data))
                using (var img = Image.FromStream(ms))
                {
                    var dlg = new Form
                    {
                        Text = imgAttachment.FileName ?? "Image",
                        StartPosition = FormStartPosition.CenterParent,
                        FormBorderStyle = FormBorderStyle.Sizable,
                        BackColor = Color.White,
                        ClientSize = new Size(
                            Math.Min(img.Width + 20, 800),
                            Math.Min(img.Height + 20, 600)),
                        MaximizeBox = true,
                        MinimizeBox = false
                    };

                    var picBox = new PictureBox
                    {
                        Dock = DockStyle.Fill,
                        SizeMode = PictureBoxSizeMode.Zoom,
                        Image = (Image)img.Clone() // clone so we can dispose the stream
                    };
                    dlg.Controls.Add(picBox);
                    dlg.ShowDialog(FindForm());
                    picBox.Image?.Dispose();
                }
            }
            catch { /* Image display error */ }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _rtb?.Dispose();
                _messageFont?.Dispose();
                _timestampFont?.Dispose();
                _avatarFont?.Dispose();
                _nameFont?.Dispose();
                foreach (var picBox in _imageThumbs)
                {
                    picBox.Image?.Dispose();
                    picBox.Dispose();
                }
                _imageThumbs.Clear();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// RichTextBox subclass that intercepts Ctrl+C and Ctrl+A before
    /// Trados's global shortcut system can steal them.
    /// </summary>
    internal class ChatRichTextBox : RichTextBox
    {
    }

}
