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
    /// What SuperVault holds, where it lives, and the button that builds it. The build runs in the background, so the
    /// dialog can be closed while it works: Studio keeps using the old index (or none) until the new one is ready.
    /// </summary>
    internal sealed class SuperVaultDialog : Form
    {
        readonly VaultLibrary _library;
        readonly Label _status = new Label { AutoSize = false, Dock = DockStyle.Fill };
        readonly Button _build = new Button { AutoSize = true };
        readonly Button _cancelBuild = new Button { Text = "Stop building", AutoSize = true };
        readonly Timer _timer = new Timer { Interval = 1000 };

        public SuperVaultDialog(VaultLibrary library, bool adding)
        {
            _library = library;
            Text = adding ? "Add SuperVault" : "SuperVault";
            Icon = Supervertaler.Trados.Core.IconHelper.AppIcon;
            Font = new Font("Segoe UI", 9F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(600, 290);

            var intro = new Label
            {
                Dock = DockStyle.Top, Height = 64,
                Text = "SuperVault serves your Supervertaler Workbench memory to Trados: every language variant, in both directions " +
                       "(nl-BE > en-US, en-GB > nl-NL, ... all from the same units). Its matches never lower your project TMs' matches.\n" +
                       "It reads the Workbench database and never changes it.",
            };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(0, 6, 0, 0) };
            var ok = new Button { Text = adding ? "Add" : "Close", DialogResult = DialogResult.OK, AutoSize = true };
            buttons.Controls.Add(ok);
            if (adding) buttons.Controls.Add(new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true });
            buttons.Controls.Add(_cancelBuild);
            buttons.Controls.Add(_build);
            AcceptButton = ok;

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
            body.Controls.Add(_status);
            body.Controls.Add(intro);
            Controls.Add(body);
            Controls.Add(buttons);

            _build.Click += (s, e) => StartBuild();
            _cancelBuild.Click += (s, e) => _library.CancelBuild();
            _timer.Tick += (s, e) => Refresh2();
            Shown += (s, e) => { Refresh2(); _timer.Start(); };
            FormClosed += (s, e) => _timer.Dispose();
        }

        void StartBuild()
        {
            var db = VaultLibrary.DefaultDatabase;
            if (!File.Exists(db))
            {
                MessageBox.Show(this, "The Supervertaler Workbench database was not found at:\n" + db, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _library.StartBuild(db);
            Refresh2();
        }

        void Refresh2()
        {
            var s = _library.Status;
            var lines = new System.Text.StringBuilder();
            switch (s.State)
            {
                case VaultState.Ready:
                    lines.AppendLine("Index: " + s.Units.ToString("N0", CultureInfo.CurrentCulture) + " units" +
                                     (s.BuiltAt.HasValue ? ", built " + s.BuiltAt.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : ""));
                    break;
                case VaultState.Broken: lines.AppendLine(s.Message); break;
                default: lines.AppendLine("No index yet. Build it to start using SuperVault (about 5 minutes per 1.5 million units)."); break;
            }
            if (s.Building) lines.AppendLine("Building: " + s.BuildProgress + ". You can close this window; Studio keeps working meanwhile.");
            else if (s.LastBuildError != null) lines.AppendLine("The last build failed: " + s.LastBuildError);
            lines.AppendLine();
            lines.AppendLine("Workbench database: " + VaultLibrary.DefaultDatabase);
            lines.AppendLine("Index folder: " + _library.Root);
            lines.AppendLine("New Workbench units reach SuperVault when the index is rebuilt.");
            _status.Text = lines.ToString();
            _build.Text = s.State == VaultState.Ready ? "Rebuild index" : "Build index";
            _build.Enabled = !s.Building;
            _cancelBuild.Visible = s.Building;
        }
    }
}
