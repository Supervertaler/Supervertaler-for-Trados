using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Supervertaler.Trados.Settings
{
    /// <summary>
    /// Runs the data folder copy off the UI thread and shows its progress, with
    /// Cancel. Closes itself when the copy ends; read <see cref="Result"/> or
    /// <see cref="Error"/> afterwards. Laid out by a TableLayoutPanel, like
    /// SurveyDialog: no computed coordinates.
    /// </summary>
    internal sealed class DataFolderMoveForm : Form
    {
        private const int ContentWidth = 420;

        private readonly Func<IProgress<string>, CancellationToken, DataFolderMover.Result> _work;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Label _status;
        private readonly Button _cancel;
        private bool _running;

        public DataFolderMover.Result Result { get; private set; }
        public Exception Error { get; private set; }

        public DataFolderMoveForm(string to, Func<IProgress<string>, CancellationToken, DataFolderMover.Result> work)
        {
            _work = work;

            Icon = Supervertaler.Trados.Core.IconHelper.AppIcon;
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Move data folder";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;   // finish via Cancel or completion
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9F);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var root = new TableLayoutPanel
            {
                ColumnCount = 1,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Padding = new Padding(20, 16, 20, 16),
                GrowStyle = TableLayoutPanelGrowStyle.AddRows
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ContentWidth));

            root.Controls.Add(new Label
            {
                Text = "Copying your data folder to " + to,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                AutoSize = true,
                MaximumSize = new Size(ContentWidth, 0),
                Margin = new Padding(0, 0, 0, 10)
            });
            root.Controls.Add(new ProgressBar
            {
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 30,
                Width = ContentWidth,
                Margin = new Padding(0, 0, 0, 8)
            });
            _status = new Label
            {
                Text = "Looking at what to copy…",
                AutoSize = true,
                MinimumSize = new Size(ContentWidth, 0),   // the count changes; the window should not
                MaximumSize = new Size(ContentWidth, 0),
                ForeColor = Color.DimGray,
                Margin = new Padding(0, 0, 0, 12)
            };
            root.Controls.Add(_status);

            _cancel = new Button
            {
                Text = "Cancel",
                AutoSize = true,
                FlatStyle = FlatStyle.System,
                Anchor = AnchorStyles.Right,
                Padding = new Padding(8, 2, 8, 2)
            };
            _cancel.Click += (s, e) => RequestCancel();
            root.Controls.Add(_cancel);

            Controls.Add(root);

            Shown += (s, e) => Start();
            // Alt+F4 while copying cancels instead of abandoning a half-made copy.
            FormClosing += (s, e) => { if (_running) { e.Cancel = true; RequestCancel(); } };
        }

        private void Start()
        {
            _running = true;
            var progress = new UiProgress(this);
            Task.Run(() => _work(progress, _cts.Token)).ContinueWith(t =>
            {
                try { BeginInvoke(new Action(() => Finish(t))); } catch { /* window already gone */ }
            });
        }

        private void Finish(Task<DataFolderMover.Result> t)
        {
            _running = false;
            if (t.IsCanceled) Error = new OperationCanceledException();
            else if (t.IsFaulted) Error = t.Exception.GetBaseException();
            else Result = t.Result;
            Close();
        }

        private void RequestCancel()
        {
            if (_cts.IsCancellationRequested) return;
            _cts.Cancel();
            _cancel.Enabled = false;
            _status.Text = "Cancelling – removing the partial copy…";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _cts.Dispose();
            base.Dispose(disposing);
        }

        /// <summary>Marshals progress text to the window; Progress&lt;T&gt; would need a
        /// SynchronizationContext on the thread that created it.</summary>
        private sealed class UiProgress : IProgress<string>
        {
            private readonly DataFolderMoveForm _form;
            public UiProgress(DataFolderMoveForm form) { _form = form; }

            public void Report(string value)
            {
                try
                {
                    _form.BeginInvoke(new Action(() =>
                    {
                        if (!_form._cts.IsCancellationRequested) _form._status.Text = value;
                    }));
                }
                catch { /* window closing */ }
            }
        }
    }
}
