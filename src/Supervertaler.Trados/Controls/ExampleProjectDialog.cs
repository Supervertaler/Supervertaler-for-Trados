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
    /// "Open example project": which language, and which way. Every Windows
    /// language is offered; the prepared ones (with a translation memory and a
    /// termbase) are listed first. "Into English" is only possible for a
    /// prepared language, because it needs the translated document.
    /// </summary>
    internal sealed class ExampleProjectDialog : Form
    {
        private sealed class Item
        {
            public string Code;
            public string Text;
            public override string ToString() => Text;
        }

        private readonly ExampleData _data;
        private readonly ComboBox _cmbLanguage;
        private readonly ComboBox _cmbEnglish;
        private readonly RadioButton _rbFromEnglish;
        private readonly RadioButton _rbIntoEnglish;
        private readonly Label _lblInfo;
        private readonly Label _lblError;

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

            var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var intro = new Label
            {
                Text = "A small project to try Supervertaler on: an invented installation guide for a water heater, " +
                       "about 70 segments, with a memory bank, and a translation memory and a termbase where your language is prepared. " +
                       "It is made in Studio's Projects folder and opened in the Editor.",
                AutoSize = true,
                MaximumSize = new Size(UiScale.Pixels(460), 0),
                Margin = new Padding(0, 0, 0, UiScale.Pixels(10))
            };
            grid.Controls.Add(intro, 0, 0);
            grid.SetColumnSpan(intro, 2);

            _cmbLanguage = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = UiScale.Pixels(300), MaxDropDownItems = 20 };
            grid.Controls.Add(new Label { Text = "Your language:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            grid.Controls.Add(_cmbLanguage, 1, 1);

            // The two ways round, spelt out with the chosen language's name
            // ("English → Dutch"); see UpdateDirectionLabels.
            _rbFromEnglish = new RadioButton { AutoSize = true, Checked = true, Margin = new Padding(0, UiScale.Pixels(3), UiScale.Pixels(16), 0) };
            _rbIntoEnglish = new RadioButton { AutoSize = true, Margin = new Padding(0, UiScale.Pixels(3), 0, 0) };
            var radios = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0, UiScale.Pixels(4), 0, 0) };
            radios.Controls.Add(_rbFromEnglish);
            radios.Controls.Add(_rbIntoEnglish);
            grid.Controls.Add(new Label { Text = "Direction:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
            grid.Controls.Add(radios, 1, 2);

            _cmbEnglish = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = UiScale.Pixels(300) };
            grid.Controls.Add(new Label { Text = "English variant:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
            grid.Controls.Add(_cmbEnglish, 1, 3);

            _lblInfo = new Label { AutoSize = true, MaximumSize = new Size(UiScale.Pixels(460), 0), ForeColor = Color.FromArgb(80, 80, 80), Margin = new Padding(0, UiScale.Pixels(10), 0, 0) };
            grid.Controls.Add(_lblInfo, 0, 4);
            grid.SetColumnSpan(_lblInfo, 2);
            _lblError = new Label { AutoSize = true, MaximumSize = new Size(UiScale.Pixels(460), 0), ForeColor = Color.FromArgb(180, 60, 0), Visible = false };
            grid.Controls.Add(_lblError, 0, 5);
            grid.SetColumnSpan(_lblError, 2);

            var ok = new Button { Text = "Make the project", AutoSize = true, DialogResult = DialogResult.None };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            ok.Click += OnOk;
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, UiScale.Pixels(12), 0, 0) };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            grid.Controls.Add(buttons, 0, 6);
            grid.SetColumnSpan(buttons, 2);
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(grid);

            FillLanguages(guessLanguage);
            FillEnglish(guessEnglish);
            if (guessIntoEnglish) _rbIntoEnglish.Checked = true;
            _cmbLanguage.SelectedIndexChanged += (s, e) => UpdateInfo();
            _rbFromEnglish.CheckedChanged += (s, e) => UpdateInfo();
            UpdateInfo();
        }

        private bool Prepared(string code)
        {
            var key = ExampleProject.DataKeyFor(_data, code);
            return key != null && ExampleProject.Missing(_data, key).Count == 0;
        }

        /// <summary>Every specific Windows culture except English, prepared ones first. About 800 entries.</summary>
        private void FillLanguages(string guess)
        {
            var all = CultureInfo.GetCultures(CultureTypes.SpecificCultures)
                .Where(c => c.TwoLetterISOLanguageName != "en" && (c.CultureTypes & CultureTypes.UserCustomCulture) == 0)
                .Select(c => new Item { Code = c.Name, Text = c.EnglishName })
                .OrderBy(i => i.Text, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            var isPrepared = all.ToDictionary(i => i.Code, i => Prepared(i.Code));
            var prepared = all.Where(i => isPrepared[i.Code]).ToList();
            var rest = all.Where(i => !isPrepared[i.Code]).ToList();
            _cmbLanguage.BeginUpdate();
            foreach (var i in prepared) _cmbLanguage.Items.Add(i);
            if (prepared.Count > 0) _cmbLanguage.Items.Add(new Item { Code = null, Text = "──────────────" });
            foreach (var i in rest) _cmbLanguage.Items.Add(i);
            _cmbLanguage.EndUpdate();

            var pick = _cmbLanguage.Items.Cast<Item>().FirstOrDefault(i => i.Code != null && string.Equals(i.Code, guess, StringComparison.OrdinalIgnoreCase));
            if (pick != null) _cmbLanguage.SelectedItem = pick;
        }

        private void FillEnglish(string guess)
        {
            var en = CultureInfo.GetCultures(CultureTypes.SpecificCultures)
                .Where(c => c.TwoLetterISOLanguageName == "en" && (c.CultureTypes & CultureTypes.UserCustomCulture) == 0)
                .Select(c => new Item { Code = c.Name, Text = c.EnglishName })
                .OrderBy(i => i.Code == "en-US" ? 0 : i.Code == "en-GB" ? 1 : 2).ThenBy(i => i.Text)
                .ToList();
            foreach (var i in en) _cmbEnglish.Items.Add(i);
            var pick = en.FirstOrDefault(i => string.Equals(i.Code, guess, StringComparison.OrdinalIgnoreCase))
                       ?? en.FirstOrDefault(i => i.Code == "en-US") ?? en.FirstOrDefault();
            _cmbEnglish.SelectedItem = pick;
        }

        private void UpdateInfo()
        {
            _lblError.Visible = false;
            var item = _cmbLanguage.SelectedItem as Item;
            var lang = item?.Code != null ? ExampleProject.EnglishName(item.Code) : "your language";
            _rbFromEnglish.Text = "English → " + lang;
            _rbIntoEnglish.Text = lang + " → English";
            if (item?.Code == null)
            {
                _lblInfo.Text = "Choose the language you translate into or from.";
                _rbIntoEnglish.Enabled = true;
                return;
            }
            bool prepared = Prepared(item.Code);
            _rbIntoEnglish.Enabled = prepared;
            if (!prepared && _rbIntoEnglish.Checked) _rbFromEnglish.Checked = true;
            var name = ExampleProject.EnglishName(item.Code);
            _lblInfo.Text = prepared
                ? "Includes a translation memory with a few fuzzy matches (for FuzzyFixer) and a termbase (for TermLens and the AI)."
                : "There is no example translation memory or termbase for " + name + " yet, so the project has the document only. " +
                  "Translate, Proofread, QuickLauncher and Batch Translate all work; FuzzyFixer and TermLens have nothing to show.";
        }

        private void OnOk(object sender, EventArgs e)
        {
            var item = _cmbLanguage.SelectedItem as Item;
            var english = (_cmbEnglish.SelectedItem as Item)?.Code;
            var d = ExampleProject.Direction(_data, item?.Code, _rbIntoEnglish.Checked, english, out var error);
            if (d == null)
            {
                _lblError.Text = error;
                _lblError.Visible = true;
                return;
            }
            Direction = d;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
