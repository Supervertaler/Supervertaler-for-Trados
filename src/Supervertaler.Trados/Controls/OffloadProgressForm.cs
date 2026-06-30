using System;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// A small top-level progress window for the "Translate via Workbench" offload.
    /// It is a separate top-level form (not docked in the editor) because the editor
    /// – and the Batch Operations log inside it – is CLOSED while the offload runs,
    /// so any in-pane progress would be invisible. Updated from the engine's stdout
    /// (one line per batch); shows a determinate bar once "[batch n/N]" is seen.
    /// </summary>
    internal class OffloadProgressForm : Form
    {
        private readonly Label _status;
        private readonly ProgressBar _bar;
        private readonly Label _detail;
        private readonly Button _cancel;

        /// <summary>Raised when the user clicks Cancel (cancel the engine + reopen).</summary>
        public event EventHandler CancelRequested;

        public OffloadProgressForm(string fileName)
        {
            Text = "Translate via Workbench";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;      // no X – finish via Cancel or completion
            ShowInTaskbar = true;
            TopMost = true;
            ClientSize = new Size(460, 150);

            _status = new Label
            {
                Text = "Translating " + fileName + " in the 64-bit Workbench…",
                Location = new Point(14, 16),
                AutoSize = false,
                Size = new Size(432, 18),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            };
            _bar = new ProgressBar
            {
                Location = new Point(14, 44),
                Size = new Size(432, 20),
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 30,
            };
            _detail = new Label
            {
                Text = "The document is closed while it translates; it reopens automatically when done.",
                Location = new Point(14, 72),
                AutoSize = false,
                Size = new Size(432, 30),
                ForeColor = Color.Gray,
                Font = new Font("Segoe UI", 8f),
            };
            _cancel = new Button
            {
                Text = "Cancel",
                Location = new Point(366, 112),
                Size = new Size(80, 26),
                FlatStyle = FlatStyle.System,
            };
            _cancel.Click += (s, e) =>
            {
                _cancel.Enabled = false;
                _status.Text = "Cancelling…";
                CancelRequested?.Invoke(this, EventArgs.Empty);
            };

            Controls.Add(_status);
            Controls.Add(_bar);
            Controls.Add(_detail);
            Controls.Add(_cancel);
        }

        /// <summary>Thread-safe: feed it an engine stdout line.</summary>
        public void UpdateProgress(string line)
        {
            if (InvokeRequired) { try { BeginInvoke(new Action(() => UpdateProgress(line))); } catch { } return; }
            if (string.IsNullOrWhiteSpace(line)) return;
            var m = Regex.Match(line, @"batch\s+(\d+)\s*/\s*(\d+)", RegexOptions.IgnoreCase);
            if (m.Success
                && int.TryParse(m.Groups[1].Value, out int n)
                && int.TryParse(m.Groups[2].Value, out int total) && total > 0)
            {
                _bar.Style = ProgressBarStyle.Continuous;
                _bar.Maximum = total;
                _bar.Value = Math.Max(0, Math.Min(n, total));
                _status.Text = $"Translating… batch {n} of {total}";
            }
            _detail.Text = line;
        }

        /// <summary>Thread-safe: set the status line (e.g. "Applying & reopening…").</summary>
        public void SetStatus(string text)
        {
            if (InvokeRequired) { try { BeginInvoke(new Action(() => SetStatus(text))); } catch { } return; }
            _status.Text = text;
        }

        /// <summary>Thread-safe: close the window.</summary>
        public void Finish()
        {
            if (InvokeRequired) { try { BeginInvoke(new Action(Finish)); } catch { } return; }
            try { Close(); } catch { }
        }
    }
}
