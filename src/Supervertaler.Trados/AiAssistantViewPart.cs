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
using Sdl.ProjectAutomation.FileBased;
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
        Name = "Supervertaler",
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

        // Cached language pair – ActiveFile can be null when the AI panel has focus
        private string _cachedSourceLang;
        private string _cachedTargetLang;

        // Chat state
        private readonly List<ChatMessage> _chatHistory = new List<ChatMessage>();
        private CancellationTokenSource _chatCts;
        private bool _userCancelled;

        // Batch translate state
        private BatchTranslator _batchTranslator;
        private CancellationTokenSource _batchCts;
        private BatchTranslationBackup _batchBackup;

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
        private bool _fullyInitialized;

        // Localhost HTTP bridge for the Workbench Sidekick Chat. See
        // Core/SupervertalerBridge.cs for protocol details. Started at the end of
        // InitializeFullIfNeeded when the user has Assistant access AND the
        // hidden setting AiSettings.SidekickBridgeEnabled is true.
        private SupervertalerBridge _supervertalerBridge;

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
            BridgeLog.Write("AiAssistantViewPart.Initialize() ENTERED");
            _currentInstance = this;

            // Regression guard for the v4.19.52 silent-data-loss bug. If the
            // DataContractJsonSerializer can't round-trip a default
            // TermLensSettings, Load() will swallow the exception and return
            // fresh defaults – making every saved setting vanish. Logging the
            // failure here surfaces it in bridge.log immediately instead of
            // after users notice their settings have disappeared.
            var selfTestError = TermLensSettings.RunStartupSelfTest();
            if (selfTestError != null)
            {
                BridgeLog.Write("CRITICAL: TermLensSettings.RunStartupSelfTest FAILED: " + selfTestError);
                BridgeLog.Write("CRITICAL: Saved settings will appear empty until this is fixed. Likely cause: duplicate [OnDeserializing]/[OnSerializing]/[OnDeserialized]/[OnSerialized] callback in a [DataContract] type.");
            }
            else
            {
                BridgeLog.Write("TermLensSettings.RunStartupSelfTest passed.");
            }

            var langSelfTestError = LanguageUtils.RunStartupSelfTest();
            if (langSelfTestError != null)
            {
                BridgeLog.Write("CRITICAL: LanguageUtils.RunStartupSelfTest FAILED: " + langSelfTestError);
                BridgeLog.Write("CRITICAL: Termbase language-direction logic is misclassifying inputs. Term lookups, writes, or merges may go to the wrong columns until this is fixed.");
            }
            else
            {
                BridgeLog.Write("LanguageUtils.RunStartupSelfTest passed.");
            }

            // License check – show/hide upgrade overlay based on tier.
            // When the user activates a licence mid-session (after Initialize
            // returned early due to no access), run the deferred full init so
            // event handlers, memory-bank dropdown, inbox watcher, etc. are
            // all wired up without requiring a Trados restart.
            LicenseManager.Instance.LicenseStateChanged += (s, e) =>
            {
                _control.Value.BeginInvoke(new Action(() =>
                {
                    if (LicenseManager.Instance.HasAssistantAccess)
                    {
                        _control.Value.HideUpgradeRequired();
                        InitializeFullIfNeeded();
                    }
                    else
                    {
                        _control.Value.ShowUpgradeRequired();
                    }
                }));
            };

            // Load settings and wire up gear button even when unlicensed,
            // so users can open Settings → License to activate.
            _settings = TermLensSettings.Load();
            _promptLibrary = TermLensEditorViewPart.GetPromptLibrary() ?? new PromptLibrary();
            _promptLibrary.EnsureDefaultPrompts();
            _control.Value.SettingsRequested += OnSettingsRequested;

            // Live-sync the Batch Translate dropdown whenever the user toggles the
            // active prompt in the Prompt Manager, no matter which entry point
            // opened the Settings dialog (AI Assistant gear, termbase gear, etc.).
            // A static event avoids the per-instance wiring that previously missed
            // forms opened from TermLensEditorViewPart.
            Controls.PromptManagerPanel.ActivePromptChangedGlobal -= OnActivePromptChangedGlobal;
            Controls.PromptManagerPanel.ActivePromptChangedGlobal += OnActivePromptChangedGlobal;

            if (!LicenseManager.Instance.HasAssistantAccess)
            {
                BridgeLog.Write($"Initialize: HasAssistantAccess=false (tier={LicenseManager.Instance.CurrentTier}). Bridge will NOT start until license activates.");
                _control.Value.ShowUpgradeRequired();
                return;
            }

            BridgeLog.Write($"Initialize: HasAssistantAccess=true (tier={LicenseManager.Instance.CurrentTier}). Calling InitializeFullIfNeeded.");
            InitializeFullIfNeeded();
        }

        /// <summary>
        /// Performs the full initialisation that requires a valid licence:
        /// wires event handlers, populates the memory-bank dropdown, starts
        /// the inbox watcher, and restores chat history.  Guarded by
        /// <see cref="_fullyInitialized"/> so it runs at most once per
        /// ViewPart lifetime – either from <see cref="Initialize"/> (when
        /// licensed at startup) or from the <c>LicenseStateChanged</c>
        /// handler (when the user activates mid-session).
        /// </summary>
        private void InitializeFullIfNeeded()
        {
            if (_fullyInitialized) return;
            _fullyInitialized = true;

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
            _control.Value.SaveToMemoryBankRequested += OnSaveToMemoryBank;
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
            batchControl.OpenBackupFolderRequested += OnOpenBackupFolderRequested;
            batchControl.CopyToClipboardRequested += OnCopyToClipboardRequested;
            batchControl.PasteFromClipboardRequested += OnPasteFromClipboardRequested;
            batchControl.PreviewPromptRequested += OnPreviewPromptRequested;
            batchControl.ModelChangeRequested += OnModelChangeRequested;

            // Wire reports control events
            var reportsControl = _control.Value.ReportsControl;
            reportsControl.NavigateToSegmentRequested += OnNavigateToSegment;
            reportsControl.ClearResultsRequested += OnClearReports;

            // Wire Import / Export control events (v4.20.7). Export collects
            // segments from the active document and writes them via the
            // Core.Export.* pipeline; import reads the sidecar manifest +
            // round-tripped file and applies diffs back via ProcessSegmentPair.
            var importExportControl = _control.Value.ImportExportControl;
            importExportControl.ExportRequested += OnBilingualExportRequested;
            importExportControl.ImportRequested += OnBilingualImportRequested;
            importExportControl.OpenFileRequested += OnImportExportOpenFile;
            importExportControl.OpenFolderRequested += OnImportExportOpenFolder;
            importExportControl.FileSelectionChanged += (s, e) => UpdateImportExportSegmentCount();

            // Optionally host SuperSearch as a 4th tab in this panel. The
            // SuperSearchController owns the control and all its logic; we just
            // re-parent the shared control into a tab here. The standalone
            // SuperSearchViewPart shows a placeholder when this mode is on.
            if (_settings.SuperSearchInAssistantTab)
            {
                _control.Value.EnsureSuperSearchTab(SuperSearchController.Shared.Control);
            }

            // Wire prompt logging
            LlmClient.PromptCompleted += OnPromptCompleted;

            // Wire tag-handler diagnostics to batch translate log
            SegmentTagHandler.DiagnosticMessage = msg =>
                SafeInvoke(() => _control.Value.BatchTranslateControl.AppendLog(msg, true));

            // Wire SuperMemory toolbar events
            _control.Value.ProcessInboxRequested += OnProcessInbox;
            _control.Value.HealthCheckRequested += OnHealthCheck;
            _control.Value.DistillRequested += OnDistill;
            _control.Value.OverviewRequested += OnOverview;
            _control.Value.AiSummaryRequested += OnAiSummary;
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
            StartSupervertalerBridge();

            // Check the already-active bank at start-up too. If the user has
            // a bank (e.g. one created before template bundling shipped, or
            // pre-existing from Step 5i) that is missing canonical template
            // files, offer to restore them now rather than waiting for the
            // user to click Process Inbox and see a confusing error.
            //
            // Deferred via BeginInvoke so the MessageBox does not block
            // Trados Studio's plugin-init message pump – without this, the
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
            // Strip inline-formatting tags from the source preview – ToString()
            // would emit e.g. `<cf bold=True>SEVT</cf>` which leaks Trados'
            // internal tag syntax into the chat header. SegmentTagHandler
            // .GetFinalText returns just the readable text. Same treatment
            // already applied to the target.
            var sourceText = _activeDocument?.ActiveSegmentPair?.Source != null
                ? SegmentTagHandler.GetFinalText(_activeDocument.ActiveSegmentPair.Source)
                : null;
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

                // Tell TermLensEditorViewPart to reload AiSettings from disk.
                // Otherwise its in-memory settings stay on the OLD provider/
                // model, so opening the Settings dialog from the TermLens
                // panel's gear icon would show stale values that don't match
                // the chat status bar. We use the lightweight NotifyAi-
                // variant rather than the full NotifySettingsChanged so we
                // don't pointlessly reload the termbase – AI provider/model
                // changes don't affect terminology.
                TermLensEditorViewPart.NotifyAiSettingsChanged();
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

                    // Note: live-sync of the active prompt is handled by the static
                    // Controls.PromptManagerPanel.ActivePromptChangedGlobal hook
                    // wired in Initialize, not here – that way it also fires when
                    // the Settings dialog is opened from TermLensEditorViewPart.

                    var parent = _control.Value.FindForm();
                    var result = parent != null
                        ? form.ShowDialog(parent)
                        : form.ShowDialog();

                    if (form.SettingsImported)
                    {
                        // User imported settings from file – reload from disk
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

                    // Always refresh the prompt dropdown – prompt deletions happen
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
            // The break position is a layout concern, not a linguistic one – it
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

            // Surrounding segments – count from settings (default 5)
            var surroundingSegments = GetSurroundingSegments(
                _settings?.AiSettings?.QuickLauncherSurroundingSegments ?? 5);

            // Project metadata
            var projectName = GetProjectName();
            var fileName = GetFileName();

            // 3. Build system prompt with full context
            // Load SuperMemory KB context (if vault exists). Pass the user's
            // message so a term they ask about is force-included even when the
            // document's domain/language wouldn't otherwise rank that note.
            var kbPromptSection = LoadKbContextForPrompt(projectName, sourceLang, targetLang, messageText);

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
            // QuickLauncher prompts are standalone – send only the current message,
            // not the chat history. This prevents accumulated history from inflating
            // token costs (e.g. previous {{PROJECT}} expansions).
            // AutoPrompt (showAsStatus) is also standalone.
            List<ChatMessage> messagesToSend;
            var isStandalone = !string.IsNullOrEmpty(args.PromptName) || args.ShowAsStatus;
            if (isStandalone)
            {
                // Send only the current message – no history
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

            // 7. Call LLM async – calculate prompt size for diagnostics
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
                    "Use GPT-5.5 only for AutoPrompt or complex tasks.\n\n" +
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

            // Capture tool settings – Claude, OpenAI, Gemini, Grok, Mistral all support tool use
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
            // Archive the current session before wiping it, so it can be recovered.
            if (_chatHistory.Count > 0)
                ArchiveChatHistory();

            _chatHistory.Clear();
            _control.Value.ClearMessages();
            SaveChatHistory();
        }

        private void ArchiveChatHistory()
        {
            try
            {
                var archivePath = UserDataPath.ChatArchiveFilePath(DateTime.Now);
                Directory.CreateDirectory(Path.GetDirectoryName(archivePath));
                var serializer = new DataContractJsonSerializer(typeof(List<ChatMessage>));
                using (var fs = new FileStream(archivePath, FileMode.Create, FileAccess.Write))
                    serializer.WriteObject(fs, _chatHistory);
            }
            catch { /* archive is best-effort – never block the clear */ }
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

        // ─── Supervertaler Bridge ─────────────────────────────────────────────
        //
        // The Supervertaler Bridge (Core/SupervertalerBridge.cs) is a localhost-only
        // HTTP listener that lets external Supervertaler clients – primarily
        // the floating Sidekick Chat in Supervertaler Workbench – read the
        // active Trados project context and insert translations back into
        // the editor. The bridge runs in this ViewPart's lifecycle because
        // we already hold the references to _activeDocument, _settings, and
        // the helpers (GetSurroundingSegments, GetProjectName, etc.) the
        // bridge needs to build its context snapshot.

        private void StartSupervertalerBridge()
        {
            try
            {
                BridgeLog.Write("StartSupervertalerBridge() called");

                if (_supervertalerBridge != null)
                {
                    BridgeLog.Write("guard: bridge already non-null – no-op");
                    return;
                }
                if (!LicenseManager.Instance.HasAssistantAccess)
                {
                    BridgeLog.Write("guard: HasAssistantAccess=false – bridge skipped");
                    return;
                }
                if (_settings?.AiSettings?.SidekickBridgeEnabled == false)
                {
                    BridgeLog.Write("guard: AiSettings.SidekickBridgeEnabled=false – bridge skipped");
                    return;
                }
                BridgeLog.Write($"guards passed: tier={LicenseManager.Instance.CurrentTier}, enabled={_settings?.AiSettings?.SidekickBridgeEnabled}");

                _supervertalerBridge = new SupervertalerBridge(
                    getContext: BuildBridgeContextSnapshot,
                    insertText: BridgeInsertTranslation);
                _supervertalerBridge.Start();
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"StartSupervertalerBridge() THREW: {ex.GetType().Name}: {ex.Message}\r\n{ex.StackTrace}");
                _supervertalerBridge = null;
            }
        }

        /// <summary>
        /// Called by the bridge listener thread; marshals to the UI thread to
        /// build a snapshot of the current Trados project state. Mirrors the
        /// fields the in-Trados Chat already gathers in OnSendRequested so
        /// both consumers see the same shape of context.
        /// </summary>
        private BridgeContextSnapshot BuildBridgeContextSnapshot()
        {
            var ctrl = _control?.Value;
            if (ctrl == null || ctrl.IsDisposed)
                return new BridgeContextSnapshot { Available = false };

            if (ctrl.InvokeRequired)
            {
                return (BridgeContextSnapshot)ctrl.Invoke(
                    new Func<BridgeContextSnapshot>(BuildBridgeContextSnapshot));
            }

            var snapshot = new BridgeContextSnapshot { Available = false };
            if (_activeDocument == null) return snapshot;

            try
            {
                var pair = _activeDocument.ActiveSegmentPair;
                if (pair == null) return snapshot;

                // Strip U+2028 / U+2029 the same way the Chat path does
                var sourceText = pair.Source?.ToString();
                if (sourceText != null)
                    sourceText = sourceText.Replace("\u2028", " ").Replace("\u2029", " ");

                var targetText = pair.Target != null
                    ? SegmentTagHandler.GetFinalText(pair.Target)
                    : null;

                snapshot.Available = true;
                snapshot.Project = new BridgeProjectInfo
                {
                    Name = GetProjectName(),
                    FileName = GetFileName(),
                    SourceLang = GetDocumentSourceLanguage(),
                    TargetLang = GetDocumentTargetLanguage()
                };
                snapshot.ActiveSegment = new BridgeSegmentInfo
                {
                    Source = sourceText ?? "",
                    Target = targetText
                };

                // Surrounding segments
                var surroundingCount = _settings?.AiSettings?.QuickLauncherSurroundingSegments ?? 5;
                var surrounding = GetSurroundingSegments(surroundingCount);
                snapshot.SurroundingSegments = new List<BridgeSegmentInfo>();
                foreach (var s in surrounding)
                {
                    snapshot.SurroundingSegments.Add(new BridgeSegmentInfo
                    {
                        Source = s[0] ?? "",
                        Target = s[1]
                    });
                }

                // TM matches (only if user has IncludeTmMatches enabled, mirroring Chat)
                if (_settings?.AiSettings?.IncludeTmMatches != false)
                {
                    try
                    {
                        var tmMatches = DocumentContextHelper.GetTmMatches(_activeDocument);
                        snapshot.TmMatches = new List<BridgeTmMatch>();
                        if (tmMatches != null)
                        {
                            foreach (var m in tmMatches)
                            {
                                snapshot.TmMatches.Add(new BridgeTmMatch
                                {
                                    Score = m.MatchPercentage,
                                    Source = m.SourceText ?? "",
                                    Target = m.TargetText ?? "",
                                    TmName = m.TmName
                                });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[SupervertalerBridge] TM gather threw: {ex.Message}");
                    }
                }

                // Termbase hits – filter by AI-disabled IDs the same way Chat does
                try
                {
                    var disabledIds = _settings?.AiSettings?.DisabledAiTermbaseIds ?? new List<long>();
                    var allMatches = TermLensEditorViewPart.GetCurrentSegmentMatches();
                    var matchedTerms = disabledIds.Count > 0
                        ? allMatches.Where(m => !disabledIds.Contains(m.PrimaryEntry?.TermbaseId ?? 0)).ToList()
                        : allMatches;

                    snapshot.TermbaseHits = new List<BridgeTermbaseHit>();
                    foreach (var m in matchedTerms)
                    {
                        var entry = m.PrimaryEntry;
                        if (entry == null) continue;
                        snapshot.TermbaseHits.Add(new BridgeTermbaseHit
                        {
                            Source = entry.SourceTerm ?? "",
                            Target = entry.TargetTerm ?? "",
                            TermbaseName = entry.TermbaseName,
                            Definition = entry.Definition,
                            Domain = entry.Domain,
                            Notes = entry.Notes
                        });
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SupervertalerBridge] termbase gather threw: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SupervertalerBridge] BuildBridgeContextSnapshot threw: {ex.Message}");
                return new BridgeContextSnapshot { Available = false };
            }

            return snapshot;
        }

        /// <summary>
        /// Inserts text into the active Trados target segment via the same
        /// Selection.Target.Replace path that powers the in-Chat Apply-To-Target
        /// button. Returns null on success, an error string otherwise.
        /// Marshals to the UI thread.
        /// </summary>
        private string BridgeInsertTranslation(string text)
        {
            var ctrl = _control?.Value;
            if (ctrl == null || ctrl.IsDisposed) return "ai assistant disposed";

            if (ctrl.InvokeRequired)
            {
                return (string)ctrl.Invoke(
                    new Func<string, string>(BridgeInsertTranslation), text);
            }

            if (_activeDocument == null) return "no active document";
            if (string.IsNullOrEmpty(text)) return "empty text";

            try
            {
                _activeDocument.Selection.Target.Replace(text, "Supervertaler Workbench");
                return null;
            }
            catch (Exception ex)
            {
                return "insert failed: " + ex.Message;
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

                // Diagnostic log: record exactly which termbase terms TermScan injected into
                // the prompt, so the injection is auditable after the fact (open the BridgeLog
                // file in %TEMP%). Logging must never interfere with prompt generation.
                try
                {
                    var injectedTerms = string.Join(", ", termbaseTerms
                        .Select(t => t.SourceTerm)
                        .Where(s => !string.IsNullOrWhiteSpace(s)));
                    var injectedFrom = string.Join(", ", termbaseTerms
                        .Select(t => t.TermbaseName ?? "Default")
                        .Distinct());
                    BridgeLog.Write(
                        $"[AutoPrompt] TermScan injected {termbaseTerms.Count} of {totalTermCount} " +
                        $"enabled term(s) from termbase(s): {injectedFrom}. Terms: {injectedTerms}");
                }
                catch { /* never break prompt generation on a logging failure */ }

                // AutoPrompt works best with a small, project-focused termbase. Warn based on
                // the SIZE of the termbase(s) enabled for AI (totalTermCount, before TermScan
                // filtering) – not on how many terms survive filtering. A large or general-
                // purpose termbase, even when filtered, injects many incidental whole-word
                // matches (common words that merely appear in the document) and is the wrong
                // tool for AutoPrompt. Threshold is deliberately low: a useful AutoPrompt
                // glossary is typically a few dozen carefully chosen terms, not hundreds.
                const int LargeTermbaseWarningThreshold = 50;
                if (totalTermCount > LargeTermbaseWarningThreshold)
                {
                    var parent = _control.Value.FindForm();
                    var warn =
                        $"The termbase(s) you have enabled for AI contain {totalTermCount:N0} terms.\n\n" +
                        "AutoPrompt works best with a small, project-focused termbase – typically " +
                        "a few dozen carefully chosen terms. Large or general-purpose termbases inject " +
                        "many incidental matches (common words that merely appear in the document), " +
                        "which crowd the prompt with terms that are not relevant to this project.\n\n" +
                        "Tip: on Settings → Termbases, untick the “AI” column for large or " +
                        "general termbases and enable only a compact, project-specific one.\n\n" +
                        "Generate the prompt anyway?";
                    var choice = MessageBox.Show(
                        parent, warn, "Large termbase – AutoPrompt",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);
                    if (choice != DialogResult.Yes)
                        return;
                }

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
                // Use 32768 tokens for prompt generation – comprehensive prompts with
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
        /// are Translated, ApprovedTranslation, or ApprovedSignOff – i.e., segments
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

                    // Only include human-confirmed segments – not unconfirmed AI output
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

        private void OnSaveToMemoryBank(object sender, string assistantContent)
        {
            SafeInvoke(() =>
            {
                if (string.IsNullOrWhiteSpace(assistantContent))
                    return;

                var vaultDir = ActiveMemoryBankDir;
                var bankName = ActiveMemoryBankName;

                if (!Directory.Exists(vaultDir))
                {
                    ShowSuperMemoryMessage(
                        $"Memory bank **{bankName}** does not exist yet.\n\n" +
                        $"Expected location:\n`{vaultDir}`");
                    return;
                }

                // Find the preceding user message in chat history
                string userQuestion = null;
                for (int i = _chatHistory.Count - 1; i >= 0; i--)
                {
                    if (_chatHistory[i].Role == ChatRole.User)
                    {
                        userQuestion = _chatHistory[i].Content;
                        break;
                    }
                }

                // Write inbox note
                var inboxDir = Path.Combine(vaultDir, "00_INBOX");
                Directory.CreateDirectory(inboxDir);

                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var fileName = $"chat-save-{stamp}.md";
                var filePath = Path.Combine(inboxDir, fileName);

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("# Chat – saved to memory bank");
                sb.AppendLine($"*Saved on {DateTime.Now:yyyy-MM-dd HH:mm} from Supervertaler*");
                sb.AppendLine();

                if (!string.IsNullOrWhiteSpace(userQuestion))
                {
                    sb.AppendLine("## Question");
                    sb.AppendLine();
                    sb.AppendLine(userQuestion);
                    sb.AppendLine();
                }

                sb.AppendLine("## Answer");
                sb.AppendLine();
                sb.AppendLine(assistantContent);

                File.WriteAllText(filePath, sb.ToString(), new System.Text.UTF8Encoding(false));

                // Confirmation in chat
                ShowSuperMemoryMessage(
                    $"Saved to memory bank **{bankName}** \u2014 " +
                    "run **Process Inbox** to compile it into the knowledge base.");

                RefreshSuperMemoryInboxCount();
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
        /// Static-event handler wired in Initialize. Runs whenever the user toggles
        /// the active prompt in the Prompt Manager, regardless of which code path
        /// opened the Settings dialog.
        /// </summary>
        private void OnActivePromptChangedGlobal(object sender, string newPath)
        {
            RefreshBatchPromptDropdownWithActive(newPath);
        }

        /// <summary>
        /// Live variant of <see cref="PopulateBatchPromptDropdown"/>: refreshes the
        /// Batch Translate dropdown using an in-memory active-prompt path (typically
        /// the pending value from the Prompt Manager while the Settings dialog is
        /// still open). The change is NOT persisted here – the normal on-close
        /// refresh reads from disk, so a Cancel naturally snaps back.
        /// </summary>
        private void RefreshBatchPromptDropdownWithActive(string activePath)
        {
            SafeInvoke(() =>
            {
                try
                {
                    // Use the existing cache – the prompt library on disk hasn't
                    // changed (the user is just toggling active in memory), so a
                    // rescan is unnecessary and would slow down each right-click.
                    var prompts = _promptLibrary?.GetAllPrompts();
                    if (prompts == null) return;
                    if (_control?.Value?.BatchTranslateControl == null) return;

                    var normalisedActive = string.IsNullOrEmpty(activePath) ? null : activePath;
                    var selectedPath = normalisedActive ?? (_settings?.AiSettings?.SelectedPromptPath ?? "");

                    var mode = _control.Value.BatchTranslateControl.CurrentMode;
                    var categoryFilter = mode == BatchMode.Proofread ? "Proofread" : "Translate";
                    var projectName = TermLensEditorViewPart.GetCurrentProjectName();

                    _control.Value.BatchTranslateControl.SetPrompts(
                        prompts, selectedPath, categoryFilter, projectName, normalisedActive);
                }
                catch
                {
                    // Swallow – a stale settings dialog or disposed control shouldn't
                    // surface an error to the user for a UI-refresh helper.
                }
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
        private string LoadKbContextForPrompt(string projectName, string sourceLang, string targetLang, string queryText = null)
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
                    tokenBudget: 24000, queryText: queryText);

                if (ctx == null) return null;

                return MemoryBankReader.FormatForPrompt(ctx);
            }
            catch
            {
                return null; // KB is optional – never block translation
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  Memory-bank dropdown: populate + live switching
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Rebuilds the Memory Bank dropdown in the SuperMemory toolbar from
        /// the current on-disk bank list, pre-selecting the active bank. Safe
        /// to call repeatedly – the toolbar suppresses its own change event
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
                // Non-critical – the rest of the AI Assistant still works
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

            // User-initiated action (dropdown selection) – re-engage
            // auto-scroll so the "Switched to memory bank X" confirmation
            // and any follow-up heal prompt chat messages land in view.
            _control.Value.ReengageAutoScroll();

            try
            {
                // 1. Persist the new active bank to settings
                if (_settings == null) _settings = TermLensSettings.Load();
                if (_settings.AiSettings == null) _settings.AiSettings = new AiSettings();
                _settings.AiSettings.ActiveMemoryBankName = newName;
                _settings.Save();

                // 2. Invalidate the cached reader – the next LoadKbContextForPrompt
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
                        Content = $"Left memory bank **{bankName}** as-is. Process Inbox and Health Check will not work until the missing template files ({missingList}) are restored – switch away and back, or create a fresh bank, to see the restore prompt again."
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
            // User-initiated action (+ New memory bank sentinel) – re-engage
            // auto-scroll so the "Created memory bank X" confirmation lands
            // in view after the dialog closes.
            _control.Value.ReengageAutoScroll();

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

                // Creation failed – tell the user why and loop back to the
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
                    Text = "Folder name: –",
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
                        lblPreview.Text = "Folder name: –";
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

                // Watch every file type, not just *.md – users drop TMX, PDF,
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
                // Non-critical – toolbar still works via manual refresh
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
            // User-initiated action – re-engage auto-scroll so the progress
            // message and the response land in view.
            _control.Value.ReengageAutoScroll();

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
            var inboxFiles = new List<Tuple<string, string>>(); // (path, content) – .md only
            var nonMdFiles = new List<string>();                // paths of everything else

            foreach (var f in allFiles)
            {
                // Skip bank-internal sidecars (.edtz etc.) entirely – they are
                // neither Markdown to compile nor material to hand to Distill.
                if (Core.MemoryBankReader.IsIgnoredSidecar(f))
                    continue;

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
                    "**Process Inbox** reads Markdown briefs, notes, and feedback – it cannot extract knowledge from binary files like TMX, DOCX, PDF, or XLSX.\n\n" +
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
                        "I'll process the Markdown files now. For the others, run **Distill** afterwards – it can read TMX, DOCX, PDF, XLSX, and termbases directly."
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

            // The promptName is what the Reports tab labels each operation as.
            // Earlier in this code's life Process Inbox was internally called
            // "Compile" (the .md template file is still named compile.md, and
            // the post-processor method is still PostProcessCompileResponse),
            // but the user-facing button is "Process Inbox" everywhere else,
            // so the Reports tab should match.
            RunSuperMemoryAgent(systemPrompt, userMessage, displayText,
                PromptLogFeature.SuperMemory, "SuperMemory: Process Inbox",
                response => PostProcessCompileResponse(response, inboxFiles));
        }

        private void OnHealthCheck(object sender, EventArgs e)
        {
            // User-initiated action – re-engage auto-scroll so the progress
            // bubble and the response land in view even if the user had
            // scrolled up to read history from a previous Health Check run.
            _control.Value.ReengageAutoScroll();

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
            // Show the progress message IMMEDIATELY before the vault scan.
            // The scan below reads every .md file in the bank and can take
            // multiple seconds on a mature bank – if we did it synchronously
            // before adding a chat bubble (as the original code did), the
            // user would click Health Check and see absolutely nothing
            // until the scan finished. Adding an upfront bubble, plus
            // calling SetThinking so the Stop button replaces Send, gives
            // the user immediate visual confirmation that the click was
            // received.
            //
            // The scan itself is moved to Task.Run so the UI stays
            // responsive while it runs. Once the scan completes we hop
            // back onto the UI thread via SafeInvoke and hand the scanned
            // content to RunSuperMemoryAgent with an empty displayText so
            // it does not add a duplicate chat bubble (see the displayText
            // null check there).
            _control.Value.AddMessage(new ChatMessage
            {
                Role = ChatRole.Assistant,
                Content = $"\U0001F3E5 **SuperMemory: Health Check** \u2014 scanning memory bank **{bankName}**\u2026"
            });
            _control.Value.SetThinking(true);
            _control.Value.SetSuperMemoryBusy(true);
            SaveChatHistory();

            // Capture state for the background task.
            var capturedVaultDir = vaultDir;
            var capturedBankName = bankName;
            var capturedTemplatePath = templatePath;

            Task.Run(() =>
            {
                try
                {
                    var systemPrompt = File.ReadAllText(capturedTemplatePath);

                    // Collect vault content (skip .obsidian, .git,
                    // 06_TEMPLATES, 00_INBOX/_archive)
                    var sb = new System.Text.StringBuilder();
                    int fileCount = 0;
                    var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        { ".obsidian", ".git", "06_TEMPLATES" };

                    foreach (var dir in Directory.GetDirectories(capturedVaultDir))
                    {
                        var dirName = Path.GetFileName(dir);
                        if (skipDirs.Contains(dirName)) continue;
                        CollectVaultFiles(dir, capturedVaultDir, sb, ref fileCount, "_archive");
                    }
                    // Also collect any top-level .md files
                    foreach (var f in Directory.GetFiles(capturedVaultDir, "*.md", SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            var relPath = f.Substring(capturedVaultDir.Length).TrimStart('\\', '/');
                            sb.AppendLine($"## File: {relPath}");
                            sb.AppendLine(File.ReadAllText(f));
                            sb.AppendLine();
                            fileCount++;
                        }
                        catch { }
                    }

                    var capturedFileCount = fileCount;
                    var userMessage = $"Perform a health check on the following knowledge base ({capturedFileCount} files):\n\n{sb}";

                    // Cap the message to avoid exceeding token limits
                    // (~400K chars ≈ 100K tokens)
                    if (userMessage.Length > 400000)
                    {
                        userMessage = userMessage.Substring(0, 400000) +
                            "\n\n[Truncated \u2014 vault too large to scan in one pass. The above is a partial scan.]";
                    }

                    var capturedUserMessage = userMessage;
                    var capturedSystemPrompt = systemPrompt;

                    SafeInvoke(() =>
                    {
                        if (capturedFileCount == 0)
                        {
                            // Bank is empty after all – roll back the
                            // progress state and show the "nothing to
                            // check" message.
                            _control.Value.SetThinking(false);
                            _control.Value.SetSuperMemoryBusy(false);
                            ShowSuperMemoryMessage($"Memory bank **{capturedBankName}** is empty \u2014 nothing to check.\n\n" +
                                "Start by adding content via **Process Inbox** or the **Quick Add** shortcut (Ctrl+Alt+M).");
                            return;
                        }

                        // Empty displayText because we already showed the
                        // progress message above – RunSuperMemoryAgent
                        // will skip its own AddMessage call.
                        RunSuperMemoryAgent(capturedSystemPrompt, capturedUserMessage, "",
                            PromptLogFeature.SuperMemory, "SuperMemory: Health Check",
                            response => PostProcessHealthCheckResponse(response));
                    });
                }
                catch (Exception ex)
                {
                    var capturedError = ex.Message;
                    SafeInvoke(() =>
                    {
                        _control.Value.SetThinking(false);
                        _control.Value.SetSuperMemoryBusy(false);
                        AddErrorMessage($"Health Check scan failed: {capturedError}");
                    });
                }
            });
        }

        /// <summary>
        /// Overview button: generate a self-contained HTML overview of the active
        /// memory bank from its frontmatter index and open it in the browser.
        /// Metadata only – no LLM call – so it is fast and free.
        /// </summary>
        private void OnOverview(object sender, EventArgs e)
        {
            var vaultDir = ActiveMemoryBankDir;
            var bankName = ActiveMemoryBankName;
            if (!Directory.Exists(vaultDir))
            {
                ShowSuperMemoryMessage($"Memory bank **{bankName}** does not exist yet.\n\n" +
                    $"Expected location:\n`{vaultDir}`");
                return;
            }

            var capturedDir = vaultDir;
            var capturedName = bankName;
            Task.Run(() =>
            {
                try
                {
                    var reader = new MemoryBankReader(capturedDir);
                    var index = reader.GetIndexSnapshot();
                    var path = MemoryBankReport.WriteHtmlOverviewToTempFile(index, capturedName);
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });

                    SafeInvoke(() => ShowSuperMemoryMessage(
                        $"☰ **Memory bank overview** generated for **{capturedName}** " +
                        $"({index.Count} notes) and opened in your browser.\n\n`{path}`"));
                }
                catch (Exception ex)
                {
                    var err = ex.Message;
                    SafeInvoke(() => AddErrorMessage($"Overview generation failed: {err}"));
                }
            });
        }

        /// <summary>
        /// Summary button: ask the AI for a short plain-English profile of the
        /// active memory bank, built from a compact metadata digest (counts,
        /// domains, conflicts, term list) rather than full article bodies.
        /// </summary>
        private void OnAiSummary(object sender, EventArgs e)
        {
            _control.Value.ReengageAutoScroll();

            var vaultDir = ActiveMemoryBankDir;
            var bankName = ActiveMemoryBankName;
            if (!Directory.Exists(vaultDir))
            {
                ShowSuperMemoryMessage($"Memory bank **{bankName}** does not exist yet.\n\n" +
                    $"Expected location:\n`{vaultDir}`");
                return;
            }

            _control.Value.AddMessage(new ChatMessage
            {
                Role = ChatRole.Assistant,
                Content = $"✨ **SuperMemory: Summary** — profiling memory bank **{bankName}**…"
            });
            _control.Value.SetThinking(true);
            _control.Value.SetSuperMemoryBusy(true);
            SaveChatHistory();

            var capturedDir = vaultDir;
            var capturedName = bankName;
            Task.Run(() =>
            {
                try
                {
                    var reader = new MemoryBankReader(capturedDir);
                    var index = reader.GetIndexSnapshot();

                    if (index.Count == 0)
                    {
                        SafeInvoke(() =>
                        {
                            _control.Value.SetThinking(false);
                            _control.Value.SetSuperMemoryBusy(false);
                            ShowSuperMemoryMessage($"Memory bank **{capturedName}** is empty — nothing to summarise.");
                        });
                        return;
                    }

                    var digest = MemoryBankReport.BuildMetadataDigest(index, capturedName);
                    const string systemPrompt =
                        "You are a terminology and knowledge-base analyst. You are given a metadata " +
                        "digest of a translator's SuperMemory knowledge base: totals, domain coverage, " +
                        "terminology conflicts, stubs, and a list of source -> target term decisions. " +
                        "Write a short, scannable plain-English profile so the translator can see at a " +
                        "glance what their bank contains. Cover: (1) overall size and main focus; " +
                        "(2) the strongest domains and any thin or under-covered areas; (3) anything " +
                        "needing attention - conflicting term pairs, stubs, stale notes - naming "  +
                        "specific examples from the digest. Keep it to two short paragraphs plus a few " +
                        "bullet points. Be concrete and cite numbers. Use British English spelling. " +
                        "Do not invent entries that are not present in the digest.";

                    SafeInvoke(() =>
                        RunSuperMemoryAgent(systemPrompt, digest, "",
                            PromptLogFeature.SuperMemory, "SuperMemory: Summary", _ => { }));
                }
                catch (Exception ex)
                {
                    var err = ex.Message;
                    SafeInvoke(() =>
                    {
                        _control.Value.SetThinking(false);
                        _control.Value.SetSuperMemoryBusy(false);
                        AddErrorMessage($"Summary failed: {err}");
                    });
                }
            });
        }

        private void CollectVaultFiles(string dir, string vaultRoot,
            System.Text.StringBuilder sb, ref int count, string skipSubDir)
        {
            foreach (var f in Directory.GetFiles(dir, "*.md", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var fileName = Path.GetFileName(f);
                    // Skip example/template files – they're shipped scaffolding, not real content
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

            // Show status message – unless the caller has already displayed
            // its own progress bubble (in which case displayText is left
            // empty). This lets slow operations like OnHealthCheck, which
            // need to scan the vault before the displayText would know how
            // many files it is going to process, show an upfront "scanning
            // memory bank..." bubble before calling into us. SetThinking is
            // idempotent so it is safe to call even if the caller already
            // set thinking.
            if (!string.IsNullOrEmpty(displayText))
            {
                _control.Value.AddMessage(new ChatMessage
                {
                    Role = ChatRole.Assistant,
                    Content = displayText
                });
            }
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

                if (writtenFiles.Count > 0)
                {
                    summary.AppendLine();
                    summary.AppendLine("**Next steps:**");
                    summary.AppendLine("1. Click **Health Check** to scan the bank for inconsistencies, broken links, and missing cross-references introduced by the new articles.");
                    summary.AppendLine("2. (Optional) Open the bank in Obsidian to browse the structured articles and the knowledge graph.");
                }

                var summaryMsg = new ChatMessage
                {
                    Role = ChatRole.Assistant,
                    Content = summary.ToString()
                };
                _chatHistory.Add(summaryMsg);
                _control.Value.AddMessage(summaryMsg);
                SaveChatHistory();
            }

            // Rebuild master indices after every successful Process Inbox run
            RebuildIndices(vaultDir);

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

            // Always append a completion summary at the end, regardless of
            // whether the AI produced any "### FILE:" markers to auto-fix.
            // Without this the user sees the tail of the report but has no
            // clear "done" indicator – the Stop button reverts to Send and
            // the thinking bubble disappears, but there is no chat message
            // marking the transition, which is confusing especially after
            // a long-running Health Check where the user may have scrolled
            // away during the wait.
            var summary = new System.Text.StringBuilder();

            if (fileResults.Count > 0)
            {
                int newCount = 0, updatedCount = 0;
                foreach (var r in fileResults)
                    if (r.Item2) newCount++; else updatedCount++;

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
            }
            else
            {
                // No files auto-fixed – the AI's response was purely a
                // report with findings flagged for human review, or the
                // parser did not recognise the AI's fix format. Either way,
                // the user needs an explicit "done" marker.
                summary.AppendLine("**Health Check complete \u2014 no changes applied**\n");
                summary.AppendLine("The AI scanned the active memory bank and wrote its report above, but did not auto-fix any files. Any issues it flagged are for your review.\n");
                summary.AppendLine("Scroll up to read the full report, and open Obsidian if you want to inspect the flagged files.");
            }

            var msg = new ChatMessage { Role = ChatRole.Assistant, Content = summary.ToString() };
            _chatHistory.Add(msg);
            _control.Value.AddMessage(msg);
            SaveChatHistory();

            // Rebuild master indices after Health Check (it may have restructured articles)
            RebuildIndices(vaultDir);
        }

        // ─── Auto-indexing ──────────────────────────────────────────

        /// <summary>
        /// Rebuilds the master index files in <c>05_INDICES/</c> by scanning
        /// all content folders for article frontmatter. No LLM call – this is
        /// a pure file-scan operation and completes in under a second even on
        /// large banks.
        /// </summary>
        private void RebuildIndices(string vaultDir)
        {
            try
            {
                var indicesDir = Path.Combine(vaultDir, "05_INDICES");
                Directory.CreateDirectory(indicesDir);

                var today = DateTime.Now.ToString("yyyy-MM-dd");

                // ── Master Terminology Index ───────────────────────
                var termDir = Path.Combine(vaultDir, "02_TERMINOLOGY");
                var termSb = new System.Text.StringBuilder();
                termSb.AppendLine("---");
                termSb.AppendLine("title: Master Terminology Index");
                termSb.AppendLine("type: index");
                termSb.AppendLine($"updated: {today}");
                termSb.AppendLine("---");
                termSb.AppendLine();
                termSb.AppendLine("# Master Terminology Index");
                termSb.AppendLine();
                termSb.AppendLine("| Source | Target | Domain | Client | Confidence | Status |");
                termSb.AppendLine("|--------|--------|--------|--------|------------|--------|");

                if (Directory.Exists(termDir))
                {
                    foreach (var file in Directory.GetFiles(termDir, "*.md", SearchOption.AllDirectories))
                    {
                        var fn = Path.GetFileName(file);
                        if (fn.StartsWith("_EXAMPLE_", System.StringComparison.OrdinalIgnoreCase)) continue;
                        if (file.Contains("_archive")) continue;

                        var head = MemoryBankReader.ReadHead(file, 2048);
                        var fm = MemoryBankReader.ParseFrontmatter(head);

                        var src = fm.ContainsKey("term_source") ? fm["term_source"] : "";
                        var tgt = fm.ContainsKey("term_target") ? fm["term_target"] : "";
                        var domain = fm.ContainsKey("domain") ? fm["domain"] : "";
                        var client = fm.ContainsKey("client") ? fm["client"] : "";
                        var confidence = fm.ContainsKey("confidence") ? fm["confidence"] : "";
                        var status = fm.ContainsKey("status") ? fm["status"] : "";

                        if (string.IsNullOrWhiteSpace(src) && string.IsNullOrWhiteSpace(tgt))
                        {
                            // Fall back to title if term_source/term_target are missing
                            var title = fm.ContainsKey("title") ? fm["title"] : Path.GetFileNameWithoutExtension(fn);
                            src = title;
                        }

                        termSb.AppendLine($"| {Escape(src)} | {Escape(tgt)} | {Escape(domain)} | {Escape(client)} | {confidence} | {status} |");
                    }
                }

                File.WriteAllText(Path.Combine(indicesDir, "master-terminology.md"),
                    termSb.ToString(), new System.Text.UTF8Encoding(false));

                // ── Client Summary ─────────────────────────────────
                var clientDir = Path.Combine(vaultDir, "01_CLIENTS");
                var clientSb = new System.Text.StringBuilder();
                clientSb.AppendLine("---");
                clientSb.AppendLine("title: Client Summary");
                clientSb.AppendLine("type: index");
                clientSb.AppendLine($"updated: {today}");
                clientSb.AppendLine("---");
                clientSb.AppendLine();
                clientSb.AppendLine("# Client Summary");
                clientSb.AppendLine();

                if (Directory.Exists(clientDir))
                {
                    foreach (var file in Directory.GetFiles(clientDir, "*.md", SearchOption.AllDirectories))
                    {
                        var fn = Path.GetFileName(file);
                        if (fn.StartsWith("_EXAMPLE_", System.StringComparison.OrdinalIgnoreCase)) continue;
                        if (file.Contains("_archive")) continue;

                        var head = MemoryBankReader.ReadHead(file, 2048);
                        var fm = MemoryBankReader.ParseFrontmatter(head);

                        var title = fm.ContainsKey("title") ? fm["title"]
                            : fm.ContainsKey("client") ? fm["client"]
                            : Path.GetFileNameWithoutExtension(fn);
                        var tldr = fm.ContainsKey("tldr") ? fm["tldr"] : null;

                        // If no tldr, extract the first non-frontmatter, non-heading paragraph
                        if (string.IsNullOrWhiteSpace(tldr))
                            tldr = ExtractFirstParagraph(head);

                        clientSb.AppendLine($"## {title}");
                        clientSb.AppendLine();
                        if (!string.IsNullOrWhiteSpace(tldr))
                            clientSb.AppendLine(tldr);
                        else
                            clientSb.AppendLine("*(No summary available)*");
                        clientSb.AppendLine();
                    }
                }

                File.WriteAllText(Path.Combine(indicesDir, "client-summary.md"),
                    clientSb.ToString(), new System.Text.UTF8Encoding(false));

                // ── Domain Summary ─────────────────────────────────
                var domainDir = Path.Combine(vaultDir, "03_DOMAINS");
                var domainSb = new System.Text.StringBuilder();
                domainSb.AppendLine("---");
                domainSb.AppendLine("title: Domain Summary");
                domainSb.AppendLine("type: index");
                domainSb.AppendLine($"updated: {today}");
                domainSb.AppendLine("---");
                domainSb.AppendLine();
                domainSb.AppendLine("# Domain Summary");
                domainSb.AppendLine();

                if (Directory.Exists(domainDir))
                {
                    foreach (var file in Directory.GetFiles(domainDir, "*.md", SearchOption.AllDirectories))
                    {
                        var fn = Path.GetFileName(file);
                        if (fn.StartsWith("_EXAMPLE_", System.StringComparison.OrdinalIgnoreCase)) continue;
                        if (file.Contains("_archive")) continue;

                        var head = MemoryBankReader.ReadHead(file, 2048);
                        var fm = MemoryBankReader.ParseFrontmatter(head);

                        var title = fm.ContainsKey("title") ? fm["title"]
                            : fm.ContainsKey("domain") ? fm["domain"]
                            : Path.GetFileNameWithoutExtension(fn);
                        var tldr = fm.ContainsKey("tldr") ? fm["tldr"] : null;

                        if (string.IsNullOrWhiteSpace(tldr))
                            tldr = ExtractFirstParagraph(head);

                        domainSb.AppendLine($"## {title}");
                        domainSb.AppendLine();
                        if (!string.IsNullOrWhiteSpace(tldr))
                            domainSb.AppendLine(tldr);
                        else
                            domainSb.AppendLine("*(No summary available)*");
                        domainSb.AppendLine();
                    }
                }

                File.WriteAllText(Path.Combine(indicesDir, "domain-summary.md"),
                    domainSb.ToString(), new System.Text.UTF8Encoding(false));
            }
            catch
            {
                // Non-critical – indices are a convenience, not a requirement
            }
        }

        /// <summary>Escapes pipe characters for Markdown table cells.</summary>
        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Replace("|", "\\|");
        }

        /// <summary>
        /// Extracts the first non-empty paragraph after the frontmatter block
        /// and any heading lines. Used as a fallback when no <c>tldr:</c> is
        /// available in the frontmatter.
        /// </summary>
        private static string ExtractFirstParagraph(string head)
        {
            if (string.IsNullOrEmpty(head)) return null;

            var lines = head.Split('\n');
            bool pastFrontmatter = false;
            bool inFrontmatter = false;

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();

                if (!pastFrontmatter)
                {
                    if (line == "---" && !inFrontmatter)
                    { inFrontmatter = true; continue; }
                    if (line == "---" && inFrontmatter)
                    { pastFrontmatter = true; continue; }
                    continue;
                }

                // Skip headings and empty lines
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.StartsWith("#")) continue;

                // Found a content line – return it (trimmed to ~200 chars)
                return line.Length > 200 ? line.Substring(0, 200) + "…" : line;
            }

            return null;
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
- `00_INBOX/<filename>.md` – ALL distilled articles go here for review

Each article must have this frontmatter structure:
```
---
title: <descriptive title>
type: terminology|domain|style|client|reference
domain: <subject area, e.g. medical-imaging, patent-law, legal, marketing>
client: <client name if known, omit if generic>
language_pair: <e.g. nl-BE → en-US>
confidence: high|medium|low
tags: [<relevant tags>]
source: distilled
sources:
  - <original filename 1>
  - <original filename 2>
created: <today's date YYYY-MM-DD>
updated: <today's date YYYY-MM-DD>
tldr: <one-sentence summary of what this article covers – max 150 characters>
---
```

### Confidence scoring

Assign a confidence level based on the quality and authority of the source material:
- **high** – derived from an authoritative source: official client glossary, published style guide, large TMX with consistent patterns, or confirmed by multiple corroborating sources.
- **medium** – derived from a single source of reasonable quality: a short PDF, a single reference document, a small TMX.
- **low** – derived from ambiguous or incomplete material, or when the extraction required significant inference. Flag uncertain terminology decisions explicitly.

### Source traceability

Always list the original source filename(s) in the `sources:` frontmatter field. When recording terminology decisions, **always quote the exact source and target terms verbatim** – do not paraphrase or generalise term pairs.

## Guidelines

- Keep articles **focused and concise** – one topic per article where possible.
- Use bullet points and tables for terminology lists.
- Include the *reasoning* behind translation choices, not just the choices themselves.
- When in doubt, create separate articles rather than one huge article.
- Write in English (the knowledge base language), but include source/target examples in their original languages.
- If the source material is too large to fully process, prioritise the most valuable and non-obvious knowledge.
- Always include a `tldr:` – this is used for fast scanning during context loading.";

        private void OnDistill(object sender, EventArgs e)
        {
            // User-initiated action – re-engage auto-scroll so the progress
            // bubble and the response land in view.
            _control.Value.ReengageAutoScroll();

            var vaultDir = ActiveMemoryBankDir;
            var bankName = ActiveMemoryBankName;
            if (!Directory.Exists(vaultDir))
            {
                ShowSuperMemoryMessage($"Memory bank **{bankName}** does not exist yet.\n\n" +
                    $"Expected location:\n`{vaultDir}`");
                return;
            }

            // Scan inbox for non-Markdown files that can be distilled. Skip
            // bank-internal sidecars (.edtz etc.) – DocumentTextExtractor will
            // throw "Unsupported file format" on those, and they aren't
            // knowledge content anyway.
            var inboxDir = Path.Combine(vaultDir, "00_INBOX");
            var inboxDistillable = new List<string>();
            if (Directory.Exists(inboxDir))
            {
                foreach (var f in Directory.GetFiles(inboxDir, "*", SearchOption.TopDirectoryOnly))
                {
                    if (Core.MemoryBankReader.IsIgnoredSidecar(f))
                        continue;
                    var ext = Path.GetExtension(f);
                    if (!string.Equals(ext, ".md", System.StringComparison.OrdinalIgnoreCase))
                        inboxDistillable.Add(f);
                }
            }

            // Show choice dialog: distill inbox or pick files from disk.
            string[] selectedFiles;
            using (var choiceDlg = new Controls.DistillChoiceDialog(inboxDistillable))
            {
                if (choiceDlg.ShowDialog() != DialogResult.OK)
                    return;

                if (choiceDlg.Choice == Controls.DistillChoice.DistillInbox)
                {
                    selectedFiles = inboxDistillable.ToArray();
                }
                else // SelectFiles
                {
                    using (var dlg = new OpenFileDialog())
                    {
                        dlg.Title = "Select files to distill into knowledge base articles";
                        dlg.Filter = "Translation files|*.tmx;*.docx;*.pdf;*.xlsx;*.csv;*.tsv;*.tbx;*.xml;*.txt|All files|*.*";
                        dlg.Multiselect = true;
                        if (dlg.ShowDialog() != DialogResult.OK || dlg.FileNames.Length == 0)
                            return;
                        selectedFiles = dlg.FileNames;
                    }
                }
            }

            // Extract text from each file. We track full source paths in
            // parallel so PostProcessDistillResponse can archive any source
            // file that lives inside the active bank's 00_INBOX/ folder
            // after a successful distill – that closes the loop on "user
            // dropped a TMX in the inbox, ran Distill, and now wants the
            // source file to follow the same archive workflow as the
            // Markdown files Process Inbox writes from it".
            var fileContents = new List<Tuple<string, string>>(); // (filename, extractedText)
            var sourceFilePaths = new List<string>();             // full paths, parallel to fileContents
            var errors = new List<string>();

            foreach (var filePath in selectedFiles)
            {
                try
                {
                    var text = DocumentTextExtractor.ExtractText(filePath);
                    fileContents.Add(Tuple.Create(Path.GetFileName(filePath), text));
                    sourceFilePaths.Add(filePath);
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
                    "\n\n[Truncated – files too large to process in one pass. The above is a partial extraction.]";
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
                response => PostProcessDistillResponse(response, sourceFilePaths, archiveInboxSources: true));
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
                    "\n\n[Truncated – termbase too large to process in one pass.]";
            }

            var displayText = $"\u2697 **SuperMemory: Distill Termbase** \u2014 {termbaseName}";
            // For the termbase shortcut there is no source file on disk to
            // archive – the source is the live termbase database. Pass the
            // termbase name through as a synthetic "path"; the archive
            // logic in PostProcessDistillResponse will see it is not a real
            // file and skip it. archiveInboxSources defaults to false.
            var sourceFilePaths = new List<string> { termbaseName };

            RunSuperMemoryAgent(DistillSystemPrompt, userMessage, displayText,
                PromptLogFeature.SuperMemory, "SuperMemory: Distill",
                response => PostProcessDistillResponse(response, sourceFilePaths));
        }

        /// <summary>
        /// Parses a Distill AI response into Markdown articles, writes them
        /// into the active bank, and (optionally) archives any source files
        /// that were sitting inside the bank's <c>00_INBOX/</c> folder.
        /// </summary>
        /// <param name="response">Raw AI response containing <c>### FILE: path</c> markers.</param>
        /// <param name="sourceFilePaths">
        /// Full paths (or synthetic display names for the termbase shortcut)
        /// of the files that fed this Distill run. Used both for the chat
        /// summary and – when <paramref name="archiveInboxSources"/> is true –
        /// for the inbox archive sweep.
        /// </param>
        /// <param name="archiveInboxSources">
        /// When true, after a successful distill (i.e. at least one Markdown
        /// article was written), each entry in <paramref name="sourceFilePaths"/>
        /// is checked for being directly inside the active bank's
        /// <c>00_INBOX/</c> folder. Matching files are moved to
        /// <c>00_INBOX/_archive/</c>, mirroring how Process Inbox archives
        /// the Markdown files it compiles. False (the default) is used by
        /// <see cref="DistillTermbase"/> where the "source" is a live database
        /// rather than a file on disk.
        /// </param>
        private void PostProcessDistillResponse(string response, List<string> sourceFilePaths, bool archiveInboxSources = false)
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

            // Archive any source files that came from inside the bank's inbox.
            // Only fires when (a) the caller asked us to archive (file picker
            // path, not the termbase shortcut) and (b) the AI actually wrote
            // at least one Markdown article – if the distill produced nothing
            // we want the source files to stay in the inbox so the user can
            // retry without losing them.
            int archivedCount = 0;
            var archivedNames = new List<string>();
            if (archiveInboxSources && writtenFiles.Count > 0 && sourceFilePaths != null)
            {
                var archiveDir = Path.Combine(vaultDir, "00_INBOX", "_archive");
                foreach (var sourcePath in sourceFilePaths)
                {
                    if (!IsDirectlyInsideInbox(sourcePath, vaultDir)) continue;
                    if (!File.Exists(sourcePath)) continue;
                    try
                    {
                        Directory.CreateDirectory(archiveDir);
                        var fileName = Path.GetFileName(sourcePath);
                        var destPath = Path.Combine(archiveDir, fileName);

                        // If a file with the same name already exists in the
                        // archive (e.g. the user re-distilled the same file),
                        // append a timestamp suffix rather than overwriting.
                        if (File.Exists(destPath))
                        {
                            var stem = Path.GetFileNameWithoutExtension(fileName);
                            var ext = Path.GetExtension(fileName);
                            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                            destPath = Path.Combine(archiveDir, $"{stem}.{stamp}{ext}");
                        }

                        File.Move(sourcePath, destPath);
                        archivedCount++;
                        archivedNames.Add(fileName);
                    }
                    catch
                    {
                        // Per-file failures are non-fatal – the rest of the
                        // distill still succeeded, the user can clean up
                        // manually if a file is locked by another process.
                    }
                }
            }

            // Show summary
            if (writtenFiles.Count > 0)
            {
                // Use display names for the "Distilled X into N articles" line.
                // For the file-picker path these are real filenames; for the
                // termbase shortcut they are the synthetic termbase name.
                var displayNames = new List<string>();
                if (sourceFilePaths != null)
                {
                    foreach (var p in sourceFilePaths)
                        displayNames.Add(Path.GetFileName(p));
                }

                var summary = new System.Text.StringBuilder();
                summary.AppendLine("**SuperMemory: Distill complete**\n");
                summary.AppendLine($"Distilled {string.Join(", ", displayNames)} into {writtenFiles.Count} article{(writtenFiles.Count != 1 ? "s" : "")}:");
                foreach (var f in writtenFiles)
                    summary.AppendLine($"- `{f}`");

                if (archivedCount > 0)
                {
                    summary.AppendLine($"\nArchived {archivedCount} source file{(archivedCount != 1 ? "s" : "")} from `00_INBOX/` to `00_INBOX/_archive/`: {string.Join(", ", archivedNames)}.");
                }

                summary.AppendLine();
                summary.AppendLine("**Next steps:**");
                summary.AppendLine("1. Click **Process Inbox** to compile these draft articles into structured client, terminology, domain, and style entries in the bank.");
                summary.AppendLine("2. Click **Health Check** afterwards to scan the bank for inconsistencies, broken links, and missing cross-references.");
                summary.AppendLine("3. (Optional) Open the bank in Obsidian if you want to review or edit the drafts before processing.");

                var msg = new ChatMessage { Role = ChatRole.Assistant, Content = summary.ToString() };
                _chatHistory.Add(msg);
                _control.Value.AddMessage(msg);
                SaveChatHistory();

                // Make sure the inbox count tile reflects the archived files
                // straight away (the FileSystemWatcher will catch up too, but
                // an explicit refresh removes the half-second flicker).
                if (archivedCount > 0)
                    RefreshSuperMemoryInboxCount();
            }
        }

        /// <summary>
        /// True when <paramref name="filePath"/> sits directly in
        /// <c>&lt;bankDir&gt;/00_INBOX/</c> (not in a sub-folder, not in
        /// <c>_archive/</c>, not anywhere else on disk). Used by
        /// <see cref="PostProcessDistillResponse"/> to decide whether a
        /// Distill source file is part of the inbox lifecycle and should
        /// be archived after processing.
        /// </summary>
        private static bool IsDirectlyInsideInbox(string filePath, string bankDir)
        {
            if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(bankDir)) return false;
            try
            {
                var inboxDir = Path.GetFullPath(Path.Combine(bankDir, "00_INBOX"));
                var fileDir = Path.GetFullPath(Path.GetDirectoryName(filePath) ?? "");
                return string.Equals(fileDir, inboxDir, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
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

                // Warn if document context will be truncated for the AI. Truncation
                // happens silently inside TranslationPrompt.BuildSystemPrompt – without
                // this warning, users would have no way to know their middle-of-document
                // segments aren't visible to the AI, which can hurt terminology
                // consistency on long jobs.
                if (aiSettings.IncludeDocumentContext && docSegments != null)
                {
                    var maxDocSegs = aiSettings.DocumentContextMaxSegments > 0
                        ? aiSettings.DocumentContextMaxSegments : 500;
                    if (docSegments.Count > maxDocSegs)
                    {
                        int firstCount = (int)(maxDocSegs * 0.8);
                        int lastCount = maxDocSegs - firstCount;
                        int omitted = docSegments.Count - maxDocSegs;
                        batchControl.AppendLog(
                            $"⚠ Document context truncated: {docSegments.Count} segments " +
                            $"in document, but only {maxDocSegs} fit in the AI context window " +
                            $"(segments 1–{firstCount} and " +
                            $"{docSegments.Count - lastCount + 1}–{docSegments.Count} sent; " +
                            $"the middle {omitted} segments are omitted). " +
                            $"To send the whole document, raise “Max segments” in " +
                            $"Settings → AI Settings → AI Context.");
                    }
                }

                // Start backup TMX – written every 10 segments so translations survive a crash
                if (batchControl.IsTmxBackupEnabled)
                {
                    var backupPath = Settings.UserDataPath.BatchBackupFilePath(
                        DateTime.Now, TermLensEditorViewPart.GetCurrentProjectName());
                    _batchBackup = new BatchTranslationBackup(
                        backupPath, sourceLang, targetLang,
                        GetType().Assembly.GetName().Version?.ToString());
                    batchControl.AppendLog($"Backup TMX: {backupPath}");
                }

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

        private void OnOpenBackupFolderRequested(object sender, EventArgs e)
        {
            try
            {
                var dir = Settings.UserDataPath.BatchBackupsDir;
                System.IO.Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start("explorer.exe", dir);
            }
            catch { }
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

                // Re-activate the Supervertaler Assistant pane at every batch
                // boundary. When ProcessSegmentPair writes a translation to a
                // segment, Trados' built-in Translation Results pane reacts to
                // the active-segment change and steals focus. Without this
                // counter-Activate, the user's Supervertaler Assistant tab
                // loses front position on every batch boundary.
                //
                // Trigger on both "Translating batch ..." (next batch starting)
                // AND "✓ Batch X complete" (this batch's writes just landed).
                // Trados' focus steal happens DURING/AFTER ProcessSegmentPair
                // writes, which fire just before "✓ Batch ... complete" is
                // logged. Activating only on next-batch-start was too late on
                // the last batch and on slow API runs where the steal landed
                // mid-gap and stuck. The synchronous Activate handles inline
                // steals; the deferred Activate (posted via BeginInvoke) wins
                // against steals queued for a later UI tick — same pattern as
                // OnNavigateToSegment.
                if (!string.IsNullOrEmpty(e.Message) &&
                    (e.Message.StartsWith("Translating batch ", StringComparison.Ordinal) ||
                     e.Message.StartsWith("Proofreading batch ", StringComparison.Ordinal) ||
                     e.Message.StartsWith("✓ Batch ", StringComparison.Ordinal)))
                {
                    try { Activate(); } catch { }
                    try
                    {
                        _control.Value.BeginInvoke((Action)(() =>
                        {
                            try { Activate(); } catch { }
                        }));
                    }
                    catch { /* control may not be available */ }
                }
            });
        }

        private void OnBatchSegmentTranslated(object sender, BatchSegmentResultEventArgs e)
        {
            // Run SYNCHRONOUSLY on the UI thread so e.WriteSucceeded is set
            // before BatchTranslator reads it back. SafeInvoke uses BeginInvoke
            // (asynchronous) and would return before the write attempt, leaving
            // the flag at its default true and the final completion summary
            // over-reporting success on write failures.
            void DoWrite()
            {
                try
                {
                    // Capture to avoid NullReferenceException if the user switches projects
                    // while batch translation is running (OnActiveDocumentChanged can null
                    // _activeDocument between the null check and ProcessSegmentPair).
                    var doc = _activeDocument;
                    if (e.SegmentPairRef == null || doc == null) { e.WriteSucceeded = false; return; }

                    // All segments now store ISegmentPair for ProcessSegmentPair.
                    // This avoids the editor buffer issue (Selection.Target.Replace
                    // loses changes) and ensures correct soft return handling for
                    // Excel/Visio segments with literal newlines.
                    var pair = e.SegmentPairRef as ISegmentPair;
                    if (pair == null) { e.WriteSucceeded = false; return; }

                    doc.ProcessSegmentPair(pair, "Supervertaler",
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

                    // Back up to TMX regardless of tag complexity
                    _batchBackup?.AddSegment(e.SourceText, e.Translation);
                }
                catch (Exception ex)
                {
                    e.WriteSucceeded = false;
                    _control.Value.BatchTranslateControl.AppendLog(
                        $"Failed to write segment {e.SegmentIndex}: {ex.Message}", true);
                }
            }

            var ctrl = _control.Value;
            if (ctrl.InvokeRequired)
                ctrl.Invoke(new Action(DoWrite));
            else
                DoWrite();
        }

        private void OnBatchCompleted(object sender, BatchCompletedEventArgs e)
        {
            // Flush any remaining segments to the backup TMX before reporting completion
            var backup = _batchBackup;
            _batchBackup = null;
            backup?.Flush();

            SafeInvoke(() =>
            {
                _control.Value.BatchTranslateControl.ReportCompleted(
                    e.Translated, e.Failed, e.Skipped,
                    e.TotalTime, e.WasCancelled);

                if (backup != null && backup.Count > 0)
                {
                    _control.Value.BatchTranslateControl.AppendLog(
                        $"✓ Backup TMX saved: {backup.Count} segments → {backup.FilePath}");
                }

                // Update segment counts (some may now be filled)
                UpdateBatchSegmentCounts();

                // Final counter-Activate for the last batch. The mid-run fix
                // in OnBatchProgress only fires while progress messages are
                // arriving; once the run ends, Trados' Translation Results
                // pane can still steal focus on the last segment write. Use
                // the same dual sync + deferred pattern as OnNavigateToSegment.
                try { Activate(); } catch { }
                try
                {
                    _control.Value.BeginInvoke((Action)(() =>
                    {
                        try { Activate(); } catch { }
                    }));
                }
                catch { /* control may not be available */ }
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

                // Apply the Limit spinner – same as the API batch path
                var clipLimit = batchControl.GetMaxSegments();
                if (clipLimit > 0 && segments.Count > clipLimit)
                    segments = segments.Take(clipLimit).ToList();

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

                var includeTermMeta = aiSettings?.IncludeTermMetadata ?? true;
                var includeDocContext = aiSettings != null && aiSettings.IncludeDocumentContext;
                var maxDocSegs = aiSettings?.DocumentContextMaxSegments ?? 500;

                // Format for clipboard. Proofread mode uses the same full bilingual
                // document context the API path uses, so the clipboard text really
                // is "what would be sent to the AI". Translate mode keeps its
                // source-only document context (target text doesn't exist yet).
                string clipboardText;
                if (batchControl.CurrentMode == BatchMode.Proofread)
                {
                    var bilingualDocSegments = includeDocContext
                        ? CollectBilingualDocumentContext()
                        : null;

                    clipboardText = ClipboardRelay.FormatForProofreading(
                        segments, sourceLang, targetLang,
                        customPromptContent, termbaseTerms, customSystemPrompt,
                        bilingualDocSegments, includeTermMeta);
                }
                else
                {
                    List<string> docSegments = null;
                    if (includeDocContext)
                    {
                        var docCtx = CollectDocumentContext();
                        docSegments = docCtx.Item1;
                    }

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

        /// <summary>
        /// Shows a read-only dialog with EXACTLY what would be sent to the AI for
        /// the current Batch Translate / Batch Proofread configuration. Reuses the
        /// same ClipboardRelay assembly the Copy-to-Clipboard path uses, so what
        /// the user sees in the preview is identical to what the LLM would receive.
        /// Does NOT trigger an actual API call.
        /// </summary>
        private void OnPreviewPromptRequested(object sender, EventArgs e)
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
                    batchControl.AppendLog("No segments matched the current scope.", true);
                    return;
                }

                // Apply the Limit spinner so the preview reflects what would actually be sent
                var clipLimit = batchControl.GetMaxSegments();
                if (clipLimit > 0 && segments.Count > clipLimit)
                    segments = segments.Take(clipLimit).ToList();

                // Termbase terms, prompt path, custom prompt \u2014 same flow as the copy path
                var allTerms = TermLensEditorViewPart.GetCurrentTermbaseTerms();
                var batchDisabledIds = aiSettings?.DisabledAiTermbaseIds ?? new List<long>();
                var termbaseTerms = batchDisabledIds.Count > 0
                    ? allTerms.Where(t => !batchDisabledIds.Contains(t.TermbaseId)).ToList()
                    : allTerms;

                var selectedPromptPath = batchControl.GetSelectedPromptPath();
                if (aiSettings != null)
                    aiSettings.SelectedPromptPath = selectedPromptPath;
                _settings.Save();

                var customPromptContent = ResolveCustomPromptContent(sourceLang, targetLang);
                var customSystemPrompt = aiSettings?.CustomSystemPrompt;

                var includeTermMeta = aiSettings?.IncludeTermMetadata ?? true;
                var includeDocContext = aiSettings != null && aiSettings.IncludeDocumentContext;
                var maxDocSegs = aiSettings?.DocumentContextMaxSegments ?? 500;

                string promptText;
                if (batchControl.CurrentMode == BatchMode.Proofread)
                {
                    var bilingualDocSegments = includeDocContext
                        ? CollectBilingualDocumentContext()
                        : null;

                    promptText = ClipboardRelay.FormatForProofreading(
                        segments, sourceLang, targetLang,
                        customPromptContent, termbaseTerms, customSystemPrompt,
                        bilingualDocSegments, includeTermMeta);
                }
                else
                {
                    List<string> docSegments = null;
                    if (includeDocContext)
                    {
                        var docCtx = CollectDocumentContext();
                        docSegments = docCtx.Item1;
                    }

                    promptText = ClipboardRelay.FormatForTranslation(
                        segments, sourceLang, targetLang,
                        customPromptContent, termbaseTerms, customSystemPrompt,
                        docSegments, maxDocSegs, includeTermMeta);
                }

                var modeLabel = batchControl.CurrentMode == BatchMode.Proofread
                    ? "proofreading" : "translation";
                var title = $"Prompt preview \u2013 {modeLabel} ({segments.Count} segments)";
                var headerText = "This is exactly what will be sent to the AI for this batch: " +
                    "the assembled system prompt (including the active custom prompt, termbase entries, " +
                    "language-specific checks, and the full bilingual document context for proofread), " +
                    "followed by the numbered segment list. No LLM call is made by this preview.";

                using (var dlg = new Controls.PromptPreviewDialog(title, headerText, promptText))
                {
                    dlg.ShowDialog();
                }
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

                // Collect FULL bilingual document context (source + target for every
                // segment in the document, no truncation). The proofreader needs both
                // sides to verify cross-document target consistency – source-only
                // context can't catch "rendered as X here, Y there" claims.
                List<(string source, string target)> docSegments = null;
                if (aiSettings.IncludeDocumentContext)
                {
                    docSegments = CollectBilingualDocumentContext();
                }

                int batchSize = aiSettings.BatchSize > 0 ? aiSettings.BatchSize : 20;

                // Initialize the report
                _currentReport = new ProofreadingReport();

                // Start proofreading
                batchControl.SetRunning(true);
                batchControl.AppendLog(
                    $"Starting proofreading: {segments.Count} segments, provider={provider}, model={model}, " +
                    $"batch size={batchSize}" +
                    (docSegments != null ? $", bilingual context: {docSegments.Count} segments" : ", no document context"));

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
                    return;
                }

                // Re-activate the Supervertaler Assistant pane after navigation.
                // Same focus-steal scenario as the v4.19.66 batch-boundary fix:
                // SetActiveSegmentPair fires Trados' active-segment-changed event,
                // and the built-in Translation Results pane reacts by re-running
                // its TM/MT lookups for the new segment, which on Trados 18 brings
                // its tab to the front. Without this counter-Activate, every click
                // on a Reports issue card kicks the user away from the Reports
                // tab to Translation Results — exactly when they want to read the
                // issue details and act on them.
                //
                // The synchronous Activate() handles the case where Trados raises
                // its event inline. The deferred one (posted via BeginInvoke on
                // the control) handles the case where Trados queues the focus
                // steal for a later UI tick — by posting our Activate after, we
                // run after the steal has already happened and reliably win.
                try { Activate(); } catch { }
                try
                {
                    _control.Value.BeginInvoke((Action)(() =>
                    {
                        try { Activate(); } catch { }
                    }));
                }
                catch { /* control may not be available */ }
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

                        // Trados' segment ID IS the number shown in the editor grid –
                        // it's preserved across merges (the surviving segment keeps its
                        // ID, the retired one is gone) and assigned fresh on splits, so
                        // using it as the per-file number keeps our Reports tab numbering
                        // aligned with what the user sees in Trados even after
                        // merging or splitting. Falling back to iteration count only when
                        // the ID isn't parseable as an int (older formats / exotic filters).
                        //
                        // File-boundary detection: a segment ID that resets to a low number
                        // (or, in the non-parseable case, looks suspiciously like a restart)
                        // means we've crossed into the next file in a multi-file project.
                        int segIdNum;
                        bool parsed = int.TryParse(sid, out segIdNum);

                        if (parsed && segIdNum <= fileSegIdx && fileSegIdx > 0)
                            fileSegIdx = 0;

                        if (parsed)
                            fileSegIdx = segIdNum;
                        else
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

                    // Skip segments with empty target – nothing to proofread
                    if (string.IsNullOrWhiteSpace(targetText))
                    {
                        index++;
                        continue;
                    }

                    var sourceText = pair.Source != null
                        ? SegmentTagHandler.GetFinalText(pair.Source) : "";
                    // Strip Unicode line/paragraph separators – see comment in SendChatMessage
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
                            // "Translated only" – segments at exactly Translated status
                            include = confirmLevel == Sdl.Core.Globalization.ConfirmationLevel.Translated;
                            break;
                        case ProofreadScope.TranslatedAndConfirmed:
                            // "Translated + Approved" – Translated, Approved, and Signed-off
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

                    // Strip Unicode line/paragraph separators – see comment in SendChatMessage
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

            // Piggyback the Import / Export tab's file list AND segment
            // counter onto the same document-change events. Single-file
            // documents see no UI change; multi-file documents get a
            // checklist populated with the merged-in files.
            UpdateImportExportFileList();
            UpdateImportExportSegmentCount();
        }

        /// <summary>Update the "Segments: N" label on the Import / Export
        /// tab. In multi-file mode the count reflects ONLY segments in
        /// the currently-checked files; in single-file mode it's the
        /// active document's full count. Always runs on the UI thread
        /// via SafeInvoke and degrades to 0 on any SDK hiccup.</summary>
        private void UpdateImportExportSegmentCount()
        {
            SafeInvoke(() =>
            {
                var ctrl = _control?.Value?.ImportExportControl;
                if (ctrl == null) return;

                if (_activeDocument == null)
                {
                    ctrl.UpdateSegmentCount(0);
                    return;
                }

                // Honour the file selection when in multi-file mode.
                // GetSelectedFileIds returns an empty list for single-
                // file documents (the UI is hidden); in that case empty
                // means "no file filter — count everything".
                //
                // In multi-file mode, empty selection means the user
                // unchecked everything → count = 0 (so the "None" button
                // visibly does something).
                //
                // When per-file attribution couldn't be built (SDK didn't
                // expose enough info), we silently drop the filter and
                // count everything regardless of selection. Better to
                // show a meaningful total than a misleading 0; the export
                // path uses the same fallback so behaviour stays
                // consistent.
                var selected = ctrl.GetSelectedFileIds();
                bool multiFileVisible = ctrl.IsMultiFileUiVisible;
                HashSet<string> filter;
                if (!_perFileMappingWorked)
                {
                    filter = null;     // attribution failed → can't filter
                }
                else if (!multiFileVisible)
                {
                    filter = null;     // single-file mode — no filtering
                }
                else
                {
                    // Multi-file mode: empty selection = 0 segments.
                    // Non-empty selection = those files only.
                    filter = new HashSet<string>(selected, StringComparer.Ordinal);
                }

                int total = 0;
                try
                {
                    foreach (var pair in _activeDocument.SegmentPairs)
                    {
                        if (filter != null)
                        {
                            if (filter.Count == 0) { total = 0; break; }
                            var fileId = GetFileIdForSegment(pair);
                            if (string.IsNullOrEmpty(fileId) || !filter.Contains(fileId)) continue;
                        }
                        total++;
                    }
                }
                catch { total = 0; }

                ctrl.UpdateSegmentCount(total);
            });
        }

        // Cached map of fileId → set of "{puId}/{segId}" composite keys
        // for the active document. Built once per ActiveDocumentChanged,
        // queried per segment via GetFileIdForSegment. Bypasses the
        // brittle "look for a FileId on the segment / pu via reflection"
        // pattern that fails on Studio 18 (the property genuinely doesn't
        // exist there) in favour of walking each file's own SegmentPairs
        // collection — which we can usually access via reflection through
        // IFile.ParagraphUnits[].SegmentPairs.
        private readonly Dictionary<string, HashSet<string>> _fileIdToSegmentKeys =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _fileIdToName =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Return the list of files in the active document, plus
        /// the currently-active file's id (for the [Active only] quick-
        /// select button). For a single-file document the list has one
        /// entry — the ImportExportControl uses that as a signal to hide
        /// the multi-file UI. All access is via reflection so the code
        /// stays decoupled from per-SDK-version IFile type names.</summary>
        private List<Controls.ImportExportControl.FileEntry> EnumerateActiveDocumentFiles(out string activeFileId)
        {
            activeFileId = "";
            var entries = new List<Controls.ImportExportControl.FileEntry>();
            if (_activeDocument == null) return entries;

            // Refresh the per-file segment map. Cheap on single-file
            // documents (one iteration); a bit more on multi-file, but
            // it only runs on document changes / file-list refresh.
            RefreshFileToSegmentMap();

            // Active file id for the quick-select button.
            try
            {
                var af = _activeDocument.ActiveFile;
                if (af != null)
                    activeFileId = TryGetStringProp(af, "Id") ?? TryGetStringProp(af, "FileId") ?? "";
            }
            catch { }

            // Try _activeDocument.Files first (multi-file document
            // exposes them); fall back to ActiveFile only.
            var filesEnum = TryGetEnumerable(_activeDocument, "Files");
            if (filesEnum == null)
            {
                var af = _activeDocument.ActiveFile;
                if (af != null)
                {
                    var id = TryGetStringProp(af, "Id") ?? TryGetStringProp(af, "FileId") ?? "";
                    activeFileId = id;
                    entries.Add(new Controls.ImportExportControl.FileEntry
                    {
                        FileId = id,
                        FileName = TryGetStringProp(af, "Name") ?? "(unknown file)",
                        SegmentCount = LookupSegmentCount(id)
                    });
                }
                return entries;
            }

            foreach (var f in filesEnum)
            {
                if (f == null) continue;
                var id = TryGetStringProp(f, "Id") ?? TryGetStringProp(f, "FileId") ?? "";
                var name = TryGetStringProp(f, "Name") ?? "(unknown file)";
                entries.Add(new Controls.ImportExportControl.FileEntry
                {
                    FileId = id,
                    FileName = name,
                    SegmentCount = LookupSegmentCount(id)
                });
            }
            return entries;
        }

        private int LookupSegmentCount(string fileId)
        {
            if (string.IsNullOrEmpty(fileId)) return 0;
            return _fileIdToSegmentKeys.TryGetValue(fileId, out var keys) ? keys.Count : 0;
        }

        /// <summary>True when the most recent <see cref="RefreshFileToSegmentMap"/>
        /// call produced at least one attributed segment. When false, callers
        /// should ignore per-file filters and operate on the full segment
        /// list — the SDK didn't give us enough info to attribute segments
        /// to files, so filtering would silently drop everything.</summary>
        private bool _perFileMappingWorked = false;

        /// <summary>Rebuild the (fileId → segment-key set) map.
        ///
        /// Trados Studio 18 + 19 don't expose per-file segment enumeration
        /// at the SDK level (verified via the v4.20.8/9 diagnostics —
        /// ProjectFile has no ParagraphUnits, and paragraph-unit context
        /// metadata contains ZERO file-identifying strings, only style /
        /// header-footer info). So we go around the SDK: each ProjectFile
        /// has a <c>LocalFilePath</c> pointing at its on-disk SDLXLIFF,
        /// which is XML. We extract every GUID from each SDLXLIFF — Trados
        /// paragraph-unit ids are GUIDs and are globally unique, so the
        /// set of GUIDs in file A's SDLXLIFF is exactly the set of PU ids
        /// belonging to file A. Then for each <c>SegmentPair</c> we get the
        /// parent PU's id and look up which file's GUID set contains it.
        ///
        /// One-time cost: ~tens of MB of file I/O + a regex scan, run only
        /// when the active document changes. Sets <see cref="_perFileMappingWorked"/>
        /// to true iff at least one segment got attributed.</summary>
        private void RefreshFileToSegmentMap()
        {
            _fileIdToSegmentKeys.Clear();
            _fileIdToName.Clear();
            _puIdToFileId.Clear();
            _perFileMappingWorked = false;
            if (_activeDocument == null) return;

            var filesEnum = TryGetEnumerable(_activeDocument, "Files");
            if (filesEnum == null) return;

            // Step 1: scan each file's SDLXLIFF for GUIDs → puId→fileId map.
            // Build _fileIdToName + _fileIdToSegmentKeys (empty sets) at
            // the same time so the rest of the API has something to read
            // even if scanning fails for one file.
            var puIdToFileId = new Dictionary<string, string>(StringComparer.Ordinal);
            int totalGuids = 0;
            foreach (var f in filesEnum)
            {
                if (f == null) continue;
                var fileId = TryGetStringProp(f, "Id") ?? TryGetStringProp(f, "FileId") ?? "";
                if (string.IsNullOrEmpty(fileId)) continue;
                var name = TryGetStringProp(f, "Name") ?? "";
                _fileIdToName[fileId] = name;
                _fileIdToSegmentKeys[fileId] = new HashSet<string>(StringComparer.Ordinal);

                var local = TryGetStringProp(f, "LocalFilePath") ?? "";
                if (string.IsNullOrEmpty(local) || !System.IO.File.Exists(local)) continue;

                try
                {
                    var content = System.IO.File.ReadAllText(local);
                    foreach (System.Text.RegularExpressions.Match m in SdlxliffGuidRe.Matches(content))
                    {
                        var g = m.Value;
                        // First-wins. A GUID present in two files would be a
                        // Trados bug — they're globally unique paragraph-unit
                        // ids — but defend against it by not overwriting.
                        if (!puIdToFileId.ContainsKey(g))
                        {
                            puIdToFileId[g] = fileId;
                            totalGuids++;
                        }
                    }
                }
                catch { }
            }

            if (puIdToFileId.Count == 0) return;

            // Step 2: walk every segment pair, attribute via PU id lookup.
            int attributed = 0;
            try
            {
                foreach (var pair in _activeDocument.SegmentPairs)
                {
                    if (pair == null) continue;

                    object pu = null;
                    try { pu = _activeDocument.GetParentParagraphUnit(pair); }
                    catch { }
                    if (pu == null) continue;

                    var puId = TryGetParagraphUnitId(pu);
                    string segId = "";
                    try { segId = pair.Properties?.Id.Id ?? ""; } catch { }
                    if (string.IsNullOrEmpty(puId) || string.IsNullOrEmpty(segId)) continue;

                    string fid;
                    if (!puIdToFileId.TryGetValue(puId, out fid)) continue;

                    var key = puId + "/" + segId;
                    _fileIdToSegmentKeys[fid].Add(key);
                    _puIdToFileId[puId] = fid;
                    attributed++;
                }
            }
            catch { }

            _perFileMappingWorked = attributed > 0;
        }

        /// <summary>Regex matching the standard "8-4-4-4-12" GUID pattern.
        /// Compiled once. Used to extract paragraph-unit ids from on-disk
        /// SDLXLIFF files in <see cref="RefreshFileToSegmentMap"/>.</summary>
        private static readonly System.Text.RegularExpressions.Regex SdlxliffGuidRe =
            new System.Text.RegularExpressions.Regex(
                @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // PU id → file id cache. Many segment pairs share the same PU;
        // walking the context stack for each one would be wasteful. Built
        // lazily inside RefreshFileToSegmentMap, cleared on doc change.
        private readonly Dictionary<string, string> _puIdToFileId =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Per-file attribution candidate set. Built from a
        /// ProjectFile's Name / OriginalName / LocalFilePath (with and
        /// without the .sdlxliff suffix and basename variants). The
        /// matcher tries to find any of these strings inside a PU's
        /// context-stack strings.</summary>
        private sealed class FileMatchEntry
        {
            public string FileId;
            public string Name;
            public readonly HashSet<string> Candidates =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private static void AddCandidate(HashSet<string> set, string s)
        {
            if (!string.IsNullOrEmpty(s)) set.Add(s);
        }

        private static string StripSdlxliffExt(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (s.EndsWith(".sdlxliff", StringComparison.OrdinalIgnoreCase))
                return s.Substring(0, s.Length - 9);
            return s;
        }

        /// <summary>Walk a paragraph unit's context stack and try to match
        /// any context-string against any file's candidate set. Returns the
        /// matching file id, or null if no context contains an identifiable
        /// file reference. Pure reflection — no compile-time IContextInfo
        /// type reference (avoids Studio-version coupling).</summary>
        private static string MatchFileFromPuContexts(object pu, List<FileMatchEntry> files)
        {
            try
            {
                var props = pu.GetType().GetProperty("Properties")?.GetValue(pu, null);
                if (props == null) return null;
                var contexts = props.GetType().GetProperty("Contexts")?.GetValue(props, null);
                if (contexts == null) return null;

                // Prefer the IEnumerable<IContextInfo> nested "Contexts"
                // collection; fall back to the IContextProperties root
                // itself if it implements IEnumerable directly.
                System.Collections.IEnumerable list = null;
                try { list = contexts.GetType().GetProperty("Contexts")?.GetValue(contexts, null) as System.Collections.IEnumerable; } catch { }
                if (list == null) { try { list = contexts as System.Collections.IEnumerable; } catch { } }
                if (list == null) return null;

                foreach (var ctx in list)
                {
                    if (ctx == null) continue;
                    var ctxStrings = CollectContextStrings(ctx);
                    if (ctxStrings.Count == 0) continue;

                    foreach (var entry in files)
                    {
                        foreach (var fcand in entry.Candidates)
                        {
                            if (fcand.Length < 4) continue; // too generic
                            foreach (var cs in ctxStrings)
                            {
                                if (string.IsNullOrEmpty(cs)) continue;
                                if (cs.IndexOf(fcand, StringComparison.OrdinalIgnoreCase) >= 0)
                                    return entry.FileId;
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>Pluck every string that could plausibly identify the
        /// source file from a single IContextInfo: surface string
        /// properties + a small set of likely metadata keys (FilePath,
        /// OriginalFilePath, etc.).</summary>
        private static List<string> CollectContextStrings(object ctx)
        {
            var result = new List<string>(12);
            var type = ctx.GetType();
            foreach (var propName in new[] { "Description", "DisplayName", "Code", "DisplayCode", "ContextType" })
            {
                try
                {
                    var v = type.GetProperty(propName)?.GetValue(ctx, null) as string;
                    if (!string.IsNullOrEmpty(v)) result.Add(v);
                }
                catch { }
            }
            foreach (var key in new[]
            {
                "FilePath", "OriginalFilePath", "Path", "FileName",
                "OriginalName", "SourceFilePath", "Source", "File"
            })
            {
                try
                {
                    var v = TryGetContextMetaData(ctx, key);
                    if (!string.IsNullOrEmpty(v)) result.Add(v);
                }
                catch { }
            }
            return result;
        }

        private static string TryGetParagraphUnitId(object pu)
        {
            try
            {
                var propsProp = pu.GetType().GetProperty("Properties");
                if (propsProp == null) return "";
                var props = propsProp.GetValue(pu, null);
                if (props == null) return "";
                var puIdProp = props.GetType().GetProperty("ParagraphUnitId");
                if (puIdProp == null) return "";
                var puId = puIdProp.GetValue(props, null);
                if (puId == null) return "";
                var idProp = puId.GetType().GetProperty("Id");
                if (idProp == null) return "";
                return idProp.GetValue(puId, null) as string ?? "";
            }
            catch { return ""; }
        }

        private static System.Collections.IEnumerable TryGetEnumerable(object obj, string propName)
        {
            if (obj == null || string.IsNullOrEmpty(propName)) return null;
            try
            {
                var prop = obj.GetType().GetProperty(propName);
                if (prop == null) return null;
                return prop.GetValue(obj, null) as System.Collections.IEnumerable;
            }
            catch { return null; }
        }

        private static System.Collections.IEnumerable TryInvokeEnumerable(object obj, string methodName)
        {
            if (obj == null || string.IsNullOrEmpty(methodName)) return null;
            try
            {
                var method = obj.GetType().GetMethod(methodName, Type.EmptyTypes);
                if (method == null) return null;
                return method.Invoke(obj, null) as System.Collections.IEnumerable;
            }
            catch { return null; }
        }

        /// <summary>Get the file id a segment pair belongs to, using the
        /// precomputed map built by <see cref="RefreshFileToSegmentMap"/>.
        /// Returns empty string when the map is empty (SDK didn't expose
        /// ParagraphUnits) or the segment isn't found.</summary>
        private string GetFileIdForSegment(Sdl.FileTypeSupport.Framework.BilingualApi.ISegmentPair pair)
        {
            if (pair == null || _fileIdToSegmentKeys.Count == 0) return "";

            string puId = "", segId = "";
            try
            {
                var pu = _activeDocument?.GetParentParagraphUnit(pair);
                puId = pu?.Properties?.ParagraphUnitId.Id ?? "";
            }
            catch { }
            try { segId = pair.Properties?.Id.Id ?? ""; } catch { }
            if (string.IsNullOrEmpty(puId) || string.IsNullOrEmpty(segId)) return "";

            var key = puId + "/" + segId;
            foreach (var kv in _fileIdToSegmentKeys)
            {
                if (kv.Value.Contains(key)) return kv.Key;
            }
            return "";
        }

        /// <summary>Read a string-typed property by name via reflection
        /// from an arbitrary SDK object. Returns null if the property
        /// doesn't exist, isn't a string, or throws.</summary>
        private static string TryGetStringProp(object obj, string propName)
        {
            if (obj == null || string.IsNullOrEmpty(propName)) return null;
            try
            {
                var prop = obj.GetType().GetProperty(propName);
                if (prop == null) return null;
                var val = prop.GetValue(obj, null);
                return val?.ToString();
            }
            catch { return null; }
        }

        /// <summary>Push the active document's file list (plus the
        /// active-file id for the quick-select button) into the Import /
        /// Export tab. Single-file documents result in an empty/one-item
        /// list — the control hides the multi-file UI in that case.</summary>
        private void UpdateImportExportFileList()
        {
            SafeInvoke(() =>
            {
                var ctrl = _control?.Value?.ImportExportControl;
                if (ctrl == null) return;
                string activeFileId;
                var files = EnumerateActiveDocumentFiles(out activeFileId);
                ctrl.SetFileList(files, activeFileId);
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

            // Activate the Supervertaler Assistant panel so it is visible even
            // when auto-hidden, unpinned, or behind another dock tab. Matches
            // the SuperSearchAction pattern. SubmitMessage will then switch to
            // the Chat tab (index 0) inside the panel.
            try { instance.Activate(); }
            catch { /* Activate may not be available in all Trados versions */ }

            instance.SafeInvoke(() =>
            {
                _control.Value.SubmitMessage(expandedPrompt, displayPrompt, promptName);
            });
        }

        /// <summary>
        /// Activates the Supervertaler Assistant panel and switches to the
        /// SuperSearch tab. Used by <c>SuperSearchAction</c> (Alt+S) when
        /// SuperSearch is hosted as a tab rather than its own ViewPart. No-op
        /// if the tab isn't present (setting off, or unlicensed).
        /// </summary>
        public static void ActivateSuperSearchTab()
        {
            var instance = _currentInstance;
            if (instance == null) return;

            try { instance.Activate(); }
            catch { /* Activate may not be available in all Trados versions */ }

            instance.SafeInvoke(() => _control.Value.SwitchToSuperSearchTab());
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
        /// Legacy redirect – calls HandleTranslateActiveSegment (Ctrl+T pipeline).
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

                    // Run async – single segment, reuse TranslationPrompt + LlmClient
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

                                            // Reconstruction failed – strip placeholders, use plain text
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
            catch { /* ignore load failures – start with empty history */ }
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
        /// <summary>
        /// Collects the full document as bilingual (source, target) pairs for use as
        /// proofreading context. Unlike <see cref="CollectDocumentContext"/> which is
        /// source-only and used by Batch Translate / chat, this also includes the
        /// existing target so the proofreader can verify target-side consistency
        /// across the whole document – not just within the current 20-segment batch.
        /// </summary>
        private List<(string source, string target)> CollectBilingualDocumentContext()
        {
            var segments = new List<(string source, string target)>();

            if (_activeDocument == null)
                return segments;

            try
            {
                foreach (var pair in _activeDocument.SegmentPairs)
                {
                    var sourceText = pair.Source?.ToString() ?? "";
                    var targetText = pair.Target?.ToString() ?? "";
                    segments.Add((sourceText, targetText));
                }
            }
            catch (Exception)
            {
                // Document may not be accessible during transitions
            }

            return segments;
        }

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
                var project = _activeDocument?.Project as FileBasedProject;
                var name = project?.GetProjectInfo()?.Name;
                if (!string.IsNullOrEmpty(name))
                    return name;
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

            if (_supervertalerBridge != null)
            {
                try { _supervertalerBridge.Dispose(); } catch { /* never let bridge cleanup break Dispose */ }
                _supervertalerBridge = null;
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

        // ════════════════════════════════════════════════════════════════════
        // Import / Export tab (v4.20.7) — bilingual review file export &
        // round-trip re-import. The tab UI fires the four events handled
        // below; the heavy lifting lives in
        // <see cref="Core.Export.BilingualExporter"/> and
        // <see cref="Core.Export.BilingualImporter"/>.
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Walks the active Trados document, builds an <see cref="Core.Export.ExportSegment"/>
        /// list with source/target/status, picks a file path, and writes the
        /// bilingual file plus its sidecar manifest. Adds the result to the
        /// recent-exports list.
        /// </summary>
        private void OnBilingualExportRequested(object sender, ExportRequestedEventArgs e)
        {
            SafeInvoke(() =>
            {
                var ctrl = _control.Value.ImportExportControl;
                if (_activeDocument == null)
                {
                    ctrl.AppendLog("No document open.", true);
                    return;
                }

                // Multi-file: build the file-id filter from the UI
                // selection. Empty filter means "no UI restriction" —
                // either single-file (UI hidden) or user has the "All"
                // quick-select active. Null = include everything.
                //
                // When per-file attribution failed (SDK didn't expose
                // enough info to map segments to files), we silently
                // drop the filter and export everything from selected
                // files' segments… well, every segment, since we can't
                // tell them apart. Better than emitting an empty file.
                // The diagnostic is also dumped to the log so we can
                // refine the matcher if this path gets hit.
                var selectedIds = ctrl.GetSelectedFileIds();
                HashSet<string> filter;
                if (selectedIds.Count == 0)
                {
                    filter = null;
                }
                else if (!_perFileMappingWorked)
                {
                    filter = null;
                    ctrl.AppendLog("Note: this document didn't expose per-file segment attribution. " +
                                   "Exporting all segments in the active view (file selection is ignored).");
                    // One-shot: dump diagnostic to the in-tab log so the next
                    // round can refine the matcher. Doesn't block the export.
                    try { DumpMultiFileDiagnostic(ctrl); } catch { }
                }
                else
                {
                    filter = new HashSet<string>(selectedIds, StringComparer.Ordinal);
                }

                // v4.20.18: honour the "Include locked segments" checkbox.
                // Off → locked segments are skipped entirely. On → they're
                // included and visually flagged with 🔒 in the Status
                // column by the renderers.
                bool includeLocked = ctrl.IncludeLockedSegments;

                // v4.20.24: honour the confirmation-status checkboxes.
                // If the user has ticked specific statuses, the collector
                // filters segments accordingly. An empty / null set means
                // "no filter — include every status".
                var statusFilter = ctrl.GetSelectedStatuses();

                List<Core.Export.ExportSegment> segments;
                try
                {
                    segments = CollectBilingualExportSegments(filter, includeLocked, statusFilter);
                }
                catch (Exception ex)
                {
                    ctrl.AppendLog("Could not enumerate segments: " + ex.Message, true);
                    return;
                }

                if (segments.Count == 0)
                {
                    ctrl.AppendLog("No segments to export.", true);
                    return;
                }

                var opts = e.Options;
                var srcLang = GetDocumentSourceLanguage();
                var tgtLang = GetDocumentTargetLanguage();
                opts.SourceLanguageDisplay = !string.IsNullOrEmpty(srcLang)
                    ? Core.LanguageUtils.ShortenLanguageName(srcLang)
                    : "Source";
                opts.TargetLanguageDisplay = !string.IsNullOrEmpty(tgtLang)
                    ? Core.LanguageUtils.ShortenLanguageName(tgtLang)
                    : "Target";
                opts.ProjectName = SafeGetProjectName();
                opts.ToolVersion = SafeGetPluginVersion();

                // Group by source file so we can branch on output mode.
                var groups = new List<KeyValuePair<string, List<Core.Export.ExportSegment>>>();
                {
                    var byFile = new Dictionary<string, List<Core.Export.ExportSegment>>(StringComparer.Ordinal);
                    foreach (var seg in segments)
                    {
                        var key = string.IsNullOrEmpty(seg.SourceFileName)
                            ? SafeGetActiveFileName()
                            : seg.SourceFileName;
                        if (!byFile.TryGetValue(key, out var list))
                        {
                            list = new List<Core.Export.ExportSegment>();
                            byFile[key] = list;
                            groups.Add(new KeyValuePair<string, List<Core.Export.ExportSegment>>(key, list));
                        }
                        list.Add(seg);
                    }
                }

                bool multiFile = groups.Count > 1;
                bool separatePerFile = multiFile
                    && ctrl.SelectedOutputMode == MultiFileOutputMode.SeparatePerFile;

                if (separatePerFile)
                {
                    // ── Output mode: one bilingual DOCX per source file.
                    // Ask the user for a target FOLDER (not a file). We
                    // synthesise per-file file names from the project +
                    // source filename + layout.
                    string targetDir;
                    using (var dlg = new FolderBrowserDialog())
                    {
                        dlg.Description =
                            "Pick a folder. One bilingual " + opts.Format +
                            " will be created per source file inside it.";
                        if (dlg.ShowDialog(_control.Value) != DialogResult.OK) return;
                        targetDir = dlg.SelectedPath;
                    }

                    ctrl.SetBusy(true);
                    int filesWritten = 0;
                    try
                    {
                        var exporter = new Core.Export.BilingualExporter();
                        foreach (var grp in groups)
                        {
                            var perFileOpts = ClonePerFileOpts(opts, sourceFileName: grp.Key);
                            // Renumber segments 1..N within each file so
                            // the round-trip stays clean (manifest carries
                            // the puId/segId identity anyway).
                            int n = 1;
                            foreach (var s in grp.Value) s.Number = n++;
                            var fileName = Core.Export.BilingualExporter.DefaultFileName(perFileOpts);
                            var path = Path.Combine(targetDir, fileName);
                            var manifest = exporter.Export(grp.Value, perFileOpts, path);
                            ctrl.AddHistoryEntry(DateTime.Now, opts.Format.ToString(), path);
                            ctrl.AppendLog(
                                $"Exported {grp.Value.Count} segments from {grp.Key} → " +
                                Path.GetFileName(path));
                            filesWritten++;
                        }
                        ctrl.AppendLog(
                            $"Wrote {filesWritten} bilingual file(s) into {targetDir}.");
                    }
                    catch (Exception ex)
                    {
                        ctrl.AppendLog("Export failed: " + ex.Message, true);
                    }
                    finally
                    {
                        ctrl.SetBusy(false);
                    }
                    return;
                }

                // ── Output mode: one combined DOCX. Source file name on
                // the manifest reflects the active file; per-segment file
                // attribution lives on each ExportSegment.SourceFileName.
                opts.SourceFileName = multiFile
                    ? $"(multi-file: {groups.Count} files)"
                    : SafeGetActiveFileName();

                var defaultName = Core.Export.BilingualExporter.DefaultFileName(opts);
                string targetPath;
                using (var dlg = new SaveFileDialog())
                {
                    dlg.FileName = defaultName;
                    dlg.Title = multiFile
                        ? "Save combined bilingual review file (all selected files)"
                        : "Save bilingual review file";
                    switch (opts.Format)
                    {
                        case Core.Export.ExportFormat.Docx:
                            dlg.Filter = "Word document (*.docx)|*.docx";
                            dlg.DefaultExt = "docx";
                            break;
                        case Core.Export.ExportFormat.Markdown:
                            dlg.Filter = "Markdown (*.md)|*.md";
                            dlg.DefaultExt = "md";
                            break;
                        case Core.Export.ExportFormat.Html:
                            dlg.Filter = "HTML (*.html)|*.html";
                            dlg.DefaultExt = "html";
                            break;
                    }
                    if (dlg.ShowDialog(_control.Value) != DialogResult.OK) return;
                    targetPath = dlg.FileName;
                }

                ctrl.SetBusy(true);
                try
                {
                    var exporter = new Core.Export.BilingualExporter();
                    var manifest = exporter.Export(segments, opts, targetPath);
                    ctrl.AddHistoryEntry(DateTime.Now, opts.Format.ToString(), targetPath);
                    var fileCountSuffix = multiFile ? $" ({groups.Count} source files)" : "";
                    ctrl.AppendLog(
                        $"Exported {segments.Count} segments to {Path.GetFileName(targetPath)} " +
                        $"({opts.Format}, {opts.Layout}){fileCountSuffix}.");
                    ctrl.AppendLog("Sidecar manifest: " +
                        Path.GetFileName(Core.Export.ExportManifest.SidecarPathFor(targetPath)));
                }
                catch (Exception ex)
                {
                    ctrl.AppendLog("Export failed: " + ex.Message, true);
                }
                finally
                {
                    ctrl.SetBusy(false);
                }
            });
        }

        /// <summary>One-shot diagnostic: dump everything we can introspect
        /// about the active document's file structure to a TEMP FILE
        /// (and to the tab log). We use a temp file rather than just the
        /// log because the multi-file UI may have pushed the log area
        /// off-screen on some layouts. Returns the temp-file path so the
        /// caller can show it in a MessageBox.</summary>
        private string DumpMultiFileDiagnostic(Controls.ImportExportControl ctrl)
        {
            var sb = new System.Text.StringBuilder();
            void Log(string s) { sb.AppendLine(s); try { ctrl.AppendLog(s); } catch { } }

            try
            {
                Log("── DIAG: active document SDK shape ──");
                var doc = _activeDocument;
                if (doc == null) { Log("  _activeDocument is null"); }
                else
                {
                    Log($"  _activeDocument type: {doc.GetType().FullName}");

                    // 1) Walk doc properties returning IEnumerable
                    Log("  -- IEnumerable properties on _activeDocument --");
                    foreach (var prop in doc.GetType().GetProperties())
                    {
                        if (prop.GetIndexParameters().Length > 0) continue;
                        if (!typeof(System.Collections.IEnumerable).IsAssignableFrom(prop.PropertyType)) continue;
                        if (prop.PropertyType == typeof(string)) continue;
                        try
                        {
                            var val = prop.GetValue(doc, null) as System.Collections.IEnumerable;
                            int n = 0;
                            if (val != null) foreach (var _ in val) { n++; if (n > 100000) break; }
                            Log($"    {prop.Name}  ({prop.PropertyType.Name})  → {n} items");
                        }
                        catch (Exception ex)
                        {
                            Log($"    {prop.Name}  ({prop.PropertyType.Name})  → threw {ex.GetType().Name}");
                        }
                    }

                    // 2) For first file in doc.Files, dump every property + every no-arg method
                    var filesProp = doc.GetType().GetProperty("Files");
                    if (filesProp != null)
                    {
                        var filesEnum = filesProp.GetValue(doc, null) as System.Collections.IEnumerable;
                        if (filesEnum != null)
                        {
                            object firstFile = null;
                            int fileCount = 0;
                            foreach (var f in filesEnum) { if (firstFile == null) firstFile = f; fileCount++; }
                            Log($"  .Files contains {fileCount} item(s)");

                            if (firstFile != null)
                            {
                                Log($"  -- First file type: {firstFile.GetType().FullName} --");
                                Log("  -- Properties on first file --");
                                foreach (var prop in firstFile.GetType().GetProperties())
                                {
                                    if (prop.GetIndexParameters().Length > 0) continue;
                                    try
                                    {
                                        var val = prop.GetValue(firstFile, null);
                                        string valStr;
                                        if (val == null) valStr = "null";
                                        else if (val is System.Collections.IEnumerable enu && !(val is string))
                                        {
                                            int n = 0; foreach (var _ in enu) { n++; if (n > 100000) break; }
                                            valStr = $"<enumerable, {n} items, type={prop.PropertyType.Name}>";
                                        }
                                        else valStr = val.ToString();
                                        if (valStr != null && valStr.Length > 200) valStr = valStr.Substring(0, 200) + "…";
                                        Log($"    {prop.Name}  ({prop.PropertyType.Name}) = {valStr}");
                                    }
                                    catch (Exception ex)
                                    {
                                        Log($"    {prop.Name}  → threw {ex.GetType().Name}");
                                    }
                                }

                                Log("  -- No-arg methods on first file --");
                                foreach (var method in firstFile.GetType().GetMethods(
                                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
                                {
                                    if (method.GetParameters().Length != 0) continue;
                                    if (method.IsSpecialName) continue;
                                    if (method.DeclaringType == typeof(object)) continue;
                                    Log($"    {method.Name}() → {method.ReturnType.Name}");
                                }
                            }
                        }
                    }

                    // 3) Methods on _activeDocument (one-arg too) — we're
                    //    looking for something like GetFile(pair) or
                    //    GetActiveFile(pair) that maps a segment to its
                    //    parent file.
                    try
                    {
                        Log("  -- Methods on _activeDocument (no-arg + one-arg, non-property) --");
                        foreach (var method in doc.GetType().GetMethods(
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
                        {
                            if (method.IsSpecialName) continue;
                            if (method.DeclaringType == typeof(object)) continue;
                            var pars = method.GetParameters();
                            if (pars.Length > 1) continue;
                            string sig = string.Join(", ",
                                pars.Select(p => p.ParameterType.Name + " " + p.Name).ToArray());
                            Log($"    {method.Name}({sig}) → {method.ReturnType.Name}");
                        }
                    }
                    catch (Exception ex) { Log("  doc-methods dump threw: " + ex.Message); }

                    // 4) Walk the first 3 segment pairs, dump full property
                    //    chain so we can see where (if anywhere) FileId is
                    //    hiding. Wrap EVERY access in try/catch so a single
                    //    threw doesn't kill the whole section (which is
                    //    probably what happened last time).
                    try
                    {
                        Log("  -- First 3 segment pairs --");
                        int pi = 0;
                        foreach (var pair in doc.SegmentPairs)
                        {
                            if (pair == null) { pi++; continue; }
                            Log($"  PAIR[{pi}] type={SafeTypeName(pair)}");
                            DumpObjectPropsSafe(pair, "    pair.", Log);

                            try
                            {
                                if (pair.Properties != null)
                                {
                                    Log($"    pair.Properties type={SafeTypeName(pair.Properties)}");
                                    DumpObjectPropsSafe(pair.Properties, "      Properties.", Log);
                                    try
                                    {
                                        var sid = pair.Properties.Id;
                                        Log($"      Properties.Id type={SafeTypeName(sid)}");
                                        DumpObjectPropsSafe(sid, "        Id.", Log);
                                    }
                                    catch (Exception ex) { Log("      Properties.Id threw: " + ex.Message); }
                                }
                            }
                            catch (Exception ex) { Log("    pair.Properties access threw: " + ex.Message); }

                            try
                            {
                                var pu = doc.GetParentParagraphUnit(pair);
                                if (pu != null)
                                {
                                    Log($"    parentPU type={SafeTypeName(pu)}");
                                    DumpObjectPropsSafe(pu, "      pu.", Log);
                                    try
                                    {
                                        if (pu.Properties != null)
                                        {
                                            Log($"      pu.Properties type={SafeTypeName(pu.Properties)}");
                                            DumpObjectPropsSafe(pu.Properties, "        Properties.", Log);
                                            try
                                            {
                                                var puid = pu.Properties.ParagraphUnitId;
                                                Log($"        ParagraphUnitId type={SafeTypeName(puid)}");
                                                DumpObjectPropsSafe(puid, "          PUId.", Log);
                                            }
                                            catch (Exception ex) { Log("        ParagraphUnitId threw: " + ex.Message); }

                                            // Walk INTO the Contexts collection so we can see
                                            // whether file-identifying strings live there.
                                            try
                                            {
                                                var ctxProp = pu.Properties.GetType().GetProperty("Contexts");
                                                var ctxRoot = ctxProp?.GetValue(pu.Properties, null);
                                                if (ctxRoot != null)
                                                {
                                                    Log($"        Contexts root type={SafeTypeName(ctxRoot)}");
                                                    DumpObjectPropsSafe(ctxRoot, "          ctxRoot.", Log);
                                                    System.Collections.IEnumerable ctxList = null;
                                                    try { ctxList = ctxRoot.GetType().GetProperty("Contexts")?.GetValue(ctxRoot, null) as System.Collections.IEnumerable; } catch { }
                                                    if (ctxList == null) { try { ctxList = ctxRoot as System.Collections.IEnumerable; } catch { } }
                                                    if (ctxList != null)
                                                    {
                                                        int ci = 0;
                                                        foreach (var c in ctxList)
                                                        {
                                                            if (c == null) { ci++; continue; }
                                                            Log($"          CTX[{ci}] type={SafeTypeName(c)}");
                                                            DumpObjectPropsSafe(c, "            ", Log);
                                                            // Try the well-known metadata keys explicitly.
                                                            foreach (var key in new[]
                                                            {
                                                                "FilePath","OriginalFilePath","Path","FileName",
                                                                "OriginalName","SourceFilePath","Source","File",
                                                                "ParagraphFormatting"
                                                            })
                                                            {
                                                                try
                                                                {
                                                                    var v = TryGetContextMetaData(c, key);
                                                                    if (!string.IsNullOrEmpty(v))
                                                                    {
                                                                        var trunc = v.Length > 200 ? v.Substring(0, 200) + "…" : v;
                                                                        Log($"            metadata[{key}] = {trunc}");
                                                                    }
                                                                }
                                                                catch { }
                                                            }
                                                            ci++;
                                                            if (ci >= 8) break;
                                                        }
                                                    }
                                                }
                                            }
                                            catch (Exception ex) { Log("        Contexts dump threw: " + ex.Message); }
                                        }
                                    }
                                    catch (Exception ex) { Log("      pu.Properties access threw: " + ex.Message); }
                                }
                            }
                            catch (Exception ex) { Log("    GetParentParagraphUnit threw: " + ex.Message); }

                            pi++;
                            if (pi >= 3) break;
                        }
                    }
                    catch (Exception ex) { Log("  pair walk threw: " + ex.Message); }
                }

                Log("── /DIAG ──");
            }
            catch (Exception ex)
            {
                Log("DIAG outer fail: " + ex.Message);
            }

            // Write to a temp file. Stable name so user can locate it.
            try
            {
                var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "supervertaler-trados-diag.txt");
                System.IO.File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(false));
                ctrl.AppendLog("Diagnostic written to: " + path);
                return path;
            }
            catch (Exception ex)
            {
                ctrl.AppendLog("Could not write diagnostic file: " + ex.Message, true);
                return null;
            }
        }

        private static string SafeTypeName(object obj)
        {
            try { return obj?.GetType().FullName ?? "null"; } catch { return "<threw>"; }
        }

        /// <summary>Like <see cref="DumpObjectProps"/> but wrapping every
        /// access in its own try/catch so a single threw doesn't kill
        /// the whole enumeration. Used by the v4.20.8 multi-file
        /// diagnostic.</summary>
        private static void DumpObjectPropsSafe(object obj, string indent, Action<string> log)
        {
            if (obj == null) { log(indent + "(null)"); return; }
            System.Reflection.PropertyInfo[] props;
            try { props = obj.GetType().GetProperties(); }
            catch (Exception ex) { log(indent + "GetProperties threw: " + ex.Message); return; }
            foreach (var prop in props)
            {
                if (prop.GetIndexParameters().Length > 0) continue;
                string valStr;
                try
                {
                    var val = prop.GetValue(obj, null);
                    if (val == null) valStr = "null";
                    else if (val is System.Collections.IEnumerable enu && !(val is string))
                    {
                        int n = 0;
                        try { foreach (var _ in enu) { n++; if (n > 100000) break; } }
                        catch { valStr = "<enumerable, iteration threw>"; goto write; }
                        valStr = $"<enumerable, {n} items, type={prop.PropertyType.Name}>";
                    }
                    else valStr = val.ToString();
                    if (valStr != null && valStr.Length > 200) valStr = valStr.Substring(0, 200) + "…";
                }
                catch (Exception ex)
                {
                    valStr = "<threw: " + ex.GetType().Name + ": " + ex.Message + ">";
                }
                write:
                log($"{indent}{prop.Name}  ({prop.PropertyType.Name}) = {valStr}");
            }
        }

        private static void DumpObjectProps(object obj, string indent, Action<string> log)
        {
            if (obj == null) return;
            foreach (var prop in obj.GetType().GetProperties())
            {
                if (prop.GetIndexParameters().Length > 0) continue;
                try
                {
                    var val = prop.GetValue(obj, null);
                    string valStr;
                    if (val == null) valStr = "null";
                    else if (val is System.Collections.IEnumerable enu && !(val is string))
                    {
                        int n = 0; foreach (var _ in enu) { n++; if (n > 100000) break; }
                        valStr = $"<enumerable, {n} items, type={prop.PropertyType.Name}>";
                    }
                    else valStr = val.ToString();
                    if (valStr != null && valStr.Length > 200) valStr = valStr.Substring(0, 200) + "…";
                    log($"{indent}{prop.Name}  ({prop.PropertyType.Name}) = {valStr}");
                }
                catch (Exception ex)
                {
                    log($"{indent}{prop.Name}  → threw {ex.GetType().Name}");
                }
            }
        }

        /// <summary>Clone an <see cref="Core.Export.ExportOptions"/> with a
        /// different per-file SourceFileName. Used in the SeparatePerFile
        /// output mode so each emitted file's manifest records the right
        /// source filename and the default file name generator picks up
        /// the per-file stem.</summary>
        private static Core.Export.ExportOptions ClonePerFileOpts(
            Core.Export.ExportOptions src, string sourceFileName)
        {
            return new Core.Export.ExportOptions
            {
                Format = src.Format,
                Layout = src.Layout,
                SourceLanguageDisplay = src.SourceLanguageDisplay,
                TargetLanguageDisplay = src.TargetLanguageDisplay,
                ProjectName = (src.ProjectName ?? "") +
                              (string.IsNullOrEmpty(sourceFileName) ? "" : " — " + Path.GetFileNameWithoutExtension(sourceFileName)),
                SourceFileName = sourceFileName ?? "",
                ToolVersion = src.ToolVersion,
                IncludeLocked = src.IncludeLocked,
                IncludedStatuses = src.IncludedStatuses
            };
        }

        /// <summary>
        /// Reads a round-tripped DOCX or Markdown file, loads its sidecar
        /// manifest if present, computes the diff against the current Trados
        /// document state, confirms with the user, and applies accepted
        /// changes via <c>ProcessSegmentPair</c> (same writeback path the
        /// batch AI translator uses).
        /// </summary>
        private void OnBilingualImportRequested(object sender, ImportRequestedEventArgs e)
        {
            SafeInvoke(() =>
            {
                var ctrl = _control.Value.ImportExportControl;
                if (_activeDocument == null)
                {
                    ctrl.AppendLog("No document open.", true);
                    return;
                }

                if (!File.Exists(e.FilePath))
                {
                    ctrl.AppendLog("File does not exist: " + e.FilePath, true);
                    return;
                }

                var sidecarPath = Core.Export.ExportManifest.SidecarPathFor(e.FilePath);
                Core.Export.ExportManifest manifest;
                try
                {
                    manifest = File.Exists(sidecarPath)
                        ? Core.Export.ExportManifest.Load(sidecarPath)
                        : null;
                }
                catch (Exception ex)
                {
                    ctrl.AppendLog("Could not read sidecar manifest: " + ex.Message, true);
                    manifest = null;
                }

                if (manifest == null)
                {
                    // Build a fallback "manifest" purely from current document
                    // state. This loses source-tamper protection but lets the
                    // user re-import files that were generated before
                    // manifests existed or whose sidecars got deleted.
                    ctrl.AppendLog(
                        "No sidecar manifest found — falling back to current-document mapping. " +
                        "Source-tamper detection will be disabled for this import.", true);
                    manifest = BuildManifestFromCurrentDocument();
                }

                // Lookups for the importer to query current state.
                var currentTargetMap = SnapshotCurrentTargets();
                var lockedMap = SnapshotLockedSegments();
                var sourceTagCountMap = SnapshotSourceTagCounts();

                var importer = new Core.Export.BilingualImporter();
                var result = importer.Build(
                    e.FilePath, manifest,
                    currentTargetLookup: (pu, sg) =>
                    {
                        string val;
                        return currentTargetMap.TryGetValue(KeyOf(pu, sg), out val) ? val : null;
                    },
                    isWriteable: (pu, sg) => !lockedMap.Contains(KeyOf(pu, sg)),
                    currentSourceLookup: null,
                    currentSourceTagCountLookup: (pu, sg) =>
                    {
                        int n;
                        return sourceTagCountMap.TryGetValue(KeyOf(pu, sg), out n) ? n : 0;
                    });

                if (result.TotalImported == 0)
                {
                    ctrl.AppendLog("No segments parsed from the file. " +
                        "Check that it's a Supervertaler-exported DOCX or Markdown.", true);
                    return;
                }

                // Strict-mode flag from the UI checkbox. When OFF, the
                // writeback loop treats TagMismatch like Changed so the
                // edit is applied verbatim (with a per-segment warning).
                bool strict = ctrl.StrictTagIntegrityCheck;

                // Confirmation prompt. Surface the tag-mismatch count
                // separately so the user can see at a glance whether any
                // segments would be skipped for safety.
                var tagMismatch = result.TagMismatchCount;
                var nonTagIssues = result.IssueCount - tagMismatch;
                var tagLine = tagMismatch > 0
                    ? (strict
                        ? $"  {tagMismatch} tag-mismatch (will be SKIPPED — would break Trados QA)\n"
                        : $"  {tagMismatch} tag-mismatch (will be applied — strict check is OFF)\n")
                    : "";
                var msg = $"Read {result.TotalImported} segments from the file.\n\n" +
                          $"  {result.ChangedCount} change(s) to apply\n" +
                          $"  {result.UnchangedCount} unchanged\n" +
                          tagLine +
                          $"  {nonTagIssues} other issue(s) (missing, locked, source-mismatched)\n\n" +
                          "Apply the changes to the active Trados document?";
                var dr = MessageBox.Show(_control.Value, msg, "Re-import bilingual file",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (dr != DialogResult.OK) return;

                int applied = 0, failed = 0, skippedTagMismatch = 0;
                ctrl.SetBusy(true);
                try
                {
                    foreach (var d in result.Diffs)
                    {
                        // Strict mode: skip TagMismatch entirely.
                        // Permissive mode: treat TagMismatch as Changed.
                        bool isWriteableNow =
                            d.Kind == Core.Export.ImportChangeKind.Changed
                            || (d.Kind == Core.Export.ImportChangeKind.TagMismatch && !strict);

                        if (!isWriteableNow)
                        {
                            if (d.Kind == Core.Export.ImportChangeKind.TagMismatch && strict)
                            {
                                ctrl.AppendLog(
                                    $"Segment {d.Number}: skipped — {d.Detail}. " +
                                    "Restore the tag in the bilingual file, edit the segment " +
                                    "directly in Trados, or turn off strict tag-integrity check.",
                                    true);
                                skippedTagMismatch++;
                            }
                            continue;
                        }

                        if (d.Kind == Core.Export.ImportChangeKind.TagMismatch && !strict)
                        {
                            ctrl.AppendLog(
                                $"Segment {d.Number}: applying despite tag mismatch — {d.Detail}. " +
                                "Strict tag-integrity check is OFF; verify Trados QA after import.",
                                true);
                        }
                        var pair = FindSegmentPair(d.ParagraphUnitId, d.SegmentId);
                        if (pair == null)
                        {
                            ctrl.AppendLog($"Segment {d.Number}: not found in document, skipped.", true);
                            failed++;
                            continue;
                        }
                        try
                        {
                            _activeDocument.ProcessSegmentPair(pair, "Supervertaler",
                                (sp, cancel) =>
                                {
                                    // v4.20.7-tag: try the tag-aware reconstruction
                                    // path first. We re-serialize the live source to get
                                    // a fresh TagMap with the same numbering the
                                    // proofreader saw at export time (deterministic given
                                    // the source). SegmentTagHandler.ReconstructTarget
                                    // then parses the proofreader's <tN>...</tN> markers
                                    // and rebuilds the target with the correct cloned
                                    // tags wrapped around the translated text.
                                    //
                                    // Fall back to plain-text writeback when:
                                    //   - the source has no tags (nothing to reconstruct)
                                    //   - ReconstructTarget returns false (proofreader
                                    //     broke the tag structure — mismatched <tN>,
                                    //     unknown tag number, etc.)
                                    bool reconstructed = false;
                                    if (d.NewTarget != null)
                                    {
                                        // Serialise BOTH source and target. Source tag
                                        // references stay valid throughout reconstruction
                                        // (source isn't modified). Target tag references
                                        // need pre-cloning because ReconstructTarget calls
                                        // sp.Target.Clear() internally — without cloning,
                                        // those references could be invalidated before
                                        // the parsed tags get used. Combining both maps
                                        // lets a proofreader's edit reference tags that
                                        // originated in either the source OR the target
                                        // cell (the common case for the user's reported
                                        // "moved bold to a different word" scenario:
                                        // the bold only exists in target, source TagMap
                                        // is empty, so without including target tags
                                        // ReconstructTarget can't find the <tN> entry
                                        // and falls back to plain text — stripping all
                                        // formatting).
                                        var sourceSer = Core.SegmentTagHandler.Serialize(sp.Source);
                                        var targetSer = Core.SegmentTagHandler.Serialize(sp.Target);
                                        var combinedMap = BuildCombinedTagMap(sourceSer.TagMap, targetSer.TagMap);

                                        // Resolve any semantic-name markers (<b>, <i>, …)
                                        // that BilingualTagNamer.ApplySemanticNames wrote on
                                        // export back into the matching <tN>…</tN> form
                                        // SegmentTagHandler.ReconstructTarget understands.
                                        // Positional matching against the combined TagMap.
                                        var resolved = Core.Export.BilingualTagNamer.ResolveSemanticNames(
                                            d.NewTarget, combinedMap);

                                        bool hasAnyMarker = combinedMap.Count > 0
                                            || resolved.IndexOf("<t", StringComparison.Ordinal) >= 0;
                                        if (hasAnyMarker)
                                        {
                                            reconstructed = Core.SegmentTagHandler.ReconstructTarget(
                                                sp.Target, sp.Source, resolved, combinedMap);
                                        }
                                    }

                                    if (!reconstructed)
                                    {
                                        // Plain-text fallback: strip any stray <tN> markers
                                        // (and any leftover semantic markers) and write a
                                        // single IText cloned from source.
                                        var plain = Core.SegmentTagHandler.StripTagPlaceholders(d.NewTarget ?? "");
                                        // Also strip residual <b>/<i>/<u>/<bi> markers that
                                        // didn't resolve to a TagMap entry.
                                        plain = System.Text.RegularExpressions.Regex.Replace(
                                            plain, @"</?(?:bi|b|i|u)>", "",
                                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                                        var textTpl = Core.SegmentTagHandler.FindFirstText(sp.Source);
                                        if (textTpl != null)
                                        {
                                            sp.Target.Clear();
                                            var clone = (IText)textTpl.Clone();
                                            clone.Properties.Text = plain;
                                            sp.Target.Add(clone);
                                        }
                                    }
                                });
                            applied++;
                        }
                        catch (Exception ex)
                        {
                            ctrl.AppendLog($"Segment {d.Number}: write failed — {ex.Message}", true);
                            failed++;
                        }
                    }
                }
                finally
                {
                    ctrl.SetBusy(false);
                }

                var otherIssues = result.IssueCount - skippedTagMismatch;
                var summary = $"Re-import complete: {applied} applied, {failed} failed";
                if (skippedTagMismatch > 0)
                    summary += $", {skippedTagMismatch} skipped (tag mismatch)";
                if (otherIssues > 0)
                    summary += $", {otherIssues} other issue(s) skipped";
                summary += ".";
                ctrl.AppendLog(summary);
            });
        }

        private void OnImportExportOpenFile(object sender, string filePath)
        {
            try { System.Diagnostics.Process.Start(filePath); }
            catch (Exception ex)
            {
                _control.Value.ImportExportControl.AppendLog(
                    "Could not open file: " + ex.Message, true);
            }
        }

        private void OnImportExportOpenFolder(object sender, string filePath)
        {
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir))
                    System.Diagnostics.Process.Start("explorer.exe", dir);
            }
            catch (Exception ex)
            {
                _control.Value.ImportExportControl.AppendLog(
                    "Could not open folder: " + ex.Message, true);
            }
        }

        // ─── Trados SDK helpers for the Import / Export tab ─────────────

        /// <summary>Walks the active document and returns one
        /// <see cref="Core.Export.ExportSegment"/> per non-empty source
        /// segment, with stable Trados (paragraph-unit-id, segment-id) keys
        /// in the manifest.</summary>
        private List<Core.Export.ExportSegment> CollectBilingualExportSegments(
            HashSet<string> fileIdFilter = null,
            bool includeLocked = true,
            HashSet<string> statusFilter = null)
        {
            var result = new List<Core.Export.ExportSegment>();
            if (_activeDocument == null) return result;

            // Build a (fileId → fileName) lookup once so we can attribute
            // each segment to a human-readable file name. Single-file
            // documents end up with one entry; multi-file documents with
            // one per merged file.
            var fileIdToName = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                string _af;
                foreach (var f in EnumerateActiveDocumentFiles(out _af))
                    if (!string.IsNullOrEmpty(f.FileId))
                        fileIdToName[f.FileId] = f.FileName ?? "";
            }
            catch { }

            // Defensive: if attribution didn't work, the filter would
            // drop every segment (because every fid would be empty).
            // Treat that the same as "no filter" so we at least export
            // something. The caller already shows a log warning.
            var effectiveFilter = (fileIdFilter != null && _perFileMappingWorked)
                ? fileIdFilter : null;

            int number = 1;
            foreach (var pair in _activeDocument.SegmentPairs)
            {
                // Multi-file mode: skip segments outside the selected
                // files. Null filter = include everything (single-file
                // mode and "All files checked" mode).
                if (effectiveFilter != null)
                {
                    var fid = GetFileIdForSegment(pair);
                    if (string.IsNullOrEmpty(fid) || !effectiveFilter.Contains(fid)) continue;
                }
                if (pair?.Source == null) continue;

                // v4.20.18: read the segment's locked flag once. Used both
                // to honour the includeLocked filter (skip when off) and
                // to set ExportSegment.IsLocked so the renderers can
                // visually mark the row.
                bool isLocked = false;
                try { isLocked = pair.Properties?.IsLocked ?? false; }
                catch { }
                if (isLocked && !includeLocked) continue;

                // v4.20.7-tag: serialize source + target through SegmentTagHandler
                // so inline tags (cf bold/italic, field codes, page numbers, etc.)
                // come out as numbered <tN>...</tN> / <tN/> placeholders in the
                // bilingual file. This is the same serialization the batch AI
                // translator uses; importantly the numbering is deterministic
                // given the source segment, so re-import can regenerate the
                // matching TagMap and call SegmentTagHandler.ReconstructTarget
                // to put the tags back where the proofreader moved them to.
                //
                // After serialising we run the result through
                // BilingualTagNamer.ApplySemanticNames so recognised cf
                // pairs (bold/italic/underline) become <b>/<i>/<u>/<bi> —
                // matching the Workbench's "With Tags" Bilingual Table
                // export style. Unrecognised tags keep their numbered
                // <tN> form.
                var sourceSer = Core.SegmentTagHandler.Serialize(pair.Source);
                var sourceText = Core.Export.BilingualTagNamer.ApplySemanticNames(
                    sourceSer.SerializedText ?? "", sourceSer.TagMap);
                if (string.IsNullOrWhiteSpace(Core.SegmentTagHandler.StripTagPlaceholders(sourceText))) continue;

                string targetText = "";
                if (pair.Target != null)
                {
                    var targetSer = Core.SegmentTagHandler.Serialize(pair.Target);
                    targetText = Core.Export.BilingualTagNamer.ApplySemanticNames(
                        targetSer.SerializedText ?? "", targetSer.TagMap);
                }

                IParagraphUnit parentParagraphUnit = null;
                string puId = "", segId = "";
                try
                {
                    parentParagraphUnit = _activeDocument.GetParentParagraphUnit(pair);
                    puId = parentParagraphUnit?.Properties?.ParagraphUnitId.Id ?? "";
                }
                catch { }
                try { segId = pair.Properties?.Id.Id ?? ""; } catch { }

                var status = "";
                try
                {
                    status = pair.Properties?.ConfirmationLevel.ToString() ?? "";
                }
                catch { }

                // v4.20.24: confirmation-status filter. When the user has
                // ticked specific statuses in the UI, skip any segment
                // whose status isn't in the chosen set. Empty / null
                // filter = include everything (matches pre-v4.20.24
                // behaviour). Comparison is case-insensitive on the
                // enum's ToString() form.
                if (statusFilter != null && statusFilter.Count > 0
                    && !statusFilter.Contains(status ?? ""))
                {
                    continue;
                }

                // Detect paragraph-level formatting (Heading 1 bold, whole-
                // paragraph italic, etc.) so the bilingual file can render
                // segments with the same visual styling Trados shows in its
                // editor. Only meaningful when the segment has no inline
                // tags — inline cf-bold/italic is already serialised as
                // <b>/<i> markers and applying paragraph-level bold ON TOP
                // would over-style mixed-formatting segments. The detector
                // is best-effort: it reads IText run formatting + parent
                // paragraph context, with both probes wrapped in try/catch
                // so an SDK quirk on any one probe doesn't lose the whole
                // segment.
                bool pBold = false, pItalic = false, pUnderline = false;
                if (!sourceSer.HasTags)
                {
                    DetectParagraphLevelFormatting(pair.Source, parentParagraphUnit,
                        out pBold, out pItalic, out pUnderline);
                }

                // Tag the segment with its source-file identity. Single-
                // file documents end up with the only file's id+name on
                // every row; multi-file documents have the correct per-
                // segment attribution.
                var segFileId = GetFileIdForSegment(pair);
                string segFileName = null;
                if (!string.IsNullOrEmpty(segFileId))
                    fileIdToName.TryGetValue(segFileId, out segFileName);
                if (string.IsNullOrEmpty(segFileName))
                    segFileName = SafeGetActiveFileName();

                result.Add(new Core.Export.ExportSegment
                {
                    Number = number++,
                    ParagraphUnitId = puId,
                    SegmentId = segId,
                    SourceText = sourceText,
                    TargetText = targetText,
                    Status = status,
                    SourceHash = Core.Export.BilingualExporter.HashPrefix(sourceText),
                    IsBold = pBold,
                    IsItalic = pItalic,
                    IsUnderline = pUnderline,
                    SourceFileId = segFileId ?? "",
                    SourceFileName = segFileName ?? "",
                    IsLocked = isLocked
                });
            }
            return result;
        }

        /// <summary>Build a synthetic manifest from the current document — used
        /// when the user picks a file without a sidecar JSON. Loses tamper
        /// detection but lets the round-trip still work in best-effort mode.</summary>
        private Core.Export.ExportManifest BuildManifestFromCurrentDocument()
        {
            var m = new Core.Export.ExportManifest
            {
                ProjectName = SafeGetProjectName(),
                SourceFileName = SafeGetActiveFileName(),
                SourceLanguage = GetDocumentSourceLanguage() ?? "",
                TargetLanguage = GetDocumentTargetLanguage() ?? "",
                ExportTimestampUtc = DateTime.UtcNow,
                Format = "",
                Layout = "",
                ToolVersion = SafeGetPluginVersion()
            };

            int n = 1;
            foreach (var pair in _activeDocument.SegmentPairs)
            {
                if (pair?.Source == null) continue;
                var src = pair.Source.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(src)) continue;

                string puId = "", segId = "";
                try { puId = _activeDocument.GetParentParagraphUnit(pair)?.Properties?.ParagraphUnitId.Id ?? ""; } catch { }
                try { segId = pair.Properties?.Id.Id ?? ""; } catch { }

                m.Segments.Add(new Core.Export.ExportManifestSegment
                {
                    Number = n++,
                    ParagraphUnitId = puId,
                    SegmentId = segId,
                    SourceHash = Core.Export.BilingualExporter.HashPrefix(src),
                    Status = ""
                });
            }
            return m;
        }

        /// <summary>Snapshot the current target text for every segment, keyed
        /// by <c>"{puId}/{segId}"</c>. Used by the importer's diff pass.</summary>
        private Dictionary<string, string> SnapshotCurrentTargets()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (_activeDocument == null) return map;
            foreach (var pair in _activeDocument.SegmentPairs)
            {
                if (pair?.Source == null) continue;
                string puId = "", segId = "";
                try { puId = _activeDocument.GetParentParagraphUnit(pair)?.Properties?.ParagraphUnitId.Id ?? ""; } catch { }
                try { segId = pair.Properties?.Id.Id ?? ""; } catch { }
                if (string.IsNullOrEmpty(puId) || string.IsNullOrEmpty(segId)) continue;

                // CRITICAL: serialise the target the SAME WAY the export
                // path does — Serialize() into numbered <tN>/</tN> markers
                // and then ApplySemanticNames() to convert recognised
                // cf-bold / cf-italic / cf-underline pairs into the friendly
                // <b>/<i>/<u> form. Without this, every segment whose
                // current target contains any inline formatting registers
                // as "changed" on re-import even when the proofreader
                // touched nothing, because the live ToString() value
                // would be plain text while the DOCX cell contains the
                // semantic markers.
                var targetText = "";
                if (pair.Target != null)
                {
                    try
                    {
                        var targetSer = Core.SegmentTagHandler.Serialize(pair.Target);
                        targetText = Core.Export.BilingualTagNamer.ApplySemanticNames(
                            targetSer.SerializedText ?? "", targetSer.TagMap);
                    }
                    catch
                    {
                        // Fall back to plain text on any serialisation
                        // hiccup — better an over-reporting diff than
                        // losing the segment from the lookup entirely.
                        targetText = pair.Target.ToString() ?? "";
                    }
                }
                map[KeyOf(puId, segId)] = targetText ?? "";
            }
            return map;
        }

        /// <summary>Detect paragraph-level bold / italic / underline styling
        /// for a segment. Reads the parent
        /// <c>IParagraphUnit.Properties.Contexts</c> list and inspects each
        /// context via two complementary probes:
        ///
        /// 1. <c>IContextInfo.DisplayStyle</c> — a
        ///    <see cref="System.Drawing.FontStyle"/>? that Trados Studio
        ///    uses to render context-styled text in its editor (Heading 1
        ///    bold, Title italic, etc.). The string form of FontStyle is
        ///    e.g. "Bold", "Bold, Italic" — we match against those names.
        ///    This is the path that catches DOCX heading paragraphs.
        ///
        /// 2. <c>IContextInfo.Formatting</c> — a formatting-group
        ///    collection that some file types populate with explicit
        ///    bold/italic/underline entries. Walked via reflection by
        ///    <see cref="ExtractBoldItalicUnderline"/>.
        ///
        /// Inline cf bold/italic tags around part of a segment are
        /// handled separately by SegmentTagHandler; the caller skips this
        /// probe for tag-bearing segments to avoid double-applying
        /// styling. All access is through reflection on the context's
        /// runtime type — no strongly-typed reference to SDK formatting
        /// interfaces is made anywhere in the method signatures, so the
        /// class loads cleanly even if any of those types ship in
        /// different assemblies across SDK versions.</summary>
        private static void DetectParagraphLevelFormatting(
            ISegment sourceSegment,
            IParagraphUnit parentParagraphUnit,
            out bool isBold,
            out bool isItalic,
            out bool isUnderline)
        {
            isBold = false; isItalic = false; isUnderline = false;

            try
            {
                var contexts = parentParagraphUnit?.Properties?.Contexts;
                if (contexts == null) return;
                System.Collections.IEnumerable list = null;
                try { list = contexts.Contexts as System.Collections.IEnumerable; } catch { }
                if (list == null)
                {
                    try { list = contexts as System.Collections.IEnumerable; } catch { }
                }
                if (list == null) return;

                foreach (var ctx in list)
                {
                    if (ctx == null) continue;

                    // Probe 1: DisplayStyle. Universal across DOCX / PPTX /
                    // Excel etc. — this is the field that drives Trados'
                    // own editor rendering of context-styled text.
                    try
                    {
                        var dsProp = ctx.GetType().GetProperty("DisplayStyle");
                        if (dsProp != null)
                        {
                            var ds = dsProp.GetValue(ctx, null);
                            if (ds != null)
                            {
                                var dsStr = ds.ToString() ?? "";
                                if (dsStr.IndexOf("Bold", StringComparison.OrdinalIgnoreCase) >= 0)
                                    isBold = true;
                                if (dsStr.IndexOf("Italic", StringComparison.OrdinalIgnoreCase) >= 0)
                                    isItalic = true;
                                if (dsStr.IndexOf("Underline", StringComparison.OrdinalIgnoreCase) >= 0)
                                    isUnderline = true;
                            }
                        }
                    }
                    catch { }

                    // Probe 2: Formatting collection. File-type-dependent
                    // fallback for SDLXLIFFs that publish their paragraph
                    // styling via an explicit IFormattingGroup rather than
                    // via DisplayStyle.
                    try
                    {
                        var fmtProp = ctx.GetType().GetProperty("Formatting");
                        if (fmtProp != null)
                        {
                            var fmt = fmtProp.GetValue(ctx, null);
                            if (fmt != null)
                            {
                                ExtractBoldItalicUnderline(fmt, ref isBold, ref isItalic, ref isUnderline);
                            }
                        }
                    }
                    catch { }

                    // Probe 3: ParagraphFormatting metadata. THE one for
                    // DOCX. Trados encodes the paragraph's Word run-
                    // property block as a metadata string under the key
                    // "ParagraphFormatting" — looks like
                    //   <w:pPr><w:rPr><w:b/><w:bCs/></w:rPr></w:pPr>
                    // for a paragraph-wide bold paragraph (Heading 1
                    // included). We don't need to interpret the style
                    // name itself — we just look for the Word formatting
                    // markers w:b (bold), w:i (italic), w:u (underline)
                    // directly. Catches every DOCX case I've seen,
                    // regardless of style-name conventions.
                    try
                    {
                        var paraFmt = TryGetContextMetaData(ctx, "ParagraphFormatting");
                        if (!string.IsNullOrEmpty(paraFmt))
                        {
                            // <w:b/> = bold on, <w:b w:val="false"/> = bold off.
                            // We only flip the flag ON for explicit-true markers;
                            // explicit-false stays unset.
                            if (HasWordPropertyOn(paraFmt, "b"))      isBold = true;
                            if (HasWordPropertyOn(paraFmt, "i"))      isItalic = true;
                            if (HasWordPropertyOn(paraFmt, "u"))      isUnderline = true;
                        }
                    }
                    catch { }

                    // Probe 4: style-name heuristic. Fallback for files
                    // where ParagraphFormatting isn't populated but the
                    // style name is recognisable as a heading.
                    try
                    {
                        if (!isBold && ContextLooksLikeHeading(ctx))
                            isBold = true;
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>Read a single string-valued entry from a Trados
        /// IContextInfo's metadata bag. Trados encodes per-context
        /// key/value pairs (e.g. "ParagraphFormatting", "StartsAt") as
        /// metadata; the SDK exposes them via different access patterns
        /// across SDK versions. We try each known pattern in sequence
        /// and return the first non-null/non-empty match. All access
        /// goes through reflection so no compile-time SDK type is
        /// referenced.</summary>
        private static string TryGetContextMetaData(object ctx, string key)
        {
            if (ctx == null || string.IsNullOrEmpty(key)) return null;
            var type = ctx.GetType();

            // Pattern A: GetMetaData(string) instance method.
            try
            {
                var method = type.GetMethod("GetMetaData", new[] { typeof(string) });
                if (method != null)
                {
                    var result = method.Invoke(ctx, new object[] { key });
                    var s = result?.ToString();
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            catch { }

            // Pattern B: MetaData property → dictionary-like → indexer.
            try
            {
                var prop = type.GetProperty("MetaData");
                if (prop != null)
                {
                    var dict = prop.GetValue(ctx, null);
                    if (dict != null)
                    {
                        var indexer = dict.GetType().GetMethod("get_Item", new[] { typeof(string) });
                        if (indexer != null)
                        {
                            var result = indexer.Invoke(dict, new object[] { key });
                            var s = result?.ToString();
                            if (!string.IsNullOrEmpty(s)) return s;
                        }
                    }
                }
            }
            catch { }

            // Pattern C: MetaDataCount + GetMetaDataItem(index) enumeration.
            // The interface returns IMetaDataItem with Key/Value string
            // properties. Walk it linearly until we find the key.
            try
            {
                var countProp = type.GetProperty("MetaDataCount");
                var getItem = type.GetMethod("GetMetaDataItem", new[] { typeof(int) });
                if (countProp != null && getItem != null)
                {
                    var count = (int)(countProp.GetValue(ctx, null) ?? 0);
                    for (int i = 0; i < count; i++)
                    {
                        var item = getItem.Invoke(ctx, new object[] { i });
                        if (item == null) continue;
                        var itemType = item.GetType();
                        var itemKey = itemType.GetProperty("Key")?.GetValue(item, null) as string;
                        if (!string.Equals(itemKey, key, StringComparison.Ordinal)) continue;
                        var itemVal = itemType.GetProperty("Value")?.GetValue(item, null) as string;
                        if (!string.IsNullOrEmpty(itemVal)) return itemVal;
                    }
                }
            }
            catch { }

            return null;
        }

        /// <summary>Detect whether a Word run-property fragment carries an
        /// "on" marker for the given short element name (e.g. "b" for
        /// bold, "i" for italic, "u" for underline). Word represents
        /// these as:
        ///   <c>&lt;w:b/&gt;</c>                — element-only, defaults to on
        ///   <c>&lt;w:b w:val="true"/&gt;</c>   — explicit on
        ///   <c>&lt;w:b w:val="false"/&gt;</c>  — explicit off (only relevant
        ///                                       when an inherited style was
        ///                                       on; we treat it as off here)
        ///   <c>&lt;w:u w:val="single"/&gt;</c> — underline style (treated as on
        ///                                       for any non-"none" value)
        /// Returns true for the on cases, false otherwise. Conservative
        /// — when in doubt, returns false.</summary>
        private static bool HasWordPropertyOn(string paraFormatting, string elementShortName)
        {
            if (string.IsNullOrEmpty(paraFormatting)) return false;
            // Look for <w:b ... /> or <w:b/> patterns. The fragment is
            // raw XML text (possibly with mixed casing); use ordinal
            // case-insensitive matching.
            var openMarker = "<w:" + elementShortName;
            int idx = 0;
            while ((idx = paraFormatting.IndexOf(openMarker, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int end = idx + openMarker.Length;
                if (end >= paraFormatting.Length) break;
                char next = paraFormatting[end];
                // Need the element name to end here — not be the start of
                // a longer name like "bCs" or "bdr".
                if (next == '/' || next == ' ' || next == '>' || next == '\t' || next == '\n')
                {
                    // Find the end of this tag.
                    int tagEnd = paraFormatting.IndexOf('>', end);
                    if (tagEnd < 0) return false;
                    var tag = paraFormatting.Substring(idx, tagEnd - idx + 1);
                    // Self-closing or no w:val? Treat as on.
                    int valIdx = tag.IndexOf("w:val=", StringComparison.OrdinalIgnoreCase);
                    if (valIdx < 0) return true;
                    // Extract the value inside the quotes.
                    int q1 = tag.IndexOf('"', valIdx);
                    int q2 = q1 >= 0 ? tag.IndexOf('"', q1 + 1) : -1;
                    if (q1 < 0 || q2 < 0) return true; // can't parse; assume on
                    var val = tag.Substring(q1 + 1, q2 - q1 - 1).Trim().ToLowerInvariant();
                    if (val == "false" || val == "0" || val == "off" || val == "none") return false;
                    return true;
                }
                idx = end;
            }
            return false;
        }

        // Regex matching DOCX heading-style context names. Catches
        // "Heading 1" / "heading2" / "h1" / "h-1" / "Title" / "Subtitle".
        // Anchored loosely (look-around for word boundaries) to avoid
        // matching unrelated words containing "title" or "heading" as
        // substrings of longer style names.
        private static readonly System.Text.RegularExpressions.Regex HeadingStyleRe =
            new System.Text.RegularExpressions.Regex(
                @"(?<![a-z])(?:heading\s*-?\s*[1-9]?|h-?[1-6]|title|subtitle)(?![a-z])",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Probe a context's string-typed name fields for an
        /// indication that the parent paragraph is a heading-style
        /// paragraph (Heading 1-6, Title, Subtitle, etc.). Reads
        /// Description / DisplayName / Code / DisplayCode via reflection,
        /// so it stays decoupled from the specific IContextInfo SDK
        /// type's shape.</summary>
        private static bool ContextLooksLikeHeading(object ctx)
        {
            if (ctx == null) return false;
            var type = ctx.GetType();
            foreach (var propName in new[] { "Description", "DisplayName", "Code", "DisplayCode" })
            {
                try
                {
                    var prop = type.GetProperty(propName);
                    if (prop == null) continue;
                    var val = prop.GetValue(ctx, null) as string;
                    if (string.IsNullOrEmpty(val)) continue;
                    if (HeadingStyleRe.IsMatch(val)) return true;
                }
                catch { }
            }
            return false;
        }

        /// <summary>Extract bold/italic/underline flags from any
        /// formatting-collection-like object using pure reflection. Accepts
        /// <c>object</c> instead of a typed <c>IFormattingGroup</c>
        /// parameter on purpose — referencing
        /// <c>Sdl.FileTypeSupport.Framework.Formatting.IFormattingGroup</c>
        /// in a method signature forces the CLR to resolve the type at
        /// class-load time, and that interface isn't shipped in Studio 18's
        /// runtime assemblies. A typed reference here makes the entire
        /// AiAssistantViewPart class fail to load with a silent
        /// TypeLoadException — the ViewPart disappears from the Trados UI
        /// with no visible error. Pure reflection sidesteps that.</summary>
        private static void ExtractBoldItalicUnderline(
            object fmt,
            ref bool isBold, ref bool isItalic, ref bool isUnderline)
        {
            if (fmt == null) return;
            try
            {
                var type = fmt.GetType();
                var keysProp = type.GetProperty("Keys");
                if (keysProp == null) return;
                var keys = keysProp.GetValue(fmt, null) as System.Collections.IEnumerable;
                if (keys == null) return;

                // Indexer: find the get_Item(string) method.
                var indexer = type.GetMethod("get_Item", new[] { typeof(string) });

                foreach (var keyObj in keys)
                {
                    var lc = (keyObj?.ToString() ?? "").ToLowerInvariant();
                    if (string.IsNullOrEmpty(lc)) continue;

                    object valObj = null;
                    if (indexer != null)
                    {
                        try { valObj = indexer.Invoke(fmt, new object[] { keyObj?.ToString() }); }
                        catch { continue; }
                    }
                    var val = (valObj?.ToString() ?? "").ToLowerInvariant();
                    bool isOn = val.IndexOf("true", StringComparison.Ordinal) >= 0
                             || val.IndexOf("single", StringComparison.Ordinal) >= 0;
                    if (!isOn) continue;
                    if (lc.Contains("bold")) isBold = true;
                    else if (lc.Contains("italic")) isItalic = true;
                    else if (lc.Contains("underline")) isUnderline = true;
                }
            }
            catch { }
        }

        /// <summary>Snapshot of (puId/segId) keys that should not be silently
        /// overwritten on re-import. Currently empty in the MVP — Trados'
        /// own segment-protection will reject writes to locked segments
        /// inside <c>ProcessSegmentPair</c>, so the worst case is a per-
        /// segment "write failed" log line rather than data corruption.
        /// The per-confirmation-level conflict policy is a Phase-3 follow-up
        /// once the right enum values for "locked / rejected" are confirmed
        /// against the live SDK enum (`Sdl.Core.Globalization.ConfirmationLevel`
        /// has different value names across Studio SDK versions).</summary>
        /// <summary>v4.20.18: actually reads pair.Properties.IsLocked
        /// from every segment in the active document. Was previously a
        /// stub returning an empty set — meaning re-import would happily
        /// overwrite locked segments. Now the BilingualImporter's
        /// isWriteable predicate sees the real picture and refuses to
        /// write back to locked segments (they show up as the "locked"
        /// counter in the re-import dialog's "other issues" line).</summary>
        private HashSet<string> SnapshotLockedSegments()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (_activeDocument == null) return set;
            try
            {
                foreach (var pair in _activeDocument.SegmentPairs)
                {
                    if (pair == null) continue;
                    bool locked = false;
                    try { locked = pair.Properties?.IsLocked ?? false; }
                    catch { }
                    if (!locked) continue;
                    string puId = "", segId = "";
                    try { puId = _activeDocument.GetParentParagraphUnit(pair)?.Properties?.ParagraphUnitId.Id ?? ""; }
                    catch { }
                    try { segId = pair.Properties?.Id.Id ?? ""; }
                    catch { }
                    if (string.IsNullOrEmpty(puId) || string.IsNullOrEmpty(segId)) continue;
                    set.Add(KeyOf(puId, segId));
                }
            }
            catch { }
            return set;
        }

        /// <summary>Snapshot of (puId/segId) → live source's STRUCTURAL
        /// tag count. Used by the importer's tag-integrity check.
        /// "Structural" = tags that DON'T map to a semantic name via
        /// BilingualTagNamer.DetectSemantic; i.e. field codes, page
        /// numbers, custom format pairs, line breaks — anything that
        /// stays as <tN> in the exported bilingual file rather than
        /// becoming <b>/<i>/<u>/<bi>. These are the tags whose count
        /// must round-trip exactly because Trados file structure depends
        /// on them. Semantic formatting tags (bold/italic/underline) are
        /// intentionally excluded — the proofreader can add or remove
        /// them at will without breaking Trados QA.</summary>
        private Dictionary<string, int> SnapshotSourceTagCounts()
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            if (_activeDocument == null) return map;
            foreach (var pair in _activeDocument.SegmentPairs)
            {
                if (pair?.Source == null) continue;
                string puId = "", segId = "";
                try { puId = _activeDocument.GetParentParagraphUnit(pair)?.Properties?.ParagraphUnitId.Id ?? ""; } catch { }
                try { segId = pair.Properties?.Id.Id ?? ""; } catch { }
                if (string.IsNullOrEmpty(puId) || string.IsNullOrEmpty(segId)) continue;

                int structuralCount = 0;
                try
                {
                    var ser = Core.SegmentTagHandler.Serialize(pair.Source);
                    if (ser?.TagMap != null)
                    {
                        foreach (var kv in ser.TagMap)
                        {
                            // A tag is "structural" if BilingualTagNamer
                            // can't assign it a semantic short-name —
                            // i.e. it's NOT a cf bold / italic / underline
                            // / bi pair. Standalone tags (line breaks,
                            // page-number placeholders, etc.) are always
                            // structural since DetectSemantic only
                            // recognises ITagPair entries.
                            if (Core.Export.BilingualTagNamer.DetectSemantic(kv.Value) == null)
                                structuralCount++;
                        }
                    }
                }
                catch { structuralCount = 0; }

                map[KeyOf(puId, segId)] = structuralCount;
            }
            return map;
        }

        /// <summary>Find the live <c>ISegmentPair</c> for a given
        /// (paragraph-unit, segment) id pair. Returns <c>null</c> if not
        /// found.</summary>
        private ISegmentPair FindSegmentPair(string paragraphUnitId, string segmentId)
        {
            if (_activeDocument == null) return null;
            if (string.IsNullOrEmpty(paragraphUnitId) || string.IsNullOrEmpty(segmentId)) return null;
            foreach (var pair in _activeDocument.SegmentPairs)
            {
                string puId = "", segId = "";
                try { puId = _activeDocument.GetParentParagraphUnit(pair)?.Properties?.ParagraphUnitId.Id ?? ""; } catch { }
                try { segId = pair.Properties?.Id.Id ?? ""; } catch { }
                if (string.Equals(puId, paragraphUnitId, StringComparison.Ordinal) &&
                    string.Equals(segId, segmentId, StringComparison.Ordinal))
                {
                    return pair;
                }
            }
            return null;
        }

        private static string KeyOf(string puId, string segId) =>
            (puId ?? "") + "/" + (segId ?? "");

        /// <summary>Build a unified <see cref="Core.TagInfo"/> dictionary
        /// that combines source-side and target-side tag references. Source
        /// tags pass through unchanged; target tags are pre-cloned via
        /// <see cref="IAbstractMarkupData.Clone"/> so they survive the
        /// <c>sp.Target.Clear()</c> that ReconstructTarget runs internally.
        /// On numbering collisions (source and target both have <c>&lt;tN&gt;</c>
        /// for the same N), target wins because the proofreader's edits
        /// live in the target cell.</summary>
        private static Dictionary<int, Core.TagInfo> BuildCombinedTagMap(
            Dictionary<int, Core.TagInfo> sourceTagMap,
            Dictionary<int, Core.TagInfo> targetTagMap)
        {
            var combined = new Dictionary<int, Core.TagInfo>();

            if (sourceTagMap != null)
            {
                foreach (var kv in sourceTagMap)
                    combined[kv.Key] = kv.Value;
            }

            if (targetTagMap != null)
            {
                foreach (var kv in targetTagMap)
                {
                    var clone = CloneTagInfo(kv.Value);
                    if (clone != null)
                        combined[kv.Key] = clone;
                }
            }

            return combined;
        }

        /// <summary>Deep-clone a <see cref="Core.TagInfo"/> so its
        /// <c>OriginalMarkup</c> reference can be used after the original
        /// segment is cleared. Returns null if cloning fails (rare —
        /// IAbstractMarkupData.Clone is generally well-behaved).</summary>
        private static Core.TagInfo CloneTagInfo(Core.TagInfo info)
        {
            if (info?.OriginalMarkup == null) return null;
            try
            {
                var clonedMarkup = info.OriginalMarkup.Clone() as IAbstractMarkupData;
                if (clonedMarkup == null) return null;
                return new Core.TagInfo
                {
                    TagType = info.TagType,
                    OriginalMarkup = clonedMarkup,
                    IsLineBreak = info.IsLineBreak
                };
            }
            catch
            {
                return null;
            }
        }

        private string SafeGetProjectName()
        {
            try
            {
                var pc = SdlTradosStudio.Application.GetController<ProjectsController>();
                return pc?.CurrentProject?.GetProjectInfo()?.Name ?? "Trados project";
            }
            catch { return "Trados project"; }
        }

        private string SafeGetActiveFileName()
        {
            try
            {
                var name = _activeDocument?.ActiveFile?.Name;
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch { }
            return "";
        }

        private static string SafeGetPluginVersion()
        {
            try
            {
                return typeof(AiAssistantViewPart).Assembly.GetName().Version?.ToString() ?? "";
            }
            catch { return ""; }
        }
    }
}
