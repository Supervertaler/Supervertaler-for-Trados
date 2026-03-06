using System;
using System.Drawing;
using System.Windows.Forms;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// WinForms UserControl for the Batch Translate tab.
    /// Displays scope selector, provider info, progress, and translation log.
    /// All layout is programmatic (no designer file).
    /// </summary>
    public class BatchTranslateControl : UserControl
    {
        // Header
        private Label _lblHeader;

        // Configuration
        private ComboBox _cmbScope;
        private Label _lblProvider;
        private Label _lblSegmentCount;

        // Progress
        private ProgressBar _progressBar;
        private Label _lblProgress;

        // Action
        private Button _btnTranslate;

        // Log
        private TextBox _txtLog;

        // State
        private bool _isRunning;

        /// <summary>Fired when user clicks "Translate".</summary>
        public event EventHandler TranslateRequested;

        /// <summary>Fired when user clicks "Stop".</summary>
        public event EventHandler StopRequested;

        /// <summary>Fired when user changes the scope dropdown.</summary>
        public event EventHandler ScopeChanged;

        public BatchTranslateControl()
        {
            BuildUI();
        }

        private void BuildUI()
        {
            SuspendLayout();
            BackColor = Color.White;
            AutoScroll = false;

            var labelColor = Color.FromArgb(80, 80, 80);
            var headerFont = new Font("Segoe UI", 9f, FontStyle.Bold);
            var bodyFont = new Font("Segoe UI", 8.5f);
            var logFont = new Font("Consolas", 8f);

            var y = 10;

            // ─── Header ────────────────────────────────────────
            _lblHeader = new Label
            {
                Text = "Batch Translate",
                Font = headerFont,
                ForeColor = Color.FromArgb(50, 50, 50),
                Location = new Point(12, y),
                AutoSize = true
            };
            Controls.Add(_lblHeader);
            y += 26;

            // ─── Scope ─────────────────────────────────────────
            var lblScope = new Label
            {
                Text = "Scope:",
                Location = new Point(12, y + 3),
                AutoSize = true,
                Font = bodyFont,
                ForeColor = labelColor
            };
            _cmbScope = new ComboBox
            {
                Location = new Point(100, y),
                Width = 200,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = bodyFont
            };
            _cmbScope.Items.Add("Empty segments only");
            _cmbScope.Items.Add("All segments");
            _cmbScope.Items.Add("Filtered segments");
            _cmbScope.Items.Add("Filtered (empty only)");
            _cmbScope.SelectedIndex = 0;
            _cmbScope.SelectedIndexChanged += (s, e) => ScopeChanged?.Invoke(this, EventArgs.Empty);
            Controls.Add(lblScope);
            Controls.Add(_cmbScope);
            y += 28;

            // ─── Provider ───────────────────────────────────────
            var lblProviderLabel = new Label
            {
                Text = "Provider:",
                Location = new Point(12, y + 1),
                AutoSize = true,
                Font = bodyFont,
                ForeColor = labelColor
            };
            _lblProvider = new Label
            {
                Text = "Not configured",
                Location = new Point(100, y + 1),
                AutoSize = true,
                Font = bodyFont,
                ForeColor = Color.FromArgb(50, 50, 50)
            };
            Controls.Add(lblProviderLabel);
            Controls.Add(_lblProvider);
            y += 22;

            // ─── Segment count ──────────────────────────────────
            _lblSegmentCount = new Label
            {
                Text = "Segments: \u2014",
                Location = new Point(12, y + 1),
                AutoSize = true,
                Font = bodyFont,
                ForeColor = Color.FromArgb(100, 100, 100)
            };
            Controls.Add(_lblSegmentCount);
            y += 28;

            // ─── Progress bar ───────────────────────────────────
            _progressBar = new ProgressBar
            {
                Location = new Point(12, y),
                Height = 18,
                Width = 320,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _lblProgress = new Label
            {
                Text = "",
                Location = new Point(340, y + 1),
                AutoSize = true,
                Font = new Font("Segoe UI", 7.5f),
                ForeColor = Color.FromArgb(100, 100, 100),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            Controls.Add(_progressBar);
            Controls.Add(_lblProgress);
            y += 28;

            // ─── Translate / Stop button ────────────────────────
            _btnTranslate = new Button
            {
                Text = "\u25B6  Translate",
                Location = new Point(12, y),
                Width = 120,
                Height = 28,
                FlatStyle = FlatStyle.System,
                Font = bodyFont
            };
            _btnTranslate.Click += OnTranslateClick;
            Controls.Add(_btnTranslate);
            y += 38;

            // ─── Log ────────────────────────────────────────────
            var lblLog = new Label
            {
                Text = "Log:",
                Location = new Point(12, y),
                AutoSize = true,
                Font = bodyFont,
                ForeColor = labelColor
            };
            Controls.Add(lblLog);
            y += 18;

            _txtLog = new TextBox
            {
                Location = new Point(12, y),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = logFont,
                BackColor = Color.FromArgb(248, 248, 248),
                ForeColor = Color.FromArgb(60, 60, 60),
                BorderStyle = BorderStyle.FixedSingle,
                WordWrap = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };
            Controls.Add(_txtLog);

            ResumeLayout(false);

            // Handle resize for responsive layout
            Resize += OnResize;
            OnResize(this, EventArgs.Empty);
        }

        private void OnResize(object sender, EventArgs e)
        {
            if (_txtLog == null) return;
            var w = Width - 24;
            _txtLog.Width = Math.Max(100, w);
            _txtLog.Height = Math.Max(40, Height - _txtLog.Top - 8);

            _progressBar.Width = Math.Max(100, w - 80);
            _lblProgress.Location = new Point(_progressBar.Right + 8, _lblProgress.Top);
        }

        // ─── Event Handlers ──────────────────────────────────────

        private void OnTranslateClick(object sender, EventArgs e)
        {
            if (_isRunning)
                StopRequested?.Invoke(this, EventArgs.Empty);
            else
                TranslateRequested?.Invoke(this, EventArgs.Empty);
        }

        // ─── Public Methods (called by ViewPart) ─────────────────

        /// <summary>
        /// Updates the displayed provider and model name.
        /// </summary>
        public void UpdateProviderDisplay(string providerName, string modelName)
        {
            _lblProvider.Text = providerName + " / " + modelName;
        }

        /// <summary>
        /// Updates the segment count display.
        /// </summary>
        public void UpdateSegmentCounts(int emptyCount, int totalCount, int filteredCount = -1)
        {
            var scope = GetSelectedScope();
            if ((scope == BatchScope.Filtered || scope == BatchScope.FilteredEmptyOnly) && filteredCount >= 0)
                _lblSegmentCount.Text = $"Segments: {filteredCount} filtered / {emptyCount} empty / {totalCount} total";
            else
                _lblSegmentCount.Text = $"Segments: {emptyCount} empty / {totalCount} total";
            UpdateTranslateButton();
        }

        /// <summary>
        /// Returns the selected batch scope.
        /// </summary>
        public BatchScope GetSelectedScope()
        {
            switch (_cmbScope.SelectedIndex)
            {
                case 1: return BatchScope.All;
                case 2: return BatchScope.Filtered;
                case 3: return BatchScope.FilteredEmptyOnly;
                default: return BatchScope.EmptyOnly;
            }
        }

        /// <summary>
        /// Reports progress from the batch translator.
        /// </summary>
        public void ReportProgress(int current, int total, string message, bool isError)
        {
            if (total > 0)
            {
                _progressBar.Maximum = total;
                _progressBar.Value = Math.Min(current, total);
                _lblProgress.Text = $"{current}/{total}";
            }

            if (!string.IsNullOrEmpty(message))
                AppendLog(message, isError);
        }

        /// <summary>
        /// Reports batch translation completion.
        /// </summary>
        public void ReportCompleted(int translated, int failed, int skipped,
            TimeSpan elapsed, bool cancelled)
        {
            SetRunning(false);

            var status = cancelled ? "Cancelled" : "Complete";
            AppendLog(
                $"\u2014 {status}: {translated} translated, {failed} failed " +
                $"({elapsed.TotalSeconds:F1}s)",
                false);
        }

        /// <summary>
        /// Toggles the UI between running and idle states.
        /// </summary>
        public void SetRunning(bool running)
        {
            _isRunning = running;
            _btnTranslate.Text = running ? "\u25A0  Stop" : "\u25B6  Translate";
            _cmbScope.Enabled = !running;

            if (!running)
            {
                _progressBar.Value = 0;
                _lblProgress.Text = "";
            }
        }

        /// <summary>
        /// Appends a timestamped line to the log.
        /// </summary>
        public void AppendLog(string message, bool isError = false)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var prefix = isError ? "\u2717 " : "";
            var line = timestamp + "  " + prefix + message + Environment.NewLine;

            _txtLog.AppendText(line);
            // Auto-scroll to bottom
            _txtLog.SelectionStart = _txtLog.TextLength;
            _txtLog.ScrollToCaret();
        }

        /// <summary>
        /// Resets the control state (e.g., when document changes).
        /// </summary>
        public void Reset()
        {
            _progressBar.Value = 0;
            _lblProgress.Text = "";
            _lblSegmentCount.Text = "Segments: \u2014";
            SetRunning(false);
        }

        private void UpdateTranslateButton()
        {
            // Disable translate button if there are no segments
            // (actual logic depends on document state; the ViewPart calls this)
        }
    }
}
