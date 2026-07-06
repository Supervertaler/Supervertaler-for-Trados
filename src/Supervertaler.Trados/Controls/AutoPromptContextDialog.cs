using System;
using System.Drawing;
using System.Windows.Forms;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// Shown before AutoPrompt generation so the translator can confirm or correct
    /// the AI-detected document context and add an optional briefing. The detection
    /// is automatic; this dialog just lets a translator who wants to correct the
    /// domain or add a short note do so. Most users press Generate and move on.
    /// Mirrors the Supervertaler Workbench _AutoPromptContextDialog.
    /// </summary>
    internal class AutoPromptContextDialog : Form
    {
        private readonly ComboBox _domainCombo;
        private readonly TextBox _hintBox;

        // Keep in sync with DocumentContextClassifier.Domains and the
        // PromptGenerator domain templates.
        private static readonly string[] Domains =
            { "general", "patent", "legal", "medical", "technical", "financial", "marketing" };

        /// <summary>The domain the user confirmed (lowercase, template key).</summary>
        public string SelectedDomain
        {
            get
            {
                var idx = _domainCombo.SelectedIndex;
                return (idx >= 0 && idx < Domains.Length) ? Domains[idx] : "general";
            }
        }

        /// <summary>The optional free-text briefing the user typed (may be empty).</summary>
        public string ContextHint => (_hintBox.Text ?? "").Trim();

        public AutoPromptContextDialog(string detectedDomain, string description)
        {
            Text = "AutoPrompt – confirm context";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(480, 258);

            var intro = new Label
            {
                Text = "Supervertaler had the AI read the document and detect the context below. " +
                       "Confirm it, or correct it and add a short briefing so the generated prompt " +
                       "matches your document.",
                Location = new Point(14, 12),
                Size = new Size(452, 44),
                AutoSize = false,
            };

            var detected = string.IsNullOrEmpty(detectedDomain) ? "general" : detectedDomain;
            var summaryText = "Detected context: " + Capitalize(detected)
                + (string.IsNullOrEmpty(description) ? "" : "  –  " + description);
            var summary = new Label
            {
                Text = summaryText,
                Location = new Point(14, 62),
                Size = new Size(452, 34),
                AutoSize = false,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            };

            var domainLabel = new Label { Text = "Domain:", Location = new Point(14, 104), AutoSize = true };
            _domainCombo = new ComboBox
            {
                Location = new Point(80, 100),
                Size = new Size(190, 24),
                DropDownStyle = ComboBoxStyle.DropDownList,
            };
            foreach (var d in Domains) _domainCombo.Items.Add(Capitalize(d));
            var idx = Array.IndexOf(Domains, detected.ToLowerInvariant());
            _domainCombo.SelectedIndex = idx >= 0 ? idx : 0;

            var hintLabel = new Label
            {
                Text = "Context briefing (optional) – e.g. “creative marketing copy, playful tone”:",
                Location = new Point(14, 136),
                Size = new Size(452, 18),
                AutoSize = false,
            };
            _hintBox = new TextBox
            {
                Location = new Point(14, 156),
                Size = new Size(452, 50),
                Multiline = true,
            };

            var btnGenerate = new Button
            {
                Text = "Generate",
                Location = new Point(298, 220),
                Size = new Size(82, 26),
                DialogResult = DialogResult.OK,
                FlatStyle = FlatStyle.System,
            };
            var btnCancel = new Button
            {
                Text = "Cancel",
                Location = new Point(386, 220),
                Size = new Size(80, 26),
                DialogResult = DialogResult.Cancel,
                FlatStyle = FlatStyle.System,
            };
            AcceptButton = btnGenerate;
            CancelButton = btnCancel;

            Controls.Add(intro);
            Controls.Add(summary);
            Controls.Add(domainLabel);
            Controls.Add(_domainCombo);
            Controls.Add(hintLabel);
            Controls.Add(_hintBox);
            Controls.Add(btnGenerate);
            Controls.Add(btnCancel);
        }

        private static string Capitalize(string s) =>
            string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);
    }
}
