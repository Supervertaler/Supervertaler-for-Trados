using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using Supervertaler.Core.SuperVault;
using Supervertaler.Trados.Core;

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
        readonly Label _intro = new Label { Dock = DockStyle.Top };
        readonly Panel _body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        readonly FlowLayoutPanel _imports = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, Padding = new Padding(0, 4, 0, 0) };
        readonly FlowLayoutPanel _buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(0, 6, 0, 0) };
        readonly Button _importTmx = new Button { Text = "Import TMX...", AutoSize = true };
        readonly Button _importTm = new Button { Text = "Import Studio TM...", AutoSize = true };
        readonly Button _importWorkbench = new Button { Text = "Import from Workbench", AutoSize = true };
        readonly Button _rebuild = new Button { Text = "Rebuild index", AutoSize = true };
        readonly Button _stop = new Button { Text = "Stop", AutoSize = true };
        readonly Timer _timer = new Timer { Interval = 1000 };
        long _storeUnits = -1;
        bool _outOfDate;
        long _learnWaiting;
        DateTime _storeCheckedAt;

        public SuperVaultDialog(VaultLibrary library, bool adding)
        {
            _library = library;
            Text = adding ? "Add SuperVault" : "SuperVault";
            Icon = Supervertaler.Trados.Core.IconHelper.AppIcon;
            Font = new Font("Segoe UI", 9F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            // The title-bar "?" (WinForms shows it only without the maximise/minimise boxes) and F1 open the Help page.
            HelpButton = true;
            HelpButtonClicked += (s, e) => { e.Cancel = true; HelpSystem.OpenHelp(HelpSystem.Topics.SuperVault); };
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.F1) { e.Handled = true; HelpSystem.OpenHelp(HelpSystem.Topics.SuperVault); } };
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(660, 360);

            _intro.Text = "SuperVault is one memory for every language variant, in both directions: an English–Dutch unit serves " +
                       "nl-BE > en-US, en-GB > nl-NL and the rest. Fill it by importing TMX files or Studio TMs; duplicates are stored once. " +
                       "With its Update box ticked in the project's TM settings, the segments you confirm are added as you work. " +
                       "Its matches never lower your project TMs' matches.";
            _imports.Controls.AddRange(new Control[] { _importTmx, _importTm, _importWorkbench, _rebuild, _stop });
            var ok = new Button { Text = adding ? "Add" : "Close", DialogResult = DialogResult.OK, AutoSize = true };
            _buttons.Controls.Add(ok);
            if (adding) _buttons.Controls.Add(new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true });
            AcceptButton = ok;

            _body.Controls.Add(_status);
            _body.Controls.Add(_intro);
            Controls.Add(_body);
            Controls.Add(_imports);
            Controls.Add(_buttons);

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
                try { _storeUnits = _library.StoreCount(); _outOfDate = _library.IndexOutOfDate(); _learnWaiting = _library.LearnWaiting(); }
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
                                     (_outOfDate && !s.Building && _learnWaiting == 0 && s.LearnError == null ? ". Out of date: click Rebuild index." : ""));
                    break;
                case VaultState.Broken: lines.AppendLine(s.Message); break;
                default: lines.AppendLine(_storeUnits > 0 ? "No index yet: it is built when you next confirm a segment, or click Rebuild index." : "Empty. It fills as you confirm segments (tick SuperVault's Update box in the project's TM settings), or import a TMX file or a Studio TM."); break;
            }
            if (s.Learned.Total > 0 || _learnWaiting > 0 || s.LearnError != null)
                lines.AppendLine("Since Studio started, " + s.Learned +
                                 (_learnWaiting > 0 ? "; " + _learnWaiting.ToString("N0", CultureInfo.CurrentCulture) + " more waiting to be stored" : "") +
                                 (s.LearnError != null ? ". Last attempt failed (" + s.LearnError + "); they are kept and tried again." : "."));
            if (s.Building) lines.AppendLine("Working: " + s.BuildProgress + ". You can close this window; Studio keeps working meanwhile.");
            else if (_library.LastJobResult != null) lines.AppendLine("Last import or build: " + _library.LastJobResult);
            lines.AppendLine();
            lines.AppendLine("Stored in: " + _library.Root);
            _status.Text = lines.ToString();
            FitText();
            _importTmx.Enabled = _importTm.Enabled = _rebuild.Enabled = !s.Building;
            _importWorkbench.Visible = File.Exists(VaultLibrary.WorkbenchDatabase);
            _importWorkbench.Enabled = !s.Building;
            _rebuild.Enabled = !s.Building && _storeUnits > 0;
            _stop.Visible = s.Building;
        }

        /// <summary>
        /// Sizes the description to its text and makes the window tall enough for the description and the status, so
        /// neither is cut off at any display scaling or font size (fixed heights cut the description off at 125%+).
        /// The window only grows, so it doesn't jump while the status changes.
        /// </summary>
        internal void FitText()
        {
            int width = _body.ClientSize.Width - _body.Padding.Horizontal;
            if (width <= 0) return;
            const TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl;
            int line = TextRenderer.MeasureText("Xg", Font, new Size(width, int.MaxValue), flags).Height;
            _intro.Height = TextRenderer.MeasureText(_intro.Text, Font, new Size(width, int.MaxValue), flags).Height + line / 2;
            int status = TextRenderer.MeasureText(_status.Text ?? "", Font, new Size(width, int.MaxValue), flags).Height;
            // The import buttons wrap to a second row when the font is large: make room for every row.
            _imports.Height = Math.Max(_imports.Height, _imports.GetPreferredSize(new Size(ClientSize.Width, 0)).Height);
            // A line to spare: a long folder path breaks inside the word, where measuring and drawing can differ by a line.
            int needed = _body.Padding.Vertical + _intro.Height + status + line + _imports.Height + _buttons.Height;
            if (ClientSize.Height < needed) ClientSize = new Size(ClientSize.Width, needed);
        }

        /// <summary>Tests: the description and status labels, to check nothing is cut off.</summary>
        internal Label IntroLabel => _intro;
        internal Label StatusLabel => _status;
    }
}
