using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using Supervertaler.Core.SuperVault;

namespace Supervertaler.Trados.SuperVault
{
    /// <summary>Adds SuperVault to a project (Use > SuperVault...) and opens its settings (Settings...).</summary>
    [TranslationProviderWinFormsUi(Id = "SupervertalerSuperVaultUi", Name = "SupervertalerSuperVaultUi", Description = "SupervertalerSuperVaultUi_Description")]
    public sealed class SuperVaultWinFormsUI : ITranslationProviderWinFormsUI
    {
        public string TypeName => "SuperVault...";
        public string TypeDescription => "One Supervertaler memory for every language variant, in both directions.";
        public bool SupportsEditing => true;

        public ITranslationProvider[] Browse(IWin32Window owner, LanguagePair[] languagePairs, ITranslationProviderCredentialStore credentialStore)
        {
            var provider = new SuperVaultProvider();
            using (var dlg = new SuperVaultDialog(provider.Library, adding: true))
                return dlg.ShowDialog(owner) == DialogResult.OK ? new ITranslationProvider[] { provider } : new ITranslationProvider[0];
        }

        public bool Edit(IWin32Window owner, ITranslationProvider translationProvider, LanguagePair[] languagePairs, ITranslationProviderCredentialStore credentialStore)
        {
            var library = (translationProvider as SuperVaultProvider)?.Library ?? VaultLibrary.For(VaultLibrary.DefaultRoot);
            using (var dlg = new SuperVaultDialog(library, adding: false)) dlg.ShowDialog(owner);
            return false;   // nothing about the provider itself changes
        }

        public bool GetCredentialsFromUser(IWin32Window owner, Uri translationProviderUri, string translationProviderState, ITranslationProviderCredentialStore credentialStore) => false;
        public bool SupportsTranslationProviderUri(Uri translationProviderUri) => translationProviderUri != null && translationProviderUri.Scheme == SuperVaultProvider.Scheme;

        public TranslationProviderDisplayInfo GetDisplayInfo(Uri translationProviderUri, string translationProviderState)
        {
            var icon = Supervertaler.Trados.Core.IconHelper.AppIcon ?? SystemIcons.Application;
            return new TranslationProviderDisplayInfo
            {
                Name = SuperVaultProvider.DisplayName,
                TooltipText = "SuperVault: your Supervertaler memory, every variant, both directions",
                TranslationProviderIcon = icon,
                SearchResultImage = new Icon(icon, 16, 16).ToBitmap(),
            };
        }
    }

    /// <summary>
    /// What SuperVault holds and where, and the buttons that fill it: import a TMX file, a Studio TM, or (if you have
    /// Supervertaler Workbench) its database. Imports and index builds run in the background, so the dialog can be
    /// closed while they work: Studio keeps using the index it has until the new one is ready.
    /// </summary>
    internal sealed class SuperVaultDialog : Form
    {
        readonly VaultLibrary _library;
        readonly Label _status = new Label { AutoSize = false, Dock = DockStyle.Fill };
        readonly Button _importTmx = new Button { Text = "Import TMX...", AutoSize = true };
        readonly Button _importTm = new Button { Text = "Import Studio TM...", AutoSize = true };
        readonly Button _importWorkbench = new Button { Text = "Import from Workbench", AutoSize = true };
        readonly Button _rebuild = new Button { Text = "Rebuild index", AutoSize = true };
        readonly Button _stop = new Button { Text = "Stop", AutoSize = true };
        readonly Timer _timer = new Timer { Interval = 1000 };
        long _storeUnits = -1;
        bool _outOfDate;
        DateTime _storeCheckedAt;

        public SuperVaultDialog(VaultLibrary library, bool adding)
        {
            _library = library;
            Text = adding ? "Add SuperVault" : "SuperVault";
            Icon = Supervertaler.Trados.Core.IconHelper.AppIcon;
            Font = new Font("Segoe UI", 9F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(660, 330);

            var intro = new Label
            {
                Dock = DockStyle.Top, Height = 66,
                Text = "SuperVault is one memory for every language variant, in both directions: an English–Dutch unit serves " +
                       "nl-BE > en-US, en-GB > nl-NL and the rest. Fill it by importing TMX files or Studio TMs; duplicates are stored once. " +
                       "Its matches never lower your project TMs' matches.",
            };
            var imports = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, Padding = new Padding(0, 4, 0, 0) };
            imports.Controls.AddRange(new Control[] { _importTmx, _importTm, _importWorkbench, _rebuild, _stop });
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(0, 6, 0, 0) };
            var ok = new Button { Text = adding ? "Add" : "Close", DialogResult = DialogResult.OK, AutoSize = true };
            buttons.Controls.Add(ok);
            if (adding) buttons.Controls.Add(new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true });
            AcceptButton = ok;

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
            body.Controls.Add(_status);
            body.Controls.Add(intro);
            Controls.Add(body);
            Controls.Add(imports);
            Controls.Add(buttons);

