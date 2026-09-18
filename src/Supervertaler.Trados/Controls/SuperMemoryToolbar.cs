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
        private Button _btnConvertLegacy;
        private Button _btnOverview;   // now the bank report
        private Button _btnOpenFolder;
        private Button _btnHarvest;
        private Button _btnRefresh;

        /// <summary>
        /// Suppresses <see cref="MemoryBankChanged"/> while the dropdown is being
        /// populated programmatically. Mirrors the <c>_suppress_combo_change</c>
        /// flag in the Python Supervertaler Assistant.
        /// </summary>
        private bool _suppressComboChange;

        /// <summary>Raised when the user asks to convert a legacy-layout bank.
        /// Only reachable while <see cref="SetLegacyBank"/> has flagged one.</summary>
        public event EventHandler ConvertLegacyRequested;

        // ProcessInboxRequested was declared here, never raised and never
        // subscribed — there is no Process Inbox button on this toolbar. It was
        // the source of the CS0067 warning on every build, and, more usefully,
        // of the Quick Add dialog's claim that a raw note would be "compiled by
        // Process Inbox". Removed rather than wired up: reference/ is
        // deliberately the audit trail, not a queue waiting to be processed.

        /// <summary>Raised for the bank report (formerly Overview).</summary>
        public event EventHandler OverviewRequested;

        /// <summary>Raised to open the active bank's folder on disk. The whole
        /// design assumes the user edits these files themselves, so getting to
        /// them has to be one click.</summary>
        public event EventHandler OpenFolderRequested;

        /// <summary>Raised to harvest the open document's tracked changes into
        /// the active bank's reference/ folder.</summary>
        public event EventHandler HarvestRequested;


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
        /// #135: the user picked "(match by project name)". The view part forgets
        /// the project's recorded bank and resolves it again from the project name.
        /// </summary>
        public event EventHandler MatchByProjectNameRequested;

        /// <summary>
        /// Sentinel display text for the "create new bank" entry. Kept as a
        /// constant so the change handler can reliably recognise the row
        /// without depending on string literals sprinkled through the file.
        /// </summary>
        private const string NewBankSentinel = "+ New memory bank\u2026"; // ellipsis
        private const string MatchByNameSentinel = "(match by project name)";

        /// <summary>
        /// #135: shown as the selected row when no project bank is chosen and no
        /// bank called 'default' exists - the state used to display as "default",
        /// beside a real "_shared" row, and read as a bank. A placeholder, not a
        /// choice: selecting it does nothing.
        /// </summary>
        public const string NoBankPlaceholder = "(no project bank – shared only)";

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
                Text = "SuperMemory",
                Font = new Font("Segoe UI Semibold", UiScale.FontSize(8.5f), FontStyle.Bold),
                ForeColor = Color.FromArgb(30, 90, 158),
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

            var tip = new ToolTip { AutoPopDelay = 8000 };
            tip.SetToolTip(_cmbMemoryBank,
                "Active memory bank." + Environment.NewLine +
                "Switching is immediate - the next chat turn reads from" + Environment.NewLine +
                "the new bank; chat history is preserved." + Environment.NewLine +
                Environment.NewLine +
                "_shared is not a project bank: it holds your house" + Environment.NewLine +
                "defaults and is loaded on top of whichever bank is" + Environment.NewLine +
                "active, so you never need to select it to use it." + Environment.NewLine +
                "Select it only to edit those defaults - the active" + Environment.NewLine +
                "bank overrides them wherever the two disagree.");

            // ─── Convert legacy bank ────────────────────────────────
            // Hidden unless the active bank is still on the old seven-folder
            // layout. Such a bank contributes NOTHING to a prompt under the new
            // reader, so the one thing this must not do is stay quiet about it.
            _btnConvertLegacy = new Button
            {
                Text = "⚠ Convert this bank",
                Font = btnFont,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.FromArgb(150, 70, 0),
                BackColor = Color.FromArgb(255, 244, 214),
                Cursor = Cursors.Hand,
                AutoSize = true,
                Padding = new Padding(UiScale.Pixels(4), 0, UiScale.Pixels(4), 0),
                Height = UiScale.Pixels(24),
                TabStop = false,
                UseCompatibleTextRendering = true,
                Visible = false
            };
            _btnConvertLegacy.FlatAppearance.BorderSize = 0;
            _btnConvertLegacy.FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 232, 180);
            _btnConvertLegacy.Click += (s, e) => ConvertLegacyRequested?.Invoke(this, EventArgs.Empty);

            // ─── Bank report ────────────────────────────────────────
            _btnOverview = MakeActionButton("☰ Report", btnFont); // ☰
            _btnOverview.Click += (s, e) => OverviewRequested?.Invoke(this, EventArgs.Empty);
            // Studio 2026 first-click-eaten workaround - see Core/ClickThrough.cs.
            ClickThrough.Attach(_btnOverview, () => OverviewRequested?.Invoke(this, EventArgs.Empty));
            tip.SetToolTip(_btnOverview,
                "What this bank actually contributes: the three files and their" + Environment.NewLine +
                "sizes, how many terms are in the table, how many tokens get" + Environment.NewLine +
                "injected into a prompt, what _shared adds on top, and anything" + Environment.NewLine +
                "that looks wrong. Instant - no AI call.");

            // ─── Open bank folder ───────────────────────────────────
            // No emoji: U+1F4C2 is astral and the button font renders it as a tofu
            // box - the same trap the Overview button hit with U+1F4CA. Plain text
            // beats a placeholder square.
            _btnOpenFolder = MakeActionButton("Open folder", btnFont);
            _btnOpenFolder.Click += (s, e) => OpenFolderRequested?.Invoke(this, EventArgs.Empty);
            ClickThrough.Attach(_btnOpenFolder, () => OpenFolderRequested?.Invoke(this, EventArgs.Empty));
            tip.SetToolTip(_btnOpenFolder,
                "Open this bank's folder. The files are meant to be edited by" + Environment.NewLine +
                "hand - brief.md, terminology.md, style.md - so this is the" + Environment.NewLine +
                "normal way to change what the AI knows.");

            // ─── Harvest tracked changes ────────────────────────────
            // The extraction existed from 20.158 but only over MCP, so it was
            // invisible to anyone not driving Trados from Claude Desktop. It is
            // a read plus a write into reference/, needs no AI call, and is most
            // useful at the end of a review pass - which is exactly when the
            // user is looking at this panel.
            _btnHarvest = MakeActionButton("\u21BA Harvest changes", btnFont);
            _btnHarvest.Click += (s, e) => HarvestRequested?.Invoke(this, EventArgs.Empty);
            ClickThrough.Attach(_btnHarvest, () => HarvestRequested?.Invoke(this, EventArgs.Empty));
            tip.SetToolTip(_btnHarvest,
                "Collect this document's tracked changes into the active bank as" + Environment.NewLine +
                "(before, after) pairs - what the draft offered vs what you made it." + Environment.NewLine +
                Environment.NewLine +
                "Needs Track Changes to have been ON while you edited. The file goes" + Environment.NewLine +
                "to reference/ as source material; nothing reads it automatically." + Environment.NewLine +
                "A change that recurs is a rule worth adding to terminology.md.");

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
            Controls.Add(_btnOverview);
            Controls.Add(_btnOpenFolder);
            Controls.Add(_btnHarvest);
            Controls.Add(_btnConvertLegacy);
            Controls.Add(_lnkHelp);
            Controls.Add(_cmbMemoryBank);
            Controls.Add(_lblHeading);

            // Manual layout – position controls left to right
            Resize += (s, e) => LayoutControls();
            Layout += (s, e) => LayoutControls();
        }

        private void LayoutControls()
        {
            if (_btnOverview == null) return;

            var x = UiScale.Pixels(4);

            // Hidden controls must not reserve width. _btnConvertLegacy is hidden
            // for a converted bank, and counting it anyway pushed everything to
            // its right off the edge of a docked panel.
            void Place(Control c, int gap)
            {
                if (c == null || !c.Visible) return;
                c.Location = new Point(x, (Height - c.Height) / 2);
                x += c.Width + UiScale.Pixels(gap);
            }

            Place(_lblHeading, 4);
            Place(_cmbMemoryBank, 4);
            Place(_btnConvertLegacy, 6);
            Place(_lnkHelp, 6);
            Place(_btnOverview, 2);
            Place(_btnOpenFolder, 2);
            Place(_btnHarvest, 6);
            Place(_btnRefresh, 0);
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
        /// <summary>
        /// Shows or hides the convert prompt for a bank still on the old layout.
        /// </summary>
        public void SetLegacyBank(bool isLegacy)
        {
            if (_btnConvertLegacy == null) return;
            _btnConvertLegacy.Visible = isLegacy;
            if (isLegacy)
            {
                var tip = new ToolTip { AutoPopDelay = 12000 };
                tip.SetToolTip(_btnConvertLegacy,
                    "This bank still uses the old folder layout and is NOT being read" + Environment.NewLine +
                    "- it contributes nothing to the AI's context." + Environment.NewLine + Environment.NewLine +
                    "Converting folds its articles into brief.md, terminology.md and" + Environment.NewLine +
                    "style.md. Nothing is deleted: the original folders are moved to" + Environment.NewLine +
                    "reference/_legacy so you can check the result.");
            }
        }

        /// <summary>
        /// No longer shown. The inbox belonged to the folder layout that banks
        /// no longer use; kept as a no-op so the callers that still report a
        /// count do not need to know that.
        /// </summary>
        public void UpdateInboxCount(int count)
        {
            _lastInboxCount = count;
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
            // Nothing here calls the LLM any more: the report is computed from
            // the files on disk, and opening a folder or re-reading it is always
            // safe. Kept because callers still bracket long operations with it.
            if (_cmbMemoryBank != null) _cmbMemoryBank.Enabled = !busy;
        }

        /// <summary>
        /// Replaces the memory bank dropdown contents with the given list and
        /// selects <paramref name="activeBank"/> if present. Does not raise
        /// <see cref="MemoryBankChanged"/> – callers drive that side effect
        /// themselves so repopulation after a user switch is silent.
        /// </summary>
        /// <param name="banks">Bank names from <c>UserDataPath.ListMemoryBanks()</c>.</param>
        /// <param name="activeBank">The bank that should appear selected, or null.</param>
        /// <summary>
        /// Widens the DROPDOWN to fit the longest bank name, leaving the closed
        /// combo at its fixed width.
        ///
        /// <para>The combo itself is pinned to 130 logical px on purpose - see its
        /// construction - because the toolbar is a single non-wrapping row and at
        /// 150% display scaling a wider box pushed Distill off the end. That
        /// constraint applies to the closed control only: the dropdown is a popup
        /// and is free to be wider than the thing it hangs from, which is what
        /// WinForms' DropDownWidth is for.</para>
        ///
        /// <para>Real bank names run well past what 130 px shows, so they were being
        /// truncated at exactly the characters that tell one bank from another.</para>
        /// </summary>
        private void FitDropDownWidth()
        {
            if (_cmbMemoryBank == null || _cmbMemoryBank.IsDisposed) return;
            try
            {
                var widest = _cmbMemoryBank.Width;
                foreach (var item in _cmbMemoryBank.Items)
                {
                    var text = item as string;
                    if (string.IsNullOrEmpty(text)) continue;
                    var w = TextRenderer.MeasureText(text, _cmbMemoryBank.Font).Width;
                    if (w > widest) widest = w;
                }

                // Room for the scrollbar the list grows once it overflows, plus the
                // few pixels an item's own left inset costs. Without the scrollbar
                // allowance the longest name is clipped precisely when there are
                // enough banks to need scrolling - which is when it matters.
                widest += SystemInformation.VerticalScrollBarWidth + UiScale.Pixels(8);

                // A popup may overhang the panel, but not the screen: past the
                // working area Windows shifts it left and clips the other end
                // instead. Half the screen is far more than any plausible name.
                var cap = Math.Max(Screen.FromControl(_cmbMemoryBank).WorkingArea.Width / 2,
                                   _cmbMemoryBank.Width);
                _cmbMemoryBank.DropDownWidth = Math.Min(widest, cap);
            }
            catch
            {
                // Measuring is cosmetic. A handle-less or disposed control here must
                // not stop the bank list being populated.
            }
        }

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
                    _cmbMemoryBank.Items.Add(MatchByNameSentinel);
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
                _cmbMemoryBank.Items.Add(MatchByNameSentinel);
                _cmbMemoryBank.Items.Add(NewBankSentinel);

                _cmbMemoryBank.SelectedIndex = selected;
                _lastRealSelection = banks[selected];
            }
            finally
            {
                _suppressComboChange = false;
                // Both exit paths above end here, including the early return for
                // "no banks", so the list is always sized to what is in it.
                FitDropDownWidth();
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
                if (item == NoBankPlaceholder) return null;
                if (item == NewBankSentinel) return null;
                if (item == MatchByNameSentinel) return null;
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
            if (rawItem == NewBankSentinel || rawItem == MatchByNameSentinel)
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
                        if (text != null && text != NewBankSentinel && text != MatchByNameSentinel
                            && text != "(no memory banks)" && text != NoBankPlaceholder)
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

                if (rawItem == NewBankSentinel)
                    NewMemoryBankRequested?.Invoke(this, EventArgs.Empty);
                else
                    MatchByProjectNameRequested?.Invoke(this, EventArgs.Empty);
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
