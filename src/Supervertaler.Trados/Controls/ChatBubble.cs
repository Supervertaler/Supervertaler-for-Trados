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
        private static readonly Font MessageFont = new Font("Segoe UI", 9f);
        private static readonly Font TimestampFont = new Font("Segoe UI", 7f);

        private const int BubblePadding = 10;
        private const int BubbleRadius = 8;
        private const int TimestampHeight = 14;
        private const int HorizontalMargin = 8;
        private const int AvatarSize = 18;
        private const int AvatarHeaderHeight = 22;
        private const int ImageThumbMaxWidth = 200;
        private const int ImageThumbMaxHeight = 150;
        private const int ImageSpacing = 4;

        private static readonly Color UserAvatarBg = ColorTranslator.FromHtml("#4A90D9");
        private static readonly Color AssistantAvatarBg = ColorTranslator.FromHtml("#6B7280");
        private static readonly Font AvatarFont = new Font("Segoe UI", 7f, FontStyle.Bold);
        private static readonly Font NameFont = new Font("Segoe UI", 7.5f);

        private readonly ChatMessage _message;
        private readonly bool _isUser;
        private readonly string _timestampText;
        private readonly string _plainContent;
        private readonly RichTextBox _rtb;
        private readonly List<PictureBox> _imageThumbs = new List<PictureBox>();

        private int _bubbleWidth;
        private int _bubbleHeight;
        private int _imageAreaHeight;
        private Rectangle _bubbleRect;

        /// <summary>Raised when user clicks "Apply to target" on an assistant bubble.</summary>
        public event EventHandler<string> ApplyRequested;

        /// <summary>Raised when user clicks "Save as Prompt" on an assistant bubble.</summary>
        public event EventHandler<string> SaveAsPromptRequested;

        public ChatBubble(ChatMessage message, int maxWidth)
        {
            _message = message;
            _isUser = message.Role == ChatRole.User;
            _timestampText = message.Timestamp.ToString("HH:mm");

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

            // Prepare plain content (for copy/apply)
            _plainContent = _isUser || hasDisplayOverride
                ? userDisplayText
                : MarkdownToRtf.StripMarkdown(message.Content ?? "");

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
            _rtb = new RichTextBox
            {
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                ScrollBars = RichTextBoxScrollBars.None,
                BackColor = _isUser ? UserBg : AssistantBg,
                ForeColor = TextColor,
                Font = MessageFont,
                Cursor = Cursors.Default,
                DetectUrls = false,
                WordWrap = true,
                TabStop = false,
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

            _bubbleWidth = maxBubble;
            _bubbleHeight = _imageAreaHeight + contentHeight + BubblePadding * 2 + TimestampHeight;

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

            Size = new Size(maxWidth, _bubbleHeight + AvatarHeaderHeight + 8);
        }

        private int MeasureRtbHeight(int width)
        {
            _rtb.Width = width;
            _rtb.Height = 100000; // allow full layout

            if (_rtb.TextLength == 0)
                return (int)Math.Ceiling(MessageFont.GetHeight()) + 4;

            var pos = _rtb.GetPositionFromCharIndex(_rtb.TextLength - 1);
            var lineHeight = (int)Math.Ceiling(MessageFont.GetHeight() * 1.3f);
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
            var nameText = _isUser ? "You" : "Supervertaler Assistant";
            var avatarX = _bubbleRect.X;
            var avatarY = _bubbleRect.Y - AvatarHeaderHeight;

            // If user bubble is right-aligned, right-align the avatar too
            if (_isUser)
                avatarX = _bubbleRect.Right - AvatarSize
                    - (int)g.MeasureString(nameText, NameFont).Width - 6;

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
                var avatarTextSize = g.MeasureString(avatarText, AvatarFont);
                g.DrawString(avatarText, AvatarFont, Brushes.White,
                    avatarX + (AvatarSize - avatarTextSize.Width) / 2f,
                    avatarY + (AvatarSize - avatarTextSize.Height) / 2f);
            }

            // Name label next to circle
            g.DrawString(nameText, NameFont, new SolidBrush(Color.FromArgb(100, 100, 100)),
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

            TextRenderer.DrawText(g, _timestampText, TimestampFont,
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
                else if (!string.IsNullOrEmpty(_plainContent))
                {
                    Clipboard.SetText(_plainContent);
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
                    var textToSave = _plainContent;
                    if (!string.IsNullOrEmpty(textToSave))
                        SaveAsPromptRequested?.Invoke(this, textToSave);
                };
                menu.Items.Add(savePromptItem);
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
}
