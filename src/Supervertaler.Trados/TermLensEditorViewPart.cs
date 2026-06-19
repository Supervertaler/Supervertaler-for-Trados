using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.Desktop.IntegrationApi.Interfaces;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Sdl.ProjectAutomation.FileBased;
using Supervertaler.Trados.Controls;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Licensing;
using Supervertaler.Trados.Models;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados
{
    /// <summary>
    /// Trados Studio editor ViewPart that docks the TermLens panel above the editor.
    /// Listens to segment changes and updates the terminology display accordingly.
    /// </summary>
    [ViewPart(
        Id = "TermLensEditorViewPart",
        Name = "TermLens",
        Description = "Terminology display for Trados Studio",
        Icon = "TermLensIcon"
    )]
    [ViewPartLayout(typeof(EditorController), Dock = DockType.Top, Pinned = true)]
    public class TermLensEditorViewPart : AbstractViewPartController
    {
        private static readonly Lazy<TermLensControl> _control =
            new Lazy<TermLensControl>(() => new TermLensControl());

        private static readonly Lazy<MainPanelControl> _mainPanel =
            new Lazy<MainPanelControl>(() => new MainPanelControl(_control.Value));

        // Single instance – Trados creates exactly one ViewPart of each type.
        // Used by AddTermAction to trigger a reload after inserting a term.
        private static TermLensEditorViewPart _currentInstance;

        private EditorController _editorController;
        private IStudioDocument _activeDocument;
        private TermLensSettings _settings;

        // MultiTerm integration
        private List<MultiTermTermbaseConfig> _multiTermConfigs;
        private List<MultiTermTermbaseInfo> _multiTermInfos;
        private List<TerminologyProviderFallback> _fallbackProviders;
        private Dictionary<string, DateTime> _multiTermFileTimestamps;

        // Prompt library (shared – used by settings dialog)
        private PromptLibrary _promptLibrary;

        // Per-project settings – tracks the active project's .sdlproj path
        private string _currentProjectPath;
        private string _currentProjectName;

        // --- Alt+digit shortcut state machine ---
        private static int? _pendingDigit;
        private static int _pendingRepeatCount;   // repeated mode: how many times same digit pressed
        private static int _pendingAccumulated;    // sequential mode: accumulated number so far
        private static System.Windows.Forms.Timer _chordTimer;

        // --- Ctrl-tap TermPicker (memoQ-style) ---
        private static CtrlTapFilter _ctrlTapFilter;

        // ───────────────────────────────────────────────────────────────
        // v4.19.113: Termbase DB file watcher (cross-process auto-refresh)
        // ───────────────────────────────────────────────────────────────
        // When the same SQLite database is open in Workbench and a user
        // edits terms there (or in any other process), the plugin's
        // in-memory term index goes stale until the next manual
        // NotifyTermAdded(). Mirrors the Workbench v1.10.69
        // _setup_termbase_db_watcher pattern:
        //   1. FileSystemWatcher on the DB file. fileChanged restarts
        //      a 2-second debounce timer.
        //   2. When the timer fires, snapshot the termbase tables
        //      (COUNT + MAX(id) on termbases; COUNT + MAX(id) +
        //      MAX(modified_date) on termbase_terms) and compare to the
        //      snapshot taken at the end of the last own-reload.
        //   3. Only reload if the snapshot genuinely differs — so
        //      routine TM / project-metadata writes that tick the file
        //      mtime don't cause a visible TermLens flicker.
        //   4. Every own-reload path (NotifyTermAdded / Inserted /
        //      Deleted) refreshes the snapshot afterwards, so when the
        //      watcher debounce later fires for the same write, the
        //      comparison sees no change and skips → zero spurious
        //      rebuilds from own-writes.
        private FileSystemWatcher _dbWatcher;
        private System.Windows.Forms.Timer _dbDebounceTimer;
        private string _watchedDbPath;
        // Snapshot tuple: (termbasesCount, termbasesMaxId, termsCount,
        // termsMaxId, termsMaxModified). Compared as-is.
        private Tuple<long, long, long, long, string> _dbSnapshot;

        protected override IUIControl GetContentControl()
        {
            return _mainPanel.Value;
        }

        protected override void Initialize()
        {
            _currentInstance = this;

            // Ctrl-tap filter: pressing and releasing Ctrl alone opens the
            // floating TermLens popup. Faster than the picker dialog and
            // shows the segment in context, so Ctrl-tap is the preferred
            // primary trigger. The picker dialog is still reachable via
            // Ctrl+Shift+P for users who want the list-based UI.
            if (_ctrlTapFilter == null)
            {
                _ctrlTapFilter = new CtrlTapFilter(() => HandleTermLensPopup());
                System.Windows.Forms.Application.AddMessageFilter(_ctrlTapFilter);
            }

            // License check – show/hide activation overlay based on tier.
            // Named handler (not a lambda) so Dispose can unsubscribe; the
            // singletons LicenseManager.Instance / _control would otherwise
            // hold dead view-part instances alive across document reopens
            // and fan out every license-state notification to all of them.
            LicenseManager.Instance.LicenseStateChanged += OnLicenseStateChanged;

            // Check for plugin updates in the background.
            // Runs regardless of license state – even expired-trial users should
            // know about new versions.
            var ctrl = _control.Value;
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var update = await UpdateChecker.CheckForUpdateAsync(_settings);
                    if (!update.HasValue) return;

                    // Wait for the control's window handle to be created.
                    // Initialize() runs before the control is parented in the
                    // Trados docking framework, so IsHandleCreated may still be
                    // false.  Poll briefly – the handle is created within a
                    // second or two once Trados finishes layout.
                    for (int i = 0; i < 30 && !ctrl.IsHandleCreated; i++)
                        await System.Threading.Tasks.Task.Delay(500);

                    if (!ctrl.IsHandleCreated) return; // give up after 15 s

                    ctrl.BeginInvoke(new Action(() =>
                    {
                        ShowUpdateDialog(update.Value.version, update.Value.url, update.Value.pluginUrl);
                    }));
                }
                catch
                {
                    // Silently ignore – network errors, parse errors, etc.
                }
            });

            // Anonymous usage statistics – opt-in dialog + background ping
            ShowUsageStatisticsOptIn(ctrl);
            System.Threading.Tasks.Task.Run(async () =>
            {
                try { await UsageStatistics.SendPingAsync(); } catch { }
            });

            // Load persisted settings – needed even when unlicensed so the
            // settings dialog can open and let the user enter a license key.
            _settings = TermLensSettings.Load();

            // Apply global UI scale factor before any controls are created.
            // SeedSystemScale picks up the current Windows DPI (1.0 at 100%,
            // 1.5 at 150%, etc.) so plugin layouts don't squish on hi-DPI
            // displays even when the user hasn't dialled in a custom UI
            // scale via TermLens settings. UserFactor stacks on top.
            UiScale.SeedSystemScale();
            UiScale.UserFactor = _settings.UiScaleFactor;

            // Initialize prompt library and seed default prompts on first run
            _promptLibrary = new PromptLibrary();
            _promptLibrary.EnsureDefaultPrompts();

            // Wire up the gear/settings button – must be done even when
            // unlicensed so users can open Settings → License to activate.
            _mainPanel.Value.SettingsRequested += OnSettingsRequested;

            // Wire up editor controller and load termbases regardless of tier –
            // AssistantOnly users need termbase terms for AI prompt context even
            // though the TermLens UI panel is locked.
            _editorController = SdlTradosStudio.Application.GetController<EditorController>();

            if (_editorController != null)
            {
                _editorController.ActiveDocumentChanged += OnActiveDocumentChanged;

                // If a document is already open, wire up to it immediately
                if (_editorController.ActiveDocument != null)
                {
                    _activeDocument = _editorController.ActiveDocument;
                    _activeDocument.ActiveSegmentChanged += OnActiveSegmentChanged;
                    _activeDocument.ActiveFilePropertiesChanged += OnActiveFilePropertiesChanged;

                    // Apply per-project settings if available
                    ApplyProjectSettingsFromDocument(_activeDocument);
                }
            }

            // Load termbase + project .sdltb/.ttb termbases on a background
            // thread so the editor view part can finish Initialize quickly and
            // Studio's UI becomes responsive immediately. Cold-disk SQLite reads
            // for 92K+ Supervertaler terms plus several project termbases take
            // ~2 minutes on x64 Studio 2026; doing that synchronously blocked
            // editor activation for the whole duration. After the background
            // load completes, marshal a UI refresh so chips appear on whatever
            // segment is currently active.
            //
            // While loading is in progress, _matcher is empty so segment clicks
            // simply show no term chips yet — the user can still scroll, edit,
            // and translate normally. UI updates inside the loaders (status
            // label, MergeMultiTermEntries) already marshal to the UI thread.
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    LoadTermbase();
                    LoadMultiTermTermbases();
                    MigrateGlobalAiTermbaseListIfNeeded();
                    SafeInvoke(UpdateFromActiveSegment);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[TermLens] Background termbase load failed: " + ex);
                }
            });

            // v4.19.113: wire the new ↻ refresh button in the TermLens
            // header to the same reload path F5 uses, and install a
            // FileSystemWatcher on the SQLite DB so cross-process edits
            // (typically by the Workbench desktop app sharing the same
            // DB) auto-trigger a reload after a 2-second debounce.
            try
            {
                _control.Value.RefreshRequested -= OnTermLensRefreshRequested;
                _control.Value.RefreshRequested += OnTermLensRefreshRequested;
            }
            catch { /* control not yet realised */ }
            try
            {
                SetupTermbaseDbWatcher();
            }
            catch { /* watcher is best-effort; F5 + ↻ still work */ }

            // Start periodic check for MultiTerm config changes (e.g. user
            // toggles Enabled in Project Settings → Termbases mid-session)
            StartMultiTermConfigTimer();

            if (!LicenseManager.Instance.HasTier1Access)
            {
                _control.Value.ShowLicenseRequired();
                return;
            }

            // ─── TermLens UI wiring (Tier1+ only) ──────────────────────

            // Wire up term insertion – when user clicks a translation in the panel
            _control.Value.TermInsertRequested += OnTermInsertRequested;

            // Wire up right-click edit/delete/non-translatable on term blocks
            _control.Value.TermEditRequested += OnTermEditRequested;
            _control.Value.TermDeleteRequested += OnTermDeleteRequested;
            _control.Value.TermNonTranslatableToggled += OnTermNonTranslatableToggled;

            // Wire up font size changes from the A+/A- buttons in the panel header
            _control.Value.FontSizeChanged += OnFontSizeChanged;

            // Apply persisted font size
            _control.Value.SetFontSize(_settings.PanelFontSize);

            // Apply shortcut style to badge rendering
            TermBlock.UseRepeatedDigitBadges = _settings.TermShortcutStyle == "repeated";

            // Display the current segment immediately (even without a termbase, show all words)
            UpdateFromActiveSegment();
        }

        private void LoadTermbase(bool forceReload = false)
        {
            var disabled = _settings.DisabledTermbaseIds != null && _settings.DisabledTermbaseIds.Count > 0
                ? new HashSet<long>(_settings.DisabledTermbaseIds)
                : null;
            var globalCaseSensitive = _settings.CaseSensitiveMatching;

            // Push project termbase ID to the control for pink/blue coloring
            _control.Value.SetProjectTermbaseId(_settings.ProjectTermbaseId);

            var projectSourceLang = GetDocumentSourceLanguage();

            // Suffix-tolerant matching for Korean/Japanese (particles attach to
            // nouns without a space). Honours the three-state user setting,
            // defaulting to on for those source languages.
            _control.Value.SetSuffixTolerant(_settings.ResolveSuffixTolerant(projectSourceLang));

            // 1. Use the saved termbase path if set and the file exists
            if (!string.IsNullOrEmpty(_settings.TermbasePath) && File.Exists(_settings.TermbasePath))
            {
                _control.Value.LoadTermbase(_settings.TermbasePath, disabled, forceReload, globalCaseSensitive, projectSourceLang);
                return;
            }

            // 2. Fallback: auto-detect Supervertaler's default locations
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Supervertaler_Data", "resources", "supervertaler.db"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Supervertaler", "resources", "supervertaler.db"),
            };

            foreach (var path in candidates)
            {
                if (File.Exists(path))
                {
                    _control.Value.LoadTermbase(path, disabled, forceReload, globalCaseSensitive, projectSourceLang);
                    return;
                }
            }
        }

        /// <summary>
        /// Detects MultiTerm .sdltb termbases from the active Trados project
        /// and loads their terms into the TermMatcher index.
        /// </summary>
        private void LoadMultiTermTermbases()
        {
            try
            {
                // Clear previous MultiTerm entries from the index
                _control.Value.ClearMultiTermEntries();
                DisposeFallbackProviders();
                _multiTermConfigs = null;
                _multiTermInfos = null;
                _multiTermFileTimestamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

                if (_activeDocument == null) return;

                _multiTermConfigs = MultiTermProjectDetector.DetectTermbases(_activeDocument);

                if (_multiTermConfigs.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[TermLens] No MultiTerm termbases detected in project");
                    DiagnosticLog.Log("MultiTerm", "LoadMultiTermTermbases: detector returned 0 termbases — nothing to load (TermLens MultiTerm hits will be empty).");
                    return;
                }
                System.Diagnostics.Debug.WriteLine($"[TermLens] Detected {_multiTermConfigs.Count} MultiTerm termbase(s)");
                DiagnosticLog.Log("MultiTerm", $"LoadMultiTermTermbases: detector returned {_multiTermConfigs.Count} termbase(s); attempting load.");

                // Filter out termbases disabled in Supervertaler settings
                var disabledMtIds = _settings.DisabledMultiTermIds ?? new List<long>();

                var mergedIndex = new Dictionary<string, List<TermEntry>>(StringComparer.OrdinalIgnoreCase);
                var infos = new List<MultiTermTermbaseInfo>();
                var failedConfigs = new List<MultiTermTermbaseConfig>();

                foreach (var config in _multiTermConfigs)
                {
                    if (disabledMtIds.Contains(config.SyntheticId))
                        continue;

                    try
                    {
                        using (var reader = TermbaseReaderFactory.Create(config.FilePath))
                        {
                            if (reader.Open())
                            {
                                var index = reader.LoadAllTerms(
                                    config.SourceIndexName, config.TargetIndexName,
                                    config.SyntheticId, config.TermbaseName);

                                // Merge into combined index
                                foreach (var kvp in index)
                                {
                                    if (mergedIndex.TryGetValue(kvp.Key, out var existing))
                                        existing.AddRange(kvp.Value);
                                    else
                                        mergedIndex[kvp.Key] = new List<TermEntry>(kvp.Value);
                                }

                                infos.Add(reader.GetTermbaseInfo(
                                    config.SourceIndexName, config.TargetIndexName,
                                    config.SyntheticId));

                                // Record file timestamp for change detection
                                try { _multiTermFileTimestamps[config.FilePath] = File.GetLastWriteTimeUtc(config.FilePath); }
                                catch { /* ignore timestamp errors */ }
                            }
                            else
                            {
                                // Direct access failed – try API fallback later
                                failedConfigs.Add(config);
                                infos.Add(new MultiTermTermbaseInfo
                                {
                                    SyntheticId = config.SyntheticId,
                                    FilePath = config.FilePath,
                                    Name = config.TermbaseName,
                                    SourceIndexName = config.SourceIndexName,
                                    TargetIndexName = config.TargetIndexName,
                                    LoadMode = MultiTermLoadMode.Failed
                                });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[TermLens] Failed to load MultiTerm '{config.TermbaseName}': {ex.Message}");
                        failedConfigs.Add(config);
                        infos.Add(new MultiTermTermbaseInfo
                        {
                            SyntheticId = config.SyntheticId,
                            FilePath = config.FilePath,
                            Name = config.TermbaseName,
                            SourceIndexName = config.SourceIndexName,
                            TargetIndexName = config.TargetIndexName,
                            LoadMode = MultiTermLoadMode.Failed
                        });
                    }
                }

                // Try API fallback for termbases that failed direct access
                if (failedConfigs.Count > 0)
                    SetupFallbackProviders(failedConfigs, infos);

                _multiTermInfos = infos;

                System.Diagnostics.Debug.WriteLine($"[TermLens] MultiTerm merged index: {mergedIndex.Count} keys, {infos.Count} termbases, {failedConfigs.Count} failed");
                DiagnosticLog.Log("MultiTerm",
                    $"LoadMultiTermTermbases: merged index has {mergedIndex.Count} key(s) across {infos.Count} termbase(s) " +
                    $"({failedConfigs.Count} needed the API fallback). These feed both TermLens and (if AI-enabled) the prompt.");
                if (mergedIndex.Count > 0)
                    SafeInvoke(() => _control.Value.MergeMultiTermEntries(mergedIndex, infos));
                else if (failedConfigs.Count > 0)
                    System.Diagnostics.Debug.WriteLine("[TermLens] All MultiTerm termbases failed direct load – relying on fallback providers");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TermLens] MultiTerm loading failed: {ex}");
                try
                {
                    var logDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Supervertaler.Trados");
                    Directory.CreateDirectory(logDir);
                    File.AppendAllText(Path.Combine(logDir, "multiterm_debug.log"),
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] LoadMultiTermTermbases failed: {ex}\n\n");
                }
                catch { /* ignore log write errors */ }
            }
        }

        /// <summary>
        /// Sets up API-based fallback providers for termbases that couldn't be opened via OleDb.
        /// Uses Trados's ITerminologyProviderManager to try multiple URI schemes.
        /// </summary>
        private void SetupFallbackProviders(
            List<MultiTermTermbaseConfig> failedConfigs,
            List<MultiTermTermbaseInfo> infos)
        {
            try
            {
                var manager = ResolveTerminologyProviderManager();
                if (manager == null) return;

                var factories = DiscoverTerminologyProviderFactories(manager);

                foreach (var config in failedConfigs)
                {
                    try
                    {
                        var candidateUris = BuildCandidateUris(config);

                        Sdl.Terminology.TerminologyProvider.Core.ITerminologyProvider provider = null;

                        // Strategy 1: Check each factory's SupportsTerminologyProviderUri()
                        foreach (var factory in factories)
                        {
                            foreach (var uri in candidateUris)
                            {
                                try
                                {
                                    if (factory.SupportsTerminologyProviderUri(uri))
                                    {
                                        // Use the single-argument overload — the two-argument
                                        // (Uri, ITerminologyProviderCredentialStore) form is obsolete
                                        // in Studio 2024 and removed in Studio 2026.
                                        provider = factory.CreateTerminologyProvider(uri);
                                        if (provider != null) break;
                                    }
                                }
                                catch { }
                            }
                            if (provider != null) break;
                        }

                        // Strategy 2: Try manager.GetTerminologyProvider() with each URI
                        if (provider == null)
                        {
                            foreach (var uri in candidateUris)
                            {
                                try
                                {
                                    provider = manager.GetTerminologyProvider(uri);
                                    if (provider != null) break;
                                }
                                catch { }
                            }
                        }

                        if (provider != null)
                        {
                            var fallback = new TerminologyProviderFallback(
                                provider,
                                config.SourceIndexName,
                                config.TargetIndexName,
                                config.TermbaseName,
                                config.SyntheticId);

                            if (fallback.IsAvailable)
                            {
                                if (_fallbackProviders == null)
                                    _fallbackProviders = new List<TerminologyProviderFallback>();
                                _fallbackProviders.Add(fallback);

                                foreach (var info in infos)
                                {
                                    if (info.SyntheticId == config.SyntheticId)
                                    {
                                        info.LoadMode = MultiTermLoadMode.TerminologyProviderApi;
                                        break;
                                    }
                                }
                            }
                            else
                            {
                                fallback.Dispose();
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// Discovers ITerminologyProviderFactory instances from the manager or loaded assemblies.
        /// </summary>
        private List<Sdl.Terminology.TerminologyProvider.Core.ITerminologyProviderFactory> DiscoverTerminologyProviderFactories(
            Sdl.Terminology.TerminologyProvider.Core.ITerminologyProviderManager manager)
        {
            var result = new List<Sdl.Terminology.TerminologyProvider.Core.ITerminologyProviderFactory>();
            var factoryType = typeof(Sdl.Terminology.TerminologyProvider.Core.ITerminologyProviderFactory);

            try
            {
                var mgrType = manager.GetType();
                var flags = System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.NonPublic;

                // Search properties for factory collections
                foreach (var prop in mgrType.GetProperties(flags))
                {
                    try
                    {
                        if (prop.PropertyType.Name.Contains("IEnumerable") ||
                            prop.PropertyType.Name.Contains("List") ||
                            prop.PropertyType.Name.Contains("Collection") ||
                            prop.PropertyType.Name.Contains("Factory"))
                        {
                            var val = prop.GetValue(manager);
                            if (val is System.Collections.IEnumerable enumerable)
                            {
                                foreach (var item in enumerable)
                                {
                                    if (item != null && factoryType.IsAssignableFrom(item.GetType()))
                                        result.Add((Sdl.Terminology.TerminologyProvider.Core.ITerminologyProviderFactory)item);
                                }
                            }
                        }
                    }
                    catch { }
                }

                // Search fields for factory collections
                foreach (var field in mgrType.GetFields(flags))
                {
                    try
                    {
                        if (field.FieldType.Name.Contains("Factory") ||
                            field.FieldType.Name.Contains("List") ||
                            field.FieldType.Name.Contains("Dictionary"))
                        {
                            var val = field.GetValue(manager);
                            if (val is System.Collections.IEnumerable enumerable)
                            {
                                foreach (var item in enumerable)
                                {
                                    if (item != null && factoryType.IsAssignableFrom(item.GetType()))
                                        result.Add((Sdl.Terminology.TerminologyProvider.Core.ITerminologyProviderFactory)item);
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            // If no factories found via manager, scan loaded assemblies
            if (result.Count == 0)
            {
                try
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            if (!asm.FullName.Contains("Sdl") && !asm.FullName.Contains("MultiTerm"))
                                continue;

                            foreach (var type in asm.GetTypes())
                            {
                                if (type.IsAbstract || type.IsInterface) continue;
                                if (!factoryType.IsAssignableFrom(type)) continue;

                                try
                                {
                                    var instance = Activator.CreateInstance(type) as Sdl.Terminology.TerminologyProvider.Core.ITerminologyProviderFactory;
                                    if (instance != null)
                                        result.Add(instance);
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            return result;
        }

        /// <summary>
        /// Builds candidate URIs for a MultiTerm termbase config.
        /// </summary>
        private List<Uri> BuildCandidateUris(MultiTermTermbaseConfig config)
        {
            var uris = new List<Uri>();
            var filePath = config.FilePath;

            // Try to extract URI from SettingsXml
            if (!string.IsNullOrEmpty(config.SettingsXml))
            {
                try
                {
                    var xml = System.Xml.Linq.XElement.Parse(config.SettingsXml);
                    foreach (var el in xml.DescendantsAndSelf())
                    {
                        if (el.Name.LocalName.Contains("Uri") || el.Name.LocalName.Contains("uri") ||
                            el.Name.LocalName.Contains("Path") || el.Name.LocalName.Contains("path") ||
                            el.Name.LocalName.Contains("Location") || el.Name.LocalName.Contains("location"))
                        {
                            var text = el.Value?.Trim();
                            if (!string.IsNullOrEmpty(text))
                            {
                                try { uris.Add(new Uri(text)); }
                                catch { }
                            }
                        }
                        foreach (var attr in el.Attributes())
                        {
                            if (attr.Name.LocalName.Contains("uri") || attr.Name.LocalName.Contains("Uri") ||
                                attr.Name.LocalName.Contains("path") || attr.Name.LocalName.Contains("Path"))
                            {
                                try { uris.Add(new Uri(attr.Value)); }
                                catch { }
                            }
                        }
                    }
                }
                catch { }
            }

            // Try various URI formats for local .sdltb files
            var pathForward = filePath.Replace('\\', '/');
            var schemes = new[]
            {
                $"multiterm:///{pathForward}",
                $"multiterm://{pathForward}",
                $"multiterm:local:///{pathForward}",
                $"sdl-multiterm:///{pathForward}",
                $"glossary:///{pathForward}",
                $"file:///{pathForward}"
            };

            foreach (var s in schemes)
            {
                try
                {
                    var uri = new Uri(s);
                    if (!uris.Any(u => u.ToString() == uri.ToString()))
                        uris.Add(uri);
                }
                catch { }
            }

            return uris;
        }

        /// <summary>
        /// Resolves ITerminologyProviderManager via reflection (multiple strategies).
        /// </summary>
        private Sdl.Terminology.TerminologyProvider.Core.ITerminologyProviderManager ResolveTerminologyProviderManager()
        {
            var managerType = typeof(Sdl.Terminology.TerminologyProvider.Core.ITerminologyProviderManager);

            // Approach 1: Search app type hierarchy for manager properties or DI containers
            try
            {
                var app = SdlTradosStudio.Application;
                var type = app.GetType();
                var flags = System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.NonPublic |
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.DeclaredOnly;

                while (type != null && type != typeof(object))
                {
                    foreach (var prop in type.GetProperties(flags))
                    {
                        if (managerType.IsAssignableFrom(prop.PropertyType))
                        {
                            try
                            {
                                var val = prop.GetValue(app) as Sdl.Terminology.TerminologyProvider.Core.ITerminologyProviderManager;
                                if (val != null) return val;
                            }
                            catch { }
                        }

                        if (prop.PropertyType.Name.Contains("Container") ||
                            prop.PropertyType.Name.Contains("ServiceProvider") ||
                            prop.PropertyType.Name.Contains("ComponentContext") ||
                            prop.PropertyType.Name.Contains("Scope"))
                        {
                            try
                            {
                                var container = prop.GetValue(app);
                                if (container != null)
                                {
                                    var mgr = TryResolveFromContainer(container, managerType);
                                    if (mgr != null) return mgr;
                                }
                            }
                            catch { }
                        }
                    }

                    foreach (var field in type.GetFields(flags))
                    {
                        if (managerType.IsAssignableFrom(field.FieldType))
                        {
                            try
                            {
                                var val = field.GetValue(app) as Sdl.Terminology.TerminologyProvider.Core.ITerminologyProviderManager;
                                if (val != null) return val;
                            }
                            catch { }
                        }

                        if (field.FieldType.Name.Contains("Container") ||
                            field.FieldType.Name.Contains("ServiceProvider") ||
                            field.FieldType.Name.Contains("ComponentContext") ||
                            field.FieldType.Name.Contains("Scope") ||
                            field.FieldType.Name.Contains("Kernel") ||
                            field.FieldType.Name.Contains("Locator"))
                        {
                            try
                            {
                                var container = field.GetValue(app);
                                if (container != null)
                                {
                                    var mgr = TryResolveFromContainer(container, managerType);
                                    if (mgr != null) return mgr;
                                }
                            }
                            catch { }
                        }
                    }

                    type = type.BaseType;
                }
            }
            catch { }

            // Approach 2: Search loaded assemblies for singleton
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (!asm.FullName.Contains("Sdl.Terminology")) continue;

                        foreach (var t in asm.GetTypes())
                        {
                            if (t.IsAbstract || t.IsInterface) continue;
                            if (!managerType.IsAssignableFrom(t)) continue;

                            foreach (var prop in t.GetProperties(
                                System.Reflection.BindingFlags.Static |
                                System.Reflection.BindingFlags.Public |
                                System.Reflection.BindingFlags.NonPublic))
                            {
                                if (managerType.IsAssignableFrom(prop.PropertyType))
                                {
                                    try
                                    {
                                        var val = prop.GetValue(null) as Sdl.Terminology.TerminologyProvider.Core.ITerminologyProviderManager;
                                        if (val != null) return val;
                                    }
                                    catch { }
                                }
                            }

                            foreach (var field in t.GetFields(
                                System.Reflection.BindingFlags.Static |
                                System.Reflection.BindingFlags.Public |
                                System.Reflection.BindingFlags.NonPublic))
                            {
                                if (managerType.IsAssignableFrom(field.FieldType))
                                {
                                    try
                                    {
                                        var val = field.GetValue(null) as Sdl.Terminology.TerminologyProvider.Core.ITerminologyProviderManager;
                                        if (val != null) return val;
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            // Approach 3: Try direct construction
            try
            {
                var concreteType = typeof(Sdl.Terminology.TerminologyProvider.Core.ITerminologyProviderManager).Assembly
                    .GetType("Sdl.Terminology.TerminologyProvider.Core.TerminologyProviderManager");

                if (concreteType != null)
                {
                    foreach (var ctor in concreteType.GetConstructors(
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic))
                    {
                        if (ctor.GetParameters().Length == 0)
                        {
                            try
                            {
                                var val = ctor.Invoke(new object[0]) as Sdl.Terminology.TerminologyProvider.Core.ITerminologyProviderManager;
                                if (val != null) return val;
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Tries to resolve ITerminologyProviderManager from a DI container via Resolve() or GetService().
        /// </summary>
        private Sdl.Terminology.TerminologyProvider.Core.ITerminologyProviderManager TryResolveFromContainer(
            object container, Type serviceType)
        {
            var containerType = container.GetType();

            var resolveMethod = containerType.GetMethod("Resolve", new Type[] { typeof(Type) });
            if (resolveMethod != null)
            {
                try
                {
                    var val = resolveMethod.Invoke(container, new object[] { serviceType })
                        as Sdl.Terminology.TerminologyProvider.Core.ITerminologyProviderManager;
                    if (val != null) return val;
                }
                catch { }
            }

            var getServiceMethod = containerType.GetMethod("GetService", new Type[] { typeof(Type) });
            if (getServiceMethod != null)
            {
                try
                {
                    var val = getServiceMethod.Invoke(container, new object[] { serviceType })
                        as Sdl.Terminology.TerminologyProvider.Core.ITerminologyProviderManager;
                    if (val != null) return val;
                }
                catch { }
            }

            return null;
        }

        private void DisposeFallbackProviders()
        {
            if (_fallbackProviders != null)
            {
                foreach (var fb in _fallbackProviders)
                {
                    try { fb.Dispose(); }
                    catch { /* ignore */ }
                }
                _fallbackProviders = null;
            }
        }

        /// <summary>
        /// Returns detected MultiTerm termbase metadata for the settings dialog.
        /// </summary>
        public static List<MultiTermTermbaseInfo> GetMultiTermInfos()
        {
            return _currentInstance?._multiTermInfos ?? new List<MultiTermTermbaseInfo>();
        }

        /// <summary>
        /// Like <see cref="GetMultiTermInfos"/>, but resilient for the Settings dialog:
        /// if the live editor view part hasn't loaded MultiTerm info (e.g. the Settings
        /// dialog was opened from the AI Assistant panel or with no document in the
        /// Editor, so <c>_currentInstance</c> is null/empty), it detects the active
        /// project's MultiTerm termbases directly and builds the info on the spot — so an
        /// attached .sdltb still appears in the grid (and its AI box can be ticked)
        /// regardless of how Settings was reached.
        /// </summary>
        public static List<MultiTermTermbaseInfo> GetMultiTermInfosForSettings()
        {
            // Prefer the editor instance's already-loaded list (accurate term counts).
            var live = _currentInstance?._multiTermInfos;
            if (live != null && live.Count > 0)
                return live;

            var result = new List<MultiTermTermbaseInfo>();
            try
            {
                var doc = SdlTradosStudio.Application.GetController<EditorController>()?.ActiveDocument;
                if (doc == null)
                {
                    DiagnosticLog.Log("MultiTerm", "GetMultiTermInfosForSettings: no live infos and no active document → no MultiTerm rows.");
                    return result;
                }

                var configs = MultiTermProjectDetector.DetectTermbases(doc);
                foreach (var config in configs)
                {
                    try
                    {
                        using (var reader = TermbaseReaderFactory.Create(config.FilePath))
                        {
                            if (reader.Open())
                            {
                                // Load so the term count is accurate, then capture the info.
                                reader.LoadAllTerms(config.SourceIndexName, config.TargetIndexName,
                                    config.SyntheticId, config.TermbaseName);
                                result.Add(reader.GetTermbaseInfo(
                                    config.SourceIndexName, config.TargetIndexName, config.SyntheticId));
                                continue;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLog.Log("MultiTerm", $"GetMultiTermInfosForSettings: '{config.TermbaseName}' failed to open: {ex.Message}");
                    }
                    // Open/read failed — still surface the row so it's visible (and tickable).
                    result.Add(new MultiTermTermbaseInfo
                    {
                        SyntheticId = config.SyntheticId,
                        FilePath = config.FilePath,
                        Name = config.TermbaseName,
                        SourceIndexName = config.SourceIndexName,
                        TargetIndexName = config.TargetIndexName,
                        LoadMode = MultiTermLoadMode.Failed
                    });
                }
                DiagnosticLog.Log("MultiTerm",
                    $"GetMultiTermInfosForSettings: editor instance had no infos; detected {result.Count} from the active project directly.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Log("MultiTerm", "GetMultiTermInfosForSettings fallback threw: " + ex);
            }
            return result;
        }

        /// <summary>
        /// Returns detected MultiTerm configs for the settings dialog.
        /// </summary>
        public static List<MultiTermTermbaseConfig> GetMultiTermConfigs()
        {
            return _currentInstance?._multiTermConfigs ?? new List<MultiTermTermbaseConfig>();
        }

        private void OnLicenseStateChanged(object sender, EventArgs e)
        {
            // Marshal to the panel's UI thread; LicenseManager fires on
            // whichever thread updated the license state.
            if (_control.IsValueCreated && _control.Value.IsHandleCreated)
            {
                _control.Value.BeginInvoke(new Action(() =>
                {
                    if (LicenseManager.Instance.HasTier1Access)
                        _control.Value.HideLicenseRequired();
                    else
                        _control.Value.ShowLicenseRequired();
                }));
            }
        }

        private void OnSettingsRequested(object sender, EventArgs e)
        {
            SafeInvoke(() =>
            {
                using (var form = new TermLensSettingsForm(_settings, _promptLibrary, defaultTab: 1))
                {
                    // Find a parent window handle for proper dialog parenting
                    var parent = _control.Value.FindForm();
                    var result = parent != null
                        ? form.ShowDialog(parent)
                        : form.ShowDialog();

                    if (form.SettingsImported)
                    {
                        // User imported settings from file – reload everything from disk
                        var fresh = TermLensSettings.Load();
                        _settings.TermbasePath = fresh.TermbasePath;
                        _settings.AutoLoadOnStartup = fresh.AutoLoadOnStartup;
                        _settings.PanelFontSize = fresh.PanelFontSize;
                        _settings.TermShortcutStyle = fresh.TermShortcutStyle;
                        _settings.ChordDelayMs = fresh.ChordDelayMs;
                        _settings.DisabledTermbaseIds = fresh.DisabledTermbaseIds;
                        _settings.WriteTermbaseIds = fresh.WriteTermbaseIds;
                        _settings.ProjectTermbaseId = fresh.ProjectTermbaseId;
                        _settings.DisabledMultiTermIds = fresh.DisabledMultiTermIds;
                        _settings.AiSettings = fresh.AiSettings;
                    }

                    if (result == System.Windows.Forms.DialogResult.OK || form.SettingsImported)
                    {
                        // Apply font size change (user may have adjusted it in settings)
                        _control.Value.SetFontSize(_settings.PanelFontSize);

                        // Apply shortcut style change
                        TermBlock.UseRepeatedDigitBadges = _settings.TermShortcutStyle == "repeated";

                        // Refresh prompt library (user may have added/edited/deleted prompts)
                        _promptLibrary.Refresh();

                        // Notify AI Assistant to reload settings from disk
                        AiAssistantViewPart.NotifySettingsChanged();

                        // Force-reload termbases on a background thread so the
                        // dialog closes immediately and the UI stays responsive
                        // while the reload (which can take ~2 min on Studio 2026
                        // cold cache) runs. UpdateFromActiveSegment is marshaled
                        // back to the UI thread when the reload finishes so
                        // chips refresh on whatever segment is active by then.
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            try
                            {
                                LoadTermbase(forceReload: true);
                                LoadMultiTermTermbases();
                                SafeInvoke(UpdateFromActiveSegment);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine(
                                    "[TermLens] Post-settings reload failed: " + ex);
                            }
                        });
                    }
                }
            });
        }

        private void OnFontSizeChanged(object sender, EventArgs e)
        {
            // Persist the new font size from the A+/A- buttons
            _settings.PanelFontSize = _control.Value.Font.Size;
            _settings.Save();

            // Refresh the segment display with the new font
            UpdateFromActiveSegment();
        }

        private void OnActiveDocumentChanged(object sender, DocumentEventArgs e)
        {
            if (_activeDocument != null)
            {
                _activeDocument.ActiveSegmentChanged -= OnActiveSegmentChanged;
                _activeDocument.ActiveFilePropertiesChanged -= OnActiveFilePropertiesChanged;
            }

            _activeDocument = _editorController?.ActiveDocument;

            if (_activeDocument != null)
            {
                _activeDocument.ActiveSegmentChanged += OnActiveSegmentChanged;
                _activeDocument.ActiveFilePropertiesChanged += OnActiveFilePropertiesChanged;

                // Check if the project has changed – if so, save outgoing and load incoming settings
                ApplyProjectSettingsFromDocument(_activeDocument);

                // Reload MultiTerm termbases – may have switched projects
                LoadMultiTermTermbases();
                UpdateFromActiveSegment();
            }
            else
            {
                SafeInvoke(() => _control.Value.Clear());
            }
        }

        /// <summary>
        /// Detects whether the active document belongs to a different Trados project
        /// and, if so, saves outgoing project settings and loads incoming project settings.
        /// </summary>
        private void ApplyProjectSettingsFromDocument(IStudioDocument document)
        {
            try
            {
                var project = document?.Project as FileBasedProject;
                var projectPath = project?.FilePath;
                var projectName = project?.GetProjectInfo()?.Name;

                System.Diagnostics.Debug.WriteLine($"[TermLens] Project detection: path={projectPath ?? "(null)"}, name={projectName ?? "(null)"}, current={_currentProjectPath ?? "(null)"}");

                // Same project (or no project) – nothing to do
                if (string.Equals(projectPath, _currentProjectPath, StringComparison.OrdinalIgnoreCase))
                    return;

                System.Diagnostics.Debug.WriteLine($"[TermLens] Project switch: '{_currentProjectName}' → '{projectName}'");

                // Save outgoing project settings (if we had a project active)
                SaveCurrentProjectSettings();

                // Update tracking
                _currentProjectPath = projectPath;
                _currentProjectName = projectName;

                if (string.IsNullOrEmpty(projectPath))
                    return;

                // Load incoming project settings
                var ps = ProjectSettings.Load(projectPath);
                if (ps != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[TermLens] Loaded project settings: db={ps.TermbasePath}, write={ps.WriteTermbaseIds?.Count ?? 0}, disabled={ps.DisabledTermbaseIds?.Count ?? 0}");

                    // One-time migration: older project files have an empty DisabledAiTermbaseIds
                    // which was interpreted as "include all termbases". New projects default to
                    // "disable all termbases" (opt-in). Migrate existing projects on first load
                    // to match the new opt-in default.
                    // Condition: flag is false AND the disabled list is empty. A non-empty
                    // list means the user has already made deliberate AI termbase choices
                    // (even if the flag was reset by an earlier bug in ExtractProjectSettings).
                    if (!ps.AiTermbaseIdsInitialized && ps.DisabledAiTermbaseIds.Count == 0)
                    {
                        var allIds = GetAllTermbaseIds(ps.TermbasePath ?? _settings.TermbasePath);
                        ps.DisabledAiTermbaseIds = new List<long>(allIds);
                        ps.AiTermbaseIdsInitialized = true;
                        ProjectSettings.Save(projectPath, ps);
                        System.Diagnostics.Debug.WriteLine($"[TermLens] Migrated AI termbase IDs for existing project: disabled {allIds.Count} termbases");
                    }

                    _settings.ApplyProjectOverlay(ps);
                    // CRITICAL: persist the global settings file so that
                    // disk-based readers (QuickAddTermAction, AddTermAction)
                    // see the new project's WriteTermbaseIds / ProjectTermbaseId
                    // instead of the previous project's stale values. Pre-fix,
                    // pressing Alt+Down on a freshly-opened project would write
                    // to the previous project's Write termbase.
                    _settings.Save();

                    // Reload termbase with the project-specific path
                    LoadTermbase(forceReload: true);
                }
                else
                {
                    // First time encountering this project – create clean defaults.
                    // We must NOT snapshot _settings here because it still carries
                    // the previous project's overlay (write IDs, project termbase, etc.).
                    System.Diagnostics.Debug.WriteLine($"[TermLens] No project settings found – creating clean defaults");

                    // Default: all termbases disabled for new projects; user opts in per project.
                    var allIds = GetAllTermbaseIds(_settings.TermbasePath);

                    // Seed the MultiTerm "AI" opt-in from the Trados project settings
                    // bundle. When this project was created from a project template that
                    // had MultiTerm termbases ticked for AI, that choice is carried in the
                    // bundle and inherited here — so CLI/template-driven workflows don't
                    // need to re-tick every new project (issue #36). Empty otherwise.
                    var inheritedAiMtIds = ProjectBundleSettings.ReadEnabledMultiTermIds(project);
                    if (inheritedAiMtIds.Count > 0)
                        DiagnosticLog.Log("ProjectBundle",
                            $"New project '{projectName}': inherited {inheritedAiMtIds.Count} AI-enabled MultiTerm termbase(s) from its template.");

                    var newPs = new ProjectSettings
                    {
                        ProjectPath = projectPath ?? "",
                        ProjectName = projectName ?? "",
                        TermbasePath = _settings.TermbasePath ?? "",
                        WriteTermbaseIds = new List<long>(),
                        ProjectTermbaseId = -1,
                        DisabledTermbaseIds = allIds,
                        DisabledMultiTermIds = new List<long>(),
                        DisabledAiTermbaseIds = new List<long>(allIds),
                        EnabledAiMultiTermIds = inheritedAiMtIds,
                        AiTermbaseIdsInitialized = true,
                    };
                    _settings.ApplyProjectOverlay(newPs);
                    ProjectSettings.Save(projectPath, newPs);
                    // CRITICAL: persist the global settings file so disk-based
                    // readers see the empty WriteTermbaseIds / cleared
                    // ProjectTermbaseId. See comment on the matching call
                    // above for the bug this prevents.
                    _settings.Save();

                    // Reload termbase with clean state
                    LoadTermbase(forceReload: true);
                }
            }
            catch
            {
                // Never crash on project detection – fall back to global settings
            }
        }

        /// <summary>
        /// Opens the termbase database briefly and returns all termbase IDs.
        /// Used to pre-populate the disabled list when creating new project defaults.
        /// </summary>
        /// <summary>
        /// Opt-in default migration for the global AI-termbase inclusion list.
        /// Old behaviour: empty DisabledAiTermbaseIds = "include all termbases in AI
        /// prompts" — i.e. opt-out, which silently sends sensitive termbase contents
        /// to the AI provider. New behaviour: opt-in — by default nothing is included,
        /// the user explicitly enables specific termbases.
        ///
        /// This mirrors the per-project migration in OnActiveDocumentChanged: when the
        /// initialized flag is false AND the disabled list is empty, populate the
        /// disabled list with all known termbase IDs and set the flag. Users with any
        /// existing explicit choices (non-empty disabled list) or already-migrated
        /// settings (flag=true) are left untouched.
        ///
        /// Runs on the background termbase-load thread after termbase IDs are known.
        /// </summary>
        private void MigrateGlobalAiTermbaseListIfNeeded()
        {
            try
            {
                var ai = _settings?.AiSettings;
                if (ai == null) return;
                if (ai.AiTermbaseIdsInitialized) return;
                if (ai.DisabledAiTermbaseIds != null && ai.DisabledAiTermbaseIds.Count > 0)
                {
                    // User has pre-existing explicit choices; just mark as initialized
                    // so we don't keep re-evaluating on every load.
                    ai.AiTermbaseIdsInitialized = true;
                    _settings.Save();
                    return;
                }

                var allIds = GetAllTermbaseIds(_settings.TermbasePath);
                if (allIds == null || allIds.Count == 0)
                {
                    // No termbases loaded yet — don't migrate prematurely (we'd
                    // mark as initialized with an empty list, defeating the
                    // opt-in default on the next run). Try again next session.
                    return;
                }

                ai.DisabledAiTermbaseIds = new List<long>(allIds);
                ai.AiTermbaseIdsInitialized = true;
                _settings.Save();
                System.Diagnostics.Debug.WriteLine(
                    $"[TermLens] Migrated global AI termbase list: disabled {allIds.Count} termbases (opt-in default)");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[TermLens] AI termbase migration failed: " + ex);
            }
        }

        private List<long> GetAllTermbaseIds(string termbasePath)
        {
            var dbPath = termbasePath;
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
            {
                var candidates = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "Supervertaler_Data", "resources", "supervertaler.db"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Supervertaler", "resources", "supervertaler.db"),
                };
                dbPath = Array.Find(candidates, p => File.Exists(p));
            }

            if (string.IsNullOrEmpty(dbPath)) return new List<long>();

            try
            {
                using (var reader = new TermbaseReader(dbPath))
                {
                    if (!reader.Open()) return new List<long>();
                    return reader.GetTermbases().ConvertAll(tb => tb.Id);
                }
            }
            catch
            {
                return new List<long>();
            }
        }

        /// <summary>
        /// Saves the current per-project settings to disk (if a project is active).
        /// Called on project switch and when the settings dialog is closed.
        /// </summary>
        private void SaveCurrentProjectSettings()
        {
            if (string.IsNullOrEmpty(_currentProjectPath)) return;

            try
            {
                var ps = _settings.ExtractProjectSettings(_currentProjectPath, _currentProjectName);
                ProjectSettings.Save(_currentProjectPath, ps);
                MirrorAiOptInToBundle(_currentProjectPath, ps.EnabledAiMultiTermIds);
            }
            catch
            {
                // Silently ignore save failures
            }
        }

        /// <summary>
        /// Mirrors the project's MultiTerm "AI" opt-in into the Trados project settings
        /// bundle (in addition to Supervertaler's sidecar JSON), so the choice can be baked
        /// into a project template via "Create Template from Project" and inherited by every
        /// project created from that template (issue #36). Best-effort; never throws.
        /// </summary>
        private static void MirrorAiOptInToBundle(string projectPath, List<long> enabledAiMtIds)
        {
            try
            {
                var proj = ResolveCurrentProject(projectPath);
                if (proj != null)
                    ProjectBundleSettings.WriteEnabledMultiTermIds(proj, enabledAiMtIds);
            }
            catch { }
        }

        /// <summary>
        /// Returns the Trados CurrentProject only when its path matches
        /// <paramref name="projectPath"/>, so we never write the wrong project's bundle
        /// mid project-switch. Null on any mismatch or failure.
        /// </summary>
        private static FileBasedProject ResolveCurrentProject(string projectPath)
        {
            try
            {
                if (string.IsNullOrEmpty(projectPath)) return null;
                var pc = SdlTradosStudio.Application.GetController<ProjectsController>();
                var proj = pc?.CurrentProject;
                if (proj != null && string.Equals(proj.FilePath, projectPath, StringComparison.OrdinalIgnoreCase))
                    return proj;
                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Returns the .sdlproj path of the currently active Trados project, or null.
        /// Used by the settings dialog to save project-specific settings on OK.
        /// </summary>
        public static string GetCurrentProjectPath()
        {
            var tracked = _currentInstance?._currentProjectPath;
            if (!string.IsNullOrEmpty(tracked))
                return tracked;
            // Fallback: resolve straight from the editor's ActiveDocument. This
            // view part only tracks the project once OnActiveDocumentChanged has
            // fired for it, but a document can be open (and a MultiTerm "AI" box
            // tickable in Settings) before that — e.g. Settings opened from the
            // AI Assistant panel. Without this fallback the per-project save in
            // ApplySettings was skipped, so the tick lived only in the global
            // settings and got clobbered by the empty per-project overlay on the
            // next Trados restart (issue #36).
            return GetActiveDocumentProject()?.FilePath;
        }

        /// <summary>
        /// Returns the name of the currently active Trados project, or null.
        /// </summary>
        public static string GetCurrentProjectName()
        {
            var tracked = _currentInstance?._currentProjectName;
            if (!string.IsNullOrEmpty(tracked))
                return tracked;
            return GetActiveDocumentProject()?.GetProjectInfo()?.Name;
        }

        /// <summary>
        /// Resolves the FileBasedProject backing the editor's ActiveDocument,
        /// or null when no document is open. Used as a fallback for the
        /// current-project getters so per-project settings still resolve before
        /// this view part has tracked the project (issue #36).
        /// </summary>
        private static FileBasedProject GetActiveDocumentProject()
        {
            try
            {
                var doc = SdlTradosStudio.Application.GetController<EditorController>()?.ActiveDocument;
                return doc?.Project as FileBasedProject;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Returns the active document's source-language display name (e.g.
        /// "English (United States)"), or null when no document is open.
        /// Used by the Settings dialog to warn when the user ticks Write or
        /// Project on a termbase whose language pair doesn't match the
        /// active project.
        /// </summary>
        public static string GetCurrentProjectSourceLanguage()
        {
            return _currentInstance?.GetDocumentSourceLanguage();
        }

        /// <summary>
        /// Returns the active document's target-language display name (e.g.
        /// "Chinese (Simplified)"), or null when no document is open. Used by the
        /// Add / Quick-Add term actions to decide whether a target selection may
        /// be auto-expanded to the whitespace token — it must not be for no-space
        /// scripts (Chinese, Korean, Japanese), or the whole segment is saved.
        /// </summary>
        public static string GetCurrentProjectTargetLanguage()
        {
            return _currentInstance?.GetDocumentTargetLanguage();
        }

        /// <summary>
        /// Best-effort snapshot of the active project / file / language pair / client
        /// for token-usage attribution, read at log time by <see cref="Core.UsageLogger"/>.
        /// Every field is optional; any failure yields nulls and never throws.
        /// </summary>
        public static Core.UsageContextSnapshot GetCurrentUsageContext()
        {
            var snap = new Core.UsageContextSnapshot();
            var inst = _currentInstance;
            if (inst == null) return snap;
            try
            {
                var doc = inst._activeDocument;
                if (doc != null)
                {
                    snap.Project = Core.DocumentContextHelper.GetProjectName(doc);
                    snap.File = Core.DocumentContextHelper.GetDocumentName(doc);
                }
                snap.SrcLang = inst.GetDocumentSourceLanguage();
                snap.TgtLang = inst.GetDocumentTargetLanguage();

                var projPath = inst._currentProjectPath;
                if (!string.IsNullOrEmpty(projPath))
                {
                    snap.ProjectKey = Settings.ProjectSettings.GetProjectKey(projPath);
                    var ps = Settings.ProjectSettings.Load(projPath);
                    if (ps != null)
                    {
                        snap.Client = ps.Client;
                        if (string.IsNullOrEmpty(snap.Project)) snap.Project = ps.ProjectName;
                    }
                }
            }
            catch { /* best-effort attribution */ }
            return snap;
        }

        private void OnActiveFilePropertiesChanged(object sender, EventArgs e)
        {
            // Fired when file/project properties change – reload MultiTerm
            // termbases in case the user toggled termbases in Project Settings.
            if (HasMultiTermConfigChanged())
            {
                LoadMultiTermTermbases();
                UpdateFromActiveSegment();
            }
        }

        // ─── Periodic MultiTerm config check ─────────────────────

        private System.Windows.Forms.Timer _multiTermConfigTimer;

        /// <summary>
        /// Starts a lightweight timer that checks every 2 seconds whether the
        /// set of enabled MultiTerm termbases in the Trados project has changed
        /// (e.g. user toggled the Enabled checkbox in Project Settings → Termbases).
        /// DetectTermbases() reads an in-memory object – no file I/O or DB access.
        /// </summary>
        private void StartMultiTermConfigTimer()
        {
            if (_multiTermConfigTimer != null) return;
            _multiTermConfigTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _multiTermConfigTimer.Tick += OnMultiTermConfigTimerTick;
            _multiTermConfigTimer.Start();
        }

        private void StopMultiTermConfigTimer()
        {
            if (_multiTermConfigTimer == null) return;
            _multiTermConfigTimer.Stop();
            _multiTermConfigTimer.Dispose();
            _multiTermConfigTimer = null;
        }

        private void OnMultiTermConfigTimerTick(object sender, EventArgs e)
        {
            if (_activeDocument == null) return;

            if (HasMultiTermConfigChanged())
            {
                LoadMultiTermTermbases();
                UpdateFromActiveSegment();
            }
        }

        private void OnActiveSegmentChanged(object sender, EventArgs e)
        {
            // Reload MultiTerm terms if any .sdltb file has been modified since last load
            // (e.g., user added terms via Trados's native MultiTerm interface)
            // or if the user toggled termbases in Trados Project Settings
            if (HasMultiTermFileChanged() || HasMultiTermConfigChanged())
                LoadMultiTermTermbases();

            UpdateFromActiveSegment();
        }

        /// <summary>
        /// Checks whether any loaded .sdltb file has been modified since we last read it.
        /// Uses File.GetLastWriteTimeUtc() which is a fast stat call.
        /// </summary>
        private bool HasMultiTermFileChanged()
        {
            if (_multiTermFileTimestamps == null || _multiTermFileTimestamps.Count == 0)
                return false;

            foreach (var kvp in _multiTermFileTimestamps)
            {
                try
                {
                    if (!File.Exists(kvp.Key)) continue;
                    var currentMtime = File.GetLastWriteTimeUtc(kvp.Key);
                    if (currentMtime > kvp.Value)
                        return true;
                }
                catch { /* ignore errors checking file times */ }
            }
            return false;
        }

        /// <summary>
        /// Checks whether the set of enabled MultiTerm termbases in the Trados project
        /// settings has changed since we last loaded (e.g., user toggled the Enabled
        /// checkbox in Project Settings → Termbases). Lightweight – reads only the
        /// project config, no database access.
        /// </summary>
        private bool HasMultiTermConfigChanged()
        {
            try
            {
                var current = MultiTermProjectDetector.DetectTermbases(_activeDocument);
                var loaded = _multiTermConfigs;

                // Both null/empty → no change
                var currentCount = current?.Count ?? 0;
                var loadedCount = loaded?.Count ?? 0;
                if (currentCount == 0 && loadedCount == 0) return false;

                // Different count → changed
                if (currentCount != loadedCount) return true;

                // Same count – compare file paths (order-sensitive)
                for (int i = 0; i < currentCount; i++)
                {
                    if (!string.Equals(current[i].FilePath, loaded[i].FilePath, StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private void UpdateFromActiveSegment()
        {
            if (_activeDocument?.ActiveSegmentPair == null)
            {
                SafeInvoke(() => _control.Value.Clear());
                return;
            }

            try
            {
                var sourceSegment = _activeDocument.ActiveSegmentPair.Source;
                var sourceText = GetPlainText(sourceSegment);

                // If we have fallback providers (API-based), search them for this segment
                // and merge results into the index before updating the display
                if (_fallbackProviders != null && _fallbackProviders.Count > 0
                    && !string.IsNullOrWhiteSpace(sourceText))
                {
                    try
                    {
                        foreach (var fb in _fallbackProviders)
                        {
                            var results = fb.SearchSegment(sourceText);
                            if (results.Count > 0)
                            {
                                // Temporarily merge these results for this segment
                                SafeInvoke(() => _control.Value.MergeMultiTermEntries(results, null));
                            }
                        }
                    }
                    catch
                    {
                        // Swallow – fallback search should never crash the plugin
                    }
                }

                SafeInvoke(() => _control.Value.UpdateSegment(sourceText));
            }
            catch (Exception)
            {
                // Silently handle – segment may not be available during transitions
            }
        }

        /// <summary>
        /// Extracts only the human-readable text from a Trados segment,
        /// skipping inline tag metadata (URLs, tag attributes, etc.).
        /// Falls back to ToString() if the bilingual API iteration fails.
        /// </summary>
        internal static string GetPlainText(ISegment segment)
        {
            if (segment == null) return "";
            try
            {
                var sb = new StringBuilder();
                foreach (var item in segment.AllSubItems)
                {
                    if (item is IText textItem)
                        sb.Append(textItem.Properties.Text);
                }
                var result = sb.ToString();
                // If we got text, use it; otherwise fall back to ToString()
                return !string.IsNullOrEmpty(result) ? result : segment.ToString() ?? "";
            }
            catch
            {
                return segment.ToString() ?? "";
            }
        }

        private void SafeInvoke(Action action)
        {
            var ctrl = _control.Value;
            if (ctrl.InvokeRequired)
                ctrl.BeginInvoke(action);
            else
                action();
        }

        private void OnTermInsertRequested(object sender, TermInsertEventArgs e)
        {
            if (_activeDocument == null || string.IsNullOrEmpty(e.TargetTerm))
                return;

            try
            {
                _activeDocument.Selection.Target.Replace(e.TargetTerm, "TermLens");
            }
            catch (Exception)
            {
                // Silently handle – editor may not allow insertion at this moment
            }
        }

        private void OnTermEditRequested(object sender, TermEditEventArgs e)
        {
            if (e.Entry == null || e.Entry.IsMultiTerm) return;

            SafeInvoke(() =>
            {
                var dbPath = _settings.TermbasePath;
                if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath)) return;

                // Multi-entry mode: look up termbase info for ALL entries
                var allEntries = e.AllEntries;
                if (allEntries != null && allEntries.Count > 1)
                {
                    var entryTermbases = new List<KeyValuePair<TermEntry, TermbaseInfo>>();
                    using (var reader = new TermbaseReader(dbPath))
                    {
                        if (reader.Open())
                        {
                            foreach (var entry in allEntries)
                            {
                                var tb = reader.GetTermbaseById(entry.TermbaseId);
                                if (tb != null)
                                    entryTermbases.Add(new KeyValuePair<TermEntry, TermbaseInfo>(entry, tb));
                            }
                        }
                    }

                    if (entryTermbases.Count == 0) return;

                    using (var dlg = new TermEntryEditorDialog(entryTermbases, dbPath, GetDocumentSourceLanguage()))
                    {
                        var parent = _control.Value.FindForm();
                        var result = parent != null ? dlg.ShowDialog(parent) : dlg.ShowDialog();

                        if (result == DialogResult.OK || result == DialogResult.Abort)
                        {
                            // Force reload to rebuild index after save or delete
                            LoadTermbase(forceReload: true);
                            LoadMultiTermTermbases();
                            UpdateFromActiveSegment();
                        }
                    }
                    return;
                }

                // Single-entry mode (fallback)
                TermbaseInfo termbase = null;
                using (var reader = new TermbaseReader(dbPath))
                {
                    if (reader.Open())
                        termbase = reader.GetTermbaseById(e.Entry.TermbaseId);
                }

                using (var dlg = new TermEntryEditorDialog(e.Entry, dbPath, termbase, GetDocumentSourceLanguage()))
                {
                    var parent = _control.Value.FindForm();
                    var result = parent != null ? dlg.ShowDialog(parent) : dlg.ShowDialog();

                    if (result == DialogResult.OK)
                    {
                        // Term was saved (possibly with synonym changes) – force reload
                        // to rebuild the index including source synonym keys
                        LoadTermbase(forceReload: true);
                        LoadMultiTermTermbases();
                        UpdateFromActiveSegment();
                    }
                    else if (result == DialogResult.Abort)
                    {
                        // Term was deleted from the editor
                        _control.Value.RemoveTermFromIndex(e.Entry.Id);
                        UpdateFromActiveSegment();
                    }
                }
            });
        }

        private void OnTermDeleteRequested(object sender, TermEditEventArgs e)
        {
            if (e.Entry == null || e.Entry.IsMultiTerm) return;

            SafeInvoke(() =>
            {
                var confirmResult = MessageBox.Show(
                    $"Delete the term \u201c{e.Entry.SourceTerm} \u2192 {e.Entry.TargetTerm}\u201d?\n\n" +
                    "This cannot be undone.",
                    "TermLens \u2014 Delete Term",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);

                if (confirmResult != DialogResult.Yes) return;

                try
                {
                    bool deleted = TermbaseReader.DeleteTerm(
                        _settings.TermbasePath,
                        e.Entry.Id);

                    if (deleted)
                        NotifyTermDeleted(e.Entry.Id);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Failed to delete term: {ex.Message}\n\n" +
                        "The database may be locked by another application.",
                        "TermLens \u2014 Delete Term",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            });
        }

        private void OnTermNonTranslatableToggled(object sender, TermEditEventArgs e)
        {
            if (e.Entry == null || e.Entry.IsMultiTerm) return;

            SafeInvoke(() =>
            {
                bool newState = !e.Entry.IsNonTranslatable;

                try
                {
                    bool updated = TermbaseReader.SetNonTranslatable(
                        _settings.TermbasePath, e.Entry.Id, newState, e.Entry.SourceTerm);

                    if (updated)
                    {
                        // Incremental update: remove old entry, add updated one
                        _control.Value.RemoveTermFromIndex(e.Entry.Id);
                        var updatedEntry = new TermEntry
                        {
                            Id = e.Entry.Id,
                            SourceTerm = e.Entry.SourceTerm,
                            TargetTerm = newState ? e.Entry.SourceTerm : e.Entry.TargetTerm,
                            SourceLang = e.Entry.SourceLang,
                            TargetLang = e.Entry.TargetLang,
                            TermbaseId = e.Entry.TermbaseId,
                            TermbaseName = e.Entry.TermbaseName,
                            IsProjectTermbase = e.Entry.IsProjectTermbase,
                            Ranking = e.Entry.Ranking,
                            Definition = e.Entry.Definition ?? "",
                            Domain = e.Entry.Domain,
                            Notes = e.Entry.Notes,
                            Forbidden = e.Entry.Forbidden,
                            CaseSensitive = e.Entry.CaseSensitive,
                            IsNonTranslatable = newState,
                            TargetSynonyms = e.Entry.TargetSynonyms
                        };
                        _control.Value.AddTermToIndex(updatedEntry);
                        UpdateFromActiveSegment();
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Failed to toggle non-translatable: {ex.Message}\n\n" +
                        "The database may be locked by another application.",
                        "TermLens \u2014 Non-Translatable",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            });
        }

        /// <summary>
        /// Reloads only the AiSettings portion of this ViewPart's _settings
        /// from disk. Called by AiAssistantViewPart when the user changes
        /// the provider/model via the chat status-bar picker — TermLens
        /// doesn't care about termbase reloads in that case, just needs
        /// its in-memory copy of AiSettings refreshed so opening the
        /// Settings dialog from this ViewPart's gear icon shows the
        /// current values rather than stale ones.
        /// </summary>
        public static void NotifyAiSettingsChanged()
        {
            var instance = _currentInstance;
            if (instance == null) return;
            var fresh = TermLensSettings.Load();
            if (instance._settings == null)
                instance._settings = fresh;
            else
                instance._settings.AiSettings = fresh.AiSettings;
        }

        /// <summary>
        /// Reloads settings from disk. Called by AiAssistantViewPart after its
        /// settings dialog saves, so this ViewPart picks up changes made there.
        /// </summary>
        public static void NotifySettingsChanged()
        {
            var instance = _currentInstance;
            if (instance == null) return;
            instance._settings = TermLensSettings.Load();
            // Re-detect system DPI in case the user moved Trados to a
            // different monitor since startup, then re-apply user scale.
            UiScale.SeedSystemScale();
            UiScale.UserFactor = instance._settings.UiScaleFactor;
            _control.Value.SetFontSize(instance._settings.PanelFontSize);
            TermBlock.UseRepeatedDigitBadges = instance._settings.TermShortcutStyle == "repeated";

            // Re-apply per-project overlay so reload doesn't clobber project-specific values
            if (!string.IsNullOrEmpty(instance._currentProjectPath))
            {
                var ps = ProjectSettings.Load(instance._currentProjectPath);
                if (ps != null)
                {
                    instance._settings.ApplyProjectOverlay(ps);
                    // Keep the global settings file in sync so disk-based
                    // readers (QuickAddTermAction, AddTermAction) see the
                    // current project's effective Write/Project termbase IDs.
                    instance._settings.Save();
                    // The user may have just changed the MultiTerm "AI" ticks in the
                    // settings dialog — mirror the choice into the Trados project bundle
                    // now so "Create Template from Project" captures it (issue #36).
                    MirrorAiOptInToBundle(instance._currentProjectPath, ps.EnabledAiMultiTermIds);
                }
            }

            instance.LoadTermbase(forceReload: true);
            instance.LoadMultiTermTermbases();
            instance.UpdateFromActiveSegment();
        }

        /// <summary>
        /// Called by AddTermAction after a term is inserted.
        /// Reloads settings and the term index so the new term appears immediately.
        /// </summary>
        public static void NotifyTermAdded()
        {
            var instance = _currentInstance;
            if (instance == null) return;

            // Re-read settings in case WriteTermbaseId or disabled list changed
            instance._settings = TermLensSettings.Load();

            // Re-apply per-project overlay so the reload doesn't clobber project-specific values
            if (!string.IsNullOrEmpty(instance._currentProjectPath))
            {
                var ps = ProjectSettings.Load(instance._currentProjectPath);
                if (ps != null)
                {
                    instance._settings.ApplyProjectOverlay(ps);
                    // Keep the global settings file in sync (see notes above).
                    instance._settings.Save();
                }
            }

            instance.LoadTermbase(forceReload: true);
            instance.LoadMultiTermTermbases();
            instance.UpdateFromActiveSegment();

            // v4.19.113: refresh the file-watcher's snapshot so this
            // own-write doesn't cause a redundant rebuild ~2 s later
            // when the FileSystemWatcher debounce fires. Also re-attach
            // the watcher in case the DB path itself changed (e.g. user
            // pointed Settings at a different supervertaler.db).
            try { instance.RefreshTermbaseDbSnapshot(); } catch { }
            try { instance.SetupTermbaseDbWatcher(); } catch { }
        }

        // ───────────────────────────────────────────────────────────────
        // v4.19.113: ↻ refresh button handler (wired in Initialize)
        // ───────────────────────────────────────────────────────────────
        private void OnTermLensRefreshRequested(object sender, EventArgs e)
        {
            // Same code path as the F5 keyboard shortcut and AddTermAction.
            // Static so it can be reused; instance method here just defers
            // to it. Wrapped in try/catch so a click on the button can
            // never tear down the panel.
            try
            {
                NotifyTermAdded();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TermLens] Refresh button failed: {ex.Message}");
            }
        }

        // ───────────────────────────────────────────────────────────────
        // v4.19.113: Termbase DB file watcher implementation
        // ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Install (or re-install on path change) a FileSystemWatcher on
        /// the currently-loaded supervertaler.db. Called from Initialize
        /// right after LoadTermbase, and again from NotifyTermAdded in
        /// case settings changes pointed at a different DB.
        /// </summary>
        private void SetupTermbaseDbWatcher()
        {
            // Resolve the path the TermLensControl is actually using right
            // now (set inside its LoadTermbase). If empty, no termbase
            // loaded yet → nothing to watch; we'll be called again the
            // next time LoadTermbase succeeds.
            var path = _control.Value.CurrentDbPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                TeardownTermbaseDbWatcher();
                _watchedDbPath = null;
                return;
            }

            // Already watching this exact path? Snapshot only; no need to
            // rebuild the watcher.
            if (string.Equals(_watchedDbPath, path, StringComparison.OrdinalIgnoreCase) &&
                _dbWatcher != null)
            {
                _dbSnapshot = SnapshotTermbaseDb(path);
                return;
            }

            TeardownTermbaseDbWatcher();

            var dir = Path.GetDirectoryName(path);
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return;

            try
            {
                _dbWatcher = new FileSystemWatcher(dir, name)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };

                // 2-second debounce so a batch of related writes (e.g. an
                // INSERT followed by a SELECT-verify, or several
                // back-to-back deletes) collapses into a single
                // snapshot-compare-and-maybe-reload.
                _dbDebounceTimer = new System.Windows.Forms.Timer { Interval = 2000 };
                _dbDebounceTimer.Tick += OnDbDebounceFire;

                FileSystemEventHandler trigger = (s, e) =>
                {
                    // FileSystemWatcher fires on a thread pool thread; marshal
                    // to the UI thread before touching the WinForms timer.
                    if (_control.IsValueCreated && _control.Value.InvokeRequired)
                    {
                        try
                        {
                            _control.Value.BeginInvoke(new Action(() =>
                            {
                                if (_dbDebounceTimer == null) return;
                                _dbDebounceTimer.Stop();
                                _dbDebounceTimer.Start();
                            }));
                        }
                        catch { /* control gone */ }
                    }
                    else
                    {
                        try
                        {
                            if (_dbDebounceTimer == null) return;
                            _dbDebounceTimer.Stop();
                            _dbDebounceTimer.Start();
                        }
                        catch { }
                    }
                };
                _dbWatcher.Changed += trigger;
                _dbWatcher.Created += trigger;
                _dbWatcher.Renamed += (s, e) => trigger(s, e);

                _watchedDbPath = path;
                _dbSnapshot = SnapshotTermbaseDb(path);
                System.Diagnostics.Debug.WriteLine(
                    $"[TermLens] DB auto-refresh: watching {name} (2s debounce)");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[TermLens] DB auto-refresh setup failed: {ex.Message} – F5 / ↻ still work");
                TeardownTermbaseDbWatcher();
            }
        }

        private void TeardownTermbaseDbWatcher()
        {
            try
            {
                if (_dbWatcher != null)
                {
                    _dbWatcher.EnableRaisingEvents = false;
                    _dbWatcher.Dispose();
                    _dbWatcher = null;
                }
            }
            catch { }
            try
            {
                if (_dbDebounceTimer != null)
                {
                    _dbDebounceTimer.Stop();
                    _dbDebounceTimer.Dispose();
                    _dbDebounceTimer = null;
                }
            }
            catch { }
        }

        /// <summary>
        /// Debounce timer fired — the DB file has been quiet for 2 s.
        /// Snapshot-gate the reload so non-termbase writes (TM saves,
        /// project metadata) don't cause a visible TermLens flicker.
        /// </summary>
        private void OnDbDebounceFire(object sender, EventArgs e)
        {
            try { _dbDebounceTimer?.Stop(); } catch { }

            try
            {
                var path = _watchedDbPath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

                var fresh = SnapshotTermbaseDb(path);
                if (fresh == null)
                {
                    // Couldn't snapshot (DB locked mid-write?) — be safe and
                    // reload. Worst case is one extra sub-second rebuild.
                    System.Diagnostics.Debug.WriteLine(
                        "[TermLens] DB snapshot failed, reloading to be safe");
                    NotifyTermAdded();
                    return;
                }

                if (Equals(fresh, _dbSnapshot))
                {
                    // File mtime ticked but the termbase tables didn't change
                    // (probably a TM / project-metadata write). Skip.
                    return;
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[TermLens] DB changed externally — auto-refreshing index " +
                    $"(termbases:{_dbSnapshot?.Item1.ToString() ?? "?"}→{fresh.Item1}, " +
                    $"terms:{_dbSnapshot?.Item3.ToString() ?? "?"}→{fresh.Item3})");

                // NotifyTermAdded does the full reload (settings re-read,
                // LoadTermbase forceReload, MultiTerm refresh, segment
                // update) and refreshes the snapshot at the end.
                NotifyTermAdded();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[TermLens] DB auto-refresh failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Refresh the cached snapshot in-place (used after an own-write
        /// reload so the watcher's pending fire sees no change and skips).
        /// </summary>
        private void RefreshTermbaseDbSnapshot()
        {
            var path = _watchedDbPath;
            if (string.IsNullOrEmpty(path)) path = _control.Value.CurrentDbPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            _dbSnapshot = SnapshotTermbaseDb(path);
        }

        /// <summary>
        /// Cheap aggregate query against the termbase tables. Returns
        /// (termbasesCount, termbasesMaxId, termsCount, termsMaxId,
        /// termsMaxModified). The tuple is value-compared by the watcher
        /// to decide whether the termbase data actually changed (vs the
        /// file mtime just ticking because of an unrelated TM / project
        /// write that also lives in the same .db).
        /// </summary>
        private static Tuple<long, long, long, long, string> SnapshotTermbaseDb(string dbPath)
        {
            try
            {
                var connStr = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly
                }.ToString();
                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(connStr))
                {
                    conn.Open();
                    long tbCount = 0, tbMaxId = 0, termsCount = 0, termsMaxId = 0;
                    string termsMaxMod = "";
                    using (var cmd = new Microsoft.Data.Sqlite.SqliteCommand(
                        "SELECT COUNT(*), COALESCE(MAX(id), 0) FROM termbases", conn))
                    using (var r = cmd.ExecuteReader())
                    {
                        if (r.Read())
                        {
                            tbCount = r.GetInt64(0);
                            tbMaxId = r.GetInt64(1);
                        }
                    }
                    using (var cmd = new Microsoft.Data.Sqlite.SqliteCommand(
                        "SELECT COUNT(*), COALESCE(MAX(id), 0), COALESCE(MAX(modified_date), '') FROM termbase_terms", conn))
                    using (var r = cmd.ExecuteReader())
                    {
                        if (r.Read())
                        {
                            termsCount = r.GetInt64(0);
                            termsMaxId = r.GetInt64(1);
                            termsMaxMod = r.IsDBNull(2) ? "" : r.GetString(2);
                        }
                    }
                    return Tuple.Create(tbCount, tbMaxId, termsCount, termsMaxId, termsMaxMod);
                }
            }
            catch
            {
                return null;
            }
        }

        // ─── Context sharing for AI Assistant ─────────────────────

        /// <summary>
        /// Returns all loaded termbase terms for the AI Assistant context.
        /// Returns already-computed data – no DB queries.
        /// </summary>
        public static List<TermEntry> GetCurrentTermbaseTerms()
        {
            if (_currentInstance == null) return new List<TermEntry>();
            try { return _control.Value.GetAllLoadedTerms() ?? new List<TermEntry>(); }
            catch { return new List<TermEntry>(); }
        }

        /// <summary>
        /// Returns the matched terms for the active segment.
        /// Used by the AI Assistant to inject terminology context into prompts.
        /// Returns already-computed data – no DB queries.
        /// </summary>
        public static List<TermPickerMatch> GetCurrentSegmentMatches()
        {
            if (_currentInstance == null) return new List<TermPickerMatch>();
            try { return _control.Value.GetCurrentMatches() ?? new List<TermPickerMatch>(); }
            catch { return new List<TermPickerMatch>(); }
        }

        /// <summary>
        /// Called after a term is inserted via quick-add. Incrementally updates the
        /// in-memory index and refreshes the segment display, without reloading the
        /// entire database. Much faster than NotifyTermAdded() for single inserts.
        /// </summary>
        public static void NotifyTermInserted(List<Models.TermEntry> newEntries)
        {
            var instance = _currentInstance;
            if (instance == null) return;

            foreach (var entry in newEntries)
                _control.Value.AddTermToIndex(entry);

            instance.UpdateFromActiveSegment();

            // v4.19.113: keep the file-watcher snapshot in sync so the
            // own-write fileChanged event that's about to fire doesn't
            // cause a redundant full reload. Without this we'd run the
            // full LoadTermbase ~2 s after every Ctrl+E quick-add.
            try { instance.RefreshTermbaseDbSnapshot(); } catch { }
        }

        /// <summary>
        /// Called after a term is deleted. Removes it from the in-memory index
        /// and refreshes the segment display, without reloading the database.
        /// </summary>
        public static void NotifyTermDeleted(long termId)
        {
            var instance = _currentInstance;
            if (instance == null) return;

            _control.Value.RemoveTermFromIndex(termId);
            instance.UpdateFromActiveSegment();

            // v4.19.113: own-delete snapshot refresh (see NotifyTermInserted).
            try { instance.RefreshTermbaseDbSnapshot(); } catch { }
        }

        /// <summary>
        /// Returns the prompt library for sharing with other ViewParts (e.g., AiAssistantViewPart).
        /// </summary>
        public static PromptLibrary GetPromptLibrary()
        {
            return _currentInstance?._promptLibrary;
        }

        private string GetDocumentSourceLanguage()
        {
            try
            {
                var file = _activeDocument?.ActiveFile;
                if (file != null)
                {
                    var lang = file.SourceFile?.Language;
                    if (lang != null)
                        return lang.DisplayName;
                }
            }
            catch (Exception) { }
            return null;
        }

        private string GetDocumentTargetLanguage()
        {
            try
            {
                var file = _activeDocument?.ActiveFile;
                if (file != null)
                {
                    var lang = file.Language;
                    if (lang != null)
                        return lang.DisplayName;
                }
            }
            catch (Exception) { }
            return null;
        }

        // ─── Alt+digit term insertion ────────────────────────────────
        // Two modes controlled by TermShortcutStyle setting:
        //   "sequential" – Alt+4,5 = term 45 (timer waits for next digit)
        //   "repeated"   – Alt+5,5 = term 14 (same digit = next tier)

        private static bool IsRepeatedMode =>
            _currentInstance?._settings?.TermShortcutStyle == "repeated";

        /// <summary>
        /// Called by TermInsertDigitNAction when Alt+digit is pressed.
        /// Dispatches to repeated-digit or sequential handler based on settings.
        /// </summary>
        public static void HandleDigitPress(int digit)
        {
            var instance = _currentInstance;
            if (instance == null) return;

            if (IsRepeatedMode)
                HandleDigitPressRepeated(digit);
            else
                HandleDigitPressSequential(digit);
        }

        // ── Sequential mode: Alt+4,5 = term 45 ──────────────────────

        private static void HandleDigitPressSequential(int digit)
        {
            var instance = _currentInstance;
            if (instance == null) return;

            int matchCount = _control.Value.MatchCount;

            if (_pendingDigit.HasValue)
            {
                // Second (or third) digit in the sequence
                StopChordTimer();
                int accumulated = _pendingAccumulated * 10 + digit;

                // How many digits could the highest term need?
                int maxDigits = matchCount.ToString().Length;
                int currentDigits = accumulated.ToString().Length;

                if (currentDigits >= maxDigits)
                {
                    // We have enough digits – insert immediately
                    _pendingDigit = null;
                    _pendingAccumulated = 0;
                    int number = accumulated == 0 ? 10 : accumulated;
                    instance.InsertTermByIndex(number);
                }
                else
                {
                    // Could still have more digits – keep waiting
                    _pendingDigit = digit;
                    _pendingAccumulated = accumulated;
                    StartChordTimer();
                }
            }
            else
            {
                // First digit pressed
                if (matchCount <= 9)
                {
                    // ≤9 terms: insert immediately, no ambiguity
                    int number = digit == 0 ? 10 : digit;
                    instance.InsertTermByIndex(number);
                }
                else
                {
                    _pendingDigit = digit;
                    _pendingAccumulated = digit;
                    _pendingRepeatCount = 0;
                    StartChordTimer();
                }
            }
        }

        // ── Repeated-digit mode: Alt+5,5 = term 14 ─────────────────

        private static void HandleDigitPressRepeated(int digit)
        {
            var instance = _currentInstance;
            if (instance == null) return;
            if (digit == 0) return; // Alt+0 not used in repeated mode

            int matchCount = _control.Value.MatchCount;

            if (_pendingDigit.HasValue && _pendingDigit.Value == digit)
            {
                // Same digit repeated
                StopChordTimer();
                _pendingRepeatCount++;

                int maxTiers = TermBlock.MaxTiers;
                if (_pendingRepeatCount >= maxTiers || matchCount <= _pendingRepeatCount * 9)
                {
                    // Max tier reached, or no higher tier needed given match count
                    int oneBasedIndex = (_pendingRepeatCount - 1) * 9 + digit;
                    _pendingDigit = null;
                    _pendingRepeatCount = 0;
                    instance.InsertTermByIndex(oneBasedIndex);
                }
                else
                {
                    // Wait for potential further repeat
                    StartChordTimer();
                }
            }
            else if (_pendingDigit.HasValue)
            {
                // Different digit pressed – insert the pending one first,
                // then start tracking the new digit
                StopChordTimer();
                int oneBasedIndex = (_pendingRepeatCount - 1) * 9 + _pendingDigit.Value;
                instance.InsertTermByIndex(oneBasedIndex);

                // Now handle the new digit
                if (matchCount <= 9)
                {
                    instance.InsertTermByIndex(digit);
                }
                else
                {
                    _pendingDigit = digit;
                    _pendingRepeatCount = 1;
                    StartChordTimer();
                }
            }
            else
            {
                // First digit pressed
                if (matchCount <= 9)
                {
                    // ≤9 terms: insert immediately
                    instance.InsertTermByIndex(digit);
                }
                else
                {
                    _pendingDigit = digit;
                    _pendingRepeatCount = 1;
                    StartChordTimer();
                }
            }
        }

        // ── Shared timer ────────────────────────────────────────────

        private static void StartChordTimer()
        {
            StopChordTimer();
            int delay = _currentInstance?._settings?.ChordDelayMs ?? 1100;
            if (delay < 300) delay = 300;
            if (delay > 3000) delay = 3000;
            _chordTimer = new System.Windows.Forms.Timer { Interval = delay };
            _chordTimer.Tick += OnChordTimerTick;
            _chordTimer.Start();
        }

        private static void StopChordTimer()
        {
            if (_chordTimer != null)
            {
                _chordTimer.Stop();
                _chordTimer.Tick -= OnChordTimerTick;
                _chordTimer.Dispose();
                _chordTimer = null;
            }
        }

        private static void OnChordTimerTick(object sender, EventArgs e)
        {
            StopChordTimer();

            var instance = _currentInstance;
            if (instance == null || !_pendingDigit.HasValue) return;

            if (IsRepeatedMode)
            {
                int digit = _pendingDigit.Value;
                int repeats = _pendingRepeatCount;
                _pendingDigit = null;
                _pendingRepeatCount = 0;
                int oneBasedIndex = (repeats - 1) * 9 + digit;
                instance.InsertTermByIndex(oneBasedIndex);
            }
            else
            {
                // Sequential mode: insert whatever has accumulated
                int accumulated = _pendingAccumulated;
                _pendingDigit = null;
                _pendingAccumulated = 0;
                int number = accumulated == 0 ? 10 : accumulated;
                instance.InsertTermByIndex(number);
            }
        }

        private void InsertTermByIndex(int oneBasedIndex)
        {
            if (_activeDocument == null) return;

            var (entry, matchedViaAbbreviation) = _control.Value.GetTermByIndex(oneBasedIndex);
            if (entry == null) return;

            try
            {
                var textToInsert = matchedViaAbbreviation && !string.IsNullOrEmpty(entry.PrimaryTargetAbbreviation)
                    ? entry.PrimaryTargetAbbreviation
                    : entry.TargetTerm;
                _activeDocument.Selection.Target.Replace(textToInsert, "TermLens");
            }
            catch (Exception)
            {
                // Silently handle – editor may not allow insertion at this moment
            }
        }

        // ─── TermPicker ─────────────────────────────────────────────

        /// <summary>
        /// Called by TermPickerAction (Ctrl+Alt+G).
        /// Opens a dialog showing all matched terms for the current segment.
        /// </summary>
        public static void HandleTermPicker()
        {
            var instance = _currentInstance;
            if (instance == null || instance._activeDocument == null) return;

            var matches = _control.Value.GetCurrentMatches();
            if (matches.Count == 0) return;

            instance.SafeInvoke(() =>
            {
                using (var dlg = new TermPickerDialog(matches, instance._settings))
                {
                    var parent = _control.Value.FindForm();
                    var result = parent != null
                        ? dlg.ShowDialog(parent)
                        : dlg.ShowDialog();

                    if (result == DialogResult.OK && !string.IsNullOrEmpty(dlg.SelectedTargetTerm))
                    {
                        try
                        {
                            instance._activeDocument.Selection.Target.Replace(
                                dlg.SelectedTargetTerm, "TermLens");
                        }
                        catch (Exception)
                        {
                            // Silently handle
                        }
                    }
                }
            });
        }

        // ─── TermLens Popup (Ctrl-tap / Ctrl+Alt+G) ─────────────────

        private static TermLensPopupForm _currentPopup;

        /// <summary>
        /// Called by Ctrl-tap (CtrlTapFilter), TermLensPopupAction (Ctrl+Alt+G).
        /// Opens a borderless floating version of the docked TermLens panel
        /// for the active segment. Acts as a toggle: if the popup is already
        /// open, the second Ctrl-tap closes it. Cycling between matches is
        /// done with arrow keys / Tab inside the popup, not by re-pressing
        /// the open shortcut.
        /// </summary>
        public static void HandleTermLensPopup()
        {
            var instance = _currentInstance;
            if (instance == null || instance._activeDocument == null) return;

            // Already open → close (toggle behaviour). Use arrow keys / Tab
            // inside the popup to cycle the highlighted match.
            if (_currentPopup != null && !_currentPopup.IsDisposed)
            {
                _currentPopup.Close();
                return;
            }

            // Same source-text extraction the QuickAdd actions use.
            string sourceText = instance._activeDocument.ActiveSegmentPair?.Source != null
                ? SegmentTagHandler.GetFinalText(instance._activeDocument.ActiveSegmentPair.Source)
                : "";
            if (string.IsNullOrWhiteSpace(sourceText)) return;

            // No matches → no popup, matching HandleTermPicker's behaviour.
            if (_control.Value.MatchCount == 0)
            {
                // MatchCount reflects what's currently rendered in the docked
                // panel for the active segment, so we can short-circuit without
                // running the matcher again.
                return;
            }

            instance.SafeInvoke(() =>
            {
                // Find the WinForms Form that hosts the docked TermLens panel –
                // this is the Trados main window from a Win32 perspective. Used
                // for two things: (a) Show(owner) so the popup is owned by it
                // and the Z-order / focus-return relationship is well-defined;
                // (b) re-activated explicitly just before the insert runs so
                // focus lands back on the target cell instead of getting lost
                // during the popup's teardown.
                var ownerForm = _control.Value.FindForm();

                Action<int> insertWithFocusReturn = oneBasedIndex =>
                {
                    if (ownerForm != null && ownerForm.IsHandleCreated && !ownerForm.IsDisposed)
                        ownerForm.Activate();
                    instance.InsertTermByIndex(oneBasedIndex);
                };

                var popup = new TermLensPopupForm(
                    _control.Value, sourceText, insertWithFocusReturn);

                _currentPopup = popup;
                popup.FormClosed += (s, e) =>
                {
                    if (_currentPopup == popup) _currentPopup = null;
                };

                popup.PositionNear(Cursor.Position);
                if (ownerForm != null)
                    popup.Show(ownerForm);
                else
                    popup.Show();
                // Show(owner) activates the popup – no need for an extra Activate().
            });
        }

        /// <summary>
        /// Called by TermLensPopupForm when the user presses "E" on a
        /// highlighted match. Opens the term-entry editor through the same
        /// code path as the docked panel's right-click "Edit Term…" menu –
        /// the popup snapshots the entry data before it disposes itself, so
        /// this just re-uses OnTermEditRequested with synthesized event args.
        /// </summary>
        public static void HandleEditCurrentTerm(TermEntry entry, IReadOnlyList<TermEntry> allEntries)
        {
            var instance = _currentInstance;
            if (instance == null || entry == null) return;
            instance.OnTermEditRequested(null, new TermEditEventArgs
            {
                Entry = entry,
                AllEntries = allEntries
            });
        }

        // ─────────────────────────────────────────────────────────────

        public override void Dispose()
        {
            // Save per-project settings before shutting down
            SaveCurrentProjectSettings();

            if (_currentInstance == this)
                _currentInstance = null;

            StopChordTimer();
            StopMultiTermConfigTimer();
            DisposeFallbackProviders();

            if (_activeDocument != null)
            {
                _activeDocument.ActiveSegmentChanged -= OnActiveSegmentChanged;
                _activeDocument.ActiveFilePropertiesChanged -= OnActiveFilePropertiesChanged;
            }

            if (_editorController != null)
                _editorController.ActiveDocumentChanged -= OnActiveDocumentChanged;

            // Unsubscribe everything wired to a static singleton in Initialize.
            // Without this, dead view-part instances stay reachable through the
            // singleton's invocation lists across document close/reopen cycles,
            // and every term-insert / right-click action gets dispatched to all
            // of them – i.e. the term gets inserted N times instead of once
            // (root cause of the v4.19.30 popup-double-insert bug, which the
            // popup fix sidestepped by skipping the bubble entirely; the docked
            // panel still went through this code path until now).
            //
            // IsValueCreated checks avoid forcing the Lazy<T> singletons into
            // existence here purely to call -= on something we never subscribed
            // to. The -= operator itself is a safe no-op when the delegate
            // isn't in the invocation list, so the early-return-on-unlicensed
            // path in Initialize doesn't need a parallel branch here.
            LicenseManager.Instance.LicenseStateChanged -= OnLicenseStateChanged;

            if (_mainPanel.IsValueCreated)
                _mainPanel.Value.SettingsRequested -= OnSettingsRequested;

            if (_control.IsValueCreated)
            {
                _control.Value.TermInsertRequested -= OnTermInsertRequested;
                _control.Value.TermEditRequested -= OnTermEditRequested;
                _control.Value.TermDeleteRequested -= OnTermDeleteRequested;
                _control.Value.TermNonTranslatableToggled -= OnTermNonTranslatableToggled;
                _control.Value.FontSizeChanged -= OnFontSizeChanged;
                _control.Value.RefreshRequested -= OnTermLensRefreshRequested; // v4.19.113
            }

            // v4.19.113: tear down the DB file watcher so we don't leak it
            // across document close/reopen cycles.
            try { TeardownTermbaseDbWatcher(); } catch { }

            base.Dispose();
        }

        // ─── Update checker dialog ──────────────────────────────────

        /// <summary>
        /// Shows the one-time usage statistics opt-in dialog if the user hasn't been asked yet.
        /// Waits for the control handle, then shows the dialog on the UI thread.
        /// </summary>
        private void ShowUsageStatisticsOptIn(TermLensControl ctrl)
        {
            var settings = TermLensSettings.Load();
            // Use the v2 flag so users who answered the old opt-in dialog get
            // shown the rewritten (informational, default-on) dialog once.
            if (settings.UsageStatisticsAskedV2)
                return;

            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    // Wait for the control's window handle (same pattern as update checker)
                    for (int i = 0; i < 30 && !ctrl.IsHandleCreated; i++)
                        await System.Threading.Tasks.Task.Delay(500);

                    if (!ctrl.IsHandleCreated) return;

                    ctrl.BeginInvoke(new Action(() =>
                    {
                        using (var dlg = new UsageStatisticsDialog())
                        {
                            var result = dlg.ShowDialog();
                            settings.UsageStatisticsAsked = true;
                            settings.UsageStatisticsAskedV2 = true;
                            // Default-on: anything except an explicit "Turn it off"
                            // (DialogResult.No) is treated as keeping stats enabled.
                            // This covers Yes, Enter, Esc, and the X-close button.
                            settings.UsageStatisticsEnabled = (result != DialogResult.No);
                            if (settings.UsageStatisticsEnabled && string.IsNullOrEmpty(settings.UsageStatisticsId))
                                settings.UsageStatisticsId = Guid.NewGuid().ToString("D");
                            settings.Save();

                            // Sync the choice into the ViewPart's live _settings so the
                            // settings form shows the correct checkbox state without
                            // needing a Trados restart.
                            if (_settings != null)
                            {
                                _settings.UsageStatisticsAsked   = settings.UsageStatisticsAsked;
                                _settings.UsageStatisticsAskedV2 = settings.UsageStatisticsAskedV2;
                                _settings.UsageStatisticsEnabled = settings.UsageStatisticsEnabled;
                                _settings.UsageStatisticsId      = settings.UsageStatisticsId;
                            }

                            // If they opted in, send the first ping now
                            if (settings.UsageStatisticsEnabled)
                            {
                                System.Threading.Tasks.Task.Run(async () =>
                                {
                                    try { await UsageStatistics.SendPingAsync(); } catch { }
                                });
                            }
                        }
                    }));
                }
                catch { }
            });
        }

        private void ShowUpdateDialog(string newVersion, string releaseUrl, string pluginDownloadUrl)
        {
            var currentVersion = UpdateChecker.GetCurrentVersion();
            bool canOneClick = !string.IsNullOrEmpty(pluginDownloadUrl);

            using (var form = new Form())
            {
                form.Text = "Update Available";
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.StartPosition = FormStartPosition.CenterScreen;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.Size = new System.Drawing.Size(440, 260);
                form.Font = new System.Drawing.Font("Segoe UI", 9f);

                var lbl = new Label
                {
                    Text = $"A new version of Supervertaler for Trados is available.\n\n" +
                           $"Latest version:   v{newVersion}\n" +
                           $"Your version:      v{currentVersion}",
                    Location = new System.Drawing.Point(16, 16),
                    Size = new System.Drawing.Size(400, 80),
                    AutoSize = false
                };

                // Status label – used to show download progress / success
                var lblStatus = new Label
                {
                    Text = "",
                    Location = new System.Drawing.Point(16, 96),
                    Size = new System.Drawing.Size(400, 18),
                    ForeColor = System.Drawing.SystemColors.GrayText,
                    AutoSize = false,
                    Visible = false
                };

                // Link to open the Unpacked plugins folder – fallback for
                // Mac/Parallels users or if automatic install fails.
                // Scope-aware: targets whichever install scope the existing
                // plugin lives in (Roaming / LocalAppData / ProgramData),
                // matching the user's original Trados Plugin Installer choice.
                var unpackedPath = UpdateChecker.FindCurrentInstallScopeUnpackedDir();
                var lnkFolder = new LinkLabel
                {
                    Text = "Open Plugins folder (manual install)",
                    Location = new System.Drawing.Point(16, 118),
                    Size = new System.Drawing.Size(400, 18),
                    AutoSize = false
                };
                lnkFolder.LinkClicked += (s, ev) =>
                {
                    try
                    {
                        if (System.IO.Directory.Exists(unpackedPath))
                            System.Diagnostics.Process.Start("explorer.exe", unpackedPath);
                        else
                            System.Diagnostics.Process.Start("explorer.exe",
                                System.IO.Path.GetDirectoryName(unpackedPath));
                    }
                    catch { }
                };

                // Release notes link
                var lnkNotes = new LinkLabel
                {
                    Text = "What's new in this version?",
                    Location = new System.Drawing.Point(16, 140),
                    Size = new System.Drawing.Size(400, 18),
                    AutoSize = false
                };
                lnkNotes.LinkClicked += (s, ev) =>
                {
                    try { System.Diagnostics.Process.Start(releaseUrl); }
                    catch { }
                };

                var btnInstall = new Button
                {
                    Text = canOneClick ? "Install Update" : "Download",
                    DialogResult = DialogResult.None, // handled manually for one-click
                    Location = new System.Drawing.Point(16, 172),
                    Width = 120,
                    Height = 30,
                    FlatStyle = FlatStyle.System
                };

                var btnSkip = new Button
                {
                    Text = "Skip This Version",
                    DialogResult = DialogResult.Ignore,
                    Location = new System.Drawing.Point(144, 172),
                    Width = 130,
                    Height = 30,
                    FlatStyle = FlatStyle.System
                };

                var btnLater = new Button
                {
                    Text = "Remind Me Later",
                    DialogResult = DialogResult.Cancel,
                    Location = new System.Drawing.Point(282, 172),
                    Width = 130,
                    Height = 30,
                    FlatStyle = FlatStyle.System
                };

                btnInstall.Click += async (s, ev) =>
                {
                    if (!canOneClick)
                    {
                        // Fallback: open the release page in the browser
                        try { System.Diagnostics.Process.Start(releaseUrl); }
                        catch { }
                        form.DialogResult = DialogResult.Yes;
                        return;
                    }

                    // One-click install: download, clean up, prompt restart
                    btnInstall.Enabled = false;
                    btnSkip.Enabled = false;
                    btnLater.Enabled = false;
                    lblStatus.Visible = true;
                    lblStatus.Text = "Downloading update...";

                    try
                    {
                        // 1. Download .sdlplugin to the Packages folder of the
                        // install scope that already has Supervertaler (so we
                        // don't create an orphan duplicate in a different scope).
                        var packagesDir = UpdateChecker.FindCurrentInstallScopePackagesDir();
                        System.IO.Directory.CreateDirectory(packagesDir);

                        var pluginPath = System.IO.Path.Combine(packagesDir, "Supervertaler for Trados.sdlplugin");

                        await UpdateChecker.DownloadFileAsync(pluginDownloadUrl, pluginPath);

                        // 2. Rename current Unpacked folder to .old so Trados
                        //    re-extracts from the new package on next start
                        lblStatus.Text = "Preparing update...";
                        var unpackedDir = System.IO.Path.Combine(unpackedPath, "Supervertaler for Trados");
                        var oldDir = unpackedDir + ".old";

                        // Clean up any leftover .old folder from a previous update
                        if (System.IO.Directory.Exists(oldDir))
                        {
                            try { System.IO.Directory.Delete(oldDir, true); } catch { }
                        }

                        // Rename current folder – Windows allows this even with
                        // locked DLLs, the files stay accessible via open handles
                        if (System.IO.Directory.Exists(unpackedDir))
                        {
                            try { System.IO.Directory.Move(unpackedDir, oldDir); } catch { }
                        }

                        // 3. Done – auto-restart Trados
                        lblStatus.ForeColor = System.Drawing.Color.FromArgb(0, 128, 0);
                        lblStatus.Text = "Update installed successfully.";
                        lnkFolder.Visible = false;
                        lnkNotes.Visible = false;

                        var restartResult = MessageBox.Show(
                            $"Supervertaler for Trados v{newVersion} has been installed.\n\n" +
                            "Trados Studio needs to restart to load the new version.\n" +
                            "Restart now?",
                            "Update Installed",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Information);

                        if (restartResult == DialogResult.Yes)
                        {
                            // Find and launch Trados executable, then close current instance
                            try
                            {
                                var tradosExe = FindTradosExecutable();
                                if (tradosExe != null)
                                {
                                    // Launch a cmd process that waits for the current Trados
                                    // to fully exit before starting the new instance.
                                    // This prevents the race condition of two instances running.
                                    var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                                    var cmd = $"/c taskkill /PID {pid} /F >nul 2>&1 & " +
                                              $"ping -n 3 127.0.0.1 >nul 2>&1 & " +
                                              $"\"{tradosExe}\"";
                                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                                    {
                                        FileName = "cmd.exe",
                                        Arguments = cmd,
                                        WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                                        CreateNoWindow = true
                                    });
                                    form.DialogResult = DialogResult.Yes;
                                    // Exit immediately – cmd will wait, then relaunch
                                    System.Threading.SynchronizationContext.Current?.Post(_ =>
                                    {
                                        System.Windows.Forms.Application.Exit();
                                    }, null);
                                }
                                else
                                {
                                    MessageBox.Show(
                                        "Could not find Trados Studio executable.\n" +
                                        "Please close and restart Trados Studio manually.",
                                        "Restart",
                                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                                    form.DialogResult = DialogResult.Yes;
                                }
                            }
                            catch
                            {
                                MessageBox.Show(
                                    "Could not restart Trados Studio automatically.\n" +
                                    "Please close and restart Trados Studio manually.",
                                    "Restart",
                                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                                form.DialogResult = DialogResult.Yes;
                            }
                        }
                        else
                        {
                            form.DialogResult = DialogResult.Yes;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Download failed – fall back to opening the release page
                        lblStatus.ForeColor = System.Drawing.Color.FromArgb(192, 0, 0);
                        lblStatus.Text = "Download failed. Opening release page instead...";
                        btnInstall.Enabled = true;
                        btnSkip.Enabled = true;
                        btnLater.Enabled = true;

                        try { System.Diagnostics.Process.Start(releaseUrl); }
                        catch { }
                    }
                };

                form.Controls.AddRange(new Control[] { lbl, lblStatus, lnkFolder, lnkNotes, btnInstall, btnSkip, btnLater });
                form.AcceptButton = btnInstall;
                form.CancelButton = btnLater;

                var result = form.ShowDialog();

                if (result == DialogResult.Ignore)
                {
                    // Save "skip this version" to settings
                    var settings = TermLensSettings.Load();
                    settings.SkippedUpdateVersion = newVersion;
                    settings.Save();
                }
                // Remind Me Later – do nothing, will check again next session
            }
        }

        /// <summary>
        /// Locates the Trados Studio executable. Tries the running process first,
        /// then falls back to common install paths.
        /// </summary>
        private static string FindTradosExecutable()
        {
            // Try to find it from the currently running process
            try
            {
                foreach (var proc in System.Diagnostics.Process.GetProcesses())
                {
                    try
                    {
                        if (proc.ProcessName.IndexOf("SDLTradosStudio", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            proc.ProcessName.IndexOf("TradosStudio", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var path = proc.MainModule?.FileName;
                            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                                return path;
                        }
                    }
                    catch { }
                }
            }
            catch { }

            // Fallback: well-known install paths
            var candidates = new[]
            {
                @"C:\Program Files (x86)\Trados\Trados Studio\Studio18\SDLTradosStudio.exe",
                @"C:\Program Files\Trados\Trados Studio\Studio18\SDLTradosStudio.exe",
            };

            foreach (var path in candidates)
            {
                if (System.IO.File.Exists(path))
                    return path;
            }

            return null;
        }

    }
}