            _importTmx.Click += (s, e) => PickAndImport("TMX files (*.tmx)|*.tmx|All files (*.*)|*.*", (store, path, p, c) => VaultTmx.Import(store, path, p, c));
            _importTm.Click += (s, e) => PickAndImport("Studio translation memories (*.sdltm)|*.sdltm", (store, path, p, c) => SuperVaultImport.Sdltm(store, path, p, c));
            _importWorkbench.Click += (s, e) => Start(_library.StartImport((store, p, c) => VaultStore.ImportWorkbench(store, VaultLibrary.WorkbenchDatabase, p, c)));
            _rebuild.Click += (s, e) => Start(_library.StartBuild());
            _stop.Click += (s, e) => _library.CancelBuild();
            _timer.Tick += (s, e) => Refresh2();
            Shown += (s, e) => { Refresh2(); _timer.Start(); };
            FormClosed += (s, e) => _timer.Dispose();
        }

        void PickAndImport(string filter, Func<string, string, Action<string>, System.Threading.CancellationToken, VaultImportResult> importer)
        {
            using (var dlg = new OpenFileDialog { Filter = filter, Multiselect = true, Title = "Import into SuperVault" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var files = dlg.FileNames;
                // All selected files in one job: one import transaction each, one index build at the end.
                Start(_library.StartImport((store, p, c) =>
                {
                    var total = new VaultImportResult();
                    foreach (var f in files)
                    {
                        var r = importer(store, f, p, c);
                        total.Read += r.Read; total.Added += r.Added; total.Duplicates += r.Duplicates; total.Skipped += r.Skipped;
                    }
                    return total;
                }));
            }
        }

        void Start(bool started)
        {
            if (!started) MessageBox.Show(this, "SuperVault is already busy importing or building. Wait for it to finish.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            _storeCheckedAt = DateTime.MinValue;
            Refresh2();
        }

        void Refresh2()
        {
            var s = _library.Status;
            // The store is a file to open: look at it every 5 s at most, and when a job ends.
            if (_storeUnits < 0 || (!s.Building && (DateTime.UtcNow - _storeCheckedAt).TotalSeconds > 5))
            {
                try { _storeUnits = _library.StoreCount(); _outOfDate = _library.IndexOutOfDate(); }
                catch (Exception ex) { _storeUnits = 0; _outOfDate = false; Supervertaler.Trados.Core.DiagnosticLog.WriteAlways("SuperVault", "Store check failed: " + ex.Message); }
                _storeCheckedAt = DateTime.UtcNow;
            }
            var lines = new System.Text.StringBuilder();
            lines.AppendLine("Memory: " + _storeUnits.ToString("N0", CultureInfo.CurrentCulture) + " units");
            switch (s.State)
            {
                case VaultState.Ready:
                    lines.AppendLine("Index: " + s.Units.ToString("N0", CultureInfo.CurrentCulture) + " units" +
                                     (s.BuiltAt.HasValue ? ", built " + s.BuiltAt.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "") +
                                     (_outOfDate && !s.Building ? ". Out of date: click Rebuild index." : ""));
                    break;
                case VaultState.Broken: lines.AppendLine(s.Message); break;
                default: lines.AppendLine(_storeUnits > 0 ? "No index yet: click Rebuild index." : "Empty. Import a TMX file or a Studio TM to start."); break;
            }
            if (s.Building) lines.AppendLine("Working: " + s.BuildProgress + ". You can close this window; Studio keeps working meanwhile.");
            else if (_library.LastJobResult != null) lines.AppendLine("Last import or build: " + _library.LastJobResult);
            lines.AppendLine();
            lines.AppendLine("Stored in: " + _library.Root);
            _status.Text = lines.ToString();
            _importTmx.Enabled = _importTm.Enabled = _rebuild.Enabled = !s.Building;
            _importWorkbench.Visible = File.Exists(VaultLibrary.WorkbenchDatabase);
            _importWorkbench.Enabled = !s.Building;
            _rebuild.Enabled = !s.Building && _storeUnits > 0;
            _stop.Visible = s.Building;
        }
    }
}
