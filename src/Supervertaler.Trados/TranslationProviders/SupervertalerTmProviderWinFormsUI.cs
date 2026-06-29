using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Sdl.Core.Globalization;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.TranslationProviders
{
    /// <summary>
    /// Provides the Trados Studio "Use → Add → Supervertaler TM" entry point
    /// for the bridged TMs. The Browse() method shows a one-list picker of
    /// every TM the user has flagged <c>bridged_to_trados</c> in Workbench
    /// and returns the chosen ones as ready-to-use provider instances.
    ///
    /// Editing isn't supported (<see cref="SupportsEditing"/> = false) –
    /// the provider has no per-instance configuration today; everything is
    /// driven from Workbench's TMs tab. Settings only live on the Workbench
    /// side; this UI is purely a discovery surface.
    /// </summary>
    [TranslationProviderWinFormsUi(
        Id = "SupervertalerTmProviderWinFormsUI",
        Name = "Supervertaler TM Bridge",
        Description = "Attach one of your Supervertaler Workbench TMs as a Trados translation provider")]
    public class SupervertalerTmProviderWinFormsUI : ITranslationProviderWinFormsUI
    {
        public string TypeName => "Supervertaler Workbench TM (bridged)";

        public string TypeDescription =>
            "Reads translation memories directly from a Supervertaler Workbench user-data folder. " +
            "Only TMs flagged \"Bridge\" in Workbench's TMs tab are visible.";

        public bool SupportsEditing => false;

        public bool SupportsTranslationProviderUri(Uri translationProviderUri)
        {
            return translationProviderUri != null
                && string.Equals(
                    translationProviderUri.Scheme,
                    SupervertalerTmProvider.ProviderScheme,
                    StringComparison.OrdinalIgnoreCase);
        }

        public ITranslationProvider[] Browse(
            IWin32Window owner,
            LanguagePair[] languagePairs,
            ITranslationProviderCredentialStore credentialStore)
        {
            var dbPath = SupervertalerTmProviderFactory.ResolveDbPath();
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
            {
                MessageBox.Show(
                    owner,
                    "No Supervertaler database found at:\n  " + (dbPath ?? "(unresolved)") +
                    "\n\nOpen Supervertaler Workbench at least once to create it, then mark " +
                    "one or more TMs as \"Bridge\" in the Workbench TMs tab.",
                    "Supervertaler TM Bridge",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return new ITranslationProvider[0];
            }

            List<TmInfo> bridged;
            using (var reader = new TmReader(dbPath))
            {
                if (!reader.Open())
                {
                    MessageBox.Show(
                        owner,
                        "Could not open the Supervertaler database:\n  " + dbPath +
                        "\n\n" + (reader.LastError ?? "(no error message)"),
                        "Supervertaler TM Bridge",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return new ITranslationProvider[0];
                }
                bridged = reader.GetBridgedTms();
            }

            if (bridged.Count == 0)
            {
                MessageBox.Show(
                    owner,
                    "No TMs are currently marked as \"Bridge\" in Supervertaler Workbench.\n\n" +
                    "To make a TM available here:\n" +
                    "  1. Open Supervertaler Workbench\n" +
                    "  2. Go to the TMs tab\n" +
                    "  3. Tick the orange \"Bridge\" checkbox next to the TM\n" +
                    "  4. Reopen this dialogue",
                    "Supervertaler TM Bridge",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return new ITranslationProvider[0];
            }

            // Filter to TMs whose stored languages match at least one of the
            // language pairs Studio is asking about. Same loose, direction-
            // agnostic match as SupportsLanguageDirection: base-language only
            // (so a TM stored as bare "nl"/"en" surfaces for "nl-NL"/"en-GB"
            // project pairs) and EITHER orientation (so an nl→en TM surfaces
            // for an en→nl project — the lookup reverses it transparently).
            List<TmInfo> compatible;
            if (languagePairs != null && languagePairs.Length > 0)
            {
                compatible = new List<TmInfo>();
                foreach (var tm in bridged)
                {
                    foreach (var pair in languagePairs)
                    {
                        bool reversed;
                        if (SupervertalerTmProvider.IsCompatibleEitherDirection(
                                tm, pair.SourceCulture.Name, pair.TargetCulture.Name, out reversed))
                        {
                            compatible.Add(tm);
                            break;
                        }
                    }
                }
            }
            else
            {
                compatible = bridged;
            }

            if (compatible.Count == 0)
            {
                MessageBox.Show(
                    owner,
                    "None of the bridged Supervertaler TMs match this project's language pair.\n\n" +
                    "Tip: open Supervertaler Workbench and check the source/target languages " +
                    "on your Bridge-flagged TMs.",
                    "Supervertaler TM Bridge",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return new ITranslationProvider[0];
            }

            // Show the picker dialogue.
            using (var dlg = new BridgedTmPickerDialog(compatible))
            {
                var dr = dlg.ShowDialog(owner);
                if (dr != DialogResult.OK || dlg.SelectedTms == null || dlg.SelectedTms.Count == 0)
                    return new ITranslationProvider[0];

                var providers = new List<ITranslationProvider>(dlg.SelectedTms.Count);
                foreach (var tm in dlg.SelectedTms)
                {
                    var uri = SupervertalerTmProvider.BuildUriForTm(tm.TmId);
                    providers.Add(new SupervertalerTmProvider(uri, tm, dbPath));
                }
                return providers.ToArray();
            }
        }

        public bool Edit(
            IWin32Window owner,
            ITranslationProvider translationProvider,
            LanguagePair[] languagePairs,
            ITranslationProviderCredentialStore credentialStore)
        {
            // Nothing to edit in v1 – the provider's "configuration" is the
            // Workbench-side Bridge flag, not anything we can change here.
            // Returning false signals Studio that no changes were made.
            MessageBox.Show(
                owner,
                "Supervertaler bridged TMs are configured in Workbench's TMs tab. " +
                "Use the orange \"Bridge\" checkbox there to add or remove TMs from this provider.",
                "Supervertaler TM Bridge",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return false;
        }

        public TranslationProviderDisplayInfo GetDisplayInfo(
            Uri translationProviderUri,
            string translationProviderState)
        {
            var tmId = SupervertalerTmProvider.ExtractTmIdFromUri(translationProviderUri);
            var dbPath = SupervertalerTmProviderFactory.ResolveDbPath();
            var tmInfo = SupervertalerTmProviderFactory.ResolveTmInfo(dbPath, tmId);

            var name = tmInfo != null
                ? "Supervertaler Workbench TM: " + tmInfo.Name
                : "Supervertaler Workbench TM: " + (tmId ?? "(unknown)");
            var tooltip = tmInfo != null
                ? "Bridged from Supervertaler Workbench (" + tmInfo.EntryCount + " entries; " +
                  (tmInfo.SourceLang ?? "?") + " → " + (tmInfo.TargetLang ?? "?") + ")"
                : "Supervertaler Workbench TM is no longer marked as Bridged in Workbench";

            return new TranslationProviderDisplayInfo
            {
                Name = name,
                TooltipText = tooltip,
                SearchResultImage = null,
                // Trados shows this icon in the project's "Translation Memory
                // and Automated Translation" list next to each attached
                // provider. Same blue Sv icon every other dialog uses.
                TranslationProviderIcon = IconHelper.AppIcon,
            };
        }

        public bool GetCredentialsFromUser(
            IWin32Window owner,
            Uri translationProviderUri,
            string translationProviderState,
            ITranslationProviderCredentialStore credentialStore)
        {
            // No credentials – the bridge is local-file-based.
            return true;
        }
    }

    // ─── Picker dialogue ─────────────────────────────────────────────

    /// <summary>
    /// Modal picker shown by <see cref="SupervertalerTmProviderWinFormsUI.Browse"/>.
    /// Plain CheckedListBox with the bridged TM names; multi-select supported
    /// so attaching several at once takes one click each instead of one full
    /// "Add provider" round-trip each. Intentionally barebones – no fancy
    /// styling, no preview – it's a transient dialogue that the user sees
    /// once per project.
    /// </summary>
    internal sealed class BridgedTmPickerDialog : Form
    {
        private readonly CheckedListBox _list;
        private readonly IList<TmInfo> _tms;
        public List<TmInfo> SelectedTms { get; private set; }

        public BridgedTmPickerDialog(IList<TmInfo> tms)
        {
            _tms = tms ?? throw new ArgumentNullException(nameof(tms));
            SelectedTms = new List<TmInfo>();

            Text = "Add bridged Supervertaler Workbench TMs";
            Icon = IconHelper.AppIcon;
            Size = new Size(560, 420);
            MinimumSize = new Size(420, 280);
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            var lbl = new Label
            {
                Text = "Tick the TMs you want to add to this project. " +
                       "All listed TMs are flagged \"Bridge\" in Supervertaler Workbench.",
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 40,
                Padding = new Padding(12, 10, 12, 6),
            };

            _list = new CheckedListBox
            {
                Dock = DockStyle.Fill,
                CheckOnClick = true,
                IntegralHeight = false,
                Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            };
            foreach (var tm in _tms)
            {
                var langSummary = (tm.SourceLang ?? "?") + " → " + (tm.TargetLang ?? "?");
                _list.Items.Add(
                    tm.Name + "   [" + langSummary + ", " + tm.EntryCount + " TUs]",
                    isChecked: false);
            }

            var btnOk = new Button { Text = "Add", DialogResult = DialogResult.OK, Width = 90 };
            var btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
            btnOk.Click += (s, e) =>
            {
                SelectedTms.Clear();
                for (int i = 0; i < _list.Items.Count; i++)
                {
                    if (_list.GetItemChecked(i))
                        SelectedTms.Add(_tms[i]);
                }
            };

            var btnPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 44,
                Padding = new Padding(8, 6, 8, 6),
            };
            btnPanel.Controls.Add(btnOk);
            btnPanel.Controls.Add(btnCancel);

            // Order matters – Dock=Bottom controls are stacked in the order
            // they're added (last-added sits closest to the bottom edge).
            Controls.Add(_list);     // fills remaining space
            Controls.Add(lbl);       // top
            Controls.Add(btnPanel);  // bottom
            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }
    }
}
