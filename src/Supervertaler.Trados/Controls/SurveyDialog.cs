using System;
using System.Drawing;
using System.Windows.Forms;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// A tiny, easily-ignorable dev-survey dialog (issue #43). Two modes:
    ///
    ///   "yesno" — the question with Yes/No buttons + an optional comment box.
    ///   "open"  — no buttons; a large free-text box IS the answer, with a Send
    ///             button (for questions that aren't answerable Yes/No).
    ///
    /// Closing without answering is fine — the copy says so, and an unanswered
    /// close leaves Answer = "ignored".
    ///
    /// Read after ShowDialog():
    ///   Answer        — "yes", "no" (yesno mode), "answered" (open mode), or "ignored"
    ///   Comment       — free text (trimmed); the answer itself in open mode
    ///   DontAskAgain  — the user ticked "Don't ask again"
    /// </summary>
    internal sealed class SurveyDialog : Form
    {
        public string Answer { get; private set; } = "ignored";
        public string Comment => (_txtComment.Text ?? "").Trim();
        public bool DontAskAgain => _chkDontAsk.Checked;

        private readonly TextBox _txtComment;
        private readonly CheckBox _chkDontAsk;

        public SurveyDialog(string question, string yesLabel, string noLabel, string kind = "yesno")
        {
            bool isOpen = (kind == "open");

            Icon = Supervertaler.Trados.Core.IconHelper.AppIcon;
            // Let WinForms scale by system DPI so the dialog doesn't squish at
            // >100% Windows display scaling (same approach as UsageStatisticsDialog).
            AutoScaleMode = AutoScaleMode.Dpi;
            SuspendLayout();

            Text = "Supervertaler for Trados";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9F);
            KeyPreview = true;

            // Esc closes as an ignore (does not count as an answer).
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    Answer = "ignored";
                    DialogResult = DialogResult.Cancel;
                }
            };

            var lblIntro = new Label
            {
                Text = "Sorry to bother you – a quick question about Supervertaler development.",
                Location = new Point(20, 16),
                Size = new Size(430, 34),
                ForeColor = Color.FromArgb(90, 90, 90)
            };

            var lblQuestion = new Label
            {
                Text = question ?? "",
                Location = new Point(20, 54),
                Size = new Size(430, 56),
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                ForeColor = Color.FromArgb(30, 30, 30)
            };

            var lblComment = new Label
            {
                Text = isOpen ? "Your answer:" : "Anything to add? (optional)",
                ForeColor = Color.FromArgb(90, 90, 90)
            };

            _txtComment = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                AcceptsReturn = true
            };

            _chkDontAsk = new CheckBox
            {
                Text = "Don't ask again",
                AutoSize = true,
                ForeColor = Color.FromArgb(90, 90, 90)
            };

            var lblIgnore = new Label
            {
                Text = "Feel free to just ignore this and close it.",
                AutoSize = true,
                ForeColor = Color.FromArgb(150, 150, 150)
            };

            if (isOpen)
            {
                // Open mode: the big text box is the answer; a Send button submits.
                lblComment.Location = new Point(20, 118);
                lblComment.AutoSize = true;
                _txtComment.Location = new Point(20, 138);
                _txtComment.Size = new Size(430, 96);

                var btnSend = new Button
                {
                    Text = "Send",
                    Location = new Point(20, 244),
                    Size = new Size(150, 32),
                    FlatStyle = FlatStyle.System
                };
                btnSend.Click += (s, e) =>
                {
                    // Only counts as an answer if they actually wrote something.
                    Answer = string.IsNullOrEmpty((_txtComment.Text ?? "").Trim()) ? "ignored" : "answered";
                    DialogResult = DialogResult.OK;
                };

                _chkDontAsk.Location = new Point(20, 288);
                lblIgnore.Location = new Point(180, 289);

                ClientSize = new Size(470, 332);
                Controls.AddRange(new Control[]
                {
                    lblIntro, lblQuestion, lblComment, _txtComment, btnSend, _chkDontAsk, lblIgnore
                });
            }
            else
            {
                // Yes/No mode: buttons, then an optional comment box.
                var btnYes = new Button
                {
                    Text = string.IsNullOrEmpty(yesLabel) ? "Yes" : yesLabel,
                    Location = new Point(20, 118),
                    Size = new Size(150, 32),
                    FlatStyle = FlatStyle.System
                };
                btnYes.Click += (s, e) => { Answer = "yes"; DialogResult = DialogResult.OK; };

                var btnNo = new Button
                {
                    Text = string.IsNullOrEmpty(noLabel) ? "No" : noLabel,
                    Location = new Point(180, 118),
                    Size = new Size(150, 32),
                    FlatStyle = FlatStyle.System
                };
                btnNo.Click += (s, e) => { Answer = "no"; DialogResult = DialogResult.OK; };

                lblComment.Location = new Point(20, 162);
                lblComment.AutoSize = true;
                _txtComment.Location = new Point(20, 182);
                _txtComment.Size = new Size(430, 72);

                _chkDontAsk.Location = new Point(20, 272);
                lblIgnore.Location = new Point(160, 273);

                ClientSize = new Size(470, 316);
                Controls.AddRange(new Control[]
                {
                    lblIntro, lblQuestion, btnYes, btnNo, lblComment, _txtComment, _chkDontAsk, lblIgnore
                });
            }

            ResumeLayout(false);
            PerformLayout();
        }
    }
}
