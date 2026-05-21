using System;
using System.Drawing;
using System.Windows.Forms;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// Thin toolbar strip for SuperMemory operations.
    /// Sits below the context strip in the Chat tab.
    /// Two buttons: Process Inbox and Health Check, plus an inbox count label.
    /// </summary>
    public class SuperMemoryToolbar : Panel
    {
        private Label _lblHeading;
        private ComboBox _cmbMemoryBank;
        private LinkLabel _lnkHelp;
        private Button _btnProcessInbox;
        private Button _btnHealthCheck;
        private Button _btnDistill;
        private Button _btnOverview;
        private Button _btnSummary;
        private Button _btnRefresh;
        private Label _lblInboxCount;

        /// <summary>
        /// Suppresses <see cref="MemoryBankChanged"/> while the dropdown is being
        /// populated programmatically. Mirrors the <c>_suppress_combo_change</c>
        /// flag in the Python Supervertaler Assistant.
        /// </summary>
        private bool _suppressComboChange;

        /// <summary>Raised when the user clicks "Process Inbox".</summary>
        public event EventHandler ProcessInboxRequested;

        /// <summary>Raised when the user clicks "Health Check".</summary>
        public event EventHandler HealthCheckRequested;

        /// <summary>Raised when the user clicks "Distill".</summary>
        public event EventHandler DistillRequested;

        /// <summary>Raised when the user clicks "Overview" (generate HTML overview).</summary>
        public event EventHandler OverviewRequested;

        /// <summary>Raised when the user clicks "Summary" (AI natural-language summary).</summary>
        public event EventHandler AiSummaryRequested;

        /// <summary>Raised when the user clicks the refresh button.</summary>
        public event EventHandler RefreshRequested;

        /// <summary>
        /// Raised when the user picks a different memory bank from the dropdown.
        /// Suppressed while <see cref="SetMemoryBanks"/> is repopulating the list.
        /// </summary>
        public event EventHandler<MemoryBankChangedEventArgs> MemoryBankChanged;

        /// <summary>
        /// Raised when the user picks the "+ New memory bank…" sentinel entry at
        /// the end of the dropdown. The parent view part is expected to prompt
        /// the user for a name, create the bank on disk, and then call
        /// <see cref="SetMemoryBanks"/> to repopulate the combo with the new
        /// bank selected. The dropdown reverts its own selection back to the
        /// previously active bank before firing this event so the sentinel
        /// never appears as the "current" selection.
        /// </summary>
        public event EventHandler NewMemoryBankRequested;

        /// <summary>
        /// Sentinel display text for the "create new bank" entry. Kept as a
        /// constant so the change handler can reliably recognise the row
        /// without depending on string literals sprinkled through the file.
        /// </summary>
        private const string NewBankSentinel = "+ New memory bank\u2026"; // ellipsis

        public SuperMemoryToolbar()
        {
            BuildUI();
        }

        /// <summary>Creates a flat text button styled like the other toolbar actions.</summary>
        private Button MakeActionButton(string text, Font font)
        {
            var b = new Button
            {
                Text = text,
                Font = font,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.FromArgb(30, 90, 158),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                AutoSize = true,
                Padding = new Padding(UiScale.Pixels(4), 0, UiScale.Pixels(4), 0),
                Height = UiScale.Pixels(24),
                TabStop = false,
                UseCompatibleTextRendering = true
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 232, 245);
            return b;
        }

        private void BuildUI()
        {
            Height = UiScale.Pixels(32);
            Dock = DockStyle.Top;
            BackColor = Color.FromArgb(245, 248, 252); // light blue-gray tint
            Padding = new Padding(UiScale.Pixels(6), UiScale.Pixels(3), UiScale.Pixels(6), UiScale.Pixels(3));

            var btnFont = new Font("Segoe UI", UiScale.FontSize(7.5f));
            var labelFont = new Font("Segoe UI", UiScale.FontSize(7f));

            // ─── Heading label ───────────────────────────────────────
            _lblHeading = new Label
            {
                Text = "Memory Bank",
                Font = new Font("Segoe UI Semibold", UiScale.FontSize(7f)),
                ForeColor = Color.FromArgb(90, 90, 90),
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft
            };

            // ─── Memory bank dropdown ───────────────────────────────
            // Populated by the parent view part via SetMemoryBanks().
            // Switching is immediate: the next chat turn reads from the
            // new bank, chat history is preserved.
            _cmbMemoryBank = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", UiScale.FontSize(8f)),
                FlatStyle = FlatStyle.Flat,
                // 130 logical px (was 180). The toolbar is a single row that
                // doesn't wrap or scroll; at 150% Windows display scaling
                // the previous 180 + Memory Bank label + ? + Process Inbox
                // + Health Check + Distill exceeded the side-panel width
                // and clipped Distill. 130 fits typical bank names
                // ("default", "test-mb", "client-x") without dropping
                // the trailing buttons. Long names still scroll inside
                // the dropdown.
                Width = UiScale.Pixels(130),
                Height = UiScale.Pixels(22),
                TabStop = false
            };
            _cmbMemoryBank.SelectedIndexChanged += OnMemoryBankComboChanged;

            // ─── Help link ──────────────────────────────────────────
            _lnkHelp = new LinkLabel
            {
                Text = "?",
                Font = new Font("Segoe UI", UiScale.FontSize(7f)),
                AutoSize = true,
                LinkColor = Color.FromArgb(100, 140, 180),
                ActiveLinkColor = Color.FromArgb(30, 90, 158),
                VisitedLinkColor = Color.FromArgb(100, 140, 180),
                TabStop = false
            };
            _lnkHelp.LinkClicked += (s, e) =>
                HelpSystem.OpenHelp(HelpSystem.Topics.SuperMemory);

            // ─── Process Inbox button ────────────────────────────────
            _btnProcessInbox = new Button
            {
                Text = "\u2B07 Process Inbox", // ⬇ down arrow
                Font = btnFont,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.FromArgb(30, 90, 158),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                AutoSize = true,
                Padding = new Padding(UiScale.Pixels(4), 0, UiScale.Pixels(4), 0),
                Height = UiScale.Pixels(24),
                TabStop = false,
                UseCompatibleTextRendering = true
            };
            _btnProcessInbox.FlatAppearance.BorderSize = 0;
            _btnProcessInbox.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 232, 245);
            _btnProcessInbox.Click += (s, e) => ProcessInboxRequested?.Invoke(this, EventArgs.Empty);

            // Tooltip explaining what this does
            var tip = new ToolTip { AutoPopDelay = 8000 };
            tip.SetToolTip(_cmbMemoryBank,
                "Active memory bank.\n" +
                "Switching is immediate - the next chat turn reads from\n" +
                "the new bank; chat history is preserved.");
            tip.SetToolTip(_btnProcessInbox,
                "Reads new Markdown notes from the active memory bank's\n" +
                "00_INBOX folder and uses AI to organise them into structured\n" +
                "knowledge base articles (client profiles, terminology,\n" +
                "domain knowledge, style guides). For binary files like TMX\n" +
                "or PDF, use Distill instead.");

            // ─── Health Check button ─────────────────────────────────
            _btnHealthCheck = new Button
            {
                Text = "\u2714 Health Check", // ✔ check mark
                Font = btnFont,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.FromArgb(30, 90, 158),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                AutoSize = true,
                Padding = new Padding(UiScale.Pixels(4), 0, UiScale.Pixels(4), 0),
                Height = UiScale.Pixels(24),
                TabStop = false,
                UseCompatibleTextRendering = true
            };
            _btnHealthCheck.FlatAppearance.BorderSize = 0;
            _btnHealthCheck.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 232, 245);
            _btnHealthCheck.Click += (s, e) => HealthCheckRequested?.Invoke(this, EventArgs.Empty);

            tip.SetToolTip(_btnHealthCheck,
                "Scans the active memory bank for problems: conflicting\n" +
                "terminology, broken links, stale or duplicate content.\n" +
                "Fixes what it can and flags the rest for review.");

            // ─── Distill button ─────────────────────────────────────
            _btnDistill = new Button
            {
                Text = "\u2697 Distill", // ⚗ alembic
                Font = btnFont,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.FromArgb(30, 90, 158),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                AutoSize = true,
                Padding = new Padding(UiScale.Pixels(4), 0, UiScale.Pixels(4), 0),
                Height = UiScale.Pixels(24),
                TabStop = false,
                UseCompatibleTextRendering = true
            };
            _btnDistill.FlatAppearance.BorderSize = 0;
            _btnDistill.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 232, 245);
            _btnDistill.Click += (s, e) => DistillRequested?.Invoke(this, EventArgs.Empty);

            tip.SetToolTip(_btnDistill,
                "Extract knowledge from translation files (TMX, DOCX, PDF,\n" +
                "termbases) into structured Markdown articles in the active\n" +
                "memory bank. Source files dropped into the inbox are\n" +
                "archived after a successful distill.");

            // ─── Overview button ────────────────────────────────────
            // ☰ (U+2630) renders in the same Misc-Symbols block as the Distill
            // alembic; the earlier 📊 (U+1F4CA) is an astral emoji the button
            // font can't render and showed as a tofu box.
            _btnOverview = MakeActionButton("☰ Overview", btnFont); // ☰
            _btnOverview.Click += (s, e) => OverviewRequested?.Invoke(this, EventArgs.Empty);
            // Studio 2026 first-click-eaten workaround – see Core/ClickThrough.cs.
            ClickThrough.Attach(_btnOverview, () => OverviewRequested?.Invoke(this, EventArgs.Empty));
            tip.SetToolTip(_btnOverview,
                "Open a scannable HTML overview of the active memory bank:\n" +
                "a searchable terminology table, conflicting-term and stub\n" +
                "lists, domain/client coverage, and recent changes. Opens in\n" +
                "your browser; reads only metadata, so it is instant.");

            // ─── AI Summary button ──────────────────────────────────
            _btnSummary = MakeActionButton("✨ Summary", btnFont); // ✨
            _btnSummary.Click += (s, e) => AiSummaryRequested?.Invoke(this, EventArgs.Empty);
            ClickThrough.Attach(_btnSummary, () => AiSummaryRequested?.Invoke(this, EventArgs.Empty));
            tip.SetToolTip(_btnSummary,
                "Ask the AI for a short plain-English profile of the active\n" +
                "memory bank: what it covers, where it is strong or thin, and\n" +
                "what needs attention. Posted into the chat.");

            // ─── Inbox count label ───────────────────────────────────
            _lblInboxCount = new Label
            {
                Text = "",
                Font = labelFont,
                ForeColor = Color.FromArgb(140, 140, 140),
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(UiScale.Pixels(4), 0, 0, 0)
            };

            // ─── Refresh button ─────────────────────────────────────
            _btnRefresh = new Button
            {
                Text = "\u21BB", // ↻ clockwise arrow
                Font = new Font("Segoe UI", UiScale.FontSize(8.5f)),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.FromArgb(140, 140, 140),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                Size = new Size(UiScale.Pixels(24), UiScale.Pixels(24)),
                TabStop = false,
                UseCompatibleTextRendering = true
            };
            _btnRefresh.FlatAppearance.BorderSize = 0;
            _btnRefresh.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 232, 245);
            _btnRefresh.Click += (s, e) => RefreshRequested?.Invoke(this, EventArgs.Empty);

            tip.SetToolTip(_btnRefresh,
                "Refresh the inbox count.\nUse this after adding files via the\nObsidian Web Clipper or file explorer.");

            // Separator line at bottom
            var sep = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 1,
                BackColor = Color.FromArgb(220, 220, 220)
            };

            Controls.Add(sep);
            Controls.Add(_btnRefresh);
            Controls.Add(_lblInboxCount);
            Controls.Add(_btnSummary);
            Controls.Add(_btnOverview);
            Controls.Add(_btnDistill);
            Controls.Add(_btnHealthCheck);
            Controls.Add(_btnProcessInbox);
            Controls.Add(_lnkHelp);
            Controls.Add(_cmbMemoryBank);
            Controls.Add(_lblHeading);

            // Manual layout – position controls left to right
            Resize += (s, e) => LayoutControls();
            Layout += (s, e) => LayoutControls();
        }

        private void LayoutControls()
        {
            if (_btnProcessInbox == null) return;

            var y = (Height - _btnProcessInbox.Height) / 2;
            var x = UiScale.Pixels(4);

            _lblHeading.Location = new Point(x,
                (Height - _lblHeading.Height) / 2);
            x += _lblHeading.Width + UiScale.Pixels(4);

            _cmbMemoryBank.Location = new Point(x,
                (Height - _cmbMemoryBank.Height) / 2);
            x += _cmbMemoryBank.Width + UiScale.Pixels(4);

            _lnkHelp.Location = new Point(x,
                (Height - _lnkHelp.Height) / 2);
            x += _lnkHelp.Width + UiScale.Pixels(6);

            _btnProcessInbox.Location = new Point(x, y);
            x += _btnProcessInbox.Width + UiScale.Pixels(2);

            _btnHealthCheck.Location = new Point(x, y);
            x += _btnHealthCheck.Width + UiScale.Pixels(2);

            _btnDistill.Location = new Point(x, y);
            x += _btnDistill.Width + UiScale.Pixels(2);

            _btnOverview.Location = new Point(x, y);
            x += _btnOverview.Width + UiScale.Pixels(2);

            _btnSummary.Location = new Point(x, y);
            x += _btnSummary.Width + UiScale.Pixels(6);

            _lblInboxCount.Location = new Point(x,
                (Height - _lblInboxCount.Height) / 2);
            x += _lblInboxCount.Width + UiScale.Pixels(2);

            _btnRefresh.Location = new Point(x,
                (Height - _btnRefresh.Height) / 2);
        }

        /// <summary>
        /// Last inbox count reported by <see cref="UpdateInboxCount"/>.
        /// Tracked so <see cref="SetBusy"/>(false) can restore the correct
        /// Process Inbox button state after a busy operation completes –
        /// without this, the button would be unconditionally re-enabled
        /// even when the inbox is empty, which is a confusing dead-end
        /// click for the user.
        /// </summary>
        private int _lastInboxCount;

        /// <summary>
        /// Updates the inbox file count display and enables/disables the Process Inbox button.
        /// </summary>
        public void UpdateInboxCount(int count)
        {
            _lastInboxCount = count;
            if (_lblInboxCount == null) return;
            _lblInboxCount.Text = count > 0
                ? $"{count} file{(count != 1 ? "s" : "")} in inbox"
                : "Inbox empty";
            _btnProcessInbox.Enabled = count > 0;
            _btnProcessInbox.ForeColor = count > 0
                ? Color.FromArgb(30, 90, 158)
                : Color.FromArgb(170, 170, 170);
        }

        /// <summary>
        /// Enables or disables the SuperMemory action buttons (e.g. during
        /// long-running operations like Health Check or Distill). When
        /// un-busying, Process Inbox is only re-enabled if the last known
        /// inbox count is non-zero – it does not make sense to offer a
        /// clickable "Process Inbox" button when there is nothing to
        /// process, and the previous implementation's unconditional
        /// <c>_btnProcessInbox.Enabled = !busy</c> overrode the count-based
        /// decision that <see cref="UpdateInboxCount"/> had made.
        /// </summary>
        public void SetBusy(bool busy)
        {
            // Process Inbox: respects both the busy flag AND the current
            // inbox count. Disabled while busy; after busy, only enabled
            // if the inbox has at least one file.
            _btnProcessInbox.Enabled = !busy && _lastInboxCount > 0;

            // Health Check and Distill: available whenever not busy.
            // Health Check handles the "bank is empty" case gracefully
            // with a chat message; Distill always needs to be clickable
            // because the user picks files via a dialog.
            _btnHealthCheck.Enabled = !busy;
            _btnDistill.Enabled = !busy;
            // Overview is metadata-only and never calls the LLM, so it stays
            // usable even while an AI operation is running. Summary uses the LLM,
            // so it follows the busy flag.
            _btnSummary.Enabled = !busy;

            if (!busy)
            {
                _btnProcessInbox.ForeColor = _btnProcessInbox.Enabled
                    ? Color.FromArgb(30, 90, 158)
                    : Color.FromArgb(170, 170, 170);
                _btnHealthCheck.ForeColor = Color.FromArgb(30, 90, 158);
                _btnDistill.ForeColor = Color.FromArgb(30, 90, 158);
                _btnSummary.ForeColor = Color.FromArgb(30, 90, 158);
            }
            else
            {
                _btnProcessInbox.ForeColor = Color.FromArgb(170, 170, 170);
                _btnHealthCheck.ForeColor = Color.FromArgb(170, 170, 170);
                _btnDistill.ForeColor = Color.FromArgb(170, 170, 170);
                _btnSummary.ForeColor = Color.FromArgb(170, 170, 170);
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  Memory bank dropdown
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Replaces the memory bank dropdown contents with the given list and
        /// selects <paramref name="activeBank"/> if present. Does not raise
        /// <see cref="MemoryBankChanged"/> – callers drive that side effect
        /// themselves so repopulation after a user switch is silent.
        /// </summary>
        /// <param name="banks">Bank names from <c>UserDataPath.ListMemoryBanks()</c>.</param>
        /// <param name="activeBank">The bank that should appear selected, or null.</param>
        public void SetMemoryBanks(System.Collections.Generic.IList<string> banks, string activeBank)
        {
            if (_cmbMemoryBank == null) return;

            _suppressComboChange = true;
            try
            {
                _cmbMemoryBank.Items.Clear();

                if (banks == null || banks.Count == 0)
                {
                    // Even with no banks on disk we still want the user to be
                    // able to create one, so the "New memory bank…" sentinel is
                    // added as the only usable row. The placeholder above it
                    // is disabled-looking via the text itself – ComboBox has no
                    // per-item enabled state, so we rely on the change handler
                    // ignoring the placeholder.
                    _cmbMemoryBank.Items.Add("(no memory banks)");
                    _cmbMemoryBank.Items.Add(NewBankSentinel);
                    _cmbMemoryBank.SelectedIndex = 0;
                    _cmbMemoryBank.Enabled = true;
                    LayoutControls();
                    return;
                }

                _cmbMemoryBank.Enabled = true;

                int selected = 0;
                for (int i = 0; i < banks.Count; i++)
                {
                    _cmbMemoryBank.Items.Add(banks[i]);
                    if (string.Equals(banks[i], activeBank, System.StringComparison.Ordinal))
                        selected = i;
                }

                // Append the "create new bank" sentinel as the last entry.
                // It is not selectable in the normal sense – OnMemoryBankComboChanged
                // fires NewMemoryBankRequested and reverts the combo instead.
                _cmbMemoryBank.Items.Add(NewBankSentinel);

                _cmbMemoryBank.SelectedIndex = selected;
                _lastRealSelection = banks[selected];
            }
            finally
            {
                _suppressComboChange = false;
            }

            LayoutControls();
        }

        /// <summary>Returns the currently selected bank name, or null if none.</summary>
        public string SelectedMemoryBank
        {
            get
            {
                if (_cmbMemoryBank == null) return null;
                var item = _cmbMemoryBank.SelectedItem as string;
                if (string.IsNullOrEmpty(item)) return null;
                if (item == "(no memory banks)") return null;
                if (item == NewBankSentinel) return null;
                return item;
            }
        }

        /// <summary>
        /// Finds the combo index of the bank named <paramref name="name"/>, or
        /// -1 if not present. Used to revert the selection after the user
        /// clicks the sentinel entry.
        /// </summary>
        private int IndexOfBank(string name)
        {
            if (_cmbMemoryBank == null || string.IsNullOrEmpty(name)) return -1;
            for (int i = 0; i < _cmbMemoryBank.Items.Count; i++)
            {
                var text = _cmbMemoryBank.Items[i] as string;
                if (string.Equals(text, name, System.StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }

        private void OnMemoryBankComboChanged(object sender, EventArgs e)
        {
            if (_suppressComboChange) return;
            if (_cmbMemoryBank == null) return;

            var rawItem = _cmbMemoryBank.SelectedItem as string;

            // Intercept the sentinel row: instead of firing MemoryBankChanged,
            // revert the combo to the previously selected bank and ask the
            // view part to prompt for a new bank name.
            if (rawItem == NewBankSentinel)
            {
                // Revert selection so the sentinel never appears as "active".
                // Prefer the bank we were on when the dropdown opened; fall
                // back to the first real row if that is somehow gone.
                var revertTo = _lastRealSelection;
                int idx = IndexOfBank(revertTo);
                if (idx < 0)
                {
                    // Find the first entry that isn't a placeholder/sentinel.
                    for (int i = 0; i < _cmbMemoryBank.Items.Count; i++)
                    {
                        var text = _cmbMemoryBank.Items[i] as string;
                        if (text != null && text != NewBankSentinel && text != "(no memory banks)")
                        {
                            idx = i;
                            break;
                        }
                    }
                }

                _suppressComboChange = true;
                try
                {
                    if (idx >= 0) _cmbMemoryBank.SelectedIndex = idx;
                }
                finally
                {
                    _suppressComboChange = false;
                }

                NewMemoryBankRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            var name = SelectedMemoryBank;
            if (string.IsNullOrEmpty(name)) return;

            _lastRealSelection = name;
            MemoryBankChanged?.Invoke(this, new MemoryBankChangedEventArgs(name));
        }

        /// <summary>
        /// Tracks the last bank name the user actually settled on, so we can
        /// revert the combo to that row when they click the sentinel entry.
        /// Updated on every real selection change and by <see cref="SetMemoryBanks"/>.
        /// </summary>
        private string _lastRealSelection;
    }

    /// <summary>
    /// Event args for <see cref="SuperMemoryToolbar.MemoryBankChanged"/>.
    /// Carries the name of the bank the user just selected.
    /// </summary>
    public class MemoryBankChangedEventArgs : EventArgs
    {
        public string BankName { get; }

        public MemoryBankChangedEventArgs(string bankName)
        {
            BankName = bankName;
        }
    }
}
