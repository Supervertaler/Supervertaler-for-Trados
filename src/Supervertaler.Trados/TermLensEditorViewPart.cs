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
using Supervertaler.Trados.Controls;
using Supervertaler.Trados.Core;
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

        // --- Alt+digit chord state machine ---
        private static int? _pendingDigit;
        private static System.Windows.Forms.Timer _chordTimer;

        protected override IUIControl GetContentControl()
        {
            return _mainPanel.Value;
        }

        protected override void Initialize()
        {
            _currentInstance = this;

            // Load persisted settings
            _settings = TermLensSettings.Load();

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

            // Push project termbase ID to the control for pink/blue coloring
            _control.Value.SetProjectTermbaseId(_settings.ProjectTermbaseId);

            // 1. Use the saved termbase path if set and the file exists
            if (!string.IsNullOrEmpty(_settings.TermbasePath) && File.Exists(_settings.TermbasePath))
            {
                _control.Value.LoadTermbase(_settings.TermbasePath, disabled, forceReload);
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
                    _control.Value.LoadTermbase(path, disabled, forceReload);
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
                using (var form = new TermLensSettingsForm(_settings, _promptLibrary, defaultTab: 0))
                {
                    // Find a parent window handle for proper dialog parenting
                    var parent = _control.Value.FindForm();
                    var result = parent != null
                        ? form.ShowDialog(parent)
                        : form.ShowDialog();

                    if (result == System.Windows.Forms.DialogResult.OK)
                    {
                        // Settings already saved inside the form's OK handler.
                        // Apply font size change (user may have adjusted it in settings)
                        _control.Value.SetFontSize(_settings.PanelFontSize);

                        // Force reload — the user may have toggled glossaries.
                        LoadTermbase(forceReload: true);
                        LoadMultiTermTermbases();
                        UpdateFromActiveSegment();

                        // Refresh prompt library (user may have added/edited/deleted prompts)
                        _promptLibrary.Refresh();
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

                // Reload MultiTerm termbases — may have switched projects
                LoadMultiTermTermbases();
                UpdateFromActiveSegment();
            }
            else
            {
                SafeInvoke(() => _control.Value.Clear());
            }
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

                    using (var dlg = new TermEntryEditorDialog(entryTermbases, dbPath))
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

                using (var dlg = new TermEntryEditorDialog(e.Entry, dbPath, termbase))
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
        /// Called by AddTermAction after a term is inserted.
        /// Reloads settings and the term index so the new term appears immediately.
        /// </summary>
        public static void NotifyTermAdded()
        {
            var instance = _currentInstance;
            if (instance == null) return;

            // Re-read settings in case WriteTermbaseId or disabled list changed
            instance._settings = TermLensSettings.Load();
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

        /// <summary>
        /// Called by TermInsertDigitNAction when Alt+digit is pressed.
        /// Implements a two-digit chord state machine with 400ms timeout.
        /// </summary>
        public static void HandleDigitPress(int digit)
        {
            var instance = _currentInstance;
            if (instance == null) return;

            // If there's already a pending first digit, combine into a two-digit number
            if (_pendingDigit.HasValue)
            {
                StopChordTimer();
                int number = _pendingDigit.Value * 10 + digit;
                _pendingDigit = null;
                instance.InsertTermByIndex(number);
                return;
            }

            // Check how many matched terms are in the current segment
            int matchCount = _control.Value.MatchCount;

            if (matchCount <= 9)
            {
                // ≤9 terms: insert immediately, no chord wait needed
                int number = digit == 0 ? 10 : digit;
                instance.InsertTermByIndex(number);
            }
            else
            {
                // 10+ terms: start chord timer, wait for possible second digit
                _pendingDigit = digit;
                StartChordTimer();
            }
        }

        private static void StartChordTimer()
        {
            StopChordTimer();
            _chordTimer = new System.Windows.Forms.Timer { Interval = 400 };
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

            int digit = _pendingDigit.Value;
            _pendingDigit = null;

            // Single digit: 0 means term 10, otherwise 1-9
            int number = digit == 0 ? 10 : digit;
            instance.InsertTermByIndex(number);
        }

        private void InsertTermByIndex(int oneBasedIndex)
        {
            if (_activeDocument == null) return;

            var entry = _control.Value.GetTermByIndex(oneBasedIndex);
            if (entry == null) return;

            try
            {
                _activeDocument.Selection.Target.Replace(entry.TargetTerm, "TermLens");
            }
            catch (Exception)
            {
                // Silently handle — editor may not allow insertion at this moment
            }
        }

        // ─── Term Picker dialog ─────────────────────────────────────

        /// <summary>
        /// Called by TermPickerAction (Ctrl+Shift+G).
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
    }
}
