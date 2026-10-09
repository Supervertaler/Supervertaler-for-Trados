using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// Edits one memory-bank Markdown file as plain text.
    ///
    /// <para>Plain text on purpose. These files go straight into the AI's
    /// context, so a save that reformats or "tidies" one degrades every
    /// subsequent translation quietly. Nothing here parses the markdown; what
    /// you typed is what gets written.</para>
    ///
    /// <para>Not <see cref="PromptEditorDialog"/>, which carries Name,
    /// Description and Category fields. A bank file has none of those, and a
    /// Name box would imply it renames the file.</para>
    ///
    /// <para><b>Two hazards this exists to handle.</b> These files are also
    /// written by Obsidian, by AI clients over the MCP tools and by the memoQ
    /// plugin, and nothing locks them — so the file can change between opening
    /// this dialog and saving it. And the bank files do not agree on line
    /// endings: some are CRLF, many are LF-only. Writing back with whatever
    /// WinForms produced would rewrite every line of such a file, turning a
    /// one-word edit into a whole-file diff in the user's sync and version
    /// history. Both are handled by <see cref="BankFileStore"/>, the same code
    /// the MCP write tools use, which also writes atomically and keeps the
    /// previous version in the backups folder.</para>
    /// </summary>
    internal class BankFileEditorDialog : Form
    {
        private readonly string _filePath;
        private TextBox _txt;

        /// <summary>The file's version when we opened it, so a concurrent write
        /// is noticed rather than silently clobbered.</summary>
        private string _version;

        public BankFileEditorDialog(string filePath, string bankName, bool readIntoPrompts)
        {
            _filePath = filePath;

            Icon = Core.IconHelper.AppIcon;
            // Scaled to the screen DPI by DialogScale.Apply at the end of the
            // constructor: AutoScaleMode.Dpi, which this set before, scaled nothing.
            AutoScaleMode = AutoScaleMode.None;
            Text = "Edit " + Path.GetFileName(filePath ?? "");
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = true;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(760, 620);
            MinimumSize = new Size(420, 300);
            BackColor = Color.White;

            var header = new Label
            {
                Text = readIntoPrompts
                    ? "Memory bank \"" + bankName + "\" – read into the AI's context."
                    : "Memory bank \"" + bankName + "\", reference folder – never read into a prompt.",
                Dock = DockStyle.Top,
                Height = 26,
                Padding = new Padding(12, 6, 12, 0),
                Font = new Font("Segoe UI", 8.25f, FontStyle.Italic),
                ForeColor = Color.FromArgb(110, 110, 110)
            };

            _txt = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                AcceptsTab = true,
                Font = new Font("Consolas", 9f),
                BackColor = Color.FromArgb(252, 252, 252),
                BorderStyle = BorderStyle.FixedSingle,
                Dock = DockStyle.Fill
            };

            var buttons = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = Color.White };

            var btnSave = new Button
            {
                Text = "Save",
                DialogResult = DialogResult.None,   // set only after a successful write
                Width = 90,
                Height = 26,
                FlatStyle = FlatStyle.System,
                Anchor = AnchorStyles.Right | AnchorStyles.Top
            };
            btnSave.Click += OnSave;

            var btnCancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Width = 90,
                Height = 26,
                FlatStyle = FlatStyle.System,
                Anchor = AnchorStyles.Right | AnchorStyles.Top
            };

            buttons.Controls.Add(btnSave);
            buttons.Controls.Add(btnCancel);
            void PlaceButtons()
            {
                btnCancel.Location = new Point(buttons.Width - btnCancel.Width - Core.DialogScale.Pixels(12), Core.DialogScale.Pixels(10));
                btnSave.Location = new Point(btnCancel.Left - btnSave.Width - Core.DialogScale.Pixels(8), Core.DialogScale.Pixels(10));
            }
            buttons.Resize += (s, e) => PlaceButtons();

            // Fill first, then the docked edges, so the text box gets what is left.
            var pad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 4, 12, 0), BackColor = Color.White };
            pad.Controls.Add(_txt);

            Controls.Add(pad);
            Controls.Add(buttons);
            Controls.Add(header);
            Supervertaler.Trados.Core.DialogScale.Apply(this);
            // Scale resizes the panel (placing the buttons) and THEN scales the
            // buttons' positions again, pushing them off its right edge.
            PlaceButtons();

            AcceptButton = null;      // Enter inserts a newline; this is a text editor
            CancelButton = btnCancel;

            Load += (s, e) => LoadFile();
        }

        private void LoadFile()
        {
            var opened = BankFileStore.ForUser().Load(_filePath);
            if (!opened.Ok)
            {
                MessageBox.Show(this, "Could not open this file:\n\n" + opened.Error,
                    "Supervertaler", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.Cancel;
                Close();
                return;
            }

            _version = opened.Version;
            // The TextBox needs CRLF to show line breaks at all. The file's own
            // endings are restored on save.
            _txt.Text = opened.Content.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
            _txt.Select(0, 0);
        }

        private void OnSave(object sender, EventArgs e)
        {
            try
            {
                Save();
            }
            catch (Exception ex)
            {
                // BankFileStore reports failures rather than throwing; this is for
                // the unforeseen, which must not escape a click handler into Studio.
                MessageBox.Show(this, "Could not save this file:\n\n" + ex.Message,
                    "Supervertaler", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void Save()
        {
            var store = BankFileStore.ForUser();
            var saved = store.Save(_filePath, _txt.Text, _version);

            // Somebody else wrote this file while the dialog was open. Saying
            // so beats silently winning.
            if (saved.Conflict)
            {
                var answer = MessageBox.Show(this,
                    "Something else wrote to this file while you had it open.\n\n"
                    + "That would be Obsidian or another editor, an AI assistant connected "
                    + "through the MCP server, or Supervertaler for memoQ. Saving now replaces "
                    + "what they wrote with the text in this window.\n\n"
                    + "Choose No to go back \u2013 your text stays in the editor, so you can "
                    + "copy it somewhere safe and compare before deciding.\n\n"
                    + "Save anyway?",
                    "File changed on disk",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes) return;

                // Over the file as it is now; a file deleted meanwhile is recreated.
                saved = store.Save(_filePath, _txt.Text, saved.Version ?? BankFileStore.NewFile);
            }

            if (!saved.Ok)
            {
                MessageBox.Show(this, "Could not save this file:\n\n" + saved.Error,
                    "Supervertaler", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
