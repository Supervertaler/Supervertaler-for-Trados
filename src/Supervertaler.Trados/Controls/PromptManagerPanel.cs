using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Models;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// UserControl for the "Prompts" tab in the Settings dialog.
    /// Side-by-side layout: custom prompt library on the left, system prompt on the right.
    /// </summary>
    public class PromptManagerPanel : UserControl
    {
        private TextBox _txtSystemPrompt;
        private Button _btnEditSystem;
        private Button _btnResetSystem;
        private Label _lblSystemStatus;

        private DataGridView _dgvPrompts;
        private Button _btnNew;
        private Button _btnEdit;
        private Button _btnDelete;
        private Button _btnRestore;

        private Panel _leftPanel;
        private Panel _rightPanel;

        private PromptLibrary _library;
        private List<PromptTemplate> _prompts;
        private string _customSystemPrompt; // null = use default

        public PromptManagerPanel()
        {
            BuildUI();
        }

        private void BuildUI()
        {
            SuspendLayout();
            BackColor = Color.White;

            var labelColor = Color.FromArgb(80, 80, 80);
            var headerFont = new Font("Segoe UI", 9f, FontStyle.Bold);
            var bodyFont = new Font("Segoe UI", 8.5f);

            // ═══════════════════════════════════════════════
            // TWO-PANEL LAYOUT — library left (Fill), system prompt right (38%)
            // Uses plain Panels instead of SplitContainer to avoid
            // SplitterDistance initialization errors in WinForms.
            // ═══════════════════════════════════════════════
            _rightPanel = new Panel
            {
                Dock = DockStyle.Right,
                Width = 250, // initial; updated in Resize
                BackColor = Color.White
            };

            _leftPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White
            };

            // ─── LEFT PANEL: Custom Prompt Library ───────────
            BuildLibraryPanel(_leftPanel, headerFont, bodyFont, labelColor);

            // ─── RIGHT PANEL: System Prompt ──────────────────
            BuildSystemPromptPanel(_rightPanel, headerFont, bodyFont, labelColor);

            // Add right first so Fill calculates correctly
            Controls.Add(_leftPanel);
            Controls.Add(_rightPanel);

            // Keep right panel at ~38% of total width
            Resize += (s, e) =>
            {
                if (Width > 100)
                    _rightPanel.Width = Math.Max(200, (int)(Width * 0.38));
            };

            ResumeLayout(false);
        }

        private void BuildLibraryPanel(Panel panel, Font headerFont, Font bodyFont, Color labelColor)
        {
            // Header
            var lblLibHeader = new Label
            {
                Text = "Custom Prompt Library",
                Font = headerFont,
                ForeColor = Color.FromArgb(50, 50, 50),
                Location = new Point(10, 10),
                AutoSize = true
            };
            panel.Controls.Add(lblLibHeader);

            // Buttons (right-aligned on header row)
            var topPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 36,
                BackColor = Color.White
            };

            _btnNew = new Button
            {
                Text = "New",
                Width = 45,
                Height = 25,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(80, 80, 80),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnNew.FlatAppearance.BorderSize = 0;
            _btnNew.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 220, 220);
            _btnNew.Click += OnNewPrompt;

            _btnEdit = new Button
            {
                Text = "Edit",
                Width = 45,
                Height = 25,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(80, 80, 80),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnEdit.FlatAppearance.BorderSize = 0;
            _btnEdit.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 220, 220);
            _btnEdit.Click += OnEditPrompt;

            _btnDelete = new Button
            {
                Text = "Delete",
                Width = 55,
                Height = 25,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(80, 80, 80),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnDelete.FlatAppearance.BorderSize = 0;
            _btnDelete.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 220, 220);
            _btnDelete.Click += OnDeletePrompt;

            _btnRestore = new Button
            {
                Text = "Restore",
                Width = 60,
                Height = 25,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(80, 80, 80),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnRestore.FlatAppearance.BorderSize = 0;
            _btnRestore.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 220, 220);
            _btnRestore.Click += OnRestoreBuiltIn;

            topPanel.Controls.AddRange(new Control[] { lblLibHeader, _btnNew, _btnEdit, _btnDelete, _btnRestore });

            // Position buttons from right edge
            topPanel.Resize += (s, e) =>
            {
                var pw = topPanel.Width;
                _btnRestore.Location = new Point(pw - 4 - _btnRestore.Width, 6);
                _btnDelete.Location = new Point(_btnRestore.Left - _btnDelete.Width - 2, 6);
                _btnEdit.Location = new Point(_btnDelete.Left - _btnEdit.Width - 2, 6);
                _btnNew.Location = new Point(_btnEdit.Left - _btnNew.Width - 2, 6);
            };

            // ─── Prompt grid ──────────────────────────────
            _dgvPrompts = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                ReadOnly = true,
                BorderStyle = BorderStyle.FixedSingle,
                BackgroundColor = Color.FromArgb(250, 250, 250),
                Font = new Font("Segoe UI", 8.5f),
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                EnableHeadersVisualStyles = false
            };
            _dgvPrompts.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(240, 240, 240),
                ForeColor = Color.FromArgb(50, 50, 50),
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                SelectionBackColor = Color.FromArgb(240, 240, 240),
                SelectionForeColor = Color.FromArgb(50, 50, 50)
            };
            _dgvPrompts.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(250, 250, 250),
                ForeColor = Color.FromArgb(40, 40, 40),
                SelectionBackColor = Color.FromArgb(220, 235, 252),
                SelectionForeColor = Color.FromArgb(40, 40, 40)
            };

            var colName = new DataGridViewTextBoxColumn
            {
                Name = "colName",
                HeaderText = "Name",
                FillWeight = 50
            };
            var colDomain = new DataGridViewTextBoxColumn
            {
                Name = "colDomain",
                HeaderText = "Category",
                FillWeight = 25
            };
            var colSource = new DataGridViewTextBoxColumn
            {
                Name = "colSource",
                HeaderText = "Source",
                FillWeight = 15,
                MinimumWidth = 50
            };
            _dgvPrompts.Columns.AddRange(new DataGridViewColumn[] { colName, colDomain, colSource });
            _dgvPrompts.CellDoubleClick += OnGridDoubleClick;

            var gridPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10, 0, 4, 10),
                BackColor = Color.White
            };
            gridPanel.Controls.Add(_dgvPrompts);

            // Add in reverse order for correct Dock layout
            panel.Controls.Add(gridPanel);   // Fill
            panel.Controls.Add(topPanel);    // Top
        }

        private void BuildSystemPromptPanel(Panel panel, Font headerFont, Font bodyFont, Color labelColor)
        {
            // Top section: header + info + buttons
            var topPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 54,
                BackColor = Color.White
            };

            var lblSysHeader = new Label
            {
                Text = "System Prompt",
                Font = headerFont,
                ForeColor = Color.FromArgb(50, 50, 50),
                Location = new Point(6, 10),
                AutoSize = true
            };

            var lblSysInfo = new Label
            {
                Text = "Base instructions for AI translation. Always included before custom prompts.",
                Location = new Point(6, 30),
                Font = new Font("Segoe UI", 7.5f, FontStyle.Italic),
                ForeColor = Color.FromArgb(130, 130, 130),
                AutoSize = false,
                Height = 18,
                Width = 400,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            topPanel.Controls.AddRange(new Control[] { lblSysHeader, lblSysInfo });

            // Bottom section: Edit/Reset buttons
            var bottomPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 34,
                BackColor = Color.White
            };

            _btnEditSystem = new Button
            {
                Text = "Edit System Prompt",
                Location = new Point(6, 4),
                Width = 130,
                Height = 25,
                FlatStyle = FlatStyle.System,
                Font = bodyFont
            };
            _btnEditSystem.Click += OnEditSystemPrompt;

            _btnResetSystem = new Button
            {
                Text = "Reset to Default",
                Location = new Point(142, 4),
                Width = 120,
                Height = 25,
                FlatStyle = FlatStyle.System,
                Font = bodyFont
            };
            _btnResetSystem.Click += OnResetSystemPrompt;

            _lblSystemStatus = new Label
            {
                Text = "",
                Location = new Point(268, 8),
                AutoSize = true,
                ForeColor = Color.FromArgb(100, 100, 100),
                Font = new Font("Segoe UI", 8f)
            };

            bottomPanel.Controls.AddRange(new Control[] { _btnEditSystem, _btnResetSystem, _lblSystemStatus });

            // Middle: system prompt textbox
            _txtSystemPrompt = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 7.5f),
                BackColor = Color.FromArgb(248, 248, 248),
                ForeColor = Color.FromArgb(60, 60, 60),
                WordWrap = true
            };

            var textPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(6, 0, 6, 0),
                BackColor = Color.White
            };
            textPanel.Controls.Add(_txtSystemPrompt);

            // Add in reverse order for correct Dock layout
            panel.Controls.Add(textPanel);      // Fill
            panel.Controls.Add(bottomPanel);    // Bottom
            panel.Controls.Add(topPanel);       // Top
        }

        // ─── Public API ─────────────────────────────────────────

        /// <summary>
        /// Populates the panel from current settings and prompt library.
        /// </summary>
        public void PopulateFromSettings(AiSettings settings, PromptLibrary library)
        {
            _library = library ?? new PromptLibrary();
            _customSystemPrompt = settings?.CustomSystemPrompt;

            // Show system prompt
            UpdateSystemPromptDisplay();

            // Load prompt library
            RefreshPromptList();
        }

        /// <summary>
        /// Applies changes back to AI settings.
        /// </summary>
        public void ApplyToSettings(AiSettings settings)
        {
            if (settings == null) return;
            settings.CustomSystemPrompt = _customSystemPrompt;
        }

        // ─── System Prompt ──────────────────────────────────────

        private void UpdateSystemPromptDisplay()
        {
            if (!string.IsNullOrWhiteSpace(_customSystemPrompt))
            {
                _txtSystemPrompt.Text = _customSystemPrompt;
                _lblSystemStatus.Text = "(customized)";
                _lblSystemStatus.ForeColor = Color.FromArgb(180, 120, 0);
            }
            else
            {
                _txtSystemPrompt.Text = TranslationPrompt.GetDefaultBaseSystemPrompt();
                _lblSystemStatus.Text = "(default)";
                _lblSystemStatus.ForeColor = Color.FromArgb(30, 130, 60);
            }
        }

        private void OnEditSystemPrompt(object sender, EventArgs e)
        {
            var content = !string.IsNullOrWhiteSpace(_customSystemPrompt)
                ? _customSystemPrompt
                : TranslationPrompt.GetDefaultBaseSystemPrompt();

            var prompt = new PromptTemplate
            {
                Name = "System Prompt",
                Description = "Base system instructions for AI translation",
                Domain = "System",
                Content = content
            };

            using (var dlg = new PromptEditorDialog(prompt))
            {
                dlg.Text = "Edit System Prompt";
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _customSystemPrompt = dlg.Result.Content;
                    UpdateSystemPromptDisplay();
                }
            }
        }

        private void OnResetSystemPrompt(object sender, EventArgs e)
        {
            var result = MessageBox.Show(
                "Reset the system prompt to the default?\n\nThis will discard any customizations.",
                "Reset System Prompt",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);

            if (result == DialogResult.Yes)
            {
                _customSystemPrompt = null;
                UpdateSystemPromptDisplay();
            }
        }

        // ─── Custom Prompt Library ──────────────────────────────

        private void RefreshPromptList()
        {
            _dgvPrompts.Rows.Clear();
            _prompts = _library.GetAllPrompts();

            foreach (var p in _prompts)
            {
                var source = p.IsReadOnly ? "Supervertaler" : (p.IsBuiltIn ? "Built-in" : "Custom");
                _dgvPrompts.Rows.Add(p.Name, p.Domain, source);
            }
        }

        private PromptTemplate GetSelectedPrompt()
        {
            if (_dgvPrompts.SelectedRows.Count == 0)
                return null;
            var idx = _dgvPrompts.SelectedRows[0].Index;
            if (idx < 0 || idx >= _prompts.Count)
                return null;
            return _prompts[idx];
        }

        private void OnNewPrompt(object sender, EventArgs e)
        {
            using (var dlg = new PromptEditorDialog())
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _library.SavePrompt(dlg.Result);
                    RefreshPromptList();
                }
            }
        }

        private void OnEditPrompt(object sender, EventArgs e)
        {
            var selected = GetSelectedPrompt();
            if (selected == null)
            {
                MessageBox.Show("Select a prompt to edit.",
                    "Prompts", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new PromptEditorDialog(selected))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _library.SavePrompt(dlg.Result);
                    RefreshPromptList();
                }
            }
        }

        private void OnDeletePrompt(object sender, EventArgs e)
        {
            var selected = GetSelectedPrompt();
            if (selected == null)
            {
                MessageBox.Show("Select a prompt to delete.",
                    "Prompts", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (selected.IsReadOnly)
            {
                MessageBox.Show("This prompt is from the Supervertaler desktop app and cannot be deleted from here.",
                    "Prompts", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var result = MessageBox.Show(
                $"Delete prompt \"{selected.Name}\"?\n\nThis cannot be undone.",
                "Delete Prompt",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

            if (result == DialogResult.Yes)
            {
                _library.DeletePrompt(selected);
                RefreshPromptList();
            }
        }

        private void OnRestoreBuiltIn(object sender, EventArgs e)
        {
            var result = MessageBox.Show(
                "Restore all built-in prompts?\n\nThis will overwrite any edits to built-in prompts and re-create deleted ones.",
                "Restore Built-in Prompts",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);

            if (result == DialogResult.Yes)
            {
                _library.RestoreBuiltInPrompts();
                RefreshPromptList();
            }
        }

        private void OnGridDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            OnEditPrompt(sender, EventArgs.Empty);
        }
    }
}
