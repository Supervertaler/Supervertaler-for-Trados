using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Supervertaler.Core.SuperVault;
using Supervertaler.Trados.SuperVault;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// "Edit in EverTM..." from the EverTM Matches pane (Michael, 23 Nov 2026; modelled on Studio's Edit Translation
    /// Unit): the stored source and target, editable, with their tags as grey markers (‹1›…‹/1›, ‹2/›) that can't be
    /// typed over; and the fields Project, Client, End client, Subject, Document and Note. Source, dates, users and the
    /// language variants are shown, not editable: they are EverTM's record of where the unit came from.
    ///
    /// <para>On Save the texts are put back together from the markers, each replaced by the tag code it stands for; if a
    /// marker is missing or doubled, Save says so and nothing is saved. What an edit does to EverTM is
    /// <see cref="VaultLibrary.Edit"/>'s.</para>
    /// </summary>
    public sealed class EverTmEditDialog : Form
    {
        readonly RichTextBox _source, _target;
        readonly Dictionary<string, TextBox> _fields = new Dictionary<string, TextBox>(StringComparer.Ordinal);
        readonly Label _problem = new Label { AutoSize = false, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(190, 30, 30), TextAlign = ContentAlignment.MiddleLeft };
        readonly List<(string marker, string raw)> _sourceTags, _targetTags;
        readonly VaultCandidate _shown;
        static readonly Color MarkerColor = Color.FromArgb(120, 120, 120);

        /// <summary>The edited source and target as stored text (tag codes included), and the fields (null when unchanged). Set on Save.</summary>
        public string NewSource { get; private set; }
        public string NewTarget { get; private set; }
        public List<(string Field, string Value)> NewFields { get; private set; }

        public EverTmEditDialog(VaultCandidate shown, string sourceLanguage, string targetLanguage)
        {
            _shown = shown ?? throw new ArgumentNullException(nameof(shown));
            Text = "Edit in EverTM";
            Icon = Supervertaler.Trados.Core.IconHelper.AppIcon;
            Font = new Font("Segoe UI", 9F);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(720, 520);
            MinimumSize = new Size(520, 420);

            // Tags are numbered by first appearance, source then target, so a pair has the same number on both sides.
            var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
            _source = TextBox(shown.SourceRaw, numbers, out _sourceTags);
            _target = TextBox(shown.TargetRaw, numbers, out _targetTags);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12, 10, 12, 4) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            void Row(string label, Control c, SizeType size = SizeType.AutoSize, float height = 0)
            {
                layout.RowStyles.Add(size == SizeType.Percent ? new RowStyle(SizeType.Percent, height) : new RowStyle(SizeType.AutoSize));
                layout.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 6, 10, 0) });
                c.Dock = DockStyle.Fill;
                layout.Controls.Add(c);
            }
            Row("Source (" + sourceLanguage + ")", _source, SizeType.Percent, 50);
            Row("Target (" + targetLanguage + ")", _target, SizeType.Percent, 50);
            var fields = shown.Fields ?? new List<(string Field, string Value)>();
            foreach (var name in VaultStore.Writer.EditableFields)
            {
                var box = new TextBox { Text = string.Join("; ", fields.Where(f => f.Field == name).Select(f => f.Value)) };
                _fields[name] = box;
                Row(name, box);
            }
            var record = new List<string>();
            var source = string.Join("; ", fields.Where(f => f.Field == VaultFields.Source).Select(f => f.Value));
            if (source.Length > 0) record.Add("Source: " + source);
            record.Add("Created: " + When(shown.Created, shown.CreatedBy));
            record.Add("Changed: " + When(shown.Changed, shown.ChangedBy));
            if (!string.IsNullOrWhiteSpace(shown.Variant)) record.Add("Variants: " + shown.Variant.Replace(">", " > "));
            Row("", new Label { Text = string.Join("   ·   ", record), AutoSize = true, ForeColor = Color.FromArgb(110, 110, 110), Margin = new Padding(0, 6, 0, 0) });

            var note = new Label
            {
                Dock = DockStyle.Top, Height = 34, Padding = new Padding(12, 8, 12, 0), ForeColor = Color.FromArgb(70, 70, 70),
                Text = "Changes EverTM only: never your Studio TMs or files. Every copy of this translation becomes the edited one, and the old text won't come back from an import. Several values: separate them with \";\".",
            };
            var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 44, ColumnCount = 3, Padding = new Padding(12, 6, 12, 8) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var save = new Button { Text = "Save", AutoSize = true };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            save.Click += (s, e) => Save();
            bottom.Controls.Add(_problem, 0, 0);
            bottom.Controls.Add(save, 1, 0);
            bottom.Controls.Add(cancel, 2, 0);
            CancelButton = cancel;
            AcceptButton = save;   // Enter in a field; in the texts, Enter saves too (a segment has no line breaks)

            Controls.Add(layout);
            Controls.Add(note);
            Controls.Add(bottom);
            Shown += (s, e) => { _target.Focus(); _target.SelectionStart = _target.TextLength; };
        }

        static string When(DateTime? utc, string user) =>
            (utc == null ? "not known" : EverTmSummary.FormatDate(utc.Value)) + (string.IsNullOrWhiteSpace(user) ? "" : ", " + user);

        /// <summary>A text box with the stored text, its tags as protected grey markers.</summary>
        RichTextBox TextBox(string raw, Dictionary<string, int> numbers, out List<(string marker, string raw)> tags)
        {
            tags = new List<(string, string)>();
            var box = new RichTextBox { Multiline = true, WordWrap = true, ScrollBars = RichTextBoxScrollBars.Vertical, DetectUrls = false, Font = new Font("Segoe UI", 10F), Height = 70 };
            box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter && e.Modifiers == Keys.None) { e.SuppressKeyPress = true; Save(); } };
            box.Protected += (s, e) => _problem.Text = "The grey markers are the formatting: type around them.";
            foreach (var p in VaultMarkup.Parse(raw ?? ""))
            {
                box.SelectionStart = box.TextLength;
                box.SelectionLength = 0;
                if (!p.IsTag)
                {
                    box.SelectionProtected = false;
                    box.SelectionColor = Color.Black;
                    box.SelectedText = p.Text;
                    continue;
                }
                string key = (p.Key ?? p.Raw).Replace("/close", "");
                if (!numbers.TryGetValue(key, out int n)) numbers[key] = n = numbers.Count + 1;
                string marker = p.Kind == VaultTagKind.Close ? "\u2039/" + n + "\u203A" : p.Kind == VaultTagKind.Standalone ? "\u2039" + n + "/\u203A" : "\u2039" + n + "\u203A";
                tags.Add((marker, p.Raw));
                box.SelectionColor = MarkerColor;
                box.SelectedText = marker;
                box.Select(box.TextLength - marker.Length, marker.Length);
                box.SelectionProtected = true;
            }
            box.SelectionStart = box.TextLength;
            box.SelectionLength = 0;
            box.SelectionProtected = false;
            box.SelectionColor = Color.Black;
            return box;
        }

        /// <summary>
        /// The edited text back as stored text: each marker replaced by its tag code, in the order the markers now
        /// stand. Null (and <paramref name="problem"/>) when a marker is missing, doubled or new.
        /// </summary>
        internal static string Rebuild(string edited, List<(string marker, string raw)> tags, out string problem)
        {
            problem = null;
            edited = (edited ?? "").Replace("\r", " ").Replace("\n", " ");
            var waiting = tags.GroupBy(t => t.marker).ToDictionary(g => g.Key, g => new Queue<string>(g.Select(t => t.raw)), StringComparer.Ordinal);
            var sb = new StringBuilder(edited.Length + 32);
            int i = 0;
            while (i < edited.Length)
            {
                int open = edited.IndexOf('\u2039', i);
                if (open < 0) { sb.Append(edited, i, edited.Length - i); break; }
                int close = edited.IndexOf('\u203A', open + 1);
                sb.Append(edited, i, open - i);
                if (close < 0) { problem = "A formatting marker is broken."; return null; }
                var marker = edited.Substring(open, close - open + 1);
                if (!waiting.TryGetValue(marker, out var q) || q.Count == 0) { problem = "The marker " + marker + " is doubled or not this text's."; return null; }
                sb.Append(q.Dequeue());
                i = close + 1;
            }
            var missing = waiting.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).ToList();
            if (missing.Count > 0) { problem = "Formatting marker " + string.Join(", ", missing) + " is missing: put the text back around it, or Cancel."; return null; }
            return sb.ToString().Trim();
        }

        void Save()
        {
            var src = Rebuild(_source.Text, _sourceTags, out var p1);
            var trg = Rebuild(_target.Text, _targetTags, out var p2);
            if (src == null || trg == null) { _problem.Text = src == null ? "Source: " + p1 : "Target: " + p2; return; }
            if (VaultMarkup.Plain(src).Length == 0 || VaultMarkup.Plain(trg).Length == 0) { _problem.Text = "The source and the target need some text."; return; }
            var fields = new List<(string Field, string Value)>();
            foreach (var kv in _fields)
                foreach (var v in kv.Value.Text.Split(';').Select(x => VaultFields.Clean(x)).Where(x => x.Length > 0).Distinct())
                    fields.Add((kv.Key, v));
            var old = (_shown.Fields ?? new List<(string Field, string Value)>()).Where(f => VaultStore.Writer.EditableFields.Contains(f.Field)).ToList();
            bool fieldsChanged = !fields.OrderBy(f => f.Field + "\t" + f.Value, StringComparer.Ordinal).SequenceEqual(old.OrderBy(f => f.Field + "\t" + f.Value, StringComparer.Ordinal));
            NewSource = src;
            NewTarget = trg;
            NewFields = fieldsChanged ? fields : null;
            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>Tests (tools/SuperVaultCheck): the two texts and a field box, and Save.</summary>
        public RichTextBox SourceBox => _source;
        public RichTextBox TargetBox => _target;
        public TextBox FieldBox(string field) => _fields.TryGetValue(field, out var b) ? b : null;
        public string Problem => _problem.Text;
        public void SaveForTests() => Save();
    }
}
