using System;
using System.Drawing;
using System.Windows.Forms;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// A small top-level progress window for the bilingual re-import writeback.
    /// The writeback runs on the UI thread (Trados' editor is thread-affine, so
    /// each <c>ProcessSegmentPair</c> must run there), which used to freeze the
    /// whole editor for the duration of a large multi-file import. This modeless
    /// window gives a determinate "segment N of M" bar plus a Cancel button; the
    /// writeback loop pumps it (via Application.DoEvents) every few segments so it
    /// stays responsive and the user can stop a long run.
    /// </summary>
    internal class ReimportProgressForm : Form
    {
        private readonly Label _status;
        private readonly ProgressBar _bar;
        private readonly Label _detail;
        private readonly Button _cancel;

        /// <summary>Raised when the user clicks Cancel.</summary>
        public event EventHandler CancelRequested;

        public ReimportProgressForm(string fileName, int total)
        {
            Text = "Re-import bilingual file";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;      // no X – finish via Cancel or completion
            ShowInTaskbar = false;
            TopMost = true;
            ClientSize = new Size(460, 150);

            _status = new Label
            {
                Text = "Applying changes to " + fileName + "…",
                Location = new Point(14, 16),
                AutoSize = false,
                Size = new Size(432, 18),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            };
            _bar = new ProgressBar
            {
                Location = new Point(14, 44),
                Size = new Size(432, 20),
                Style = ProgressBarStyle.Continuous,
                Minimum = 0,
                Maximum = Math.Max(1, total),
                Value = 0,
            };
            _detail = new Label
            {
                Text = "Writing translations back into Trados. Please wait…",
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
                _status.Text = "Cancelling — finishing the current segment…";
                CancelRequested?.Invoke(this, EventArgs.Empty);
            };

            Controls.Add(_status);
            Controls.Add(_bar);
            Controls.Add(_detail);
            Controls.Add(_cancel);
        }

        /// <summary>Thread-safe: advance the bar and status line.</summary>
        public void SetProgress(int value, string status)
        {
            if (InvokeRequired) { try { BeginInvoke(new Action(() => SetProgress(value, status))); } catch { } return; }
            try { _bar.Value = Math.Max(_bar.Minimum, Math.Min(value, _bar.Maximum)); } catch { }
            if (!string.IsNullOrEmpty(status)) _status.Text = status;
        }

        /// <summary>Thread-safe: replace the grey detail line.</summary>
        public void SetDetail(string text)
        {
            if (InvokeRequired) { try { BeginInvoke(new Action(() => SetDetail(text))); } catch { } return; }
            _detail.Text = text ?? "";
        }

        /// <summary>Thread-safe: close the window.</summary>
        public void Finish()
        {
            if (InvokeRequired) { try { BeginInvoke(new Action(Finish)); } catch { } return; }
            try { Close(); } catch { }
        }
    }
}
