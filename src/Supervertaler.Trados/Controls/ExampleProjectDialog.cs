using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// "Open example project": a source and a target language, as a translator
    /// thinks of a job. One of them has to be English (the example is written in
    /// English); <see cref="ExampleProject.FromPair"/> says so before anything is
    /// made. Both lists show languages ("Dutch"); "Show language variants" lists
    /// every Windows variant instead ("Dutch (Belgium)"). English comes first,
    /// then the prepared languages (with a translation memory and a termbase),
    /// then the rest.
    /// </summary>
    internal sealed class ExampleProjectDialog : Form
    {
        private sealed class Item
        {
            public string Code;     // a specific culture (nl-NL); null for the separator
            public string Text;
            public override string ToString() => Text;
        }

        private readonly ExampleData _data;
        private readonly ComboBox _cmbSource;
        private readonly ComboBox _cmbTarget;
        private readonly CheckBox _chkVariants;
        private readonly Label _lblInfo;
        private readonly Button _ok;
        private readonly HashSet<string> _prepared;
        // Group name ("Dutch", "Portuguese (Brazil)") -> the variant its plain
        // entry stands for; worked out once (about 800 cultures), see DefaultFor.
        private readonly Dictionary<string, string> _defaults;

        public ExampleDirection Direction { get; private set; }

        public ExampleProjectDialog(ExampleData data, string guessLanguage, bool guessIntoEnglish, string guessEnglish)
        {
            _data = data;
            Text = "Open example project";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9f);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(UiScale.Pixels(12));

            // Which variants have the example's TM and termbase. About 800 cultures,
            // each a dictionary lookup: once, not on every list change.
            var specific = AllSpecific().ToList();
            _prepared = new HashSet<string>(
                specific.Where(c => Prepared(c.Name)).Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            _defaults = specific.GroupBy(c => GroupName(c.Name))
                .ToDictionary(g => g.Key, g => PickDefault(g.Key, g.Select(c => c.Name).ToList()));

            var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var intro = new Label
            {
                Text = "A small project to try Supervertaler on: an invented installation guide for a water heater, " +
                       "about 70 segments. It is created in Studio's Projects folder and opened in the Editor.",
                AutoSize = true,
                MaximumSize = new Size(UiScale.Pixels(460), 0),
                Margin = new Padding(0, 0, 0, UiScale.Pixels(10))
            };
            grid.Controls.Add(intro, 0, 0);
            grid.SetColumnSpan(intro, 2);

            _cmbSource = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = UiScale.Pixels(300), MaxDropDownItems = 20 };
            _cmbTarget = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = UiScale.Pixels(300), MaxDropDownItems = 20 };
            grid.Controls.Add(new Label { Text = "Source language:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            grid.Controls.Add(_cmbSource, 1, 1);
            grid.Controls.Add(new Label { Text = "Target language:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
            grid.Controls.Add(_cmbTarget, 1, 2);

            _chkVariants = new CheckBox { Text = "Show language variants", AutoSize = true, Margin = new Padding(0, UiScale.Pixels(4), 0, 0) };
            grid.Controls.Add(_chkVariants, 1, 3);

            _lblInfo = new Label { AutoSize = true, MaximumSize = new Size(UiScale.Pixels(460), 0), Margin = new Padding(0, UiScale.Pixels(10), 0, 0) };
            grid.Controls.Add(_lblInfo, 0, 4);
            grid.SetColumnSpan(_lblInfo, 2);

            _ok = new Button { Text = "Create project", AutoSize = true, DialogResult = DialogResult.None };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            _ok.Click += OnOk;
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, UiScale.Pixels(12), 0, 0) };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(_ok);
            grid.Controls.Add(buttons, 0, 5);
            grid.SetColumnSpan(buttons, 2);
            AcceptButton = _ok;
            CancelButton = cancel;
            Controls.Add(grid);

            // The first guess, as a pair.
            var english = ExampleProject.Canonical(guessEnglish) ?? "en-US";
            var other = ExampleProject.Canonical(guessLanguage);
            var src = guessIntoEnglish && other != null ? other : english;
            var tgt = guessIntoEnglish ? english : other;
            // Open with variants shown when the guess is a variant the plain list
            // would not show as itself (Dutch (Belgium), English (UK) …).
            _chkVariants.Checked = (src != null && DefaultFor(src) != src) || (tgt != null && DefaultFor(tgt) != tgt);
            Fill(_cmbSource, src);
            Fill(_cmbTarget, tgt);

            _cmbSource.SelectedIndexChanged += (s, e) => UpdateInfo();
            _cmbTarget.SelectedIndexChanged += (s, e) => UpdateInfo();
            _chkVariants.CheckedChanged += (s, e) =>
            {
                var a = (_cmbSource.SelectedItem as Item)?.Code;
                var b = (_cmbTarget.SelectedItem as Item)?.Code;
                Fill(_cmbSource, a);
                Fill(_cmbTarget, b);
                UpdateInfo();
            };
            UpdateInfo();
        }

        private static IEnumerable<CultureInfo> AllSpecific() =>
            CultureInfo.GetCultures(CultureTypes.SpecificCultures)
                .Where(c => (c.CultureTypes & CultureTypes.UserCustomCulture) == 0 && !string.IsNullOrEmpty(c.Name));

        private bool Prepared(string code)
        {
            var key = ExampleProject.DataKeyFor(_data, code);
            return key != null && ExampleProject.Missing(_data, key).Count == 0;
        }

        private static bool IsEnglish(string code) =>
            code != null && code.StartsWith("en", StringComparison.OrdinalIgnoreCase) && (code.Length == 2 || code[2] == '-');

        /// <summary>
        /// The variant a plain language entry stands for: English is en-US;
        /// otherwise Windows' own default for the language (Dutch: nl-NL), within
        /// the same <see cref="ExampleProject.EnglishName"/> group, so Portuguese
        /// (Brazil) and Chinese (Traditional) keep their own entries.
        /// </summary>
        private string DefaultFor(string code)
        {
            if (code == null) return null;
            return _defaults.TryGetValue(GroupName(code), out var d) ? d : code;
        }

        private static string GroupName(string code) => IsEnglish(code) ? "English" : ExampleProject.EnglishName(code);

        private static string PickDefault(string group, List<string> members)
        {
            if (group == "English") return "en-US";
            string preferred = null;
            try { preferred = CultureInfo.CreateSpecificCulture(members[0].Split('-')[0]).Name; } catch { }
            if (preferred != null && members.Contains(preferred, StringComparer.OrdinalIgnoreCase)) return preferred;
            return members.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).First();
        }

        /// <summary>The list: English first, the prepared languages, a line, the rest.
        /// Selects <paramref name="select"/>, or the entry that stands for it.</summary>
        private void Fill(ComboBox box, string select)
        {
            List<Item> all;
            if (_chkVariants.Checked)
            {
                all = AllSpecific().Select(c => new Item { Code = c.Name, Text = c.EnglishName }).ToList();
            }
            else
            {
                all = _defaults.Select(kv => new Item { Code = kv.Value, Text = kv.Key }).ToList();
            }
            int Rank(Item i) => IsEnglish(i.Code) ? 0 : _prepared.Contains(i.Code) ? 1 : 2;
            all = all.OrderBy(Rank)
                     .ThenBy(i => IsEnglish(i.Code) ? (i.Code == "en-US" ? 0 : i.Code == "en-GB" ? 1 : 2) : 0)
                     .ThenBy(i => i.Text, StringComparer.CurrentCultureIgnoreCase)
                     .ToList();

            box.BeginUpdate();
            box.Items.Clear();
            int lastRank = -1;
            foreach (var i in all)
            {
                var r = Rank(i);
                if (lastRank >= 0 && r != lastRank) box.Items.Add(new Item { Code = null, Text = "──────────────" });
                box.Items.Add(i);
                lastRank = r;
            }
            box.EndUpdate();

            var want = _chkVariants.Checked ? select : DefaultFor(select);
            var pick = box.Items.Cast<Item>().FirstOrDefault(i => i.Code != null && string.Equals(i.Code, want, StringComparison.OrdinalIgnoreCase));
            box.SelectedItem = pick;
        }

        private void UpdateInfo()
        {
            var src = (_cmbSource.SelectedItem as Item)?.Code;
            var tgt = (_cmbTarget.SelectedItem as Item)?.Code;
            var d = ExampleProject.FromPair(_data, src, tgt, out var error);
            _ok.Enabled = d != null;
            if (d == null)
            {
                _lblInfo.ForeColor = Color.FromArgb(180, 60, 0);
                _lblInfo.Text = error;
                return;
            }
            _lblInfo.ForeColor = Color.FromArgb(80, 80, 80);
            var other = d.IntoEnglish ? d.Source : d.Target;
            _lblInfo.Text = d.Prepared
                ? "Includes a translation memory with a few fuzzy matches (for FuzzyFixer), a termbase (for TermLens and the AI) " +
                  "and a memory bank with a short client brief (for the AI)."
                : "There is no example translation memory or termbase for " + ExampleProject.EnglishName(other) + " yet, so the project has " +
                  "the document and a memory bank. Translate, Proofread, QuickLauncher and Batch Translate all work; FuzzyFixer and TermLens have nothing to show.";
        }

        private void OnOk(object sender, EventArgs e)
        {
            var src = (_cmbSource.SelectedItem as Item)?.Code;
            var tgt = (_cmbTarget.SelectedItem as Item)?.Code;
            var d = ExampleProject.FromPair(_data, src, tgt, out var error);
            if (d == null) { UpdateInfo(); return; }
            Direction = d;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
