using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.Desktop.IntegrationApi.Interfaces;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Supervertaler.Trados.Controls;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Models;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados
{
    /// <summary>
    /// Trados Studio editor ViewPart that docks the TermLens panel below the editor.
    /// Listens to segment changes and updates the terminology display accordingly.
    /// </summary>
    [ViewPart(
        Id = "TermLensEditorViewPart",
        Name = "Supervertaler for Trados",
        Description = "Terminology display and AI translation for Trados Studio",
        Icon = "TermLensIcon"
    )]
    [ViewPartLayout(typeof(EditorController), Dock = DockType.Top, Pinned = true)]
    public class TermLensEditorViewPart : AbstractViewPartController
    {
        private static readonly Lazy<TermLensControl> _control =
            new Lazy<TermLensControl>(() => new TermLensControl());

        private static readonly Lazy<BatchTranslateControl> _batchControl =
            new Lazy<BatchTranslateControl>(() => new BatchTranslateControl());

        private static readonly Lazy<MainPanelControl> _mainPanel =
            new Lazy<MainPanelControl>(() => new MainPanelControl(
                _control.Value, _batchControl.Value));

        // Single instance — Trados creates exactly one ViewPart of each type.
        // Used by AddTermAction to trigger a reload after inserting a term.
        private static TermLensEditorViewPart _currentInstance;

        private EditorController _editorController;
        private IStudioDocument _activeDocument;
        private TermLensSettings _settings;

        // Batch translate state
        private BatchTranslator _batchTranslator;
        private CancellationTokenSource _batchCts;

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

            // Wire up batch translate control events
            _batchControl.Value.TranslateRequested += OnBatchTranslateRequested;
            _batchControl.Value.StopRequested += OnBatchStopRequested;
            _batchControl.Value.ScopeChanged += OnBatchScopeChanged;

            // Apply persisted font size
            _control.Value.SetFontSize(_settings.PanelFontSize);

            // Load termbase: prefer saved setting, fall back to auto-detect
            LoadTermbase();

            // Display the current segment immediately (even without a termbase, show all words)
            UpdateFromActiveSegment();

            // Update batch translate tab with current provider info and segment counts
            UpdateBatchProviderDisplay();
            UpdateBatchSegmentCounts();
        }

        private void LoadTermbase(bool forceReload = false)
        {
            var disabled = _settings.DisabledTermbaseIds != null && _settings.DisabledTermbaseIds.Count > 0
                ? new HashSet<long>(_settings.DisabledTermbaseIds)
                : null;

            // Push project glossary ID to the control for pink/blue coloring
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

        private void OnSettingsRequested(object sender, EventArgs e)
        {
            SafeInvoke(() =>
            {
                using (var form = new TermLensSettingsForm(_settings))
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
                        UpdateFromActiveSegment();

                        // Refresh batch translate provider display (user may have changed AI settings)
                        UpdateBatchProviderDisplay();
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
                _activeDocument.DocumentFilterChanged -= OnDocumentFilterChanged;
            }

            _activeDocument = _editorController?.ActiveDocument;

            if (_activeDocument != null)
            {
                _activeDocument.ActiveSegmentChanged += OnActiveSegmentChanged;
                _activeDocument.DocumentFilterChanged += OnDocumentFilterChanged;
                UpdateFromActiveSegment();
                UpdateBatchSegmentCounts();
            }
            else
            {
                SafeInvoke(() =>
                {
                    _control.Value.Clear();
                    _batchControl.Value.Reset();
                });
            }
        }

        private void OnDocumentFilterChanged(object sender, DocumentFilterEventArgs e)
        {
            UpdateBatchSegmentCounts();
        }

        private void OnActiveSegmentChanged(object sender, EventArgs e)
        {
            UpdateFromActiveSegment();
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
                var sourceText = sourceSegment?.ToString() ?? "";
                SafeInvoke(() => _control.Value.UpdateSegment(sourceText));
            }
            catch (Exception)
            {
                // Silently handle — segment may not be available during transitions
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
            if (e.Entry == null) return;

            SafeInvoke(() =>
            {
                // Look up the termbase info for the entry's termbase
                TermbaseInfo termbase = null;
                if (!string.IsNullOrEmpty(_settings.TermbasePath) && File.Exists(_settings.TermbasePath))
                {
                    using (var reader = new TermbaseReader(_settings.TermbasePath))
                    {
                        if (reader.Open())
                            termbase = reader.GetTermbaseById(e.Entry.TermbaseId);
                    }
                }

                using (var dlg = new AddTermDialog(e.Entry, termbase))
                {
                    var parent = _control.Value.FindForm();
                    var result = parent != null ? dlg.ShowDialog(parent) : dlg.ShowDialog();

                    if (result == DialogResult.OK)
                    {
                        if (string.IsNullOrWhiteSpace(dlg.SourceTerm) ||
                            string.IsNullOrWhiteSpace(dlg.TargetTerm))
                        {
                            MessageBox.Show("Both source and target terms are required.",
                                "TermLens", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }

                        try
                        {
                            bool updated = TermbaseReader.UpdateTerm(
                                _settings.TermbasePath,
                                e.Entry.Id,
                                dlg.SourceTerm,
                                dlg.TargetTerm,
                                dlg.Definition,
                                isNonTranslatable: dlg.IsNonTranslatable);

                            if (updated)
                            {
                                // Incremental update: remove old entry, add updated one
                                _control.Value.RemoveTermFromIndex(e.Entry.Id);
                                var updatedEntry = new TermEntry
                                {
                                    Id = e.Entry.Id,
                                    SourceTerm = dlg.SourceTerm,
                                    TargetTerm = dlg.TargetTerm,
                                    SourceLang = e.Entry.SourceLang,
                                    TargetLang = e.Entry.TargetLang,
                                    TermbaseId = e.Entry.TermbaseId,
                                    TermbaseName = e.Entry.TermbaseName,
                                    IsProjectTermbase = e.Entry.IsProjectTermbase,
                                    Ranking = e.Entry.Ranking,
                                    Definition = dlg.Definition ?? "",
                                    Domain = e.Entry.Domain,
                                    Notes = e.Entry.Notes,
                                    Forbidden = e.Entry.Forbidden,
                                    CaseSensitive = e.Entry.CaseSensitive,
                                    IsNonTranslatable = dlg.IsNonTranslatable,
                                    TargetSynonyms = e.Entry.TargetSynonyms
                                };
                                _control.Value.AddTermToIndex(updatedEntry);
                                UpdateFromActiveSegment();
                            }
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(
                                $"Failed to update term: {ex.Message}\n\n" +
                                "The database may be locked by another application.",
                                "TermLens \u2014 Edit Term",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                }
            });
        }

        private void OnTermDeleteRequested(object sender, TermEditEventArgs e)
        {
            if (e.Entry == null) return;

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
            if (e.Entry == null) return;

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

        // ─── Batch Translate ──────────────────────────────────────────

        private void OnBatchTranslateRequested(object sender, EventArgs e)
        {
            SafeInvoke(() =>
            {
                if (_activeDocument == null)
                {
                    _batchControl.Value.AppendLog("No document open.", true);
                    return;
                }

                var aiSettings = _settings.AiSettings;
                if (aiSettings == null)
                {
                    _batchControl.Value.AppendLog("AI settings not configured. Open Settings to configure a provider.", true);
                    return;
                }

                // Resolve API key
                var provider = aiSettings.SelectedProvider ?? LlmModels.ProviderOpenAi;
                string apiKey;
                string baseUrl = null;
                string model = aiSettings.GetSelectedModel();

                if (provider == LlmModels.ProviderOllama)
                {
                    apiKey = "ollama"; // Ollama doesn't need a real key
                    baseUrl = aiSettings.OllamaEndpoint ?? "http://localhost:11434";
                }
                else if (provider == LlmModels.ProviderCustomOpenAi)
                {
                    var profile = aiSettings.GetActiveCustomProfile();
                    if (profile == null)
                    {
                        _batchControl.Value.AppendLog("No custom OpenAI profile configured.", true);
                        return;
                    }
                    apiKey = profile.ApiKey;
                    baseUrl = profile.Endpoint;
                    model = profile.Model;
                }
                else
                {
                    apiKey = LlmClient.ResolveApiKey(provider, aiSettings.ApiKeys);
                }

                if (string.IsNullOrEmpty(apiKey))
                {
                    _batchControl.Value.AppendLog(
                        $"No API key configured for {provider}. Open Settings \u2192 AI Settings to add one.", true);
                    return;
                }

                // Get language pair from the document
                var sourceLang = GetDocumentSourceLanguage();
                var targetLang = GetDocumentTargetLanguage();

                if (string.IsNullOrEmpty(sourceLang) || string.IsNullOrEmpty(targetLang))
                {
                    _batchControl.Value.AppendLog("Cannot determine source/target language from document.", true);
                    return;
                }

                // Collect segments based on selected scope
                var scope = _batchControl.Value.GetSelectedScope();
                var segments = CollectSegments(scope);

                if (segments.Count == 0)
                {
                    _batchControl.Value.AppendLog("No segments to translate.", true);
                    return;
                }

                // Get glossary terms for prompt injection
                var glossaryTerms = _control.Value.GetAllLoadedTerms();

                int batchSize = aiSettings.BatchSize > 0 ? aiSettings.BatchSize : 20;

                // Start the batch translation
                _batchControl.Value.SetRunning(true);
                _batchControl.Value.AppendLog(
                    $"Starting: {segments.Count} segments, provider={provider}, model={model}, " +
                    $"batch size={batchSize}");

                _batchCts = new CancellationTokenSource();
                _batchTranslator = new BatchTranslator();

                _batchTranslator.Progress += OnBatchProgress;
                _batchTranslator.SegmentTranslated += OnBatchSegmentTranslated;
                _batchTranslator.Completed += OnBatchCompleted;

                var ct = _batchCts.Token;

                Task.Run(async () =>
                {
                    try
                    {
                        await _batchTranslator.TranslateAsync(
                            segments, sourceLang, targetLang,
                            aiSettings, glossaryTerms, batchSize, ct);
                    }
                    catch (Exception ex)
                    {
                        SafeInvoke(() =>
                        {
                            _batchControl.Value.AppendLog($"Unexpected error: {ex.Message}", true);
                            _batchControl.Value.SetRunning(false);
                        });
                    }
                });
            });
        }

        private void OnBatchStopRequested(object sender, EventArgs e)
        {
            _batchCts?.Cancel();
            SafeInvoke(() => _batchControl.Value.AppendLog("Cancellation requested..."));
        }

        private void OnBatchScopeChanged(object sender, EventArgs e)
        {
            UpdateBatchSegmentCounts();
        }

        private void OnBatchProgress(object sender, BatchProgressEventArgs e)
        {
            SafeInvoke(() =>
            {
                _batchControl.Value.ReportProgress(e.Current, e.Total, e.Message, e.IsError);
            });
        }

        private void OnBatchSegmentTranslated(object sender, BatchSegmentResultEventArgs e)
        {
            SafeInvoke(() =>
            {
                try
                {
                    if (e.SegmentPairRef == null || _activeDocument == null) return;

                    // SegmentPairRef stores string[] { paragraphUnitId, segmentId }
                    var ids = e.SegmentPairRef as string[];
                    if (ids == null || ids.Length < 2) return;

                    _activeDocument.SetActiveSegmentPair(ids[0], ids[1], true);
                    _activeDocument.Selection.Target.Replace(e.Translation, "Supervertaler");
                }
                catch (Exception ex)
                {
                    _batchControl.Value.AppendLog(
                        $"Failed to write segment {e.SegmentIndex}: {ex.Message}", true);
                }
            });
        }

        private void OnBatchCompleted(object sender, BatchCompletedEventArgs e)
        {
            SafeInvoke(() =>
            {
                _batchControl.Value.ReportCompleted(
                    e.Translated, e.Failed, e.Skipped,
                    e.TotalTime, e.WasCancelled);

                // Update segment counts (some may now be filled)
                UpdateBatchSegmentCounts();
            });

            // Clean up
            if (_batchTranslator != null)
            {
                _batchTranslator.Progress -= OnBatchProgress;
                _batchTranslator.SegmentTranslated -= OnBatchSegmentTranslated;
                _batchTranslator.Completed -= OnBatchCompleted;
                _batchTranslator = null;
            }

            _batchCts?.Dispose();
            _batchCts = null;
        }

        private List<BatchSegment> CollectSegments(BatchScope scope)
        {
            var segments = new List<BatchSegment>();
            if (_activeDocument == null) return segments;

            try
            {
                // Use filtered or full segment pairs depending on scope
                var useFiltered = scope == BatchScope.Filtered
                    || scope == BatchScope.FilteredEmptyOnly;
                var emptyOnly = scope == BatchScope.EmptyOnly
                    || scope == BatchScope.FilteredEmptyOnly;
                var pairs = useFiltered
                    ? _activeDocument.FilteredSegmentPairs
                    : _activeDocument.SegmentPairs;

                int index = 0;
                foreach (var pair in pairs)
                {
                    var sourceText = pair.Source?.ToString() ?? "";
                    var targetText = pair.Target?.ToString() ?? "";

                    if (string.IsNullOrWhiteSpace(sourceText))
                    {
                        index++;
                        continue;
                    }

                    bool include = !emptyOnly || string.IsNullOrWhiteSpace(targetText);

                    if (include)
                    {
                        // Store IDs for later navigation via SetActiveSegmentPair
                        var parentPU = _activeDocument.GetParentParagraphUnit(pair);
                        var paragraphUnitId = parentPU.Properties.ParagraphUnitId.Id;
                        var segmentId = pair.Properties.Id.Id;

                        segments.Add(new BatchSegment
                        {
                            Index = index,
                            SourceText = sourceText,
                            ExistingTarget = targetText,
                            SegmentPairRef = new[] { paragraphUnitId, segmentId }
                        });
                    }

                    index++;
                }
            }
            catch (Exception)
            {
                // Document may not be accessible during transitions
            }

            return segments;
        }

        private void UpdateBatchSegmentCounts()
        {
            SafeInvoke(() =>
            {
                if (_activeDocument == null)
                {
                    _batchControl.Value.UpdateSegmentCounts(0, 0);
                    return;
                }

                try
                {
                    int total = 0;
                    int empty = 0;

                    foreach (var pair in _activeDocument.SegmentPairs)
                    {
                        total++;
                        var targetText = pair.Target?.ToString() ?? "";
                        if (string.IsNullOrWhiteSpace(targetText))
                            empty++;
                    }

                    // Get filtered count from Trados display filter
                    int filtered = _activeDocument.FilteredSegmentPairsCount;

                    _batchControl.Value.UpdateSegmentCounts(empty, total, filtered);
                }
                catch (Exception)
                {
                    _batchControl.Value.UpdateSegmentCounts(0, 0);
                }
            });
        }

        private void UpdateBatchProviderDisplay()
        {
            SafeInvoke(() =>
            {
                var ai = _settings?.AiSettings;
                if (ai == null)
                {
                    _batchControl.Value.UpdateProviderDisplay("Not configured", "");
                    return;
                }

                var provider = ai.SelectedProvider ?? "Not configured";
                var model = ai.GetSelectedModel() ?? "";

                if (provider == LlmModels.ProviderCustomOpenAi)
                {
                    var profile = ai.GetActiveCustomProfile();
                    if (profile != null)
                    {
                        provider = string.IsNullOrEmpty(profile.Name) ? "Custom" : profile.Name;
                        model = profile.Model ?? "";
                    }
                }

                _batchControl.Value.UpdateProviderDisplay(provider, model);
            });
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

        // ─── Single-segment AI translate ─────────────────────────────

        /// <summary>
        /// Called by AiTranslateSegmentAction (Ctrl+Alt+A / right-click menu).
        /// Translates the active segment using the configured AI provider.
        /// </summary>
        public static void HandleAiTranslateSegment()
        {
            var instance = _currentInstance;
            if (instance == null) return;

            instance.SafeInvoke(() =>
            {
                try
                {
                    if (instance._activeDocument?.ActiveSegmentPair == null)
                    {
                        MessageBox.Show("No active segment.",
                            "Supervertaler", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    var settings = instance._settings;
                    var aiSettings = settings?.AiSettings;
                    if (aiSettings == null)
                    {
                        MessageBox.Show(
                            "AI settings not configured.\n\nOpen Settings \u2192 AI Settings to configure a provider.",
                            "Supervertaler \u2014 AI Translate",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    // Resolve API key (same logic as batch translate)
                    var provider = aiSettings.SelectedProvider ?? LlmModels.ProviderOpenAi;
                    string apiKey;
                    string baseUrl = null;
                    string model = aiSettings.GetSelectedModel();

                    if (provider == LlmModels.ProviderOllama)
                    {
                        apiKey = "ollama";
                        baseUrl = aiSettings.OllamaEndpoint ?? "http://localhost:11434";
                    }
                    else if (provider == LlmModels.ProviderCustomOpenAi)
                    {
                        var profile = aiSettings.GetActiveCustomProfile();
                        if (profile == null)
                        {
                            MessageBox.Show("No custom OpenAI profile configured.",
                                "Supervertaler \u2014 AI Translate",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        apiKey = profile.ApiKey;
                        baseUrl = profile.Endpoint;
                        model = profile.Model;
                    }
                    else
                    {
                        apiKey = LlmClient.ResolveApiKey(provider, aiSettings.ApiKeys);
                    }

                    if (string.IsNullOrEmpty(apiKey))
                    {
                        MessageBox.Show(
                            $"No API key configured for {provider}.\n\nOpen Settings \u2192 AI Settings to add one.",
                            "Supervertaler \u2014 AI Translate",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    var sourceLang = instance.GetDocumentSourceLanguage();
                    var targetLang = instance.GetDocumentTargetLanguage();
                    if (string.IsNullOrEmpty(sourceLang) || string.IsNullOrEmpty(targetLang))
                    {
                        MessageBox.Show("Cannot determine source/target language.",
                            "Supervertaler \u2014 AI Translate",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    var sourceText = instance._activeDocument.ActiveSegmentPair.Source?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(sourceText))
                    {
                        MessageBox.Show("Active segment has no source text.",
                            "Supervertaler \u2014 AI Translate",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    // Get glossary terms for prompt injection
                    var glossaryTerms = _control.Value.GetAllLoadedTerms();

                    // Log to batch translate panel for visibility
                    _batchControl.Value.AppendLog($"Translating segment: \"{Truncate(sourceText, 60)}\"...");

                    // Run async — single segment, reuse TranslationPrompt + LlmClient
                    var capturedAiSettings = aiSettings;
                    Task.Run(async () =>
                    {
                        try
                        {
                            var systemPrompt = TranslationPrompt.BuildSystemPrompt(
                                sourceLang, targetLang, glossaryTerms);

                            var client = new LlmClient(
                                capturedAiSettings.SelectedProvider,
                                capturedAiSettings.GetSelectedModel(),
                                apiKey, baseUrl);

                            // For single segment, send it directly (not numbered batch format)
                            var userPrompt = $"Translate the following segment:\n\n{sourceText}";

                            var response = await client.SendPromptAsync(systemPrompt, userPrompt);

                            if (!string.IsNullOrWhiteSpace(response))
                            {
                                // Clean up the response (remove potential numbering or quotes)
                                var translation = response.Trim();
                                // Remove leading "1. " if the model added numbering
                                if (translation.StartsWith("1. "))
                                    translation = translation.Substring(3).Trim();
                                // Remove surrounding quotes if present
                                if (translation.Length >= 2 &&
                                    ((translation.StartsWith("\"") && translation.EndsWith("\"")) ||
                                     (translation.StartsWith("\u201c") && translation.EndsWith("\u201d"))))
                                    translation = translation.Substring(1, translation.Length - 2);

                                instance.SafeInvoke(() =>
                                {
                                    try
                                    {
                                        instance._activeDocument.Selection.Target.Replace(
                                            translation, "Supervertaler");
                                        _batchControl.Value.AppendLog(
                                            $"Done: \"{Truncate(translation, 60)}\"");
                                    }
                                    catch (Exception ex)
                                    {
                                        _batchControl.Value.AppendLog(
                                            $"Failed to write translation: {ex.Message}", true);
                                    }
                                });
                            }
                            else
                            {
                                instance.SafeInvoke(() =>
                                    _batchControl.Value.AppendLog("Empty response from AI provider.", true));
                            }
                        }
                        catch (Exception ex)
                        {
                            instance.SafeInvoke(() =>
                                _batchControl.Value.AppendLog(
                                    $"AI translate failed: {ex.Message}", true));
                        }
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Unexpected error: {ex.Message}",
                        "Supervertaler", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            });
        }

        private static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
                return text;
            return text.Substring(0, maxLength) + "\u2026";
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

            // Cancel any running batch translation
            _batchCts?.Cancel();
            _batchCts?.Dispose();
            _batchCts = null;

            if (_batchTranslator != null)
            {
                _batchTranslator.Progress -= OnBatchProgress;
                _batchTranslator.SegmentTranslated -= OnBatchSegmentTranslated;
                _batchTranslator.Completed -= OnBatchCompleted;
                _batchTranslator = null;
            }

            if (_activeDocument != null)
            {
                _activeDocument.ActiveSegmentChanged -= OnActiveSegmentChanged;
                _activeDocument.DocumentFilterChanged -= OnDocumentFilterChanged;
            }

            if (_editorController != null)
                _editorController.ActiveDocumentChanged -= OnActiveDocumentChanged;

            base.Dispose();
        }
    }
}
