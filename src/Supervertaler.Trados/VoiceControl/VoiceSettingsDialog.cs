using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.VoiceControl
{
    /// <summary>
    /// The "Voice command settings" dialog – deliberately the ONLY place
    /// where the voice system shows its depth. A grid of commands (enable/disable, edit
    /// phrases and aliases, change actions, add your own), plus restore-
    /// defaults. Command files are JSON-compatible with Supervertaler
    /// Workbench's voice_commands.json.
    /// </summary>
    internal sealed class VoiceSettingsDialog : Form
    {
        private readonly DataGridView _grid;
        private List<VoiceCommand> _commands;

        public VoiceSettingsDialog()
        {
            Text = "Voice command settings";
            Icon = IconHelper.AppIcon;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(UiScale.Pixels(760), UiScale.Pixels(520));
            MinimizeBox = false;
            MaximizeBox = false;
            HelpButton = true;
            HelpButtonClicked += (s, e) =>
            {
                ((System.ComponentModel.CancelEventArgs)e).Cancel = true;
                HelpSystem.OpenHelp(HelpSystem.Topics.VoiceCommands);
            };

            _commands = VoiceCommandSet.Load();

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = true,
                AllowUserToDeleteRows = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowHeadersVisible = false,
                BackgroundColor = SystemColors.Window
            };
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "On", Name = "colEnabled", FillWeight = 12 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Spoken phrase", Name = "colPhrase", FillWeight = 30 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Aliases (comma-separated)", Name = "colAliases", FillWeight = 34 });
            var typeCol = new DataGridViewComboBoxColumn { HeaderText = "Type", Name = "colType", FillWeight = 20 };
            typeCol.Items.AddRange("keystroke", "internal");
            _grid.Columns.Add(typeCol);
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Action (chord or internal id)", Name = "colAction", FillWeight = 30 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Description", Name = "colDescription", FillWeight = 40 });

            var help = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = UiScale.Pixels(74),
                Padding = new Padding(UiScale.Pixels(8), UiScale.Pixels(6), UiScale.Pixels(8), 0),
                Text = "Keystroke actions send a chord to Studio (e.g. \"ctrl+enter\", \"alt+up\", \"f3\") – any Studio or " +
                       "Supervertaler shortcut works. Internal actions call the plugin directly: insert_term_1…insert_term_9, " +
                       "term_picker, termlens_popup, navigate_next, navigate_previous, stop_listening, select_phrase, " +
                       "select_source_phrase, delete_selection, dictate_on, dictate_off, dictate_toggle. " +
                       "A phrase ending in {phrase} takes the words spoken after it as its argument. " +
                       "The recogniser only listens for the words below, so prefer distinctive ones: every word here " +
                       "competes with your own text in every segment."
            };

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = UiScale.Pixels(42),
                Padding = new Padding(UiScale.Pixels(6))
            };
            var btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var btnSave = new Button { Text = "Save", AutoSize = true };
            var btnDefaults = new Button { Text = "Restore defaults", AutoSize = true };
            btnSave.Click += (s, e) => OnSave();
            btnDefaults.Click += (s, e) =>
            {
                if (MessageBox.Show(
                        "Replace all commands with the built-in defaults?",
                        "Voice commands", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    _commands = VoiceCommandSet.Defaults();
                    FillGrid();
                }
            };
            buttons.Controls.Add(btnCancel);
            buttons.Controls.Add(btnSave);
            buttons.Controls.Add(btnDefaults);

            Controls.Add(_grid);
            Controls.Add(help);
            Controls.Add(buttons);
            AcceptButton = btnSave;
            CancelButton = btnCancel;

            FillGrid();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F1)
            {
                HelpSystem.OpenHelp(HelpSystem.Topics.VoiceCommands);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void FillGrid()
        {
            _grid.Rows.Clear();
            foreach (var c in _commands)
            {
                _grid.Rows.Add(
                    c.Enabled,
                    c.Phrase,
                    string.Join(", ", c.Aliases ?? new List<string>()),
                    c.ActionType == "internal" ? "internal" : "keystroke",
                    c.Action,
                    c.Description);
            }
        }

        private void OnSave()
        {
            var result = new List<VoiceCommand>();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.IsNewRow) continue;
                var phrase = (row.Cells["colPhrase"].Value ?? "").ToString().Trim();
                if (phrase.Length == 0) continue;

                var aliasesRaw = (row.Cells["colAliases"].Value ?? "").ToString();
                result.Add(new VoiceCommand
                {
                    Enabled = row.Cells["colEnabled"].Value is bool b && b,
                    Phrase = phrase,
                    // Split on semicolons too – a user typing "a;b" would
                    // otherwise silently get one unmatchable alias phrase.
                    Aliases = aliasesRaw.Split(',', ';')
                        .Select(a => a.Trim()).Where(a => a.Length > 0).ToList(),
                    ActionType = (row.Cells["colType"].Value ?? "keystroke").ToString(),
                    Action = (row.Cells["colAction"].Value ?? "").ToString().Trim(),
                    Description = (row.Cells["colDescription"].Value ?? "").ToString().Trim()
                });
            }

            if (result.Count == 0)
            {
                MessageBox.Show("At least one command is required.", "Voice commands",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            VoiceCommandSet.Save(result);
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
