using System;
using System.Drawing;
using System.Windows.Forms;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// Read-only dialog showing exactly what will be sent to the AI for a Batch
    /// Translate or Batch Proofread call: assembled system prompt (incl. termbase,
    /// language-specific checks, full bilingual document context for proofread,
    /// and the active custom prompt) plus the numbered segment list.
    ///
    /// Triggered by the "Preview prompt" link in BatchTranslateControl. Works in
    /// both API and Clipboard mode and does not actually call the LLM – useful
    /// for inspecting before incurring cost or for debugging unexpected output.
    ///
    /// Bonus button copies the same text to the clipboard, so the dialog can
    /// double as a "manual paste into web LLM" path even when not in Clipboard
    /// Mode.
    /// </summary>
    public class PromptPreviewDialog : Form
    {
        private TextBox _txt;
        private Button _btnCopy;
        private Button _btnClose;
        private Label _lblHeader;

        public PromptPreviewDialog(string title, string contentLabel, string content)
        {
            Icon = Supervertaler.Trados.Core.IconHelper.AppIcon;
            BuildUI(title, contentLabel, content);
        }

        private void BuildUI(string title, string contentLabel, string content)
        {
            // Let WinForms scale this dialog by system DPI so it doesn't squish
            // at >100% Windows display scaling. Cheap fallback; for surfaces
            // with their own UiScale-driven layout, set AutoScaleMode = None
            // instead and let UiScale own scaling.
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = title ?? "Prompt preview";
            Size = new Size(900, 700);
            MinimumSize = new Size(500, 400);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9f);
            BackColor = Color.White;

            _lblHeader = new Label
            {
                Dock = DockStyle.Top,
                // Three lines: the banner now carries which request this is out of
                // how many, and that the document context is repeated in each. That
                // used to be a footnote 90,000 characters below, where it was read
                // by nobody.
                Height = 64,
                Padding = new Padding(12, 10, 12, 0),
                Text = contentLabel ?? "This is exactly what will be sent to the AI for this batch.",
                Font = new Font("Segoe UI", 9f, FontStyle.Italic),
                ForeColor = Color.FromArgb(80, 80, 80),
                AutoSize = false,
                TextAlign = ContentAlignment.TopLeft
            };

            // Multi-line read-only TextBox – simpler than RichTextBox, handles huge
            // text well, gives us monospace font for prompt readability, lets the
            // user select-all + copy via Ctrl+A / Ctrl+C natively.
            _txt = new TextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                // Wrapped. Without it every long paragraph - and a prompt is mostly
                // long paragraphs - runs off the right edge, so reading one means
                // scrolling horizontally, line by line. On a 90,000-character
                // preview that is how a reader concludes something is missing when
                // it is only past the edge, or further down than they scrolled.
                WordWrap = true,
                Font = new Font("Consolas", 9.5f),
                BackColor = Color.White,
                Margin = new Padding(8),
                Text = content ?? "(no prompt to preview)"
            };

            var pnlButtons = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 48,
                Padding = new Padding(8, 8, 8, 8)
            };

            _btnCopy = new Button
            {
                Text = "📋  Copy to clipboard",
                Size = new Size(170, 28),
                Location = new Point(8, 10),
                FlatStyle = FlatStyle.System
            };
            _btnCopy.Click += (s, e) =>
            {
                try
                {
                    Clipboard.SetText(_txt.Text);
                    _btnCopy.Text = "✓  Copied";
                    var t = new Timer { Interval = 1500 };
                    t.Tick += (s2, e2) =>
                    {
                        _btnCopy.Text = "📋  Copy to clipboard";
                        t.Stop();
                        t.Dispose();
                    };
                    t.Start();
                }
                catch { /* clipboard may be locked – swallow */ }
            };

            _btnClose = new Button
            {
                Text = "Close",
                Size = new Size(90, 28),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(pnlButtons.Width - 100, 10),
                FlatStyle = FlatStyle.System,
                DialogResult = DialogResult.OK
            };

            pnlButtons.Controls.Add(_btnCopy);
            pnlButtons.Controls.Add(_btnClose);

            Controls.Add(_txt);
            Controls.Add(_lblHeader);
            Controls.Add(pnlButtons);

            AcceptButton = _btnClose;
            CancelButton = _btnClose;
        }
    }
}
