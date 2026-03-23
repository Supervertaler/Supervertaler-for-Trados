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
        Name = "Supervertaler TermLens",
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

        // Single instance — Trados creates exactly one ViewPart of each type.
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

        // Prompt library (shared — used by settings dialog)
        private PromptLibrary _promptLibrary;

        // Per-project settings — tracks the active project's .sdlproj path
        private string _currentProjectPath;
        private string _currentProjectName;

        // --- Alt+digit shortcut state machine ---
        private static int? _pendingDigit;
        private static int _pendingRepeatCount;   // repeated mode: how many times same digit pressed
        private static int _pendingAccumulated;    // sequential mode: accumulated number so far
        private static System.Windows.Forms.Timer _chordTimer;

        protected override IUIControl GetContentControl()
        {
            return _mainPanel.Value;
        }

        protected override void Initialize()
        {
            _currentInstance = this;

            // License check — show/hide activation overlay based on tier
            LicenseManager.Instance.LicenseStateChanged += (s, e) =>
            {
                _control.Value.BeginInvoke(new Action(() =>
                {
                    if (LicenseManager.Instance.HasTier1Access)
                        _control.Value.HideLicenseRequired();
                    else
                        _control.Value.ShowLicenseRequired();
                }));
            };

            // Check for plugin updates in the background.
            // Runs regardless of license state — even expired-trial users should
            // know about new versions.
            var ctrl = _control.Value;
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var update = await UpdateChecker.CheckForUpdateAsync();
                    if (!update.HasValue) return;

                    // Wait for the control's window handle to be created.
                    // Initialize() runs before the control is parented in the
                    // Trados docking framework, so IsHandleCreated may still be
                    // false.  Poll briefly — the handle is created within a
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
                    // Silently ignore — network errors, parse errors, etc.
                }
            });

            // Anonymous usage statistics — opt-in dialog + background ping
            ShowUsageStatisticsOptIn(ctrl);
            System.Threading.Tasks.Task.Run(async () =>
            {
                try { await UsageStatistics.SendPingAsync(); } catch { }
            });

            if (LicenseManager.Instance.CurrentTier == LicenseTier.None)
            {
                _control.Value.ShowLicenseRequired();
                return;
            }

            // Load persisted settings
            _settings = TermLensSettings.Load();

            // Apply global UI scale factor before any controls are created
            UiScale.Factor = _settings.UiScaleFactor;

            // Initialize prompt library and seed built-in prompts on first run
            _promptLibrary = new PromptLibrary();
            _promptLibrary.EnsureBuiltInPrompts();

            _editorController = SdlTradosStudio.Application.GetController<EditorController>();

            if (_editorController != null)
            {
                _editorController.ActiveDocumentChanged += OnActiveDocumentChanged;

                // If a document is already open, wire up to it immediately
                if (_editorController.ActiveDocument != null)
                {
                    _activeDocument = _editorController.ActiveDocument;
                    _activeDocument.ActiveSegmentChanged += OnActiveSegmentChanged;

                    // Apply per-project settings if available
                    ApplyProjectSettingsFromDocument(_activeDocument);
                }
            }

            // Wire up term insertion — when user clicks a translation in the panel
            _control.Value.TermInsertRequested += OnTermInsertRequested;

            // Wire up right-click edit/delete/non-translatable on term blocks
            _control.Value.TermEditRequested += OnTermEditRequested;
            _control.Value.TermDeleteRequested += OnTermDeleteRequested;
            _control.Value.TermNonTranslatableToggled += OnTermNonTranslatableToggled;

            // Wire up the gear/settings button (on the MainPanelControl, visible on all tabs)
            _mainPanel.Value.SettingsRequested += OnSettingsRequested;

            // Wire up font size changes from the A+/A- buttons in the panel header
            _control.Value.FontSizeChanged += OnFontSizeChanged;

            // Apply persisted font size
            _control.Value.SetFontSize(_settings.PanelFontSize);

            // Apply shortcut style to badge rendering
            TermBlock.UseRepeatedDigitBadges = _settings.TermShortcutStyle == "repeated";

            // Load termbase: prefer saved setting, fall back to auto-detect
            LoadTermbase();

            // Load MultiTerm termbases from the active Trados project (if any)
            LoadMultiTermTermbases();

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

                if (_multiTermConfigs.Count == 0) return;

                // Filter out disabled MultiTerm termbases
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
                        using (var reader = new MultiTermReader(config.FilePath))
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
                                // Direct access failed — try API fallback later
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
                    catch (Exception)
                    {
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

                if (mergedIndex.Count > 0)
                    SafeInvoke(() => _control.Value.MergeMultiTermEntries(mergedIndex, infos));
            }
            catch (Exception)
            {
                // Silently handle — MultiTerm integration should never crash the plugin
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
                                        provider = factory.CreateTerminologyProvider(uri, null);
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
        /// Returns detected MultiTerm configs for the settings dialog.
        /// </summary>
        public static List<MultiTermTermbaseConfig> GetMultiTermConfigs()
        {
            return _currentInstance?._multiTermConfigs ?? new List<MultiTermTermbaseConfig>();
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
                        // User imported settings from file — reload everything from disk
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

                        // Force reload — the user may have toggled glossaries.
                        LoadTermbase(forceReload: true);
                        LoadMultiTermTermbases();
                        UpdateFromActiveSegment();

                        // Refresh prompt library (user may have added/edited/deleted prompts)
                        _promptLibrary.Refresh();

                        // Notify AI Assistant to reload settings from disk
                        AiAssistantViewPart.NotifySettingsChanged();
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
            }

            _activeDocument = _editorController?.ActiveDocument;

            if (_activeDocument != null)
            {
                _activeDocument.ActiveSegmentChanged += OnActiveSegmentChanged;

                // Check if the project has changed — if so, save outgoing and load incoming settings
                ApplyProjectSettingsFromDocument(_activeDocument);

                // Reload MultiTerm termbases — may have switched projects
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

                // Same project (or no project) — nothing to do
                if (string.Equals(projectPath, _currentProjectPath, StringComparison.OrdinalIgnoreCase))
                    return;

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
                    _settings.ApplyProjectOverlay(ps);

                    // Reload termbase with the project-specific path
                    LoadTermbase(forceReload: true);
                }
            }
            catch
            {
                // Never crash on project detection — fall back to global settings
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
            }
            catch
            {
                // Silently ignore save failures
            }
        }

        /// <summary>
        /// Returns the .sdlproj path of the currently active Trados project, or null.
        /// Used by the settings dialog to save project-specific settings on OK.
        /// </summary>
        public static string GetCurrentProjectPath()
        {
            return _currentInstance?._currentProjectPath;
        }

        /// <summary>
        /// Returns the name of the currently active Trados project, or null.
        /// </summary>
        public static string GetCurrentProjectName()
        {
            return _currentInstance?._currentProjectName;
        }

        private void OnActiveSegmentChanged(object sender, EventArgs e)
        {
            // Reload MultiTerm terms if any .sdltb file has been modified since last load
            // (e.g., user added terms via Trados's native MultiTerm interface)
            if (HasMultiTermFileChanged())
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
                        // Swallow — fallback search should never crash the plugin
                    }
                }

                SafeInvoke(() => _control.Value.UpdateSegment(sourceText));
            }
            catch (Exception)
            {
                // Silently handle — segment may not be available during transitions
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
                // Silently handle — editor may not allow insertion at this moment
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
                        // Term was saved (possibly with synonym changes) — force reload
                        // to rebuild the index including source synonym keys
                        LoadTermbase(forceReload: true);
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
        /// Reloads settings from disk. Called by AiAssistantViewPart after its
        /// settings dialog saves, so this ViewPart picks up changes made there.
        /// </summary>
        public static void NotifySettingsChanged()
        {
            var instance = _currentInstance;
            if (instance == null) return;
            instance._settings = TermLensSettings.Load();
            UiScale.Factor = instance._settings.UiScaleFactor;
            _control.Value.SetFontSize(instance._settings.PanelFontSize);
            TermBlock.UseRepeatedDigitBadges = instance._settings.TermShortcutStyle == "repeated";
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
                    instance._settings.ApplyProjectOverlay(ps);
            }

            instance.LoadTermbase(forceReload: true);
            instance.UpdateFromActiveSegment();
        }

        // ─── Context sharing for AI Assistant ─────────────────────

        /// <summary>
        /// Returns all loaded termbase terms for the AI Assistant context.
        /// Returns already-computed data — no DB queries.
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
        /// Returns already-computed data — no DB queries.
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
        //   "sequential" — Alt+4,5 = term 45 (timer waits for next digit)
        //   "repeated"   — Alt+5,5 = term 14 (same digit = next tier)

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
                    // We have enough digits — insert immediately
                    _pendingDigit = null;
                    _pendingAccumulated = 0;
                    int number = accumulated == 0 ? 10 : accumulated;
                    instance.InsertTermByIndex(number);
                }
                else
                {
                    // Could still have more digits — keep waiting
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
                // Different digit pressed — insert the pending one first,
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
                // Silently handle — editor may not allow insertion at this moment
            }
        }

        // ─── Term Picker dialog ─────────────────────────────────────

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

        // ─────────────────────────────────────────────────────────────

        public override void Dispose()
        {
            if (_currentInstance == this)
                _currentInstance = null;

            StopChordTimer();
            DisposeFallbackProviders();

            if (_activeDocument != null)
            {
                _activeDocument.ActiveSegmentChanged -= OnActiveSegmentChanged;
            }

            if (_editorController != null)
                _editorController.ActiveDocumentChanged -= OnActiveDocumentChanged;

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
            if (settings.UsageStatisticsAsked)
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
                            settings.UsageStatisticsEnabled = (result == DialogResult.Yes);
                            if (settings.UsageStatisticsEnabled && string.IsNullOrEmpty(settings.UsageStatisticsId))
                                settings.UsageStatisticsId = Guid.NewGuid().ToString("D");
                            settings.Save();

                            // Sync the choice into the ViewPart's live _settings so the
                            // settings form shows the correct checkbox state without
                            // needing a Trados restart.
                            if (_settings != null)
                            {
                                _settings.UsageStatisticsAsked   = settings.UsageStatisticsAsked;
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

                // Status label — used to show download progress / success
                var lblStatus = new Label
                {
                    Text = "",
                    Location = new System.Drawing.Point(16, 96),
                    Size = new System.Drawing.Size(400, 18),
                    ForeColor = System.Drawing.SystemColors.GrayText,
                    AutoSize = false,
                    Visible = false
                };

                // Link to open the Unpacked plugins folder — fallback for
                // Mac/Parallels users or if automatic install fails.
                var unpackedPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Trados\Trados Studio\18\Plugins\Unpacked");
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
                        // 1. Download .sdlplugin to Packages folder
                        var packagesDir = System.IO.Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            @"Trados\Trados Studio\18\Plugins\Packages");
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

                        // Rename current folder — Windows allows this even with
                        // locked DLLs, the files stay accessible via open handles
                        if (System.IO.Directory.Exists(unpackedDir))
                        {
                            try { System.IO.Directory.Move(unpackedDir, oldDir); } catch { }
                        }

                        // 3. Done — auto-restart Trados
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
                                    // Exit immediately — cmd will wait, then relaunch
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
                        // Download failed — fall back to opening the release page
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
                // Remind Me Later — do nothing, will check again next session
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
