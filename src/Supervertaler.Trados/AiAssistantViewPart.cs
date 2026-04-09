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

        // Cached language pair — ActiveFile can be null when the AI panel has focus
        private string _cachedSourceLang;
        private string _cachedTargetLang;

        // Chat state
        private readonly List<ChatMessage> _chatHistory = new List<ChatMessage>();
        private CancellationTokenSource _chatCts;
        private bool _userCancelled;

        // Batch translate state
        private BatchTranslator _batchTranslator;
        private CancellationTokenSource _batchCts;

        // Proofreading state
        private BatchProofreader _batchProofreader;
        private CancellationTokenSource _proofreadCts;
        private ProofreadingReport _currentReport;

        // Clipboard Mode state
        private List<BatchSegment> _clipboardSegments;

        // Prompt library
        private PromptLibrary _promptLibrary;

        // Memory-bank inbox watcher
        private FileSystemWatcher _inboxWatcher;

        // Memory-bank reader (lazy: created once, cached for the session).
        // Cached against _kbReaderBankName so that switching the active memory
        // bank at runtime (Step 5 toolbar dropdown) forces a fresh reader on the
        // next LoadKbContextForPrompt() call.
        private MemoryBankReader _kbReader;
        private string _kbReaderBankName;

        /// <summary>
        /// Resolves the on-disk path of the active memory bank for the current
        /// session. Reads <c>AiSettings.ActiveMemoryBankName</c> and falls back
        /// to <see cref="UserDataPath.DefaultMemoryBankName"/> when settings are
        /// not yet loaded or the field is blank.
        /// </summary>
        private string ActiveMemoryBankDir
        {
            get
            {
                var name = _settings?.AiSettings?.ActiveMemoryBankName;
                if (string.IsNullOrWhiteSpace(name))
                    name = UserDataPath.DefaultMemoryBankName;
                return UserDataPath.GetMemoryBankDir(name);
            }
        }

        /// <summary>
        /// Human-friendly label for the active memory bank, used in log/toast
        /// messages so translators can tell which bank they just acted on.
        /// </summary>
        private string ActiveMemoryBankName =>
            string.IsNullOrWhiteSpace(_settings?.AiSettings?.ActiveMemoryBankName)
                ? UserDataPath.DefaultMemoryBankName
                : _settings.AiSettings.ActiveMemoryBankName;

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
                    if (LicenseManager.Instance.HasAssistantAccess)
                        _control.Value.HideUpgradeRequired();
                    else
                        _control.Value.ShowUpgradeRequired();
                }));
            };

            // Load settings and wire up gear button even when unlicensed,
            // so users can open Settings → License to activate.
            _settings = TermLensSettings.Load();
            _promptLibrary = TermLensEditorViewPart.GetPromptLibrary() ?? new PromptLibrary();
            _promptLibrary.EnsureDefaultPrompts();
            _control.Value.SettingsRequested += OnSettingsRequested;

            if (!LicenseManager.Instance.HasAssistantAccess)
            {
                _control.Value.ShowUpgradeRequired();
                return;
            }

            _editorController = SdlTradosStudio.Application.GetController<EditorController>();
            if (_editorController != null)
            {
                _editorController.ActiveDocumentChanged += OnActiveDocumentChanged;

                if (_editorController.ActiveDocument != null)
                {
                    _activeDocument = _editorController.ActiveDocument;
                    _activeDocument.ActiveSegmentChanged += OnActiveSegmentChanged;
                    _activeDocument.DocumentFilterChanged += OnDocumentFilterChanged;
                    GetDocumentSourceLanguage();
                    GetDocumentTargetLanguage();
                }
            }

            // Wire chat control events
            _control.Value.SendRequested += OnSendRequested;
            _control.Value.ClearRequested += OnClearRequested;
            _control.Value.ApplyToTargetRequested += OnApplyToTargetRequested;
            _control.Value.SaveAsPromptRequested += OnSaveAsPromptRequested;
            _control.Value.StopRequested += OnStopRequested;

            // Wire remaining buttons (SettingsRequested already wired above)
            _control.Value.ModelChangeRequested += OnModelChangeRequested;

            // Chat font size: restore persisted size and wire change handler
            _control.Value.SetChatFontSize(_settings.ChatFontSize);
            _control.Value.ChatFontSizeChanged += OnChatFontSizeChanged;

            // Wire batch translate control events
            var batchControl = _control.Value.BatchTranslateControl;
            batchControl.TranslateRequested += OnBatchTranslateRequested;
            batchControl.ProofreadRequested += OnProofreadRequested;
            batchControl.StopRequested += OnBatchStopRequested;
            batchControl.ScopeChanged += OnBatchScopeChanged;
            batchControl.OpenAiSettingsRequested += OnSettingsRequested;
            batchControl.BatchModeChanged += (s, e) => PopulateBatchPromptDropdown();
            batchControl.GeneratePromptRequested += OnGeneratePromptRequested;
            batchControl.CopyToClipboardRequested += OnCopyToClipboardRequested;
            batchControl.PasteFromClipboardRequested += OnPasteFromClipboardRequested;
            batchControl.ModelChangeRequested += OnModelChangeRequested;

            // Wire reports control events
            var reportsControl = _control.Value.ReportsControl;
            reportsControl.NavigateToSegmentRequested += OnNavigateToSegment;
            reportsControl.ClearResultsRequested += OnClearReports;

            // Wire prompt logging
            LlmClient.PromptCompleted += OnPromptCompleted;

            // Wire tag-handler diagnostics to batch translate log
            SegmentTagHandler.DiagnosticMessage = msg =>
                SafeInvoke(() => _control.Value.BatchTranslateControl.AppendLog(msg, true));

            // Wire SuperMemory toolbar events
            _control.Value.ProcessInboxRequested += OnProcessInbox;
            _control.Value.HealthCheckRequested += OnHealthCheck;
            _control.Value.DistillRequested += OnDistill;
            _control.Value.SuperMemoryRefreshRequested += (s, e) => RefreshSuperMemoryInboxCount();
            _control.Value.MemoryBankChanged += OnMemoryBankChanged;
            _control.Value.NewMemoryBankRequested += OnNewMemoryBankRequested;

            // Initial context update
            UpdateContextDisplay();
            UpdateProviderDisplay();
            UpdateBatchProviderDisplay();
            UpdateBatchSegmentCounts();
            PopulateBatchPromptDropdown();
            RefreshMemoryBankDropdown();
            RefreshSuperMemoryInboxCount();
            StartInboxWatcher();

            // Check the already-active bank at start-up too. If the user has
            // a bank (e.g. one created before template bundling shipped, or
            // pre-existing from Step 5i) that is missing canonical template
            // files, offer to restore them now rather than waiting for the
            // user to click Process Inbox and see a confusing error.
            //
            // Deferred via BeginInvoke so the MessageBox does not block
            // Trados Studio's plugin-init message pump — without this, the
            // whole Studio UI would freeze until the user dismisses the
            // prompt at start-up.
            try
            {
                var activeName = ActiveMemoryBankName;
                if (_control.Value.IsHandleCreated)
                {
                    _control.Value.BeginInvoke(new Action(() =>
                    {
                        try { CheckAndOfferTemplateHealing(activeName); } catch { }
                    }));
                }
                else
                {
                    _control.Value.HandleCreated += (s, e) =>
                    {
                        _control.Value.BeginInvoke(new Action(() =>
                        {
                            try { CheckAndOfferTemplateHealing(activeName); } catch { }
                        }));
                    };
                }
            }
            catch { }

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
            _cachedSourceLang = null;
            _cachedTargetLang = null;

            if (_activeDocument != null)
            {
                _activeDocument.ActiveSegmentChanged += OnActiveSegmentChanged;
                _activeDocument.DocumentFilterChanged += OnDocumentFilterChanged;
                // Pre-cache language pair while ActiveFile is likely available
                GetDocumentSourceLanguage();
                GetDocumentTargetLanguage();
                SafeInvoke(UpdateContextDisplay);
                UpdateBatchSegmentCounts();
                PopulateBatchPromptDropdown();
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
            // Refresh language cache while ActiveFile is available
            GetDocumentSourceLanguage();
            GetDocumentTargetLanguage();
            SafeInvoke(UpdateContextDisplay);
        }

        private void OnDocumentFilterChanged(object sender, DocumentFilterEventArgs e)
        {
            UpdateBatchSegmentCounts();
        }

        private void UpdateContextDisplay()
        {
            var sourceText = _activeDocument?.ActiveSegmentPair?.Source?.ToString();
            var targetText = _activeDocument?.ActiveSegmentPair?.Target != null
                ? SegmentTagHandler.GetFinalText(_activeDocument.ActiveSegmentPair.Target)
                : null;
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

        // ─── Chat font size ────────────────────────────────────────

        private void OnChatFontSizeChanged(object sender, EventArgs e)
        {
            _settings.ChatFontSize = _control.Value.ChatFontSize;
            _settings.Save();
        }

        // ─── Settings ───────────────────────────────────────────────

        private void OnSettingsRequested(object sender, EventArgs e)
        {
            SafeInvoke(() =>
            {
                using (var form = new TermLensSettingsForm(_settings, _promptLibrary, defaultTab: 2))
                {
                    form.DistillTermbaseRequested += (ds, de) =>
                        DistillTermbase(de.TermbaseName, de.FormattedTerms);

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
            // Strip Unicode line/paragraph separators (U+2028, U+2029).
            // These are used by InDesign (IDML) as forced line breaks and by some
            // PDF converters as layout artifacts. They're invisible in Trados but
            // cause the AI to introduce spurious line breaks in the translation.
            // The break position is a layout concern, not a linguistic one — it
            // almost never belongs in the same place in the target language.
            if (sourceText != null)
                sourceText = sourceText.Replace("\u2028", " ").Replace("\u2029", " ");
            var targetText = _activeDocument?.ActiveSegmentPair?.Target != null
                ? SegmentTagHandler.GetFinalText(_activeDocument.ActiveSegmentPair.Target)
                : null;
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
            // Load SuperMemory KB context (if vault exists)
            var kbPromptSection = LoadKbContextForPrompt(projectName, sourceLang, targetLang);

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
                IncludeTermMetadata = _settings?.AiSettings?.IncludeTermMetadata != false,
                KbContext = kbPromptSection,
                DemoMode = _settings?.AiSettings?.DemoMode ?? false
            };
            var systemPrompt = ChatPrompt.BuildSystemPrompt(chatCtx);

            // 4. Build message window
            // QuickLauncher prompts are standalone — send only the current message,
            // not the chat history. This prevents accumulated history from inflating
            // token costs (e.g. previous {{PROJECT}} expansions).
            // AutoPrompt (showAsStatus) is also standalone.
            List<ChatMessage> messagesToSend;
            var isStandalone = !string.IsNullOrEmpty(args.PromptName) || args.ShowAsStatus;
            if (isStandalone)
            {
                // Send only the current message — no history
                messagesToSend = new List<ChatMessage> { _chatHistory[_chatHistory.Count - 1] };
            }
            else
            {
                // Regular chat: send last 10 messages for conversational context
                messagesToSend = BuildMessageWindow(_chatHistory, 10);
            }

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
            var capturedPromptName = args.PromptName;
            var capturedFeature = !string.IsNullOrEmpty(args.PromptName)
                ? PromptLogFeature.QuickLauncher
                : PromptLogFeature.Chat;

            // 7. Call LLM async — calculate prompt size for diagnostics
            var promptCharCount = 0;
            foreach (var m in capturedMessages)
                promptCharCount += m.Content?.Length ?? 0;
            promptCharCount += capturedSystemPrompt?.Length ?? 0;

            // Cost guard: warn if estimated cost exceeds $0.50
            var estimatedTokens = promptCharCount / 4; // rough: 1 token ≈ 4 chars
            var estimatedCost = Core.TokenEstimator.EstimateInputCost(capturedModel, estimatedTokens);
            if (estimatedCost > 0.50m)
            {
                var costStr = estimatedCost.ToString("F2");
                var tokenStr = estimatedTokens.ToString("N0");
                var result = System.Windows.Forms.MessageBox.Show(
                    $"This request will send approximately {tokenStr} tokens to {capturedModel}.\n" +
                    $"Estimated input cost: ~${costStr}\n\n" +
                    "Tip: use GPT-5.4 Mini for everyday queries \u2014 it is much cheaper.\n" +
                    "Use GPT-5.4 only for AutoPrompt or complex tasks.\n\n" +
                    "Continue?",
                    "Cost Warning",
                    System.Windows.Forms.MessageBoxButtons.YesNo,
                    System.Windows.Forms.MessageBoxIcon.Warning,
                    System.Windows.Forms.MessageBoxDefaultButton.Button2);

                if (result != System.Windows.Forms.DialogResult.Yes)
                {
                    _control.Value.SetThinking(false);
                    return;
                }
            }

            // Capture tool settings — Claude, OpenAI, Gemini, Grok, Mistral all support tool use
            var useTools = LlmClient.SupportsToolUse(capturedProvider);
            var toolDefsJson = useTools ? TradosTools.GetToolDefinitionsJson(capturedProvider) : null;

            Task.Run(async () =>
            {
                try
                {
                    var client = new LlmClient(capturedProvider, capturedModel, capturedKey, capturedBaseUrl,
                        ollamaTimeoutMinutes: aiSettings.OllamaTimeoutMinutes);

                    string response;
                    if (useTools)
                    {
                        response = await client.SendChatWithToolsAsync(
                            capturedMessages, capturedSystemPrompt,
                            toolDefsJson, TradosTools.ExecuteTool,
                            maxTokens: capturedMaxTokens, cancellationToken: ct,
                            feature: capturedFeature, promptName: capturedPromptName,
                            toolStatusCallback: toolName =>
                                SafeInvoke(() => _control.Value.SetThinking(true, FormatToolStatus(toolName))));
                    }
                    else
                    {
                        response = await client.SendChatAsync(
                            capturedMessages, capturedSystemPrompt,
                            maxTokens: capturedMaxTokens, cancellationToken: ct,
                            feature: capturedFeature, promptName: capturedPromptName);
                    }

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
                catch (OperationCanceledException oce)
                {
                    SafeInvoke(() =>
                    {
                        _control.Value.SetThinking(false);
                        if (_userCancelled)
                        {
                            _userCancelled = false;
                        }
                        else
                        {
                            var tokensEst = promptCharCount / 4;
                            var inner = oce.InnerException?.Message;
                            var detail = inner != null ? $"\n\nInner: {inner}" : "";
                            AddErrorMessage(
                                $"The request timed out.\n\n" +
                                $"Model: {capturedModel}\n" +
                                $"Prompt size: ~{tokensEst:N0} tokens ({promptCharCount:N0} chars)\n" +
                                $"Max output tokens: {capturedMaxTokens}" +
                                detail);
                        }
                    });
                }
                catch (Exception ex)
                {
                    SafeInvoke(() =>
                    {
                        _control.Value.SetThinking(false);
                        var inner = ex.InnerException?.Message;
                        var detail = inner != null ? $"\n\nInner: {inner}" : "";
                        AddErrorMessage($"Error: {ex.Message}{detail}");
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
            _userCancelled = true;
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

        // ─── AutoPrompt ──────────────────────────────────────────────

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
                // Respects the "Include TM matches" toggle in AI Settings
                var includeTm = aiSettings.IncludeTmMatches;
                var tmPairs = includeTm ? CollectTmReferencePairs() : new List<TmMatch>();

                // Phase 4b: SuperMemory KB context (if enabled)
                string kbContext = null;
                if (aiSettings.IncludeSuperMemoryContext && aiSettings.IncludeSuperMemoryInAutoPrompt)
                {
                    var projectName = TermLensEditorViewPart.GetCurrentProjectName();
                    kbContext = LoadKbContextForPrompt(projectName, sourceLang, targetLang)?.Trim();
                }

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
                    TmPairs = tmPairs,
                    KbContext = kbContext
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
                    var targetText = pair.Target != null
                        ? SegmentTagHandler.GetFinalText(pair.Target) : "";

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
                        Category = "Translate",
                        Content = content,
                        Description = "Generated by AutoPrompt"
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

                // Use per-project active prompt if set, else global
                var selectedPath = _settings?.AiSettings?.SelectedPromptPath ?? "";
                string activePromptPath = null;
                var projectPath = TermLensEditorViewPart.GetCurrentProjectPath();
                if (!string.IsNullOrEmpty(projectPath))
                {
                    try
                    {
                        var ps = Settings.ProjectSettings.Load(projectPath);
                        if (ps != null && !string.IsNullOrEmpty(ps.ActivePromptPath))
                        {
                            activePromptPath = ps.ActivePromptPath;
                            selectedPath = activePromptPath;
                        }
                    }
                    catch { }
                }

                var mode = _control.Value.BatchTranslateControl.CurrentMode;
                var categoryFilter = mode == BatchMode.Proofread ? "Proofread" : "Translate";
                var projectName = TermLensEditorViewPart.GetCurrentProjectName();
                _control.Value.BatchTranslateControl.SetPrompts(
                    prompts, selectedPath, categoryFilter, projectName, activePromptPath);
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

        // ─── SuperMemory ─────────────────────────────────────────────

        /// <summary>
        /// Loads SuperMemory KB context for the current project/document.
        /// Returns the formatted prompt section, or null if KB is empty/unavailable.
        /// </summary>
        private string LoadKbContextForPrompt(string projectName, string sourceLang, string targetLang)
        {
            try
            {
                // Check if memory-bank context is enabled in settings
                if (_settings?.AiSettings?.IncludeSuperMemoryContext == false)
                    return null;

                // Re-create the reader if the active bank changed (Step 5 dropdown
                // can swap banks without a restart). First run lands here too.
                var bankName = ActiveMemoryBankName;
                if (_kbReader == null || !string.Equals(_kbReaderBankName, bankName, StringComparison.Ordinal))
                {
                    _kbReader = new MemoryBankReader(ActiveMemoryBankDir);
                    _kbReaderBankName = bankName;
                }

                if (!_kbReader.VaultExists) return null;

                // Detect domain from document content
                string domain = null;
                try
                {
                    if (_activeDocument != null)
                    {
                        var docCtx = CollectDocumentContext();
                        if (docCtx.Item1 != null && docCtx.Item1.Count > 0)
                        {
                            var analysis = DocumentAnalyzer.Analyze(docCtx.Item1);
                            domain = analysis?.PrimaryDomain;
                        }
                    }
                }
                catch { /* domain detection is best-effort */ }

                var ctx = _kbReader.LoadContext(
                    projectName, domain, sourceLang, targetLang,
                    tokenBudget: 4000);

                if (ctx == null) return null;

                return MemoryBankReader.FormatForPrompt(ctx);
            }
            catch
            {
                return null; // KB is optional — never block translation
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  Memory-bank dropdown: populate + live switching
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Rebuilds the Memory Bank dropdown in the SuperMemory toolbar from
        /// the current on-disk bank list, pre-selecting the active bank. Safe
        /// to call repeatedly — the toolbar suppresses its own change event
        /// while the combo is being repopulated, so no accidental switch fires.
        /// </summary>
        private void RefreshMemoryBankDropdown()
        {
            try
            {
                var banks = UserDataPath.ListMemoryBanks();
                var activeName = ActiveMemoryBankName;

                // Make sure the active bank is always visible in the list, even
                // if the on-disk directory hasn't been created yet (e.g. just
                // after a fresh install before the default bank's sub-folders
                // are written). This keeps the combo from looking empty on day one.
                if (!string.IsNullOrWhiteSpace(activeName) &&
                    !banks.Contains(activeName, StringComparer.Ordinal))
                {
                    banks.Insert(0, activeName);
                }

                _control.Value.SuperMemoryToolbar?.SetMemoryBanks(banks, activeName);
            }
            catch
            {
                // Non-critical — the rest of the AI Assistant still works
                // against the active bank via ActiveMemoryBankDir.
            }
        }

        /// <summary>
        /// Handles the user picking a different bank from the toolbar dropdown.
        /// Persists the new active bank, invalidates the cached <see cref="MemoryBankReader"/>,
        /// restarts the inbox watcher against the new bank, and drops a system
        /// banner into the chat so the user sees confirmation of the switch.
        /// </summary>
        private void OnMemoryBankChanged(object sender, MemoryBankChangedEventArgs e)
        {
            if (e == null || string.IsNullOrWhiteSpace(e.BankName)) return;

            var newName = e.BankName;
            var oldName = ActiveMemoryBankName;
            if (string.Equals(newName, oldName, StringComparison.Ordinal))
                return;

            try
            {
                // 1. Persist the new active bank to settings
                if (_settings == null) _settings = TermLensSettings.Load();
                if (_settings.AiSettings == null) _settings.AiSettings = new AiSettings();
                _settings.AiSettings.ActiveMemoryBankName = newName;
                _settings.Save();

                // 2. Invalidate the cached reader — the next LoadKbContextForPrompt
                //    call will lazily recreate it against the new bank directory.
                _kbReader = null;
                _kbReaderBankName = null;

                // 3. Restart the inbox watcher against the new bank
                try { _inboxWatcher?.Dispose(); } catch { }
                _inboxWatcher = null;
                StartInboxWatcher();

                // 4. Refresh the inbox count display (now reading from the new bank)
                RefreshSuperMemoryInboxCount();

                // 5. User-visible confirmation in the chat history
                _control.Value.AddMessage(new ChatMessage
                {
                    Role = ChatRole.Assistant,
                    Content = $"Switched to memory bank **{newName}**. The next chat turn will read from this bank."
                });

                // 6. Heal-on-activation: if the newly active bank is missing
                //    any canonical template files (06_TEMPLATES/compile.md,
                //    06_TEMPLATES/lint.md), offer to restore them from the
                //    bundled defaults. This catches banks created before the
                //    template bundling shipped, as well as any banks where the
                //    user deleted a critical template by mistake.
                CheckAndOfferTemplateHealing(newName);
            }
            catch (Exception ex)
            {
                AddErrorMessage($"Could not switch memory bank: {ex.Message}");
            }
        }

        /// <summary>
        /// Tracks which bank names have already been offered a template-heal
        /// prompt during the current Trados session, so that switching between
        /// two broken banks does not fire the dialog repeatedly. The set is
        /// cleared whenever the plugin is reloaded (i.e. when Trados restarts).
        /// </summary>
        private readonly HashSet<string> _healPromptsShownThisSession =
            new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Inspects the active bank for missing canonical template files
        /// (compile.md, lint.md). If any are missing, shows a one-time
        /// confirmation dialog offering to restore them from the built-in
        /// defaults. Safe to call multiple times: subsequent calls for the
        /// same bank are no-ops once the user has either healed it or
        /// declined healing during the session.
        /// </summary>
        /// <param name="bankName">
        /// Name of the bank being activated. Used as the key for the
        /// per-session "already asked" tracker.
        /// </param>
        private void CheckAndOfferTemplateHealing(string bankName)
        {
            if (string.IsNullOrWhiteSpace(bankName)) return;

            try
            {
                var bankDir = UserDataPath.GetMemoryBankDir(bankName);
                if (!Directory.Exists(bankDir)) return;

                var missing = UserDataPath.GetMissingCanonicalTemplates(bankDir);
                if (missing.Count == 0) return;

                // Only ask once per session per bank.
                if (_healPromptsShownThisSession.Contains(bankName)) return;
                _healPromptsShownThisSession.Add(bankName);

                var parent = _control.Value.FindForm();
                var missingList = string.Join(", ", missing);
                var message =
                    $"Memory bank \"{bankName}\" is missing the following template file(s) in 06_TEMPLATES:\n\n" +
                    $"    {missingList}\n\n" +
                    "These templates are the AI prompts that drive Process Inbox and Health Check. " +
                    "Without them, those features cannot run against this bank.\n\n" +
                    "Restore them from the built-in defaults now?";

                var result = MessageBox.Show(
                    parent,
                    message,
                    "Missing memory bank templates",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button1);

                if (result != DialogResult.Yes)
                {
                    _control.Value.AddMessage(new ChatMessage
                    {
                        Role = ChatRole.Assistant,
                        Content = $"Left memory bank **{bankName}** as-is. Process Inbox and Health Check will not work until the missing template files ({missingList}) are restored — switch away and back, or create a fresh bank, to see the restore prompt again."
                    });
                    return;
                }

                string writeError;
                var count = UserDataPath.WriteMemoryBankTemplates(bankDir, overwrite: false, out writeError);
                if (count > 0)
                {
                    _control.Value.AddMessage(new ChatMessage
                    {
                        Role = ChatRole.Assistant,
                        Content = $"Restored {count} template file(s) to memory bank **{bankName}**. Process Inbox and Health Check will now work against this bank."
                    });
                }
                else if (!string.IsNullOrEmpty(writeError))
                {
                    AddErrorMessage($"Could not restore templates: {writeError}");
                }
                else
                {
                    AddErrorMessage("Could not restore templates: no files were written. The bundled template resources may be missing from this build of the plugin.");
                }
            }
            catch (Exception ex)
            {
                AddErrorMessage($"Could not check memory bank templates: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles the "+ New memory bank…" sentinel selection from the toolbar
        /// dropdown. Prompts the user for a name, sanitises it, creates the
        /// bank on disk (with the full <see cref="UserDataPath.SkeletonFolders"/>
        /// layout), refreshes the dropdown with the new bank visible, and
        /// switches to it by reusing <see cref="OnMemoryBankChanged"/>.
        /// </summary>
        private void OnNewMemoryBankRequested(object sender, EventArgs e)
        {
            var parent = _control.Value.FindForm();
            string rawName;

            // Retry on validation errors (empty / invalid / already exists)
            // until the user either gives us something usable or cancels.
            while (true)
            {
                rawName = PromptForNewBankName(parent);
                if (rawName == null)
                {
                    // User cancelled. Dropdown has already been reverted to
                    // the previously active bank by the toolbar, so there is
                    // nothing else to undo here.
                    return;
                }

                string sanitised;
                string error;
                if (UserDataPath.TryCreateMemoryBank(rawName, out sanitised, out error))
                {
                    try
                    {
                        // Repopulate the dropdown so the new bank is visible,
                        // pre-selected as the active bank. We do NOT fire
                        // MemoryBankChanged from SetMemoryBanks (it is
                        // suppressed), so we drive the switch ourselves via
                        // OnMemoryBankChanged.
                        var banks = UserDataPath.ListMemoryBanks();
                        _control.Value.SuperMemoryToolbar?.SetMemoryBanks(banks, sanitised);

                        OnMemoryBankChanged(this, new MemoryBankChangedEventArgs(sanitised));

                        // Replace the generic "Switched to…" banner that
                        // OnMemoryBankChanged just added with one that makes
                        // the creation explicit, so the user sees confirmation
                        // of what actually happened.
                        _control.Value.AddMessage(new ChatMessage
                        {
                            Role = ChatRole.Assistant,
                            Content = $"Created memory bank **{sanitised}** with the standard folder layout (00_INBOX, 01_CLIENTS, 02_TERMINOLOGY, 03_DOMAINS, 04_STYLE, 05_INDICES, 06_TEMPLATES) and switched to it."
                        });
                    }
                    catch (Exception ex)
                    {
                        AddErrorMessage($"Bank created but could not be activated: {ex.Message}");
                    }
                    return;
                }

                // Creation failed — tell the user why and loop back to the
                // prompt so they can adjust the name without losing their
                // typing flow.
                MessageBox.Show(
                    parent,
                    error ?? "Could not create the memory bank.",
                    "Create memory bank",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// Shows a small modal dialog asking the user to name a new memory
        /// bank, with a live sanitisation preview underneath the text box so
        /// they can see what folder name will actually be created.
        /// </summary>
        /// <returns>The raw user input, or null if the dialog was cancelled.</returns>
        private string PromptForNewBankName(IWin32Window parent)
        {
            using (var dlg = new Form())
            {
                dlg.Text = "Create new memory bank";
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.ClientSize = new System.Drawing.Size(420, 170);
                dlg.ShowInTaskbar = false;

                var lblInstructions = new Label
                {
                    Text = "Short name for the new bank (lowercase letters, digits,\nhyphens or underscores). Example: legal, medical, eu-procurement.",
                    Location = new System.Drawing.Point(12, 12),
                    Size = new System.Drawing.Size(396, 34),
                    AutoSize = false
                };

                var txtName = new TextBox
                {
                    Location = new System.Drawing.Point(12, 54),
                    Size = new System.Drawing.Size(396, 22),
                };

                var lblPreview = new Label
                {
                    Text = "Folder name: —",
                    Location = new System.Drawing.Point(12, 82),
                    Size = new System.Drawing.Size(396, 18),
                    ForeColor = System.Drawing.Color.FromArgb(120, 120, 120)
                };

                var btnOk = new Button
                {
                    Text = "Create",
                    DialogResult = DialogResult.OK,
                    Location = new System.Drawing.Point(232, 125),
                    Size = new System.Drawing.Size(85, 28),
                    Enabled = false
                };

                var btnCancel = new Button
                {
                    Text = "Cancel",
                    DialogResult = DialogResult.Cancel,
                    Location = new System.Drawing.Point(323, 125),
                    Size = new System.Drawing.Size(85, 28)
                };

                txtName.TextChanged += (s, e) =>
                {
                    var safe = UserDataPath.SanitizeBankName(txtName.Text);
                    if (string.IsNullOrEmpty(safe))
                    {
                        lblPreview.Text = "Folder name: —";
                        lblPreview.ForeColor = System.Drawing.Color.FromArgb(120, 120, 120);
                        btnOk.Enabled = false;
                    }
                    else
                    {
                        lblPreview.Text = "Folder name: " + safe;
                        lblPreview.ForeColor = System.Drawing.Color.FromArgb(30, 90, 158);
                        btnOk.Enabled = true;
                    }
                };

                dlg.Controls.Add(lblInstructions);
                dlg.Controls.Add(txtName);
                dlg.Controls.Add(lblPreview);
                dlg.Controls.Add(btnOk);
                dlg.Controls.Add(btnCancel);
                dlg.AcceptButton = btnOk;
                dlg.CancelButton = btnCancel;

                var result = parent != null ? dlg.ShowDialog(parent) : dlg.ShowDialog();
                if (result != DialogResult.OK) return null;
                return txtName.Text;
            }
        }

        private void RefreshSuperMemoryInboxCount()
        {
            try
            {
                var inboxDir = Path.Combine(ActiveMemoryBankDir, "00_INBOX");
                if (!Directory.Exists(inboxDir))
                {
                    _control.Value.UpdateInboxCount(0);
                    return;
                }

                // Count every file in the inbox – not just .md – so the
                // Process Inbox button lights up whenever the user has
                // dropped anything in. Process Inbox itself handles only
                // Markdown; it shows a routing message for TMX/DOCX/PDF/etc.
                // pointing the user at Distill. See OnProcessInbox for the
                // actual per-file-type logic.
                //
                // .md files with "compiled: true" in their frontmatter are
                // excluded (they have already been processed into structured
                // articles and are now just archived inbox receipts).
                var files = Directory.GetFiles(inboxDir, "*", SearchOption.TopDirectoryOnly);
                int count = 0;
                foreach (var f in files)
                {
                    try
                    {
                        var ext = Path.GetExtension(f);
                        if (string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase))
                        {
                            // Only count uncompiled .md files.
                            var head = ReadFileHead(f, 500);
                            if (head.IndexOf("compiled: true", StringComparison.OrdinalIgnoreCase) < 0)
                                count++;
                        }
                        else
                        {
                            // Non-.md files are always counted – they indicate
                            // material the user wants to hand off to Distill
                            // (or a Markdown file they forgot to rename).
                            count++;
                        }
                    }
                    catch { count++; } // if can't stat the file, count it
                }
                _control.Value.UpdateInboxCount(count);
            }
            catch
            {
                _control.Value.UpdateInboxCount(0);
            }
        }

        /// <summary>
        /// Watches the SuperMemory 00_INBOX folder for file changes and auto-refreshes the count.
        /// </summary>
        private void StartInboxWatcher()
        {
            try
            {
                var inboxDir = Path.Combine(ActiveMemoryBankDir, "00_INBOX");
                if (!Directory.Exists(inboxDir)) return;

                // Watch every file type, not just *.md — users drop TMX, PDF,
                // DOCX into the inbox too, and the Process Inbox button needs
                // to reflect that (it will route non-.md files to Distill via
                // a helpful message rather than silently ignoring them).
                _inboxWatcher = new FileSystemWatcher(inboxDir, "*.*")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };

                // Debounce: FileSystemWatcher fires multiple events per file operation.
                // Use a timer to coalesce them into a single refresh.
                var debounceTimer = new System.Windows.Forms.Timer { Interval = 500 };
                debounceTimer.Tick += (s, e) =>
                {
                    debounceTimer.Stop();
                    RefreshSuperMemoryInboxCount();
                };

                EventHandler triggerRefresh = (s, e) =>
                {
                    if (_control.Value.InvokeRequired)
                        _control.Value.BeginInvoke(new Action(() => { debounceTimer.Stop(); debounceTimer.Start(); }));
                    else
                    { debounceTimer.Stop(); debounceTimer.Start(); }
                };

                _inboxWatcher.Created += (s, e) => triggerRefresh(s, e);
                _inboxWatcher.Deleted += (s, e) => triggerRefresh(s, e);
                _inboxWatcher.Renamed += (s, e) => triggerRefresh(s, e);
            }
            catch
            {
                // Non-critical — toolbar still works via manual refresh
            }
        }

        private static string ReadFileHead(string path, int maxChars)
        {
            using (var sr = new StreamReader(path))
            {
                var buf = new char[maxChars];
                int read = sr.Read(buf, 0, maxChars);
                return new string(buf, 0, read);
            }
        }

        private void OnProcessInbox(object sender, EventArgs e)
        {
            var bankDir = ActiveMemoryBankDir;
            var bankName = ActiveMemoryBankName;
            var inboxDir = Path.Combine(bankDir, "00_INBOX");
            if (!Directory.Exists(inboxDir))
            {
                ShowSuperMemoryMessage($"The inbox for memory bank **{bankName}** does not exist yet.\n\n" +
                    $"Create it at:\n`{inboxDir}`\n\nThen drop raw material (client briefs, glossaries, feedback notes) into it.");
                return;
            }

            // Collect everything in the inbox and split by extension. Process
            // Inbox only consumes Markdown; other file types (TMX, DOCX, PDF,
            // XLSX, …) are recognised and surfaced back to the user with a
            // pointer at Distill, which is the feature that actually reads
            // them. This stops the button from silently ignoring files the
            // user deliberately placed in the inbox.
            var allFiles = Directory.GetFiles(inboxDir, "*", SearchOption.TopDirectoryOnly);
            var inboxFiles = new List<Tuple<string, string>>(); // (path, content) — .md only
            var nonMdFiles = new List<string>();                // paths of everything else

            foreach (var f in allFiles)
            {
                var ext = Path.GetExtension(f);
                if (string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var content = File.ReadAllText(f);
                        if (content.IndexOf("compiled: true", StringComparison.OrdinalIgnoreCase) < 0)
                            inboxFiles.Add(Tuple.Create(f, content));
                    }
                    catch { }
                }
                else
                {
                    nonMdFiles.Add(f);
                }
            }

            if (inboxFiles.Count == 0 && nonMdFiles.Count == 0)
            {
                ShowSuperMemoryMessage($"The inbox for memory bank **{bankName}** is empty \u2014 nothing to process.\n\n" +
                    $"Drop raw material (client briefs, glossaries, feedback notes, style guides) into:\n`{inboxDir}`");
                return;
            }

            if (inboxFiles.Count == 0 && nonMdFiles.Count > 0)
            {
                // Nothing Markdown to compile, but there are TMX/DOCX/PDF/etc.
                // files sitting in the inbox. Point the user at Distill.
                var nameList = string.Join(", ", nonMdFiles.Select(Path.GetFileName));
                ShowSuperMemoryMessage(
                    $"The inbox for memory bank **{bankName}** contains {nonMdFiles.Count} file(s), but none of them are Markdown notes:\n\n" +
                    $"    {nameList}\n\n" +
                    "**Process Inbox** reads Markdown briefs, notes, and feedback — it cannot extract knowledge from binary files like TMX, DOCX, PDF, or XLSX.\n\n" +
                    "Click the **Distill** button instead and select these files there. Distill will read each file, extract client, domain, terminology, and style information from it, and write structured Markdown articles straight into the bank.");
                return;
            }

            // We have Markdown to process. If there are also non-md files
            // alongside it, warn about them so the user knows they weren't
            // silently ignored, but continue processing the Markdown.
            if (nonMdFiles.Count > 0)
            {
                var nameList = string.Join(", ", nonMdFiles.Select(Path.GetFileName));
                _control.Value.AddMessage(new ChatMessage
                {
                    Role = ChatRole.Assistant,
                    Content =
                        $"Heads up: the inbox also contains {nonMdFiles.Count} non-Markdown file(s) that Process Inbox cannot handle:\n\n" +
                        $"    {nameList}\n\n" +
                        "I'll process the Markdown files now. For the others, run **Distill** afterwards — it can read TMX, DOCX, PDF, XLSX, and termbases directly."
                });
            }

            // Read the compile template
            var templatePath = Path.Combine(bankDir, "06_TEMPLATES", "compile.md");
            if (!File.Exists(templatePath))
            {
                ShowSuperMemoryMessage("Could not find the compilation template at:\n" +
                    $"`{templatePath}`\n\nMake sure memory bank **{bankName}** contains the `06_TEMPLATES/compile.md` file.");
                return;
            }
            var systemPrompt = File.ReadAllText(templatePath);

            // Build user message with all inbox files
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Process the following {inboxFiles.Count} inbox file(s) into structured knowledge base articles:\n");
            foreach (var item in inboxFiles)
            {
                var fileName = Path.GetFileName(item.Item1);
                sb.AppendLine($"## File: {fileName}");
                sb.AppendLine(item.Item2);
                sb.AppendLine();
            }
            var userMessage = sb.ToString();

            // Show status in chat
            var fileNames = new List<string>();
            foreach (var item in inboxFiles)
                fileNames.Add(Path.GetFileName(item.Item1));
            var displayText = $"\U0001F4E5 **SuperMemory: Process Inbox** \u2014 {inboxFiles.Count} file{(inboxFiles.Count != 1 ? "s" : "")}: {string.Join(", ", fileNames)}";

            RunSuperMemoryAgent(systemPrompt, userMessage, displayText,
                PromptLogFeature.SuperMemory, "SuperMemory: Compile",
                response => PostProcessCompileResponse(response, inboxFiles));
        }

        private void OnHealthCheck(object sender, EventArgs e)
        {
            var vaultDir = ActiveMemoryBankDir;
            var bankName = ActiveMemoryBankName;
            if (!Directory.Exists(vaultDir))
            {
                ShowSuperMemoryMessage($"Memory bank **{bankName}** does not exist yet.\n\n" +
                    $"Expected location:\n`{vaultDir}`");
                return;
            }

            // Read the lint template
            var templatePath = Path.Combine(vaultDir, "06_TEMPLATES", "lint.md");
            if (!File.Exists(templatePath))
            {
                ShowSuperMemoryMessage("Could not find the health check template at:\n" +
                    $"`{templatePath}`\n\nMake sure memory bank **{bankName}** contains the `06_TEMPLATES/lint.md` file.");
                return;
            }
            var systemPrompt = File.ReadAllText(templatePath);

            // Collect vault content (skip .obsidian, .git, 06_TEMPLATES, 00_INBOX/_archive)
            var sb = new System.Text.StringBuilder();
            int fileCount = 0;
            var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".obsidian", ".git", "06_TEMPLATES" };

            foreach (var dir in Directory.GetDirectories(vaultDir))
            {
                var dirName = Path.GetFileName(dir);
                if (skipDirs.Contains(dirName)) continue;
                CollectVaultFiles(dir, vaultDir, sb, ref fileCount, "_archive");
            }
            // Also collect any top-level .md files
            foreach (var f in Directory.GetFiles(vaultDir, "*.md", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var relPath = f.Substring(vaultDir.Length).TrimStart('\\', '/');
                    sb.AppendLine($"## File: {relPath}");
                    sb.AppendLine(File.ReadAllText(f));
                    sb.AppendLine();
                    fileCount++;
                }
                catch { }
            }

            if (fileCount == 0)
            {
                ShowSuperMemoryMessage($"Memory bank **{bankName}** is empty \u2014 nothing to check.\n\n" +
                    "Start by adding content via **Process Inbox** or the **Quick Add** shortcut (Ctrl+Alt+M).");
                return;
            }

            var userMessage = $"Perform a health check on the following knowledge base ({fileCount} files):\n\n{sb}";
            var displayText = $"\U0001F3E5 **SuperMemory: Health Check** \u2014 scanning {fileCount} file{(fileCount != 1 ? "s" : "")}";

            // Cap the message to avoid exceeding token limits (~400K chars ≈ 100K tokens)
            if (userMessage.Length > 400000)
            {
                userMessage = userMessage.Substring(0, 400000) +
                    "\n\n[Truncated — vault too large to scan in one pass. The above is a partial scan.]";
            }

            RunSuperMemoryAgent(systemPrompt, userMessage, displayText,
                PromptLogFeature.SuperMemory, "SuperMemory: Health Check",
                response => PostProcessHealthCheckResponse(response));
        }

        private void CollectVaultFiles(string dir, string vaultRoot,
            System.Text.StringBuilder sb, ref int count, string skipSubDir)
        {
            foreach (var f in Directory.GetFiles(dir, "*.md", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var fileName = Path.GetFileName(f);
                    // Skip example/template files — they're shipped scaffolding, not real content
                    if (fileName.StartsWith("_EXAMPLE_", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var relPath = f.Substring(vaultRoot.Length).TrimStart('\\', '/');
                    sb.AppendLine($"## File: {relPath}");
                    sb.AppendLine(File.ReadAllText(f));
                    sb.AppendLine();
                    count++;
                }
                catch { }
            }
            foreach (var subDir in Directory.GetDirectories(dir))
            {
                var subDirName = Path.GetFileName(subDir);
                if (string.Equals(subDirName, skipSubDir, StringComparison.OrdinalIgnoreCase))
                    continue;
                CollectVaultFiles(subDir, vaultRoot, sb, ref count, skipSubDir);
            }
        }

        private void RunSuperMemoryAgent(string systemPrompt, string userMessage,
            string displayText, PromptLogFeature feature, string promptName,
            Action<string> postProcess)
        {
            // Resolve provider / API key
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

            // Switch to Chat tab and show status message
            _control.Value.AddMessage(new ChatMessage
            {
                Role = ChatRole.Assistant,
                Content = displayText
            });
            _control.Value.SetThinking(true);
            _control.Value.SetSuperMemoryBusy(true);

            // Cancel any pending chat request
            _chatCts?.Cancel();
            _chatCts = new CancellationTokenSource();
            var ct = _chatCts.Token;

            var capturedProvider = provider;
            var capturedModel = model;
            var capturedKey = apiKey;
            var capturedBaseUrl = baseUrl;

            Task.Run(async () =>
            {
                try
                {
                    var client = new LlmClient(capturedProvider, capturedModel, capturedKey,
                        capturedBaseUrl, ollamaTimeoutMinutes: aiSettings.OllamaTimeoutMinutes);
                    var response = await client.SendPromptAsync(
                        userMessage, systemPrompt,
                        maxTokens: 16384, cancellationToken: ct,
                        feature: feature, promptName: promptName);

                    SafeInvoke(() =>
                    {
                        var responseMsg = new ChatMessage
                        {
                            Role = ChatRole.Assistant,
                            Content = response?.Trim() ?? "(No response)"
                        };
                        _chatHistory.Add(responseMsg);
                        _control.Value.AddMessage(responseMsg);
                        _control.Value.SetThinking(false);
                        _control.Value.SetSuperMemoryBusy(false);
                        SaveChatHistory();

                        // Run post-processing (e.g. write files from compile response)
                        try
                        {
                            postProcess?.Invoke(response ?? "");
                        }
                        catch (Exception pex)
                        {
                            AddErrorMessage($"SuperMemory post-processing error: {pex.Message}");
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                    SafeInvoke(() =>
                    {
                        _control.Value.SetThinking(false);
                        _control.Value.SetSuperMemoryBusy(false);
                    });
                }
                catch (Exception ex)
                {
                    SafeInvoke(() =>
                    {
                        _control.Value.SetThinking(false);
                        _control.Value.SetSuperMemoryBusy(false);
                        AddErrorMessage($"SuperMemory error: {ex.Message}");
                    });
                }
            });
        }

        private void PostProcessCompileResponse(string response, List<Tuple<string, string>> inboxFiles)
        {
            if (string.IsNullOrEmpty(response)) return;

            var vaultDir = ActiveMemoryBankDir;
            var writtenFiles = new List<string>();

            // Parse "### FILE: path" markers
            var lines = response.Split('\n');
            string currentPath = null;
            var currentContent = new System.Text.StringBuilder();

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.StartsWith("### FILE:", StringComparison.OrdinalIgnoreCase))
                {
                    // Write previous file if any
                    if (currentPath != null)
                    {
                        WriteVaultFile(vaultDir, currentPath, currentContent.ToString().Trim(), writtenFiles);
                    }
                    currentPath = line.Substring("### FILE:".Length).Trim();
                    currentContent.Clear();
                }
                else
                {
                    currentContent.AppendLine(line);
                }
            }
            // Write last file
            if (currentPath != null)
            {
                WriteVaultFile(vaultDir, currentPath, currentContent.ToString().Trim(), writtenFiles);
            }

            // Archive processed inbox files
            var archiveDir = Path.Combine(vaultDir, "00_INBOX", "_archive");
            int archivedCount = 0;
            foreach (var item in inboxFiles)
            {
                try
                {
                    Directory.CreateDirectory(archiveDir);
                    var destPath = Path.Combine(archiveDir, Path.GetFileName(item.Item1));
                    // Add compiled frontmatter to the archived file
                    var content = item.Item2;
                    if (content.StartsWith("---"))
                    {
                        var endIdx = content.IndexOf("---", 3, StringComparison.Ordinal);
                        if (endIdx > 0)
                        {
                            content = content.Substring(0, endIdx) +
                                $"compiled: true\ncompiled_date: {DateTime.Now:yyyy-MM-dd}\n" +
                                content.Substring(endIdx);
                        }
                    }
                    else
                    {
                        content = $"---\ncompiled: true\ncompiled_date: {DateTime.Now:yyyy-MM-dd}\n---\n\n{content}";
                    }
                    File.WriteAllText(destPath, content);
                    File.Delete(item.Item1);
                    archivedCount++;
                }
                catch { }
            }

            // Show summary
            if (writtenFiles.Count > 0 || archivedCount > 0)
            {
                var summary = new System.Text.StringBuilder();
                summary.AppendLine("**SuperMemory: Processing complete**\n");
                if (writtenFiles.Count > 0)
                {
                    summary.AppendLine($"Wrote {writtenFiles.Count} file{(writtenFiles.Count != 1 ? "s" : "")}:");
                    foreach (var f in writtenFiles)
                        summary.AppendLine($"- `{f}`");
                }
                if (archivedCount > 0)
                    summary.AppendLine($"\nArchived {archivedCount} inbox file{(archivedCount != 1 ? "s" : "")} to `00_INBOX/_archive/`.");

                var summaryMsg = new ChatMessage
                {
                    Role = ChatRole.Assistant,
                    Content = summary.ToString()
                };
                _chatHistory.Add(summaryMsg);
                _control.Value.AddMessage(summaryMsg);
                SaveChatHistory();
            }

            RefreshSuperMemoryInboxCount();
        }

        private void PostProcessHealthCheckResponse(string response)
        {
            if (string.IsNullOrEmpty(response)) return;

            var vaultDir = ActiveMemoryBankDir;
            // Track files with their status (new vs updated)
            var fileResults = new List<Tuple<string, bool>>(); // (path, isNew)

            // Parse "### FILE:" markers
            var lines = response.Split('\n');
            string currentPath = null;
            var currentContent = new System.Text.StringBuilder();

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.StartsWith("### FILE:", StringComparison.OrdinalIgnoreCase))
                {
                    if (currentPath != null)
                        WriteVaultFileTracked(vaultDir, currentPath, currentContent.ToString().Trim(), fileResults);
                    currentPath = line.Substring("### FILE:".Length).Trim();
                    currentContent.Clear();
                }
                else if (currentPath != null)
                {
                    currentContent.AppendLine(line);
                }
            }
            if (currentPath != null)
                WriteVaultFileTracked(vaultDir, currentPath, currentContent.ToString().Trim(), fileResults);

            if (fileResults.Count > 0)
            {
                int newCount = 0, updatedCount = 0;
                foreach (var r in fileResults)
                    if (r.Item2) newCount++; else updatedCount++;

                var summary = new System.Text.StringBuilder();
                summary.AppendLine($"**Health Check: applied {fileResults.Count} change{(fileResults.Count != 1 ? "s" : "")}**\n");

                if (updatedCount > 0)
                {
                    summary.AppendLine($"Updated {updatedCount} file{(updatedCount != 1 ? "s" : "")}:");
                    foreach (var r in fileResults)
                        if (!r.Item2) summary.AppendLine($"- \u270F `{r.Item1}`");
                    summary.AppendLine();
                }
                if (newCount > 0)
                {
                    summary.AppendLine($"Created {newCount} new file{(newCount != 1 ? "s" : "")}:");
                    foreach (var r in fileResults)
                        if (r.Item2) summary.AppendLine($"- \u2728 `{r.Item1}`");
                    summary.AppendLine();
                }

                summary.AppendLine("Scroll up for the full report. Open Obsidian to review the changes.");

                var msg = new ChatMessage { Role = ChatRole.Assistant, Content = summary.ToString() };
                _chatHistory.Add(msg);
                _control.Value.AddMessage(msg);
                SaveChatHistory();
            }
        }

        private void WriteVaultFileTracked(string vaultDir, string relativePath,
            string content, List<Tuple<string, bool>> results)
        {
            try
            {
                relativePath = relativePath.Replace('/', '\\');
                var fullPath = Path.Combine(vaultDir, relativePath);
                bool isNew = !File.Exists(fullPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                File.WriteAllText(fullPath, content);
                results.Add(Tuple.Create(relativePath, isNew));
            }
            catch { }
        }

        private void WriteVaultFile(string vaultDir, string relativePath, string content, List<string> writtenFiles)
        {
            try
            {
                // Normalize path separators
                relativePath = relativePath.Replace('/', '\\');
                var fullPath = Path.Combine(vaultDir, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                File.WriteAllText(fullPath, content);
                writtenFiles.Add(relativePath);
            }
            catch { }
        }

        private void ShowSuperMemoryMessage(string text)
        {
            _chatHistory.Add(new ChatMessage { Role = ChatRole.Assistant, Content = text });
            _control.Value.AddMessage(new ChatMessage { Role = ChatRole.Assistant, Content = text });
            SaveChatHistory();
        }

        // ─── Distill ────────────────────────────────────────────────

        private const string DistillSystemPrompt =
@"You are a translation knowledge extraction specialist. Your job is to analyse source material provided by a professional translator and distil it into structured SuperMemory knowledge base articles.

## Your task

1. **Identify the source type**: translation memory (TMX), termbase/glossary, style guide, client brief, reference document, or mixed.
2. **Extract knowledge** that is valuable for future translation work:
   - **Terminology decisions** with reasoning (why this term, not that one)
   - **Domain knowledge** (industry concepts, product names, regulatory terms)
   - **Client preferences** (tone, register, specific phrasings, forbidden terms)
   - **Style patterns** (sentence structure, punctuation conventions, number formatting)
   - **Translation pitfalls** (false friends, tricky constructions, common mistakes)

## Source-specific guidance

- **TMX / translation memory**: Focus on *patterns* across segments, not individual translations. Look for consistent terminology choices, recurring constructions, client-specific style. Group findings by theme.
- **Termbases / glossaries**: Organise by domain or client. Include definitions, usage notes, and any context that helps a translator pick the right term. Flag ambiguous or overlapping terms.
- **Documents / style guides**: Extract domain knowledge, preferred phrasing, style conventions, and any rules that should be followed.
- **Mixed / other**: Use your best judgement to categorise and extract.

## Output format

Output one or more knowledge base articles using `### FILE: <relative-path>` markers. Each article is a Markdown file with YAML frontmatter.

**IMPORTANT:** Always write articles to the `00_INBOX/` folder. The user will review them before moving them to the correct vault location using Process Inbox.

Use these vault paths:
- `00_INBOX/<filename>.md` — ALL distilled articles go here for review

Each article must have this frontmatter structure:
```
---
title: <descriptive title>
tags: [<relevant tags>]
source: distilled
distilled_from: <original filename(s)>
date: <today's date YYYY-MM-DD>
---
```

## Guidelines

- Keep articles **focused and concise** — one topic per article where possible.
- Use bullet points and tables for terminology lists.
- Include the *reasoning* behind translation choices, not just the choices themselves.
- When in doubt, create separate articles rather than one huge article.
- Write in English (the knowledge base language), but include source/target examples in their original languages.
- If the source material is too large to fully process, prioritise the most valuable and non-obvious knowledge.";

        private void OnDistill(object sender, EventArgs e)
        {
            string[] selectedFiles;
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Select files to distill into knowledge base articles";
                dlg.Filter = "Translation files|*.tmx;*.docx;*.pdf;*.xlsx;*.csv;*.tsv;*.tbx;*.xml;*.txt|All files|*.*";
                dlg.Multiselect = true;
                if (dlg.ShowDialog() != DialogResult.OK || dlg.FileNames.Length == 0)
                    return;
                selectedFiles = dlg.FileNames;
            }

            var vaultDir = ActiveMemoryBankDir;
            var bankName = ActiveMemoryBankName;
            if (!Directory.Exists(vaultDir))
            {
                ShowSuperMemoryMessage($"Memory bank **{bankName}** does not exist yet.\n\n" +
                    $"Expected location:\n`{vaultDir}`");
                return;
            }

            // Extract text from each file
            var fileContents = new List<Tuple<string, string>>(); // (filename, extractedText)
            var errors = new List<string>();

            foreach (var filePath in selectedFiles)
            {
                try
                {
                    var text = DocumentTextExtractor.ExtractText(filePath);
                    fileContents.Add(Tuple.Create(Path.GetFileName(filePath), text));
                }
                catch (Exception ex)
                {
                    errors.Add($"{Path.GetFileName(filePath)}: {ex.Message}");
                }
            }

            if (fileContents.Count == 0)
            {
                ShowSuperMemoryMessage("Could not extract text from any of the selected files.\n\n" +
                    string.Join("\n", errors));
                return;
            }

            // Build user message with all file contents
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Distill the following {fileContents.Count} file(s) into structured knowledge base articles:\n");
            foreach (var item in fileContents)
            {
                sb.AppendLine($"## File: {item.Item1}");
                sb.AppendLine(item.Item2);
                sb.AppendLine();
            }
            var userMessage = sb.ToString();

            // Cap the message to avoid exceeding token limits (~400K chars ~ 100K tokens)
            if (userMessage.Length > 400000)
            {
                userMessage = userMessage.Substring(0, 400000) +
                    "\n\n[Truncated — files too large to process in one pass. The above is a partial extraction.]";
            }

            // Show status in chat
            var fileNames = new List<string>();
            foreach (var item in fileContents)
                fileNames.Add(item.Item1);
            var displayText = $"\u2697 **SuperMemory: Distill** \u2014 {fileContents.Count} file{(fileContents.Count != 1 ? "s" : "")}: {string.Join(", ", fileNames)}";

            if (errors.Count > 0)
            {
                displayText += $"\n\n\u26A0 Could not read {errors.Count} file{(errors.Count != 1 ? "s" : "")}: {string.Join("; ", errors)}";
            }

            RunSuperMemoryAgent(DistillSystemPrompt, userMessage, displayText,
                PromptLogFeature.SuperMemory, "SuperMemory: Distill",
                response => PostProcessDistillResponse(response, fileNames));
        }

        /// <summary>
        /// Distils knowledge from termbase terms into SuperMemory articles.
        /// Called from the termbase context menu via <see cref="DistillTermbase"/>.
        /// </summary>
        public void DistillTermbase(string termbaseName, string formattedTerms)
        {
            var vaultDir = ActiveMemoryBankDir;
            var bankName = ActiveMemoryBankName;
            if (!Directory.Exists(vaultDir))
            {
                ShowSuperMemoryMessage($"Memory bank **{bankName}** does not exist yet.\n\n" +
                    $"Expected location:\n`{vaultDir}`");
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Distill the following termbase into structured knowledge base articles:\n");
            sb.AppendLine($"## File: {termbaseName}");
            sb.AppendLine(formattedTerms);
            var userMessage = sb.ToString();

            if (userMessage.Length > 400000)
            {
                userMessage = userMessage.Substring(0, 400000) +
                    "\n\n[Truncated — termbase too large to process in one pass.]";
            }

            var displayText = $"\u2697 **SuperMemory: Distill Termbase** \u2014 {termbaseName}";
            var fileNames = new List<string> { termbaseName };

            RunSuperMemoryAgent(DistillSystemPrompt, userMessage, displayText,
                PromptLogFeature.SuperMemory, "SuperMemory: Distill",
                response => PostProcessDistillResponse(response, fileNames));
        }

        private void PostProcessDistillResponse(string response, List<string> sourceFileNames)
        {
            if (string.IsNullOrEmpty(response)) return;

            var vaultDir = ActiveMemoryBankDir;
            var writtenFiles = new List<string>();

            // Parse "### FILE: path" markers (same format as compile)
            var lines = response.Split('\n');
            string currentPath = null;
            var currentContent = new System.Text.StringBuilder();

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.StartsWith("### FILE:", StringComparison.OrdinalIgnoreCase))
                {
                    if (currentPath != null)
                        WriteVaultFile(vaultDir, currentPath, currentContent.ToString().Trim(), writtenFiles);
                    currentPath = line.Substring("### FILE:".Length).Trim();
                    currentContent.Clear();
                }
                else
                {
                    currentContent.AppendLine(line);
                }
            }
            if (currentPath != null)
                WriteVaultFile(vaultDir, currentPath, currentContent.ToString().Trim(), writtenFiles);

            // Show summary
            if (writtenFiles.Count > 0)
            {
                var summary = new System.Text.StringBuilder();
                summary.AppendLine("**SuperMemory: Distill complete**\n");
                summary.AppendLine($"Distilled {string.Join(", ", sourceFileNames)} into {writtenFiles.Count} article{(writtenFiles.Count != 1 ? "s" : "")}:");
                foreach (var f in writtenFiles)
                    summary.AppendLine($"- `{f}`");
                summary.AppendLine("\nOpen Obsidian to review the new articles.");

                var msg = new ChatMessage { Role = ChatRole.Assistant, Content = summary.ToString() };
                _chatHistory.Add(msg);
                _control.Value.AddMessage(msg);
                SaveChatHistory();
            }
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

                // Apply segment limit if set
                var maxSeg = batchControl.GetMaxSegments();
                if (maxSeg > 0 && segments.Count > maxSeg)
                {
                    batchControl.AppendLog($"Limit: processing first {maxSeg} of {segments.Count} segments.");
                    segments = segments.GetRange(0, maxSeg);
                }

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

                // Collect document context for AI document type analysis
                List<string> docSegments = null;
                if (aiSettings.IncludeDocumentContext)
                {
                    var docCtx = CollectDocumentContext();
                    docSegments = docCtx.Item1;
                }

                int batchSize = aiSettings.BatchSize > 0 ? aiSettings.BatchSize : 20;

                // Load SuperMemory KB context
                var projectName = GetProjectName();
                var kbContext = LoadKbContextForPrompt(projectName, sourceLang, targetLang);

                // Start the batch translation
                batchControl.SetRunning(true);

                var kbSummary = "";
                if (kbContext != null)
                {
                    try
                    {
                        _kbReader?.RefreshIndex();
                        var kbCtx = _kbReader?.LoadContext(projectName, null, sourceLang, targetLang);
                        if (kbCtx != null)
                            kbSummary = " | " + kbCtx.GetSummary();
                    }
                    catch { }
                }

                batchControl.AppendLog(
                    $"Starting: {segments.Count} segments, provider={provider}, model={model}, " +
                    $"batch size={batchSize}{kbSummary}");

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
                            customPromptContent, customSystemPrompt,
                            docSegments, kbContext);
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

                    // All segments now store ISegmentPair for ProcessSegmentPair.
                    // This avoids the editor buffer issue (Selection.Target.Replace
                    // loses changes) and ensures correct soft return handling for
                    // Excel/Visio segments with literal newlines.
                    var pair = e.SegmentPairRef as ISegmentPair;
                    if (pair == null) return;

                    _activeDocument.ProcessSegmentPair(pair, "Supervertaler",
                        (sp, cancel) =>
                        {
                            // Tagged segments: reconstruct with full tag handling
                            if (e.HasTags && e.TagMap != null && e.TagMap.Count > 0)
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
                                return;
                            }

                            // Non-tagged segments: clone IText from source and set text.
                            // For segments with literal \n (Excel, Visio), the cloned IText
                            // preserves the text properties so Trados renders soft returns
                            // instead of paragraph marks.
                            var textTpl = SegmentTagHandler.FindFirstText(sp.Source);
                            if (textTpl != null && !string.IsNullOrEmpty(e.Translation))
                            {
                                sp.Target.Clear();
                                var textClone = (IText)textTpl.Clone();
                                textClone.Properties.Text = e.Translation;
                                sp.Target.Add(textClone);
                            }
                        });
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

        // ─── Clipboard Mode ──────────────────────────────────────

        private void OnCopyToClipboardRequested(object sender, EventArgs e)
        {
            SafeInvoke(() =>
            {
                var batchControl = _control.Value.BatchTranslateControl;

                if (_activeDocument == null)
                {
                    batchControl.AppendLog("No document open.", true);
                    return;
                }

                var sourceLang = GetDocumentSourceLanguage();
                var targetLang = GetDocumentTargetLanguage();

                if (string.IsNullOrEmpty(sourceLang) || string.IsNullOrEmpty(targetLang))
                {
                    batchControl.AppendLog("Cannot determine source/target language from document.", true);
                    return;
                }

                var aiSettings = _settings?.AiSettings;

                // Collect segments based on mode and scope
                List<BatchSegment> segments;
                if (batchControl.CurrentMode == BatchMode.Proofread)
                {
                    var proofScope = batchControl.GetSelectedProofreadScope();
                    segments = CollectProofreadSegments(proofScope);
                }
                else
                {
                    var scope = batchControl.GetSelectedScope();
                    segments = CollectSegments(scope);
                }

                if (segments.Count == 0)
                {
                    batchControl.AppendLog("No segments to copy.", true);
                    return;
                }

                // Get termbase terms (filtered by AI-disabled list)
                var allTerms = TermLensEditorViewPart.GetCurrentTermbaseTerms();
                var batchDisabledIds = aiSettings?.DisabledAiTermbaseIds ?? new List<long>();
                var termbaseTerms = batchDisabledIds.Count > 0
                    ? allTerms.Where(t => !batchDisabledIds.Contains(t.TermbaseId)).ToList()
                    : allTerms;

                // Persist the prompt dropdown selection before resolving
                var selectedPromptPath = batchControl.GetSelectedPromptPath();
                if (aiSettings != null)
                    aiSettings.SelectedPromptPath = selectedPromptPath;
                _settings.Save();

                // Resolve custom prompt
                var customPromptContent = ResolveCustomPromptContent(sourceLang, targetLang);
                var customSystemPrompt = aiSettings?.CustomSystemPrompt;

                // Collect document context
                List<string> docSegments = null;
                if (aiSettings != null && aiSettings.IncludeDocumentContext)
                {
                    var docCtx = CollectDocumentContext();
                    docSegments = docCtx.Item1;
                }

                var maxDocSegs = aiSettings?.DocumentContextMaxSegments ?? 500;
                var includeTermMeta = aiSettings?.IncludeTermMetadata ?? true;

                // Format for clipboard
                string clipboardText;
                if (batchControl.CurrentMode == BatchMode.Proofread)
                {
                    clipboardText = ClipboardRelay.FormatForProofreading(
                        segments, sourceLang, targetLang,
                        customPromptContent, termbaseTerms, customSystemPrompt,
                        docSegments, maxDocSegs, includeTermMeta);
                }
                else
                {
                    clipboardText = ClipboardRelay.FormatForTranslation(
                        segments, sourceLang, targetLang,
                        customPromptContent, termbaseTerms, customSystemPrompt,
                        docSegments, maxDocSegs, includeTermMeta);
                }

                // Copy to clipboard
                System.Windows.Forms.Clipboard.SetText(clipboardText);

                // Store segments for paste
                _clipboardSegments = segments;

                // Enable paste button
                batchControl.EnablePasteButton(true);

                var mode = batchControl.CurrentMode == BatchMode.Proofread
                    ? "proofreading" : "translation";
                batchControl.AppendLog(
                    $"Copied {segments.Count} segments to clipboard for {mode}. " +
                    $"Paste into your LLM, then copy the response and click \u201cPaste from Clipboard\u201d.");
            });
        }

        private void OnPasteFromClipboardRequested(object sender, EventArgs e)
        {
            SafeInvoke(() =>
            {
                var batchControl = _control.Value.BatchTranslateControl;

                if (_clipboardSegments == null || _clipboardSegments.Count == 0)
                {
                    batchControl.AppendLog("No segments pending \u2013 click \u201cCopy to Clipboard\u201d first.", true);
                    return;
                }

                if (_activeDocument == null)
                {
                    batchControl.AppendLog("No document open.", true);
                    return;
                }

                var text = System.Windows.Forms.Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(text))
                {
                    batchControl.AppendLog("Clipboard is empty \u2013 copy the LLM response first.", true);
                    return;
                }

                var targetLang = GetDocumentTargetLanguage();

                if (batchControl.CurrentMode == BatchMode.Translate)
                {
                    // Parse translations
                    var parsed = ClipboardRelay.ParseTranslationResponse(
                        text, _clipboardSegments.Count, targetLang);

                    if (parsed.Count == 0)
                    {
                        batchControl.AppendLog(
                            "Could not parse any translations from the clipboard. " +
                            "Make sure the LLM response uses the numbered segment format.", true);
                        return;
                    }

                    // Write translations back to Trados
                    int success = 0;
                    int failed = 0;
                    int tagWarnings = 0;

                    foreach (var pt in parsed)
                    {
                        // Map 1-based segment number to 0-based index
                        var segIdx = pt.Number - 1;
                        if (segIdx < 0 || segIdx >= _clipboardSegments.Count)
                        {
                            failed++;
                            continue;
                        }

                        var seg = _clipboardSegments[segIdx];
                        var pair = seg.SegmentPairRef as ISegmentPair;
                        if (pair == null)
                        {
                            failed++;
                            continue;
                        }

                        try
                        {
                            _activeDocument.ProcessSegmentPair(pair, "Supervertaler",
                                (sp, cancel) =>
                                {
                                    if (seg.HasTags && seg.TagMap != null && seg.TagMap.Count > 0)
                                    {
                                        // Validate tags
                                        if (!SegmentTagHandler.ValidateTagsPresent(pt.Translation, seg.TagMap))
                                            tagWarnings++;

                                        bool reconstructed = SegmentTagHandler.ReconstructTarget(
                                            sp.Target, sp.Source, pt.Translation, seg.TagMap);

                                        if (!reconstructed)
                                        {
                                            var plainTranslation = SegmentTagHandler.StripTagPlaceholders(pt.Translation);
                                            var textTemplate = SegmentTagHandler.FindFirstText(sp.Source);
                                            if (textTemplate != null && !string.IsNullOrEmpty(plainTranslation))
                                            {
                                                sp.Target.Clear();
                                                var textClone = (IText)textTemplate.Clone();
                                                textClone.Properties.Text = plainTranslation;
                                                sp.Target.Add(textClone);
                                            }
                                        }
                                    }
                                    else
                                    {
                                        var textTpl = SegmentTagHandler.FindFirstText(sp.Source);
                                        if (textTpl != null && !string.IsNullOrEmpty(pt.Translation))
                                        {
                                            sp.Target.Clear();
                                            var textClone = (IText)textTpl.Clone();
                                            textClone.Properties.Text = pt.Translation;
                                            sp.Target.Add(textClone);
                                        }
                                    }
                                });
                            success++;
                        }
                        catch (Exception ex)
                        {
                            batchControl.AppendLog(
                                $"Failed to write segment {pt.Number}: {ex.Message}", true);
                            failed++;
                        }
                    }

                    // Report results
                    var msg = $"Imported {success} translation{(success != 1 ? "s" : "")}";
                    if (failed > 0) msg += $", {failed} failed";
                    if (tagWarnings > 0) msg += $", {tagWarnings} tag warning{(tagWarnings != 1 ? "s" : "")}";
                    var missing = _clipboardSegments.Count - parsed.Count;
                    if (missing > 0) msg += $", {missing} segment{(missing != 1 ? "s" : "")} not found in response";
                    batchControl.AppendLog(msg + ".");
                }
                else
                {
                    // Proofread mode: log the response for manual review
                    batchControl.AppendLog(
                        "Proofreading response received. Review the results in your LLM.");
                }

                // Clear clipboard segments and disable paste
                _clipboardSegments = null;
                batchControl.EnablePasteButton(false);

                // Update segment counts
                UpdateBatchSegmentCounts();
            });
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

                // Apply segment limit if set
                var maxSeg = batchControl.GetMaxSegments();
                if (maxSeg > 0 && segments.Count > maxSeg)
                {
                    batchControl.AppendLog($"Limit: proofreading first {maxSeg} of {segments.Count} segments.");
                    segments = segments.GetRange(0, maxSeg);
                }

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

                // Collect document context for AI document type analysis
                List<string> docSegments = null;
                if (aiSettings.IncludeDocumentContext)
                {
                    var docCtx = CollectDocumentContext();
                    docSegments = docCtx.Item1;
                }

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
                            customPromptContent,
                            docSegments);
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

        private void OnPromptCompleted(object sender, PromptLogEntry entry)
        {
            if (entry == null) return;
            if (_settings?.AiSettings?.LogPromptsToReports != true) return;

            SafeInvoke(() =>
            {
                // Add card to Reports tab
                _control.Value.ReportsControl.AddPromptLog(entry);

                // Show summary line in chat for Chat/QuickLauncher calls
                if (entry.Feature == PromptLogFeature.Chat ||
                    entry.Feature == PromptLogFeature.QuickLauncher)
                {
                    _control.Value.AddSummaryLine(entry.SummaryLine);
                }
            });
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
                    var targetText = pair.Target != null
                        ? SegmentTagHandler.GetFinalText(pair.Target) : "";

                    // Skip segments with empty target — nothing to proofread
                    if (string.IsNullOrWhiteSpace(targetText))
                    {
                        index++;
                        continue;
                    }

                    var sourceText = pair.Source != null
                        ? SegmentTagHandler.GetFinalText(pair.Source) : "";
                    // Strip Unicode line/paragraph separators — see comment in SendChatMessage
                    sourceText = sourceText.Replace("\u2028", " ").Replace("\u2029", " ");
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
                    var targetText = pair.Target != null
                        ? SegmentTagHandler.GetFinalText(pair.Target) : "";

                    // Serialize source with tag placeholders (if segment has inline tags)
                    var sourceSegment = pair.Source;
                    var serialization = SegmentTagHandler.Serialize(sourceSegment);
                    var sourceText = serialization.HasTags
                        ? serialization.SerializedText
                        : (sourceSegment?.ToString() ?? "");

                    // Strip Unicode line/paragraph separators — see comment in SendChatMessage
                    sourceText = sourceText.Replace("\u2028", " ").Replace("\u2029", " ");

                    if (string.IsNullOrWhiteSpace(SegmentTagHandler.StripTagPlaceholders(sourceText)))
                    {
                        index++;
                        continue;
                    }

                    bool include = !emptyOnly || string.IsNullOrWhiteSpace(targetText);

                    if (include)
                    {
                        // Always store ISegmentPair so ProcessSegmentPair can be used
                        // for all segments. This ensures correct handling of literal
                        // newlines (Excel, Visio) which need IText cloning from source
                        // to produce soft returns instead of paragraph marks.
                        segments.Add(new BatchSegment
                        {
                            Index = index,
                            SourceText = sourceText,
                            ExistingTarget = targetText,
                            SegmentPairRef = pair,
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
                        var targetText = pair.Target != null
                            ? SegmentTagHandler.GetFinalText(pair.Target) : "";
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
        public static void RunQuickLauncherPrompt(string expandedPrompt, string displayPrompt = null, string promptName = null)
        {
            if (string.IsNullOrWhiteSpace(expandedPrompt)) return;

            var instance = _currentInstance;
            if (instance == null) return;

            instance.SafeInvoke(() =>
            {
                _control.Value.SubmitMessage(expandedPrompt, displayPrompt, promptName);
            });
        }

        // ─── Text transforms (local find/replace, no AI call) ─────────

        /// <summary>
        /// Applies a text transform to the active target segment.
        /// Runs the find/replace rules from the prompt's Replacements list
        /// directly on the target text without calling an AI provider.
        /// Uses ProcessContentWithDocument to commit changes through the
        /// Trados document model (same mechanism as batch translate).
        /// Returns a message describing what happened (for status display).
        /// </summary>
        public static string RunTextTransform(Models.PromptTemplate transform)
        {
            if (transform == null || transform.Replacements.Count == 0)
                return "No replacements defined.";

            var instance = _currentInstance;
            if (instance == null)
                return "AI Assistant not initialised.";

            if (instance._activeDocument == null)
                return "No document open.";

            var pair = instance._activeDocument.ActiveSegmentPair;
            if (pair?.Target == null)
                return "No active segment.";

            // Count occurrences first (on plain text) to report accurately
            var plainText = pair.Target.ToString() ?? "";
            if (string.IsNullOrEmpty(plainText))
                return "Target segment is empty.";

            int totalReplacements = 0;
            foreach (var r in transform.Replacements)
            {
                if (string.IsNullOrEmpty(r.Find)) continue;
                int idx = 0;
                while ((idx = plainText.IndexOf(r.Find, idx, StringComparison.Ordinal)) >= 0)
                {
                    totalReplacements++;
                    idx += r.Find.Length;
                }
            }

            if (totalReplacements == 0)
                return "No matches found \u2014 target unchanged.";

            // Apply replacements through ProcessContentWithDocument so the
            // Trados editor commits the changes (direct IText property writes
            // do not persist). This modifies IText nodes in-place inside the
            // document model, preserving all formatting tags.
            string result = null;
            string cleanedText = null;
            instance.SafeInvoke(() =>
            {
                try
                {
                    // Capture replacements for use inside the delegate
                    var replacements = transform.Replacements;

                    instance._activeDocument.ProcessSegmentPair(pair, "Supervertaler",
                        (sp, cancel) =>
                        {
                            foreach (var item in sp.Target.AllSubItems)
                            {
                                var textItem = item as IText;
                                if (textItem == null) continue;

                                var text = textItem.Properties.Text;
                                if (string.IsNullOrEmpty(text)) continue;

                                foreach (var r in replacements)
                                {
                                    if (string.IsNullOrEmpty(r.Find)) continue;
                                    text = text.Replace(r.Find, r.Replace);
                                }

                                // Collapse runs of multiple spaces into a single space
                                // (replacing an invisible char with a space next to an
                                // existing space would otherwise leave double spaces)
                                while (text.Contains("  "))
                                    text = text.Replace("  ", " ");

                                if (text != textItem.Properties.Text)
                                    textItem.Properties.Text = text;
                            }

                            // Capture the cleaned plain text for clipboard
                            cleanedText = sp.Target.ToString();
                        });

                    // Copy the cleaned text to the clipboard
                    if (!string.IsNullOrEmpty(cleanedText))
                    {
                        try { Clipboard.SetText(cleanedText); } catch { /* clipboard may be locked */ }
                    }

                    result = $"\u2713 {totalReplacements} replacement{(totalReplacements == 1 ? "" : "s")} applied (copied to clipboard).";
                }
                catch (Exception ex)
                {
                    result = "Failed to update target: " + ex.Message;
                }
            });

            return result ?? "Transform applied.";
        }

        /// <summary>
        /// Shows the result of a text transform as a brief MessageBox.
        /// </summary>
        public static void ShowTransformResult(string transformName, string result)
        {
            MessageBox.Show(result, "Supervertaler \u2014 " + transformName,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
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

                    // Collect document context for AI document type analysis
                    List<string> singleDocSegments = null;
                    if (aiSettings.IncludeDocumentContext)
                    {
                        var docCtx = instance.CollectDocumentContext();
                        singleDocSegments = docCtx.Item1;
                    }

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
                                customPromptContent, termbaseTerms, customSystemPrompt,
                                singleDocSegments,
                                capturedAiSettings.DocumentContextMaxSegments,
                                capturedAiSettings.IncludeTermMetadata);

                            var client = new LlmClient(
                                capturedAiSettings.SelectedProvider,
                                capturedAiSettings.GetSelectedModel(),
                                apiKey, baseUrl,
                                ollamaTimeoutMinutes: capturedAiSettings.OllamaTimeoutMinutes);

                            // For single segment, send it directly (not numbered batch format)
                            var userPrompt = $"Translate the following segment:\n\n{sourceText}";

                            var response = await client.SendPromptAsync(userPrompt, systemPrompt,
                                feature: PromptLogFeature.Translate);

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

                                        // If translation contains newlines, use ProcessSegmentPair
                                        // with text cloning to preserve soft returns (e.g. Excel, Visio).
                                        // The editor's Replace() API converts \n to paragraph marks.
                                        if (translation.IndexOf('\n') >= 0 || translation.IndexOf('\r') >= 0)
                                        {
                                            var activePair = instance._activeDocument.ActiveSegmentPair;
                                            if (activePair != null)
                                            {
                                                instance._activeDocument.ProcessSegmentPair(activePair, "Supervertaler",
                                                    (sp, cancel) =>
                                                    {
                                                        var textTpl = SegmentTagHandler.FindFirstText(sp.Source);
                                                        if (textTpl != null)
                                                        {
                                                            sp.Target.Clear();
                                                            var textClone = (IText)textTpl.Clone();
                                                            textClone.Properties.Text = translation;
                                                            sp.Target.Add(textClone);
                                                        }
                                                    });
                                                batchControl.AppendLog(
                                                    $"Done: \"{Truncate(translation, 60)}\"");
                                                return;
                                            }
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

                    // Always store ISegmentPair so ProcessSegmentPair can be used
                    // directly (avoids async SetActiveSegmentPair issues and ensures
                    // correct soft return handling for Excel/Visio segments).
                    var segments = new List<BatchSegment>
                    {
                        new BatchSegment
                        {
                            Index = 0,
                            SourceText = sourceText,
                            ExistingTarget = pair.Target != null
                                ? SegmentTagHandler.GetFinalText(pair.Target) : "",
                            SegmentPairRef = pair,
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
                    var targetText = pair.Target != null
                        ? SegmentTagHandler.GetFinalText(pair.Target) : null;

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

        /// <summary>
        /// Maps a tool name to a user-friendly status message shown in the thinking indicator.
        /// </summary>
        private static string FormatToolStatus(string toolName)
        {
            switch (toolName)
            {
                case "studio_list_projects": return "Checking Trados projects\u2026";
                case "studio_get_project": return "Looking up project details\u2026";
                case "studio_get_project_statistics": return "Reading project statistics\u2026";
                case "studio_get_file_status": return "Checking file status\u2026";
                case "studio_list_project_termbases": return "Listing project termbases\u2026";
                case "studio_get_tm_info": return "Reading TM details\u2026";
                case "studio_search_tm": return "Searching translation memory\u2026";
                case "studio_list_tms": return "Listing translation memories\u2026";
                case "studio_list_project_templates": return "Listing project templates\u2026";
                default: return "Querying Trados Studio\u2026";
            }
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
        /// Returns the last N messages for the API context window, constrained by
        /// a character budget (~50K tokens ≈ 200K chars) to prevent runaway costs
        /// from accumulated large prompts (e.g. {{PROJECT}} expansions).
        /// Always includes at least the most recent message.
        /// </summary>
        private static List<ChatMessage> BuildMessageWindow(List<ChatMessage> history, int maxMessages)
        {
            const int maxChars = 200_000; // ~50K tokens

            if (history.Count == 0)
                return new List<ChatMessage>();

            // Start from the most recent message and work backwards
            var result = new List<ChatMessage>();
            var totalChars = 0;
            var startIdx = Math.Max(0, history.Count - maxMessages);

            for (int i = history.Count - 1; i >= startIdx; i--)
            {
                var msgLen = history[i].Content?.Length ?? 0;

                // Always include the most recent message
                if (i == history.Count - 1)
                {
                    result.Insert(0, history[i]);
                    totalChars += msgLen;
                    continue;
                }

                // Stop adding older messages if we'd exceed the budget
                if (totalChars + msgLen > maxChars)
                    break;

                result.Insert(0, history[i]);
                totalChars += msgLen;
            }

            return result;
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
                    {
                        _cachedSourceLang = lang.DisplayName;
                        return _cachedSourceLang;
                    }
                }
            }
            catch (Exception) { }
            return _cachedSourceLang;
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
                    {
                        _cachedTargetLang = lang.DisplayName;
                        return _cachedTargetLang;
                    }
                }
            }
            catch (Exception) { }
            return _cachedTargetLang;
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
                    var tgt = pair.Target != null
                        ? SegmentTagHandler.GetFinalText(pair.Target) : "";
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
            // Pick up any bank-list changes (new bank added via settings dialog,
            // rename, etc.) and re-select the active bank without firing the
            // toolbar's change event.
            instance.RefreshMemoryBankDropdown();
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

            if (_inboxWatcher != null)
            {
                _inboxWatcher.EnableRaisingEvents = false;
                _inboxWatcher.Dispose();
                _inboxWatcher = null;
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
