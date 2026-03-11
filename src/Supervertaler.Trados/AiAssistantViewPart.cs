using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.Desktop.IntegrationApi.Interfaces;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Supervertaler.Trados.Controls;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Licensing;
using Supervertaler.Trados.Models;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados
{
    /// <summary>
    /// Dockable ViewPart for the Supervertaler Assistant.
    /// Hosts the AI Chat interface and Batch Translate tabs.
    /// Provides a conversational interface where translators can ask questions
    /// about translations, get suggestions, and apply them to the target segment.
    /// </summary>
    [ViewPart(
        Id = "AiAssistantViewPart",
        Name = "Supervertaler Assistant",
        Description = "AI-powered translation assistant with chat and batch translate",
        Icon = "TermLensIcon"
    )]
    [ViewPartLayout(typeof(EditorController), Dock = DockType.Right, Pinned = false)]
    public class AiAssistantViewPart : AbstractViewPartController
    {
        private static readonly Lazy<AiAssistantControl> _control =
            new Lazy<AiAssistantControl>(() => new AiAssistantControl());

        private static AiAssistantViewPart _currentInstance;

        private EditorController _editorController;
        private IStudioDocument _activeDocument;
        private TermLensSettings _settings;

        // Chat state
        private readonly List<ChatMessage> _chatHistory = new List<ChatMessage>();
        private CancellationTokenSource _chatCts;

        // Batch translate state
        private BatchTranslator _batchTranslator;
        private CancellationTokenSource _batchCts;

        // Prompt library
        private PromptLibrary _promptLibrary;

        protected override IUIControl GetContentControl()
        {
            return _control.Value;
        }

        protected override void Initialize()
        {
            _currentInstance = this;

            // License check — show/hide upgrade overlay based on tier
            LicenseManager.Instance.LicenseStateChanged += (s, e) =>
            {
                _control.Value.BeginInvoke(new Action(() =>
                {
                    if (LicenseManager.Instance.HasTier2Access)
                        _control.Value.HideUpgradeRequired();
                    else
                        _control.Value.ShowUpgradeRequired();
                }));
            };

            if (!LicenseManager.Instance.HasTier2Access)
            {
                _control.Value.ShowUpgradeRequired();
                return;
            }

            _settings = TermLensSettings.Load();

            // Initialize prompt library — try to share with TermLens if already loaded
            _promptLibrary = TermLensEditorViewPart.GetPromptLibrary() ?? new PromptLibrary();
            _promptLibrary.EnsureBuiltInPrompts();

            _editorController = SdlTradosStudio.Application.GetController<EditorController>();
            if (_editorController != null)
            {
                _editorController.ActiveDocumentChanged += OnActiveDocumentChanged;

                if (_editorController.ActiveDocument != null)
                {
                    _activeDocument = _editorController.ActiveDocument;
                    _activeDocument.ActiveSegmentChanged += OnActiveSegmentChanged;
                    _activeDocument.DocumentFilterChanged += OnDocumentFilterChanged;
                }
            }

            // Wire chat control events
            _control.Value.SendRequested += OnSendRequested;
            _control.Value.ClearRequested += OnClearRequested;
            _control.Value.ApplyToTargetRequested += OnApplyToTargetRequested;
            _control.Value.StopRequested += OnStopRequested;

            // Wire settings/help buttons
            _control.Value.SettingsRequested += OnSettingsRequested;

            // Wire batch translate control events
            var batchControl = _control.Value.BatchTranslateControl;
            batchControl.TranslateRequested += OnBatchTranslateRequested;
            batchControl.StopRequested += OnBatchStopRequested;
            batchControl.ScopeChanged += OnBatchScopeChanged;

            // Initial context update
            UpdateContextDisplay();
            UpdateProviderDisplay();
            UpdateBatchProviderDisplay();
            UpdateBatchSegmentCounts();
            PopulateBatchPromptDropdown();
        }

        // ─── Document / Segment Events ────────────────────────────

        private void OnActiveDocumentChanged(object sender, DocumentEventArgs e)
        {
            if (_activeDocument != null)
            {
                try { _activeDocument.ActiveSegmentChanged -= OnActiveSegmentChanged; }
                catch { }
                try { _activeDocument.DocumentFilterChanged -= OnDocumentFilterChanged; }
                catch { }
            }

            _activeDocument = _editorController?.ActiveDocument;

            if (_activeDocument != null)
            {
                _activeDocument.ActiveSegmentChanged += OnActiveSegmentChanged;
                _activeDocument.DocumentFilterChanged += OnDocumentFilterChanged;
                SafeInvoke(UpdateContextDisplay);
                UpdateBatchSegmentCounts();
            }
            else
            {
                SafeInvoke(() =>
                {
                    UpdateContextDisplay();
                    _control.Value.BatchTranslateControl.Reset();
                });
            }
        }

        private void OnActiveSegmentChanged(object sender, EventArgs e)
        {
            SafeInvoke(UpdateContextDisplay);
        }

        private void OnDocumentFilterChanged(object sender, DocumentFilterEventArgs e)
        {
            UpdateBatchSegmentCounts();
        }

        private void UpdateContextDisplay()
        {
            var sourceText = _activeDocument?.ActiveSegmentPair?.Source?.ToString();
            var targetText = _activeDocument?.ActiveSegmentPair?.Target?.ToString();
            var matches = TermLensEditorViewPart.GetCurrentSegmentMatches();
            var langPair = BuildLangPairString();

            _control.Value.UpdateContextInfo(
                sourceText, targetText, matches.Count, langPair);
        }

        private void UpdateProviderDisplay()
        {
            var aiSettings = _settings?.AiSettings;
            if (aiSettings != null)
            {
                var provider = aiSettings.SelectedProvider ?? "openai";
                var model = aiSettings.GetSelectedModel() ?? "";
                _control.Value.UpdateProviderInfo(provider, model);
            }
        }

        // ─── Settings ───────────────────────────────────────────────

        private void OnSettingsRequested(object sender, EventArgs e)
        {
            SafeInvoke(() =>
            {
                using (var form = new TermLensSettingsForm(_settings, _promptLibrary, defaultTab: 1))
                {
                    var parent = _control.Value.FindForm();
                    var result = parent != null
                        ? form.ShowDialog(parent)
                        : form.ShowDialog();

                    if (result == System.Windows.Forms.DialogResult.OK)
                    {
                        // Refresh provider displays
                        UpdateProviderDisplay();
                        UpdateBatchProviderDisplay();

                        // Refresh prompt library (user may have added/edited/deleted prompts)
                        _promptLibrary.Refresh();
                        PopulateBatchPromptDropdown();

                        // Notify TermLens to reload settings from disk
                        TermLensEditorViewPart.NotifySettingsChanged();
                    }
                }
            });
        }

        // ─── Chat Logic ───────────────────────────────────────────

        private void OnSendRequested(object sender, ChatSendEventArgs args)
        {
            var messageText = args.Text;
            var images = args.Images;

            if (string.IsNullOrWhiteSpace(messageText) && (images == null || images.Count == 0))
                return;

            // 1. Add user message to history and display
            var userMsg = new ChatMessage
            {
                Role = ChatRole.User,
                Content = messageText ?? "",
                Images = images
            };
            _chatHistory.Add(userMsg);
            _control.Value.AddMessage(userMsg);

            // 2. Gather current context
            var sourceText = _activeDocument?.ActiveSegmentPair?.Source?.ToString();
            var targetText = _activeDocument?.ActiveSegmentPair?.Target?.ToString();
            var sourceLang = GetDocumentSourceLanguage();
            var targetLang = GetDocumentTargetLanguage();

            // Filter matched terms by AI-disabled termbase IDs
            var disabledIds = _settings?.AiSettings?.DisabledAiTermbaseIds ?? new List<long>();
            var allMatches = TermLensEditorViewPart.GetCurrentSegmentMatches();
            var matchedTerms = disabledIds.Count > 0
                ? allMatches.Where(m => !disabledIds.Contains(m.PrimaryEntry?.TermbaseId ?? 0)).ToList()
                : allMatches;

            // Gather TM matches if enabled
            List<TmMatch> tmMatches = null;
            if (_settings?.AiSettings?.IncludeTmMatches != false)
                tmMatches = GetTmMatches();

            // Document context (all segments for document type analysis)
            List<string> documentSegments = null;
            int activeSegmentIndex = -1;
            int totalSegmentCount = 0;
            if (_settings?.AiSettings?.IncludeDocumentContext != false)
            {
                var docCtx = CollectDocumentContext();
                documentSegments = docCtx.Item1;
                activeSegmentIndex = docCtx.Item2;
                totalSegmentCount = documentSegments?.Count ?? 0;
            }

            // Surrounding segments (2 before + 2 after)
            var surroundingSegments = GetSurroundingSegments(2);

            // Project metadata
            var projectName = GetProjectName();
            var fileName = GetFileName();

            // 3. Build system prompt with full context
            var chatCtx = new ChatContext
            {
                SourceLang = sourceLang,
                TargetLang = targetLang,
                SourceText = sourceText,
                TargetText = targetText,
                MatchedTerms = matchedTerms,
                TmMatches = tmMatches,
                ProjectName = projectName,
                FileName = fileName,
                DocumentSegments = documentSegments,
                ActiveSegmentIndex = activeSegmentIndex,
                TotalSegmentCount = totalSegmentCount,
                MaxDocumentSegments = _settings?.AiSettings?.DocumentContextMaxSegments ?? 500,
                SurroundingSegments = surroundingSegments,
                IncludeTermMetadata = _settings?.AiSettings?.IncludeTermMetadata != false
            };
            var systemPrompt = ChatPrompt.BuildSystemPrompt(chatCtx);

            // 4. Build message window (last 10 messages for context)
            var messagesToSend = BuildMessageWindow(_chatHistory, 10);

            // 5. Resolve provider / API key
            var aiSettings = _settings?.AiSettings;
            if (aiSettings == null)
            {
                AddErrorMessage("AI settings not configured. Open Settings \u2192 AI Settings to configure a provider.");
                return;
            }

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
                    AddErrorMessage("No custom OpenAI profile configured.");
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
                AddErrorMessage($"No API key configured for {provider}. Open Settings \u2192 AI Settings to add one.");
                return;
            }

            // 6. Show thinking state
            _control.Value.SetThinking(true);
            _chatCts?.Cancel();
            _chatCts = new CancellationTokenSource();
            var ct = _chatCts.Token;

            // Capture for async
            var capturedProvider = provider;
            var capturedModel = model;
            var capturedKey = apiKey;
            var capturedBaseUrl = baseUrl;
            var capturedSystemPrompt = systemPrompt;
            var capturedMessages = messagesToSend;

            // 7. Call LLM async
            Task.Run(async () =>
            {
                try
                {
                    var client = new LlmClient(capturedProvider, capturedModel, capturedKey, capturedBaseUrl);
                    var response = await client.SendChatAsync(
                        capturedMessages, capturedSystemPrompt,
                        maxTokens: 4096, cancellationToken: ct);

                    var assistantMsg = new ChatMessage
                    {
                        Role = ChatRole.Assistant,
                        Content = response?.Trim() ?? "(No response)"
                    };

                    SafeInvoke(() =>
                    {
                        _chatHistory.Add(assistantMsg);
                        _control.Value.AddMessage(assistantMsg);
                        _control.Value.SetThinking(false);
                    });
                }
                catch (OperationCanceledException)
                {
                    SafeInvoke(() => _control.Value.SetThinking(false));
                }
                catch (Exception ex)
                {
                    SafeInvoke(() =>
                    {
                        _control.Value.SetThinking(false);
                        AddErrorMessage($"Error: {ex.Message}");
                    });
                }
            });
        }

        private void OnClearRequested(object sender, EventArgs e)
        {
            _chatHistory.Clear();
            _control.Value.ClearMessages();
        }

        private void OnStopRequested(object sender, EventArgs e)
        {
            _chatCts?.Cancel();
        }

        private void OnApplyToTargetRequested(object sender, string text)
        {
            if (_activeDocument == null || string.IsNullOrEmpty(text))
                return;

            try
            {
                _activeDocument.Selection.Target.Replace(text, "Supervertaler AI");
            }
            catch (Exception)
            {
                // Editor may not allow insertion at this moment
            }
        }

        // ─── Prompt Library ─────────────────────────────────────────

        private void PopulateBatchPromptDropdown()
        {
            SafeInvoke(() =>
            {
                var prompts = _promptLibrary?.GetAllPrompts();
                var selectedPath = _settings?.AiSettings?.SelectedPromptPath ?? "";
                _control.Value.BatchTranslateControl.SetPrompts(prompts, selectedPath);
            });
        }

        /// <summary>
        /// Resolves the custom prompt content for the currently selected prompt.
        /// Applies variable substitution for source/target language.
        /// </summary>
        private string ResolveCustomPromptContent(string sourceLang, string targetLang)
        {
            var selectedPath = _settings?.AiSettings?.SelectedPromptPath;
            if (string.IsNullOrEmpty(selectedPath) || _promptLibrary == null)
                return null;

            var prompt = _promptLibrary.GetPromptByRelativePath(selectedPath);
            if (prompt == null || string.IsNullOrWhiteSpace(prompt.Content))
                return null;

            return PromptLibrary.ApplyVariables(prompt.Content, sourceLang, targetLang);
        }

        // ─── Batch Translate ────────────────────────────────────────

        private void OnBatchTranslateRequested(object sender, EventArgs e)
        {
            SafeInvoke(() =>
            {
                var batchControl = _control.Value.BatchTranslateControl;

                if (_activeDocument == null)
                {
                    batchControl.AppendLog("No document open.", true);
                    return;
                }

                var aiSettings = _settings.AiSettings;
                if (aiSettings == null)
                {
                    batchControl.AppendLog("AI settings not configured. Open Settings to configure a provider.", true);
                    return;
                }

                // Resolve API key
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
                        batchControl.AppendLog("No custom OpenAI profile configured.", true);
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
                    batchControl.AppendLog(
                        $"No API key configured for {provider}. Open Settings \u2192 AI Settings to add one.", true);
                    return;
                }

                // Get language pair from the document
                var sourceLang = GetDocumentSourceLanguage();
                var targetLang = GetDocumentTargetLanguage();

                if (string.IsNullOrEmpty(sourceLang) || string.IsNullOrEmpty(targetLang))
                {
                    batchControl.AppendLog("Cannot determine source/target language from document.", true);
                    return;
                }

                // Collect segments based on selected scope
                var scope = batchControl.GetSelectedScope();
                var segments = CollectSegments(scope);

                if (segments.Count == 0)
                {
                    batchControl.AppendLog("No segments to translate.", true);
                    return;
                }

                // Get termbase terms for prompt injection (filtered by AI-disabled list)
                var allTerms = TermLensEditorViewPart.GetCurrentTermbaseTerms();
                var batchDisabledIds = _settings?.AiSettings?.DisabledAiTermbaseIds ?? new List<long>();
                var termbaseTerms = batchDisabledIds.Count > 0
                    ? allTerms.Where(t => !batchDisabledIds.Contains(t.TermbaseId)).ToList()
                    : allTerms;

                // Resolve custom prompt from library selection
                var selectedPromptPath = batchControl.GetSelectedPromptPath();
                aiSettings.SelectedPromptPath = selectedPromptPath;
                _settings.Save();

                var customPromptContent = ResolveCustomPromptContent(sourceLang, targetLang);
                var customSystemPrompt = aiSettings.CustomSystemPrompt;

                int batchSize = aiSettings.BatchSize > 0 ? aiSettings.BatchSize : 20;

                // Start the batch translation
                batchControl.SetRunning(true);
                batchControl.AppendLog(
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
                            aiSettings, termbaseTerms, batchSize, ct,
                            customPromptContent, customSystemPrompt);
                    }
                    catch (Exception ex)
                    {
                        SafeInvoke(() =>
                        {
                            batchControl.AppendLog($"Unexpected error: {ex.Message}", true);
                            batchControl.SetRunning(false);
                        });
                    }
                });
            });
        }

        private void OnBatchStopRequested(object sender, EventArgs e)
        {
            _batchCts?.Cancel();
            SafeInvoke(() => _control.Value.BatchTranslateControl.AppendLog("Cancellation requested..."));
        }

        private void OnBatchScopeChanged(object sender, EventArgs e)
        {
            UpdateBatchSegmentCounts();
        }

        private void OnBatchProgress(object sender, BatchProgressEventArgs e)
        {
            SafeInvoke(() =>
            {
                _control.Value.BatchTranslateControl.ReportProgress(e.Current, e.Total, e.Message, e.IsError);
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
                    _control.Value.BatchTranslateControl.AppendLog(
                        $"Failed to write segment {e.SegmentIndex}: {ex.Message}", true);
                }
            });
        }

        private void OnBatchCompleted(object sender, BatchCompletedEventArgs e)
        {
            SafeInvoke(() =>
            {
                _control.Value.BatchTranslateControl.ReportCompleted(
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
                    _control.Value.BatchTranslateControl.UpdateSegmentCounts(0, 0);
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

                    _control.Value.BatchTranslateControl.UpdateSegmentCounts(empty, total, filtered);
                }
                catch (Exception)
                {
                    _control.Value.BatchTranslateControl.UpdateSegmentCounts(0, 0);
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
                    _control.Value.BatchTranslateControl.UpdateProviderDisplay("Not configured", "");
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

                _control.Value.BatchTranslateControl.UpdateProviderDisplay(provider, model);
            });
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

                    // Resolve API key
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

                    // Get termbase terms for prompt injection (filtered by AI-disabled list)
                    var allTbTerms = TermLensEditorViewPart.GetCurrentTermbaseTerms();
                    var singleDisabledIds = settings?.AiSettings?.DisabledAiTermbaseIds ?? new List<long>();
                    var termbaseTerms = singleDisabledIds.Count > 0
                        ? allTbTerms.Where(t => !singleDisabledIds.Contains(t.TermbaseId)).ToList()
                        : allTbTerms;

                    // Resolve custom prompt from settings
                    var customPromptContent = instance.ResolveCustomPromptContent(sourceLang, targetLang);
                    var customSystemPrompt = aiSettings.CustomSystemPrompt;

                    // Log to batch translate panel for visibility
                    var batchControl = _control.Value.BatchTranslateControl;
                    batchControl.AppendLog($"Translating segment: \"{Truncate(sourceText, 60)}\"...");

                    // Run async — single segment, reuse TranslationPrompt + LlmClient
                    var capturedAiSettings = aiSettings;
                    Task.Run(async () =>
                    {
                        try
                        {
                            var systemPrompt = TranslationPrompt.BuildSystemPrompt(
                                sourceLang, targetLang,
                                customPromptContent, termbaseTerms, customSystemPrompt);

                            var client = new LlmClient(
                                capturedAiSettings.SelectedProvider,
                                capturedAiSettings.GetSelectedModel(),
                                apiKey, baseUrl);

                            // For single segment, send it directly (not numbered batch format)
                            var userPrompt = $"Translate the following segment:\n\n{sourceText}";

                            var response = await client.SendPromptAsync(userPrompt, systemPrompt);

                            if (!string.IsNullOrWhiteSpace(response))
                            {
                                // Clean up the response (remove potential numbering or quotes)
                                var translation = response.Trim();
                                if (translation.StartsWith("1. "))
                                    translation = translation.Substring(3).Trim();
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
                                        batchControl.AppendLog(
                                            $"Done: \"{Truncate(translation, 60)}\"");
                                    }
                                    catch (Exception ex)
                                    {
                                        batchControl.AppendLog(
                                            $"Failed to write translation: {ex.Message}", true);
                                    }
                                });
                            }
                            else
                            {
                                instance.SafeInvoke(() =>
                                    batchControl.AppendLog("Empty response from AI provider.", true));
                            }
                        }
                        catch (Exception ex)
                        {
                            instance.SafeInvoke(() =>
                                batchControl.AppendLog(
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

        // ─── Helpers ──────────────────────────────────────────────

        /// <summary>
        /// Extracts TM match information from the active segment's translation origin.
        /// Returns the current match info if it originated from a translation memory.
        /// </summary>
        private List<TmMatch> GetTmMatches()
        {
            var matches = new List<TmMatch>();
            try
            {
                var pair = _activeDocument?.ActiveSegmentPair;
                if (pair == null) return matches;

                var origin = pair.Properties?.TranslationOrigin;
                if (origin == null) return matches;

                // Only include actual TM-originated matches
                var originType = origin.OriginType;
                if (string.IsNullOrEmpty(originType)) return matches;

                // Include TM matches and auto-propagated segments (which originate from TM)
                if (originType == "tm" || originType == "auto-propagated")
                {
                    var sourceText = pair.Source?.ToString();
                    var targetText = pair.Target?.ToString();

                    if (!string.IsNullOrEmpty(sourceText) && !string.IsNullOrEmpty(targetText))
                    {
                        matches.Add(new TmMatch
                        {
                            SourceText = sourceText,
                            TargetText = targetText,
                            MatchPercentage = origin.MatchPercent,
                            TmName = origin.OriginSystem ?? ""
                        });
                    }
                }
            }
            catch (Exception)
            {
                // Segment properties may not be accessible during transitions
            }
            return matches;
        }

        private void AddErrorMessage(string text)
        {
            var msg = new ChatMessage
            {
                Role = ChatRole.Assistant,
                Content = text
            };
            _chatHistory.Add(msg);
            _control.Value.AddMessage(msg);
        }

        /// <summary>
        /// Returns the last N messages for the API context window.
        /// </summary>
        private static List<ChatMessage> BuildMessageWindow(List<ChatMessage> history, int maxMessages)
        {
            if (history.Count <= maxMessages)
                return new List<ChatMessage>(history);

            return history.GetRange(history.Count - maxMessages, maxMessages);
        }

        private string BuildLangPairString()
        {
            var src = GetDocumentSourceLanguage();
            var tgt = GetDocumentTargetLanguage();
            if (!string.IsNullOrEmpty(src) && !string.IsNullOrEmpty(tgt))
                return $"{LanguageUtils.ShortenLanguageName(src)} \u2192 {LanguageUtils.ShortenLanguageName(tgt)}";
            return null;
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

        private static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
                return text;
            return text.Substring(0, maxLength) + "\u2026";
        }

        private void SafeInvoke(Action action)
        {
            var ctrl = _control.Value;
            if (ctrl.InvokeRequired)
                ctrl.BeginInvoke(action);
            else
                action();
        }

        // ─── Document Context Helpers ─────────────────────────────

        /// <summary>
        /// Collects all source segment texts from the active document.
        /// Also determines the 0-based index of the active segment.
        /// Returns (segments, activeIndex) where activeIndex is -1 if not found.
        /// </summary>
        private Tuple<List<string>, int> CollectDocumentContext()
        {
            var segments = new List<string>();
            int activeIndex = -1;

            if (_activeDocument == null)
                return Tuple.Create(segments, activeIndex);

            try
            {
                var activePair = _activeDocument.ActiveSegmentPair;
                string activeSegId = null;
                string activePuId = null;

                if (activePair != null)
                {
                    try
                    {
                        activeSegId = activePair.Properties.Id.Id;
                        var parentPU = _activeDocument.GetParentParagraphUnit(activePair);
                        activePuId = parentPU.Properties.ParagraphUnitId.Id;
                    }
                    catch { }
                }

                int index = 0;
                foreach (var pair in _activeDocument.SegmentPairs)
                {
                    var sourceText = pair.Source?.ToString() ?? "";
                    segments.Add(sourceText);

                    // Match against active segment
                    if (activeIndex < 0 && activePuId != null && activeSegId != null)
                    {
                        try
                        {
                            var parentPU = _activeDocument.GetParentParagraphUnit(pair);
                            var puId = parentPU.Properties.ParagraphUnitId.Id;
                            var segId = pair.Properties.Id.Id;

                            if (puId == activePuId && segId == activeSegId)
                                activeIndex = index;
                        }
                        catch { }
                    }

                    index++;
                }
            }
            catch (Exception)
            {
                // Document may not be accessible during transitions
            }

            return Tuple.Create(segments, activeIndex);
        }

        /// <summary>
        /// Gets surrounding segments (source + target) around the active segment.
        /// Returns a list of [source, target] string arrays.
        /// </summary>
        private List<string[]> GetSurroundingSegments(int count)
        {
            var result = new List<string[]>();
            if (_activeDocument == null || count <= 0)
                return result;

            try
            {
                var activePair = _activeDocument.ActiveSegmentPair;
                if (activePair == null) return result;

                string activeSegId = null;
                string activePuId = null;
                try
                {
                    activeSegId = activePair.Properties.Id.Id;
                    var parentPU = _activeDocument.GetParentParagraphUnit(activePair);
                    activePuId = parentPU.Properties.ParagraphUnitId.Id;
                }
                catch { return result; }

                if (activePuId == null || activeSegId == null)
                    return result;

                // Collect all pairs into a list for random access
                var allPairs = new List<Tuple<string, string>>(); // source, target
                int activeIdx = -1;
                int idx = 0;

                foreach (var pair in _activeDocument.SegmentPairs)
                {
                    var src = pair.Source?.ToString() ?? "";
                    var tgt = pair.Target?.ToString() ?? "";
                    allPairs.Add(Tuple.Create(src, tgt));

                    if (activeIdx < 0)
                    {
                        try
                        {
                            var parentPU = _activeDocument.GetParentParagraphUnit(pair);
                            var puId = parentPU.Properties.ParagraphUnitId.Id;
                            var segId = pair.Properties.Id.Id;
                            if (puId == activePuId && segId == activeSegId)
                                activeIdx = idx;
                        }
                        catch { }
                    }

                    idx++;
                }

                if (activeIdx < 0) return result;

                // Collect 'count' segments before and after
                int start = Math.Max(0, activeIdx - count);
                int end = Math.Min(allPairs.Count - 1, activeIdx + count);

                for (int i = start; i <= end; i++)
                {
                    if (i == activeIdx) continue; // skip the active segment itself
                    result.Add(new[] { allPairs[i].Item1, allPairs[i].Item2 });
                }
            }
            catch (Exception)
            {
                // Document may not be accessible during transitions
            }

            return result;
        }

        /// <summary>
        /// Gets the Trados project name from the active document.
        /// </summary>
        private string GetProjectName()
        {
            try
            {
                var file = _activeDocument?.ActiveFile;
                if (file != null)
                {
                    // Try to get the project name from the source file path
                    var sourceFile = file.SourceFile;
                    if (sourceFile != null)
                    {
                        // The project name is typically available via the file's project reference
                        // Fall back to extracting from the file path
                        var filePath = sourceFile.LocalFilePath;
                        if (!string.IsNullOrEmpty(filePath))
                        {
                            // Trados project files are typically in a folder named after the project
                            var dir = System.IO.Path.GetDirectoryName(filePath);
                            if (!string.IsNullOrEmpty(dir))
                            {
                                var projectDir = System.IO.Path.GetDirectoryName(dir);
                                if (!string.IsNullOrEmpty(projectDir))
                                    return System.IO.Path.GetFileName(projectDir);
                            }
                        }
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// Gets the file name of the active document.
        /// </summary>
        private string GetFileName()
        {
            try
            {
                return _activeDocument?.ActiveFile?.Name;
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// Reloads settings from disk. Called by TermLensEditorViewPart after its
        /// settings dialog saves, so this ViewPart picks up changes made there.
        /// </summary>
        public static void NotifySettingsChanged()
        {
            var instance = _currentInstance;
            if (instance == null) return;
            instance._settings = TermLensSettings.Load();
            instance.UpdateProviderDisplay();
            instance.UpdateBatchProviderDisplay();
        }

        /// <summary>
        /// Called by the launcher tab to activate/focus the AI Assistant panel.
        /// </summary>
        public static void Focus()
        {
            if (_currentInstance != null)
                _control.Value.FocusInput();
        }

        public override void Dispose()
        {
            _chatCts?.Cancel();
            _chatCts?.Dispose();

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

            if (_editorController != null)
            {
                try { _editorController.ActiveDocumentChanged -= OnActiveDocumentChanged; }
                catch { }
            }

            if (_activeDocument != null)
            {
                try { _activeDocument.ActiveSegmentChanged -= OnActiveSegmentChanged; }
                catch { }
                try { _activeDocument.DocumentFilterChanged -= OnDocumentFilterChanged; }
                catch { }
            }

            base.Dispose();
        }
    }
}
