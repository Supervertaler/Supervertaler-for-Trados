using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.Desktop.IntegrationApi.Interfaces;
using Sdl.FileTypeSupport.Framework.BilingualApi;
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

        // Proofreading state
        private BatchProofreader _batchProofreader;
        private CancellationTokenSource _proofreadCts;
        private ProofreadingReport _currentReport;

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
            _control.Value.SaveAsPromptRequested += OnSaveAsPromptRequested;
            _control.Value.StopRequested += OnStopRequested;

            // Wire settings/help buttons
            _control.Value.SettingsRequested += OnSettingsRequested;
            _control.Value.ModelChangeRequested += OnModelChangeRequested;

            // Wire batch translate control events
            var batchControl = _control.Value.BatchTranslateControl;
            batchControl.TranslateRequested += OnBatchTranslateRequested;
            batchControl.ProofreadRequested += OnProofreadRequested;
            batchControl.StopRequested += OnBatchStopRequested;
            batchControl.ScopeChanged += OnBatchScopeChanged;
            batchControl.OpenAiSettingsRequested += OnSettingsRequested;
            batchControl.BatchModeChanged += (s, e) => PopulateBatchPromptDropdown();
            batchControl.GeneratePromptRequested += OnGeneratePromptRequested;

            // Wire reports control events
            var reportsControl = _control.Value.ReportsControl;
            reportsControl.NavigateToSegmentRequested += OnNavigateToSegment;
            reportsControl.ClearResultsRequested += OnClearReports;

            // Initial context update
            UpdateContextDisplay();
            UpdateProviderDisplay();
            UpdateBatchProviderDisplay();
            UpdateBatchSegmentCounts();
            PopulateBatchPromptDropdown();

            // Restore persisted chat history
            LoadChatHistory();
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

        private void OnModelChangeRequested(string providerKey, string modelId)
        {
            SafeInvoke(() =>
            {
                var aiSettings = _settings?.AiSettings;
                if (aiSettings == null) return;

                aiSettings.SetProviderAndModel(providerKey, modelId);
                _settings.Save();

                UpdateProviderDisplay();
                UpdateBatchProviderDisplay();
            });
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

                    if (form.SettingsImported)
                    {
                        // User imported settings from file — reload from disk
                        var fresh = TermLensSettings.Load();
                        _settings.AiSettings = fresh.AiSettings;
                        _settings.TermbasePath = fresh.TermbasePath;
                        _settings.AutoLoadOnStartup = fresh.AutoLoadOnStartup;
                        _settings.PanelFontSize = fresh.PanelFontSize;
                        _settings.TermShortcutStyle = fresh.TermShortcutStyle;
                        _settings.ChordDelayMs = fresh.ChordDelayMs;
                        _settings.DisabledTermbaseIds = fresh.DisabledTermbaseIds;
                        _settings.WriteTermbaseIds = fresh.WriteTermbaseIds;
                        _settings.ProjectTermbaseId = fresh.ProjectTermbaseId;
                        _settings.DisabledMultiTermIds = fresh.DisabledMultiTermIds;
                    }

                    // Always refresh the prompt dropdown — prompt deletions happen
                    // immediately on disk even if the user clicks Cancel afterwards
                    _promptLibrary.Refresh();
                    PopulateBatchPromptDropdown();

                    if (result == System.Windows.Forms.DialogResult.OK || form.SettingsImported)
                    {
                        // Refresh provider displays
                        UpdateProviderDisplay();
                        UpdateBatchProviderDisplay();

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
            var documents = args.Documents;

            if (string.IsNullOrWhiteSpace(messageText)
                && (images == null || images.Count == 0)
                && (documents == null || documents.Count == 0))
                return;

            // Prepend document content to the message text for the AI
            string displayText = args.DisplayText;
            if (documents != null && documents.Count > 0)
            {
                var docParts = new System.Text.StringBuilder();
                foreach (var doc in documents)
                {
                    docParts.AppendLine($"[Attached file: {doc.FileName}]");
                    docParts.AppendLine(doc.ExtractedText);
                    docParts.AppendLine();
                }

                // Build display summary (short) for the chat bubble
                var docNames = new List<string>();
                foreach (var doc in documents)
                    docNames.Add($"{doc.FileName} ({DocumentTextExtractor.FormatFileSize(doc.FileSize)})");

                var displaySummary = string.Join(", ", docNames);
                var userText = messageText ?? "";

                // Full text sent to AI: document content + user's message
                messageText = docParts.ToString() + userText;

                // Display text: show short summary instead of full extracted content
                if (string.IsNullOrEmpty(displayText))
                {
                    displayText = string.IsNullOrWhiteSpace(userText)
                        ? $"\U0001F4CE {displaySummary}"
                        : $"\U0001F4CE {displaySummary}\n\n{userText}";
                }
            }

            // 1. Add user message to history and display
            // ShowAsStatus = true means the message was system-initiated (e.g. Generate Prompt)
            // and should display as an assistant-styled bubble, even though it's sent as a user message
            var userMsg = new ChatMessage
            {
                Role = ChatRole.User,
                Content = messageText ?? "",
                DisplayContent = displayText,  // null = show full Content; set for {{PROJECT}} prompts
                Images = images,
                Documents = documents
            };
            _chatHistory.Add(userMsg);

            // For display, use assistant role if this is a system-initiated message
            var displayMsg = args.ShowAsStatus
                ? new ChatMessage
                {
                    Role = ChatRole.Assistant,
                    Content = messageText ?? "",
                    DisplayContent = displayText,
                    Images = images,
                    Documents = documents
                }
                : userMsg;
            _control.Value.AddMessage(displayMsg);
            SaveChatHistory();

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
                tmMatches = DocumentContextHelper.GetTmMatches(_activeDocument);

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

            // Surrounding segments — count from settings (default 5)
            var surroundingSegments = GetSurroundingSegments(
                _settings?.AiSettings?.QuickLauncherSurroundingSegments ?? 5);

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
            var capturedMaxTokens = args.MaxTokens ?? 4096;

            // 7. Call LLM async
            Task.Run(async () =>
            {
                try
                {
                    var client = new LlmClient(capturedProvider, capturedModel, capturedKey, capturedBaseUrl);
                    var response = await client.SendChatAsync(
                        capturedMessages, capturedSystemPrompt,
                        maxTokens: capturedMaxTokens, cancellationToken: ct);

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
                        SaveChatHistory();
                    });
                }
                catch (OperationCanceledException)
                {
                    SafeInvoke(() =>
                    {
                        _control.Value.SetThinking(false);
                        // Only show timeout message if the user didn't explicitly cancel
                        // (Stop button sets _chatCts to a new instance before cancelling)
                        if (ct.IsCancellationRequested)
                            AddErrorMessage("The request timed out or was cancelled. Try again, or use a faster model.");
                    });
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
            SaveChatHistory();
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

        // ─── Generate Prompt ──────────────────────────────────────────

        private void OnGeneratePromptRequested(object sender, EventArgs e)
        {
            SafeInvoke(() =>
            {
                if (_activeDocument == null)
                {
                    AddErrorMessage("No document open. Open a document in Trados first.");
                    return;
                }

                var aiSettings = _settings?.AiSettings;
                if (aiSettings == null)
                {
                    AddErrorMessage("AI settings not configured. Open Settings \u2192 AI Settings to configure a provider.");
                    return;
                }

                // Resolve provider/API key
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

                // Gather language pair
                var sourceLang = GetDocumentSourceLanguage();
                var targetLang = GetDocumentTargetLanguage();
                if (string.IsNullOrEmpty(sourceLang) || string.IsNullOrEmpty(targetLang))
                {
                    AddErrorMessage("Cannot determine source/target language from the document.");
                    return;
                }

                // Phase 1: Collect all source segments
                var docCtx = CollectDocumentContext();
                var sourceSegments = docCtx.Item1;
                if (sourceSegments == null || sourceSegments.Count == 0)
                {
                    AddErrorMessage("No segments found in the document.");
                    return;
                }

                // Phase 2: Document analysis (domain, tone)
                var analysis = DocumentAnalyzer.Analyze(sourceSegments);

                // Phase 3: Gather termbase terms (filtered by AI-disabled list)
                var allTerms = TermLensEditorViewPart.GetCurrentTermbaseTerms();
                var disabledIds = aiSettings.DisabledAiTermbaseIds ?? new List<long>();
                var termbaseTerms = disabledIds.Count > 0
                    ? allTerms.Where(t => !disabledIds.Contains(t.TermbaseId)).ToList()
                    : allTerms;

                // Phase 3b: Filter terms to only those relevant to the document
                var totalTermCount = termbaseTerms.Count;
                termbaseTerms = PromptGenerator.FilterRelevantTerms(termbaseTerms, sourceSegments);

                // Phase 4: Gather TM reference pairs from translated segments
                var tmPairs = CollectTmReferencePairs();

                // Phase 5: Build meta-prompt
                var ctx = new PromptGenerationContext
                {
                    SourceLang = sourceLang,
                    TargetLang = targetLang,
                    DetectedDomain = analysis.PrimaryDomain,
                    AnalysisSummary = analysis.ToSummary(),
                    SegmentCount = sourceSegments.Count,
                    SourceSegments = sourceSegments,
                    TermbaseTerms = termbaseTerms,
                    TotalTermCount = totalTermCount,
                    TmPairs = tmPairs
                };

                var metaPrompt = PromptGenerator.BuildMetaPrompt(ctx);
                var displayText = PromptGenerator.BuildDisplayMessage(ctx);

                // Phase 6: Send via chat (switches to AI Assistant panel)
                // Use 32768 tokens for prompt generation — comprehensive prompts with
                // large glossaries and TM pairs can exceed 16K tokens.
                // showAsStatus: true → display as assistant-styled (gray) bubble since the
                // user clicked a button, not typed this message themselves
                _control.Value.SubmitMessage(metaPrompt, displayText, maxTokens: 32768,
                    showAsStatus: true);
            });
        }

        /// <summary>
        /// Collects source/target pairs from human-confirmed segments to use as
        /// TM reference pairs for the prompt generator. Only includes segments that
        /// are Translated, ApprovedTranslation, or ApprovedSignOff — i.e., segments
        /// a translator has explicitly confirmed. Unconfirmed AI-generated translations
        /// are excluded to avoid feeding unverified output back as "correct" references.
        /// Samples up to 50 diverse pairs, spread evenly across the document.
        /// </summary>
        private List<TmMatch> CollectTmReferencePairs()
        {
            var pairs = new List<TmMatch>();
            if (_activeDocument == null) return pairs;

            try
            {
                // First pass: collect all confirmed translated segments
                var candidates = new List<TmMatch>();
                foreach (var pair in _activeDocument.SegmentPairs)
                {
                    var sourceText = pair.Source?.ToString() ?? "";
                    var targetText = pair.Target?.ToString() ?? "";

                    // Only include segments that have a non-empty translation
                    if (string.IsNullOrWhiteSpace(sourceText) || string.IsNullOrWhiteSpace(targetText))
                        continue;

                    // Skip very short segments (headers, numbers)
                    if (sourceText.Length < 20) continue;

                    // Only include human-confirmed segments — not unconfirmed AI output
                    var confirmLevel = pair.Properties?.ConfirmationLevel
                        ?? Sdl.Core.Globalization.ConfirmationLevel.Unspecified;
                    if (confirmLevel < Sdl.Core.Globalization.ConfirmationLevel.Translated)
                        continue;

                    candidates.Add(new TmMatch
                    {
                        SourceText = sourceText,
                        TargetText = targetText,
                        MatchPercentage = 100
                    });
                }

                // Second pass: sample evenly across the document for diversity
                if (candidates.Count <= 50)
                {
                    pairs = candidates;
                }
                else
                {
                    var step = (double)candidates.Count / 50;
                    for (int i = 0; i < 50; i++)
                    {
                        var idx = (int)(i * step);
                        if (idx < candidates.Count)
                            pairs.Add(candidates[idx]);
                    }
                }
            }
            catch (Exception)
            {
                // Document may not be accessible
            }

            return pairs;
        }

        private void OnSaveAsPromptRequested(object sender, string promptContent)
        {
            SafeInvoke(() =>
            {
                if (string.IsNullOrWhiteSpace(promptContent))
                    return;

                // Try to extract the prompt from delimiters (in case the full AI response is passed)
                var extracted = PromptGenerator.ParseGeneratedPrompt(promptContent);
                var content = extracted ?? promptContent;

                // Default name = project name, with version number if it already exists
                var defaultName = GetProjectName() ?? "Custom Translation Prompt";
                var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var allPrompts = _promptLibrary?.GetAllPrompts();
                if (allPrompts != null)
                    foreach (var p in allPrompts)
                        existingNames.Add(p.Name);

                if (existingNames.Contains(defaultName))
                {
                    int version = 2;
                    while (existingNames.Contains(defaultName + " v" + version))
                        version++;
                    defaultName = defaultName + " v" + version;
                }

                // Ask user for a name
                using (var dlg = new SavePromptDialog(defaultName))
                {
                    if (dlg.ShowDialog(_control.Value.FindForm()) != DialogResult.OK)
                        return;

                    var name = dlg.PromptName;
                    if (string.IsNullOrWhiteSpace(name))
                        return;

                    var template = new PromptTemplate
                    {
                        Name = name,
                        Domain = "Translate",
                        Content = content,
                        Description = "Generated by Analyse Project & Generate Prompt"
                    };

                    _promptLibrary.SavePrompt(template);
                    PopulateBatchPromptDropdown();

                    // Confirmation in chat
                    var confirmMsg = new ChatMessage
                    {
                        Role = ChatRole.Assistant,
                        Content = $"Prompt saved as **\"{name}\"** in the Translate category. " +
                                  "You can select it from the Prompt dropdown on the Batch Operations tab."
                    };
                    _chatHistory.Add(confirmMsg);
                    _control.Value.AddMessage(confirmMsg);
                    SaveChatHistory();
                }
            });
        }

        // ─── Prompt Library ─────────────────────────────────────────

        private void PopulateBatchPromptDropdown()
        {
            SafeInvoke(() =>
            {
                _promptLibrary?.Refresh();
                var prompts = _promptLibrary?.GetAllPrompts();
                var selectedPath = _settings?.AiSettings?.SelectedPromptPath ?? "";
                var mode = _control.Value.BatchTranslateControl.CurrentMode;
                var categoryFilter = mode == BatchMode.Proofread ? "Proofread" : "Translate";
                _control.Value.BatchTranslateControl.SetPrompts(prompts, selectedPath, categoryFilter);
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
            _proofreadCts?.Cancel();
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

                    // Tagged segments: use ProcessSegmentPair to modify via the document
                    // model directly. This avoids the editor buffer issue where direct
                    // manipulation of ActiveSegmentPair.Target gets lost because the
                    // editor has its own buffer.
                    if (e.HasTags && e.TagMap != null && e.TagMap.Count > 0)
                    {
                        var pair = e.SegmentPairRef as ISegmentPair;
                        if (pair == null) return;

                        _activeDocument.ProcessSegmentPair(pair, "Supervertaler",
                            (sp, cancel) =>
                            {
                                bool reconstructed = SegmentTagHandler.ReconstructTarget(
                                    sp.Target, sp.Source, e.Translation, e.TagMap);

                                if (!reconstructed)
                                {
                                    // Fall back to plain text (strip placeholders)
                                    var plainTranslation = SegmentTagHandler.StripTagPlaceholders(e.Translation);
                                    var textTemplate = SegmentTagHandler.FindFirstText(sp.Source);
                                    if (textTemplate != null && !string.IsNullOrEmpty(plainTranslation))
                                    {
                                        sp.Target.Clear();
                                        var textClone = (IText)textTemplate.Clone();
                                        textClone.Properties.Text = plainTranslation;
                                        sp.Target.Add(textClone);
                                    }
                                }
                            });
                        return;
                    }

                    // Non-tagged segments: navigate + editor Replace (proven approach)
                    var ids = e.SegmentPairRef as string[];
                    if (ids != null && ids.Length >= 2)
                    {
                        _activeDocument.SetActiveSegmentPair(ids[0], ids[1], true);
                        _activeDocument.Selection.Target.Replace(e.Translation, "Supervertaler");
                    }
                    else
                    {
                        // SegmentPairRef is an ISegmentPair (all segments store pairs now)
                        var pair = e.SegmentPairRef as ISegmentPair;
                        if (pair == null) return;

                        _activeDocument.ProcessSegmentPair(pair, "Supervertaler",
                            (sp, cancel) =>
                            {
                                var textTemplate = SegmentTagHandler.FindFirstText(sp.Source);
                                if (textTemplate != null && !string.IsNullOrEmpty(e.Translation))
                                {
                                    sp.Target.Clear();
                                    var textClone = (IText)textTemplate.Clone();
                                    textClone.Properties.Text = e.Translation;
                                    sp.Target.Add(textClone);
                                }
                            });
                    }
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

        // ─── Proofreading ─────────────────────────────────────────

        private void OnProofreadRequested(object sender, EventArgs e)
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

                // Resolve API key (same pattern as batch translate)
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

                // Get language pair
                var sourceLang = GetDocumentSourceLanguage();
                var targetLang = GetDocumentTargetLanguage();

                if (string.IsNullOrEmpty(sourceLang) || string.IsNullOrEmpty(targetLang))
                {
                    batchControl.AppendLog("Cannot determine source/target language from document.", true);
                    return;
                }

                // Collect segments based on proofread scope
                var proofScope = batchControl.GetSelectedProofreadScope();
                var segments = CollectProofreadSegments(proofScope);

                if (segments.Count == 0)
                {
                    batchControl.AppendLog("No segments to proofread.", true);
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

                int batchSize = aiSettings.BatchSize > 0 ? aiSettings.BatchSize : 20;

                // Initialize the report
                _currentReport = new ProofreadingReport();

                // Start proofreading
                batchControl.SetRunning(true);
                batchControl.AppendLog(
                    $"Starting proofreading: {segments.Count} segments, provider={provider}, model={model}, " +
                    $"batch size={batchSize}");

                _proofreadCts = new CancellationTokenSource();
                _batchProofreader = new BatchProofreader();

                _batchProofreader.Progress += OnBatchProgress;
                _batchProofreader.SegmentProofread += OnProofreadSegmentResult;
                _batchProofreader.Completed += OnProofreadCompleted;

                var ct = _proofreadCts.Token;

                Task.Run(async () =>
                {
                    try
                    {
                        await _batchProofreader.ProofreadAsync(
                            segments, sourceLang, targetLang,
                            aiSettings, termbaseTerms, batchSize, ct,
                            customPromptContent);
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

        private void OnProofreadSegmentResult(object sender, ProofreadSegmentEventArgs e)
        {
            SafeInvoke(() =>
            {
                if (_currentReport != null && e.Issue != null)
                {
                    _currentReport.Issues.Add(e.Issue);
                }

                var batchControl = _control.Value.BatchTranslateControl;
                if (e.Issue != null)
                {
                    if (e.Issue.IsOk)
                    {
                        batchControl.AppendLog($"\u2713 Seg {e.Issue.SegmentNumber}: OK");
                    }
                    else
                    {
                        var desc = Truncate(e.Issue.IssueDescription, 80);
                        batchControl.AppendLog($"\u26A0 Seg {e.Issue.SegmentNumber}: {desc}");
                    }
                }
            });
        }

        private void OnProofreadCompleted(object sender, ProofreadCompletedEventArgs e)
        {
            SafeInvoke(() =>
            {
                if (_currentReport != null)
                {
                    _currentReport.Duration = e.Elapsed;
                    _currentReport.TotalSegmentsChecked = e.TotalChecked;

                    _control.Value.ReportsControl.SetResults(_currentReport);
                    _control.Value.UpdateReportsBadge(_currentReport.IssueCount);

                    if (_currentReport.IssueCount > 0)
                    {
                        _control.Value.SwitchToReportsTab();
                    }
                }

                _control.Value.BatchTranslateControl.ReportProofreadCompleted(
                    e.TotalChecked, e.IssueCount, e.OkCount,
                    e.Elapsed, e.Cancelled);
            });

            // Clean up
            if (_batchProofreader != null)
            {
                _batchProofreader.Progress -= OnBatchProgress;
                _batchProofreader.SegmentProofread -= OnProofreadSegmentResult;
                _batchProofreader.Completed -= OnProofreadCompleted;
                _batchProofreader = null;
            }

            _proofreadCts?.Dispose();
            _proofreadCts = null;
        }

        private void OnNavigateToSegment(object sender, NavigateToSegmentEventArgs e)
        {
            SafeInvoke(() =>
            {
                if (_activeDocument == null) return;
                if (string.IsNullOrEmpty(e.ParagraphUnitId) || string.IsNullOrEmpty(e.SegmentId))
                    return;

                try
                {
                    _activeDocument.SetActiveSegmentPair(e.ParagraphUnitId, e.SegmentId, true);
                }
                catch (Exception)
                {
                    // Segment may no longer be accessible
                }
            });
        }

        private void OnClearReports(object sender, EventArgs e)
        {
            _currentReport = null;
            _control.Value.ReportsControl.ClearResults();
            _control.Value.UpdateReportsBadge(0);
        }

        /// <summary>
        /// Collects segments for proofreading based on the selected scope.
        /// Unlike batch translate, proofreading only targets segments that have
        /// a translation (non-empty target), filtering by confirmation level.
        /// </summary>
        private List<BatchSegment> CollectProofreadSegments(ProofreadScope scope)
        {
            var segments = new List<BatchSegment>();
            if (_activeDocument == null) return segments;

            try
            {
                // Use filtered or full segment pairs depending on scope
                var useFiltered = scope == ProofreadScope.Filtered
                    || scope == ProofreadScope.FilteredConfirmedOnly;
                var pairs = useFiltered
                    ? _activeDocument.FilteredSegmentPairs
                    : _activeDocument.SegmentPairs;

                // Build a map of (ParagraphUnitId + SegmentId) → per-file segment number.
                // In multi-file projects, segment numbering restarts per file.
                // We detect file boundaries by tracking the IDocumentProperties file association.
                var segmentNumberMap = new Dictionary<string, int>();
                int fileSegIdx = 0;
                Sdl.FileTypeSupport.Framework.BilingualApi.IFileProperties lastFile = null;
                foreach (var allPair in _activeDocument.SegmentPairs)
                {
                    try
                    {
                        var parentPu = _activeDocument.GetParentParagraphUnit(allPair);
                        var sid = allPair.Properties?.Id.Id;

                        // Simple heuristic: if segment ID parses to an int that is <= previous,
                        // we've crossed a file boundary
                        int segIdNum;
                        if (int.TryParse(sid, out segIdNum) && segIdNum <= fileSegIdx && fileSegIdx > 0)
                            fileSegIdx = 0;

                        fileSegIdx++;

                        if (!string.IsNullOrEmpty(sid))
                        {
                            var puId = parentPu?.Properties?.ParagraphUnitId.Id ?? "";
                            segmentNumberMap[puId + "|" + sid] = fileSegIdx;
                        }
                    }
                    catch
                    {
                        fileSegIdx++;
                    }
                }

                int index = 0;
                foreach (var pair in pairs)
                {
                    var targetText = pair.Target?.ToString() ?? "";

                    // Skip segments with empty target — nothing to proofread
                    if (string.IsNullOrWhiteSpace(targetText))
                    {
                        index++;
                        continue;
                    }

                    var sourceText = pair.Source?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(sourceText))
                    {
                        index++;
                        continue;
                    }

                    // Filter by confirmation level based on scope
                    bool include = false;
                    var confirmLevel = pair.Properties?.ConfirmationLevel
                        ?? Sdl.Core.Globalization.ConfirmationLevel.Unspecified;

                    switch (scope)
                    {
                        case ProofreadScope.ConfirmedOnly:
                        case ProofreadScope.FilteredConfirmedOnly:
                            // "Translated only" — segments at exactly Translated status
                            include = confirmLevel == Sdl.Core.Globalization.ConfirmationLevel.Translated;
                            break;
                        case ProofreadScope.TranslatedAndConfirmed:
                            // "Translated + Approved" — Translated, Approved, and Signed-off
                            include = confirmLevel >= Sdl.Core.Globalization.ConfirmationLevel.Translated;
                            break;
                        case ProofreadScope.AllSegments:
                        case ProofreadScope.Filtered:
                            include = true;
                            break;
                    }

                    if (include)
                    {
                        // Get paragraph unit ID and segment ID for navigation
                        string paragraphUnitId = null;
                        string segmentId = null;
                        try
                        {
                            var parentPU = _activeDocument.GetParentParagraphUnit(pair);
                            paragraphUnitId = parentPU.Properties.ParagraphUnitId.Id;
                            segmentId = pair.Properties.Id.Id;
                        }
                        catch { }

                        // Use actual per-file segment number, not filtered/cross-file index
                        int actualSegNum = index + 1;
                        var mapKey = (paragraphUnitId ?? "") + "|" + (segmentId ?? "");
                        if (segmentNumberMap.TryGetValue(mapKey, out var docNum))
                            actualSegNum = docNum;

                        segments.Add(new BatchSegment
                        {
                            Index = actualSegNum - 1, // 0-based for BatchSegment.Index
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
                    var targetText = pair.Target?.ToString() ?? "";

                    // Serialize source with tag placeholders (if segment has inline tags)
                    var sourceSegment = pair.Source;
                    var serialization = SegmentTagHandler.Serialize(sourceSegment);
                    var sourceText = serialization.HasTags
                        ? serialization.SerializedText
                        : (sourceSegment?.ToString() ?? "");

                    if (string.IsNullOrWhiteSpace(SegmentTagHandler.StripTagPlaceholders(sourceText)))
                    {
                        index++;
                        continue;
                    }

                    bool include = !emptyOnly || string.IsNullOrWhiteSpace(targetText);

                    if (include)
                    {
                        // For tagged segments, store the ISegmentPair directly so we
                        // can use ProcessSegmentPair (which handles the edit transaction).
                        // For non-tagged segments, store string[] IDs for SetActiveSegmentPair
                        // + Selection.Target.Replace (the proven fast path).
                        object segRef;
                        if (serialization.HasTags)
                        {
                            segRef = pair;
                        }
                        else
                        {
                            var parentPU = _activeDocument.GetParentParagraphUnit(pair);
                            var paragraphUnitId = parentPU.Properties.ParagraphUnitId.Id;
                            var segmentId = pair.Properties.Id.Id;
                            segRef = new[] { paragraphUnitId, segmentId };
                        }

                        segments.Add(new BatchSegment
                        {
                            Index = index,
                            SourceText = sourceText,
                            ExistingTarget = targetText,
                            SegmentPairRef = segRef,
                            HasTags = serialization.HasTags,
                            TagMap = serialization.HasTags ? serialization.TagMap : null
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

        // ─── QuickLauncher entry point ────────────────────────────────────

        /// <summary>
        /// Called by QuickLauncherAction when the user selects a QuickLauncher prompt from the
        /// editor right-click menu. The prompt content must already have all variables substituted
        /// before this is called. Submits the message to the AI Assistant chat.
        /// </summary>
        /// <param name="expandedPrompt">Full prompt text sent to the AI.</param>
        /// <param name="displayPrompt">
        /// Optional shorter version shown in the chat bubble. Pass null to show the full prompt.
        /// Use this when the prompt contains a large {{PROJECT}} expansion.
        /// </param>
        public static void RunQuickLauncherPrompt(string expandedPrompt, string displayPrompt = null)
        {
            if (string.IsNullOrWhiteSpace(expandedPrompt)) return;

            var instance = _currentInstance;
            if (instance == null) return;

            instance.SafeInvoke(() =>
            {
                _control.Value.SubmitMessage(expandedPrompt, displayPrompt);
            });
        }

        // ─── Legacy entry point (AiTranslateSegmentAction compatibility) ──

        /// <summary>
        /// Legacy redirect — calls HandleTranslateActiveSegment (Ctrl+T pipeline).
        /// Kept because Trados caches action types and removing the method causes crashes.
        /// </summary>
        public static void HandleAiTranslateSegment()
        {
            HandleTranslateActiveSegment();
        }

        // ─── Original HandleAiTranslateSegment body (replaced) ──────
        // The old standalone translation logic has been replaced by the
        // unified batch pipeline below (HandleTranslateActiveSegment).
        // This dead code block is kept only to preserve line structure
        // for any pending merges.  It will be cleaned up in a future release.

        private static void _LegacyHandleAiTranslateSegment_Removed()
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

                    // Serialize source with tag placeholders if segment has inline tags
                    var sourceSegment = instance._activeDocument.ActiveSegmentPair.Source;
                    var serialization = SegmentTagHandler.Serialize(sourceSegment);
                    var hasTags = serialization.HasTags;
                    var tagMap = hasTags ? serialization.TagMap : null;
                    var sourceText = hasTags
                        ? serialization.SerializedText
                        : (sourceSegment?.ToString() ?? "");

                    if (string.IsNullOrWhiteSpace(SegmentTagHandler.StripTagPlaceholders(sourceText)))
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

                                // Capture tag state for use in UI thread
                                var capturedHasTags = hasTags;
                                var capturedTagMap = tagMap;

                                instance.SafeInvoke(() =>
                                {
                                    try
                                    {
                                        // If source had tags, try to reconstruct with proper tags
                                        if (capturedHasTags && capturedTagMap != null &&
                                            capturedTagMap.Count > 0)
                                        {
                                            var pair = instance._activeDocument.ActiveSegmentPair;
                                            if (pair != null)
                                            {
                                                bool reconstructed = SegmentTagHandler.ReconstructTarget(
                                                    pair.Target, pair.Source,
                                                    translation, capturedTagMap);

                                                if (reconstructed)
                                                {
                                                    batchControl.AppendLog(
                                                        $"Done (with tags): \"{Truncate(SegmentTagHandler.StripTagPlaceholders(translation), 60)}\"");
                                                    return;
                                                }
                                            }

                                            // Reconstruction failed — strip placeholders, use plain text
                                            translation = SegmentTagHandler.StripTagPlaceholders(translation);
                                        }

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

        // ─── Ctrl+T: Translate Active Segment via Batch Pipeline ──

        /// <summary>
        /// Translates the active segment using the batch translate pipeline
        /// (same provider, prompt, and termbase settings as the Batch Translate tab).
        /// Called by TranslateActiveSegmentAction (Ctrl+T).
        /// </summary>
        public static void HandleTranslateActiveSegment()
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

                    // Don't start if a batch is already running
                    if (instance._batchTranslator != null)
                    {
                        _control.Value.BatchTranslateControl.AppendLog(
                            "A batch translation is already running.", true);
                        return;
                    }

                    var settings = instance._settings;
                    var aiSettings = settings?.AiSettings;
                    if (aiSettings == null)
                    {
                        MessageBox.Show(
                            "AI settings not configured.\n\nOpen Settings \u2192 AI Settings to configure a provider.",
                            "Supervertaler",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    // Resolve provider (same logic as batch translate)
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
                            _control.Value.BatchTranslateControl.AppendLog(
                                "No custom OpenAI profile configured.", true);
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
                        _control.Value.BatchTranslateControl.AppendLog(
                            $"No API key configured for {provider}. Open Settings \u2192 AI Settings to add one.", true);
                        return;
                    }

                    var sourceLang = instance.GetDocumentSourceLanguage();
                    var targetLang = instance.GetDocumentTargetLanguage();
                    if (string.IsNullOrEmpty(sourceLang) || string.IsNullOrEmpty(targetLang))
                    {
                        _control.Value.BatchTranslateControl.AppendLog(
                            "Cannot determine source/target language from document.", true);
                        return;
                    }

                    // Collect only the active segment
                    var pair = instance._activeDocument.ActiveSegmentPair;
                    var sourceSegment = pair.Source;
                    var serialization = SegmentTagHandler.Serialize(sourceSegment);
                    var hasTags = serialization.HasTags;
                    var sourceText = hasTags
                        ? serialization.SerializedText
                        : (sourceSegment?.ToString() ?? "");

                    if (string.IsNullOrWhiteSpace(SegmentTagHandler.StripTagPlaceholders(sourceText)))
                    {
                        _control.Value.BatchTranslateControl.AppendLog(
                            "Active segment has no source text.");
                        return;
                    }

                    // Build single-segment batch
                    object segRef;
                    if (hasTags)
                    {
                        segRef = pair;
                    }
                    else
                    {
                        var parentPU = instance._activeDocument.GetParentParagraphUnit(pair);
                        var paragraphUnitId = parentPU.Properties.ParagraphUnitId.Id;
                        var segmentId = pair.Properties.Id.Id;
                        segRef = new[] { paragraphUnitId, segmentId };
                    }

                    var segments = new List<BatchSegment>
                    {
                        new BatchSegment
                        {
                            Index = 0,
                            SourceText = sourceText,
                            ExistingTarget = pair.Target?.ToString() ?? "",
                            SegmentPairRef = segRef,
                            HasTags = hasTags,
                            TagMap = hasTags ? serialization.TagMap : null
                        }
                    };

                    // Get termbase terms (same filtering as batch translate)
                    var allTerms = TermLensEditorViewPart.GetCurrentTermbaseTerms();
                    var disabledIds = aiSettings.DisabledAiTermbaseIds ?? new List<long>();
                    var termbaseTerms = disabledIds.Count > 0
                        ? allTerms.Where(t => !disabledIds.Contains(t.TermbaseId)).ToList()
                        : allTerms;

                    // Resolve custom prompt (from batch translate tab selection)
                    var batchControl = _control.Value.BatchTranslateControl;
                    var selectedPromptPath = batchControl.GetSelectedPromptPath();
                    aiSettings.SelectedPromptPath = selectedPromptPath;

                    var customPromptContent = instance.ResolveCustomPromptContent(sourceLang, targetLang);
                    var customSystemPrompt = aiSettings.CustomSystemPrompt;

                    // Log and run
                    batchControl.AppendLog(
                        $"Ctrl+T: translating \"{Truncate(SegmentTagHandler.StripTagPlaceholders(sourceText), 60)}\"...");

                    instance._batchCts = new CancellationTokenSource();
                    instance._batchTranslator = new BatchTranslator();

                    instance._batchTranslator.SegmentTranslated += instance.OnBatchSegmentTranslated;
                    instance._batchTranslator.Completed += instance.OnBatchCompleted;

                    var ct = instance._batchCts.Token;

                    Task.Run(async () =>
                    {
                        try
                        {
                            await instance._batchTranslator.TranslateAsync(
                                segments, sourceLang, targetLang,
                                aiSettings, termbaseTerms, 1, ct,
                                customPromptContent, customSystemPrompt);
                        }
                        catch (Exception ex)
                        {
                            instance.SafeInvoke(() =>
                            {
                                batchControl.AppendLog($"Ctrl+T failed: {ex.Message}", true);
                                batchControl.SetRunning(false);
                            });
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

        // ─── Chat History Persistence ─────────────────────────────

        private void SaveChatHistory()
        {
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(List<ChatMessage>));
                var path = UserDataPath.ChatHistoryFilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                    serializer.WriteObject(fs, _chatHistory);
            }
            catch { /* ignore save failures */ }
        }

        private void LoadChatHistory()
        {
            try
            {
                var path = UserDataPath.ChatHistoryFilePath;
                if (!File.Exists(path)) return;
                var serializer = new DataContractJsonSerializer(typeof(List<ChatMessage>));
                List<ChatMessage> history;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                    history = (List<ChatMessage>)serializer.ReadObject(fs);
                if (history == null || history.Count == 0) return;
                _chatHistory.AddRange(history);
                foreach (var msg in history)
                    _control.Value.AddMessage(msg);
            }
            catch { /* ignore load failures — start with empty history */ }
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

            // Cancel any running proofreading
            _proofreadCts?.Cancel();
            _proofreadCts?.Dispose();
            _proofreadCts = null;

            if (_batchProofreader != null)
            {
                _batchProofreader.Progress -= OnBatchProgress;
                _batchProofreader.SegmentProofread -= OnProofreadSegmentResult;
                _batchProofreader.Completed -= OnProofreadCompleted;
                _batchProofreader = null;
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
