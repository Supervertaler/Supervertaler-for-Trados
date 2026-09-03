using System;
using System.Drawing;
using System.Windows.Forms;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// Dialog for adding a terminology entry or a background note to SuperMemory.
    /// Captures a source term, a target term, optional notes, the bank to write
    /// to, and optionally appends the entry to the active translation prompt.
    ///
    /// Two save modes:
    ///   • Terminology row (default): appends a row to the bank's
    ///     terminology.md, which IS read into prompts. Takes effect at once.
    ///   • Background reference: writes a note to the bank's reference/ folder,
    ///     which is deliberately NOT read into prompts - it is the audit trail.
    ///     For knowledge too fuzzy to be a term pair yet ("fiche can mean either
    ///     sheet or plug depending on context").
    ///
    /// The labels used to name 02_TERMINOLOGY and 00_INBOX, which the bank
    /// redesign replaced with terminology.md and reference/, and the second mode
    /// advertised "AI processing" by a Process Inbox command that does not exist
    /// in this plugin. Saying where something goes is the whole job of this
    /// dialog, so naming the wrong place was the one error it could not afford.
    /// </summary>
    internal class SuperMemoryQuickAddDialog : Form
    {
        private TextBox _txtTerm;
        private TextBox _txtCorrection;
        private TextBox _txtNotes;
        private CheckBox _chkAppendToPrompt;
        private CheckBox _chkRawNote;
        private ComboBox _cmbBank;
        private Label _lblDestination;

        /// <summary>Display suffix marking the bank that is currently active, so
        /// the preselected entry is recognisable as "where things normally go"
        /// rather than just the first name in a list.</summary>
        private const string ActiveSuffix = "  (active)";

        /// <summary>Display suffix on <c>_shared</c>. Every other name in the list
        /// is one client; this one is loaded alongside whichever bank is active,
        /// so it needs to say so at the point of choosing rather than only after
        /// the fact. Kept to three words — a warning long enough to need reading
        /// is one that gets skipped.</summary>
        private const string SharedSuffix = "  (applies to all banks)";

        /// <summary>The source-language term.</summary>
        public string Term => _txtTerm?.Text?.Trim() ?? "";

        /// <summary>The target-language term or translation.</summary>
        public string Correction => _txtCorrection?.Text?.Trim() ?? "";

        /// <summary>Optional notes / context.</summary>
        public string Notes => _txtNotes?.Text?.Trim() ?? "";

        /// <summary>Whether to also append the entry to the active translation prompt.</summary>
        public bool AppendToPrompt => _chkAppendToPrompt?.Checked ?? true;

        /// <summary>
        /// When true, write a note into the bank's reference/ folder instead of
        /// appending a row to its terminology.md. Nothing reads reference/ into
        /// a prompt — it is the audit trail — so this is for knowledge that is
        /// worth keeping but not yet expressible as a term pair.
        /// </summary>
        public bool SaveAsRawNote => _chkRawNote?.Checked ?? false;

        /// <summary>
        /// The memory bank to write to: the active one by default, any other
        /// bank, or <c>_shared</c> for something true of the translator's work
        /// rather than of one client.
        ///
        /// <para>Before this existed the destination was resolved silently from
        /// the active bank and only named in the confirmation, i.e. after the
        /// write. A term filed into the wrong client's terminology is exactly the
        /// kind of mistake that stays invisible until it reaches a delivery.</para>
        /// </summary>
        public string SelectedBank
        {
            get
            {
                var text = _cmbBank?.SelectedItem as string;
                if (string.IsNullOrEmpty(text)) return null;

                // Strip any display annotation. Splitting on the double space is
                // safe rather than clever: UserDataPath.SanitizeBankName allows
                // only [a-z0-9-_], so a real bank name can contain neither a
                // space nor a bracket, and any "  (" is therefore ours.
                var marker = text.IndexOf("  (", StringComparison.Ordinal);
                if (marker > 0) text = text.Substring(0, marker);
                return text.Trim();
            }
        }

        /// <summary>
        /// Creates the Quick Add dialog.
        /// </summary>
        /// <param name="defaultTerm">Pre-filled source term (from selection).</param>
        /// <param name="defaultCorrection">Pre-filled correction (from target selection).</param>
        /// <param name="activePromptName">Display name of the active prompt (shown below checkbox).</param>
        /// <param name="targetLanguage">Display name of the target language (e.g. "English (GB)").</param>
        /// <param name="sourceLanguage">Display name of the source language (e.g. "Dutch (BE)").</param>
        /// <param name="activeBankName">The bank to preselect — normally the active one.</param>
        public SuperMemoryQuickAddDialog(string defaultTerm = "", string defaultCorrection = "",
            string activePromptName = null, string targetLanguage = null, string sourceLanguage = null,
            string activeBankName = null)
        {
            Icon = Supervertaler.Trados.Core.IconHelper.AppIcon;
            // Let WinForms scale this dialog by system DPI so it doesn't squish
            // at >100% Windows display scaling. Cheap fallback; for surfaces
            // with their own UiScale-driven layout, set AutoScaleMode = None
            // instead and let UiScale own scaling.
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Quick Add to memory bank";
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(460, 470);
            BackColor = Color.White;

            var y = 14;

            // ── Destination bank ─────────────────────────────────────
            // First, not last: everything below is "what to save", and this is
            // "where it goes". It used to be neither shown nor choosable.
            Controls.Add(new Label
            {
                Text = "Save to memory bank:",
                Location = new Point(16, y),
                AutoSize = true,
                ForeColor = Color.FromArgb(80, 80, 80)
            });
            y += 20;

            _cmbBank = new ComboBox
            {
                Location = new Point(16, y),
                Width = ClientSize.Width - 32,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 9f)
            };
            PopulateBanks(activeBankName);
            _cmbBank.SelectedIndexChanged += (s, e) => UpdateDestinationLabel();
            Controls.Add(_cmbBank);
            y += 26;

            _lblDestination = new Label
            {
                Location = new Point(18, y),
                AutoSize = false,
                Width = ClientSize.Width - 36,
                Height = 16,
                ForeColor = Color.FromArgb(0, 90, 158),
                Font = new Font("Segoe UI", 8.25f, FontStyle.Italic)
            };
            Controls.Add(_lblDestination);
            y += 24;

            // ── Source term ──────────────────────────────────────────
            var sourceLabel = string.IsNullOrEmpty(sourceLanguage)
                ? "Source term:"
                : $"Source term ({sourceLanguage}):";
            Controls.Add(new Label
            {
                Text = sourceLabel,
                Location = new Point(16, y),
                AutoSize = true,
                ForeColor = Color.FromArgb(80, 80, 80)
            });
            y += 20;

            _txtTerm = new TextBox
            {
                Location = new Point(16, y),
                Width = ClientSize.Width - 32,
                Text = defaultTerm ?? "",
                BackColor = Color.FromArgb(250, 250, 250),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };
            Controls.Add(_txtTerm);
            y += 30;

            // ── Target term ─────────────────────────────────────────
            var targetLabel = string.IsNullOrEmpty(targetLanguage)
                ? "Target term:"
                : $"Target term ({targetLanguage}):";
            Controls.Add(new Label
            {
                Text = targetLabel,
                Location = new Point(16, y),
                AutoSize = true,
                ForeColor = Color.FromArgb(80, 80, 80)
            });
            y += 20;

            _txtCorrection = new TextBox
            {
                Location = new Point(16, y),
                Width = ClientSize.Width - 32,
                Text = defaultCorrection ?? "",
                BackColor = Color.FromArgb(250, 250, 250),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };
            Controls.Add(_txtCorrection);
            y += 30;

            // ── Notes (optional) ─────────────────────────────────────
            Controls.Add(new Label
            {
                Text = "Notes (optional \u2013 context, alternatives, client preferences):",
                Location = new Point(16, y),
                AutoSize = true,
                ForeColor = Color.FromArgb(80, 80, 80)
            });
            y += 20;

            _txtNotes = new TextBox
            {
                Location = new Point(16, y),
                Width = ClientSize.Width - 32,
                Height = 60,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(250, 250, 250)
            };
            Controls.Add(_txtNotes);
            y += 68;

            // ── Background-reference toggle ──────────────────────────
            _chkRawNote = new CheckBox
            {
                Text = "Save as background reference instead",
                Location = new Point(14, y),
                AutoSize = true,
                Checked = false,
                ForeColor = Color.FromArgb(60, 60, 60)
            };
            _chkRawNote.CheckedChanged += (s, e) => UpdateDestinationLabel();
            Controls.Add(_chkRawNote);

            // States what each mode DOES, since the difference that matters is
            // whether the AI ever sees it. The old hint named two folders that no
            // longer exist and promised a Process Inbox command that does not.
            var rawNoteHint = new Label
            {
                Text = "Unchecked: a row in terminology.md - the AI reads it, straight away.  "
                     + "Checked: a note in reference/, kept for you but never read into a prompt.",
                Location = new Point(32, y + 20),
                AutoSize = false,
                Width = ClientSize.Width - 48,
                Height = 30,
                ForeColor = Color.FromArgb(140, 140, 140),
                Font = new Font("Segoe UI", 7.5f)
            };
            Controls.Add(rawNoteHint);
            y += 54;

            // ── Append to prompt checkbox ────────────────────────────
            _chkAppendToPrompt = new CheckBox
            {
                Text = "Also append to active translation prompt",
                Location = new Point(14, y),
                AutoSize = true,
                Checked = true,
                ForeColor = Color.FromArgb(60, 60, 60)
            };
            Controls.Add(_chkAppendToPrompt);
            y += 22;

            // Show active prompt name (or warning if none)
            if (!string.IsNullOrEmpty(activePromptName))
            {
                Controls.Add(new Label
                {
                    Text = "\u2192 " + activePromptName,
                    Location = new Point(32, y),
                    AutoSize = true,
                    ForeColor = Color.FromArgb(0, 90, 158), // Supervertaler blue
                    Font = new Font("Segoe UI", 8.25f, FontStyle.Italic)
                });
            }
            else
            {
                Controls.Add(new Label
                {
                    Text = "\u26A0 No active prompt set for this project",
                    Location = new Point(32, y),
                    AutoSize = true,
                    ForeColor = Color.FromArgb(180, 120, 0),
                    Font = new Font("Segoe UI", 8.25f, FontStyle.Italic)
                });
                _chkAppendToPrompt.Checked = false;
                _chkAppendToPrompt.Enabled = false;
            }
            y += 22;

            // ── Separator ────────────────────────────────────────────
            Controls.Add(new Label
            {
                Location = new Point(16, y),
                Width = ClientSize.Width - 32,
                Height = 1,
                BorderStyle = BorderStyle.Fixed3D
            });
            y += 10;

            // ── Buttons ──────────────────────────────────────────────
            var btnOk = new Button
            {
                Text = "Add",
                DialogResult = DialogResult.OK,
                Location = new Point(ClientSize.Width - 170, y),
                Width = 75,
                FlatStyle = FlatStyle.System
            };
            Controls.Add(btnOk);
            AcceptButton = btnOk;

            var btnCancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(ClientSize.Width - 88, y),
                Width = 75,
                FlatStyle = FlatStyle.System
            };
            Controls.Add(btnCancel);
            CancelButton = btnCancel;

            // Focus the target field if source is pre-filled, otherwise the source field
            Load += (s, e) =>
            {
                if (!string.IsNullOrEmpty(_txtTerm.Text) && string.IsNullOrEmpty(_txtCorrection.Text))
                    _txtCorrection.Focus();
                else
                    _txtTerm.Focus();
            };

            UpdateDestinationLabel();
        }

        /// <summary>
        /// Fills the bank list: the active bank first and preselected, then every
        /// other bank, then <c>_shared</c> last.
        ///
        /// <para><c>_shared</c> is placed at the end and labelled rather than
        /// hidden. It is loaded alongside whichever bank is active, so a rule put
        /// there applies to every job — powerful when meant and wrong when not,
        /// which is an argument for making it visible and deliberate, not for
        /// leaving it reachable only by editing files by hand.</para>
        /// </summary>
        private void PopulateBanks(string activeBankName)
        {
            var shared = MemoryBankReader.SharedBankName;

            var banks = Settings.UserDataPath.ListMemoryBanks() ?? new System.Collections.Generic.List<string>();
            var others = new System.Collections.Generic.List<string>();
            foreach (var b in banks)
            {
                if (string.IsNullOrWhiteSpace(b)) continue;
                if (string.Equals(b, shared, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(activeBankName) &&
                    string.Equals(b, activeBankName, StringComparison.OrdinalIgnoreCase)) continue;
                others.Add(b);
            }

            // The active bank leads even if the folder does not exist yet — the
            // write path creates it, and omitting it would silently retarget the
            // entry somewhere else.
            if (!string.IsNullOrWhiteSpace(activeBankName))
                _cmbBank.Items.Add(activeBankName + ActiveSuffix);

            foreach (var b in others)
                _cmbBank.Items.Add(b);

            _cmbBank.Items.Add(shared + SharedSuffix);

            if (_cmbBank.Items.Count > 0)
                _cmbBank.SelectedIndex = 0;
        }

        /// <summary>
        /// Names the exact file the entry will land in, before the user commits.
        /// The confirmation used to be the first and only place this appeared.
        /// </summary>
        private void UpdateDestinationLabel()
        {
            if (_lblDestination == null) return;

            var bank = SelectedBank;
            if (string.IsNullOrEmpty(bank))
            {
                _lblDestination.Text = "";
                return;
            }

            var target = SaveAsRawNote
                ? MemoryBankReader.ReferenceFolder + "/"
                : MemoryBankReader.TerminologyFile;

            _lblDestination.Text = "→ " + target + "  in  " + bank;

            // Amber only — the combo entry already carries the words, and saying
            // it twice on two adjacent lines reads as a nag rather than a fact.
            var isShared = string.Equals(bank, MemoryBankReader.SharedBankName,
                StringComparison.OrdinalIgnoreCase);
            _lblDestination.ForeColor = isShared
                ? Color.FromArgb(180, 120, 0)   // applies to every job, not just this client
                : Color.FromArgb(0, 90, 158);
        }
    }
}
